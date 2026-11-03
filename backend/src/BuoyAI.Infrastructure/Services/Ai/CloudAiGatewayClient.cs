using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Exceptions;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <param name="CallCostMicros">What the gateway's meter charged for this call, in micro-dollars.</param>
public sealed record CloudCompletion(string Text, int InputTokens, int OutputTokens, long CallCostMicros);

/// <summary>
/// The HTTP side of BuoyAI Cloud's AI gateway (the contract is in docs/self_host/ai.md). The app
/// knows the gateway only as an address, a pairing credential and this JSON; nothing from the Cloud code is referenced. The
/// gateway's messages are plain English and are passed to the user as they are.
/// </summary>
public sealed class CloudAiGatewayClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The gateway holds the worst-case cost of a call before it runs (every output token it might write), so a short
    // answer shouldn't ask to reserve a long one. These are generous for what each feature produces; the gateway has its own ceiling.
    private static readonly Dictionary<string, int> MaxOutputTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["categorization"] = 1024,
        ["memory-extraction"] = 2048,
        ["subscriptions"] = 2048,
    };

    private readonly HttpClient _http;
    private readonly AiProviderOptions _options;

    public CloudAiGatewayClient(HttpClient http, AiProviderOptions options)
    {
        _http = http;
        _options = options;
    }

    public async Task<CloudCompletion> CompleteAsync(string prompt, string feature, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Add("X-Buoy-Feature", feature);
        request.Headers.Add("X-Request-Id", Guid.NewGuid().ToString("N"));
        request.Content = JsonContent.Create(new
        {
            messages = new[] { new { role = "user", content = prompt } },
            max_tokens = MaxOutputTokens.TryGetValue(feature, out var max) ? max : (int?)null,
        });

        using var response = await SendAsync(request, ct);
        var body = await response.Content.ReadFromJsonAsync<Completion>(Json, ct)
            ?? throw new AiUnavailableException("BuoyAI Cloud sent an answer this app can't read.", null);
        var text = body.Choices is { Count: > 0 } ? body.Choices[0].Message?.Content : null;
        if (text == null) throw new AiUnavailableException("BuoyAI Cloud returned no text.", null);
        return new CloudCompletion(text, body.Usage?.PromptTokens ?? 0, body.Usage?.CompletionTokens ?? 0, body.Buoy?.CallCostMicros ?? 0);
    }

    public async Task<AiUsageSummary> GetUsageAsync(CancellationToken ct)
    {
        using var request = Build(HttpMethod.Get, "/v1/usage");
        using var response = await SendAsync(request, ct);
        return await ReadSummaryAsync(response, ct);
    }

    public async Task<AiUsageSummary> SetPaygAsync(bool enabled, long capMicros, CancellationToken ct)
    {
        using var request = Build(HttpMethod.Put, "/v1/usage/payg");
        request.Content = JsonContent.Create(new { enabled, capMicros });
        using var response = await SendAsync(request, ct);
        return await ReadSummaryAsync(response, ct);
    }

    private HttpRequestMessage Build(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, _options.CloudUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.CloudCredential);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            throw new AiUnavailableException("BuoyAI Cloud could not be reached. Check this server's internet connection and the BuoyAI Cloud address.", ex);
        }

        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            // { "error": { "code", "message", "details": { "canEnablePayg" } } }. Anything else is an outage.
            string? code = null, message = null;
            var canEnablePayg = false;
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                    message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                    canEnablePayg = error.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object
                        && d.TryGetProperty("canEnablePayg", out var p) && p.ValueKind == JsonValueKind.True;
                }
            }
            catch (JsonException)
            {
                // not the gateway's error shape (a proxy page, say)
            }

            if (code is "allowance_exhausted" or "payg_cap_reached")
                throw new AiAllowanceException(code, message ?? "Your BuoyAI Cloud AI allowance is used up.", canEnablePayg);
            if (code is "bad_cap" or "payg_unavailable")
                throw new DomainValidationException(message ?? "That can't be changed.");
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                throw new AiUnavailableException(message ?? "BuoyAI Cloud didn't accept this server's pairing. Pair the server again.", null);
            throw new AiUnavailableException(message ?? $"BuoyAI Cloud returned an error ({(int)response.StatusCode}).", null);
        }
    }

    private static async Task<AiUsageSummary> ReadSummaryAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var summary = await response.Content.ReadFromJsonAsync<AiUsageSummary>(Json, ct)
            ?? throw new AiUnavailableException("BuoyAI Cloud sent a usage report this app can't read.", null);
        return summary with { Source = "cloud", Unlimited = false, ByFeature = summary.ByFeature ?? [], Payg = summary.Payg ?? AiPaygStatus.NotAvailable };
    }

    private sealed record Completion(List<Choice>? Choices, Usage? Usage, Meter? Buoy);
    private sealed record Choice(ReplyMessage? Message);
    private sealed record ReplyMessage(string? Content);
    private sealed record Usage(
        [property: System.Text.Json.Serialization.JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: System.Text.Json.Serialization.JsonPropertyName("completion_tokens")] int? CompletionTokens);
    private sealed record Meter(long CallCostMicros);
}
