using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// The ingest-time policy: rules on every row, Plaid VERY_HIGH trusted until proven wrong
/// per merchant, everything else queued, and only inside the this-month-and-last window.
/// </summary>
public class TransactionIngestCategorizationServiceTests
{
    private const int UserId = 7;
    private const int AccountId = 70;

    private readonly Mock<ICategorizationRuleService> _rules = new();
    private readonly Mock<IAICategorizationService> _categorization = new();
    private readonly Mock<IAICategorizationQueueSignal> _signal = new();
    private readonly List<int> _queued = new();
    private readonly BuoyAIDbContext _db;

    public TransactionIngestCategorizationServiceTests()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        _db = new BuoyAIDbContext(options);
        _db.Accounts.Add(new Account { Id = AccountId, UserId = UserId, Name = "Checking" });
        _db.SaveChanges();

        // No rules by default: everything comes back unmatched.
        _rules.Setup(r => r.ApplyRulesToTransactionIdsAsync(UserId, It.IsAny<List<int>>()))
            .ReturnsAsync((int _, List<int> ids) => (0, ids));
        _categorization.Setup(c => c.QueueTransactionsForCategorizationAsync(UserId, It.IsAny<List<int>>()))
            .Callback<int, List<int>>((_, ids) => _queued.AddRange(ids))
            .Returns(Task.CompletedTask);
    }

    private TransactionIngestCategorizationService CreateService(int? windowMonths = null, BuoyAI.Core.Interfaces.IAiProvider? ai = null)
    {
        var settings = new Dictionary<string, string?>();
        if (windowMonths.HasValue)
            settings[TransactionIngestCategorizationService.WindowMonthsKey] = windowMonths.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new TransactionIngestCategorizationService(
            _db, _rules.Object, _categorization.Object, _signal.Object, config,
            NullLogger<TransactionIngestCategorizationService>.Instance, ai: ai);
    }

    private static Transaction NewRow(int id, string merchant, string plaidConfidence, DateTime? date = null) => new()
    {
        Id = id,
        AccountId = AccountId,
        MerchantName = merchant,
        Description = merchant,
        Amount = -20m,
        Date = date ?? DateTime.UtcNow,
        PlaidCategoryConfidence = plaidConfidence,
        // Mirrors PlaidService.CreateTransactionFromPlaid: only VERY_HIGH skips review.
        NeedsReview = plaidConfidence != "VERY_HIGH",
        CategoryId = 10,
    };

    private void SeedHistory(string merchant, params (int categoryId, string plaidConfidence)[] rows)
    {
        var id = 900;
        foreach (var (categoryId, confidence) in rows)
        {
            _db.Transactions.Add(new Transaction
            {
                Id = id++,
                AccountId = AccountId,
                MerchantName = merchant,
                Description = merchant,
                Amount = -15m,
                Date = DateTime.UtcNow.AddMonths(-2),
                IsUserCategorized = true,
                CategoryId = categoryId,
                PlaidCategoryConfidence = confidence,
            });
        }
        _db.SaveChanges();
    }

    [Fact]
    public async Task ProcessIngestedAsync_RunsRulesOnEveryRow_IncludingVeryHigh()
    {
        var rows = new[] { NewRow(1, "McDonald's", "VERY_HIGH"), NewRow(2, "Unknown Co", "LOW") };

        await CreateService().ProcessIngestedAsync(rows);

        _rules.Verify(r => r.ApplyRulesToTransactionIdsAsync(
            UserId, It.Is<List<int>>(ids => ids.Contains(1) && ids.Contains(2))), Times.Once);
    }

    [Fact]
    public async Task ProcessIngestedAsync_RuleMatchedRows_AreNotQueued()
    {
        var rows = new[] { NewRow(1, "McDonald's", "VERY_HIGH"), NewRow(2, "Unknown Co", "LOW") };
        _rules.Setup(r => r.ApplyRulesToTransactionIdsAsync(UserId, It.IsAny<List<int>>()))
            .ReturnsAsync((1, new List<int> { 2 }));

        var result = await CreateService().ProcessIngestedAsync(rows);

        result.RulesApplied.Should().Be(1);
        _queued.Should().Equal(2);
    }

    [Fact]
    public async Task ProcessIngestedAsync_PlaidUnsure_IsQueued()
    {
        var result = await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "HIGH") });

        _queued.Should().Equal(1);
        result.QueuedForAi.Should().Be(1);
        result.QueuedTransactionIds.Should().Equal(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_AiOff_StillQueues_SoRowsDrainOnceAProviderIsOn()
    {
        var off = new BuoyAI.Infrastructure.Services.Ai.NullAiProvider();

        var result = await CreateService(ai: off).ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "HIGH") });

        _queued.Should().Equal(1);
        result.QueuedForAi.Should().Be(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_VeryHighUnknownMerchant_KeptFromPlaid()
    {
        var result = await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Dillons", "VERY_HIGH") });

        _queued.Should().BeEmpty();
        result.AcceptedFromPlaid.Should().Be(1);
        _signal.Verify(s => s.Notify(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessIngestedAsync_VeryHighMerchantUserOverrodeBefore_IsQueued()
    {
        // The user recategorized a VERY_HIGH McDonald's row before (their paycheck, not lunch).
        SeedHistory("McDonald's", (1, "VERY_HIGH"));

        var result = await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "McDonald's", "VERY_HIGH") });

        _queued.Should().Equal(1);
        result.AcceptedFromPlaid.Should().Be(0);
    }

    [Fact]
    public async Task ProcessIngestedAsync_VeryHighSplitMerchant_IsQueued()
    {
        // Amazon filed as both groceries (10) and household (13); Plaid's confidence is no evidence.
        SeedHistory("Amazon", (10, "HIGH"), (13, "HIGH"));

        await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Amazon", "VERY_HIGH") });

        _queued.Should().Equal(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_VeryHighStableMerchant_KeptFromPlaid()
    {
        // Consistently groceries, never a VERY_HIGH override: Plaid stays trusted.
        SeedHistory("Dillons", (10, "HIGH"), (10, "LOW"));

        var result = await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Dillons", "VERY_HIGH") });

        _queued.Should().BeEmpty();
        result.AcceptedFromPlaid.Should().Be(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_MerchantMatchIsCaseInsensitive()
    {
        SeedHistory("amazon", (10, "HIGH"), (13, "HIGH"));

        await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "AMAZON", "VERY_HIGH") });

        _queued.Should().Equal(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_RowBeforeWindow_NotQueuedEvenIfUnsure()
    {
        var old = TransactionIngestCategorizationService.WindowStart(DateTime.UtcNow, 1).AddDays(-1);

        var result = await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "LOW", old) });

        _queued.Should().BeEmpty();
        result.OutsideWindow.Should().Be(1);
        result.QueuedForAi.Should().Be(0);
    }

    [Fact]
    public async Task ProcessIngestedAsync_RowOnWindowStart_IsQueued()
    {
        var start = TransactionIngestCategorizationService.WindowStart(DateTime.UtcNow, 1);

        await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "LOW", start) });

        _queued.Should().Equal(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_WindowIsConfigurable()
    {
        var threeMonthsBack = TransactionIngestCategorizationService.WindowStart(DateTime.UtcNow, 3).AddDays(3);

        await CreateService(windowMonths: 3).ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "LOW", threeMonthsBack) });

        _queued.Should().Equal(1);
    }

    [Fact]
    public async Task ProcessIngestedAsync_SignalsDrainerOncePerUserWhenQueued()
    {
        await CreateService().ProcessIngestedAsync(new[] { NewRow(1, "A", "LOW"), NewRow(2, "B", "LOW") });

        _signal.Verify(s => s.Notify(UserId), Times.Once);
    }

    [Fact]
    public async Task ProcessIngestedAsync_UnknownAccount_IsIgnored()
    {
        var orphan = NewRow(1, "Corner Store", "LOW");
        orphan.AccountId = 999;

        var result = await CreateService().ProcessIngestedAsync(new[] { orphan });

        _queued.Should().BeEmpty();
        result.QueuedForAi.Should().Be(0);
        _rules.Verify(r => r.ApplyRulesToTransactionIdsAsync(It.IsAny<int>(), It.IsAny<List<int>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessIngestedAsync_RuleServiceThrows_DoesNotPropagate()
    {
        _rules.Setup(r => r.ApplyRulesToTransactionIdsAsync(UserId, It.IsAny<List<int>>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => CreateService().ProcessIngestedAsync(new[] { NewRow(1, "Corner Store", "LOW") });

        await act.Should().NotThrowAsync();
        _queued.Should().BeEmpty();
    }

    [Fact]
    public void WindowStart_IsFirstOfMonthMinusPreviousMonths()
    {
        var now = new DateTime(2026, 9, 17, 13, 0, 0, DateTimeKind.Utc);

        TransactionIngestCategorizationService.WindowStart(now, 1)
            .Should().Be(new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
        TransactionIngestCategorizationService.WindowStart(now, 0)
            .Should().Be(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
    }
}
