using BuoyAI.Infrastructure;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Qdrant.Client.Grpc;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The pieces of the hosted Qdrant adapter that build or read wire values. They need no server,
/// so they run everywhere; the store as a whole is exercised by the embedding service tests.
/// </summary>
public class QdrantVectorStoreTests
{
  [Fact]
  public void Payload_values_keep_ints_and_strings_and_stringify_the_rest()
  {
    QdrantVectorStore.ToPayloadValue(7).IntegerValue.Should().Be(7);
    QdrantVectorStore.ToPayloadValue(7L).IntegerValue.Should().Be(7);
    QdrantVectorStore.ToPayloadValue("coffee").StringValue.Should().Be("coffee");
    QdrantVectorStore.ToPayloadValue(1.5m).StringValue.Should().Be("1.5");
  }

  [Fact]
  public void Payload_read_back_returns_text_for_int_and_string_and_empty_otherwise()
  {
    QdrantVectorStore.FromPayloadValue(new Value { IntegerValue = 42 }).Should().Be("42");
    QdrantVectorStore.FromPayloadValue(new Value { StringValue = "x" }).Should().Be("x");
    QdrantVectorStore.FromPayloadValue(new Value { DoubleValue = 1.5 }).Should().BeEmpty();
    QdrantVectorStore.FromPayloadValue(new Value { BoolValue = true }).Should().BeEmpty();
  }

  [Fact]
  public void Keys_round_trip_through_numeric_and_uuid_point_ids()
  {
    QdrantVectorStore.ToKey(QdrantVectorStore.ToPointId("12345")).Should().Be("12345");
    var guid = Guid.NewGuid().ToString();
    QdrantVectorStore.ToKey(QdrantVectorStore.ToPointId(guid)).Should().Be(guid);
  }

  [Fact]
  public void Integer_filter_matches_one_field_exactly()
  {
    var filter = QdrantVectorStore.IntegerFilter("user_id", 9);

    var condition = filter.Must.Should().ContainSingle().Subject;
    condition.Field.Key.Should().Be("user_id");
    condition.Field.Match.Integer.Should().Be(9);
  }
}

public class ConnectionPoolCapTests
{
  private static IConfiguration Config(int? max = null) =>
    new ConfigurationBuilder().AddInMemoryCollection(
      max is null ? [] : new Dictionary<string, string?> { ["Database:MaxPoolSize"] = max.ToString() }).Build();

  [Fact]
  public void Caps_the_pool_at_forty_by_default()
  {
    DependencyInjection.CapPoolSize("Host=h;Database=d", Config())
      .Should().Contain("Maximum Pool Size=40");
  }

  [Fact]
  public void Honours_the_configured_size_and_leaves_an_explicit_size_alone()
  {
    DependencyInjection.CapPoolSize("Host=h;Database=d", Config(25)).Should().Contain("Maximum Pool Size=25");
    DependencyInjection.CapPoolSize("Host=h;Maximum Pool Size=7", Config(25)).Should().Be("Host=h;Maximum Pool Size=7");
    DependencyInjection.CapPoolSize(null, Config()).Should().BeNull();
  }
}
