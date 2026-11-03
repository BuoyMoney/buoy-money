namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Turns text into a vector. The one place that knows the embedding model, its dimension and
/// the REST call, so the chat, help and transaction indexes (and a future non-Gemini provider)
/// share it.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>False when no API key is configured. Callers skip embedding work instead of failing.</summary>
    bool IsEnabled { get; }

    string Model { get; }

    /// <summary>Vector length. Stored with every vector (and used to size Qdrant collections).</summary>
    int Dimensions { get; }

    /// <summary>
    /// Embeds <paramref name="text"/>. Returns null when the client is disabled or the provider
    /// returned no vector. Throws <see cref="EmbeddingApiException"/> when the provider refuses.
    /// </summary>
    Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>The embedding provider answered with a non-success status.</summary>
public class EmbeddingApiException : InvalidOperationException
{
    public int StatusCode { get; }
    public string ResponseBody { get; }

    public EmbeddingApiException(int statusCode, string responseBody)
        : base($"embedContent returned {statusCode}: {responseBody}")
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
