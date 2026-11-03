using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Transaction;
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
/// The manual Auto-Categorize button is backlog-only and window-bound: it offers (and sends)
/// only never-attempted review rows from this month and last, the same window the ingest
/// pipeline auto-queues. Review stats also report the background queue depth.
/// </summary>
public class AICategorizationReviewWindowTests
{
    private const int UserId = 4;
    private readonly BuoyAIDbContext _db;
    private readonly List<Transaction> _rows = new();
    private readonly Mock<ITransactionRepository> _txnRepo = new();

    public AICategorizationReviewWindowTests()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new BuoyAIDbContext(options);
        _txnRepo.Setup(r => r.GetByUserOrFamilyAsync(UserId, It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(() => _rows);
        _txnRepo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>())).ReturnsAsync((Transaction t) => t);
    }

    private AICategorizationService CreateService(int? windowMonths = null)
    {
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, Email = "u@x.com", CreatedAt = DateTime.UtcNow.AddYears(-1) });
        var settings = new Dictionary<string, string?>();
        if (windowMonths.HasValue)
            settings[TransactionIngestCategorizationService.WindowMonthsKey] = windowMonths.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new AICategorizationService(
            _txnRepo.Object,
            userRepo.Object,
            new Mock<IEntitlementService>().Object,
            new Mock<ICategorizationRuleService>().Object,
            new Mock<ITransactionEmbeddingService>().Object,
            new Mock<ICategoryRepository>().Object,
            _db,
            config,
            new Mock<ILogger<AICategorizationService>>().Object,
            new AIModelConfiguration(config),
            TestGeminiUsage.Create());
    }

    private Transaction Pending(int id, DateTime date, bool attempted = false)
    {
        var t = new Transaction
        {
            Id = id,
            Date = date,
            NeedsReview = true,
            IsUserCategorized = false,
            MerchantName = "Merchant" + id,
            Description = "Merchant" + id,
            Amount = -12m,
            AISuggestedCategoryId = attempted ? 5 : null,
            AICategorizationDate = attempted ? date : null,
        };
        _rows.Add(t);
        return t;
    }

    private static DateTime ThisMonth => new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 3, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime BeforeWindow => TransactionIngestCategorizationService.WindowStart(DateTime.UtcNow, 1).AddDays(-2);

    [Fact]
    public async Task GetReviewStatistics_AiEligible_ExcludesRowsBeforeWindow()
    {
        Pending(1, ThisMonth);
        Pending(2, BeforeWindow);

        var stats = await CreateService().GetReviewStatisticsAsync(UserId);

        stats.AiEligibleCount.Should().Be(1);
        stats.TotalNeedingReview.Should().Be(2, "the backlog still needs review, it is just not auto-sent");
    }

    [Fact]
    public async Task GetReviewStatistics_AiEligible_ExcludesAttemptedRows()
    {
        Pending(1, ThisMonth);
        Pending(2, ThisMonth, attempted: true);

        var stats = await CreateService().GetReviewStatisticsAsync(UserId);

        stats.AiEligibleCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReviewStatistics_WindowIsConfigurable()
    {
        Pending(1, BeforeWindow);

        var stats = await CreateService(windowMonths: 3).GetReviewStatisticsAsync(UserId);

        stats.AiEligibleCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReviewStatistics_ReportsBackgroundQueueDepth()
    {
        _db.AICategorizationQueue.AddRange(
            new AICategorizationQueueItem { Id = 1, TransactionId = 1, UserId = UserId, Status = AICategorizationQueueStatus.Pending },
            new AICategorizationQueueItem { Id = 2, TransactionId = 2, UserId = UserId, Status = AICategorizationQueueStatus.Pending },
            new AICategorizationQueueItem { Id = 3, TransactionId = 3, UserId = UserId, Status = AICategorizationQueueStatus.Completed },
            new AICategorizationQueueItem { Id = 4, TransactionId = 4, UserId = 99, Status = AICategorizationQueueStatus.Pending });
        await _db.SaveChangesAsync();

        var stats = await CreateService().GetReviewStatisticsAsync(UserId);

        stats.QueuedForAiCount.Should().Be(2);
    }

    [Fact]
    public async Task BatchCategorize_Untargeted_SendsOnlyRowsInsideWindow()
    {
        var recent = Pending(1, ThisMonth);
        var old = Pending(2, BeforeWindow);

        var response = await CreateService().BatchCategorizeAsync(UserId, new AICategorizationRequest { MaxTransactions = 10 });

        response.TotalProcessed.Should().Be(1);
        recent.AICategorizationDate.Should().NotBeNull();
        old.AICategorizationDate.Should().BeNull("history is never auto-sent; the user can target it explicitly");
    }

    [Fact]
    public async Task BatchCategorize_TargetedIds_IgnoreWindow()
    {
        var old = Pending(2, BeforeWindow);

        var response = await CreateService().BatchCategorizeAsync(UserId,
            new AICategorizationRequest { MaxTransactions = 10, TransactionIds = new List<int> { 2 } });

        response.TotalProcessed.Should().Be(1);
        old.AICategorizationDate.Should().NotBeNull();
    }
}
