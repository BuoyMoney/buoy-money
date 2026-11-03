namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for indexing and searching BuoyAI's published help/blog articles in the
/// Qdrant vector database, and for fetching a single article's full text by slug.
/// Backs the read-only search_help / get_help_article MCP tools. Help content is
/// global (not per-user). Gracefully degrades to empty/null when Qdrant or the
/// embedding API key is unavailable.
/// </summary>
public interface IHelpContentService
{
    /// <summary>
    /// Create the help_articles Qdrant collection if it does not already exist.
    /// Should be called on application startup. No-op when Qdrant is not configured.
    /// </summary>
    Task EnsureCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerate the embedded HelpContent markdown resources, skip drafts, embed the
    /// published articles and upsert one point per article (stable id keyed on slug,
    /// so this is idempotent and safe to re-run on every boot).
    /// </summary>
    Task IndexPublishedArticlesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Semantic search across published help articles. Returns matching articles with a
    /// public URL. Empty when Qdrant/embeddings are unavailable. Limit is clamped 1..10.
    /// </summary>
    Task<IReadOnlyList<HelpArticleHit>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch the full plain-text of a published help article by slug. Returns null when
    /// the article is missing or a draft.
    /// </summary>
    Task<HelpArticle?> GetArticleAsync(string slug, CancellationToken cancellationToken = default);
}

/// <summary>A single help-article search hit.</summary>
public class HelpArticleHit
{
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public float Score { get; set; }
}

/// <summary>A full help article's readable content plus its public URL.</summary>
public class HelpArticle
{
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
