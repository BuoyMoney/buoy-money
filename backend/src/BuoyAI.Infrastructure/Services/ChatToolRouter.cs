using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.MCP;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Intent-based tool filtering for the AI chat (#740). Picks the small subset of MCP tools
/// relevant to the user's message so the model isn't handed all ~38 tool schemas on every
/// turn/iteration (which grows context + cost O(n_tools x iterations)). Feature-flagged and
/// FAIL-OPEN: when disabled, the message is empty/ambiguous, or nothing matches, it returns
/// null so the caller uses the full toolset — filtering can never drop a tool the turn needs.
/// </summary>
public interface IChatToolRouter
{
    /// <summary>Tool names to expose this turn, or <c>null</c> to fall back to the full toolset.</summary>
    ISet<string>? SelectToolNames(string userMessage);
}

public class ChatToolRouter : IChatToolRouter
{
    private readonly IMcpToolExecutor _tools;
    private readonly ILogger<ChatToolRouter> _logger;
    private readonly bool _enabled;

    // Coarse keyword -> domain map. Deliberately simple (v1): a cheap, deterministic, zero-latency
    // classifier. An LLM classifier is a follow-up upgrade on #740; the wiring here is unchanged.
    private static readonly IReadOnlyDictionary<string, string[]> DomainKeywords =
        new Dictionary<string, string[]>
        {
            ["budget"] = new[] { "budget", "allocat", "categor", "overspend", "rollover", "envelope", "left to spend" },
            ["savings"] = new[] { "saving", "save ", "goal", "emergency fund", "set aside", "nest egg", "rainy day" },
            ["subscription"] = new[] { "subscription", "recurring", "renew", "free trial", "cancel ", "netflix", "spotify" },
            ["income"] = new[] { "income", "paycheck", "salary", "payday", "get paid", "earn" },
            ["transaction"] = new[] { "transaction", "spent", "spend", "purchase", "charge", "merchant", "refund", "categorize", "split", "earmark", "receipt" },
            ["financial"] = new[] { "account", "balance", "net worth", "overview", "how much", "cash" },
            ["user"] = new[] { "preference", "setting", "profile", "currency", "my name" },
            ["gamification"] = new[] { "badge", "achievement", "streak", "level", "points", "wrapped", "amnesia", "oops", "keep getting", "keep earning" },
        };

    // Always included when filtering is active, so identity/context + top-level financial asks
    // still resolve even on a domain-specific turn.
    public static readonly IReadOnlyList<string> AlwaysDomains = new[] { "user", "financial" };

    public ChatToolRouter(IMcpToolExecutor tools, IConfiguration config, ILogger<ChatToolRouter> logger)
    {
        _tools = tools;
        _logger = logger;
        _enabled = config.GetValue("AI:ToolFiltering:Enabled", true);
    }

    public ISet<string>? SelectToolNames(string userMessage)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(userMessage))
        {
            return null; // full toolset
        }

        var text = userMessage.ToLowerInvariant();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (domain, keywords) in DomainKeywords)
        {
            if (keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                matched.Add(domain);
            }
        }

        // Nothing recognizable -> fail open to the full toolset rather than risk dropping a needed tool.
        if (matched.Count == 0)
        {
            _logger.LogInformation("Tool router: no domain matched; using full toolset");
            return null;
        }

        foreach (var d in AlwaysDomains) matched.Add(d);

        var names = _tools.GetToolNamesForDomains(matched);
        if (names.Count == 0)
        {
            return null; // defensive: never hand the model an empty toolset
        }

        _logger.LogInformation("Tool router: domains [{Domains}] -> {Count} tools (of {Total})",
            string.Join(",", matched.OrderBy(d => d)), names.Count, _tools.GetAvailableTools().Count);
        return names;
    }
}
