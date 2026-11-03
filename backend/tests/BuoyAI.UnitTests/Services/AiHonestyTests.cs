using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Services.Ai;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// With no Gemini key, or when a call fails, the app says AI is off or failed. It never
/// substitutes invented insights (the old mock fallback told users their grocery spending was 15% over budget).
/// </summary>
public class AiHonestyTests
{
  private static GeminiApiClient ClientWithoutKey()
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
    return new GeminiApiClient(new GeminiNativeProvider(config, NullLogger<GeminiNativeProvider>.Instance, TestGeminiUsage.Create()));
  }

  [Fact]
  public void GeminiApiClient_WithoutKey_ReportsNotConfigured()
  {
    // Environment keys may exist on a dev machine; only assert consistency with the resolver.
    var config = new ConfigurationBuilder().Build();
    var client = new GeminiApiClient(new GeminiNativeProvider(config, NullLogger<GeminiNativeProvider>.Instance, TestGeminiUsage.Create()));

    client.IsConfigured.Should().Be(GeminiApiKey.IsConfigured(config));
  }

  [Fact]
  public async Task GeminiApiClient_WithoutKey_ThrowsInsteadOfReturningMockInsights()
  {
    if (GeminiApiKey.IsConfigured(new ConfigurationBuilder().Build())) return; // a real key is in the environment

    var client = ClientWithoutKey();

    var act = () => client.GenerateContentAsync("dashboard insights please", "gemini-test");

    await act.Should().ThrowAsync<AiUnavailableException>();
  }

  [Fact]
  public async Task GeminiApiClient_WithFilesAndNoKey_Throws()
  {
    if (GeminiApiKey.IsConfigured(new ConfigurationBuilder().Build())) return;

    var client = ClientWithoutKey();

    var act = () => client.GenerateContentAsync("p", "m", new List<(string, byte[])> { ("image/png", new byte[] { 1 }) });

    await act.Should().ThrowAsync<AiUnavailableException>();
  }

  [Fact]
  public void AiUnavailableException_IsAnInvalidOperation_WithAHonestMessage()
  {
    var ex = new AiUnavailableException();

    ex.Should().BeAssignableTo<InvalidOperationException>();
    ex.Message.Should().Contain("not available").And.Contain("API key");
  }

  private static (AIInsightGenerationService Service, Mock<IAIInsightRepository> Repo, Mock<IGeminiApiClient> Gemini) InsightService(bool configured)
  {
    var repo = new Mock<IAIInsightRepository>();
    var gemini = new Mock<IGeminiApiClient>();
    gemini.SetupGet(g => g.IsConfigured).Returns(configured);
    var entitlements = new Mock<IEntitlementService>();
    entitlements.Setup(e => e.GetTierOrStandardAsync(It.IsAny<int>())).ReturnsAsync(SubscriptionTier.Premium);
    var usage = new Mock<IAIUsageTracker>();
    usage.Setup(u => u.CanMakeRequestAsync(It.IsAny<int>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);
    var refresh = new Mock<IAIRefreshTracker>();
    refresh.Setup(r => r.CanRefreshAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<SubscriptionTier>())).ReturnsAsync(true);
    repo.Setup(r => r.GetRecentInsightsForUserAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<bool>()))
      .ReturnsAsync(new List<BuoyAI.Core.Entities.AIInsight>());
    repo.Setup(r => r.GetInsightsForUserAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int>()))
      .ReturnsAsync(new List<BuoyAI.Core.Entities.AIInsight>());
    var config = new ConfigurationBuilder().Build();
    var service = new AIInsightGenerationService(
      Mock.Of<IBudgetService>(), Mock.Of<ITransactionService>(), new Lazy<ISavingsService>(() => Mock.Of<ISavingsService>()),
      usage.Object, refresh.Object, entitlements.Object, repo.Object, Mock.Of<IOnboardingQuizService>(),
      NullLogger<AIInsightGenerationService>.Instance, new AIModelConfiguration(config), gemini.Object,
      Mock.Of<IAiInsightQueue>(), Mock.Of<BuoyAI.Infrastructure.MCP.IMcpToolExecutor>(), Mock.Of<IGeminiAgenticRunner>(),
      new Lazy<IGamificationService>(() => Mock.Of<IGamificationService>()), config);
    return (service, repo, gemini);
  }

  [Theory]
  [InlineData("dashboard")]
  [InlineData("budget")]
  [InlineData("savings")]
  public async Task Insights_WithAiOff_AreEmpty_AndNothingIsSaved(string context)
  {
    var (service, repo, _) = InsightService(configured: false);

    var generated = await service.GenerateContextualInsightsAsync(1, new AIInsightRequest { Context = context, MaxInsights = 5 });
    var refreshed = await service.RefreshContextualInsightsAsync(1, context);

    generated.Should().BeEmpty();
    refreshed.Should().BeEmpty();
    repo.Verify(r => r.CreateInsightAsync(It.IsAny<BuoyAI.Core.Entities.AIInsight>()), Times.Never);
    repo.Verify(r => r.ArchiveInsightsAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
  }

  [Theory]
  [InlineData("dashboard")]
  [InlineData("budget")]
  [InlineData("savings")]
  public async Task Insights_WhenTheGeminiCallFails_AreEmpty_NotCannedText(string context)
  {
    var (service, repo, gemini) = InsightService(configured: true);
    gemini.Setup(g => g.GenerateContentAsync(It.IsAny<string>(), It.IsAny<string>()))
      .ThrowsAsync(new HttpRequestException("boom"));

    var generated = await service.GenerateContextualInsightsAsync(1, new AIInsightRequest { Context = context, MaxInsights = 5 });

    generated.Should().BeEmpty();
    repo.Verify(r => r.CreateInsightAsync(It.IsAny<BuoyAI.Core.Entities.AIInsight>()), Times.Never);
  }
}
