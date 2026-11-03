using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.UnitTests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.SimpleFin;

/// <summary>
/// SimpleFIN through the real aggregator, repositories and ingest, against a real Postgres built by
/// the SQL migrations (so column names, lengths, unique indexes and the new AlertSentAt column are
/// the real ones). Skipped unless BUOYAI_MIGRATION_TEST_CONNECTION is set.
/// </summary>
public class SimpleFinPostgresTests
{
    [PostgresFact]
    public async Task ConnectSyncFailAndDisconnect_WorkAgainstTheRealSchema()
    {
        using var scratch = new ScratchDatabase();
        SqlMigrationRunner.Run(scratch.ConnectionString);
        using var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseNpgsql(scratch.ConnectionString).Options);
        using var h = new SimpleFinHarness(db: db);
        h.OnAccounts = _ => SimpleFinHarness.Json(SimpleFinFixtures.ThreeAccounts);

        // Connect: the access URL is saved encrypted, accounts and transactions land.
        var result = await h.Aggregator.CompleteConnectionAsync(SimpleFinHarness.UserId, SimpleFinFixtures.SetupToken());
        result.Success.Should().BeTrue(result.Message);

        var connection = await db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.Provider.Should().Be(BankProvider.SimpleFin);
        connection.AccessToken.Should().Be("enc::" + SimpleFinFixtures.AccessUrl);
        connection.InstitutionName.Should().Be("SimpleFIN (2 banks)");
        (await db.Accounts.AsNoTracking().Select(a => a.PlaidAccountId).OrderBy(i => i).ToListAsync())
            .Should().Equal($"sfin:{connection.Id}:ACT-card-1", $"sfin:{connection.Id}:ACT-checking-1");
        (await db.Transactions.AsNoTracking().Select(t => t.PlaidTransactionId).OrderBy(i => i).ToListAsync())
            .Should().Equal($"sfin:{connection.Id}:ACT-card-1:TRN-9", $"sfin:{connection.Id}:ACT-checking-1:TRN-1");

        // A second pass over the same window adds nothing (the unique index would refuse a repeat).
        var again = await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
        again.Should().Be(0);

        // A dead connection moves to LoginRequired and the alert fires with the saved row.
        await h.Aggregator.HandleSyncFailureAsync(connection.Id, new BuoyAI.Infrastructure.Services.SimpleFin.SimpleFinAuthException("Gone.").Message);
        var broken = await db.PlaidConnections.AsNoTracking().SingleAsync();
        broken.Status.Should().Be(PlaidItemStatus.LoginRequired);
        broken.RequiresUserAction.Should().BeTrue();

        // The alert column exists and round-trips.
        db.ChangeTracker.Clear();
        var tracked = await db.PlaidConnections.SingleAsync();
        tracked.AlertSentAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        (await db.PlaidConnections.AsNoTracking().SingleAsync()).AlertSentAt.Should().NotBeNull();

        // Disconnect forgets the secret.
        await h.Aggregator.DisconnectConnectionAsync(SimpleFinHarness.UserId, connection.Id);
        db.ChangeTracker.Clear();
        var gone = await db.PlaidConnections.AsNoTracking().SingleAsync();
        gone.AccessToken.Should().BeEmpty();
        gone.IsActive.Should().BeFalse();
        gone.AlertSentAt.Should().BeNull();
    }
}
