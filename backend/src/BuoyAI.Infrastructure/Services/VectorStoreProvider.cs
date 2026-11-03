namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Reads <c>VectorStore:Provider</c>. Unset means qdrant (hosted). Any other value than
/// qdrant or pgvector throws, so a typo fails at startup instead of silently picking a backend.
/// </summary>
public static class VectorStoreProvider
{
    public const string Qdrant = "qdrant";
    public const string PgVector = "pgvector";

    public static string Resolve(string? configured)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? Qdrant : configured.Trim().ToLowerInvariant();
        return value is Qdrant or PgVector
            ? value
            : throw new InvalidOperationException(
                $"VectorStore:Provider '{configured}' is not valid. Use '{Qdrant}' or '{PgVector}'.");
    }
}
