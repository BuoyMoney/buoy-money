using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;
using Xunit;
using Xunit.Abstractions;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// LIVE measurement (#740): calls the real Gemini API to get exact input-token counts for the
/// full vs intent-filtered tool set, and probes prompt caching. Skipped unless GEMINI_LIVE_KEY is
/// set, so it never runs in CI. Run:  GEMINI_LIVE_KEY=... dotnet test --filter LiveTokenDelta
/// </summary>
public class ToolFilteringLiveTokenTests
{
    private readonly ITestOutputHelper _out;
    public ToolFilteringLiveTokenTests(ITestOutputHelper output) => _out = output;

    private const string Model = "gemini-2.5-flash"; // current flash model (2.0-flash-lite is decommissioned)
    private const string SystemInstruction =
        "You are BuoyAI, a personal-finance assistant. Use the available tools to answer with the user's real data. Be concise.";

    [Fact]
    public async Task LiveTokenDelta()
    {
        var key = Environment.GetEnvironmentVariable("GEMINI_LIVE_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            _out.WriteLine("GEMINI_LIVE_KEY not set — skipping live measurement.");
            return;
        }

        var tools = new McpToolExecutor(new NoopProvider(), NullLogger<McpToolExecutor>.Instance);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI:ToolFiltering:Enabled"] = "true" })
            .Build();
        var router = new ChatToolRouter(tools, config, NullLogger<ChatToolRouter>.Instance);

        var model = new GoogleAI(key).GenerativeModel(Model);

        // Use generateContent (not countTokens — that endpoint rejects top-level tools/systemInstruction)
        // and read the REAL billed input tokens (PromptTokenCount), which includes the tool schemas.
        async Task<int> CountAsync(string userMsg, List<FunctionDeclaration> decls)
        {
            var req = new GenerateContentRequest
            {
                Contents = new List<Content> { new(userMsg) { Role = "user" } },
                Tools = new Tools { new Tool { FunctionDeclarations = decls } },
                SystemInstruction = new Content(SystemInstruction),
                GenerationConfig = new GenerationConfig
                {
                    MaxOutputTokens = 1, // we only need PromptTokenCount; keep generation trivial
                    ThinkingConfig = GeminiThinking.Low(Model),
                },
            };
            var resp = await model.GenerateContent(req);
            return resp?.UsageMetadata?.PromptTokenCount ?? 0;
        }

        var full = tools.GetGeminiFunctionDeclarations();
        var prompts = new[]
        {
            "what's my account balance and net worth?",
            "am I over budget on groceries this month?",
            "add $100 to my emergency fund savings goal",
            "what subscriptions do I have and which can I cancel?",
            "show my budget, savings goals and subscriptions",
        };

        var sb = new StringBuilder();
        sb.AppendLine("# LIVE Gemini input-token delta — tool filtering (#740)");
        sb.AppendLine($"Model: `{Model}`  (real countTokens)\n");
        sb.AppendLine("| Prompt | Tools (filt/full) | Input tokens (filt/full) | Reduction |");
        sb.AppendLine("|---|---|---|---|");

        var baselineFull = await CountAsync("baseline", full);
        double sumPct = 0; int n = 0;
        foreach (var p in prompts)
        {
            var allowed = router.SelectToolNames(p);
            var decls = allowed == null ? full : tools.GetGeminiFunctionDeclarations(false, allowed);
            var filt = await CountAsync(p, decls);
            var fullForP = await CountAsync(p, full);
            var pct = fullForP == 0 ? 0 : 100.0 * (fullForP - filt) / fullForP;
            sumPct += pct; n++;
            sb.AppendLine($"| {p} | {decls.Count}/{full.Count} | {filt}/{fullForP} | {pct:0}% |");
        }
        sb.AppendLine($"\n**Mean live input-token reduction: {(n > 0 ? sumPct / n : 0):0}%** (full-toolset baseline ≈ {baselineFull} input tokens).");

        // Caching probe: send an identical full request twice; a 2nd-call cached>0 means implicit caching.
        try
        {
            var probeReq = new GenerateContentRequest
            {
                Contents = new List<Content> { new("List my budgets.") { Role = "user" } },
                Tools = new Tools { new Tool { FunctionDeclarations = full } },
                SystemInstruction = new Content(SystemInstruction),
                GenerationConfig = new GenerationConfig { ThinkingConfig = GeminiThinking.Low(Model) },
            };
            var r1 = await model.GenerateContent(probeReq);
            var r2 = await model.GenerateContent(probeReq);
            sb.AppendLine($"\nCaching probe ({Model}): call1 cached={r1?.UsageMetadata?.CachedContentTokenCount ?? 0}/{r1?.UsageMetadata?.PromptTokenCount ?? 0}, " +
                          $"call2 cached={r2?.UsageMetadata?.CachedContentTokenCount ?? 0}/{r2?.UsageMetadata?.PromptTokenCount ?? 0} " +
                          "(cached>0 on call2 ⇒ implicit caching is active; 0 ⇒ needs a 2.5+ model or explicit cache).");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\nCaching probe failed: {ex.Message}");
        }

        var path = Path.Combine(AppContext.BaseDirectory, "tool_filtering_live.md");
        File.WriteAllText(path, sb.ToString());
        _out.WriteLine(sb.ToString());
        _out.WriteLine($"(written to {path})");
    }

    private sealed class NoopProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
