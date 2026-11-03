using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.UnitTests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>
/// The bridge client against a real Postgres built by the SQL migrations, so the cloud_pairings table (migration 0054),
/// the bm: ids and the unique indexes are the real ones. Skipped unless BUOYAI_MIGRATION_TEST_CONNECTION is set.
/// </summary>
public class BridgePostgresTests
{
    [PostgresFact]
    public async Task PairConnectSyncAndDisconnect_WorkAgainstTheRealSchema()
    {
        using var scratch = new ScratchDatabase();
        SqlMigrationRunner.Run(scratch.ConnectionString);
        using var db = new BuoyAIDbContext(new DbContextOptionsBuilder<BuoyAIDbContext>().UseNpgsql(scratch.ConnectionString).Options);
        using var h = new BridgeHarness(db: db);

        await h.PairAsync();
        (await db.CloudPairings.AsNoTracking().SingleAsync()).ServerId.Should().Be(FakeCloud.ServerId);

        // Pairing again replaces the one row instead of adding a second (the table allows only id = 1).
        await h.PairAsync();
        (await db.CloudPairings.CountAsync()).Should().Be(1);

        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        (await h.ConnectAsync(item)).Success.Should().BeTrue();

        var connection = await db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.Provider.Should().Be(BankProvider.Bridge);
        (await db.Accounts.AsNoTracking().Select(a => a.PlaidAccountId).OrderBy(i => i).ToListAsync()).Should().Equal("bm:acc-1", "bm:acc-card");
        (await db.Transactions.AsNoTracking().Select(t => t.PlaidTransactionId).OrderBy(i => i).ToListAsync()).Should().Equal("bm:tx-1", "bm:tx-2", "bm:tx-3");

        // The same page again (a lost ack) adds nothing; the unique index would refuse a repeat.
        item.UpdatesAvailable = true;
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        (await h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow)).Should().Be(0);

        await h.Aggregator.DisconnectConnectionAsync(BridgeHarness.UserId, connection.Id);
        item.Removed.Should().BeTrue();

        await h.Pairing.UnpairAsync();
        (await db.CloudPairings.CountAsync()).Should().Be(0);
    }
}
