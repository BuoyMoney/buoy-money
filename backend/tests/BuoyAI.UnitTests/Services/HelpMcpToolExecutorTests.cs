using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.MCP;
using BuoyAI.Infrastructure.MCP.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// McpToolExecutor-level tests for the read-only help tools (search_help / get_help_article).
/// Mirrors McpToolExecutorTests: mocks IHelpContentService, wires a DI container, and drives the
/// tools through the executor's discovery + execution path.
/// </summary>
public class HelpMcpToolExecutorTests : IDisposable
{
  private readonly Mock<IHelpContentService> _mockHelp;
  private readonly Mock<ILogger<HelpReadTools>> _mockHelpLogger;
  private readonly Mock<ILogger<McpToolExecutor>> _mockLogger;
  private readonly IServiceProvider _serviceProvider;
  private readonly McpToolExecutor _executor;

  private const int TestUserId = 1;

  public HelpMcpToolExecutorTests()
  {
    _mockHelp = new Mock<IHelpContentService>();
    _mockHelpLogger = new Mock<ILogger<HelpReadTools>>();
    _mockLogger = new Mock<ILogger<McpToolExecutor>>();

    var services = new ServiceCollection();
    services.AddSingleton(_mockHelp.Object);
    services.AddSingleton(_mockHelpLogger.Object);
    // The write-tool gate resolves IFamilyService, but help tools are ReadOnly so it isn't hit.

    _serviceProvider = services.BuildServiceProvider();
    _executor = new McpToolExecutor(_serviceProvider, _mockLogger.Object);
  }

  public void Dispose() => GC.SuppressFinalize(this);

  [Fact]
  public void GetAvailableTools_IncludesHelpTools_AsReadOnly()
  {
    var tools = _executor.GetAvailableTools();

    var search = tools.FirstOrDefault(t => t.Name == "search_help");
    search.Should().NotBeNull();
    search!.ReadOnly.Should().BeTrue();

    var get = tools.FirstOrDefault(t => t.Name == "get_help_article");
    get.Should().NotBeNull();
    get!.ReadOnly.Should().BeTrue();
  }

  [Fact]
  public void GeminiDeclarations_HelpTools_DoNotAdvertiseUserId()
  {
    var declarations = _executor.GetGeminiFunctionDeclarations(readOnlyOnly: true);

    var search = declarations.FirstOrDefault(d => d.Name == "search_help");
    search.Should().NotBeNull();

    // userId is force-injected server-side and must not be advertised to the model.
    var json = JsonSerializer.Serialize(search);
    json.Should().NotContain("userId");
    json.Should().Contain("query");
  }

  [Fact]
  public async Task ExecuteSearchHelp_ReturnsResultsIncludingUrl()
  {
    var hits = new List<HelpArticleHit>
    {
      new()
      {
        Slug = "why-your-budget-should-count-up",
        Title = "Why your budget should count up",
        Description = "Count-up budgeting explained.",
        Url = "https://buoy-ai.com/blog/why-your-budget-should-count-up",
        Score = 0.91f
      },
      new()
      {
        Slug = "is-it-safe-to-link-your-bank",
        Title = "Is it safe to link your bank?",
        Description = "How Plaid keeps your data safe.",
        Url = "https://buoy-ai.com/blog/is-it-safe-to-link-your-bank",
        Score = 0.82f
      }
    };
    _mockHelp
      .Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(hits);

    var result = await _executor.ExecuteToolAsync(
      TestUserId,
      "search_help",
      new Dictionary<string, object> { { "query", "how does count-up work" } });

    result.Should().Contain("Why your budget should count up");
    result.Should().Contain("Is it safe to link your bank");
    // The public URL must be surfaced so the agent can link the user.
    result.Should().Contain("https://buoy-ai.com/blog/why-your-budget-should-count-up");
  }

  [Fact]
  public async Task ExecuteGetHelpArticle_Found_ReturnsTitleUrlAndContent()
  {
    _mockHelp
      .Setup(s => s.GetArticleAsync("why-your-budget-should-count-up", It.IsAny<CancellationToken>()))
      .ReturnsAsync(new HelpArticle
      {
        Slug = "why-your-budget-should-count-up",
        Title = "Why your budget should count up",
        Url = "https://buoy-ai.com/blog/why-your-budget-should-count-up",
        Content = "Full plain text of the article."
      });

    var result = await _executor.ExecuteToolAsync(
      TestUserId,
      "get_help_article",
      new Dictionary<string, object> { { "slug", "why-your-budget-should-count-up" } });

    result.Should().Contain("Why your budget should count up");
    result.Should().Contain("https://buoy-ai.com/blog/why-your-budget-should-count-up");
    result.Should().Contain("Full plain text of the article.");
  }

  [Fact]
  public async Task ExecuteGetHelpArticle_NotFound_ReturnsError()
  {
    _mockHelp
      .Setup(s => s.GetArticleAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync((HelpArticle?)null);

    var result = await _executor.ExecuteToolAsync(
      TestUserId,
      "get_help_article",
      new Dictionary<string, object> { { "slug", "missing" } });

    result.Should().Contain("Error");
  }
}
