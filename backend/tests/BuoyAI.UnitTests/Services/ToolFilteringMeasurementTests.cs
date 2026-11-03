using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// #740 measurement: computes the REAL per-turn tool-schema token delta from intent-based
/// filtering, using the reflection-discovered MCP tool declarations (no live Gemini/DB needed).
/// Writes a markdown table next to the test binary so the numbers can be captured.
/// </summary>
public class ToolFilteringMeasurementTests
{
    private readonly ITestOutputHelper _out;
    public ToolFilteringMeasurementTests(ITestOutputHelper output) => _out = output;

    // Mirror of AIService.EstimateDeclarationTokens (private) so the measurement matches the log.
    // Uses dynamic so the test project needn't reference the Gemini SDK assembly directly.
    private static int EstimateTokens(IEnumerable decls)
    {
        var chars = 0;
        foreach (dynamic d in decls)
        {
            string name = d.Name ?? "";
            string desc = d.Description ?? "";
            chars += name.Length + desc.Length + 20;
            if (d.Parameters?.Properties is IDictionary props) chars += props.Count * 50;
        }
        return chars / 4;
    }

    [Fact]
    public void Measure_SchemaTokenDelta()
    {
        var tools = new McpToolExecutor(new StubProvider(), NullLogger<McpToolExecutor>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ToolFiltering:Enabled"] = "true" })
            .Build();
        var router = new ChatToolRouter(tools, config, NullLogger<ChatToolRouter>.Instance);

        var full = tools.GetGeminiFunctionDeclarations();
        var fullTokens = EstimateTokens(full);

        var prompts = new[]
        {
            "am I over budget on groceries this month?",
            "add $100 to my emergency fund savings goal",
            "what subscriptions do I have and which can I cancel?",
            "how much did I spend at restaurants last week?",
            "what's my account balance and net worth?",
            "when do I get paid next?",
            "show my budget, savings goals and subscriptions",   // cross-domain
            "tell me a joke about penguins",                       // no match -> fail-open (full)
        };

        var sb = new StringBuilder();
        sb.AppendLine($"# Tool-filtering schema-token delta (#740)");
        sb.AppendLine();
        sb.AppendLine($"Full toolset: **{full.Count} tools ≈ {fullTokens} schema tokens/call** (re-sent every iteration, up to 10/turn).");
        sb.AppendLine();
        sb.AppendLine("| Prompt | Tools (filtered/full) | Schema tokens (filtered/full) | Reduction/call | ~Reduction/turn (×10) |");
        sb.AppendLine("|---|---|---|---|---|");

        double sumPct = 0; int measured = 0;
        foreach (var p in prompts)
        {
            var allowed = router.SelectToolNames(p);
            var decls = allowed == null ? full : tools.GetGeminiFunctionDeclarations(false, allowed);
            var tok = EstimateTokens(decls);
            var pct = fullTokens == 0 ? 0 : 100.0 * (fullTokens - tok) / fullTokens;
            if (allowed != null) { sumPct += pct; measured++; }
            var note = allowed == null ? " *(fail-open→full)*" : "";
            sb.AppendLine($"| {p}{note} | {decls.Count}/{full.Count} | {tok}/{fullTokens} | {pct:0}% | ~{(fullTokens - tok) * 10} tok |");
        }
        sb.AppendLine();
        if (measured > 0)
            sb.AppendLine($"**Mean reduction on the {measured} routed (single/cross-domain) prompts: {sumPct / measured:0}% of tool-schema tokens per call.**");

        var path = Path.Combine(AppContext.BaseDirectory, "tool_filtering_measure.md");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {path})");

        // Sanity: a single-domain budget prompt must materially reduce the toolset.
        var budgetAllowed = router.SelectToolNames(prompts[0]);
        Assert.NotNull(budgetAllowed);
        Assert.True(tools.GetGeminiFunctionDeclarations(false, budgetAllowed).Count < full.Count);
    }

    private sealed class StubProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null; // DiscoverTools is reflection-only
    }
}
