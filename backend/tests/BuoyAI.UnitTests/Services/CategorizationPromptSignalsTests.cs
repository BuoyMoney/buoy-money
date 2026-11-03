using System;
using System.Collections.Generic;
using System.Linq;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The nuance signals the categorization prompt gets: per-merchant category splits with
/// count / average amount / weekday-weekend, timing, and Plaid's own read.
/// </summary>
public class CategorizationPromptSignalsTests
{
    private static Transaction Row(string merchant, int categoryId, decimal amount, DateTime date, string? categoryName = null) => new()
    {
        MerchantName = merchant,
        CategoryId = categoryId,
        Amount = -amount,
        Date = date,
        IsUserCategorized = true,
        CategoryNav = categoryName == null ? null : new Category { Id = categoryId, Name = categoryName },
    };

    private static readonly DateTime Sat = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);   // Saturday
    private static readonly DateTime Sun = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);   // Sunday
    private static readonly DateTime Wed = new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);   // Wednesday
    private static readonly DateTime Thu = new(2026, 8, 6, 0, 0, 0, DateTimeKind.Utc);   // Thursday

    [Fact]
    public void BuildPatterns_StableMerchant_HasOneSplitAndIsNotSplit()
    {
        var patterns = CategorizationPromptSignals.BuildPatterns(new[]
        {
            Row("Dillons", 10, 80m, Sat, "Groceries"),
            Row("Dillons", 10, 90m, Sun, "Groceries"),
        });

        var p = patterns["dillons"];
        p.IsSplit.Should().BeFalse();
        p.CategoryCount.Should().Be(1);
        p.Splits.Should().ContainSingle().Which.Count.Should().Be(2);
        p.DisplayName.Should().Be("Groceries");
    }

    [Fact]
    public void BuildPatterns_SplitMerchant_ReportsCountAmountAndWeekendShare()
    {
        var patterns = CategorizationPromptSignals.BuildPatterns(new[]
        {
            Row("Dillons", 10, 80m, Sat, "Groceries"),
            Row("Dillons", 10, 90m, Sun, "Groceries"),
            Row("Dillons", 10, 100m, Sat, "Groceries"),
            Row("Dillons", 13, 30m, Wed, "Household"),
            Row("Dillons", 13, 26m, Thu, "Household"),
        });

        var p = patterns["dillons"];
        p.IsSplit.Should().BeTrue();
        p.Splits.Should().HaveCount(2);
        var groceries = p.Splits.Single(s => s.DisplayName == "Groceries");
        groceries.Count.Should().Be(3);
        groceries.AverageAmount.Should().Be(90m);
        groceries.WeekendShare.Should().Be(1.0);
        var household = p.Splits.Single(s => s.DisplayName == "Household");
        household.Count.Should().Be(2);
        household.AverageAmount.Should().Be(28m);
        household.WeekendShare.Should().Be(0.0);
    }

    [Fact]
    public void BuildPatterns_SplitsOrderedByCountDescending()
    {
        var patterns = CategorizationPromptSignals.BuildPatterns(new[]
        {
            Row("Amazon", 13, 20m, Wed, "Household"),
            Row("Amazon", 10, 60m, Sat, "Groceries"),
            Row("Amazon", 10, 70m, Sun, "Groceries"),
        });

        patterns["amazon"].Splits.Select(s => s.DisplayName).Should().Equal("Groceries", "Household");
    }

    [Fact]
    public void BuildPatterns_DominantCategoryIsMostRecent()
    {
        var patterns = CategorizationPromptSignals.BuildPatterns(new[]
        {
            Row("Amazon", 10, 60m, Sat, "Groceries"),
            Row("Amazon", 13, 20m, Wed.AddDays(7), "Household"),
        });

        patterns["amazon"].DisplayName.Should().Be("Household");
    }

    [Fact]
    public void BuildPatterns_IgnoresUnconfirmedAndNamelessRows()
    {
        var unconfirmed = Row("Target", 10, 10m, Sat, "Groceries");
        unconfirmed.IsUserCategorized = false;
        var nameless = Row("", 10, 10m, Sat, "Groceries");

        var patterns = CategorizationPromptSignals.BuildPatterns(new[] { unconfirmed, nameless });

        patterns.Should().BeEmpty();
    }

    [Fact]
    public void BuildPatterns_MerchantKeyIsCaseInsensitive()
    {
        var patterns = CategorizationPromptSignals.BuildPatterns(new[]
        {
            Row("AMAZON", 10, 60m, Sat, "Groceries"),
            Row("amazon", 13, 20m, Wed, "Household"),
        });

        patterns.Should().ContainKey("Amazon");
        patterns["amazon"].IsSplit.Should().BeTrue();
    }

    [Fact]
    public void DescribeSplit_ReadsAsOneLineOfHistory()
    {
        var pattern = new CategoryPattern(
            Core.Enums.TransactionCategory.Groceries, null, "Groceries", 2,
            new List<MerchantSplit>
            {
                new("Groceries", 7, 85m, 0.9),
                new("Household", 3, 28m, 0.1),
            });

        CategorizationPromptSignals.DescribeSplit("dillons", pattern)
            .Should().Be("'dillons': Groceries 7x (avg $85.00, mostly weekends), Household 3x (avg $28.00, mostly weekdays)");
    }

    [Theory]
    [InlineData(0.0, "mostly weekdays")]
    [InlineData(0.25, "mostly weekdays")]
    [InlineData(0.5, "any day")]
    [InlineData(0.75, "mostly weekends")]
    [InlineData(1.0, "mostly weekends")]
    public void DescribeWeekendShare_Buckets(double share, string expected)
    {
        CategorizationPromptSignals.DescribeWeekendShare(share).Should().Be(expected);
    }

    [Fact]
    public void DescribeTiming_NamesDayAndMonthPosition()
    {
        CategorizationPromptSignals.DescribeTiming(new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be("Thursday, day 17 of 30 (mid-month)");
        CategorizationPromptSignals.DescribeTiming(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be("Wednesday, day 2 of 30 (early month)");
        CategorizationPromptSignals.DescribeTiming(new DateTime(2026, 9, 29, 0, 0, 0, DateTimeKind.Utc))
            .Should().Be("Tuesday, day 29 of 30 (late month)");
    }

    [Fact]
    public void DescribePlaidGuess_CombinesPrimaryDetailedAndConfidence()
    {
        var t = new Transaction
        {
            PlaidCategoryPrimary = "FOOD_AND_DRINK",
            PlaidCategoryDetailed = "FOOD_AND_DRINK_GROCERIES",
            PlaidCategoryConfidence = "HIGH",
        };

        CategorizationPromptSignals.DescribePlaidGuess(t)
            .Should().Be("FOOD_AND_DRINK / FOOD_AND_DRINK_GROCERIES (confidence HIGH)");
    }

    [Fact]
    public void DescribePlaidGuess_NothingFromPlaid_IsNull()
    {
        CategorizationPromptSignals.DescribePlaidGuess(new Transaction()).Should().BeNull();
    }
}
