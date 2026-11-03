using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>VectorStore:Provider picks the store. Hosted leaves it unset and must keep Qdrant.</summary>
public class VectorStoreSelectionTests
{
  private static IVectorStore Resolve(Dictionary<string, string?> settings)
  {
    var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    var env = new Mock<IHostEnvironment>();
    env.Setup(e => e.EnvironmentName).Returns("Development");
    var dbName = Guid.NewGuid().ToString();

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddMemoryCache();
    services.AddHttpClient();
    services.AddHttpContextAccessor();
    services.AddSingleton<IConfiguration>(config);
    services.AddSingleton(env.Object);
    services.AddInfrastructure(config, skipDbContext: true);
    services.AddDbContext<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName));
    services.AddDbContextFactory<BuoyAIDbContext>(o => o.UseInMemoryDatabase(dbName), ServiceLifetime.Scoped);
    return services.BuildServiceProvider().GetRequiredService<IVectorStore>();
  }

  [Fact]
  public void Unset_provider_keeps_qdrant_and_is_off_without_a_qdrant_client()
  {
    var store = Resolve([]);

    store.Should().BeOfType<QdrantVectorStore>();
    store.IsEnabled.Should().BeFalse("no QdrantClient is registered without a qdrant connection string");
  }

  [Fact]
  public void Qdrant_provider_is_case_insensitive_and_ignores_a_database_connection()
  {
    Resolve(new() { ["VectorStore:Provider"] = " Qdrant ", ["ConnectionStrings:BuoyAI"] = "Host=x" })
      .Should().BeOfType<QdrantVectorStore>();
  }

  [Fact]
  public void Pgvector_provider_uses_the_application_database()
  {
    var store = Resolve(new() { ["VectorStore:Provider"] = "pgvector", ["ConnectionStrings:BuoyAI"] = "Host=localhost;Database=x" });

    store.Should().BeOfType<PgVectorStore>();
    store.IsEnabled.Should().BeTrue();
  }

  [Theory]
  [InlineData("pg-vector")]
  [InlineData("qdrnt")]
  public void Unknown_provider_is_rejected(string value)
  {
    var act = () => Resolve(new() { ["VectorStore:Provider"] = value });

    act.Should().Throw<InvalidOperationException>();
  }

  [Fact]
  public void Pgvector_provider_without_a_connection_string_is_off()
  {
    Resolve(new() { ["VectorStore:Provider"] = "pgvector" }).IsEnabled.Should().BeFalse();
  }

  [Fact]
  public void Qdrant_point_ids_keep_the_ids_hosted_data_already_uses()
  {
    // Chat messages and help slugs were numeric point ids; merchant facts were UUID point ids.
    QdrantVectorStore.ToPointId("12345").Num.Should().Be(12345UL);
    var guid = TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21);
    QdrantVectorStore.ToPointId(guid).Uuid.Should().Be(guid);
    QdrantVectorStore.ToPointId(HelpContentService.StableId("count-up").ToString()).Num
      .Should().Be(HelpContentService.StableId("count-up"));
  }
}
