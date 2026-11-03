using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The chat, transaction and help indexes talk to IVectorStore only. These tests pin what each one
/// stores, how it searches, and what it deletes, so the backing store (Qdrant or pgvector) can change
/// without changing behaviour.
/// </summary>
public class VectorStoreServicesTests
{
  /// <summary>Letter-count vectors: texts with similar letters are close, so search order is meaningful.</summary>
  private sealed class LetterEmbeddingClient : IEmbeddingClient
  {
    public bool IsEnabled => true;
    public string Model => "letters-v1";
    public int Dimensions => 26;

    public Task<float[]?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
      var v = new float[26];
      foreach (var c in text.ToLowerInvariant().Where(c => c >= 'a' && c <= 'z')) v[c - 'a']++;
      return Task.FromResult<float[]?>(v);
    }
  }

  private static IHostEnvironment Env(string name)
  {
    var env = new Mock<IHostEnvironment>();
    env.SetupGet(e => e.EnvironmentName).Returns(name);
    return env.Object;
  }

  private static ChatEmbeddingService Chat(FakeVectorStore store, string env = "Production") =>
    new(NullLogger<ChatEmbeddingService>.Instance, store, new LetterEmbeddingClient(), Env(env), TestGeminiUsage.Create());

  [Fact]
  public async Task Chat_stores_under_the_message_id_and_finds_only_the_callers_messages()
  {
    var store = new FakeVectorStore();
    var chat = Chat(store);

    await chat.StoreMessageEmbeddingAsync(1, 10, 100, "how much did I spend on groceries", "user", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    await chat.StoreMessageEmbeddingAsync(2, 20, 200, "how much did I spend on groceries", "user", DateTime.UtcNow);

    store.Count("chat_history").Should().Be(2);
    store.Get("chat_history", "100")!.Model.Should().Be("letters-v1");

    var hits = await chat.SearchChatHistoryAsync(1, "groceries spending", minScore: 0.5f);
    hits.Should().ContainSingle();
    hits[0].MessageId.Should().Be(100);
    hits[0].ConversationId.Should().Be(10);
    hits[0].Role.Should().Be("user");
    hits[0].Content.Should().Be("how much did I spend on groceries");
    hits[0].Score.Should().BeInRange(0.5f, 1.0001f);
  }

  [Fact]
  public async Task Chat_skips_short_messages_and_deletes_by_user_and_by_conversation()
  {
    var store = new FakeVectorStore();
    var chat = Chat(store);

    await chat.StoreMessageEmbeddingAsync(1, 10, 1, "short", "user", DateTime.UtcNow);
    store.Count("chat_history").Should().Be(0);

    await chat.StoreMessageEmbeddingAsync(1, 10, 2, "a message that is long enough", "user", DateTime.UtcNow);
    await chat.StoreMessageEmbeddingAsync(1, 11, 3, "another message that is long enough", "user", DateTime.UtcNow);
    await chat.StoreMessageEmbeddingAsync(2, 12, 4, "a different persons message that is long", "user", DateTime.UtcNow);

    await chat.DeleteConversationEmbeddingsAsync(10);
    store.Count("chat_history").Should().Be(2);

    await chat.DeleteUserEmbeddingsAsync(1);
    store.Count("chat_history").Should().Be(1);
    store.Get("chat_history", "4").Should().NotBeNull();
  }

  [Theory]
  [InlineData("Production", "chat_history")]
  [InlineData("Staging", "chat_history_staging")]
  [InlineData("Development", "chat_history_dev")]
  [InlineData("Testing", "chat_history_testing")]
  public async Task Chat_collection_name_depends_on_the_environment(string env, string collection)
  {
    var store = new FakeVectorStore();
    await Chat(store, env).StoreMessageEmbeddingAsync(1, 1, 1, "a message that is long enough", "user", DateTime.UtcNow);

    store.Count(collection).Should().Be(1);
  }

  [Fact]
  public async Task Chat_with_the_store_off_does_nothing()
  {
    var store = new FakeVectorStore(enabled: false);
    var chat = Chat(store);

    await chat.StoreMessageEmbeddingAsync(1, 1, 1, "a message that is long enough", "user", DateTime.UtcNow);
    (await chat.SearchChatHistoryAsync(1, "anything")).Should().BeEmpty();
    store.Count("chat_history").Should().Be(0);
  }

  private static TransactionEmbeddingService Txn(FakeVectorStore store, bool consent = true)
  {
    var users = new Mock<IUserRepository>();
    users.Setup(u => u.GetByIdAsync(It.IsAny<int>())).ReturnsAsync(consent
      ? new User { AiConsentGrantedAt = DateTime.UtcNow.AddDays(-1), AiConsentVersion = AiConsent.CurrentVersion }
      : new User());
    return new TransactionEmbeddingService(
      NullLogger<TransactionEmbeddingService>.Instance, store, new LetterEmbeddingClient(), Env("Production"),
      new BuoyAiMetrics(new TestMeterFactory()), users.Object);
  }

  private sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
  {
    public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);
    public void Dispose() { }
  }

  [Fact]
  public async Task Transactions_upsert_one_fact_per_merchant_and_category_and_find_it_again()
  {
    var store = new FakeVectorStore();
    var txn = Txn(store);
    var starbucks = new Transaction { Id = 1, MerchantName = "STARBUCKS #1234", Amount = 5m };
    var again = new Transaction { Id = 2, MerchantName = "Starbucks", Amount = 6m };

    await txn.StoreTransactionEmbeddingAsync(1, starbucks, 21, "Dining", "coffee");
    await txn.StoreTransactionEmbeddingAsync(1, again, 21, "Dining", "coffee");

    store.Count("transaction_categories").Should().Be(1, "the merchant is normalised into one fact");
    var key = TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21);
    store.Get("transaction_categories", key).Should().NotBeNull();

    var similar = await txn.FindSimilarCategorizedTransactionsAsync(1, new Transaction { MerchantName = "Starbucks", Amount = 4m }, minScore: 0.5f);
    similar.Should().ContainSingle();
    similar[0].CategoryId.Should().Be(21);
    similar[0].CategoryName.Should().Be("Dining");
    similar[0].MerchantName.Should().Be("Starbucks");
    similar[0].SemanticTags.Should().Be("coffee");

    (await txn.FindSimilarCategorizedTransactionsAsync(2, starbucks)).Should().BeEmpty("another user's facts are never searched");
  }

  [Fact]
  public async Task Transactions_without_AI_consent_store_and_find_nothing()
  {
    var store = new FakeVectorStore();
    var txn = Txn(store, consent: false);
    var tx = new Transaction { Id = 1, MerchantName = "Target", Amount = 5m };

    await txn.StoreTransactionEmbeddingAsync(1, tx, 22, "Shopping", null);
    (await txn.FindSimilarCategorizedTransactionsAsync(1, tx)).Should().BeEmpty();

    store.Count("transaction_categories").Should().Be(0);
  }

  [Fact]
  public async Task Transactions_delete_by_user_and_deleting_one_transaction_keeps_the_merchant_fact()
  {
    var store = new FakeVectorStore();
    var txn = Txn(store);
    await txn.StoreTransactionEmbeddingAsync(1, new Transaction { Id = 7, MerchantName = "Target", Amount = 5m }, 22, "Shopping", null);
    await txn.StoreTransactionEmbeddingAsync(2, new Transaction { Id = 8, MerchantName = "Target", Amount = 5m }, 22, "Shopping", null);

    await txn.DeleteTransactionEmbeddingAsync(7);
    store.Count("transaction_categories").Should().Be(2);

    await txn.DeleteUserEmbeddingsAsync(1);
    store.Count("transaction_categories").Should().Be(1);
  }

  [Fact]
  public async Task Help_indexes_the_published_articles_and_searches_them()
  {
    var store = new FakeVectorStore();
    var help = new HelpContentService(
      NullLogger<HelpContentService>.Instance, store, new LetterEmbeddingClient(), Env("Production"), TestGeminiUsage.Create());

    await help.EnsureCollectionAsync();
    await help.IndexPublishedArticlesAsync();

    store.Count("help_articles").Should().BeGreaterThan(0);
    // Idempotent: a second boot upserts the same keys.
    var count = store.Count("help_articles");
    await help.IndexPublishedArticlesAsync();
    store.Count("help_articles").Should().Be(count);

    var hits = await help.SearchAsync("why your budget should count up", limit: 3);
    hits.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(3);
    hits[0].Url.Should().StartWith("https://buoy-ai.com/");
    hits.Select(h => h.Score).Should().BeInDescendingOrder();
  }
}
