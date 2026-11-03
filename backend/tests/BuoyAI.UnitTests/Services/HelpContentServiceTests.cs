using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Unit tests for HelpContentService's frontmatter/draft parsing, slug->URL derivation, and
/// stable id hashing. Also covers embedded-resource reading via GetArticleAsync (no vector store /
/// live embeddings involved — the vector store is off so the service degrades but resource reads
/// still work).
/// </summary>
public class HelpContentServiceTests
{
  // ----- ParseArticle / frontmatter -----

  [Fact]
  public void ParseArticle_DraftTrue_IsExcludedAsDraft()
  {
    var raw = "---\ntitle: \"A draft\"\ndescription: \"nope\"\ndraft: true\n---\n\nBody text here.";

    var parsed = HelpContentService.ParseArticle("a-draft", raw);

    parsed.Draft.Should().BeTrue();
  }

  [Fact]
  public void ParseArticle_NoDraftField_DefaultsToPublished()
  {
    var raw = "---\ntitle: \"Published\"\ndescription: \"yep\"\n---\n\nBody text here.";

    var parsed = HelpContentService.ParseArticle("published", raw);

    parsed.Draft.Should().BeFalse();
  }

  [Fact]
  public void ParseArticle_DraftFalse_IsPublished()
  {
    var raw = "---\ntitle: \"Published\"\ndescription: \"yep\"\ndraft: false\n---\n\nBody.";

    var parsed = HelpContentService.ParseArticle("published", raw);

    parsed.Draft.Should().BeFalse();
  }

  [Fact]
  public void ParseArticle_ExtractsTitleAndDescription_StrippingQuotes()
  {
    var raw = "---\ntitle: \"My Title\"\ndescription: \"My description\"\n---\n\nBody.";

    var parsed = HelpContentService.ParseArticle("slug", raw);

    parsed.Title.Should().Be("My Title");
    parsed.Description.Should().Be("My description");
  }

  [Fact]
  public void ParseArticle_ReducesMarkdownBodyToPlainText()
  {
    var raw = "---\ntitle: \"T\"\ndescription: \"D\"\n---\n\n## A heading\n\nSome **bold** text and a [link](/app/register).";

    var parsed = HelpContentService.ParseArticle("slug", raw);

    parsed.PlainTextBody.Should().Contain("A heading");
    parsed.PlainTextBody.Should().Contain("bold");
    parsed.PlainTextBody.Should().Contain("link");
    // Markdown syntax should be stripped.
    parsed.PlainTextBody.Should().NotContain("##");
    parsed.PlainTextBody.Should().NotContain("**");
    parsed.PlainTextBody.Should().NotContain("](");
  }

  [Fact]
  public void ParseArticle_NoFrontmatter_TreatsWholeFileAsBody()
  {
    var raw = "Just some body with no frontmatter.";

    var parsed = HelpContentService.ParseArticle("slug", raw);

    parsed.Title.Should().BeEmpty();
    parsed.Draft.Should().BeFalse();
    parsed.PlainTextBody.Should().Contain("Just some body");
  }

  // ----- slug -> URL -----

  [Fact]
  public void UrlForSlug_BuildsPublicBlogUrl()
  {
    HelpContentService.UrlForSlug("why-your-budget-should-count-up")
      .Should().Be("https://buoy-ai.com/blog/why-your-budget-should-count-up");
  }

  [Fact]
  public void UrlForSlug_DocsPage_MapsBackToItsDocsPath()
  {
    // scripts/sync-help.sh flattens docs/budgets/count-up-budgeting.md to this name.
    HelpContentService.UrlForSlug("docs-budgets-count-up-budgeting")
      .Should().Be("https://buoy-ai.com/docs/budgets/count-up-budgeting");
  }

  [Fact]
  public void UrlForSlug_DocsPageWithOneWordName_MapsBackToItsDocsPath()
  {
    HelpContentService.UrlForSlug("docs-account-security")
      .Should().Be("https://buoy-ai.com/docs/account/security");
  }

  [Fact]
  public void ParseArticle_StarlightDocs_ReadsTitleWithoutPubDateAndDropsDirectiveFences()
  {
    var raw = "---\ntitle: Family sharing\ndescription: Share a budget.\nsidebar:\n  order: 1\n---\n\n"
      + "<span class=\"plan\">Premium</span> only.\n\n:::caution[Worth knowing before you invite]\n"
      + "Every member sees all family transactions.\n:::\n";

    var parsed = HelpContentService.ParseArticle("docs-family-family-sharing", raw);

    parsed.Title.Should().Be("Family sharing");
    parsed.Description.Should().Be("Share a budget.");
    parsed.Draft.Should().BeFalse();
    parsed.Url.Should().Be("https://buoy-ai.com/docs/family/family-sharing");
    parsed.PlainTextBody.Should().Contain("Premium only.");
    parsed.PlainTextBody.Should().Contain("Worth knowing before you invite");
    parsed.PlainTextBody.Should().Contain("Every member sees all family transactions.");
    parsed.PlainTextBody.Should().NotContain(":::");
    parsed.PlainTextBody.Should().NotContain("<span");
  }

  [Fact]
  public void ParseArticle_SetsUrlFromSlug()
  {
    var parsed = HelpContentService.ParseArticle("my-slug", "---\ntitle: \"T\"\n---\nBody.");

    parsed.Url.Should().Be("https://buoy-ai.com/blog/my-slug");
  }

  // ----- stable id -----

  [Fact]
  public void StableId_IsDeterministic_ForSameSlug()
  {
    HelpContentService.StableId("count-up").Should().Be(HelpContentService.StableId("count-up"));
  }

  [Fact]
  public void StableId_DiffersForDifferentSlugs()
  {
    HelpContentService.StableId("count-up").Should().NotBe(HelpContentService.StableId("link-your-bank"));
  }

  [Fact]
  public void StableId_IsPositive_63Bit()
  {
    var id = HelpContentService.StableId("some-slug");
    id.Should().BeLessThanOrEqualTo(long.MaxValue);
  }

  // ----- GetArticleAsync against embedded resources (no Qdrant needed) -----

  private static HelpContentService BuildService()
  {
    var logger = new Mock<ILogger<HelpContentService>>();
    var config = new ConfigurationBuilder().Build();
    var env = new Mock<IHostEnvironment>();
    env.SetupGet(e => e.EnvironmentName).Returns("Development");
    // Vector store off -> service degrades; embedded-resource reads still work.
    // Real metrics/reporter rather than mocks: both are concrete singletons with no
    // external dependencies, and these tests never reach a Gemini call anyway.
    var metrics = new BuoyAiMetrics(new DummyMeterFactory());
    var geminiUsage = new GeminiUsageReporter(
        metrics, new Mock<ILogger<GeminiUsageReporter>>().Object);
    return new HelpContentService(logger.Object, new FakeVectorStore(enabled: false), new StubEmbeddingClient(), env.Object, geminiUsage);
  }

  [Fact]
  public async Task GetArticleAsync_PublishedSlug_ReturnsArticleWithUrl()
  {
    var service = BuildService();

    var article = await service.GetArticleAsync("why-your-budget-should-count-up", CancellationToken.None);

    article.Should().NotBeNull();
    article!.Title.Should().NotBeNullOrEmpty();
    article.Url.Should().Be("https://buoy-ai.com/blog/why-your-budget-should-count-up");
    article.Content.Should().NotBeNullOrEmpty();
  }

  // (A GetArticleAsync draft test used to live here. It named a post that wasn't embedded at all,
  // so it passed on "unknown slug", not on "draft"; the post is published now. Draft filtering is
  // covered by the ParseArticle_Draft* tests above.)

  [Fact]
  public async Task GetArticleAsync_EmbeddedDocsPage_ReturnsItWithItsDocsUrl()
  {
    var service = BuildService();

    // Synced from marketing/src/content/docs/docs/family/family-sharing.md by scripts/sync-help.sh.
    var article = await service.GetArticleAsync("docs-family-family-sharing", CancellationToken.None);

    article.Should().NotBeNull();
    article!.Title.Should().Be("Family sharing");
    article.Url.Should().Be("https://buoy-ai.com/docs/family/family-sharing");
    article.Content.Should().NotContain(":::");
  }

  [Fact]
  public async Task GetArticleAsync_UnknownSlug_ReturnsNull()
  {
    var service = BuildService();

    var article = await service.GetArticleAsync("does-not-exist", CancellationToken.None);

    article.Should().BeNull();
  }
}

/// <summary>Minimal IMeterFactory so BuoyAiMetrics can be constructed in tests.</summary>
internal sealed class DummyMeterFactory : IMeterFactory
{
  public Meter Create(MeterOptions options) => new(options);
  public void Dispose() { }
}
