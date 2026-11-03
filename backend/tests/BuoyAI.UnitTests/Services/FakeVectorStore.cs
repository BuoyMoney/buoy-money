using BuoyAI.Core.Interfaces;

namespace BuoyAI.UnitTests.Services;

/// <summary>In-memory IVectorStore with exact cosine search, for service tests that need no database.</summary>
public sealed class FakeVectorStore : IVectorStore
{
  private readonly Dictionary<(string Collection, string Key), VectorPoint> _points = new();

  public FakeVectorStore(bool enabled = true) => IsEnabled = enabled;

  public bool IsEnabled { get; }

  public int Count(string collection) => _points.Count(p => p.Key.Collection == collection);

  public VectorPoint? Get(string collection, string key) => _points.GetValueOrDefault((collection, key));

  public Task EnsureCollectionAsync(string collection, int dimensions, IReadOnlyList<string>? integerIndexFields = null,
    bool onDiskPayload = false, bool recreateOnDimensionChange = false, CancellationToken cancellationToken = default) =>
    Task.CompletedTask;

  public Task UpsertAsync(string collection, VectorPoint point, CancellationToken cancellationToken = default)
  {
    if (IsEnabled) _points[(collection, point.Key)] = point;
    return Task.CompletedTask;
  }

  public Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] vector, string model, int limit,
    float minScore = 0f, int? userId = null, CancellationToken cancellationToken = default)
  {
    var hits = _points
      .Where(p => p.Key.Collection == collection && p.Value.Model == model && p.Value.Vector.Length == vector.Length)
      .Where(p => userId is null || (p.Value.Payload.TryGetValue("user_id", out var u) && Equals(u, userId)))
      .Select(p => new VectorHit(p.Key.Key, Cosine(vector, p.Value.Vector),
        p.Value.Payload.ToDictionary(kv => kv.Key, kv => Convert.ToString(kv.Value) ?? string.Empty)))
      .Where(h => h.Score >= minScore)
      .OrderByDescending(h => h.Score)
      .Take(limit)
      .ToList();
    return Task.FromResult<IReadOnlyList<VectorHit>>(hits);
  }

  public Task DeleteByFieldAsync(string collection, string field, int value, CancellationToken cancellationToken = default)
  {
    foreach (var key in _points
      .Where(p => p.Key.Collection == collection && p.Value.Payload.TryGetValue(field, out var v) && Equals(v, value))
      .Select(p => p.Key).ToList())
    {
      _points.Remove(key);
    }
    return Task.CompletedTask;
  }

  private static float Cosine(float[] a, float[] b)
  {
    double dot = 0, na = 0, nb = 0;
    for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
    return (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
  }
}
