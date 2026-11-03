using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Vector store in the app's own Postgres (pgvector). One <c>embeddings</c> table holds every
/// collection; each row carries the model and dimension that produced it, so changing the
/// embedding model leaves old rows ignored by search instead of breaking it.
///
/// Search is an exact scan over one collection and user (no ANN index). A user has hundreds to a
/// few thousand rows, and help articles are about a hundred, so an exact scan is fast and always
/// returns the true nearest neighbours. Add an HNSW index per dimension if a collection ever
/// grows past what a scan handles.
///
/// The table comes from migration 0048, which does nothing on a server without the extension.
/// So that installing pgvector later does not need a manual migration edit, the store re-runs that
/// same idempotent script once at startup when the table is missing, and disables itself with a
/// warning if it still cannot create it.
/// </summary>
public sealed class PgVectorStore : IVectorStore
{
    private const string MigrationResourceSuffix = "0048_add_pgvector_embeddings.up.sql";

    private readonly NpgsqlDataSource? _dataSource;
    private readonly ILogger<PgVectorStore> _logger;
    private readonly SemaphoreSlim _readyLock = new(1, 1);
    private volatile bool _ready;
    private volatile bool _unavailable;

    public PgVectorStore(string? connectionString, ILogger<PgVectorStore> logger)
    {
        _logger = logger;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("VectorStore:Provider is pgvector but no database connection string is set. Semantic search is disabled.");
            return;
        }
        _dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>True once configured, until a startup check finds the table cannot be created.</summary>
    public bool IsEnabled => _dataSource != null && !_unavailable;

    public async Task EnsureCollectionAsync(
        string collection,
        int dimensions,
        IReadOnlyList<string>? integerIndexFields = null,
        bool onDiskPayload = false,
        bool recreateOnDimensionChange = false,
        CancellationToken cancellationToken = default)
    {
        // Collections are rows, not objects, so there is nothing to create per collection.
        await EnsureReadyAsync(cancellationToken);
    }

    public async Task UpsertAsync(string collection, VectorPoint point, CancellationToken cancellationToken = default)
    {
        if (!await EnsureReadyAsync(cancellationToken)) return;

        int? userId = point.Payload.TryGetValue("user_id", out var u) && u is int id ? id : null;

        await using var cmd = _dataSource!.CreateCommand("""
            INSERT INTO public.embeddings (collection, key, user_id, model, dimensions, embedding, payload)
            VALUES (@c, @k, @u, @m, @d, @v, @p)
            ON CONFLICT (collection, key) DO UPDATE
            SET user_id = EXCLUDED.user_id, model = EXCLUDED.model, dimensions = EXCLUDED.dimensions,
                embedding = EXCLUDED.embedding, payload = EXCLUDED.payload, updated_at = now()
            """);
        cmd.Parameters.AddWithValue("c", collection);
        cmd.Parameters.AddWithValue("k", point.Key);
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Integer) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("m", point.Model);
        cmd.Parameters.AddWithValue("d", point.Vector.Length);
        cmd.Parameters.Add(VectorParameter("v", point.Vector));
        cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(point.Payload) });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
        if (!await EnsureReadyAsync(cancellationToken)) return [];

        // The CASE keeps the distance operator away from rows of another dimension, which pgvector
        // would reject; the planner is free to evaluate WHERE terms in any order, but not a CASE branch.
        await using var cmd = _dataSource!.CreateCommand("""
            SELECT key, score, payload::text FROM (
                SELECT key, payload,
                       1 - (CASE WHEN dimensions = @d THEN embedding <=> @v END) AS score
                FROM public.embeddings
                WHERE collection = @c AND model = @m AND (@u IS NULL OR user_id = @u)
            ) scored
            WHERE score >= @min
            ORDER BY score DESC
            LIMIT @n
            """);
        cmd.Parameters.AddWithValue("c", collection);
        cmd.Parameters.AddWithValue("m", model);
        cmd.Parameters.AddWithValue("d", vector.Length);
        cmd.Parameters.Add(VectorParameter("v", vector));
        cmd.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Integer) { Value = (object?)userId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("min", (double)minScore);
        cmd.Parameters.AddWithValue("n", limit);

        var hits = new List<VectorHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new VectorHit(reader.GetString(0), (float)reader.GetDouble(1), ReadPayload(reader.GetString(2))));
        }
        return hits;
    }

    public async Task DeleteByFieldAsync(string collection, string field, int value, CancellationToken cancellationToken = default)
    {
        if (!await EnsureReadyAsync(cancellationToken)) return;

        await using var cmd = field == "user_id"
            ? _dataSource!.CreateCommand("DELETE FROM public.embeddings WHERE collection = @c AND user_id = @v")
            : _dataSource!.CreateCommand("DELETE FROM public.embeddings WHERE collection = @c AND payload ->> @f = @s");
        cmd.Parameters.AddWithValue("c", collection);
        if (field == "user_id")
        {
            cmd.Parameters.AddWithValue("v", value);
        }
        else
        {
            cmd.Parameters.AddWithValue("f", field);
            cmd.Parameters.AddWithValue("s", value.ToString(CultureInfo.InvariantCulture));
        }
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string ToVectorLiteral(float[] vector)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return sb.Append(']').ToString();
    }

    // Sent as an untyped literal so Postgres casts it to the vector type.
    private static NpgsqlParameter VectorParameter(string name, float[] vector) =>
        new(name, NpgsqlDbType.Unknown) { Value = ToVectorLiteral(vector) };

    internal static IReadOnlyDictionary<string, string> ReadPayload(string json)
    {
        var payload = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            payload[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                ? prop.Value.GetString() ?? string.Empty
                : prop.Value.ToString();
        }
        return payload;
    }

    private async Task<bool> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready) return true;
        if (_dataSource == null || _unavailable) return false;

        await _readyLock.WaitAsync(cancellationToken);
        try
        {
            if (_ready) return true;

            if (!await TableExistsAsync(cancellationToken))
            {
                // Migration 0048 is journaled even where it did nothing (no extension at the time),
                // so run its idempotent script once more now that the extension may be installed.
                await RunMigrationScriptAsync(cancellationToken);
            }

            if (await TableExistsAsync(cancellationToken))
            {
                _ready = true;
                _logger.LogInformation("pgvector embeddings table is ready");
                return true;
            }

            _unavailable = true;
            _logger.LogWarning(
                "VectorStore:Provider is pgvector but the embeddings table could not be created: the 'vector' " +
                "extension is missing or this database user may not create it. Install pgvector " +
                "(for example postgresql-16-pgvector) and restart. Semantic search is disabled until then.");
            return false;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "pgvector store could not reach the database; will retry on the next call");
            return false;
        }
        finally
        {
            _readyLock.Release();
        }
    }

    private async Task<bool> TableExistsAsync(CancellationToken cancellationToken)
    {
        await using var cmd = _dataSource!.CreateCommand("SELECT to_regclass('public.embeddings') IS NOT NULL");
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task RunMigrationScriptAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(PgVectorStore).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(MigrationResourceSuffix, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);

        await using var cmd = _dataSource!.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
