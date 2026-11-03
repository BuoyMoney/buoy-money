using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Review-queue scoping (this-month / older / all) + the this-month vs older stats split.
/// </summary>
public class AICategorizationReviewScopeTests
{
    private const int UserId = 2;

    private static AICategorizationService CreateService(List<Transaction> transactions)
    {
        var txnRepo = new Mock<ITransactionRepository>();
        txnRepo.Setup(r => r.GetByUserOrFamilyAsync(UserId, It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(transactions);

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, FamilyId = null, Email = "u@x.com", CreatedAt = DateTime.UtcNow.AddYears(-1) });

        var config = new ConfigurationBuilder().Build();
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        return new AICategorizationService(
            txnRepo.Object,
            userRepo.Object,
            new Mock<IEntitlementService>().Object,
            new Mock<ICategorizationRuleService>().Object,
            new Mock<ITransactionEmbeddingService>().Object,
            new Mock<ICategoryRepository>().Object,
            new BuoyAIDbContext(options),
            config,
            new Mock<ILogger<AICategorizationService>>().Object,
            new AIModelConfiguration(config),
            TestGeminiUsage.Create());
    }

    private static Transaction Review(int id, DateTime date, bool uncategorized) => new()
    {
        Id = id,
        Date = date,
        NeedsReview = true,
        IsUserCategorized = false,
        MerchantName = "Merchant" + id,
        Amount = -10m,
        // uncategorized => no category; otherwise an AI-categorized-but-low-confidence item.
        CategoryId = uncategorized ? (int?)null : 5,
        AISuggestedCategoryId = uncategorized ? (int?)null : 5,
        AIConfidence = uncategorized ? null : 0.4,
    };

    private static List<Transaction> Sample()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var older = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-5);
        return new List<Transaction>
        {
            Review(1, thisMonth, uncategorized: true),
            Review(2, thisMonth, uncategorized: false),   // low-confidence, this month
            Review(3, older, uncategorized: true),        // uncategorized, older
            Review(4, older, uncategorized: false),       // low-confidence, older
            // Already handled — must be excluded from every scope + the counts.
            new() { Id = 5, Date = thisMonth, NeedsReview = false, IsUserCategorized = true, Amount = -1m, MerchantName = "done" },
        };
    }

    // Earmarking a transaction to a savings goal is the user's decision about it — the money
    // is tracked against the goal and excluded from budget totals, so there's no category
    // call left to make. It used to stay in the queue, where the only available action
    // (assign a category) silently reverses the earmark and destroys that work.
    private static Transaction Earmarked(int id, DateTime date) => new()
    {
        Id = id,
        Date = date,
        NeedsReview = true,
        IsUserCategorized = false,
        MerchantName = "Earmarked" + id,
        Amount = -10m,
        CategoryId = 5,
        AISuggestedCategoryId = 5,
        AIConfidence = 0.4,
        SavingsGoalId = 7,
    };

    [Fact]
    public async Task GetTransactionsNeedingReview_EarmarkedToSavingsGoal_IsExcluded()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var transactions = Sample();
        transactions.Add(Earmarked(6, thisMonth));

        var list = await CreateService(transactions)
            .GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.All);

        list.Select(t => t.Id).Should().NotContain(6);
    }

    [Fact]
    public async Task Stats_EarmarkedToSavingsGoal_IsNotCounted()
    {
        // The banner count and the queue must agree — otherwise it promises N to review and
        // the queue hands back fewer, stranding the user on a count that never reaches zero.
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var transactions = Sample();
        transactions.Add(Earmarked(6, thisMonth));

        var service = CreateService(transactions);
        var stats = await service.GetReviewStatisticsAsync(UserId);
        var list = await service.GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.All);

        stats.TotalNeedingReview.Should().Be(4);
        stats.TotalNeedingReview.Should().Be(list.Count);
    }

    [Fact]
    public async Task GetTransactionsNeedingReview_EarmarkRemoved_ReturnsToTheQueue()
    {
        // The exclusion is a filter, not a flag mutation — un-earmarking restores it to the
        // queue if it still needs review, with no extra bookkeeping.
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var unearmarked = Earmarked(6, thisMonth);
        unearmarked.SavingsGoalId = null;
        var transactions = Sample();
        transactions.Add(unearmarked);

        var list = await CreateService(transactions)
            .GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.All);

        list.Select(t => t.Id).Should().Contain(6);
    }

    [Fact]
    public async Task Stats_SplitsThisMonthAndOlder_UncategorizedIsSubsetOfTotal()
    {
        var stats = await CreateService(Sample()).GetReviewStatisticsAsync(UserId);

        stats.TotalNeedingReview.Should().Be(4);
        stats.ThisMonthCount.Should().Be(2);
        stats.OlderCount.Should().Be(2);
        (stats.ThisMonthCount + stats.OlderCount).Should().Be(stats.TotalNeedingReview);
        stats.UncategorizedCount.Should().Be(2);                  // #1 + #3 (subset of the 4)
        stats.LowConfidenceCount.Should().Be(2);                  // #2 + #4
        (stats.UncategorizedCount + stats.LowConfidenceCount).Should().Be(stats.TotalNeedingReview);
    }

    [Fact]
    public async Task NeedingReview_ThisMonthScope_ReturnsOnlyCurrentMonth()
    {
        var list = await CreateService(Sample()).GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.ThisMonth);
        list.Select(t => t.Id).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task NeedingReview_OlderScope_ReturnsOnlyBeforeThisMonth()
    {
        var list = await CreateService(Sample()).GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.Older);
        list.Select(t => t.Id).Should().BeEquivalentTo(new[] { 3, 4 });
    }

    [Fact]
    public async Task NeedingReview_AllScope_ReturnsEverythingNeedingReview()
    {
        var list = await CreateService(Sample()).GetTransactionsNeedingReviewAsync(UserId, limit: null, scope: ReviewScope.All);
        list.Select(t => t.Id).Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task NeedingReview_RespectsLimit()
    {
        var list = await CreateService(Sample()).GetTransactionsNeedingReviewAsync(UserId, limit: 1, scope: ReviewScope.All);
        list.Should().HaveCount(1);
    }

    // ── AI-eligible scope (Auto-Categorize workload) ────────────────────────────────────

    /// <summary>Plaid categorized it with low confidence; the AI never looked. Eligible.</summary>
    private static Transaction PlaidLowConfidence(int id, DateTime date) => new()
    {
        Id = id,
        Date = date,
        NeedsReview = true,
        IsUserCategorized = false,
        MerchantName = "PlaidGuess" + id,
        Amount = -10m,
        CategoryId = 7,
        AISuggestedCategoryId = null,
        AICategorizationDate = null,
    };

    /// <summary>The AI already tried and came back Uncategorized. Not eligible: re-sending
    /// the same row would not make the model more confident.</summary>
    private static Transaction AiGaveUp(int id, DateTime date) => new()
    {
        Id = id,
        Date = date,
        NeedsReview = true,
        IsUserCategorized = false,
        MerchantName = "Mystery" + id,
        Amount = -10m,
        CategoryId = (int)TransactionCategory.Uncategorized,
        AISuggestedCategoryId = (int)TransactionCategory.Uncategorized,
        AIConfidence = 0.2,
        AICategorizationDate = DateTime.UtcNow.AddMinutes(-5),
    };

    [Fact]
    public async Task GetReviewStatistics_AiEligible_CountsNeverAttemptedIncludingPlaidGuesses()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var txns = new List<Transaction>
        {
            Review(1, thisMonth, uncategorized: true),     // never attempted, uncategorized -> eligible
            Review(2, thisMonth, uncategorized: false),    // AI low-confidence suggestion -> not eligible
            PlaidLowConfidence(3, thisMonth),              // Plaid guess, AI never looked -> eligible
            AiGaveUp(4, thisMonth),                        // AI tried, came back Uncategorized -> not eligible
        };

        var stats = await CreateService(txns).GetReviewStatisticsAsync(UserId);

        stats.TotalNeedingReview.Should().Be(4);
        stats.UncategorizedCount.Should().Be(2, "rows 1 and 4 have no real category");
        stats.AiEligibleCount.Should().Be(2, "rows 1 and 3: never AI-attempted, Plaid guess included");
    }

    [Fact]
    public async Task GetReviewStatistics_AfterInconclusiveAiPass_NothingLeftToAutoCategorize()
    {
        var now = DateTime.UtcNow;
        var thisMonth = new DateTime(now.Year, now.Month, 15, 0, 0, 0, DateTimeKind.Utc);
        var txns = new List<Transaction> { AiGaveUp(1, thisMonth), AiGaveUp(2, thisMonth) };

        var stats = await CreateService(txns).GetReviewStatisticsAsync(UserId);

        stats.UncategorizedCount.Should().Be(2);
        stats.AiEligibleCount.Should().Be(0, "the button must not re-offer rows the AI already gave up on");
    }
}
