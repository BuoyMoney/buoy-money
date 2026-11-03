using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuoyAI.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using Xunit;
using Xunit.Abstractions;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// LIVE probe (#740 caching): confirms explicit Gemini context caching works via the Mscc SDK and
/// measures the real cached-token discount when a stable prefix (system instruction + conversation
/// history) is cached and only the new turn is sent fresh. Skipped unless GEMINI_LIVE_KEY is set.
/// </summary>
public class GeminiCacheProbeTests
{
    private readonly ITestOutputHelper _out;
    public GeminiCacheProbeTests(ITestOutputHelper output) => _out = output;

    private const string ModelId = "gemini-2.5-flash";

    [Fact]
    public async Task LiveCacheProbe()
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_LIVE_KEY");
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("GEMINI_LIVE_KEY not set — skipping."); return; }

        var googleAI = new GoogleAI(key);

        // A big-ish stable prefix: system instruction + several prior turns (simulate a long chat).
        // Must exceed the model's cache minimum (gemini-2.5-flash: 1024 tokens). Real BuoyAI clears
        // this easily (system prompt alone ~2-3K); the probe just needs enough bulk to create a cache.
        var system = new Content(string.Join(" ", Enumerable.Repeat(
            "You are BuoyAI, a concise personal-finance assistant that answers strictly with the user's own budgeting, spending, savings, and subscription data, and never gives investment advice.", 30)));
        var history = new List<Content>();
        for (int i = 0; i < 20; i++)
        {
            history.Add(new Content($"Turn {i}: an earlier detailed question about my budgets, spending categories, rollovers, savings goals, and upcoming subscriptions for the month.", Role.User));
            history.Add(new Content($"Turn {i}: a detailed earlier answer covering category allocations, rollover balances, projected savings contributions, and renewal dates.", Role.Model));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# LIVE explicit-cache probe (#740)");
        sb.AppendLine($"Model: `{ModelId}`\n");

        string? cacheName = null;
        try
        {
            // 1) Baseline: send system + history + new message, no cache.
            var model = googleAI.GenerativeModel(ModelId);
            var freshReq = new GenerateContentRequest
            {
                SystemInstruction = system,
                Contents = new List<Content>(history) { new("What's my biggest spending category?", Role.User) },
                GenerationConfig = new GenerationConfig { MaxOutputTokens = 1, ThinkingConfig = GeminiThinking.Low(ModelId) },
            };
            var fresh = await model.GenerateContent(freshReq);
            var freshPrompt = fresh?.UsageMetadata?.PromptTokenCount ?? 0;

            // 2) Cache the stable prefix (system + history), then send only the new message.
            var cache = await googleAI.CachedContent().Create(new CachedContent
            {
                Model = $"models/{ModelId}",
                SystemInstruction = system,
                Contents = history,
                Ttl = TimeSpan.FromMinutes(5),
                DisplayName = "buoyai-cache-probe",
            });
            cacheName = cache?.Name;

            var cachedReq = new GenerateContentRequest
            {
                Contents = new List<Content> { new("What's my biggest spending category?", Role.User) },
                CachedContent = cacheName,
                GenerationConfig = new GenerationConfig { MaxOutputTokens = 1, ThinkingConfig = GeminiThinking.Low(ModelId) },
            };
            var cached = await model.GenerateContent(cachedReq);
            var u = cached?.UsageMetadata;

            sb.AppendLine($"- No cache: prompt tokens = **{freshPrompt}**");
            sb.AppendLine($"- With cache: prompt={u?.PromptTokenCount ?? 0}, **cached={u?.CachedContentTokenCount ?? 0}**, total={u?.TotalTokenCount ?? 0}");
            var cachedShare = (u?.PromptTokenCount ?? 0) > 0 ? 100.0 * (u?.CachedContentTokenCount ?? 0) / (u!.PromptTokenCount!.Value) : 0;
            sb.AppendLine($"- **{cachedShare:0}% of the with-cache prompt served from cache** (billed at the discounted cached rate).");
            sb.AppendLine("\nInterpretation: cached>0 confirms explicit caching works via the SDK; that share is what a long-conversation prefix would move to the cheaper rate every turn.");

            // Design probe: cache HISTORY ONLY (no system), then send a FRESH systemInstruction +
            // new message referencing the cache. This is the shape BuoyAI needs — the system prompt
            // carries per-turn financial context, so only the append-only history is cacheable cross-turn.
            var histCache = await googleAI.CachedContent().Create(new CachedContent
            {
                Model = $"models/{ModelId}",
                Contents = history,
                Ttl = TimeSpan.FromMinutes(5),
                DisplayName = "buoyai-cache-probe-histonly",
            });
            try
            {
                var mixedReq = new GenerateContentRequest
                {
                    SystemInstruction = system, // fresh each turn (has financial context)
                    Contents = new List<Content> { new("What changed since last month?", Role.User) },
                    CachedContent = histCache?.Name,
                    GenerationConfig = new GenerationConfig { MaxOutputTokens = 1, ThinkingConfig = GeminiThinking.Low(ModelId) },
                };
                var mixed = await model.GenerateContent(mixedReq);
                var mu = mixed?.UsageMetadata;
                sb.AppendLine($"\nHistory-only cache + fresh system: prompt={mu?.PromptTokenCount ?? 0}, **cached={mu?.CachedContentTokenCount ?? 0}** " +
                              "(cached>0 ⇒ we can cache append-only history and keep the per-turn system prompt fresh — the cross-turn design).");
            }
            finally
            {
                if (!string.IsNullOrEmpty(histCache?.Name))
                    try { await googleAI.CachedContent().Delete(histCache!.Name); } catch { }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\nProbe error: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(cacheName))
            {
                try { await googleAI.CachedContent().Delete(cacheName); } catch { /* TTL cleans up */ }
            }
        }

        var path = Path.Combine(AppContext.BaseDirectory, "gemini_cache_probe.md");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {path})");
    }

    [Fact]
    public async Task LiveProductionShapeProbe()
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_LIVE_KEY");
        if (string.IsNullOrWhiteSpace(key)) { _out.WriteLine("GEMINI_LIVE_KEY not set — skipping."); return; }

        // Exact shape our code produces: cache = scaffold + FULL tools + history; request = cache ref +
        // a fresh message, NO system/tools in the request.
        var tools = new BuoyAI.Infrastructure.MCP.McpToolExecutor(new NoopProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BuoyAI.Infrastructure.MCP.McpToolExecutor>.Instance);
        var fullTools = tools.GetGeminiFunctionDeclarations();

        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Gemini:ApiKey"] = key }).Build();
        var client = new GeminiCacheClient(config,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiCacheClient>.Instance);

        var scaffold = new Content(string.Join(" ", Enumerable.Repeat(
            "You are BuoyAI, a concise budgeting assistant scoped to the user's own finances.", 20)));
        var history = new List<Content>();
        for (var i = 0; i < 12; i++)
        {
            history.Add(new Content($"Turn {i}: earlier detailed question about budgets, spending, savings, subscriptions.", Role.User));
            history.Add(new Content($"Turn {i}: earlier detailed answer with categories, rollovers, and projections.", Role.Model));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# LIVE production-shape cache probe (#743)");
        string? name = null;
        try
        {
            name = await client.CreateAsync("gemini-2.5-flash", scaffold, history, fullTools, TimeSpan.FromMinutes(5));
            sb.AppendLine($"- Cache created (scaffold + {fullTools.Count} tools + {history.Count} history): {(name != null ? "OK " + name : "FAILED")}");

            var model = new GoogleAI(key).GenerativeModel("gemini-2.5-flash");
            var req = new GenerateContentRequest
            {
                Contents = new List<Content> { new("Current snapshot: ...\n\nWhat's my biggest category?", Role.User) },
                CachedContent = name,
                GenerationConfig = new GenerationConfig { MaxOutputTokens = 1, ThinkingConfig = GeminiThinking.Low(ModelId) },
            };
            var r = await model.GenerateContent(req);
            var u = r?.UsageMetadata;
            sb.AppendLine($"- Cached call: prompt={u?.PromptTokenCount ?? 0}, **cached={u?.CachedContentTokenCount ?? 0}**, total={u?.TotalTokenCount ?? 0}");
            sb.AppendLine("- tools-in-cache + bare request works => the production cached path is valid.");
        }
        catch (Exception ex) { sb.AppendLine($"- ERROR: {ex.Message}"); }
        finally { if (name != null) await client.DeleteAsync(name); }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "gemini_cache_prodshape.md"), sb.ToString());
        _out.WriteLine(sb.ToString());
    }

    private sealed class NoopProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
