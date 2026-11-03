using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BuoyAI.Core.Interfaces;
using Markdig;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Infrastructure.Telemetry;
using Sentry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Indexes BuoyAI's published help/blog articles into the vector store and serves them to the
/// read-only search_help / get_help_article MCP tools. Articles are bundled as embedded
/// resources under HelpContent/ (checked-in copies synced from the marketing site by
/// scripts/sync-help.sh). Help is GLOBAL — no per-user filtering.
///
/// Mirrors ChatEmbeddingService: same Gemini embedContent REST call + shape, same
/// env-suffixed collection naming, and the same graceful degradation (_isEnabled =
/// store enabled; every method returns empty/null rather than throwing when the vector store or
/// the embedding API key is missing).
/// </summary>
public class HelpContentService : IHelpContentService
{
    private readonly ILogger<HelpContentService> _logger;
    private readonly GeminiUsageReporter _geminiUsage;
    private readonly IVectorStore _vectorStore;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly string _collectionName;

    // Reuse the verified-working embedding constants from ChatEmbeddingService.
    private int EmbeddingDimensions => _embeddingClient.Dimensions;
    private string EmbeddingModelName => _embeddingClient.Model;

    // Cap the stored article body so a single payload stays small; the full text is still
    // re-readable from the embedded resource via GetArticleAsync.
    private const int MaxStoredContentChars = 4000;

    private const string PublicBlogBaseUrl = "https://buoy-ai.com/blog";
    private const string PublicDocsBaseUrl = "https://buoy-ai.com/docs";

    /// <summary>
    /// Help docs (Starlight pages under marketing/src/content/docs/docs/&lt;section&gt;/&lt;page&gt;.md)
    /// are embedded flat as docs-&lt;section&gt;-&lt;page&gt;.md by scripts/sync-help.sh, because
    /// embedded-resource names are flat. Section folder names never contain a hyphen (the sync
    /// script refuses one), so the first hyphen after this prefix separates section from page.
    /// </summary>
    internal const string DocsSlugPrefix = "docs-";

    public HelpContentService(
        ILogger<HelpContentService> logger,
        IVectorStore vectorStore,
        IEmbeddingClient embeddingClient,
        IHostEnvironment hostEnvironment,
        GeminiUsageReporter geminiUsage)
    {
        _logger = logger;
        _geminiUsage = geminiUsage;
        _vectorStore = vectorStore;
        _embeddingClient = embeddingClient;

        var environment = hostEnvironment.EnvironmentName.ToLowerInvariant();
        _collectionName = environment switch
        {
            "production" => "help_articles",
            "staging" => "help_articles_staging",
            "development" => "help_articles_dev",
            _ => $"help_articles_{environment}"
        };

        if (!_vectorStore.IsEnabled)
        {
            _logger.LogWarning("HelpContentService initialized without a vector store. Help search disabled.");
        }
        else if (!_embeddingClient.IsEnabled)
        {
            _logger.LogWarning("HelpContentService initialized without a Gemini API key. Help search disabled.");
        }
        else
        {
            _logger.LogInformation("HelpContentService initialized with collection '{CollectionName}' for environment '{Environment}'",
                _collectionName, environment);
        }
    }

    private bool CanEmbed => _vectorStore.IsEnabled && _embeddingClient.IsEnabled;

    /// <summary>
    /// Generates an embedding through the shared <see cref="IEmbeddingClient"/>. Refusals reach
    /// Sentry before the exception is swallowed or reshaped upstream.
    /// </summary>
    private async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        float[]? values;
        try
        {
            values = await _embeddingClient.EmbedAsync(text, cancellationToken);
        }
        catch (EmbeddingApiException ex)
        {
            _geminiUsage.ReportRefusal(ex.StatusCode, "help-embedding", EmbeddingModelName, ex.ResponseBody);
            throw;
        }

        if (values != null)
        {
            // Counts toward the same spend metric as chat, so the cost alert sees total
            // Gemini usage rather than only the conversational slice.
            _geminiUsage.RecordEmbedding(
                EmbeddingModelName, "help-embedding", GeminiUsageReporter.EstimateTokens(text.Length));
        }
        return values;
    }

    public async Task EnsureCollectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_vectorStore.IsEnabled)
        {
            _logger.LogDebug("Skipping help_articles collection init - vector store not configured");
            return;
        }

        try
        {
            // No payload index needed: help is global, so we never filter by user.
            await _vectorStore.EnsureCollectionAsync(
                _collectionName, EmbeddingDimensions, cancellationToken: cancellationToken);
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during help_articles collection init. StatusCode: {StatusCode}, Detail: {Detail}",
                grpcEx.StatusCode, grpcEx.Status.Detail);
            // Don't throw - collection init failure shouldn't crash the app.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize vector collection '{CollectionName}'", _collectionName);
            // Don't throw - help indexing is best-effort.
        }
    }

    public async Task IndexPublishedArticlesAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEmbed)
        {
            _logger.LogDebug("Skipping help article indexing - vector store or API key not configured");
            return;
        }

        try
        {
            var articles = LoadEmbeddedArticles();
            var indexed = 0;

            foreach (var article in articles)
            {
                if (article.Draft)
                {
                    _logger.LogDebug("Skipping draft help article '{Slug}'", article.Slug);
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var embedText = $"{article.Title}\n\n{article.Description}\n\n{article.PlainTextBody}";
                var embedding = await GenerateEmbeddingAsync(embedText, cancellationToken);
                if (embedding == null || embedding.Length == 0)
                {
                    _logger.LogWarning("Failed to generate embedding for help article '{Slug}'", article.Slug);
                    continue;
                }

                var storedContent = BuoyAI.Core.Utilities.SafeText.Truncate(article.PlainTextBody, MaxStoredContentChars);

                await _vectorStore.UpsertAsync(
                    _collectionName,
                    new VectorPoint(
                        StableId(article.Slug).ToString(),
                        embedding,
                        EmbeddingModelName,
                        new Dictionary<string, object>
                        {
                            ["slug"] = article.Slug,
                            ["title"] = article.Title,
                            ["description"] = article.Description,
                            ["url"] = article.Url,
                            ["content"] = storedContent
                        }),
                    cancellationToken);

                indexed++;
            }

            _logger.LogInformation("Indexed {Count} published help article(s) into '{CollectionName}'",
                indexed, _collectionName);
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during help article indexing. StatusCode: {StatusCode}, Detail: {Detail}",
                grpcEx.StatusCode, grpcEx.Status.Detail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to index help articles. ExceptionType: {ExType}", ex.GetType().Name);
            // Don't throw - indexing is best-effort and must not break startup.
        }
    }

    public async Task<IReadOnlyList<HelpArticleHit>> SearchAsync(
        string query,
        int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (!CanEmbed)
        {
            _logger.LogDebug("Skipping help search - vector store or API key not configured");
            return [];
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 10);

        try
        {
            var queryEmbedding = await GenerateEmbeddingAsync(query, cancellationToken);
            if (queryEmbedding == null || queryEmbedding.Length == 0)
            {
                _logger.LogWarning("Failed to generate embedding for help search query");
                return [];
            }

            // No filter: help articles are global.
            var found = await _vectorStore.SearchAsync(
                _collectionName, queryEmbedding, EmbeddingModelName, limit, cancellationToken: cancellationToken);

            var results = found.Select(hit => new HelpArticleHit
            {
                Slug = hit.Get("slug"),
                Title = hit.Get("title"),
                Description = hit.Get("description"),
                Url = hit.Get("url"),
                Score = hit.Score
            }).ToList();

            _logger.LogInformation("Help search for query '{Query}' returned {Count} results", query, results.Count);
            return results;
        }
        catch (Grpc.Core.RpcException grpcEx)
        {
            _logger.LogWarning(grpcEx,
                "Qdrant gRPC connection failed during help search. StatusCode: {StatusCode}, Detail: {Detail}",
                grpcEx.StatusCode, grpcEx.Status.Detail);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search help articles. ExceptionType: {ExType}", ex.GetType().Name);
            return [];
        }
    }

    public Task<HelpArticle?> GetArticleAsync(string slug, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Task.FromResult<HelpArticle?>(null);
        }

        try
        {
            var raw = ReadEmbeddedArticleBySlug(slug);
            if (raw == null)
            {
                return Task.FromResult<HelpArticle?>(null);
            }

            var parsed = ParseArticle(slug, raw);
            if (parsed.Draft)
            {
                return Task.FromResult<HelpArticle?>(null);
            }

            return Task.FromResult<HelpArticle?>(new HelpArticle
            {
                Slug = parsed.Slug,
                Title = parsed.Title,
                Url = parsed.Url,
                Content = parsed.PlainTextBody
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read help article '{Slug}'", slug);
            return Task.FromResult<HelpArticle?>(null);
        }
    }

    // ----- Embedded resource access -------------------------------------------------

    private static Assembly ResourceAssembly => typeof(HelpContentService).Assembly;
    private const string ResourceMarker = ".HelpContent.";

    private static IEnumerable<(string Slug, string ResourceName)> EnumerateResources()
    {
        foreach (var name in ResourceAssembly.GetManifestResourceNames())
        {
            var markerIndex = name.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (markerIndex < 0 || !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = name[(markerIndex + ResourceMarker.Length)..];
            var slug = fileName[..^3]; // strip trailing ".md"
            yield return (slug, name);
        }
    }

    private static string? ReadResource(string resourceName)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? ReadEmbeddedArticleBySlug(string slug)
    {
        foreach (var (candidateSlug, resourceName) in EnumerateResources())
        {
            if (string.Equals(candidateSlug, slug, StringComparison.OrdinalIgnoreCase))
            {
                return ReadResource(resourceName);
            }
        }
        return null;
    }

    private List<ParsedHelpArticle> LoadEmbeddedArticles()
    {
        var list = new List<ParsedHelpArticle>();
        foreach (var (slug, resourceName) in EnumerateResources())
        {
            var raw = ReadResource(resourceName);
            if (raw == null)
            {
                continue;
            }
            list.Add(ParseArticle(slug, raw));
        }
        return list;
    }

    // ----- Frontmatter parsing (testable) -------------------------------------------

    /// <summary>
    /// Public URL for a slug: https://buoy-ai.com/blog/{slug}, or for a flattened help doc
    /// (docs-{section}-{page}) https://buoy-ai.com/docs/{section}/{page}.
    /// </summary>
    internal static string UrlForSlug(string slug)
    {
        if (slug.StartsWith(DocsSlugPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = slug[DocsSlugPrefix.Length..];
            var dash = rest.IndexOf('-');
            if (dash > 0 && dash < rest.Length - 1)
            {
                return $"{PublicDocsBaseUrl}/{rest[..dash]}/{rest[(dash + 1)..]}";
            }
        }
        return $"{PublicBlogBaseUrl}/{slug}";
    }

    // Starlight asides are fenced as ":::note", ":::caution[Title]" ... ":::". Plain-text
    // extraction would keep the fence markers; keep an aside's title as its own line instead.
    private static readonly System.Text.RegularExpressions.Regex DirectiveFence =
        new(@"^:::[a-z]*(?:\[(?<title>[^\]]*)\])?[ \t]*$",
            System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string StripStarlightDirectives(string body) =>
        DirectiveFence.Replace(body, m => m.Groups["title"].Success ? m.Groups["title"].Value : string.Empty);

    /// <summary>
    /// Parse a raw markdown file (leading <c>---</c> YAML-ish frontmatter fence + body) into a
    /// <see cref="ParsedHelpArticle"/>: title/description/draft from the frontmatter, plus the
    /// markdown body reduced to plain text via Markdig. Robust to missing frontmatter (whole file
    /// treated as body) and quoted values. Extracted for unit testing.
    /// </summary>
    internal static ParsedHelpArticle ParseArticle(string slug, string raw)
    {
        var normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        string frontmatter = string.Empty;
        string body = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var afterOpen = normalized["---\n".Length..];
            var closeIndex = afterOpen.IndexOf("\n---", StringComparison.Ordinal);
            if (closeIndex >= 0)
            {
                frontmatter = afterOpen[..closeIndex];
                // Body starts after the closing fence line's newline.
                var rest = afterOpen[(closeIndex + "\n---".Length)..];
                var nl = rest.IndexOf('\n');
                body = nl >= 0 ? rest[(nl + 1)..] : string.Empty;
            }
        }

        var fields = ParseFrontmatter(frontmatter);

        fields.TryGetValue("title", out var title);
        fields.TryGetValue("description", out var description);
        fields.TryGetValue("draft", out var draftValue);

        var draft = bool.TryParse(draftValue, out var d) && d;
        var plainText = Markdown.ToPlainText(StripStarlightDirectives(body)).Trim();

        return new ParsedHelpArticle(
            Slug: slug,
            Title: title ?? string.Empty,
            Description: description ?? string.Empty,
            Draft: draft,
            PlainTextBody: plainText,
            Url: UrlForSlug(slug));
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in frontmatter.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            value = StripQuotes(value);
            fields[key] = value;
        }
        return fields;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    /// <summary>
    /// Deterministic 63-bit id from a slug for a vector point key (a decimal string; Qdrant stores it as PointId.Num). Uses SHA-256 rather
    /// than string.GetHashCode (which is randomized per process in .NET), so re-runs upsert the
    /// same point — idempotent across boots/instances. Top bit masked off to stay safely positive.
    /// </summary>
    internal static ulong StableId(string slug)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(slug));
        var value = BitConverter.ToUInt64(bytes, 0);
        return value & 0x7FFFFFFFFFFFFFFFUL;
    }
}

/// <summary>Parsed help article: frontmatter fields + plain-text body + public URL.</summary>
internal sealed record ParsedHelpArticle(
    string Slug,
    string Title,
    string Description,
    bool Draft,
    string PlainTextBody,
    string Url);
