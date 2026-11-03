using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Exceptions;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

public class BridgePairingTests
{
    [Fact]
    public async Task Pair_StoresTheCredentialAndBothPrivateKeysEncrypted_AndNeverTheCodeOrAnythingReadable()
    {
        using var h = new BridgeHarness();

        var status = await h.PairAsync();

        status.Paired.Should().BeTrue();
        status.ServerId.Should().Be(FakeCloud.ServerId);
        status.AwaitingOwnerConfirmation.Should().BeTrue();
        var row = await h.Db.CloudPairings.AsNoTracking().SingleAsync();
        row.CredentialCipher.Should().StartWith("c1:").And.NotContain("bmk_");
        row.EncryptionKeyCipher.Should().StartWith("c1:");
        row.SigningKeyCipher.Should().StartWith("c1:");
        row.CloudUrl.Should().Be("https://cloud.test");
        (row.CredentialCipher + row.EncryptionKeyCipher + row.SigningKeyCipher).Should().NotContain("ABCDE");
    }

    [Fact]
    public async Task Pair_WithAWrongCode_SaysSoInTheCloudsWords_AndStoresNothing()
    {
        using var h = new BridgeHarness();

        var act = () => h.Pairing.PairAsync("WRONG-CODE0", null);

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*wrong, used or expired*");
        (await h.Pairing.GetStatusAsync()).Paired.Should().BeFalse();
    }

    [Fact]
    public async Task Pair_WhenTheCloudIsDown_ExplainsWithoutLeakingDetails()
    {
        using var h = new BridgeHarness();
        h.Cloud.Unreachable = true;

        var act = () => h.PairAsync();

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("Could not reach the BuoyAI Cloud*");
    }

    [Fact]
    public async Task WithoutACloudAddress_PairingIsRefused_AndStatusSaysNotConfigured()
    {
        using var h = new BridgeHarness(o => o.Url = "");

        (await h.Pairing.GetStatusAsync()).Configured.Should().BeFalse();
        var act = () => h.PairAsync();
        await act.Should().ThrowAsync<DomainValidationException>();
        h.Cloud.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://cloud.example.com")]
    [InlineData("ftp://cloud.example.com")]
    [InlineData("not a url")]
    public void CloudUrl_MustBeHttps_ExceptOnThisMachine(string url)
    {
        CloudPairingService.ParseCloudUrl(url).Should().BeNull();
        CloudPairingService.ParseCloudUrl("https://cloud.example.com").Should().NotBeNull();
        CloudPairingService.ParseCloudUrl("http://localhost:5100").Should().NotBeNull();
    }

    [Fact]
    public async Task TheStoredPairing_UnlocksToTheSameKeys_AndNotWithADifferentEncryptionKey()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        using (var identity = await h.Pairing.GetIdentityAsync())
        {
            identity.Should().NotBeNull();
            identity!.Credential.Should().Be(FakeCloud.Credential);
            identity.ServerId.Should().Be(FakeCloud.ServerId);
        }

        var other = new CloudPairingService(h.Db,
            new CloudSecretProtector(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Encryption:Key"] = "a-different-key-0123456789-abcdef" }).Build()),
            new FakeFactory(h.Cloud), Microsoft.Extensions.Options.Options.Create(h.Options),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CloudPairingService>.Instance);
        (await other.GetIdentityAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Unpair_ForgetsTheSecrets()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        await h.Pairing.UnpairAsync();

        (await h.Pairing.GetStatusAsync()).Paired.Should().BeFalse();
        (await h.Db.CloudPairings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EveryCloudCall_IsSigned_AndRepairingMakesTheOldKeysUseless()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        h.AddItem();

        await h.Client.ListItemsAsync();
        await h.Client.ListItemsAsync();

        h.Cloud.BadSignatures.Should().Be(0);
        h.Cloud.Calls.Count(c => c == "GET /bridge/v1/items").Should().Be(2);
    }

    [Fact]
    public async Task ARevokedServer_GetsALoginStyleFailure_NotACrash()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item);
        h.Cloud.RejectEverything = true;

        var act = () => h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);

        (await act.Should().ThrowAsync<BridgeLoginException>()).Which.Message.Should().Contain("Pair it again");
    }
}

public class BridgeConnectTests
{
    [Fact]
    public async Task Start_ReturnsTheCloudPageToOpen()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();

        var start = await h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");

        start.Provider.Should().Be(BankProvider.Bridge);
        start.Kind.Should().Be(AggregatorConnectKind.RedirectUrl);
        start.Value.Should().Be("https://cloud.test/link/s1");
        start.ExpiresAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Start_WhenNotPaired_AsksForPairing()
    {
        using var h = new BridgeHarness();

        var act = () => h.Aggregator.StartConnectionAsync(BridgeHarness.UserId, "web");

        (await act.Should().ThrowAsync<DomainValidationException>()).WithMessage("*not paired*");
    }

    [Fact]
    public async Task Complete_AdoptsTheBankTheCloudHolds_WithNoSecretOnThisServer_AndImportsAccountsAndTransactions()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());

        var result = await h.ConnectAsync(item);

        result.Success.Should().BeTrue(result.Message);
        var connection = await h.Db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.Provider.Should().Be(BankProvider.Bridge);
        connection.AccessToken.Should().BeEmpty("the Plaid token never reaches this server");
        connection.ItemId.Should().Be("bm:" + item.Id.ToString("N"));
        connection.InstitutionName.Should().Be("First Platypus Bank");
        connection.ImportStatus.Should().Be(ImportStatus.Completed);
        connection.LastSyncTransactionCount.Should().Be(3);
        result.ExternalConnectionId.Should().Be(connection.ItemId);

        var accounts = await h.Db.Accounts.AsNoTracking().OrderBy(a => a.PlaidAccountId).ToListAsync();
        accounts.Select(a => a.PlaidAccountId).Should().Equal("bm:acc-1", "bm:acc-card");
        accounts[0].Type.Should().Be(AccountType.Checking);
        accounts[0].CurrentBalance.Should().Be(1204.55m);
        accounts[0].AvailableBalance.Should().Be(1100m);
        accounts[0].PlaidConnectionId.Should().Be(connection.Id);
        accounts[1].Type.Should().Be(AccountType.CreditCard);
        accounts[1].CurrentBalance.Should().Be(312.40m, "Plaid already reports money owed as a positive number");

        var transactions = await h.Db.Transactions.AsNoTracking().OrderBy(t => t.PlaidTransactionId).ToListAsync();
        transactions.Select(t => t.PlaidTransactionId).Should().Equal("bm:tx-1", "bm:tx-2", "bm:tx-3");
        transactions[0].Amount.Should().Be(-45.10m, "Plaid shows money out as positive; BuoyAI as negative");
        transactions[1].Amount.Should().Be(2500m);

        item.Pages.Should().BeEmpty("the page was acknowledged");
        h.Cloud.Calls.Should().Contain(c => c.EndsWith("/ack"));
    }

    [Fact]
    public async Task Complete_SkipsInvestmentAccounts_AndTheirTransactions()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());

        await h.ConnectAsync(item);

        (await h.Db.Accounts.AnyAsync(a => a.PlaidAccountId == "bm:acc-401k")).Should().BeFalse();
        (await h.Db.Transactions.AnyAsync(t => t.PlaidTransactionId == "bm:tx-4")).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_AdoptsEachBankOnce_AndSaysSoWhenNothingNewIsThere()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());

        (await h.ConnectAsync(item)).Success.Should().BeTrue();
        var again = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, "done");

        again.Success.Should().BeFalse();
        again.Message.Should().Contain("No new bank connection");
        (await h.Db.PlaidConnections.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Complete_WhenTheFirstSyncFails_KeepsTheConnectionForTheLoopToRetry()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        h.Cloud.FailNextAck = true;

        var result = await h.ConnectAsync(item);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("first sync did not finish");
        var connection = await h.Db.PlaidConnections.AsNoTracking().SingleAsync();
        connection.ImportStatus.Should().Be(ImportStatus.Failed);
        connection.LastTransactionSyncAt.Should().BeNull("so the next sync pulls again");
    }
}

public class BridgeSyncTests
{
    private static Task<int> SyncAsync(BridgeHarness h, PlaidConnection c) =>
        h.Aggregator.SyncConnectionTransactionsAsync(c, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow);

    [Fact]
    public async Task ABatchAppliedButNotAcknowledged_IsPulledAgain_AndNothingIsDoubled()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);
        h.Cloud.FailNextAck = true;

        var first = () => SyncAsync(h, connection);
        await first.Should().ThrowAsync<BridgeSyncException>();
        (await h.Db.Transactions.CountAsync()).Should().Be(3);
        item.Pages.Should().HaveCount(1, "no ack, so the cursor did not move");

        var second = await SyncAsync(h, connection);

        second.Should().Be(0, "ingest skipped every id it already had");
        (await h.Db.Transactions.CountAsync()).Should().Be(3);
        item.Pages.Should().BeEmpty();
        item.Pulls.Should().Be(2);
    }

    [Fact]
    public async Task WithNothingNewAtTheCloud_NoPullIsMade()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.UpdatesAvailable = false);
        var connection = await h.SeedConnectionAsync(item);

        var count = await SyncAsync(h, connection);

        count.Should().Be(0);
        h.Cloud.Calls.Should().NotContain(c => c.EndsWith("/pull"));
    }

    [Fact]
    public async Task APagedFirstLoad_IsPulledAndAcknowledgedPageByPage()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(new FakePage
        {
            Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "Everyday Checking")),
            Added = new JsonArray(BridgeFixtures.Tx("p1-a", "acc-1", 1m, "One")),
            HasMore = true
        });
        item.Pages.Enqueue(new FakePage
        {
            Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "Everyday Checking")),
            Added = new JsonArray(BridgeFixtures.Tx("p2-a", "acc-1", 2m, "Two"))
        });
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);

        var count = await SyncAsync(h, connection);

        count.Should().Be(2);
        h.Cloud.Calls.Count(c => c.EndsWith("/ack")).Should().Be(2);
        item.Pages.Should().BeEmpty();
    }

    [Fact]
    public async Task APagedLoad_StopsAtTheBatchLimit()
    {
        using var h = new BridgeHarness(o => o.MaxBatchesPerSync = 2);
        await h.PairAsync();
        var item = h.AddItem();
        for (var i = 0; i < 5; i++)
            item.Pages.Enqueue(new FakePage { Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "A")), Added = new JsonArray(BridgeFixtures.Tx("t" + i, "acc-1", 1m, "x")), HasMore = true });
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);

        await SyncAsync(h, connection);

        item.Pages.Should().HaveCount(3);
    }

    [Fact]
    public async Task Removed_DeletesUntouchedRows_AndKeepsTheOnesTheUserWorkedOn()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);
        await SyncAsync(h, connection);
        var kept = await h.Db.Transactions.SingleAsync(t => t.PlaidTransactionId == "bm:tx-1");
        kept.UserNotes = "split with Sam";
        await h.Db.SaveChangesAsync();

        item.UpdatesAvailable = true;
        item.Pages.Enqueue(new FakePage
        {
            Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "Everyday Checking")),
            Removed = new JsonArray(new JsonObject { ["transaction_id"] = "tx-1" }, new JsonObject { ["transaction_id"] = "tx-2" })
        });
        await SyncAsync(h, connection);

        var left = await h.Db.Transactions.AsNoTracking().Select(t => t.PlaidTransactionId).ToListAsync();
        left.Should().Contain("bm:tx-1").And.NotContain("bm:tx-2");
    }

    [Fact]
    public async Task APostedChargeReplacesItsPendingHold()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(new FakePage
        {
            Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "Everyday Checking")),
            Added = new JsonArray(BridgeFixtures.Tx("hold-1", "acc-1", 9.99m, "Streaming", pending: true))
        });
        var connection = await h.SeedConnectionAsync(item, c => c.LastTransactionSyncAt = null);
        await SyncAsync(h, connection);

        item.UpdatesAvailable = true;
        item.Pages.Enqueue(new FakePage
        {
            Accounts = new JsonArray(BridgeFixtures.Account("acc-1", "Everyday Checking")),
            Added = new JsonArray(BridgeFixtures.Tx("post-1", "acc-1", 9.99m, "Streaming", pendingId: "hold-1"))
        });
        await SyncAsync(h, connection);

        var rows = await h.Db.Transactions.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].PlaidTransactionId.Should().Be("bm:post-1");
        rows[0].IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task ALoginRequiredBank_IsReportedAsOneAndTheFailureHandlerAlertsTheUser()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.Status = "login_required");
        var connection = await h.SeedConnectionAsync(item);

        var act = () => SyncAsync(h, connection);
        var ex = (await act.Should().ThrowAsync<BridgeLoginException>()).Which;
        await h.Aggregator.HandleSyncFailureAsync(connection.Id, ex.Message);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.LoginRequired);
        saved.RequiresUserAction.Should().BeTrue();
        saved.LastError.Should().NotContain("BRIDGE_AUTH");
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "ITEM_LOGIN_REQUIRED"), Times.Once);
    }

    [Fact]
    public async Task ABankMissingAtTheCloud_NeedsReconnecting()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var connection = await h.SeedConnectionAsync(new FakeCloudItem());

        var act = () => SyncAsync(h, connection);

        (await act.Should().ThrowAsync<BridgeLoginException>()).Which.Message.Should().Contain("no longer has this bank");
    }

    [Fact]
    public async Task AnOverCapBank_IsNotSynced_AndSaysWhy()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.OverCap = true);
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item);

        var act = () => SyncAsync(h, connection);

        (await act.Should().ThrowAsync<BridgeSyncException>()).WithMessage("*over its bank login limit*");
        h.Cloud.Calls.Should().NotContain(c => c.EndsWith("/pull"));
    }

    [Fact]
    public async Task ABatchSealedToAnotherServer_CannotBeOpened_AndNothingIsAcknowledged()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item);
        using var stranger = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = stranger.ExportParameters(false).Q;
        h.Cloud.ForeignKeyPoint = [0x04, .. q.X!, .. q.Y!];

        var act = () => SyncAsync(h, connection);

        (await act.Should().ThrowAsync<BridgeSyncException>()).WithMessage("*could not be opened with this server's key*");
        (await h.Db.Transactions.CountAsync()).Should().Be(0);
        h.Cloud.Calls.Should().NotContain(c => c.EndsWith("/ack"));
    }

    [Fact]
    public async Task ABatchForTheWrongServerId_FailsTheAuthTag()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item);
        h.Cloud.ForeignServerForSealing = Guid.NewGuid();

        var act = () => SyncAsync(h, connection);

        await act.Should().ThrowAsync<BridgeSyncException>();
        (await h.Db.Transactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ACloudOutage_IsAPassingError_ThatBecomesAnErrorStateAfterFourInARow()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item);
        h.Cloud.Unreachable = true;

        for (var i = 0; i < 4; i++)
        {
            var act = () => SyncAsync(h, connection);
            var ex = (await act.Should().ThrowAsync<BridgeSyncException>()).Which;
            await h.Aggregator.HandleSyncFailureAsync(connection.Id, ex.Message);
        }

        (await h.ReloadAsync(connection.Id)).Status.Should().Be(PlaidItemStatus.Error);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "SYNC_FAILED"), Times.Once);
    }

    [Fact]
    public async Task Schedule_AsksTheLoopForAtLeastTheConfiguredWait()
    {
        using var h = new BridgeHarness(o => { o.MinSyncIntervalMinutes = 45; o.JitterMinutes = 7; });
        h.Aggregator.Provider.Should().Be(BankProvider.Bridge);
        h.Aggregator.MinimumSyncInterval.Should().Be(TimeSpan.FromMinutes(45));
        h.Aggregator.SyncJitter.Should().Be(TimeSpan.FromMinutes(7));
        await Task.CompletedTask;
    }
}

public class BridgeDisconnectTests
{
    [Fact]
    public async Task Disconnect_RemovesTheBankAtTheCloud_ThenForgetsItHere()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item);

        await h.Aggregator.DisconnectConnectionAsync(BridgeHarness.UserId, connection.Id);

        item.Removed.Should().BeTrue("removing at the Cloud ends the Plaid charge");
        var saved = await h.ReloadAsync(connection.Id);
        saved.IsActive.Should().BeFalse();
        saved.Status.Should().Be(PlaidItemStatus.Disconnected);
    }

    [Fact]
    public async Task Disconnect_WhenTheCloudCannotBeReached_KeepsTheConnection_SoBillingIsNotLeftRunning()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item);
        h.Cloud.FailRemoveWith = 503;

        var act = () => h.Aggregator.DisconnectConnectionAsync(BridgeHarness.UserId, connection.Id);

        await act.Should().ThrowAsync<DomainValidationException>();
        (await h.ReloadAsync(connection.Id)).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Disconnect_WhenTheCloudNoLongerAcceptsThisServer_StillClosesTheLocalRow()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item);
        h.Cloud.FailRemoveWith = 401;

        await h.Aggregator.DisconnectConnectionAsync(BridgeHarness.UserId, connection.Id);

        (await h.ReloadAsync(connection.Id)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_RefusesSomeoneElsesConnection()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var connection = await h.SeedConnectionAsync(h.AddItem());

        var act = () => h.Aggregator.DisconnectConnectionAsync(99, connection.Id);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryRemove_NeverThrows()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var connection = await h.SeedConnectionAsync(h.AddItem());
        h.Cloud.Unreachable = true;

        (await h.Aggregator.TryRemoveConnectionAsync(connection)).Should().BeFalse();
    }
}

public class BridgeMappingTests
{
    [Fact]
    public void Transaction_MapsSignDirectionIdsAndCategoryHints()
    {
        var tx = BridgeMapping.ToIncoming(BridgeFixtures.Tx("tx-9", "acc-1", 45.10m, "Kroger", "2026-10-03"));

        tx.Amount.Should().Be(-45.10m);
        tx.Direction.Should().Be(TransactionType.Debit);
        tx.ExternalTransactionId.Should().Be("bm:tx-9");
        tx.ExternalAccountId.Should().Be("bm:acc-1");
        tx.Date.Should().Be(new DateOnly(2026, 10, 3));
        tx.CategoryId.Should().Be(SystemCategoryIds.Uncategorized);
        tx.NeedsReview.Should().BeTrue();
        tx.CategoryPrimary.Should().Be("FOOD_AND_DRINK");
        tx.CategorySource.Should().Be("Bridge");
        tx.Provider.Should().Be(BankProvider.Bridge);
    }

    [Fact]
    public void Income_IsPositive()
    {
        var tx = BridgeMapping.ToIncoming(BridgeFixtures.Tx("t", "a", -2500m, "Payroll"));
        tx.Amount.Should().Be(2500m);
        tx.Direction.Should().Be(TransactionType.Credit);
    }

    [Fact]
    public void LongIds_AreHashedNotCut_AndKeepTheirPrefix()
    {
        var id = BridgeMapping.TransactionId(new string('x', 300));
        id.Length.Should().BeLessThanOrEqualTo(100);
        id.Should().StartWith("bm:");
        id.Should().NotBe(BridgeMapping.TransactionId(new string('x', 301)));
    }

    [Theory]
    [InlineData("depository", "checking", AccountType.Checking)]
    [InlineData("depository", "savings", AccountType.Savings)]
    [InlineData("depository", "money market", AccountType.Savings)]
    [InlineData("credit", "credit card", AccountType.CreditCard)]
    [InlineData("investment", "401k", AccountType.Investment)]
    [InlineData("loan", "mortgage", AccountType.Other)]
    [InlineData(null, null, AccountType.Other)]
    public void AccountTypes(string? type, string? subtype, AccountType expected) =>
        BridgeMapping.MapAccountType(type, subtype).Should().Be(expected);

    [Fact]
    public void ItemIds_RoundTrip()
    {
        var id = Guid.NewGuid();
        BridgeMapping.TryParseItemId(BridgeMapping.ItemId(id), out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
        BridgeMapping.TryParseItemId("sfin:abc", out _).Should().BeFalse();
    }
}

public class BridgeHpkeTests
{
    // Both sides decrypt these exact bytes (the Cloud Bridge service has the same vector; see docs/self_host/bridge.md).
    private const string PrivateKey = "jO/CBsTnlvkQFg+2FrflxefFmt1StsH/fAWhZ6GN9eM=";
    private const string PublicKey = "BBgp8Gnca6ohEbXjO3t/giLetNqBTPLeIVhfznBgUdFvhJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=";
    private const string Plaintext = "{\"batch_id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"added\":[],\"modified\":[],\"removed\":[]}";
    private const string Envelope = "BLspCP7ZoDHrAuo1yUzjPPMGy5saJh_BKmPZIgWQmhZutuN7Ggb6g4zjerIxRx8U-PCYDknVNxo3rM_0zh8agRrfIrZNsb0snG9eVPMyZ41XbICAA-Ek2luasFT1asTT3jYdNZA1lwDQoLx2nWH_Q5DeT2mSQ-9sTFYGnEZB8RF6JyBK7Ki7eMupqFTiF4_kNOGncEIk5qhMQUsik7UAYwAbFGgLdS3_yfo";

    private static ECDiffieHellman Key()
    {
        var raw = Convert.FromBase64String(PublicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromBase64String(PrivateKey),
            Q = new ECPoint { X = raw[1..33], Y = raw[33..65] }
        });
    }

    [Fact]
    public void TheCloudsSharedVector_Opens()
    {
        var aad = Encoding.UTF8.GetBytes("11111111222233334444555555555555|aaaaaaaabbbbccccddddeeeeeeeeeeee|99999999888877776666555555555555|cursor-after-1");

        var plain = BridgeHpke.Open(Key(), BridgeClient.FromBase64Url(Envelope), BridgeHpke.FeedInfo, aad);

        Encoding.UTF8.GetString(plain).Should().Be(Plaintext);
    }

    [Fact]
    public void FeedAad_MatchesTheCloudsFormat()
    {
        BridgeHpke.FeedAad(Guid.Parse("11111111-2222-3333-4444-555555555555"), Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                Guid.Parse("99999999-8888-7777-6666-555555555555"), "cursor-after-1")
            .Should().Equal(Encoding.UTF8.GetBytes("11111111222233334444555555555555|aaaaaaaabbbbccccddddeeeeeeeeeeee|99999999888877776666555555555555|cursor-after-1"));
    }

    [Fact]
    public void ChangedContext_OrBytes_Fail()
    {
        var bytes = BridgeClient.FromBase64Url(Envelope);
        var aad = Encoding.UTF8.GetBytes("11111111222233334444555555555555|aaaaaaaabbbbccccddddeeeeeeeeeeee|99999999888877776666555555555555|cursor-after-1");

        var wrongAad = () => BridgeHpke.Open(Key(), bytes, BridgeHpke.FeedInfo, Encoding.UTF8.GetBytes("other"));
        wrongAad.Should().Throw<CryptographicException>();
        var tampered = (byte[])bytes.Clone();
        tampered[80] ^= 1;
        var flipped = () => BridgeHpke.Open(Key(), tampered, BridgeHpke.FeedInfo, aad);
        flipped.Should().Throw<CryptographicException>();
        var truncated = () => BridgeHpke.Open(Key(), bytes[..40], BridgeHpke.FeedInfo, aad);
        truncated.Should().Throw<CryptographicException>();
    }
}
