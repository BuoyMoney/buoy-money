using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mscc.GenerativeAI;
using Mscc.GenerativeAI.Types;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// Google Gemini through the Mscc SDK, exactly as the app has always called it (this is the code
/// that used to live in GeminiApiClient, moved, not changed). Hosted runs on this provider, so
/// the model name the caller picked is used as is and usage is reported to the Gemini spend
/// metrics under the feature name.
/// </summary>
public sealed class GeminiNativeProvider : IAiProvider
{
    internal const string ProviderDownMessage = "AI features are temporarily unavailable. Try again shortly.";

    private readonly ILogger<GeminiNativeProvider> _logger;
    private readonly GeminiUsageReporter? _usage;
    private readonly string _apiKey;

    public GeminiNativeProvider(IConfiguration configuration, ILogger<GeminiNativeProvider> logger, GeminiUsageReporter? usage)
    {
        _logger = logger;
        _usage = usage;
        _apiKey = GeminiApiKey.Resolve(configuration);
    }

    public string Id => AiProviderOptions.Gemini;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    // Chat, agents, attachments and embeddings all run on Gemini today.
    public AiCapabilities Capabilities => new(Text: true, ChatAndAgents: true, Attachments: true, Embeddings: true);

    public string ResolveModel(string requestedModel, string feature) => requestedModel;

    /// <summary>
    /// Output tokens as Google bills them: the answer plus the model's thinking. The SDK reports thinking separately, and
    /// leaving it out under-counts every call on a model that thinks by default. Null only when Gemini sent neither number.
    /// </summary>
    internal static int? OutputTokens(int? candidates, int? thoughts) =>
        candidates is null && thoughts is null ? null : (candidates ?? 0) + (thoughts ?? 0);

    public async Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        // No key: AI is off. Say so; never invent a reply.
        if (!IsConfigured)
        {
            _logger.LogWarning("Gemini call skipped: no API key configured");
            throw new AiUnavailableException();
        }

        var files = request.Files;
        if (files is { Count: > 0 }) return await GenerateWithFilesAsync(request, files);

        _logger.LogInformation("Calling Gemini API with model: {Model}, prompt length: {PromptLength}", request.Model, request.Prompt.Length);

        try
        {
            // Apply HTTP/2 workarounds as documented by Context7 MCP
            AppContext.SetSwitch("System.Net.SocketsHttpHandler.Http2FlowControl.DisableDynamicWindowSizing", true);
            Environment.SetEnvironmentVariable("DOTNET_SYSTEM_NET_HTTP_USESOCKETSHTTPHANDLER", "0");

            // Additional HTTP/2 workarounds for header encoding issues
            AppContext.SetSwitch("System.Net.Http.UseSocketsHttpHandler", false);
            AppContext.SetSwitch("System.Net.Http.HttpClientHandler.UseSocketsHttpHandler", false);

            var generativeModel = new GoogleAI(_apiKey).GenerativeModel(request.Model);
            var response = await generativeModel.GenerateContent(request.Prompt);

            // Scheduled and batch work spends steadily with nobody watching, so every call is
            // reported to the Gemini spend metrics.
            _usage?.RecordGeneration(
                request.Model, request.Feature,
                response?.UsageMetadata?.PromptTokenCount,
                OutputTokens(response?.UsageMetadata?.CandidatesTokenCount, response?.UsageMetadata?.ThoughtsTokenCount),
                request.Prompt.Length, response?.Text?.Length ?? 0);

            // An empty reply comes back as empty text with its token counts, as it always has:
            // categorization turns it into Uncategorized at 0.0 and still records the spend.
            // Callers that need text (insights, receipts) reject it in GeminiApiClient.
            var text = response?.Text ?? "";
            _logger.LogInformation("Received response from Gemini API. Response length: {ResponseLength}", text.Length);
            return new AiTextResult(text,
                response?.UsageMetadata?.PromptTokenCount,
                OutputTokens(response?.UsageMetadata?.CandidatesTokenCount, response?.UsageMetadata?.ThoughtsTokenCount));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Gemini API call failed: {Message}", ex.Message);
            throw new AiUnavailableException(ProviderDownMessage, ex);
        }
        catch (NullReferenceException ex) when (ex.StackTrace?.Contains("HPackEncoder") == true || ex.StackTrace?.Contains("Http2Connection") == true)
        {
            _logger.LogError(ex, "HTTP/2 header encoding error calling Gemini API (known .NET HTTP/2 issue)");
            throw new AiUnavailableException(ProviderDownMessage, ex);
        }
        catch (Exception ex) when (ex is not AiUnavailableException)
        {
            _logger.LogError(ex, "Unexpected error calling Gemini API: {Message}. Type: {ExceptionType}", ex.Message, ex.GetType().Name);
            throw;
        }
    }

    private async Task<AiTextResult> GenerateWithFilesAsync(AiTextRequest request, IReadOnlyList<(string MimeType, byte[] Data)> files)
    {
        var parts = new List<IPart> { new TextData { Text = request.Prompt } };
        foreach (var (mimeType, data) in files)
        {
            parts.Add(new InlineData { MimeType = mimeType, Data = Convert.ToBase64String(data) });
        }

        var generativeModel = new GoogleAI(_apiKey).GenerativeModel(request.Model);
        Mscc.GenerativeAI.Types.GenerateContentResponse? response;
        try
        {
            response = await generativeModel.GenerateContent(parts);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Gemini API call failed: {Message}", ex.Message);
            throw new AiUnavailableException(ProviderDownMessage, ex);
        }
        _usage?.RecordGeneration(
            request.Model, request.Feature,
            response?.UsageMetadata?.PromptTokenCount,
            OutputTokens(response?.UsageMetadata?.CandidatesTokenCount, response?.UsageMetadata?.ThoughtsTokenCount),
            request.Prompt.Length, response?.Text?.Length ?? 0);
        var text = response?.Text ?? throw new AiUnavailableException(ProviderDownMessage, null);
        return new AiTextResult(text,
            response.UsageMetadata?.PromptTokenCount,
            OutputTokens(response.UsageMetadata?.CandidatesTokenCount, response.UsageMetadata?.ThoughtsTokenCount));
    }
}
