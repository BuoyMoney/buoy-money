using FluentAssertions;
using Xunit;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The point key is what collapses the vector index from one-per-transaction (which grew with
/// users x time, forever) to one-per-merchant-fact (which saturates). These tests pin that
/// collapse down, because a change here silently multiplies memory rather than breaking.
/// </summary>
public class TransactionEmbeddingKeyTests
{
  [Fact]
  public void Repeat_visits_to_one_merchant_collapse_to_a_single_point()
  {
    // Measured on prod: ~90 transactions/user/month against ~15 new merchants/user/month, so
    // roughly 6 in 7 vectors were restating a fact already stored.
    var first = TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21);
    var second = TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21);

    second.Should().Be(first);
  }

  [Theory]
  [InlineData("STARBUCKS #1234")]
  [InlineData("  starbucks  ")]
  [InlineData("Starbucks 00417")]
  public void Store_numbers_and_casing_do_not_create_separate_facts(string variant)
  {
    // Plaid decorates merchant names with store numbers and references. Without normalising,
    // every branch of the same chain would become its own vector.
    TransactionEmbeddingService.MerchantPointId(1, variant, 21)
      .Should().Be(TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21));
  }

  [Fact]
  public void Different_users_never_share_a_point()
  {
    // The index is per-user knowledge: one person filing Target under Home Improvement must
    // not overwrite another person filing it under Shopping.
    TransactionEmbeddingService.MerchantPointId(1, "Target", 22)
      .Should().NotBe(TransactionEmbeddingService.MerchantPointId(2, "Target", 22));
  }

  [Fact]
  public void Re_filing_a_merchant_under_a_new_category_is_a_new_fact()
  {
    // Both facts coexist; the consensus check decides between them rather than one silently
    // clobbering the other.
    TransactionEmbeddingService.MerchantPointId(1, "Target", 22)
      .Should().NotBe(TransactionEmbeddingService.MerchantPointId(1, "Target", 25));
  }

  [Fact]
  public void Distinct_merchants_produce_distinct_points()
  {
    TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21)
      .Should().NotBe(TransactionEmbeddingService.MerchantPointId(1, "Hy-Vee", 21));
  }

  [Fact]
  public void The_key_is_a_valid_uuid_qdrant_will_accept()
  {
    Guid.TryParse(TransactionEmbeddingService.MerchantPointId(1, "Starbucks", 21), out _)
      .Should().BeTrue();
  }

  [Theory]
  [InlineData(null, "")]
  [InlineData("", "")]
  [InlineData("   ", "")]
  [InlineData("HY-VEE #1620", "hyvee")]
  [InlineData("AMAZON MKTPLACE PMTS", "amazon mktplace pmts")]
  public void Normalisation_handles_the_shapes_Plaid_actually_sends(string? input, string expected)
  {
    TransactionEmbeddingService.NormalizeMerchant(input).Should().Be(expected);
  }
}
