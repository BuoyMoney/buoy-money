using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class SyncThrottleTests
{
    private static readonly DateTime Now = new(2025, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan SixHours = TimeSpan.FromHours(6);

    private static PlaidConnection Connection(int id = 1, DateTime? lastAttempt = null, DateTime? lastSync = null) =>
        new() { Id = id, LastSyncAttemptAt = lastAttempt, LastTransactionSyncAt = lastSync };

    [Fact]
    public void ZeroInterval_IsAlwaysDue_WhateverTheLastAttempt() =>
        SyncThrottle.IsDue(Connection(lastAttempt: Now), TimeSpan.Zero, TimeSpan.FromHours(1), Now).Should().BeTrue();

    [Fact]
    public void NeverAttempted_IsDue() =>
        SyncThrottle.IsDue(Connection(), SixHours, TimeSpan.Zero, Now).Should().BeTrue();

    [Fact]
    public void NotDue_BeforeTheIntervalHasPassed()
    {
        var c = Connection(lastAttempt: Now.AddHours(-5).AddMinutes(-59));

        SyncThrottle.IsDue(c, SixHours, TimeSpan.Zero, Now).Should().BeFalse();
    }

    [Fact]
    public void Due_OnceTheIntervalHasPassed()
    {
        SyncThrottle.IsDue(Connection(lastAttempt: Now.AddHours(-6)), SixHours, TimeSpan.Zero, Now).Should().BeTrue();
        SyncThrottle.IsDue(Connection(lastAttempt: Now.AddHours(-7)), SixHours, TimeSpan.Zero, Now).Should().BeTrue();
    }

    [Fact]
    public void AFailedAttemptCounts_SoABrokenConnectionIsNotRetriedEveryCycle()
    {
        // The last good sync was a day ago, but the attempt 10 minutes ago failed.
        var c = Connection(lastAttempt: Now.AddMinutes(-10), lastSync: Now.AddDays(-1));

        SyncThrottle.IsDue(c, SixHours, TimeSpan.Zero, Now).Should().BeFalse();
    }

    [Fact]
    public void WithoutAnAttemptTime_TheLastGoodSyncIsUsed() =>
        SyncThrottle.IsDue(Connection(lastSync: Now.AddHours(-2)), SixHours, TimeSpan.Zero, Now).Should().BeFalse();

    [Fact]
    public void Jitter_DelaysTheDueTime_ByThisConnectionsShare()
    {
        var max = TimeSpan.FromHours(1);
        var c = Connection(id: 7, lastAttempt: Now.AddHours(-6));
        var jitter = SyncThrottle.JitterFor(7, max);
        jitter.Should().BeGreaterThan(TimeSpan.Zero);

        SyncThrottle.IsDue(c, SixHours, max, Now).Should().BeFalse();
        SyncThrottle.IsDue(c, SixHours, max, Now + jitter).Should().BeTrue();
    }

    [Fact]
    public void Jitter_IsStablePerConnection_BoundedByTheMaximum_AndDiffersBetweenConnections()
    {
        var max = TimeSpan.FromMinutes(60);

        var shares = Enumerable.Range(1, 200).Select(id => SyncThrottle.JitterFor(id, max)).ToList();

        SyncThrottle.JitterFor(42, max).Should().Be(SyncThrottle.JitterFor(42, max));
        shares.Should().OnlyContain(j => j >= TimeSpan.Zero && j < max);
        shares.Distinct().Count().Should().BeGreaterThan(150);
        // Spread out, not clumped in one end of the hour.
        shares.Count(j => j < TimeSpan.FromMinutes(30)).Should().BeInRange(60, 140);
    }

    [Fact]
    public void Jitter_ZeroMaximum_IsZero() =>
        SyncThrottle.JitterFor(5, TimeSpan.Zero).Should().Be(TimeSpan.Zero);

    // ── what the loop picks ─────────────────────────────────────────────

    private static IBankAggregatorRegistry Registry(TimeSpan simpleFinInterval)
    {
        var plaid = new Mock<IBankAggregator>();
        plaid.SetupGet(a => a.Provider).Returns(BankProvider.Plaid);
        plaid.SetupGet(a => a.MinimumSyncInterval).Returns(TimeSpan.Zero);
        plaid.SetupGet(a => a.SyncJitter).Returns(TimeSpan.Zero);

        var simpleFin = new Mock<IBankAggregator>();
        simpleFin.SetupGet(a => a.Provider).Returns(BankProvider.SimpleFin);
        simpleFin.SetupGet(a => a.MinimumSyncInterval).Returns(simpleFinInterval);
        simpleFin.SetupGet(a => a.SyncJitter).Returns(TimeSpan.Zero);

        return new BankAggregatorRegistry(new[] { plaid.Object, simpleFin.Object });
    }

    [Fact]
    public void TheLoop_SyncsPlaidEveryCycle_ButLeavesSimpleFinAloneUntilItsIntervalPasses()
    {
        var plaid = new PlaidConnection { Id = 1, Provider = BankProvider.Plaid, LastSyncAttemptAt = Now.AddMinutes(-10) };
        var recentSimpleFin = new PlaidConnection { Id = 2, Provider = BankProvider.SimpleFin, LastSyncAttemptAt = Now.AddMinutes(-10) };
        var dueSimpleFin = new PlaidConnection { Id = 3, Provider = BankProvider.SimpleFin, LastSyncAttemptAt = Now.AddHours(-7) };

        var due = PlaidTransactionSyncService.SelectDueConnections(
            new[] { plaid, recentSimpleFin, dueSimpleFin }, Registry(SixHours), Now);

        due.Select(c => c.Id).Should().Equal(1, 3);
    }

    private static Mock<IBankAggregator> BridgeAggregator(TimeSpan interval)
    {
        var bridge = new Mock<IBankAggregator>();
        bridge.SetupGet(a => a.Provider).Returns(BankProvider.Bridge);
        bridge.SetupGet(a => a.MinimumSyncInterval).Returns(interval);
        bridge.SetupGet(a => a.SyncJitter).Returns(TimeSpan.Zero);
        return bridge;
    }

    [Fact]
    public async Task BridgeReconcile_IsSkippedEntirely_WhenBridgeIsNotRegistered()
    {
        var repo = new Mock<BuoyAI.Core.Interfaces.IPlaidConnectionRepository>();

        await PlaidTransactionSyncService.ReconcileBridgeConnectionsAsync(
            repo.Object, Registry(SixHours), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Now);

        repo.Verify(r => r.GetBridgeConnectionsNeedingReconcileAsync(), Times.Never);
    }

    [Fact]
    public async Task BridgeReconcile_PassesOnlyTheConnectionsDueUnderTheBridgeInterval()
    {
        var bridge = BridgeAggregator(SixHours);
        var due = new PlaidConnection { Id = 1, Provider = BankProvider.Bridge, LastSyncAttemptAt = Now.AddHours(-7) };
        var recent = new PlaidConnection { Id = 2, Provider = BankProvider.Bridge, LastSyncAttemptAt = Now.AddMinutes(-10) };
        var repo = new Mock<BuoyAI.Core.Interfaces.IPlaidConnectionRepository>();
        repo.Setup(r => r.GetBridgeConnectionsNeedingReconcileAsync()).ReturnsAsync(new List<PlaidConnection> { due, recent });
        IReadOnlyCollection<PlaidConnection>? passed = null;
        bridge.Setup(a => a.ReconcileConnectionsAsync(It.IsAny<IReadOnlyCollection<PlaidConnection>>()))
            .Callback<IReadOnlyCollection<PlaidConnection>>(c => passed = c).Returns(Task.CompletedTask);

        await PlaidTransactionSyncService.ReconcileBridgeConnectionsAsync(
            repo.Object, new BankAggregatorRegistry(new[] { bridge.Object }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, Now);

        passed.Should().NotBeNull();
        passed!.Select(c => c.Id).Should().Equal(1);
        bridge.Verify(a => a.ReconcileConnectionsAsync(It.IsAny<IReadOnlyCollection<PlaidConnection>>()), Times.Once);
    }

    [Fact]
    public void TheLoop_Over144WakesADay_PicksAConnectionOnASixHourIntervalFourTimes()
    {
        var registry = Registry(SixHours);
        var connection = new PlaidConnection { Id = 5, Provider = BankProvider.SimpleFin };
        var picked = 0;
        var clock = Now;

        for (var wake = 0; wake < 144; wake++)
        {
            clock = clock.AddMinutes(10);
            if (PlaidTransactionSyncService.SelectDueConnections(new[] { connection }, registry, clock).Count == 1)
            {
                picked++;
                connection.LastSyncAttemptAt = clock; // what a finished (or failed) attempt records
            }
        }

        picked.Should().Be(4);
    }

    [Fact]
    public void TheLoop_SkipsAConnectionWhoseProviderIsNotRegistered()
    {
        var mx = new PlaidConnection { Id = 9, Provider = BankProvider.Mx };

        PlaidTransactionSyncService.SelectDueConnections(new[] { mx }, Registry(SixHours), Now).Should().BeEmpty();
    }
}
