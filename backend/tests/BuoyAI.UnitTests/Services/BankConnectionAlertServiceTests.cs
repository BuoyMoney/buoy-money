using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class BankConnectionAlertServiceTests
{
    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IEmailService> _email = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IPlaidConnectionRepository> _connections = new();
    private readonly BankConnectionAlertService _service;

    public BankConnectionAlertServiceTests()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, Email = "kat@home.test", FirstName = "Kat" });
        // The first caller to ask for the email window wins; later ones see it taken.
        var claimed = new HashSet<int>();
        _connections.Setup(c => c.TryClaimAlertEmailAsync(It.IsAny<int>(), It.IsAny<DateTime>()))
            .ReturnsAsync((int id, DateTime _) => claimed.Add(id));
        _connections.Setup(c => c.ReleaseAlertEmailClaimAsync(It.IsAny<int>()))
            .Callback((int id) => claimed.Remove(id)).Returns(Task.CompletedTask);
        _service = new BankConnectionAlertService(
            _notifications.Object, _email.Object, _users.Object, _connections.Object,
            NullLogger<BankConnectionAlertService>.Instance);
    }

    private static PlaidConnection Connection(BankProvider provider = BankProvider.Plaid, PlaidItemStatus status = PlaidItemStatus.LoginRequired) =>
        new() { Id = 3, UserId = 7, Provider = provider, InstitutionName = "My Bank", Status = status, RequiresUserAction = true };

    [Theory]
    [InlineData(BankProvider.Plaid)]
    [InlineData(BankProvider.SimpleFin)]
    [InlineData(BankProvider.Bridge)]
    public async Task EveryProvider_GetsTheInAppNotification_AndTheEmail(BankProvider provider)
    {
        var connection = Connection(provider);

        await _service.NotifyNeedsAttentionAsync(connection, "ITEM_LOGIN_REQUIRED");

        if (provider == BankProvider.Plaid)
            _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", "ITEM_LOGIN_REQUIRED"), Times.Once);
        else if (provider == BankProvider.Bridge)
            _notifications.Verify(n => n.CreateBridgeAttentionNotificationAsync(7, 3, "My Bank", "ITEM_LOGIN_REQUIRED"), Times.Once);
        else
            _notifications.Verify(n => n.CreateSimpleFinAttentionNotificationAsync(7, 3, "My Bank", "ITEM_LOGIN_REQUIRED"), Times.Once);
        if (provider == BankProvider.Bridge)
            _notifications.Verify(n => n.CreateSimpleFinAttentionNotificationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _email.Verify(e => e.SendPlaidConnectionIssueAsync("kat@home.test", "Kat", "My Bank"), Times.Once);
        connection.AlertSentAt.Should().NotBeNull();
        _connections.Verify(c => c.TryClaimAlertEmailAsync(3, It.IsAny<DateTime>()), Times.Once);
        _connections.Verify(c => c.UpdateAsync(It.IsAny<PlaidConnection>()), Times.Never); // no whole-row write
    }

    [Fact]
    public async Task TheEmailGoesOutOncePerOutage_ButTheInAppAlertStillShowsEachTime()
    {
        var connection = Connection();

        await _service.NotifyNeedsAttentionAsync(connection);
        await _service.NotifyNeedsAttentionAsync(connection);

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AfterTheConnectionRecoversAndBreaksAgain_TheEmailGoesOutAgain()
    {
        var connection = Connection();
        await _service.NotifyNeedsAttentionAsync(connection);

        connection.AlertSentAt = null; // what a clean sync does ...
        _connections.Object.ReleaseAlertEmailClaimAsync(connection.Id).Wait(); // ... in the database too
        await _service.NotifyNeedsAttentionAsync(connection);

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AnOutageStillOpenAfterAWeek_IsNotEmailedAgain()
    {
        var connection = Connection();
        connection.AlertSentAt = DateTime.UtcNow.AddDays(-8);

        await _service.NotifyNeedsAttentionAsync(connection);

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TwoAlertsRacingOnStaleCopies_SendOneEmail()
    {
        // Both callers read AlertSentAt as null; only one wins the conditional claim.
        var a = Connection();
        var b = Connection();

        await Task.WhenAll(_service.NotifyNeedsAttentionAsync(a), _service.NotifyNeedsAttentionAsync(b));

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AnEmailThatThrows_GivesTheClaimBack_SoTheNextAlertCanTryAgain()
    {
        _email.SetupSequence(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"))
            .Returns(Task.CompletedTask);
        var connection = Connection();

        await _service.NotifyNeedsAttentionAsync(connection);
        connection.AlertSentAt.Should().BeNull();
        await _service.NotifyNeedsAttentionAsync(connection);

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        connection.AlertSentAt.Should().NotBeNull();
    }

    [Fact]
    public async Task AnEmailThatFails_NeverBreaksTheAlert()
    {
        _email.Setup(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        var act = () => _service.NotifyNeedsAttentionAsync(Connection());

        await act.Should().NotThrowAsync();
        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task AUserWithNoEmailOnFile_IsOnlyNotifiedInApp()
    {
        _users.Setup(u => u.GetByIdAsync(7)).ReturnsAsync(new User { Id = 7, Email = "" });

        await _service.NotifyNeedsAttentionAsync(Connection());

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData(PlaidItemStatus.LoginRequired, "ITEM_LOGIN_REQUIRED")]
    [InlineData(PlaidItemStatus.PendingExpiration, "PENDING_EXPIRATION")]
    [InlineData(PlaidItemStatus.PendingDisconnect, "PENDING_DISCONNECT")]
    [InlineData(PlaidItemStatus.Error, "SYNC_FAILED")]
    public async Task WithoutAReasonCode_TheWordingComesFromTheConnectionsStatus(PlaidItemStatus status, string expected)
    {
        await _service.NotifyNeedsAttentionAsync(Connection(status: status));

        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", expected), Times.Once);
    }

    [Fact]
    public async Task AGivenReasonCode_WinsOverTheStatus()
    {
        await _service.NotifyNeedsAttentionAsync(Connection(status: PlaidItemStatus.Error), "ACCESS_NOT_GRANTED");

        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(7, 3, "My Bank", "ACCESS_NOT_GRANTED"), Times.Once);
    }

    [Fact]
    public async Task ASimpleFinAlert_LinksToTheSimpleFinPage_NotPlaidUpdateMode()
    {
        await _service.NotifyNeedsAttentionAsync(Connection(BankProvider.SimpleFin), "ITEM_LOGIN_REQUIRED");

        _notifications.Verify(n => n.CreateSimpleFinAttentionNotificationAsync(7, 3, "My Bank", "ITEM_LOGIN_REQUIRED"), Times.Once);
        _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(PlaidItemStatus.PendingExpiration)]
    [InlineData(PlaidItemStatus.PendingDisconnect)]
    public async Task APendingWarning_IsNotEmailedAsADisconnect_AndDoesNotUseUpTheEmailWindow(PlaidItemStatus status)
    {
        var connection = Connection(status: status);

        await _service.NotifyNeedsAttentionAsync(connection);

        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        connection.AlertSentAt.Should().BeNull();
    }

    [Fact]
    public async Task Resolved_FlipsTheOpenNotification_AndNeverThrows()
    {
        await _service.NotifyResolvedAsync(Connection());

        _notifications.Verify(n => n.UpdateConnectionNotificationToResolvedAsync(7, 3, "My Bank"), Times.Once);

        _notifications.Setup(n => n.UpdateConnectionNotificationToResolvedAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var act = () => _service.NotifyResolvedAsync(Connection());
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ANewAccountsAlert_HasItsOwnWording_AndNoEmail()
    {
        var connection = Connection(BankProvider.Bridge, PlaidItemStatus.Active);

        await _service.NotifyNeedsAttentionAsync(connection, "NEW_ACCOUNTS_AVAILABLE");

        _notifications.Verify(n => n.CreateBridgeAttentionNotificationAsync(7, 3, "My Bank", "NEW_ACCOUNTS_AVAILABLE"), Times.Once);
        _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
