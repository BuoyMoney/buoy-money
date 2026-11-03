using BuoyAI.Core.Interfaces;

namespace BuoyAI.UnitTests;

/// <summary>Embedding client stand-ins for tests that construct the vector-index services.</summary>
internal sealed class StubEmbeddingClient(bool enabled = false) : IEmbeddingClient
{
    public int CallCount { get; private set; }
    public bool IsEnabled => enabled;
    public string Model => "stub-embedding";
    public int Dimensions => 768;

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult<float[]?>(enabled ? new float[Dimensions] : null);
    }
}
