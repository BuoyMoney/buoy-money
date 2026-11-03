using System.ComponentModel;
using System.Text.Json;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace BuoyAI.Infrastructure.MCP.Tools;

/// <summary>
/// Read-only MCP tools for searching and reading BuoyAI's published help/blog articles.
/// Help content is GLOBAL — the userId parameter is force-injected by the executor per
/// convention (userId is always first) but is intentionally unused here.
/// These tools are visible on both the in-app chat and the public /mcp Connect endpoint.
/// </summary>
[McpServerToolType]
public sealed class HelpReadTools
{
    private readonly IHelpContentService _helpContentService;
    private readonly ILogger<HelpReadTools> _logger;

    public HelpReadTools(
        IHelpContentService helpContentService,
        ILogger<HelpReadTools> logger)
    {
        _helpContentService = helpContentService;
        _logger = logger;
    }

    [McpServerTool(Name = "search_help", ReadOnly = true)]
    [Description("Search Buoy Money's help articles and guides for how the app works (budgeting, count-up, savings goals, families, etc.). Returns matching articles with a public link to read more.")]
    public async Task<string> SearchHelpAsync(
        [Description("User ID (injected automatically; help is global and this is ignored)")] int userId,
        [Description("Search query - describe what you're looking for (e.g., 'how does count-up budgeting work', 'is it safe to link my bank', 'family sharing')")] string query,
        [Description("Maximum number of results to return (default: 5, max: 10)")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: search_help called with query '{Query}'", query);

            if (string.IsNullOrWhiteSpace(query))
            {
                return JsonSerializer.Serialize(new { Error = "Query is required", Results = Array.Empty<object>() });
            }

            var hits = await _helpContentService.SearchAsync(query, limit, cancellationToken);

            var results = hits.Select(h => new
            {
                h.Title,
                h.Description,
                h.Url,
                RelevanceScore = Math.Round(h.Score * 100, 1)
            });

            return JsonSerializer.Serialize(new
            {
                Query = query,
                Results = results
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in search_help for query '{Query}'", query);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }

    [McpServerTool(Name = "get_help_article", ReadOnly = true)]
    [Description("Fetch the full text of a Buoy Money help article by its slug (from search_help), plus its public URL.")]
    public async Task<string> GetHelpArticleAsync(
        [Description("User ID (injected automatically; help is global and this is ignored)")] int userId,
        [Description("The article slug from search_help results (e.g., 'why-your-budget-should-count-up')")] string slug,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("MCP Tool: get_help_article called for slug '{Slug}'", slug);

            if (string.IsNullOrWhiteSpace(slug))
            {
                return JsonSerializer.Serialize(new { Error = "Slug is required" });
            }

            var article = await _helpContentService.GetArticleAsync(slug, cancellationToken);

            if (article == null)
            {
                return JsonSerializer.Serialize(new { Error = $"Help article not found: {slug}" });
            }

            return JsonSerializer.Serialize(new
            {
                article.Title,
                article.Url,
                article.Content
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in get_help_article for slug '{Slug}'", slug);
            return JsonSerializer.Serialize(new { Error = ex.Message });
        }
    }
}
