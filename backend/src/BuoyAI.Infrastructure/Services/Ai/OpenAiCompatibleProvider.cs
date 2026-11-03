using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// Any server that speaks the OpenAI chat-completions protocol: Ollama, OpenAI, OpenRouter,
/// Gemini's compatibility endpoint, LM Studio, vLLM and the like. One class; the preset only
/// fills in the base URL and whether a key is required. Plain HTTP and JSON, no SDK.
///
/// Text only. Chat with tools, agents and attachments still run on Gemini's own code, so
/// <see cref="Capabilities"/> says no to those.
/// </summary>
public sealed partial class OpenAiCompatibleProvider : IAiProvider
{
    private readonly AiProviderOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<OpenAiCompatibleProvider> _logger;
    private readonly BuoyAiMetrics? _metrics;

    public OpenAiCompatibleProvider(
        AiProviderOptions options,
        HttpClient http,
        ILogger<OpenAiCompatibleProvider> logger,
        BuoyAiMetrics? metrics = null)
    {
        _options = options;
        _http = http;
        _logger = logger;
        _metrics = metrics;
    }

    public string Id => _options.Provider;
    public bool IsConfigured => _options.SetupProblem == null;

    public AiCapabilities Capabilities => new(
        Text: IsConfigured,
        ChatAndAgents: false,
        Attachments: false,
        Embeddings: IsConfigured && !string.IsNullOrWhiteSpace(_options.EmbeddingModel));

    public string ResolveModel(string requestedModel, string feature) =>
        _options.FeatureModels.TryGetValue(feature, out var model) ? model : _options.Model;

    public async Task<AiTextResult> GenerateTextAsync(AiTextRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new AiUnavailableException(
                $"The AI provider isn't set up: {_options.SetupProblem}", null);
        }
        if (request.Files is { Count: > 0 })
        {
            throw new AiUnavailableException(
                "This AI provider can't read attachments yet. Photos and PDFs need Gemini.", null);
        }

        var model = ResolveModel(request.Model, request.Feature);
        var body = new ChatRequest(model, [new ChatMessage("user", request.Prompt)], _options.Temperature, false,
            string.IsNullOrEmpty(_options.ReasoningEffort) ? null : _options.ReasoningEffort);

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl + "/chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI provider {Provider} unreachable at {BaseUrl}", Id, SafeBaseUrl());
            throw new AiUnavailableException(
                "The AI service could not be reached. Ask the server admin to check it is running and the address is right.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AI provider {Provider} answered {Status}: {Body}", Id, (int)response.StatusCode, Truncate(errorBody, 500));
                throw new AiUnavailableException(DescribeFailure((int)response.StatusCode, errorBody, model), null);
            }

            ChatResponse? parsed;
            try
            {
                parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new AiUnavailableException("The AI service sent a reply this app can't read.", ex);
            }

            var content = parsed?.Choices is { Count: > 0 } ? parsed.Choices[0].Message?.Content : null;
            if (content == null)
                throw new AiUnavailableException("The AI service returned no text.", null);

            var text = ThinkBlock().Replace(content, "").Trim();
            var input = parsed!.Usage?.PromptTokens;
            var output = parsed.Usage?.CompletionTokens;
            var cost = CostFor(input, output, parsed.Usage?.Cost);

            // Categorization spend is recorded (and counted in the metrics) by AIUsageTracker.
            if (request.Feature != "categorization")
                _metrics?.RecordAiUsage(model, request.Feature, (input ?? 0) + (output ?? 0), cost);
            return new AiTextResult(text, input, output, cost);
        }
    }

    /// <summary>
    /// Local models cost 0. OpenRouter reports what it charged, which wins. Otherwise the
    /// operator's per-million prices (default 0, so an unpriced model is never billed at some
    /// other model's rate).
    /// </summary>
    private decimal CostFor(int? input, int? output, decimal? reported)
    {
        if (_options.IsLocal) return 0m;
        if (reported.HasValue) return reported.Value;
        return (input ?? 0) / 1_000_000m * _options.PriceInputPerMillion
             + (output ?? 0) / 1_000_000m * _options.PriceOutputPerMillion;
    }

    /// <summary>The base URL with any user:password part removed, for logs.</summary>
    private string SafeBaseUrl() =>
        Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var uri)
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : "(invalid)";

    // Shown to every signed-in member, so no upstream body text: the body goes to the server log only.
    private static string DescribeFailure(int status, string body, string model) => status switch
    {
        401 or 403 => "The AI service rejected the API key.",
        404 => $"The AI service doesn't know the model '{model}'. For Ollama, run: ollama pull {model}",
        429 => "The AI service says there have been too many requests or the quota is used up.",
        _ => $"The AI service returned an error ({status}).",
    };

    private static string ExtractMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? body;
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)) return m.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // not JSON: show it as is
        }
        return body;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    // Reasoning models (DeepSeek-R1, Qwen3 and others) can put their thinking in the reply
    // between think tags. It is not part of the answer and would break a one-line format.
    [GeneratedRegex("<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] List<Choice>? Choices,
        [property: JsonPropertyName("usage")] UsageInfo? Usage);

    private sealed record Choice([property: JsonPropertyName("message")] ReplyMessage? Message);

    private sealed record ReplyMessage([property: JsonPropertyName("content")] string? Content);

    private sealed record UsageInfo(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        [property: JsonPropertyName("cost")] decimal? Cost);
}
