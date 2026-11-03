using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The background drainer's per-pass work: consent gating, draining to empty, and retrying
/// failed items with a cap.
/// </summary>
public class AICategorizationQueueWorkerTests
{
    private const int UserId = 5;
    private readonly BuoyAIDbContext _db;
    private readonly Mock<IAICategorizationService> _categorization = new();

    public AICategorizationQueueWorkerTests()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new BuoyAIDbContext(options);
    }

    private AICategorizationQueueWorker CreateWorker() =>
        new(_db, _categorization.Object, NullLogger<AICategorizationQueueWorker>.Instance);

    private void SeedUser(DateTime? consentGranted, DateTime? consentWithdrawn = null)
    {
        _db.Users.Add(new User
        {
            Id = UserId,
            Email = "u@x.com",
            AiConsentGrantedAt = consentGranted,
            AiConsentWithdrawnAt = consentWithdrawn,
            AiConsentVersion = consentGranted != null ? AiConsent.CurrentVersion : null,
        });
        _db.SaveChanges();
    }

    private void SeedQueue(int count, AICategorizationQueueStatus status = AICategorizationQueueStatus.Pending,
        int retryCount = 0, DateTime? processedAt = null)
    {
        var nextId = _db.AICategorizationQueue.Count() + 1;
        for (var i = 0; i < count; i++)
        {
            _db.AICategorizationQueue.Add(new AICategorizationQueueItem
            {
                Id = nextId + i,
                TransactionId = 1000 + nextId + i,
                UserId = UserId,
                Status = status,
                RetryCount = retryCount,
                ProcessedAt = processedAt,
            });
        }
        _db.SaveChanges();
    }

    [Fact]
    public async Task DrainUserAsync_NoConsent_MarksPendingSkippedAndCallsNoAi()
    {
        SeedUser(consentGranted: null);
        SeedQueue(3);

        var processed = await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        processed.Should().Be(0);
        _db.AICategorizationQueue.Should().OnlyContain(q =>
            q.Status == AICategorizationQueueStatus.Skipped && q.ErrorMessage == AICategorizationQueueWorker.NoConsentReason);
        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DrainUserAsync_ConsentWithdrawnAfterGrant_TreatedAsNoConsent()
    {
        SeedUser(consentGranted: DateTime.UtcNow.AddDays(-10), consentWithdrawn: DateTime.UtcNow.AddDays(-1));
        SeedQueue(1);

        await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DrainUserAsync_ConsentRegrantedAfterWithdrawal_Drains()
    {
        SeedUser(consentGranted: DateTime.UtcNow.AddDays(-1), consentWithdrawn: DateTime.UtcNow.AddDays(-10));
        var remaining = 1;
        _categorization.Setup(c => c.GetQueuedTransactionCountAsync(UserId)).ReturnsAsync(() => remaining);
        _categorization.Setup(c => c.ProcessCategorizationQueueAsync(UserId, AICategorizationQueueWorker.BatchSize))
            .ReturnsAsync(() => { remaining = 0; return 1; });

        var processed = await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        processed.Should().Be(1);
    }

    [Fact]
    public async Task DrainUserAsync_WithConsent_ProcessesBatchesUntilQueueEmpty()
    {
        SeedUser(consentGranted: DateTime.UtcNow.AddDays(-1));
        var remaining = 45;
        _categorization.Setup(c => c.GetQueuedTransactionCountAsync(UserId)).ReturnsAsync(() => remaining);
        _categorization.Setup(c => c.ProcessCategorizationQueueAsync(UserId, AICategorizationQueueWorker.BatchSize))
            .ReturnsAsync(() =>
            {
                var took = Math.Min(remaining, AICategorizationQueueWorker.BatchSize);
                remaining -= took;
                return took;
            });

        var processed = await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        processed.Should().Be(45);
        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(UserId, AICategorizationQueueWorker.BatchSize), Times.Exactly(3));
    }

    [Fact]
    public async Task DrainUserAsync_GrantFromBeforeTheCurrentDisclosure_DoesNotCallGemini()
    {
        // A grant recorded before consent was versioned (implied on Android/web, or given on the
        // old partial sheet) no longer counts until the user sees the current sheet.
        _db.Users.Add(new User
        {
            Id = UserId,
            Email = "u@x.com",
            AiConsentGrantedAt = DateTime.UtcNow.AddDays(-30),
            AiConsentVersion = null,
        });
        _db.SaveChanges();

        var processed = await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        processed.Should().Be(0);
        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DrainUserAsync_EmptyQueue_DoesNotCallProcess()
    {
        SeedUser(consentGranted: DateTime.UtcNow.AddDays(-1));
        _categorization.Setup(c => c.GetQueuedTransactionCountAsync(UserId)).ReturnsAsync(0);

        await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task DrainUserAsync_CapsBatchesPerPass()
    {
        SeedUser(consentGranted: DateTime.UtcNow.AddDays(-1));
        // Remaining never reaches zero (simulates a batch that keeps skipping): the pass must end.
        _categorization.Setup(c => c.GetQueuedTransactionCountAsync(UserId)).ReturnsAsync(999);
        _categorization.Setup(c => c.ProcessCategorizationQueueAsync(UserId, It.IsAny<int>())).ReturnsAsync(0);

        await CreateWorker().DrainUserAsync(UserId, CancellationToken.None);

        _categorization.Verify(c => c.ProcessCategorizationQueueAsync(UserId, It.IsAny<int>()),
            Times.Exactly(AICategorizationQueueWorker.MaxBatchesPerUserPerPass));
    }

    [Fact]
    public async Task GetUsersWithPendingItemsAsync_ReturnsDistinctUsersWithPendingOnly()
    {
        SeedQueue(2);
        SeedQueue(1, AICategorizationQueueStatus.Completed);

        var users = await CreateWorker().GetUsersWithPendingItemsAsync(CancellationToken.None);

        users.Should().Equal(UserId);
    }

    [Fact]
    public async Task RequeueFailedAsync_RetriesFailedItemsPastBackoff_UpToCap()
    {
        var stale = DateTime.UtcNow - AICategorizationQueueWorker.RetryBackoff - TimeSpan.FromMinutes(1);
        SeedQueue(1, AICategorizationQueueStatus.Failed, retryCount: 1, processedAt: stale);
        SeedQueue(1, AICategorizationQueueStatus.Failed, retryCount: AICategorizationQueueWorker.MaxRetries, processedAt: stale);
        SeedQueue(1, AICategorizationQueueStatus.Failed, retryCount: 1, processedAt: DateTime.UtcNow);

        var requeued = await CreateWorker().RequeueFailedAsync(CancellationToken.None);

        requeued.Should().Be(1);
        _db.AICategorizationQueue.Count(q => q.Status == AICategorizationQueueStatus.Pending).Should().Be(1);
        _db.AICategorizationQueue.Count(q => q.Status == AICategorizationQueueStatus.Failed).Should().Be(2);
    }
}
