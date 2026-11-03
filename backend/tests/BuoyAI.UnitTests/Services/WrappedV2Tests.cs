using Xunit;
using FluentAssertions;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class WrappedV2Tests
{
  private static Transaction Tx(decimal amount, TransactionCategory cat, DateTime date,
      string merchant = "Shop", TransactionType type = TransactionType.Debit)
  {
    return new Transaction
    {
      AccountId = 1,
      PlaidTransactionId = Guid.NewGuid().ToString("N"),
      Amount = Math.Abs(amount),
      Type = type,
      MerchantName = merchant,
      Description = merchant,
      CategoryId = (int)cat,
      Date = date,
    };
  }

  private static List<Transaction> SampleMonth()
  {
    var txns = new List<Transaction>
    {
      // Weekend-heavy dining (Aug 1 2026 is a Saturday).
      Tx(120m, TransactionCategory.DiningOut, new DateTime(2026, 8, 1), "Saltline"),
      Tx(90m, TransactionCategory.DiningOut, new DateTime(2026, 8, 2), "Saltline"),
      Tx(60m, TransactionCategory.Groceries, new DateTime(2026, 8, 4), "Kroger"),
      Tx(450m, TransactionCategory.Shopping, new DateTime(2026, 8, 10), "Apple"),
      Tx(15.99m, TransactionCategory.Subscriptions, new DateTime(2026, 8, 7), "Netflix"),
      Tx(4.50m, TransactionCategory.DiningOut, new DateTime(2026, 8, 12), "Cafe"),
      Tx(6.20m, TransactionCategory.DiningOut, new DateTime(2026, 8, 13), "Cafe"),
      Tx(8.10m, TransactionCategory.DiningOut, new DateTime(2026, 8, 14), "Cafe"),
      Tx(5.00m, TransactionCategory.DiningOut, new DateTime(2026, 8, 15), "Cafe"),
      Tx(9.90m, TransactionCategory.DiningOut, new DateTime(2026, 8, 16), "Cafe"),
    };
    return txns;
  }

  [Fact]
  public void FactSheet_ComputesCoreFacts()
  {
    var expenses = SampleMonth();
    var spendDays = expenses.Select(t => t.Date.Date).ToHashSet();

    var facts = WrappedFactSheet.Build(
      expenses, new List<Transaction>(), new HashSet<string>(),
      spendDays, new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

    facts.Should().NotBeEmpty();
    facts.Select(f => f.Key).Should().Contain(new[]
    {
      "weekend_share", "largest_purchase", "small_buys", "new_merchants",
    });
    var largest = facts.Single(f => f.Key == "largest_purchase");
    largest.Value.Should().Contain("450");
    var newM = facts.Single(f => f.Key == "new_merchants");
    newM.Value.Should().Be("5"); // all merchants unseen in prior lookback
    facts.Should().OnlyContain(f => f.Surprise >= 0 && f.Surprise <= 1);
  }

  [Fact]
  public void FactSheet_NewMerchants_RespectsLookback()
  {
    var expenses = SampleMonth();
    var prior = new HashSet<string> { "Saltline", "Kroger", "Cafe", "Netflix" };

    var facts = WrappedFactSheet.Build(
      expenses, new List<Transaction>(), prior,
      expenses.Select(t => t.Date.Date).ToHashSet(),
      new DateTime(2026, 8, 1), new DateTime(2026, 8, 31));

    facts.Single(f => f.Key == "new_merchants").Value.Should().Be("1"); // Apple
  }

  [Fact]
  public void RotationPick_TakesTwoMostSurprising()
  {
    var facts = new List<WrappedFactDto>
    {
      new() { Key = "a", Surprise = 0.2, Value = "1" },
      new() { Key = "b", Surprise = 0.9, Value = "2" },
      new() { Key = "c", Surprise = 0.5, Value = "3" },
    };

    var cards = WrappedGarnishService.RotationPick(facts);

    cards.Should().HaveCount(2);
    cards[0].FactKey.Should().Be("b");
    cards[1].FactKey.Should().Be("c");
    cards.Should().OnlyContain(c => c.Source == "rotation");
  }

  [Fact]
  public void ParseAndValidate_AcceptsGoodResponse()
  {
    var facts = new List<WrappedFactDto>
    {
      new() { Key = "weekend_share", Value = "62%", Detail = "d1" },
      new() { Key = "small_buys", Value = "12", Detail = "d2" },
    };
    var response = """
      ```json
      [{"factKey":"weekend_share","kicker":"weekend warrior","headline":"of it all happened on the weekend"},
       {"factKey":"small_buys","kicker":"tiny treats","headline":"little purchases, one big habit"}]
      ```
      """;

    var cards = WrappedGarnishService.ParseAndValidate(response, facts);

    cards.Should().HaveCount(2);
    cards[0].Value.Should().Be("62%"); // number came from OUR sheet
    cards[0].Source.Should().Be("ai");
  }

  [Fact]
  public void ParseAndValidate_RejectsDigitsUnknownKeysAndDuplicates()
  {
    var facts = new List<WrappedFactDto> { new() { Key = "small_buys", Value = "12" } };
    var response = """
      [{"factKey":"made_up","kicker":"x","headline":"y"},
       {"factKey":"small_buys","kicker":"you spent 900","headline":"digits in kicker"},
       {"factKey":"small_buys","kicker":"tiny treats","headline":"little things add up"},
       {"factKey":"small_buys","kicker":"dupe","headline":"duplicate key"}]
      """;

    var cards = WrappedGarnishService.ParseAndValidate(response, facts);

    cards.Should().HaveCount(1); // only the one valid, digit-free, novel-key card
    cards[0].Kicker.Should().Be("tiny treats");
  }

  [Fact]
  public void ParseAndValidate_GarbageYieldsEmpty()
  {
    var facts = new List<WrappedFactDto> { new() { Key = "a", Value = "1" } };
    WrappedGarnishService.ParseAndValidate("Sorry, I encountered an error.", facts)
      .Should().BeEmpty();
    WrappedGarnishService.ParseAndValidate("[{broken json", facts).Should().BeEmpty();
  }
}
