using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// With no Gemini key the AI services must construct and skip their work. Before the embedding
/// seam, ChatEmbeddingService threw from its constructor and AIService depends on it, so the
/// whole app failed to resolve IAIService.
/// </summary>
public class AiOffTests
{
  private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

  private static IHostEnvironment Env()
  {
    var env = new Mock<IHostEnvironment>();
    env.SetupGet(e => e.EnvironmentName).Returns("Development");
    return env.Object;
  }

  // An in-memory store.
  private static IVectorStore Store() => new FakeVectorStore();

  [Fact]
  public void ChatEmbeddingService_constructs_without_a_key()
  {
    var act = () => new ChatEmbeddingService(
      NullLogger<ChatEmbeddingService>.Instance, new FakeVectorStore(enabled: false), new StubEmbeddingClient(enabled: false),
      Env(), TestGeminiUsage.Create());

    act.Should().NotThrow();
  }

  [Fact]
  public async Task ChatEmbeddingService_skips_store_and_search_when_the_client_is_disabled()
  {
    var client = new StubEmbeddingClient(enabled: false);
    var service = new ChatEmbeddingService(
      NullLogger<ChatEmbeddingService>.Instance, Store(), client, Env(), TestGeminiUsage.Create());

    await service.StoreMessageEmbeddingAsync(1, 1, 1, "a message long enough to embed", "user", DateTime.UtcNow);
    var hits = await service.SearchChatHistoryAsync(1, "anything");

    hits.Should().BeEmpty();
    client.CallCount.Should().Be(0);
  }

  [Fact]
  public async Task HelpContentService_skips_search_when_the_client_is_disabled()
  {
    var client = new StubEmbeddingClient(enabled: false);
    var service = new HelpContentService(
      NullLogger<HelpContentService>.Instance, Store(), client, Env(), TestGeminiUsage.Create());

    (await service.SearchAsync("budget")).Should().BeEmpty();
    client.CallCount.Should().Be(0);
  }

  [Fact]
  public async Task TransactionEmbeddingService_skips_store_and_find_when_the_client_is_disabled()
  {
    var client = new StubEmbeddingClient(enabled: false);
    var users = new Mock<IUserRepository>(MockBehavior.Strict); // consent lookup must not be reached
    var metrics = new BuoyAiMetrics(new TestMeterFactory());
    var service = new TransactionEmbeddingService(
      NullLogger<TransactionEmbeddingService>.Instance, Store(), client, Env(), metrics, users.Object);
    var tx = new Transaction { Id = 1, MerchantName = "Starbucks", Amount = 5m };

    await service.StoreTransactionEmbeddingAsync(1, tx, 21, "Dining", null);
    var similar = await service.FindSimilarCategorizedTransactionsAsync(1, tx);

    similar.Should().BeEmpty();
    client.CallCount.Should().Be(0);
  }

  [Fact]
  public void AIService_constructs_with_no_key_and_a_disabled_embedding_stack()
  {
    var config = EmptyConfig();
    var chatEmbeddings = new ChatEmbeddingService(
      NullLogger<ChatEmbeddingService>.Instance, new FakeVectorStore(enabled: false), new StubEmbeddingClient(), Env(), TestGeminiUsage.Create());

    var act = () => new AIService(
      config,
      Mock.Of<IBudgetService>(), Mock.Of<ITransactionService>(), Mock.Of<ISavingsService>(),
      Mock.Of<IAIUsageTracker>(), Mock.Of<IAIRefreshTracker>(), Mock.Of<IEntitlementService>(),
      NullLogger<AIService>.Instance, Mock.Of<IAIInsightRepository>(), Mock.Of<IOnboardingQuizService>(),
      Mock.Of<BuoyAI.Infrastructure.MCP.IMcpToolExecutor>(), Mock.Of<IChatToolRouter>(), Mock.Of<IChatPromptCache>(),
      Mock.Of<IUserRepository>(), Mock.Of<IAIInsightGenerationService>(), Mock.Of<IAiConversationService>(),
      chatEmbeddings, Mock.Of<IAiMemoryService>(), Mock.Of<IAiToolSecurityService>(),
      new AIModelConfiguration(config), Mock.Of<IGeminiApiClient>());

    act.Should().NotThrow();
  }

  [Fact]
  public async Task GenerateTextAsync_delegates_to_the_gemini_api_client()
  {
    var config = EmptyConfig();
    var model = new AIModelConfiguration(config);
    var client = new Mock<IGeminiApiClient>();
    client.Setup(c => c.GenerateContentAsync("draft a note", model.GetPrimaryModel(BuoyAI.Core.Enums.SubscriptionTier.Admin)))
      .ReturnsAsync("drafted");
    var service = new AIService(
      config,
      Mock.Of<IBudgetService>(), Mock.Of<ITransactionService>(), Mock.Of<ISavingsService>(),
      Mock.Of<IAIUsageTracker>(), Mock.Of<IAIRefreshTracker>(), Mock.Of<IEntitlementService>(),
      NullLogger<AIService>.Instance, Mock.Of<IAIInsightRepository>(), Mock.Of<IOnboardingQuizService>(),
      Mock.Of<BuoyAI.Infrastructure.MCP.IMcpToolExecutor>(), Mock.Of<IChatToolRouter>(), Mock.Of<IChatPromptCache>(),
      Mock.Of<IUserRepository>(), Mock.Of<IAIInsightGenerationService>(), Mock.Of<IAiConversationService>(),
      Mock.Of<IChatEmbeddingService>(), Mock.Of<IAiMemoryService>(), Mock.Of<IAiToolSecurityService>(),
      model, client.Object);

    (await service.GenerateTextAsync("draft a note")).Should().Be("drafted");
  }

  private sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
  {
    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
    public void Dispose() { }
  }
}
