using System.Net.Http.Json;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Gemini embedContent over REST. Mscc's EmbedContent() serialises a body the v1beta API rejects
/// ("Unknown name content / outputDimensionality"), so the call is made directly with the
/// verified-working body shape. gemini-embedding-2 supports flexible dimensions via MRL; 768
/// keeps Qdrant memory low at good quality.
/// </summary>
public sealed class GeminiEmbeddingClient : IEmbeddingClient
{
    public const string ModelName = "gemini-embedding-2";
    public const int VectorDimensions = 768;

    private static readonly HttpClient SharedHttpClient = new();

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public GeminiEmbeddingClient(IConfiguration configuration)
        : this(configuration, SharedHttpClient)
    {
    }

    internal GeminiEmbeddingClient(IConfiguration configuration, HttpClient http)
    {
        _http = http;
        _apiKey = GeminiApiKey.Resolve(configuration);
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_apiKey);
    public string Model => ModelName;
    public int Dimensions => VectorDimensions;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return null;

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{ModelName}:embedContent";
        var payload = new
        {
            model = $"models/{ModelName}",
            content = new { parts = new[] { new { text } } },
            outputDimensionality = VectorDimensions,
        };

        // The key goes in a header, not the URL, so traces and error logs never carry it.
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        request.Headers.Add("x-goog-api-key", _apiKey);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new EmbeddingApiException((int)response.StatusCode, body);
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedContentResponse>(cancellationToken);
        return parsed?.Embedding?.Values;
    }

    private sealed record EmbedContentResponse(EmbeddingValues? Embedding);
    private sealed record EmbeddingValues(float[]? Values);
}
