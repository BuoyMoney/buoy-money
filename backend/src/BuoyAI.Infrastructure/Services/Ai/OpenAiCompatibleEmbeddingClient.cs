using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// Embeddings from an OpenAI-compatible server (<c>POST {base}/embeddings</c>): Ollama with
/// nomic-embed-text or embeddinggemma, OpenAI, and so on. Enabled only when
/// <c>Ai:EmbeddingModel</c> is set. The vector length is <c>Ai:EmbeddingDimensions</c> (default 768);
/// a reply of a different length is an error, not silently stored, because the pgvector store
/// keeps the model and length with each row and search only compares rows that match.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingClient : IEmbeddingClient
{
    private readonly AiProviderOptions _options;
    private readonly HttpClient _http;

    public OpenAiCompatibleEmbeddingClient(AiProviderOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public bool IsEnabled => _options.SetupProblem == null && !string.IsNullOrWhiteSpace(_options.EmbeddingModel);
    public string Model => _options.EmbeddingModel;
    public int Dimensions => _options.EmbeddingDimensions;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled) return null;

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl + "/embeddings")
        {
            Content = JsonContent.Create(new EmbeddingRequest(_options.EmbeddingModel, text)),
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
            throw new AiUnavailableException($"Could not reach the AI service at {_options.BaseUrl}.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new EmbeddingApiException((int)response.StatusCode, body);
            }

            var parsed = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken);
            var vector = parsed?.Data is { Count: > 0 } ? parsed.Data[0].Embedding : null;
            if (vector == null) return null;
            if (vector.Length != Dimensions)
            {
                throw new InvalidOperationException(
                    $"The embedding model '{Model}' returned {vector.Length} numbers per text but Ai:EmbeddingDimensions is {Dimensions}. Set Ai:EmbeddingDimensions to {vector.Length}.");
            }
            return vector;
        }
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem>? Data);

    private sealed record EmbeddingItem([property: JsonPropertyName("embedding")] float[]? Embedding);
}
