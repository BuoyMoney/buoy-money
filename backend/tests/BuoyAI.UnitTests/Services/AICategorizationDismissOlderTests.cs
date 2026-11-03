using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
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
/// "Forget the backlog": permanently clears the review flag on everything dated before the
/// current month. Destructive and undoable only by hand, so the tests pin down exactly which
/// rows it may touch.
/// </summary>
public class AICategorizationDismissOlderTests
{
    private const int UserId = 2;
    private const int OtherUserId = 99;

    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime MonthStart =
        new(Now.Year, Now.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Mock<ITransactionRepository> _lastRepo = null!;

    private static AICategorizationService CreateService(List<Transaction> transactions)
    {
        var txnRepo = new Mock<ITransactionRepository>();
        txnRepo.Setup(r => r.GetByUserOrFamilyAsync(UserId, It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(transactions);
        txnRepo.Setup(r => r.ClearNeedsReviewAsync(It.IsAny<IEnumerable<int>>()))
            .ReturnsAsync((IEnumerable<int> ids) => ids.Count());
        _lastRepo = txnRepo;

        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, FamilyId = null, Email = "u@x.com", CreatedAt = Now.AddYears(-1) });

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

    private static Transaction Pending(int id, DateTime date) => new()
    {
        Id = id,
        Date = date,
        NeedsReview = true,
        IsUserCategorized = false,
        MerchantName = "Merchant" + id,
        Amount = -10m,
    };

    private static List<int> DismissedIds() =>
        _lastRepo.Invocations
            .Where(i => i.Method.Name == nameof(ITransactionRepository.ClearNeedsReviewAsync))
            .SelectMany(i => ((IEnumerable<int>)i.Arguments[0]).ToList())
            .ToList();

    [Fact]
    public async Task DismissOlderReviewsAsync_ClearsOnlyTransactionsBeforeThisMonth()
    {
        var older = Pending(1, MonthStart.AddDays(-1));
        var muchOlder = Pending(2, MonthStart.AddMonths(-8));
        var thisMonth = Pending(3, MonthStart.AddHours(1));

        var service = CreateService(new List<Transaction> { older, muchOlder, thisMonth });

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(2);
        DismissedIds().Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_NeverTouchesThisMonth()
    {
        // The banner offers this as "forget the N from earlier months". Clearing the current
        // month would silently destroy the queue the user is actively working through.
        var thisMonth = Pending(1, MonthStart);
        var alsoThisMonth = Pending(2, Now);

        var service = CreateService(new List<Transaction> { thisMonth, alsoThisMonth });

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(0);
        DismissedIds().Should().BeEmpty();
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_SkipsEarmarkedTransactions()
    {
        // A savings earmark IS the user's decision; it is excluded from the review queue, so
        // dismissing it would clear a flag the queue never surfaced.
        var earmarked = Pending(1, MonthStart.AddMonths(-2));
        earmarked.SavingsGoalId = 42;

        var service = CreateService(new List<Transaction> { earmarked });

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(0);
        DismissedIds().Should().BeEmpty();
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_SkipsAlreadyUserCategorizedTransactions()
    {
        var settled = Pending(1, MonthStart.AddMonths(-2));
        settled.IsUserCategorized = true;

        var alreadyOffQueue = Pending(2, MonthStart.AddMonths(-2));
        alreadyOffQueue.NeedsReview = false;

        var service = CreateService(new List<Transaction> { settled, alreadyOffQueue });

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(0);
        DismissedIds().Should().BeEmpty();
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_IncludesSnoozedTransactionsWhoseSkipHasElapsed()
    {
        // An elapsed SkipUntil is back in the queue, so the backlog action must reach it.
        var elapsed = Pending(1, MonthStart.AddMonths(-1));
        elapsed.SkipUntil = Now.AddDays(-3);

        // A live snooze is not in the queue and stays out of it.
        var stillSnoozed = Pending(2, MonthStart.AddMonths(-1));
        stillSnoozed.SkipUntil = Now.AddDays(3);

        var service = CreateService(new List<Transaction> { elapsed, stillSnoozed });

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(1);
        DismissedIds().Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_ScopesToTheCallersOwnTransactions()
    {
        // Ownership is enforced by only ever reading through GetByUserOrFamilyAsync. A user
        // whose scoped query returns nothing dismisses nothing, even when other users have a
        // backlog.
        var service = CreateService(new List<Transaction> { Pending(1, MonthStart.AddMonths(-3)) });

        var dismissed = await service.DismissOlderReviewsAsync(OtherUserId);

        dismissed.Should().Be(0);
        DismissedIds().Should().BeEmpty();
        _lastRepo.Verify(
            r => r.GetByUserOrFamilyAsync(OtherUserId, It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()),
            Times.Once);
    }

    [Fact]
    public async Task DismissOlderReviewsAsync_WithEmptyBacklog_DoesNotHitTheDatabase()
    {
        var service = CreateService(new List<Transaction>());

        var dismissed = await service.DismissOlderReviewsAsync(UserId);

        dismissed.Should().Be(0);
        _lastRepo.Verify(r => r.ClearNeedsReviewAsync(It.IsAny<IEnumerable<int>>()), Times.Never);
    }
}
