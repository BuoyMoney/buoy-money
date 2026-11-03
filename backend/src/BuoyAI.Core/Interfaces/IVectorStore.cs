namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Where embeddings live. The chat-history, transaction-category and help-article indexes all go
/// through this one interface; the backing store is chosen by <c>VectorStore:Provider</c>
/// (<c>qdrant</c> or <c>pgvector</c>). Every method degrades quietly when the store is not
/// configured: callers check <see cref="IsEnabled"/> or just get empty results.
/// </summary>
public interface IVectorStore
{
    /// <summary>False when the configured store has no connection, so callers skip vector work.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Makes the collection ready to receive vectors of <paramref name="dimensions"/>. Qdrant
    /// creates (or, with <paramref name="recreateOnDimensionChange"/>, rebuilds) a collection;
    /// pgvector has one shared table, so this only confirms the table exists.
    /// </summary>
    Task EnsureCollectionAsync(
        string collection,
        int dimensions,
        IReadOnlyList<string>? integerIndexFields = null,
        bool onDiskPayload = false,
        bool recreateOnDimensionChange = false,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the point with the same key.</summary>
    Task UpsertAsync(string collection, VectorPoint point, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nearest neighbours by cosine similarity (1 is identical), best first, dropping anything
    /// under <paramref name="minScore"/>. When <paramref name="userId"/> is set only that user's
    /// points are searched. Points written by a different <paramref name="model"/> or dimension
    /// are ignored.
    /// </summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection,
        float[] vector,
        string model,
        int limit,
        float minScore = 0f,
        int? userId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes every point whose integer payload <paramref name="field"/> equals <paramref name="value"/>.</summary>
    Task DeleteByFieldAsync(string collection, string field, int value, CancellationToken cancellationToken = default);
}

/// <summary>
/// One stored vector. <see cref="Key"/> is stable per fact so re-embedding upserts in place (a
/// decimal number or a GUID; the Qdrant store maps those to its numeric and UUID point ids).
/// <see cref="Payload"/> values are ints or strings; a <c>user_id</c> entry is also what the
/// per-user search filter matches.
/// </summary>
public sealed record VectorPoint(
    string Key,
    float[] Vector,
    string Model,
    IReadOnlyDictionary<string, object> Payload);

public sealed record VectorHit(string Key, float Score, IReadOnlyDictionary<string, string> Payload)
{
    public string Get(string field) => Payload.TryGetValue(field, out var v) ? v : string.Empty;

    public int GetInt(string field) => int.TryParse(Get(field), out var v) ? v : 0;
}
