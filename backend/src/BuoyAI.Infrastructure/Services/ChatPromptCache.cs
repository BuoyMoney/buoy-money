using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Thin wrapper over the Gemini explicit-caching endpoints (create/delete a CachedContent).
/// Isolated behind an interface so the cache-lifecycle logic is unit-testable without the SDK/network.
/// </summary>
public interface IGeminiCacheClient
{
    /// <summary>Create a cache holding system instruction + tools + history. Returns the cache name, or null on failure.</summary>
    Task<string?> CreateAsync(string model, Content systemInstruction, List<Content> history,
        List<FunctionDeclaration> tools, TimeSpan ttl, CancellationToken ct = default);
    Task DeleteAsync(string cacheName, CancellationToken ct = default);
}

public class GeminiCacheClient : IGeminiCacheClient
{
    private readonly string _apiKey;
    private readonly ILogger<GeminiCacheClient> _logger;

    public GeminiCacheClient(IConfiguration config, ILogger<GeminiCacheClient> logger)
    {
        // Deliberately the legacy order (see GeminiApiKey.ResolveConfigFirstNullCoalesced): hosted keeps the cache off.
        _apiKey = GeminiApiKey.ResolveConfigFirstNullCoalesced(config);
        _logger = logger;
    }

    public async Task<string?> CreateAsync(string model, Content systemInstruction, List<Content> history,
        List<FunctionDeclaration> tools, TimeSpan ttl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey)) return null;
        var googleAI = new GoogleAI(_apiKey);
        var cache = await googleAI.CachedContent().Create(new CachedContent
        {
            Model = model.StartsWith("models/") ? model : $"models/{model}",
            SystemInstruction = systemInstruction,
            Contents = history,
            Tools = tools is { Count: > 0 } ? new Tools { new Tool { FunctionDeclarations = tools } } : null,
            Ttl = ttl,
            DisplayName = "buoyai-chat",
        }, cancellationToken: ct);
        return cache?.Name;
    }

    public async Task DeleteAsync(string cacheName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(cacheName)) return;
        await new GoogleAI(_apiKey).CachedContent().Delete(cacheName, cancellationToken: ct);
    }
}

/// <summary>A live cache and how much of the conversation history it already covers.</summary>
public sealed record PromptCacheRef(string CacheName, int CoveredHistoryCount);

/// <summary>
/// Explicit prompt caching for long chat conversations (#743). Caches the stable prefix
/// (system scaffold + full tools + conversation history) so a long chat re-charges that prefix at
/// the ~¼ cached rate every turn instead of full price. Keyed by conversation, reused across turns,
/// rolled forward as history grows. Feature-flagged (AI:PromptCaching:Enabled, default OFF),
/// length-gated (only worth it above a token threshold), and FAIL-OPEN: any problem returns null so
/// the caller uses the normal inline path. Chosen per-turn as the alternative to tool filtering
/// (#740): Gemini forbids referencing a cache while also sending tools/system in the request, so a
/// turn is either filtered+uncached (short) or cached with the full tool set (long).
/// </summary>
public interface IChatPromptCache
{
    Task<PromptCacheRef?> GetOrCreateAsync(string conversationId, string model, Content systemScaffold,
        List<Content> history, List<FunctionDeclaration> tools, int estimatedPrefixTokens, CancellationToken ct = default);
}

public class ChatPromptCache : IChatPromptCache
{
    private readonly IGeminiCacheClient _client;
    private readonly IMemoryCache _memory;
    private readonly ILogger<ChatPromptCache> _logger;
    private readonly bool _enabled;
    private readonly int _minPrefixTokens;
    private readonly int _rollForwardMaxUncovered;
    private readonly TimeSpan _ttl;

    private sealed record Entry(string CacheName, int CoveredHistoryCount, string Model);

    public ChatPromptCache(IGeminiCacheClient client, IMemoryCache memory, IConfiguration config, ILogger<ChatPromptCache> logger)
    {
        _client = client;
        _memory = memory;
        _logger = logger;
        // Default OFF (#743): the explicit-cache mechanism is proven in isolation, but in-app
        // CachedContent creation returns null (unresolved SDK/model discrepancy), so the cached path
        // never engages on staging and must not activate unverified on prod. Tool filtering (the
        // primary token lever) stays on independently. Flip to true only after the in-app CreateAsync
        // returning null is diagnosed and the cached path is verified end-to-end.
        _enabled = config.GetValue("AI:PromptCaching:Enabled", false);
        // Default 2500: the stable prefix (scaffold + full tools) is ~3.1K tokens, so any turn that
        // carries conversation history clears this and caches that prefix at the ~1/4 rate. Higher
        // thresholds only cache marathon chats; 2.5K lets normal multi-turn conversations benefit.
        _minPrefixTokens = config.GetValue("AI:PromptCaching:MinPrefixTokens", 2500);
        _rollForwardMaxUncovered = config.GetValue("AI:PromptCaching:RollForwardMaxUncovered", 8);
        _ttl = TimeSpan.FromMinutes(config.GetValue("AI:PromptCaching:TtlMinutes", 15));
    }

    private static string Key(string conversationId, string model) => $"promptcache:{model}:{conversationId}";

    public async Task<PromptCacheRef?> GetOrCreateAsync(string conversationId, string model, Content systemScaffold,
        List<Content> history, List<FunctionDeclaration> tools, int estimatedPrefixTokens, CancellationToken ct = default)
    {
        // One-line visibility on every entry so a live "why isn't the cache firing?" check shows the
        // exact gate inputs instead of a silent no-op. (#743 diagnosability.)
        _logger.LogInformation("Prompt cache eval: conv={ConversationId} enabled={Enabled} historyMsgs={History} prefix~{Tokens} min={Min}",
            conversationId, _enabled, history.Count, estimatedPrefixTokens, _minPrefixTokens);

        if (!_enabled || string.IsNullOrEmpty(conversationId) || history.Count == 0)
        {
            _logger.LogInformation("Prompt cache SKIP for conversation {ConversationId}: gate (enabled={Enabled}, historyMsgs={History})",
                conversationId, _enabled, history.Count);
            return null;
        }
        if (estimatedPrefixTokens < _minPrefixTokens)
        {
            // Visibility: this is the common reason caching silently no-ops on short chats. Logged so a
            // live "why isn't the cache firing?" check shows the gate rather than looking like a failure.
            _logger.LogInformation("Prompt cache SKIP for conversation {ConversationId}: prefix ~{Tokens} tokens < min {Min} (conversation too short to cache)",
                conversationId, estimatedPrefixTokens, _minPrefixTokens);
            return null;
        }

        try
        {
            var key = Key(conversationId, model);
            if (_memory.TryGetValue<Entry>(key, out var existing) && existing is not null
                && existing.Model == model
                && existing.CoveredHistoryCount <= history.Count
                && history.Count - existing.CoveredHistoryCount <= _rollForwardMaxUncovered)
            {
                // Reuse: cache covers history[0..Covered]; caller sends history[Covered..] + the new turn fresh.
                _logger.LogInformation("Prompt cache HIT for conversation {ConversationId}: covers {Covered}/{Total} history msgs",
                    conversationId, existing.CoveredHistoryCount, history.Count);
                return new PromptCacheRef(existing.CacheName, existing.CoveredHistoryCount);
            }

            // Miss / stale / rolled too far: (re)create a cache covering the full current history.
            var name = await _client.CreateAsync(model, systemScaffold, history, tools, _ttl, ct);
            if (string.IsNullOrEmpty(name)) return null;

            var entry = new Entry(name, history.Count, model);
            _memory.Set(key, entry, _ttl);
            // Best-effort delete of the previous cache we replaced.
            if (existing is not null && !string.IsNullOrEmpty(existing.CacheName))
            {
                _ = _client.DeleteAsync(existing.CacheName, ct);
            }
            _logger.LogInformation("Prompt cache CREATE for conversation {ConversationId}: {Name} covering {Total} history msgs (~{Tokens} tokens)",
                conversationId, name, history.Count, estimatedPrefixTokens);
            return new PromptCacheRef(name, history.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt cache unavailable for conversation {ConversationId}; falling back to inline prompt", conversationId);
            return null; // fail-open
        }
    }
}
