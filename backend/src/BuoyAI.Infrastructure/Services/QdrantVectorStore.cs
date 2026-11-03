using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

using QdrantFilter = Qdrant.Client.Grpc.Filter;
using QdrantCondition = Qdrant.Client.Grpc.Condition;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The hosted store. Behaviour is what the three embedding services did when they called the
/// Qdrant client themselves: same collections, same ids, same payload field names.
/// </summary>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient? _client;
    private readonly ILogger<QdrantVectorStore> _logger;

    public QdrantVectorStore(QdrantClient? client, ILogger<QdrantVectorStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool IsEnabled => _client != null;

    public async Task EnsureCollectionAsync(
        string collection,
        int dimensions,
        IReadOnlyList<string>? integerIndexFields = null,
        bool onDiskPayload = false,
        bool recreateOnDimensionChange = false,
        CancellationToken cancellationToken = default)
    {
        if (_client == null) return;

        var existing = await _client.ListCollectionsAsync(cancellationToken);
        if (existing.Contains(collection))
        {
            if (!recreateOnDimensionChange)
            {
                _logger.LogInformation("Qdrant collection '{Collection}' already exists", collection);
                return;
            }

            // Skipping here would leave a collection sized for the old dimension, and every upsert
            // would then fail on a mismatch. A dimension change makes stored vectors unusable
            // anyway, so rebuild (loudly: this discards data).
            var info = await _client.GetCollectionInfoAsync(collection, cancellationToken);
            var existingSize = (int)(info.Config?.Params?.VectorsConfig?.Params?.Size ?? 0);
            if (existingSize == dimensions)
            {
                _logger.LogInformation("Qdrant collection '{Collection}' already exists", collection);
                return;
            }

            _logger.LogWarning(
                "Qdrant collection '{Collection}' is {Existing}-dim but this build writes {New}-dim. "
                + "Recreating; {Points} existing point(s) will be discarded and must be re-embedded.",
                collection, existingSize, dimensions, info.PointsCount);
            await _client.DeleteCollectionAsync(collection, cancellationToken: cancellationToken);
        }

        await _client.CreateCollectionAsync(
            collectionName: collection,
            vectorsConfig: new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
            onDiskPayload: onDiskPayload,
            cancellationToken: cancellationToken);

        foreach (var field in integerIndexFields ?? [])
        {
            await _client.CreatePayloadIndexAsync(
                collectionName: collection,
                fieldName: field,
                schemaType: PayloadSchemaType.Integer,
                cancellationToken: cancellationToken);
        }

        _logger.LogInformation("Created Qdrant collection '{Collection}' with {Dimensions} dimensions", collection, dimensions);
    }

    public async Task UpsertAsync(string collection, VectorPoint point, CancellationToken cancellationToken = default)
    {
        if (_client == null) return;

        var qdrantPoint = new PointStruct { Id = ToPointId(point.Key), Vectors = point.Vector };
        foreach (var (field, value) in point.Payload)
        {
            qdrantPoint.Payload[field] = ToPayloadValue(value);
        }

        await _client.UpsertAsync(collectionName: collection, points: [qdrantPoint], cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection,
        float[] vector,
        string model,
        int limit,
        float minScore = 0f,
        int? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (_client == null) return [];

        var found = await _client.SearchAsync(
            collectionName: collection,
            vector: vector,
            filter: userId is { } id ? IntegerFilter("user_id", id) : null,
            limit: (ulong)limit,
            scoreThreshold: minScore > 0f ? minScore : null,
            cancellationToken: cancellationToken);

        var hits = new List<VectorHit>(found.Count);
        foreach (var p in found)
        {
            var payload = new Dictionary<string, string>();
            foreach (var (field, value) in p.Payload)
            {
                payload[field] = FromPayloadValue(value);
            }
            hits.Add(new VectorHit(ToKey(p.Id), p.Score, payload));
        }
        return hits;
    }

    public async Task DeleteByFieldAsync(string collection, string field, int value, CancellationToken cancellationToken = default)
    {
        if (_client == null) return;
        await _client.DeleteAsync(collectionName: collection, filter: IntegerFilter(field, value), cancellationToken: cancellationToken);
    }

    // Numeric keys (chat message ids, help-slug hashes) were numeric point ids; GUID keys
    // (transaction merchant facts) were UUID point ids. Keeping that keeps existing hosted data valid.
    internal static PointId ToPointId(string key) =>
        ulong.TryParse(key, out var num) ? new PointId { Num = num } : new PointId { Uuid = key };

    internal static string ToKey(PointId id) =>
        id.PointIdOptionsCase == PointId.PointIdOptionsOneofCase.Uuid ? id.Uuid : id.Num.ToString();

    internal static Value ToPayloadValue(object value) => value switch
    {
        int i => i,
        long l => l,
        string s => s,
        _ => value.ToString() ?? string.Empty
    };

    internal static string FromPayloadValue(Value value) => value.KindCase switch
    {
        Value.KindOneofCase.IntegerValue => value.IntegerValue.ToString(),
        Value.KindOneofCase.StringValue => value.StringValue,
        _ => string.Empty
    };

    internal static QdrantFilter IntegerFilter(string field, int value) => new()
    {
        Must = { new QdrantCondition { Field = new FieldCondition { Key = field, Match = new Match { Integer = value } } } }
    };
}
