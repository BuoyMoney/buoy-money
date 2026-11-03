using BuoyAI.Infrastructure.Configuration;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services.Ai;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// One tool invocation that happened during an agentic run — the name, the JSON arguments
/// the model supplied, and the raw JSON result the tool returned. The write-agent handoff
/// uses these records to build its undo journal; the read-only insight drill-down uses them
/// only for logging.
/// </summary>
public record AgenticToolCall(string ToolName, string ArgumentsJson, string ResultJson);

/// <summary>
/// Result of an agentic run: the model's final text (e.g. the insight JSON array, or a
/// human summary of actions taken) plus every tool call it made along the way.
/// </summary>
public record AgenticRunResult(string FinalText, IReadOnlyList<AgenticToolCall> ToolCalls)
{
    public static AgenticRunResult Empty { get; } = new(string.Empty, Array.Empty<AgenticToolCall>());
}

/// <summary>
/// Runs a bounded Gemini native-function-calling loop: the model may call the supplied tools
/// (via <paramref name="executeToolAsync"/>) to investigate before producing its final answer.
/// This is the single place the raw Mscc SDK tool loop lives, reused by both the read-only
/// insight drill-down and the write-capable "act on suggestion" handoff — the difference
/// between them is purely WHICH tool declarations and executor callback are passed in.
/// </summary>
public interface IGeminiAgenticRunner
{
    /// <returns>
    /// The final text and the ordered list of tool calls. If no API key is configured or the
    /// loop errors before producing text, <see cref="AgenticRunResult.FinalText"/> is empty —
    /// callers should fall back to a non-agentic path.
    /// </returns>
    Task<AgenticRunResult> RunAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<FunctionDeclaration> functionDeclarations,
        Func<string, Dictionary<string, object>, Task<string>> executeToolAsync,
        int maxIterations,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public class GeminiAgenticRunner : IGeminiAgenticRunner
{
    private readonly string _geminiApiKey;
    private readonly ILogger<GeminiAgenticRunner> _logger;
    private readonly GeminiUsageReporter _geminiUsage;
    private readonly IAIUsageTracker? _usageTracker;

    public GeminiAgenticRunner(
        IConfiguration configuration,
        ILogger<GeminiAgenticRunner> logger,
        GeminiUsageReporter geminiUsage,
        IAIUsageTracker? usageTracker = null)
    {
        _geminiApiKey = GeminiApiKey.Resolve(configuration);
        _logger = logger;
        _geminiUsage = geminiUsage;
        _usageTracker = usageTracker;
    }

    public async Task<AgenticRunResult> RunAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        IReadOnlyList<FunctionDeclaration> functionDeclarations,
        Func<string, Dictionary<string, object>, Task<string>> executeToolAsync,
        int maxIterations,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _geminiApiKey;
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("GeminiAgenticRunner: no API key configured; returning empty result for fallback.");
            return AgenticRunResult.Empty;
        }

        // Same HTTP/2 workarounds the rest of the Gemini call sites apply.
        AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2FlowControl.DisableDynamicWindowSizing", true);
        Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER", "0");
        AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);

        var toolCalls = new List<AgenticToolCall>();

        try
        {
            var googleAI = new GoogleAI(apiKey);
            var generativeModel = googleAI.GenerativeModel(model);

            var tools = new Tools();
            tools.Add(new Tool { FunctionDeclarations = functionDeclarations.ToList() });

            var toolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig { Mode = FunctionCallingConfigMode.Auto }
            };

            // Low thinking, as in CallGeminiWithToolsAsync (avoids thought-only turns): level "low"
            // on Gemini 3+, budget 0 on the Gemini 2.5 fallback (GeminiThinking). No sampling
            // settings: Gemini deprecated them.
            var generationConfig = new GenerationConfig
            {
                ThinkingConfig = GeminiThinking.Low(model)
            };

            var systemContent = string.IsNullOrEmpty(systemPrompt) ? null : new Content(systemPrompt);
            var contents = new List<Content> { new Content(userPrompt, Role.User) };

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var request = new GenerateContentRequest
                {
                    Contents = contents,
                    Tools = tools,
                    ToolConfig = toolConfig,
                    GenerationConfig = generationConfig,
                    SystemInstruction = systemContent
                };

                var response = await generativeModel.GenerateContent(request);

                // Inside the tool loop, so every iteration is counted. An agentic loop that
                // fails to converge is the fastest way to spend money here, and counting
                // only the final turn would hide exactly that.
                var reportedInput = response?.UsageMetadata?.PromptTokenCount;
                var reportedOutput = GeminiNativeProvider.OutputTokens(
                    response?.UsageMetadata?.CandidatesTokenCount, response?.UsageMetadata?.ThoughtsTokenCount);
                _geminiUsage.RecordGeneration(model, "agentic", reportedInput, reportedOutput);
                await RecordUsageRowAsync(model, reportedInput, reportedOutput);

                var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;

                var functionCall = parts?.FirstOrDefault(p => p.FunctionCall != null)?.FunctionCall
                    ?? (response?.FunctionCalls != null && response.FunctionCalls.Count > 0
                        ? response.FunctionCalls[0]
                        : null);

                if (functionCall == null)
                {
                    // Prefer non-thought text; fall back to convenience Text.
                    var text = parts?.Where(p => p.Thought != true && !string.IsNullOrEmpty(p.Text))
                                   .Select(p => p.Text)
                                   .FirstOrDefault()
                               ?? response?.Text;

                    if (!string.IsNullOrEmpty(text))
                    {
                        return new AgenticRunResult(text, toolCalls);
                    }

                    // Thought-only / empty turn: nudge and continue.
                    contents.Add(new Content("Please provide your final answer now.", Role.User));
                    continue;
                }

                var toolName = functionCall.Name ?? "unknown";
                var arguments = ParseArgs(functionCall.Args);
                var argsJson = SafeSerialize(arguments);

                string toolResult;
                try
                {
                    toolResult = await executeToolAsync(toolName, arguments);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "GeminiAgenticRunner: tool {ToolName} threw", toolName);
                    toolResult = JsonSerializer.Serialize(new { Error = ex.Message });
                }

                toolCalls.Add(new AgenticToolCall(toolName, argsJson, toolResult));

                // Preserve the model's parts (incl. thought signature) then append the result.
                if (parts != null && parts.Count > 0)
                {
                    contents.Add(new Content(parts, Role.Model));
                }
                else
                {
                    contents.Add(new Content(new Part { FunctionCall = functionCall }, Role.Model));
                }

                contents.Add(new Content(
                    new FunctionResponse { Name = functionCall.Name, Response = new { result = toolResult } },
                    Role.User)); // functionResponse rides the user role (3.7-flash 400s on the legacy function role)
            }

            _logger.LogInformation(
                "GeminiAgenticRunner hit max iterations ({Max}) without a final text answer ({ToolCalls} tool calls).",
                maxIterations, toolCalls.Count);
            return new AgenticRunResult(string.Empty, toolCalls);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GeminiAgenticRunner failed: {Message}", ex.Message);
            return new AgenticRunResult(string.Empty, toolCalls);
        }
    }

    /// <summary>
    /// One usage row per model turn for the user whose task this is (see <see cref="AiUsageScope"/>), with Gemini's own counts.
    /// A turn Gemini sent no counts for writes nothing: there is nothing true to record.
    /// </summary>
    private async Task RecordUsageRowAsync(string model, int? inputTokens, int? outputTokens)
    {
        if (_usageTracker == null || AiUsageScope.CurrentUserId is not { } userId) return;
        if (inputTokens == null || outputTokens == null) return;
        await _usageTracker.RecordCallAsync(new AiUsageEntry(
            userId, model, AiProviderOptions.Gemini, inputTokens.Value, outputTokens.Value, false,
            AiPricing.Cost(model, inputTokens.Value, outputTokens.Value, _logger), "agentic", EmitMetrics: false));
    }

    private static Dictionary<string, object> ParseArgs(object? args)
    {
        var result = new Dictionary<string, object>();
        if (args is JsonElement json && json.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in json.EnumerateObject())
            {
                result[prop.Name] = prop.Value;
            }
        }
        else if (args is IDictionary<string, object> dict)
        {
            foreach (var kvp in dict)
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    private static string SafeSerialize(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch
        {
            return "{}";
        }
    }
}
