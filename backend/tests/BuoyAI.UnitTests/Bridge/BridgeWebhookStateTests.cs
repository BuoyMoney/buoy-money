using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Repositories;
using BuoyAI.Infrastructure.Services.Bridge;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Bridge;

/// <summary>
/// What a Plaid webhook the Cloud received does on this server. The Cloud turns each webhook into a state on the bank
/// (login_required, revoked, or a notice); this server reads that state on its next sync and raises the same alerts hosted does.
/// </summary>
public class BridgeWebhookStateTests
{
    private static Task<int> SyncAsync(BridgeHarness h, PlaidConnection connection) =>
        h.Aggregator.SyncConnectionTransactionsAsync(connection, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

    private static readonly DateTime NoticeTime = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PendingExpiration_FlagsTheConnection_AlertsOnce_AndKeepsSyncing()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => { i.Notice = "pending_expiration"; i.NoticeAt = NoticeTime; });
        item.Pages.Enqueue(BridgeFixtures.OnePage());
        var connection = await h.SeedConnectionAsync(item);

        var created = await SyncAsync(h, connection);
        var again = await h.ReloadAsync(connection.Id);
        await SyncAsync(h, again);

        created.Should().Be(3, "the bank still works while it is only about to expire");
        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.PendingExpiration);
        saved.RequiresUserAction.Should().BeTrue();
        saved.ExpirationDate.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(5));
        saved.BridgeNoticeAt.Should().Be(NoticeTime);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "PENDING_EXPIRATION"), Times.Once);
    }

    [Fact]
    public async Task PendingDisconnect_FlagsTheConnectionAndAlerts()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => { i.Notice = "pending_disconnect"; i.NoticeAt = NoticeTime; i.UpdatesAvailable = false; });
        var connection = await h.SeedConnectionAsync(item);

        await SyncAsync(h, connection);

        (await h.ReloadAsync(connection.Id)).Status.Should().Be(PlaidItemStatus.PendingDisconnect);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "PENDING_DISCONNECT"), Times.Once);
    }

    [Fact]
    public async Task NewAccountsAvailable_AlertsOnce_WithoutChangingTheStatus_AndAgainForALaterNotice()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => { i.Notice = "new_accounts_available"; i.NoticeAt = NoticeTime; i.UpdatesAvailable = false; });
        var connection = await h.SeedConnectionAsync(item);

        await SyncAsync(h, connection);
        await SyncAsync(h, connection);

        (await h.ReloadAsync(connection.Id)).Status.Should().Be(PlaidItemStatus.Active);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "NEW_ACCOUNTS_AVAILABLE"), Times.Once);

        item.NoticeAt = NoticeTime.AddDays(3);
        await SyncAsync(h, connection);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), "NEW_ACCOUNTS_AVAILABLE"), Times.Exactly(2));
    }

    [Fact]
    public async Task AWarningThatTheCloudClears_PutsTheConnectionBackToActive_AndResolvesTheNotification()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.UpdatesAvailable = false);
        var connection = await h.SeedConnectionAsync(item, c =>
        {
            c.Status = PlaidItemStatus.PendingExpiration;
            c.RequiresUserAction = true;
            c.ExpirationDate = DateTime.UtcNow.AddDays(3);
            c.BridgeNoticeAt = NoticeTime;
        });

        await SyncAsync(h, connection);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.Active);
        saved.RequiresUserAction.Should().BeFalse();
        saved.ExpirationDate.Should().BeNull();
        h.Alerts.Verify(a => a.NotifyResolvedAsync(It.IsAny<PlaidConnection>()), Times.Once);
        h.Alerts.Verify(a => a.NotifyNeedsAttentionAsync(It.IsAny<PlaidConnection>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task AHealthyBankWithNoNotice_IsLeftAlone()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.UpdatesAvailable = false);
        var connection = await h.SeedConnectionAsync(item);

        await SyncAsync(h, connection);

        h.Alerts.VerifyNoOtherCalls();
    }

    // ---------------------------------------------------------------- LOGIN_REPAIRED: clears with no action by the user

    [Fact]
    public async Task LoginRepaired_ClearsALoginRequiredConnection_WithoutTheUserDoingAnything()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(); // the Cloud's item is active again
        var connection = await h.SeedConnectionAsync(item, c =>
        {
            c.Status = PlaidItemStatus.LoginRequired;
            c.RequiresUserAction = true;
            c.ConsecutiveFailures = 1;
            c.LastError = "Your bank needs you to sign in again.";
            c.AlertSentAt = DateTime.UtcNow.AddHours(-3);
        });

        await h.Aggregator.ReconcileConnectionsAsync([connection]);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.Active);
        saved.RequiresUserAction.Should().BeFalse();
        saved.ConsecutiveFailures.Should().Be(0);
        saved.LastError.Should().BeNull();
        saved.AlertSentAt.Should().BeNull("the next outage must email again");
        h.Alerts.Verify(a => a.NotifyResolvedAsync(It.IsAny<PlaidConnection>()), Times.Once);
        h.Cloud.Calls.Count(c => c == "GET /bridge/v1/items").Should().Be(1);
        h.Cloud.Calls.Should().NotContain(c => c.Contains("/pull"), "reconciling asks which banks need attention; it does not fetch transactions");
    }

    [Theory]
    [InlineData("login_required")]
    [InlineData("revoked")]
    public async Task AConnectionStaysFlagged_WhileTheCloudStillSaysTheBankNeedsASignIn(string cloudStatus)
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => i.Status = cloudStatus);
        var connection = await h.SeedConnectionAsync(item, c => { c.Status = PlaidItemStatus.LoginRequired; c.RequiresUserAction = true; });

        await h.Aggregator.ReconcileConnectionsAsync([connection]);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.LoginRequired);
        saved.LastSyncAttemptAt.Should().NotBeNull("the attempt is stamped so the schedule paces the next question");
        h.Alerts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WhenTheCloudCannotBeReached_ReconcileChangesNothingAndDoesNotThrow()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem();
        var connection = await h.SeedConnectionAsync(item, c => { c.Status = PlaidItemStatus.LoginRequired; c.RequiresUserAction = true; });
        h.Cloud.Unreachable = true;

        var act = () => h.Aggregator.ReconcileConnectionsAsync([connection]);

        await act.Should().NotThrowAsync();
        (await h.ReloadAsync(connection.Id)).Status.Should().Be(PlaidItemStatus.LoginRequired);
    }

    [Fact]
    public async Task ABankTheCloudNoLongerHas_IsNotChangedByReconcile()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var connection = await h.SeedConnectionAsync(new FakeCloudItem(), c => { c.Status = PlaidItemStatus.LoginRequired; c.RequiresUserAction = true; });

        await h.Aggregator.ReconcileConnectionsAsync([connection]);

        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.LoginRequired);
        saved.LastSyncAttemptAt.Should().NotBeNull("an unlisted bank is still paced, not asked about on every wake");
    }

    // ---------------------------------------------------------------- reconnect

    [Fact]
    public async Task Reconnect_ResolvesTheNotification_AndClearsTheWarning()
    {
        using var h = new BridgeHarness();
        await h.PairAsync();
        var item = h.AddItem(i => { i.Status = "login_required"; i.Notice = "pending_disconnect"; i.NoticeAt = NoticeTime; });
        var connection = await h.SeedConnectionAsync(item, c =>
        {
            c.Status = PlaidItemStatus.LoginRequired;
            c.RequiresUserAction = true;
            c.ExpirationDate = DateTime.UtcNow.AddDays(2);
        });

        var start = await h.Aggregator.StartReconnectAsync(BridgeHarness.UserId, connection.Id, "web");
        item.Notice = null; // the Cloud clears the notice when the update-mode sign-in finishes
        item.NoticeAt = null;
        var result = await h.Aggregator.CompleteConnectionAsync(BridgeHarness.UserId, start.Value);

        result.Success.Should().BeTrue(result.Message);
        var saved = await h.ReloadAsync(connection.Id);
        saved.Status.Should().Be(PlaidItemStatus.Active);
        saved.ExpirationDate.Should().BeNull();
        h.Alerts.Verify(a => a.NotifyResolvedAsync(It.IsAny<PlaidConnection>()), Times.Once);
    }

    // ---------------------------------------------------------------- which connections the loop picks

    [Fact]
    public async Task TheSyncLoop_KeepsABridgeBankThatIsOnlyExpiring_ButStillSkipsAPlaidOneInThatState()
    {
        using var h = new BridgeHarness();
        var repo = new PlaidConnectionRepository(h.Db);
        PlaidConnection Make(BankProvider provider, PlaidItemStatus status, string itemId) => new()
        {
            UserId = BridgeHarness.UserId, Provider = provider, AccessToken = "x", ItemId = itemId, InstitutionId = "i", InstitutionName = itemId,
            IsActive = true, Status = status, ImportStatus = ImportStatus.Completed
        };
        h.Db.PlaidConnections.AddRange(
            Make(BankProvider.Bridge, PlaidItemStatus.PendingExpiration, "bridge-exp"),
            Make(BankProvider.Bridge, PlaidItemStatus.PendingDisconnect, "bridge-disc"),
            Make(BankProvider.Bridge, PlaidItemStatus.LoginRequired, "bridge-login"),
            Make(BankProvider.Plaid, PlaidItemStatus.PendingExpiration, "plaid-exp"),
            Make(BankProvider.Plaid, PlaidItemStatus.Active, "plaid-ok"));
        await h.Db.SaveChangesAsync();

        var synced = (await repo.GetActiveConnectionsForSync()).Select(c => c.ItemId).ToList();
        var reconcile = (await repo.GetBridgeConnectionsNeedingReconcileAsync()).Select(c => c.ItemId).ToList();

        synced.Should().BeEquivalentTo(["bridge-exp", "bridge-disc", "plaid-ok"]);
        reconcile.Should().BeEquivalentTo(["bridge-login"]);
    }
}
