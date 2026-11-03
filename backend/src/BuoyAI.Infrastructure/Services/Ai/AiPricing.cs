using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// The one place a Gemini call is priced. Before this there were three copies (the cost tracker, the chat service's
/// own rates, the spend-metric reporter's flat guess) and the cost tracker priced any model it did not know as
/// gemini-1.5-flash, so a local or third-party model was billed at a Gemini rate.
///
/// Rules: an exact model name wins; then the longest table name the model name starts with (so a dated preview such as
/// "gemini-2.5-flash-preview-09-2025" prices as "gemini-2.5-flash"); an unknown Gemini model is priced at the dearest
/// row of its family (pro, or the priciest flash) so spend is never under-counted; anything else is 0, because this
/// table only knows Gemini and a self-hosted model costs nothing per call. Both fallbacks log once per model name.
/// A provider that knows its own price (local models: 0, OpenRouter: its reported figure) passes it in and skips this table.
/// </summary>
public static class AiPricing
{
    /// <summary>Dollars per 1,000 tokens (input, output). Prices from https://ai.google.dev/gemini-api/docs/pricing.</summary>
    private static readonly Dictionary<string, (decimal Input, decimal Output)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gemini 3.x series
        ["gemini-3.8-flash"] = (0.00075m, 0.00375m),      // $0.75/$3.75 per 1M (intro thru 2026-12-31; then $1.50/$7.50)
        ["gemini-3.7-flash"] = (0.00075m, 0.00375m),
        ["gemini-3.6-flash"] = (0.00075m, 0.00375m),
        ["gemini-3.5-flash"] = (0.0015m, 0.009m),         // $1.50/$9 per 1M (no intro discount; priciest Flash)
        ["gemini-3.5-flash-lite"] = (0.0003m, 0.0025m),   // $0.30/$2.50 per 1M (Standard tier primary)
        ["gemini-3.1-flash-lite"] = (0.00025m, 0.0015m),  // $0.25/$1.50 per 1M
        ["gemini-3-flash-preview"] = (0.0005m, 0.003m),   // $0.50/$3 per 1M
        ["gemini-3-pro-preview"] = (0.002m, 0.012m),      // $2/$12 per 1M (<200k)

        // Gemini 2.5 series
        ["gemini-2.5-flash"] = (0.00015m, 0.0006m),
        ["gemini-2.5-pro"] = (0.00125m, 0.005m),

        // Gemini 2.0 series
        ["gemini-2.0-flash"] = (0.000075m, 0.0003m),
        ["gemini-2.0-flash-lite"] = (0.000075m, 0.0003m),
        ["gemini-2.0-flash-experimental"] = (0.000075m, 0.0003m),

        // Gemini 1.5 series (legacy)
        ["gemini-1.5-flash"] = (0.000075m, 0.0003m),
        ["gemini-1.5-pro"] = (0.00125m, 0.005m),
        ["gemini-1.0-pro"] = (0.0005m, 0.0015m),
    };

    private static readonly string[] OrderedNames = Table.Keys.OrderByDescending(k => k.Length).ToArray();
    private static readonly ConcurrentDictionary<string, bool> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the call cost in dollars. Never throws and never returns a negative.</summary>
    public static decimal Cost(string? model, int inputTokens, int outputTokens, ILogger? logger = null)
    {
        var (input, output) = Lookup(model, logger);
        return Math.Max(0, inputTokens) / 1000m * input + Math.Max(0, outputTokens) / 1000m * output;
    }

    /// <summary>True when the model has a row in the table (exactly, or as a dated variant of one).</summary>
    public static bool IsKnown(string? model) => Find(model) != null;

    private static (decimal Input, decimal Output) Lookup(string? model, ILogger? logger)
    {
        var known = Find(model);
        if (known != null) return known.Value;

        var name = (model ?? "").Trim();
        if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) name = name[7..];

        if (name.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            var family = name.Contains("pro", StringComparison.OrdinalIgnoreCase) ? "gemini-3-pro-preview" : "gemini-3.5-flash";
            WarnOnce(logger, name, "Gemini model '{Model}' has no price row; counting it at the {Family} rate so spend is not under-counted.", name, family);
            return Table[family];
        }

        WarnOnce(logger, name, "Model '{Model}' has no price row and is not a Gemini model; counting it as free. Set the provider's own prices if it costs money.", name);
        return (0m, 0m);
    }

    private static (decimal Input, decimal Output)? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var name = model.Trim();
        if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        if (Table.TryGetValue(name, out var exact)) return exact;
        foreach (var key in OrderedNames)
        {
            // Longest name first, and only a dated or preview suffix counts as the same model.
            if (name.StartsWith(key + "-", StringComparison.OrdinalIgnoreCase) && IsDateOrPreviewSuffix(name[(key.Length + 1)..]))
                return Table[key];
        }
        return null;
    }

    // A variant suffix is "preview...", "exp...", "latest", "001", or a date; anything else (such as "lite") is a different model.
    private static bool IsDateOrPreviewSuffix(string suffix) =>
        suffix.StartsWith("preview", StringComparison.OrdinalIgnoreCase)
        || suffix.StartsWith("exp", StringComparison.OrdinalIgnoreCase)
        || suffix.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || (suffix.Length > 0 && char.IsDigit(suffix[0]));

    private static void WarnOnce(ILogger? logger, string model, string message, params object[] args)
    {
        if (logger == null || !Warned.TryAdd(model, true)) return;
        logger.LogWarning(message, args);
    }
}
