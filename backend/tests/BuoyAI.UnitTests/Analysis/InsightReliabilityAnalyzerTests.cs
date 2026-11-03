using BuoyAI.Core.Analysis;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Analysis;

public class InsightReliabilityAnalyzerTests
{
    private static TransactionDto Tx(
        decimal amount,
        TransactionCategory category,
        bool isUserCategorized,
        bool needsReview = false,
        string merchant = "Merchant",
        string? categorySource = "Plaid",
        double? aiConfidence = null)
    {
        return new TransactionDto
        {
            Amount = amount,
            Type = amount >= 0 ? TransactionType.Credit : TransactionType.Debit,
            CategoryId = (int)category,
            CategoryName = category.ToString(),
            MerchantName = merchant,
            Date = new DateTime(2026, 7, 1),
            IsUserCategorized = isUserCategorized,
            NeedsReview = needsReview,
            CategorySource = categorySource,
            AIConfidence = aiConfidence,
        };
    }

    [Fact]
    public void Assess_AllReviewed_HasNoMaterialUncertainty()
    {
        var txns = new[]
        {
            Tx(-100, TransactionCategory.Groceries, isUserCategorized: true),
            Tx(-50, TransactionCategory.DiningOut, isUserCategorized: true),
        };

        var result = InsightReliabilityAnalyzer.Assess(txns, totalExpenses: 150);

        result.HasMaterialUncertainty.Should().BeFalse();
        result.UnreviewedCount.Should().Be(0);
        result.ReviewedExpenses.Should().Be(150);
        result.UnreviewedExpenses.Should().Be(0);
    }

    [Fact]
    public void Assess_LargeUnreviewedExpense_IsFlaggedAsMaterial()
    {
        var txns = new[]
        {
            Tx(-100, TransactionCategory.Groceries, isUserCategorized: true),
            // 4000 / 4100 = 97.6% of spend, unreviewed -> material + uncertain
            Tx(-4000, TransactionCategory.Uncategorized, isUserCategorized: false, merchant: "ROBINHOOD"),
        };

        var result = InsightReliabilityAnalyzer.Assess(txns, totalExpenses: 4100);

        result.HasMaterialUncertainty.Should().BeTrue();
        result.MaterialUncertainItems.Should().HaveCount(1);
        var item = result.MaterialUncertainItems[0];
        item.Merchant.Should().Be("ROBINHOOD");
        item.Amount.Should().Be(4000);
        item.ShareOfSpend.Should().BeApproximately(0.9756m, 0.001m);
        result.UnreviewedCount.Should().Be(1);
        result.UnreviewedExpenses.Should().Be(4000);
    }

    [Fact]
    public void Assess_SmallUnreviewedExpense_CountsButIsNotFlagged()
    {
        var txns = new[]
        {
            Tx(-950, TransactionCategory.Groceries, isUserCategorized: true),
            // 50 / 1000 = 5% -> immaterial, must NOT be flagged (guardrail #2: no blanket hedging)
            Tx(-50, TransactionCategory.DiningOut, isUserCategorized: false),
        };

        var result = InsightReliabilityAnalyzer.Assess(txns, totalExpenses: 1000);

        result.HasMaterialUncertainty.Should().BeFalse();
        result.UnreviewedCount.Should().Be(1);
        result.UnreviewedExpenses.Should().Be(50);
    }

    [Fact]
    public void Assess_IncomeAndTransfers_AreExcludedFromExpenseBase()
    {
        var txns = new[]
        {
            Tx(-100, TransactionCategory.Groceries, isUserCategorized: true),
            Tx(5000, TransactionCategory.Salary, isUserCategorized: false),          // income credit
            Tx(-3000, TransactionCategory.Transfer, isUserCategorized: false),        // transfer
            Tx(-2000, TransactionCategory.Investment, isUserCategorized: false),      // income-category debit
        };

        var result = InsightReliabilityAnalyzer.Assess(txns, totalExpenses: 100);

        // Only the Groceries expense counts; the big unreviewed income/transfer rows are not
        // "unreviewed spend" and must not be flagged as material spending uncertainty.
        result.UnreviewedCount.Should().Be(0);
        result.UnreviewedExpenses.Should().Be(0);
        result.HasMaterialUncertainty.Should().BeFalse();
    }

    [Fact]
    public void Assess_CapsFlaggedItemsAtMax()
    {
        var txns = Enumerable.Range(0, 8)
            .Select(i => Tx(-1000, TransactionCategory.Shopping, isUserCategorized: false, merchant: $"M{i}"))
            .ToArray();

        // Each -1000 is 20% of the 5000 denominator, so all 8 qualify as material and the
        // cap must trim the named list to MaxFlaggedItems even though the count is higher.
        var result = InsightReliabilityAnalyzer.Assess(txns, totalExpenses: 5000);

        result.MaterialUncertainItems.Should().HaveCount(InsightReliabilityAnalyzer.MaxFlaggedItems);
        result.UnreviewedCount.Should().Be(8);
    }

    [Fact]
    public void FormatForPrompt_CleanData_ReassuresInsteadOfHedging()
    {
        var result = InsightReliabilityAnalyzer.Assess(
            new[] { Tx(-100, TransactionCategory.Groceries, isUserCategorized: true) },
            totalExpenses: 100);

        var text = InsightReliabilityAnalyzer.FormatForPrompt(result);

        text.Should().Contain("DATA RELIABILITY");
        text.Should().Contain("reliable");
        text.Should().NotContain("⚠");
    }

    [Fact]
    public void FormatForPrompt_MaterialUncertainty_EmitsHedgeGuidanceAndItem()
    {
        var result = InsightReliabilityAnalyzer.Assess(
            new[]
            {
                Tx(-100, TransactionCategory.Groceries, isUserCategorized: true),
                Tx(-4000, TransactionCategory.Uncategorized, isUserCategorized: false, merchant: "ROBINHOOD"),
            },
            totalExpenses: 4100);

        var text = InsightReliabilityAnalyzer.FormatForPrompt(result);

        text.Should().Contain("TENTATIVE");
        text.Should().Contain("ROBINHOOD");
        text.Should().Contain("⚠");
        // Must instruct the model NOT to invent a transfer itself (guardrail #3).
        text.Should().Contain("Never assume");
    }

    [Fact]
    public void FormatForPrompt_NoSpend_SaysNothingToAssess()
    {
        var result = InsightReliabilityAnalyzer.Assess(Array.Empty<TransactionDto>(), totalExpenses: 0);

        var text = InsightReliabilityAnalyzer.FormatForPrompt(result);

        text.Should().Contain("No spending in this window");
    }
}
