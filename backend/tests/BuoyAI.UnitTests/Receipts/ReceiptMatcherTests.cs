using BuoyAI.Core.Receipts;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Receipts;

public class ReceiptMatcherTests
{
  private static readonly DateTime Order = new(2026, 10, 1);
  private static readonly RetailerDirectory.Retailer Walmart = RetailerDirectory.ByKey("walmart")!;
  private static readonly RetailerDirectory.Retailer Amazon = RetailerDirectory.ByKey("amazon")!;

  private static ParsedReceipt Receipt(decimal total, params decimal[] charges) =>
    new()
    {
      IsReceipt = true,
      Retailer = "walmart",
      OrderDate = Order,
      Total = total,
      Items = { new ParsedReceiptItem { Name = "Milk", Price = total, CategoryId = 20 } },
      Charges = charges.ToList(),
    };

  private static ReceiptCandidate Charge(
    int id,
    decimal amount,
    int dayOffset = 0,
    string merchant = "Walmart",
    bool pending = false,
    bool split = false,
    bool child = false
  ) => new(id, amount, true, Order.AddDays(dayOffset), merchant, merchant.ToUpperInvariant() + " #1234", pending, split, child);

  [Fact]
  public void An_exact_amount_from_the_same_store_matches()
  {
    var match = ReceiptMatcher.Match(Receipt(187.43m), Walmart, new[] { Charge(1, 50m), Charge(2, 187.43m, 1) }, Order);
    match!.Method.Should().Be("exact");
    match.Charges.Select(c => c.Id).Should().Equal(2);
  }

  [Fact]
  public void Exact_means_to_the_cent()
  {
    // A $60.01 charge closer in date must not beat the real $60.00 one.
    var match = ReceiptMatcher.Match(Receipt(60m), Walmart, new[] { Charge(1, 60.01m), Charge(2, 60m, 2) }, Order);
    match!.Method.Should().Be("exact");
    match.Charges.Select(c => c.Id).Should().Equal(2);
  }

  [Fact]
  public void A_charge_in_another_currency_never_matches()
  {
    var receipt = Receipt(60m);
    receipt.Currency = "USD";
    var cad = Charge(1, 60m) with { Currency = "CAD" };
    ReceiptMatcher.Match(receipt, Walmart, new[] { cad }, Order).Should().BeNull();
    ReceiptMatcher.Match(receipt, Walmart, new[] { cad }, Order, hintTransactionId: 1).Should().BeNull();
    ReceiptMatcher.Match(receipt, Walmart, new[] { Charge(2, 60m) with { Currency = "USD" } }, Order).Should().NotBeNull();
    // Unknown on either side: no filter.
    ReceiptMatcher.Match(Receipt(60m), Walmart, new[] { cad }, Order).Should().NotBeNull();
  }

  [Fact]
  public void Bank_merchant_spellings_count_as_the_store()
  {
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 0, "WM SUPERCENTER") }, Order).Should().NotBeNull();
    ReceiptMatcher.Match(Receipt(20m), Amazon, new[] { Charge(1, 20m, 0, "AMZN Mktp US*2K4") }, Order).Should().NotBeNull();
  }

  [Fact]
  public void Another_store_never_matches()
  {
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 0, "Kroger") }, Order).Should().BeNull();
  }

  [Fact]
  public void Pending_holds_and_split_charges_are_skipped()
  {
    var candidates = new[]
    {
      Charge(1, 20m, pending: true),
      Charge(2, 20m, split: true),
      Charge(3, 20m, child: true),
    };
    ReceiptMatcher.Match(Receipt(20m), Walmart, candidates, Order).Should().BeNull();
  }

  [Fact]
  public void Money_coming_in_is_never_matched_even_at_the_same_amount()
  {
    var refund = new ReceiptCandidate(1, 20m, false, Order, "Walmart", "WALMART REFUND", false, false, false);
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { refund }, Order).Should().BeNull();
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { refund }, Order, hintTransactionId: 1).Should().BeNull();
  }

  [Fact]
  public void Charges_outside_the_date_window_are_skipped()
  {
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, -4) }, Order).Should().BeNull();
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 11) }, Order).Should().BeNull();
    ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 10) }, Order).Should().NotBeNull();
  }

  [Fact]
  public void The_window_runs_from_the_email_when_it_came_later_than_the_order()
  {
    // Forwarded a week late: a charge 15 days after the order still fits (10 days after the email).
    var match = ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 15) }, Order.AddDays(7));
    match.Should().NotBeNull();
  }

  [Fact]
  public void Two_identical_charges_pick_the_closer_one_or_none_when_tied()
  {
    var closer = ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 5), Charge(2, 20m, 1) }, Order);
    closer!.Charges.Single().Id.Should().Be(2);

    var tied = ReceiptMatcher.Match(Receipt(20m), Walmart, new[] { Charge(1, 20m, 2), Charge(2, 20m, 2) }, Order);
    tied.Should().BeNull();
  }

  [Fact]
  public void Listed_card_charges_each_match_a_different_charge()
  {
    var match = ReceiptMatcher.Match(
      Receipt(96m, 61.20m, 34.80m),
      Amazon,
      new[] { Charge(1, 61.20m, 1, "Amazon"), Charge(2, 34.80m, 3, "Amazon"), Charge(3, 12m, 2, "Amazon") },
      Order
    );
    match!.Method.Should().Be("charges");
    match.Charges.Select(c => c.Id).Should().BeEquivalentTo(new[] { 1, 2 });
  }

  [Fact]
  public void A_listed_charge_with_two_equally_close_matches_is_no_match()
  {
    var match = ReceiptMatcher.Match(
      Receipt(96m, 61.20m, 34.80m),
      Amazon,
      new[] { Charge(1, 61.20m, 1, "Amazon"), Charge(2, 34.80m, 3, "Amazon"), Charge(3, 34.80m, 3, "Amazon") },
      Order
    );
    match.Should().BeNull();
  }

  [Fact]
  public void An_order_shipped_in_parts_matches_charges_that_add_up()
  {
    var match = ReceiptMatcher.Match(
      Receipt(96m),
      Amazon,
      new[] { Charge(1, 61.20m, 1, "Amazon"), Charge(2, 34.80m, 3, "Amazon"), Charge(3, 12.99m, 2, "Amazon") },
      Order
    );
    match!.Method.Should().Be("sum");
    match.Charges.Select(c => c.Id).Should().BeEquivalentTo(new[] { 1, 2 });
  }

  [Fact]
  public void Two_different_ways_to_add_up_is_no_match()
  {
    var match = ReceiptMatcher.Match(
      Receipt(30m),
      Amazon,
      new[] { Charge(1, 10m, 1, "Amazon"), Charge(2, 20m, 1, "Amazon"), Charge(3, 15m, 2, "Amazon"), Charge(4, 15m, 3, "Amazon") },
      Order
    );
    match.Should().BeNull();
  }

  [Fact]
  public void A_changed_total_matches_the_only_charge_within_15_percent()
  {
    // Walmart weighed the produce at delivery: ordered $100, charged $93.10.
    var match = ReceiptMatcher.Match(Receipt(100m), Walmart, new[] { Charge(1, 93.10m, 2), Charge(2, 40m, 1) }, Order);
    match!.Method.Should().Be("scaled");
    match.Charges.Single().Id.Should().Be(1);
  }

  [Fact]
  public void A_changed_total_with_two_possible_charges_is_no_match()
  {
    var match = ReceiptMatcher.Match(Receipt(100m), Walmart, new[] { Charge(1, 93.10m, 2), Charge(2, 106m, 1) }, Order);
    match.Should().BeNull();
  }

  [Fact]
  public void More_than_15_percent_off_is_no_match()
  {
    ReceiptMatcher.Match(Receipt(100m), Walmart, new[] { Charge(1, 80m, 1) }, Order).Should().BeNull();
  }

  [Fact]
  public void Scanned_from_a_transaction_that_charge_wins_whatever_the_bank_calls_it()
  {
    var candidates = new[] { Charge(1, 20m, 1), Charge(2, 20m, 2, "POS PURCHASE 4471") };
    var match = ReceiptMatcher.Match(Receipt(20m), Walmart, candidates, Order, hintTransactionId: 2);
    match!.Charges.Single().Id.Should().Be(2);
    // Within 15%: still the hinted charge, scaled.
    ReceiptMatcher.Match(Receipt(22m), Walmart, candidates, Order, hintTransactionId: 2)!.Method.Should().Be("scaled");
  }

  [Fact]
  public void A_hinted_charge_that_does_not_fit_falls_back_to_the_normal_rules()
  {
    var candidates = new[] { Charge(1, 20m, 1), Charge(2, 75m, 2) };
    var match = ReceiptMatcher.Match(Receipt(20m), Walmart, candidates, Order, hintTransactionId: 2);
    match!.Charges.Single().Id.Should().Be(1);
  }

  [Fact]
  public void Any_store_matches_by_the_name_on_the_receipt()
  {
    var store = RetailerDirectory.Resolve("Hy-Vee")!;
    ReceiptMatcher.Match(Receipt(20m), store, new[] { Charge(1, 20m, 1, "HY-VEE TOPEKA 1234") }, Order).Should().NotBeNull();
    RetailerDirectory.Resolve("ab").Should().BeNull();
    RetailerDirectory.Resolve("The Market").Should().BeNull();
    var wholeFoods = RetailerDirectory.Resolve("Whole Foods Market")!;
    ReceiptMatcher.Match(Receipt(20m), wholeFoods, new[] { Charge(1, 20m, 1, "Whole Foods") }, Order).Should().NotBeNull();
    RetailerDirectory.Resolve("sam's club")!.Key.Should().Be("samsclub");
  }

  [Fact]
  public void Refunds_and_non_receipts_never_match()
  {
    var refund = Receipt(20m);
    refund.Kind = "refund";
    ReceiptMatcher.Match(refund, Walmart, new[] { Charge(1, 20m) }, Order).Should().BeNull();

    var marketing = Receipt(20m);
    marketing.IsReceipt = false;
    ReceiptMatcher.Match(marketing, Walmart, new[] { Charge(1, 20m) }, Order).Should().BeNull();
  }
}
