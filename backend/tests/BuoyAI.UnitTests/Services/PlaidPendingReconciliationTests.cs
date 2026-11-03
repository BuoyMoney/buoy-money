using System;
using System.Collections.Generic;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Tests the pending-hold reconciliation that removes a pending transaction once its
/// posted charge arrives, even when the hold amount differs from the final charge.
/// </summary>
public class PlaidPendingReconciliationTests
{
  private static readonly DateTime D = new(2024, 3, 10, 0, 0, 0, DateTimeKind.Utc);

  private static Transaction Tx(int id, int accountId, decimal amount, bool pending,
      string merchant, DateTime date) =>
    new()
    {
      Id = id,
      AccountId = accountId,
      Amount = amount, // debits are negative in our model
      IsPending = pending,
      MerchantName = merchant,
      Date = date,
    };

  [Fact]
  public void Grocery_HoldHigherThanCharge_IsSuperseded()
  {
    // Aldi: $284 hold, $254 final charge a day later.
    var pending = new List<Transaction> { Tx(1, 10, -284m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 10, -254m, false, "Aldi", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().Equal(1);
  }

  [Fact]
  public void Walmart_HoldOneDollarOff_IsSuperseded()
  {
    var pending = new List<Transaction> { Tx(1, 10, -97m, true, "Walmart", D) };
    var posted = new List<Transaction> { Tx(2, 10, -96m, false, "Walmart", D.AddDays(2)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().Equal(1);
  }

  [Fact]
  public void DifferentMerchant_IsNotSuperseded()
  {
    var pending = new List<Transaction> { Tx(1, 10, -50m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 10, -50m, false, "Target", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void DifferentAccount_IsNotSuperseded()
  {
    var pending = new List<Transaction> { Tx(1, 10, -50m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 99, -50m, false, "Aldi", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void AmountWildlyDifferent_IsNotSuperseded()
  {
    var pending = new List<Transaction> { Tx(1, 10, -20m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 10, -200m, false, "Aldi", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void PostedTooFarInFuture_IsNotSuperseded()
  {
    var pending = new List<Transaction> { Tx(1, 10, -50m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 10, -50m, false, "Aldi", D.AddDays(20)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void OppositeSign_RefundVsCharge_IsNotSuperseded()
  {
    // A pending debit must never be reconciled against a posted refund/credit.
    var pending = new List<Transaction> { Tx(1, 10, -10m, true, "Aldi", D) };
    var posted = new List<Transaction> { Tx(2, 10, 30m, false, "Aldi", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void SmallAmountsOutsideProportionalTolerance_AreNotSuperseded()
  {
    // $4 pending vs $12 posted at the same coffee shop: $8 > max($5, 25% of $4) → no match.
    var pending = new List<Transaction> { Tx(1, 10, -4m, true, "Cafe", D) };
    var posted = new List<Transaction> { Tx(2, 10, -12m, false, "Cafe", D.AddDays(1)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().BeEmpty();
  }

  [Fact]
  public void IsUntouchedByUser_ReturnsFalse_WhenUserCategorizedOrTaggedOrSplit()
  {
    PlaidService.IsUntouchedByUser(Tx(1, 10, -50m, true, "Aldi", D)).Should().BeTrue();

    PlaidService.IsUntouchedByUser(new Transaction
    { Id = 2, AccountId = 10, Amount = -50m, IsPending = true, MerchantName = "Aldi", Date = D, IsUserCategorized = true })
      .Should().BeFalse();

    PlaidService.IsUntouchedByUser(new Transaction
    { Id = 3, AccountId = 10, Amount = -50m, IsPending = true, MerchantName = "Aldi", Date = D, SemanticTags = "groceries" })
      .Should().BeFalse();

    PlaidService.IsUntouchedByUser(new Transaction
    { Id = 4, AccountId = 10, Amount = -50m, IsPending = true, MerchantName = "Aldi", Date = D, SavingsGoalId = 7 })
      .Should().BeFalse();

    PlaidService.IsUntouchedByUser(new Transaction
    { Id = 5, AccountId = 10, Amount = -50m, IsPending = true, MerchantName = "Aldi", Date = D, UserNotes = "split rent" })
      .Should().BeFalse();
  }

  [Fact]
  public void OnePostedCharge_SupersedesOnlyOneOfTwoHolds()
  {
    // Two same-merchant holds but only one posted charge — only one is removed.
    var pending = new List<Transaction>
    {
      Tx(1, 10, -50m, true, "Aldi", D),
      Tx(2, 10, -50m, true, "Aldi", D.AddDays(1)),
    };
    var posted = new List<Transaction> { Tx(3, 10, -50m, false, "Aldi", D.AddDays(2)) };

    PlaidService.FindSupersededPendingIds(pending, posted).Should().HaveCount(1);
  }

  // ---- Pending -> posted in-place promotion (shared by all sync/import paths) ----

  [Fact]
  public void PromotePendingToPosted_SwapsIdClearsPendingAndNegatesAmount()
  {
    // $284 grocery hold settles at $254. Plaid reports the debit as a positive 254.
    var hold = Tx(1, 10, -284m, true, "Aldi", D);

    PlaidService.PromotePendingToPosted(
      hold,
      postedPlaidTransactionId: "posted-txn-1",
      plaidAmount: 254m,
      plaidDate: DateOnly.FromDateTime(D.AddDays(1)),
      merchantName: "Aldi Grocery");

    hold.PlaidTransactionId.Should().Be("posted-txn-1");
    hold.IsPending.Should().BeFalse();
    hold.Amount.Should().Be(-254m); // expense stays negative, refreshed to the settled figure
    hold.MerchantName.Should().Be("Aldi Grocery");
    hold.Description.Should().Be("Aldi Grocery");
    hold.Date.Should().Be(DateTime.SpecifyKind(D.AddDays(1), DateTimeKind.Utc));
  }

  [Fact]
  public void PromotePendingToPosted_KeepsExistingDateAndMerchant_WhenPostedFieldsMissing()
  {
    var hold = Tx(1, 10, -50m, true, "Original", D);

    PlaidService.PromotePendingToPosted(
      hold,
      postedPlaidTransactionId: "posted-txn-2",
      plaidAmount: 50m,
      plaidDate: null,
      merchantName: null);

    hold.PlaidTransactionId.Should().Be("posted-txn-2");
    hold.IsPending.Should().BeFalse();
    hold.Amount.Should().Be(-50m);
    hold.MerchantName.Should().Be("Original"); // not blanked out
    hold.Date.Should().Be(D);
  }
}
