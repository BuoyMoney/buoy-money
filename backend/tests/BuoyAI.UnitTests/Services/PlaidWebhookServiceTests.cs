using System.Collections.Generic;
using System.Linq;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

public class PlaidWebhookServiceTests
{
    private readonly Mock<IPlaidService> _mockPlaidService;
    private readonly Mock<IPlaidConnectionRepository> _mockPlaidConnectionRepository;
    private readonly Mock<INotificationService> _mockNotificationService;
    private readonly Mock<ISubscriptionService> _mockSubscriptionService;
    private readonly Mock<ILogger<PlaidWebhookService>> _mockLogger;
    private readonly PlaidWebhookService _service;

    public PlaidWebhookServiceTests()
    {
        _mockPlaidService = new Mock<IPlaidService>();
        _mockPlaidConnectionRepository = new Mock<IPlaidConnectionRepository>();
        _mockNotificationService = new Mock<INotificationService>();
        _mockSubscriptionService = new Mock<ISubscriptionService>();
        _mockLogger = new Mock<ILogger<PlaidWebhookService>>();

        _service = new PlaidWebhookService(
            _mockPlaidService.Object,
            _mockPlaidConnectionRepository.Object,
            _mockNotificationService.Object,
            new BankConnectionAlertService(_mockNotificationService.Object, Mock.Of<IEmailService>(), Mock.Of<IUserRepository>(), _mockPlaidConnectionRepository.Object, Mock.Of<ILogger<BankConnectionAlertService>>()),
            _mockSubscriptionService.Object,
            _mockLogger.Object
        );
    }

    #region ProcessWebhookAsync Tests

    [Fact]
    public async Task ProcessWebhookAsync_WithUnknownItemId_ShouldLogWarningAndReturn()
    {
        // Arrange
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "ITEM",
            WebhookCode = "ERROR",
            ItemId = "unknown_item"
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("unknown_item"))
            .ReturnsAsync((PlaidConnection?)null);

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.GetConnectionByItemIdAsync("unknown_item"), Times.Once);
        // Should not process further
        _mockPlaidConnectionRepository.Verify(x => x.UpdateAsync(It.IsAny<PlaidConnection>()), Times.Never);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithItemWebhook_ShouldHandleItemWebhook()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item", InstitutionName = "Test Bank" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "ITEM",
            WebhookCode = "WEBHOOK_UPDATE_ACKNOWLEDGED",
            ItemId = "test_item"
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.GetConnectionByItemIdAsync("test_item"), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithTransactionsWebhook_ShouldSyncTransactions()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "TRANSACTIONS",
            WebhookCode = "SYNC_UPDATES_AVAILABLE",
            ItemId = "test_item"
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.SyncTransactionsAsync(1))
            .ReturnsAsync(new BuoyAI.Core.DTOs.Plaid.TransactionSyncResult());

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.SyncTransactionsAsync(1), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithTransactionsRemoved_ShouldPurgeReferencedTransactions()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "TRANSACTIONS",
            WebhookCode = "TRANSACTIONS_REMOVED",
            ItemId = "test_item",
            RemovedTransactions = new List<string> { "txn_a", "txn_b" }
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.RemoveTransactionsAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(2);

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.RemoveTransactionsAsync(
            It.Is<IEnumerable<string>>(ids => ids.Contains("txn_a") && ids.Contains("txn_b"))), Times.Once);
        // Removed-transactions webhooks must NOT trigger a full re-sync.
        _mockPlaidService.Verify(x => x.SyncTransactionsAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithTransactionsRemovedButNoIds_ShouldNotCallRemove()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "TRANSACTIONS",
            WebhookCode = "TRANSACTIONS_REMOVED",
            ItemId = "test_item",
            RemovedTransactions = new List<string>()
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.RemoveTransactionsAsync(It.IsAny<IEnumerable<string>>()), Times.Never);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithAccountsWebhook_ShouldSyncAccounts()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "ACCOUNTS",
            WebhookCode = "DEFAULT_UPDATE",
            ItemId = "test_item"
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.GetAccountsAsync(1))
            .ReturnsAsync(new List<AccountDto>());

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.GetAccountsAsync(1), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_WithUnhandledWebhookType_ShouldLogInfo()
    {
        // Arrange
        var connection = new PlaidConnection { Id = 1, UserId = 1, ItemId = "test_item" };
        var webhook = new PlaidWebhookRequest
        {
            WebhookType = "UNKNOWN_TYPE",
            WebhookCode = "UNKNOWN_CODE",
            ItemId = "test_item"
        };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("test_item"))
            .ReturnsAsync(connection);

        // Act
        await _service.ProcessWebhookAsync(webhook);

        // Assert
        _mockPlaidService.Verify(x => x.GetConnectionByItemIdAsync("test_item"), Times.Once);
        // Should not throw or process further
    }

    #endregion

    #region CreateUpdateTokenAsync Tests

    [Fact]
    public async Task CreateUpdateTokenAsync_WithValidConnection_ShouldReturnLinkToken()
    {
        // Arrange
        var userId = 1;
        var connectionId = 1;
        var expectedToken = "link-sandbox-token-abc123";

        _mockPlaidService.Setup(x => x.CreateUpdateTokenAsync(userId, connectionId))
            .ReturnsAsync(expectedToken);

        // Act
        var result = await _service.CreateUpdateTokenAsync(userId, connectionId);

        // Assert
        result.Should().NotBeNull();
        result.LinkToken.Should().Be(expectedToken);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CreateUpdateTokenAsync_WithError_ShouldThrowException()
    {
        // Arrange
        var userId = 1;
        var connectionId = 999;

        _mockPlaidService.Setup(x => x.CreateUpdateTokenAsync(userId, connectionId))
            .ThrowsAsync(new InvalidOperationException("Connection not found"));

        // Act
        Func<Task> act = async () => await _service.CreateUpdateTokenAsync(userId, connectionId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Connection not found");
    }

    #endregion

    #region UpdateConnectionAsync Tests

    [Fact]
    public async Task UpdateConnectionAsync_WithValidPublicToken_ShouldReturnTrue()
    {
        // Arrange
        var userId = 1;
        var connectionId = 1;
        var request = new UpdateConnectionRequest { PublicToken = "public-sandbox-token-abc123" };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(new PlaidConnection { Id = connectionId, UserId = userId });
        _mockPlaidService.Setup(x => x.HandleItemUpdateAsync(connectionId, request.PublicToken))
            .ReturnsAsync(true);

        // Act
        var result = await _service.UpdateConnectionAsync(userId, connectionId, request);

        // Assert
        result.Should().BeTrue();
        _mockPlaidService.Verify(x => x.HandleItemUpdateAsync(connectionId, request.PublicToken), Times.Once);
    }

    [Fact]
    public async Task UpdateConnectionAsync_WithError_ShouldReturnFalse()
    {
        // Arrange
        var userId = 1;
        var connectionId = 1;
        var request = new UpdateConnectionRequest { PublicToken = "invalid_token" };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(new PlaidConnection { Id = connectionId, UserId = userId });
        _mockPlaidService.Setup(x => x.HandleItemUpdateAsync(connectionId, request.PublicToken))
            .ThrowsAsync(new Exception("Invalid token"));

        // Act
        var result = await _service.UpdateConnectionAsync(userId, connectionId, request);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateConnectionAsync_WhenCallerDoesNotOwnConnection_ShouldThrowUnauthorized()
    {
        // Arrange — connection belongs to user A, but user B is the caller (IDOR).
        var ownerUserId = 1;
        var attackerUserId = 2;
        var connectionId = 42;
        var request = new UpdateConnectionRequest { PublicToken = "public-sandbox-token-xyz" };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(new PlaidConnection { Id = connectionId, UserId = ownerUserId });

        // Act
        Func<Task> act = async () => await _service.UpdateConnectionAsync(attackerUserId, connectionId, request);

        // Assert
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        // The item update must never run for a non-owner.
        _mockPlaidService.Verify(x => x.HandleItemUpdateAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    #endregion

    #region GetConnectionStatusAsync Tests

    [Fact]
    public async Task GetConnectionStatusAsync_WithValidConnection_ShouldReturnStatus()
    {
        // Arrange
        var userId = 1;
        var connectionId = 1;
        var connection = new PlaidConnection
        {
            Id = connectionId,
            UserId = userId,
            Status = PlaidItemStatus.Active,
            RequiresUserAction = false,
            InstitutionName = "Test Bank",
            LastStatusUpdate = DateTime.UtcNow
        };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(connection);

        // Act
        var result = await _service.GetConnectionStatusAsync(userId, connectionId);

        // Assert
        result.Should().NotBeNull();
        result.ConnectionId.Should().Be(connectionId);
        result.Status.Should().Be(PlaidItemStatus.Active);
        result.RequiresUserAction.Should().BeFalse();
        result.InstitutionName.Should().Be("Test Bank");
    }

    [Fact]
    public async Task GetConnectionStatusAsync_WithNonExistentConnection_ShouldThrowException()
    {
        // Arrange
        var userId = 1;
        var connectionId = 999;
        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync((PlaidConnection?)null);

        // Act
        Func<Task> act = async () => await _service.GetConnectionStatusAsync(userId, connectionId);

        // Assert
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("Connection not found");
    }

    [Fact]
    public async Task GetConnectionStatusAsync_WhenCallerDoesNotOwnConnection_ShouldThrowUnauthorized()
    {
        // Arrange — user A owns the connection; user B (attacker) requests its status (IDOR).
        var ownerUserId = 1;
        var attackerUserId = 2;
        var connectionId = 7;
        var connection = new PlaidConnection
        {
            Id = connectionId,
            UserId = ownerUserId,
            Status = PlaidItemStatus.Active,
            InstitutionName = "Victim Bank"
        };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(connection);

        // Act
        Func<Task> act = async () => await _service.GetConnectionStatusAsync(attackerUserId, connectionId);

        // Assert — user B must not be able to read user A's connection.
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    #endregion

    #region HandleItemErrorAsync Tests

    [Fact]
    public async Task HandleItemErrorAsync_WithLoginRequired_ShouldUpdateStatusAndCreateNotification()
    {
        // Arrange
        var connectionId = 1;
        var connection = new PlaidConnection
        {
            Id = connectionId,
            UserId = 1,
            InstitutionName = "Test Bank"
        };
        var error = new PlaidError
        {
            ErrorCode = "ITEM_LOGIN_REQUIRED",
            ErrorMessage = "User needs to re-authenticate"
        };

        _mockPlaidService.Setup(x => x.UpdateConnectionStatusAsync(
            connectionId,
            PlaidItemStatus.LoginRequired,
            It.IsAny<string>()
        )).Returns(Task.CompletedTask);

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(connection);

        _mockNotificationService.Setup(x => x.CreatePlaidUpdateNotificationAsync(
            connection.UserId,
            connectionId,
            connection.InstitutionName,
            error.ErrorCode
        )).Returns(Task.CompletedTask);

        // Act
        await _service.HandleItemErrorAsync(connectionId, error);

        // Assert
        _mockPlaidService.Verify(x => x.UpdateConnectionStatusAsync(
            connectionId,
            PlaidItemStatus.LoginRequired,
            "ITEM_LOGIN_REQUIRED: User needs to re-authenticate"
        ), Times.Once);

        _mockNotificationService.Verify(x => x.CreatePlaidUpdateNotificationAsync(
            connection.UserId,
            connectionId,
            connection.InstitutionName,
            "ITEM_LOGIN_REQUIRED"
        ), Times.Once);
    }

    [Theory]
    [InlineData("ITEM_LOGIN_REQUIRED", PlaidItemStatus.LoginRequired)]
    [InlineData("ACCESS_NOT_GRANTED", PlaidItemStatus.LoginRequired)]
    [InlineData("INVALID_CREDENTIALS", PlaidItemStatus.LoginRequired)]
    [InlineData("INVALID_MFA", PlaidItemStatus.LoginRequired)]
    [InlineData("PRODUCT_NOT_AVAILABLE", PlaidItemStatus.Error)]
    [InlineData("INSTITUTION_DOWN", PlaidItemStatus.Error)]
    [InlineData("UNKNOWN_ERROR", PlaidItemStatus.Error)]
    public async Task HandleItemErrorAsync_WithVariousErrorCodes_ShouldMapToCorrectStatus(
        string errorCode,
        PlaidItemStatus expectedStatus)
    {
        // Arrange
        var connectionId = 1;
        var connection = new PlaidConnection
        {
            Id = connectionId,
            UserId = 1,
            InstitutionName = "Test Bank"
        };
        var error = new PlaidError
        {
            ErrorCode = errorCode,
            ErrorMessage = "Test error message"
        };

        _mockPlaidConnectionRepository.Setup(x => x.GetByIdAsync(connectionId))
            .ReturnsAsync(connection);

        _mockPlaidService.Setup(x => x.UpdateConnectionStatusAsync(
            connectionId,
            expectedStatus,
            It.IsAny<string>()
        )).Returns(Task.CompletedTask);

        // Act
        await _service.HandleItemErrorAsync(connectionId, error);

        // Assert
        _mockPlaidService.Verify(x => x.UpdateConnectionStatusAsync(
            connectionId,
            expectedStatus,
            It.IsAny<string>()
        ), Times.Once);
    }

    #endregion

    #region HandleUserPermissionRevokedAsync Tests

    [Fact]
    public async Task HandleUserPermissionRevokedAsync_ShouldCallPlaidService()
    {
        // Arrange
        var connectionId = 1;
        _mockPlaidService.Setup(x => x.HandleUserPermissionRevokedAsync(connectionId))
            .Returns(Task.CompletedTask);

        // Act
        await _service.HandleUserPermissionRevokedAsync(connectionId);

        // Assert
        _mockPlaidService.Verify(x => x.HandleUserPermissionRevokedAsync(connectionId), Times.Once);
    }

    [Fact]
    public async Task HandleUserPermissionRevokedAsync_WithError_ShouldThrowException()
    {
        // Arrange
        var connectionId = 1;
        _mockPlaidService.Setup(x => x.HandleUserPermissionRevokedAsync(connectionId))
            .ThrowsAsync(new InvalidOperationException("Connection not found"));

        // Act
        Func<Task> act = async () => await _service.HandleUserPermissionRevokedAsync(connectionId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    #endregion

    #region HandlePendingExpirationAsync Tests

    [Fact]
    public async Task HandlePendingExpirationAsync_WithItemId_ShouldLookupConnectionAndCallPlaidService()
    {
        // Arrange
        var itemId = "test_item_id";
        var expirationDate = DateTime.UtcNow.AddDays(7);
        var connection = new PlaidConnection { Id = 1, ItemId = itemId, UserId = 1 };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync(itemId))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.HandlePendingExpirationAsync(It.IsAny<int>(), It.IsAny<DateTime>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.HandlePendingExpirationAsync(itemId, expirationDate);

        // Assert
        _mockPlaidService.Verify(x => x.GetConnectionByItemIdAsync(itemId), Times.Once);
        _mockPlaidService.Verify(x => x.HandlePendingExpirationAsync(connection.Id, It.IsAny<DateTime>()), Times.Once);
    }

    [Fact]
    public async Task HandlePendingExpirationAsync_WithNonExistentItemId_ShouldNotCallPlaidService()
    {
        // Arrange
        var itemId = "unknown_item";
        var expirationDate = DateTime.UtcNow.AddDays(7);

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync(itemId))
            .ReturnsAsync((PlaidConnection?)null);

        // Act
        await _service.HandlePendingExpirationAsync(itemId, expirationDate);

        // Assert
        _mockPlaidService.Verify(x => x.GetConnectionByItemIdAsync(itemId), Times.Once);
        _mockPlaidService.Verify(x => x.HandlePendingExpirationAsync(It.IsAny<int>(), It.IsAny<DateTime>()), Times.Never);
    }

    #endregion

    #region HandlePendingDisconnectAsync Tests

    [Fact]
    public async Task HandlePendingDisconnectAsync_WithValidItemId_ShouldUpdateStatus()
    {
        // Arrange
        var itemId = "test_item_id";
        var disconnectDate = DateTime.UtcNow.AddDays(30);
        var connection = new PlaidConnection { Id = 1, ItemId = itemId, UserId = 1 };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync(itemId))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.PendingDisconnect, It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.HandlePendingDisconnectAsync(itemId, disconnectDate);

        // Assert
        _mockPlaidService.Verify(x => x.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.PendingDisconnect, It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region HandleLoginRepairedAsync Tests

    [Fact]
    public async Task HandleLoginRepairedAsync_WithValidItemId_ShouldUpdateStatusToActive()
    {
        // Arrange
        var itemId = "test_item_id";
        var connection = new PlaidConnection { Id = 1, ItemId = itemId, UserId = 1 };

        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync(itemId))
            .ReturnsAsync(connection);
        _mockPlaidService.Setup(x => x.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.Active, It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.HandleLoginRepairedAsync(itemId);

        // Assert
        _mockPlaidService.Verify(x => x.UpdateConnectionStatusAsync(connection.Id, PlaidItemStatus.Active, It.IsAny<string>()), Times.Once);
    }

    #endregion

    #region HandleNewAccountsAvailableAsync Tests

    [Fact]
    public async Task HandleNewAccountsAvailableAsync_WithKnownItem_CreatesAccountSelectionPrompt()
    {
        var connection = new PlaidConnection { Id = 7, UserId = 3, ItemId = "item_new_accts", InstitutionName = "Chase" };
        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("item_new_accts"))
            .ReturnsAsync(connection);

        await _service.HandleNewAccountsAvailableAsync("item_new_accts", Array.Empty<string>());

        _mockNotificationService.Verify(
            x => x.CreatePlaidNewAccountsNotificationAsync(3, 7, "Chase"), Times.Once);
    }

    [Fact]
    public async Task HandleNewAccountsAvailableAsync_WithUnknownItem_DoesNotNotify()
    {
        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("ghost"))
            .ReturnsAsync((PlaidConnection?)null);

        await _service.HandleNewAccountsAvailableAsync("ghost", Array.Empty<string>());

        _mockNotificationService.Verify(
            x => x.CreatePlaidNewAccountsNotificationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region Update-mode webhook wiring (Plaid prod-readiness checklist)

    [Fact]
    public async Task ProcessWebhookAsync_PendingDisconnect_SetsStatusAndCreatesUpdateModePrompt()
    {
        var connection = new PlaidConnection { Id = 5, UserId = 2, ItemId = "item_pd", InstitutionName = "Ally" };
        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("item_pd")).ReturnsAsync(connection);

        await _service.ProcessWebhookAsync(new PlaidWebhookRequest
        {
            WebhookType = "ITEM",
            WebhookCode = "PENDING_DISCONNECT",
            ItemId = "item_pd"
        });

        _mockPlaidService.Verify(
            x => x.UpdateConnectionStatusAsync(5, PlaidItemStatus.PendingDisconnect, It.IsAny<string?>()),
            Times.Once);
        _mockNotificationService.Verify(
            x => x.CreatePlaidUpdateNotificationAsync(2, 5, "Ally", "PENDING_DISCONNECT"), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_LoginRepaired_ReactivatesAndDismissesPrompts()
    {
        var connection = new PlaidConnection { Id = 6, UserId = 2, ItemId = "item_lr", InstitutionName = "Ally" };
        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("item_lr")).ReturnsAsync(connection);

        await _service.ProcessWebhookAsync(new PlaidWebhookRequest
        {
            WebhookType = "ITEM",
            WebhookCode = "LOGIN_REPAIRED",
            ItemId = "item_lr"
        });

        _mockPlaidService.Verify(
            x => x.UpdateConnectionStatusAsync(6, PlaidItemStatus.Active, It.IsAny<string?>()), Times.Once);
        // The scary "connection needs attention" prompt flips to resolved — the item healed
        // outside our app, so the user must not be nagged into a pointless re-auth.
        _mockNotificationService.Verify(
            x => x.UpdateConnectionNotificationToResolvedAsync(2, 6, "Ally"), Times.Once);
    }

    [Fact]
    public async Task ProcessWebhookAsync_NewAccountsAvailable_RoutesToAccountSelectionPrompt()
    {
        var connection = new PlaidConnection { Id = 8, UserId = 4, ItemId = "item_na", InstitutionName = "Chase" };
        _mockPlaidService.Setup(x => x.GetConnectionByItemIdAsync("item_na")).ReturnsAsync(connection);

        await _service.ProcessWebhookAsync(new PlaidWebhookRequest
        {
            WebhookType = "ITEM",
            WebhookCode = "NEW_ACCOUNTS_AVAILABLE",
            ItemId = "item_na"
        });

        _mockNotificationService.Verify(
            x => x.CreatePlaidNewAccountsNotificationAsync(4, 8, "Chase"), Times.Once);
    }

    [Fact]
    public async Task CreateUpdateTokenAsync_PassesAccountSelectionFlagThrough()
    {
        _mockPlaidService
            .Setup(x => x.CreateUpdateTokenAsync(1, 9, true))
            .ReturnsAsync("link-update-token");

        var response = await _service.CreateUpdateTokenAsync(1, 9, accountSelectionEnabled: true);

        response.Success.Should().BeTrue();
        response.LinkToken.Should().Be("link-update-token");
        _mockPlaidService.Verify(x => x.CreateUpdateTokenAsync(1, 9, true), Times.Once);
    }

    #endregion
}
