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
/// ProcessCategorizationQueueAsync: which queued rows are actually sent on (attempt-once,
/// snooze, earmark) and the split-merchant gate that keeps the deterministic pattern shortcut
/// from committing on a merchant the user files under several categories.
///
/// No Gemini key is configured, so the model phase resolves through the keyword fallback at
/// confidence 0.3. That is enough to tell "the pattern shortcut fired" (0.95) from "the row
/// went to the model" (0.3).
/// </summary>
public class AICategorizationQueueProcessingTests
{
    private const int UserId = 3;
    private const int AccountId = 30;

    private readonly BuoyAIDbContext _db;
    private readonly Mock<ITransactionRepository> _txnRepo = new();
    private readonly List<Transaction> _history = new();

    public AICategorizationQueueProcessingTests()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new BuoyAIDbContext(options);
        _db.Accounts.Add(new Account { Id = AccountId, UserId = UserId, Name = "Checking" });
        _db.SaveChanges();

        _txnRepo.Setup(r => r.GetByUserOrFamilyAsync(UserId, It.IsAny<int?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(() => _history);
        _txnRepo.Setup(r => r.UpdateAsync(It.IsAny<Transaction>()))
            .ReturnsAsync((Transaction t) => t);
    }

    private AICategorizationService CreateService()
    {
        var userRepo = new Mock<IUserRepository>();
        userRepo.Setup(r => r.GetByIdAsync(UserId))
            .ReturnsAsync(new User { Id = UserId, Email = "u@x.com", CreatedAt = DateTime.UtcNow.AddYears(-1) });
        var config = new ConfigurationBuilder().Build();
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

    private Transaction Queue(int id, string merchant, Action<Transaction>? mutate = null)
    {
        var t = new Transaction
        {
            Id = id,
            AccountId = AccountId,
            MerchantName = merchant,
            Description = merchant,
            Amount = -25m,
            Date = DateTime.UtcNow,
            NeedsReview = true,
        };
        mutate?.Invoke(t);
        _db.Transactions.Add(t);
        _db.AICategorizationQueue.Add(new AICategorizationQueueItem
        {
            Id = id,
            TransactionId = id,
            UserId = UserId,
            Status = AICategorizationQueueStatus.Pending,
        });
        _db.SaveChanges();
        return t;
    }

    private void Remember(string merchant, params int[] categoryIds)
    {
        foreach (var categoryId in categoryIds)
        {
            _history.Add(new Transaction
            {
                Id = 500 + _history.Count,
                AccountId = AccountId,
                MerchantName = merchant,
                Amount = -10m,
                Date = DateTime.UtcNow.AddDays(-_history.Count - 1),
                IsUserCategorized = true,
                CategoryId = categoryId,
            });
        }
    }

    private AICategorizationQueueItem Item(int id) => _db.AICategorizationQueue.Single(q => q.Id == id);

    [Fact]
    public async Task ProcessQueue_AlreadyAttemptedRow_IsSkippedNotResent()
    {
        Queue(1, "Corner Store", t => t.AICategorizationDate = DateTime.UtcNow.AddHours(-1));

        var processed = await CreateService().ProcessCategorizationQueueAsync(UserId);

        processed.Should().Be(0);
        Item(1).Status.Should().Be(AICategorizationQueueStatus.Skipped);
    }

    [Fact]
    public async Task ProcessQueue_SnoozedRow_IsSkipped()
    {
        Queue(1, "Corner Store", t => t.SkipUntil = DateTime.UtcNow.AddDays(2));

        await CreateService().ProcessCategorizationQueueAsync(UserId);

        Item(1).Status.Should().Be(AICategorizationQueueStatus.Skipped);
    }

    [Fact]
    public async Task ProcessQueue_EarmarkedRow_IsSkipped()
    {
        Queue(1, "Corner Store", t => t.SavingsGoalId = 42);

        await CreateService().ProcessCategorizationQueueAsync(UserId);

        Item(1).Status.Should().Be(AICategorizationQueueStatus.Skipped);
    }

    [Fact]
    public async Task ProcessQueue_UserCategorizedRow_IsSkipped()
    {
        Queue(1, "Corner Store", t => t.IsUserCategorized = true);

        await CreateService().ProcessCategorizationQueueAsync(UserId);

        Item(1).Status.Should().Be(AICategorizationQueueStatus.Skipped);
    }

    [Fact]
    public async Task ProcessQueue_FreshRow_GetsSuggestionAndCompletes()
    {
        var row = Queue(1, "Corner Store");

        var processed = await CreateService().ProcessCategorizationQueueAsync(UserId);

        processed.Should().Be(1);
        Item(1).Status.Should().Be(AICategorizationQueueStatus.Completed);
        row.AISuggestedCategoryId.Should().NotBeNull();
        row.AICategorizationDate.Should().NotBeNull();
    }

    [Fact]
    public async Task ProcessQueue_StableMerchantHabit_AppliedWithoutModel()
    {
        Remember("Dillons", 10, 10, 10);
        var row = Queue(1, "Dillons");

        await CreateService().ProcessCategorizationQueueAsync(UserId);

        row.AISuggestedCategoryId.Should().Be(10);
        row.AIConfidence.Should().Be(0.95);
        row.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessQueue_SplitMerchant_BypassesHabitShortcut()
    {
        // Amazon as groceries (10) and household (13): no single habit to apply, so the row
        // must reach the model phase (keyword fallback here) instead of committing at 0.95.
        Remember("Amazon", 10, 13, 10);
        var row = Queue(1, "Amazon");

        await CreateService().ProcessCategorizationQueueAsync(UserId);

        row.AIConfidence.Should().Be(0.3);
        row.NeedsReview.Should().BeTrue();
    }
}
