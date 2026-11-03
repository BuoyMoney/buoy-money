using BuoyAI.Core.Receipts;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Receipts;

public class ReceiptSplitPlannerTests
{
  private const int Groceries = 20;
  private const int Shopping = 24;
  private const int Pets = 1001;

  private static ParsedReceiptItem Item(string name, decimal price, int? category) =>
    new() { Name = name, Price = price, CategoryId = category };

  private static ParsedReceipt WalmartRun() =>
    new()
    {
      IsReceipt = true,
      Total = 187.43m,
      Tax = 7.43m,
      Items =
      {
        Item("Milk", 4.50m, Groceries),
        Item("Bananas", 2.10m, Groceries),
        Item("Chicken", 101.40m, Groceries),
        Item("Paper towels", 26.00m, Shopping),
        Item("Dish soap", 20.00m, Shopping),
        Item("Dog food", 26.00m, Pets),
      },
    };

  [Fact]
  public void Parts_add_up_to_the_charge_to_the_cent()
  {
    var plan = ReceiptSplitPlanner.Plan(WalmartRun(), -187.43m);
    plan.Sum(p => p.Amount).Should().Be(187.43m);
    plan.Select(p => p.CategoryId).Should().Equal(Groceries, Shopping, Pets);
  }

  [Fact]
  public void Tax_is_spread_by_each_category_share()
  {
    var plan = ReceiptSplitPlanner.Plan(WalmartRun(), 187.43m);
    // Groceries are 108/180 of the items, so they carry 108/180 of the charge.
    plan[0].Amount.Should().Be(112.46m);
    plan[1].Amount.Should().Be(47.90m);
    plan[2].Amount.Should().Be(27.07m);
  }

  [Fact]
  public void A_changed_charge_is_shared_out_the_same_way()
  {
    var plan = ReceiptSplitPlanner.Plan(WalmartRun(), 170.01m);
    plan.Sum(p => p.Amount).Should().Be(170.01m);
    plan[0].Amount.Should().BeGreaterThan(plan[1].Amount);
  }

  [Fact]
  public void Rounding_never_loses_a_cent()
  {
    var receipt = new ParsedReceipt
    {
      IsReceipt = true,
      Items = { Item("A", 1m, Groceries), Item("B", 1m, Shopping), Item("C", 1m, Pets) },
    };
    ReceiptSplitPlanner.Plan(receipt, 10m).Sum(p => p.Amount).Should().Be(10m);
    ReceiptSplitPlanner.Plan(receipt, 0.02m).Sum(p => p.Amount).Should().Be(0.02m);
  }

  [Fact]
  public void Items_without_a_category_go_to_shopping()
  {
    var receipt = new ParsedReceipt { IsReceipt = true, Items = { Item("Mystery", 5m, null), Item("Milk", 5m, Groceries) } };
    ReceiptSplitPlanner.Plan(receipt, 10m).Select(p => p.CategoryId).Should().BeEquivalentTo(new[] { Shopping, Groceries });
  }

  [Fact]
  public void A_one_category_receipt_is_one_part()
  {
    var receipt = new ParsedReceipt { IsReceipt = true, Items = { Item("Milk", 5m, Groceries), Item("Eggs", 3m, Groceries) } };
    var plan = ReceiptSplitPlanner.Plan(receipt, 8.40m);
    plan.Should().ContainSingle();
    plan[0].Amount.Should().Be(8.40m);
    plan[0].Items.Should().Equal("Milk", "Eggs");
  }

  [Fact]
  public void An_item_discount_line_lowers_its_category()
  {
    var receipt = new ParsedReceipt
    {
      IsReceipt = true,
      Items = { Item("Shampoo", 10m, 27), Item("Coupon", -5m, 27), Item("Milk", 5m, Groceries) },
    };
    var plan = ReceiptSplitPlanner.Plan(receipt, 10m);
    plan.Should().HaveCount(2);
    plan.Single(p => p.CategoryId == 27).Amount.Should().Be(5m);
    plan.Single(p => p.CategoryId == 27).Items.Should().Equal("Shampoo");
  }

  [Fact]
  public void No_prices_means_no_plan()
  {
    var receipt = new ParsedReceipt { IsReceipt = true, Items = { Item("Milk", 0m, Groceries) } };
    ReceiptSplitPlanner.Plan(receipt, 10m).Should().BeEmpty();
  }

  [Fact]
  public void Too_many_categories_fold_into_the_last_part()
  {
    var receipt = new ParsedReceipt { IsReceipt = true };
    for (var i = 0; i < 14; i++)
      receipt.Items.Add(Item($"Item {i}", 20m - i, 1000 + i));
    var plan = ReceiptSplitPlanner.Plan(receipt, 200m);
    plan.Should().HaveCount(ReceiptSplitPlanner.MaxSplits);
    plan.Sum(p => p.Amount).Should().Be(200m);
    plan.Last().Items.Should().HaveCount(5);
  }

  [Fact]
  public void The_note_lists_up_to_three_items()
  {
    new PlannedSplit { Items = { "Milk", "Bananas" } }.Note().Should().Be("Milk, Bananas");
    new PlannedSplit { Items = { "Milk", "Bananas", "Bread", "Eggs", "Cheese" } }.Note().Should().Be("Milk, Bananas, Bread +2 more");
  }

  [Fact]
  public void A_proposal_is_confident_only_for_an_exact_match_on_a_receipt_that_adds_up()
  {
    var receipt = WalmartRun();
    var charge = new ReceiptCandidate(7, 187.43m, true, DateTime.Today, "Walmart", "", false, false, false);
    var names = new Dictionary<int, string> { [Groceries] = "Groceries" };

    var exact = ReceiptSplitPlanner.Propose(receipt, new ReceiptMatch("exact", new() { charge }), names);
    exact.Confident.Should().BeTrue();
    exact.Charges.Single().TransactionId.Should().Be(7);
    exact.Charges.Single().Splits[0].CategoryName.Should().Be("Groceries");

    ReceiptSplitPlanner.Propose(receipt, new ReceiptMatch("scaled", new() { charge })).Confident.Should().BeFalse();

    receipt.Total = 200m; // items + tax no longer add up to it
    ReceiptSplitPlanner.Propose(receipt, new ReceiptMatch("exact", new() { charge })).Confident.Should().BeFalse();
  }

  [Fact]
  public void Every_matched_charge_gets_its_own_plan()
  {
    var receipt = WalmartRun();
    var a = new ReceiptCandidate(1, 100m, true, DateTime.Today, "Amazon", "", false, false, false);
    var b = new ReceiptCandidate(2, 87.43m, true, DateTime.Today, "Amazon", "", false, false, false);
    var proposal = ReceiptSplitPlanner.Propose(receipt, new ReceiptMatch("sum", new() { a, b }));
    proposal.Charges.Select(c => c.Splits.Sum(s => s.Amount)).Should().Equal(100m, 87.43m);
  }
}
