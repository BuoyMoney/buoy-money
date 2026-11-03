using BuoyAI.Core.Analysis;
using BuoyAI.Core.Enums;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Analysis;

public class TransferPairDetectorTests
{
    private static readonly DateTime Day = new(2026, 7, 1);

    private static TransferCandidate Cand(
        int id,
        int accountId,
        decimal amount,
        TransactionType type,
        DateTime? date = null,
        int? categoryId = (int)TransactionCategory.Uncategorized,
        bool userCategorized = false,
        bool isSplit = false,
        string? plaidPrimary = null,
        string? plaidDetailed = null,
        int ownerUserId = 1,
        bool dismissedAsTransfer = false)
    {
        return new TransferCandidate(
            Id: id,
            AccountId: accountId,
            OwnerUserId: ownerUserId,
            AccountName: $"Account {accountId}",
            Amount: amount,
            Type: type,
            Date: date ?? Day,
            CategoryId: categoryId,
            IsUserCategorized: userCategorized,
            IsSplit: isSplit,
            PlaidPrimary: plaidPrimary,
            PlaidDetailed: plaidDetailed,
            MerchantName: "Merchant",
            DismissedAsTransfer: dismissedAsTransfer);
    }

    [Fact]
    public void Detect_ExactPairAcrossAccountsWithinWindow_IsMediumSuspect()
    {
        var candidates = new[]
        {
            Cand(1, accountId: 10, amount: -2000, TransactionType.Debit, Day),
            Cand(2, accountId: 20, amount: 2000, TransactionType.Credit, Day.AddDays(1)),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().HaveCount(1);
        result[0].Confidence.Should().Be(TransferConfidence.Medium);
        result[0].Sides.Select(s => s.TransactionId).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void Detect_LegDismissedAsNotATransfer_IsNotSuggested()
    {
        var candidates = new[]
        {
            Cand(1, accountId: 10, amount: -2000, TransactionType.Debit, Day, dismissedAsTransfer: true),
            Cand(2, accountId: 20, amount: 2000, TransactionType.Credit, Day.AddDays(1)),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Detect_PairWithPlaidTransferTag_IsHighConfidence()
    {
        var candidates = new[]
        {
            Cand(1, 10, -2000, TransactionType.Debit, Day, plaidPrimary: "TRANSFER_OUT"),
            Cand(2, 20, 2000, TransactionType.Credit, Day),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().ContainSingle().Which.Confidence.Should().Be(TransferConfidence.High);
    }

    [Fact]
    public void Detect_IncomeLeg_IsNotMatchedAsTransfer()
    {
        // A paycheck (auto-categorized Salary credit) must never be paired with an unrelated
        // same-amount debit and suggested as a transfer.
        var candidates = new[]
        {
            Cand(1, 10, -2000, TransactionType.Debit, Day),
            Cand(2, 20, 2000, TransactionType.Credit, Day, categoryId: (int)TransactionCategory.Salary),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_LegsOwnedByDifferentUsers_AreNotPaired()
    {
        // In a shared family view the transaction set spans members; opposite same-amount
        // transactions owned by DIFFERENT people are not an internal transfer.
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day, ownerUserId: 1),
            Cand(2, 20, 500, TransactionType.Credit, Day, ownerUserId: 2),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_SameAccount_IsNotAPair()
    {
        // Money can't transfer to the same account — must not match.
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day),
            Cand(2, 10, 500, TransactionType.Credit, Day),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_AmountMismatch_IsNotAPair()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day),
            Cand(2, 20, 480, TransactionType.Credit, Day),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_OutsideDateWindow_IsNotAPair()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day),
            Cand(2, 20, 500, TransactionType.Credit, Day.AddDays(10)),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_AlreadyCategorizedAsTransfer_IsExcluded()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day, categoryId: (int)TransactionCategory.Transfer),
            Cand(2, 20, 500, TransactionType.Credit, Day, categoryId: (int)TransactionCategory.Transfer),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_UserCategorized_IsExcluded()
    {
        // Respect explicit user decisions — never second-guess a confirmed category.
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day, userCategorized: true),
            Cand(2, 20, 500, TransactionType.Credit, Day, userCategorized: true),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_OneDebitTwoMatchingCredits_MatchesOnlyOnePair()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day),
            Cand(2, 20, 500, TransactionType.Credit, Day),          // closer in time
            Cand(3, 30, 500, TransactionType.Credit, Day.AddDays(2)),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().HaveCount(1);
        result[0].Sides.Should().HaveCount(2);
        result[0].Sides.Select(s => s.TransactionId).Should().Contain(2); // nearest-date credit
    }

    [Fact]
    public void Detect_SameDirection_IsNotAPair()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500, TransactionType.Debit, Day),
            Cand(2, 20, -500, TransactionType.Debit, Day),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_PlaidTransferWithNoPair_IsLowConfidenceSingle()
    {
        var candidates = new[]
        {
            Cand(1, 10, -750, TransactionType.Debit, Day, plaidDetailed: "TRANSFER_OUT_ACCOUNT_TRANSFER"),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().ContainSingle();
        result[0].Confidence.Should().Be(TransferConfidence.Low);
        result[0].Sides.Should().ContainSingle().Which.TransactionId.Should().Be(1);
    }

    [Fact]
    public void Detect_LoanPayment_IsNotTreatedAsTransfer()
    {
        // Loan payments are real obligations, not inter-account transfers.
        var candidates = new[]
        {
            Cand(1, 10, -900, TransactionType.Debit, Day, plaidPrimary: "LOAN_PAYMENTS", plaidDetailed: "LOAN_PAYMENTS_CAR_PAYMENT"),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_BelowMinAmount_IsExcluded()
    {
        var candidates = new[]
        {
            Cand(1, 10, -0.50m, TransactionType.Debit, Day),
            Cand(2, 20, 0.50m, TransactionType.Credit, Day),
        };

        TransferPairDetector.Detect(candidates).Should().BeEmpty();
    }

    [Fact]
    public void Detect_PlaidTaggedInflowWithoutCounterpart_SaysCountedAsIncome()
    {
        var candidates = new[]
        {
            Cand(1, 10, 6361.30m, TransactionType.Credit, Day, plaidPrimary: "TRANSFER_IN", plaidDetailed: "TRANSFER_IN_ACCOUNT_TRANSFER"),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().HaveCount(1);
        result[0].Reason.Should().Contain("counted as income").And.NotContain("spending");
    }

    [Fact]
    public void Detect_PlaidTaggedOutflowWithoutCounterpart_SaysCountedAsSpending()
    {
        var candidates = new[]
        {
            Cand(1, 10, -500m, TransactionType.Debit, Day, plaidPrimary: "TRANSFER_OUT", plaidDetailed: "TRANSFER_OUT_ACCOUNT_TRANSFER"),
        };

        var result = TransferPairDetector.Detect(candidates);

        result.Should().HaveCount(1);
        result[0].Reason.Should().Contain("counted as spending");
    }
}
