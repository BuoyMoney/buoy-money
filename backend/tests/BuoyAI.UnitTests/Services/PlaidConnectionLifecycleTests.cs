using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BuoyAI.Core.DTOs.Plaid;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BuoyAI.UnitTests.Services;

/// <summary>
/// Covers the PR2 connection-lifecycle fixes that don't depend on the (unmockable) Plaid SDK:
/// sync-failure status transitions, TRANSACTIONS_REMOVED purging, and webhook payload binding.
/// </summary>
public class PlaidConnectionLifecycleTests
{
  private readonly Mock<IUserRepository> _userRepo = new();
  private readonly Mock<IAccountRepository> _accountRepo = new();
  private readonly Mock<ITransactionRepository> _txnRepo = new();
  private readonly Mock<IPlaidConnectionRepository> _connRepo = new();
  private readonly Mock<INotificationService> _notifications = new();
  private readonly Mock<IEncryptionService> _encryption = new();
  private readonly Mock<IPlaidUsageTracker> _usageTracker = new();
  private readonly Mock<IEmailService> _email = new();

  private PlaidService CreateService()
  {
    var config = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Plaid:ClientId"] = "test-client",
        ["Plaid:SecretKey"] = "test-secret",
        ["Plaid:Environment"] = "sandbox",
      })
      .Build();

    var metrics = new BuoyAiMetrics(new TestMeterFactory());

    return new PlaidService(
      config,
      _userRepo.Object,
      _accountRepo.Object,
      _txnRepo.Object,
      _connRepo.Object,
      _notifications.Object,
      new BankConnectionAlertService(_notifications.Object, _email.Object, _userRepo.Object, _connRepo.Object, Mock.Of<ILogger<BankConnectionAlertService>>()),
      _encryption.Object,
      _usageTracker.Object,
      Mock.Of<ITransactionIngestService>(),
      Mock.Of<ILogger<PlaidService>>(),
      metrics);
  }

  // ── HandleSyncFailureAsync (finding #2) ─────────────────────────────

  [Fact]
  public async Task HandleSyncFailureAsync_WithLoginRequiredError_FlipsToLoginRequiredAndNotifies()
  {
    var connection = new PlaidConnection
    {
      Id = 5,
      UserId = 42,
      InstitutionName = "Test Bank",
      Status = PlaidItemStatus.Active,
      ConsecutiveFailures = 0,
    };
    _connRepo.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(connection);

    var service = CreateService();
    await service.HandleSyncFailureAsync(5, "ITEM_LOGIN_REQUIRED: the login details are no longer valid");

    connection.Status.Should().Be(PlaidItemStatus.LoginRequired);
    connection.RequiresUserAction.Should().BeTrue();
    connection.LastSyncError.Should().Contain("ITEM_LOGIN_REQUIRED");
    _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(42, 5, "Test Bank", "ITEM_LOGIN_REQUIRED"), Times.Once);
    _connRepo.Verify(r => r.UpdateAsync(connection), Times.Once);
  }

  [Fact]
  public async Task HandleSyncFailureAsync_LoginRequired_AlsoEmailsTheUserOnce()
  {
    var connection = new PlaidConnection
    {
      Id = 5, UserId = 42, InstitutionName = "Test Bank", Status = PlaidItemStatus.Active,
    };
    _connRepo.Setup(r => r.GetByIdAsync(5)).ReturnsAsync(connection);
    _userRepo.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(new User { Id = 42, Email = "kat@home.test", FirstName = "Kat" });
    _connRepo.Setup(r => r.TryClaimAlertEmailAsync(5, It.IsAny<DateTime>())).ReturnsAsync(true);

    var service = CreateService();
    await service.HandleSyncFailureAsync(5, "ITEM_LOGIN_REQUIRED: the login details are no longer valid");

    _email.Verify(e => e.SendPlaidConnectionIssueAsync("kat@home.test", "Kat", "Test Bank"), Times.Once);
    connection.AlertSentAt.Should().NotBeNull();
  }

  [Fact]
  public async Task HandlePendingExpirationAsync_MarksTheConnectionAndAlertsTheUser()
  {
    var connection = new PlaidConnection
    {
      Id = 8, UserId = 42, InstitutionName = "Test Bank", Status = PlaidItemStatus.Active,
    };
    _connRepo.Setup(r => r.GetByIdAsync(8)).ReturnsAsync(connection);
    _userRepo.Setup(r => r.GetByIdAsync(42)).ReturnsAsync(new User { Id = 42, Email = "kat@home.test", FirstName = "Kat" });

    var service = CreateService();
    await service.HandlePendingExpirationAsync(8, DateTime.UtcNow.AddDays(7));

    connection.Status.Should().Be(PlaidItemStatus.PendingExpiration);
    connection.RequiresUserAction.Should().BeTrue();
    _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(42, 8, "Test Bank", "PENDING_EXPIRATION"), Times.Once);
    // The email says "disconnected", which is not true yet, so only the in-app alert goes out.
    _email.Verify(e => e.SendPlaidConnectionIssueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
  }

  [Fact]
  public async Task UpdateConnectionStatusAsync_BackToActive_ClearsTheAlertSoTheNextOutageEmailsAgain()
  {
    var connection = new PlaidConnection
    {
      Id = 9, UserId = 42, Status = PlaidItemStatus.LoginRequired, RequiresUserAction = true,
      AlertSentAt = DateTime.UtcNow.AddHours(-3),
    };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);

    var service = CreateService();
    await service.UpdateConnectionStatusAsync(9, PlaidItemStatus.Active);

    connection.AlertSentAt.Should().BeNull();
    connection.RequiresUserAction.Should().BeFalse();
  }

  [Fact]
  public async Task HandleSyncFailureAsync_TransientErrorBelowThreshold_StaysActiveAndDoesNotNotify()
  {
    var connection = new PlaidConnection
    {
      Id = 6,
      UserId = 1,
      InstitutionName = "Test Bank",
      Status = PlaidItemStatus.Active,
      ConsecutiveFailures = 0,
    };
    _connRepo.Setup(r => r.GetByIdAsync(6)).ReturnsAsync(connection);

    var service = CreateService();
    await service.HandleSyncFailureAsync(6, "INSTITUTION_DOWN: temporary outage");

    connection.Status.Should().Be(PlaidItemStatus.Active);
    connection.ConsecutiveFailures.Should().Be(1);
    connection.LastSyncError.Should().Contain("INSTITUTION_DOWN");
    _notifications.Verify(
      n => n.CreatePlaidUpdateNotificationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()),
      Times.Never);
  }

  [Fact]
  public async Task HandleSyncFailureAsync_TransientErrorAtThreshold_FlipsToErrorAndNotifies()
  {
    // 4th consecutive failure (MaxConsecutiveSyncFailures) flips a non-login error to Error so it
    // drops out of the sync set instead of being hammered forever.
    var connection = new PlaidConnection
    {
      Id = 7,
      UserId = 9,
      InstitutionName = "Flaky CU",
      Status = PlaidItemStatus.Active,
      ConsecutiveFailures = 3,
    };
    _connRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(connection);

    var service = CreateService();
    await service.HandleSyncFailureAsync(7, "INSTITUTION_NOT_RESPONDING: still down");

    connection.Status.Should().Be(PlaidItemStatus.Error);
    connection.ConsecutiveFailures.Should().Be(4);
    connection.RequiresUserAction.Should().BeTrue();
    _notifications.Verify(n => n.CreatePlaidUpdateNotificationAsync(9, 7, "Flaky CU", "SYNC_FAILED"), Times.Once);
  }

  [Fact]
  public async Task HandleSyncFailureAsync_UnknownConnection_IsNoOp()
  {
    _connRepo.Setup(r => r.GetByIdAsync(999)).ReturnsAsync((PlaidConnection?)null);

    var service = CreateService();
    await service.HandleSyncFailureAsync(999, "ITEM_LOGIN_REQUIRED");

    _connRepo.Verify(r => r.UpdateAsync(It.IsAny<PlaidConnection>()), Times.Never);
    _notifications.Verify(
      n => n.CreatePlaidUpdateNotificationAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()),
      Times.Never);
  }

  // ── FetchAllTransactionsAsync sync-path error surfacing ─────────────

  [Fact]
  public async Task FetchAllTransactionsAsync_ResponseCarriesPlaidError_ThrowsWithErrorCode()
  {
    // Going.Plaid returns ITEM_LOGIN_REQUIRED as Error != null (not an exception) with a null
    // page. FetchAllTransactionsAsync must surface it as a throw carrying the raw error CODE so
    // the sync path's catch routes it to HandleSyncFailureAsync and the login-required match
    // fires — instead of being mistaken for a successful empty sync.
    var errorResponse = new Going.Plaid.Transactions.TransactionsGetResponse
    {
      Error = new Going.Plaid.Entity.PlaidError
      {
        ErrorCode = "ITEM_LOGIN_REQUIRED",
        ErrorMessage = "the login details of this item have changed",
      },
    };

    var service = CreateService();

    var act = async () => await service.FetchAllTransactionsAsync(
      "access-token",
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 31),
      _ => Task.FromResult(errorResponse));

    (await act.Should().ThrowAsync<InvalidOperationException>())
      .Which.Message.Should().Contain("ITEM_LOGIN_REQUIRED");
  }

  [Fact]
  public async Task FetchAllTransactionsAsync_GenuinelyEmptySuccessfulPage_ReturnsEmptyWithoutThrowing()
  {
    // Error == null with an empty page is a real "no new transactions" sync: it must NOT throw
    // and must not be classified as a failure.
    var emptyResponse = new Going.Plaid.Transactions.TransactionsGetResponse
    {
      Transactions = new List<Going.Plaid.Entity.Transaction>(),
      TotalTransactions = 0,
    };

    var service = CreateService();

    var (firstPage, transactions) = await service.FetchAllTransactionsAsync(
      "access-token",
      new DateOnly(2026, 1, 1),
      new DateOnly(2026, 1, 31),
      _ => Task.FromResult(emptyResponse));

    firstPage.Should().BeSameAs(emptyResponse);
    transactions.Should().BeEmpty();
  }

  // ── RemoveTransactionsAsync (finding #4) ────────────────────────────

  [Fact]
  public async Task RemoveTransactionsAsync_DeletesUntouchedButPreservesUserTouchedRows()
  {
    var untouched = new Transaction { Id = 100, PlaidTransactionId = "plaid_untouched" };
    var userCategorized = new Transaction { Id = 101, PlaidTransactionId = "plaid_touched", IsUserCategorized = true };

    _txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync("plaid_untouched")).ReturnsAsync(untouched);
    _txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync("plaid_touched")).ReturnsAsync(userCategorized);
    _txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync("plaid_missing")).ReturnsAsync((Transaction?)null);

    var service = CreateService();
    var removed = await service.RemoveTransactionsAsync(new[] { "plaid_untouched", "plaid_touched", "plaid_missing" });

    removed.Should().Be(1);
    _txnRepo.Verify(r => r.DeleteAsync(100), Times.Once);
    _txnRepo.Verify(r => r.DeleteAsync(101), Times.Never);
  }

  [Fact]
  public async Task RemoveTransactionsAsync_DedupesAndSkipsEmptyIds()
  {
    var txn = new Transaction { Id = 200, PlaidTransactionId = "dup" };
    _txnRepo.Setup(r => r.GetByPlaidTransactionIdAsync("dup")).ReturnsAsync(txn);

    var service = CreateService();
    var removed = await service.RemoveTransactionsAsync(new[] { "dup", "dup", "", null! });

    removed.Should().Be(1);
    _txnRepo.Verify(r => r.DeleteAsync(200), Times.Once);
  }

  // ── Webhook payload binding (finding #4) ────────────────────────────

  [Fact]
  public void PlaidWebhookRequest_BindsSnakeCasePayload_IncludingRemovedTransactions()
  {
    // Plaid sends snake_case; the API JSON policy is camelCase. The [JsonPropertyName] pins on the
    // DTO must make the inbound fields bind so the TRANSACTIONS_REMOVED handler can act on them.
    const string payload = """
    {
      "webhook_type": "TRANSACTIONS",
      "webhook_code": "TRANSACTIONS_REMOVED",
      "item_id": "item-123",
      "removed_transactions": ["txn_a", "txn_b"]
    }
    """;

    var options = new JsonSerializerOptions
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      PropertyNameCaseInsensitive = true,
    };

    var webhook = JsonSerializer.Deserialize<PlaidWebhookRequest>(payload, options);

    webhook.Should().NotBeNull();
    webhook!.WebhookType.Should().Be("TRANSACTIONS");
    webhook.WebhookCode.Should().Be("TRANSACTIONS_REMOVED");
    webhook.ItemId.Should().Be("item-123");
    webhook.RemovedTransactions.Should().Equal("txn_a", "txn_b");
  }

  // ── Non-Plaid connections must never reach Plaid ────────────────────

  [Fact]
  public async Task EveryPerConnectionPlaidEntryPoint_RefusesASimpleFinConnection_BeforeDecrypting()
  {
    var connection = new PlaidConnection { Id = 9, UserId = 42, Provider = BankProvider.SimpleFin, AccessToken = "enc::https://u:p@bridge.test/simplefin" };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);
    var service = CreateService();

    await service.Invoking(s => s.CreateUpdateTokenAsync(42, 9)).Should().ThrowAsync<ArgumentException>();
    await service.Invoking(s => s.ReconnectAccountAsync(42, 9)).Should().ThrowAsync<ArgumentException>();
    await service.Invoking(s => s.DisconnectAccountAsync(42, 9)).Should().ThrowAsync<ArgumentException>();
    await service.Invoking(s => s.TryRemovePlaidItemAsync(connection)).Should().ThrowAsync<ArgumentException>();
    await service.Invoking(s => s.HandleItemUpdateAsync(9, "public-token")).Should().ThrowAsync<ArgumentException>();
    connection.Status.Should().NotBe(PlaidItemStatus.Error);

    _encryption.Verify(e => e.Decrypt(It.IsAny<string>()), Times.Never);
  }

  // -- Removing a bank takes its accounts out of net worth -----------------

  [Fact]
  public async Task DisconnectAccountAsync_DeactivatesTheConnectionsAccounts_AndKeepsThemRecoverable()
  {
    var connection = new PlaidConnection { Id = 9, UserId = 7, Provider = BankProvider.Plaid, AccessToken = "enc", IsActive = true };
    var checking = new Account { Id = 1, PlaidConnectionId = 9, IsActive = true };
    var card = new Account { Id = 2, PlaidConnectionId = 9, IsActive = true };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);
    _encryption.Setup(e => e.Decrypt("enc")).Returns(string.Empty);
    _accountRepo.Setup(r => r.GetByConnectionIdAsync(9)).ReturnsAsync(new[] { checking, card });

    await CreateService().DisconnectAccountAsync(7, 9);

    connection.IsActive.Should().BeFalse();
    checking.IsActive.Should().BeFalse();
    card.IsActive.Should().BeFalse();
    _accountRepo.Verify(r => r.UpdateAsync(It.IsAny<Account>()), Times.Exactly(2));
    _accountRepo.Verify(r => r.DeleteAsync(It.IsAny<int>()), Times.Never);
  }

  [Fact]
  public async Task DisconnectAccountAsync_WhenConnectionWriteFails_AccountsAreAlreadyInactive()
  {
    var connection = new PlaidConnection { Id = 9, UserId = 7, Provider = BankProvider.Plaid, AccessToken = "enc", IsActive = true };
    var checking = new Account { Id = 1, PlaidConnectionId = 9, IsActive = true };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);
    _connRepo.Setup(r => r.UpdateAsync(It.IsAny<PlaidConnection>())).ThrowsAsync(new InvalidOperationException("db down"));
    _encryption.Setup(e => e.Decrypt("enc")).Returns(string.Empty);
    _accountRepo.Setup(r => r.GetByConnectionIdAsync(9)).ReturnsAsync(new[] { checking });

    var act = () => CreateService().DisconnectAccountAsync(7, 9);

    await act.Should().ThrowAsync<InvalidOperationException>();
    // Never a dead connection with accounts still counting in net worth.
    checking.IsActive.Should().BeFalse();
  }

  [Fact]
  public async Task HandleUserPermissionRevokedAsync_WhenConnectionWriteFails_AccountsAreAlreadyInactive()
  {
    var connection = new PlaidConnection { Id = 9, UserId = 7, Provider = BankProvider.Plaid, IsActive = true };
    var checking = new Account { Id = 1, PlaidConnectionId = 9, IsActive = true };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);
    _connRepo.Setup(r => r.UpdateAsync(It.IsAny<PlaidConnection>())).ThrowsAsync(new InvalidOperationException("db down"));
    _accountRepo.Setup(r => r.GetByConnectionIdAsync(9)).ReturnsAsync(new[] { checking });

    var act = () => CreateService().HandleUserPermissionRevokedAsync(9);

    await act.Should().ThrowAsync<InvalidOperationException>();
    checking.IsActive.Should().BeFalse();
  }

  [Fact]
  public async Task DisconnectAccountAsync_ForSomeoneElsesConnection_TouchesNoAccounts()
  {
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(new PlaidConnection { Id = 9, UserId = 1, Provider = BankProvider.Plaid });

    var act = () => CreateService().DisconnectAccountAsync(7, 9);

    await act.Should().ThrowAsync<ArgumentException>();
    _accountRepo.Verify(r => r.UpdateAsync(It.IsAny<Account>()), Times.Never);
  }

  [Fact]
  public async Task HandleUserPermissionRevokedAsync_DeactivatesTheConnectionsAccounts()
  {
    var connection = new PlaidConnection { Id = 9, UserId = 7, Provider = BankProvider.Plaid, IsActive = true };
    var checking = new Account { Id = 1, PlaidConnectionId = 9, IsActive = true };
    _connRepo.Setup(r => r.GetByIdAsync(9)).ReturnsAsync(connection);
    _accountRepo.Setup(r => r.GetByConnectionIdAsync(9)).ReturnsAsync(new[] { checking });

    await CreateService().HandleUserPermissionRevokedAsync(9);

    connection.IsActive.Should().BeFalse();
    checking.IsActive.Should().BeFalse();
  }

  [Fact]
  public void ApplySyncedAccount_OnRelink_ReactivatesTheAccountUnderTheNewConnection()
  {
    var stored = new Account { Id = 1, PlaidAccountId = "acc-1", PlaidConnectionId = 9, IsActive = false, CurrentBalance = 5m };
    var fromPlaid = new Going.Plaid.Entity.Account
    {
      AccountId = "acc-1",
      Name = "Checking",
      Mask = "0000",
      Balances = new Going.Plaid.Entity.AccountBalance { Current = 250.5m, Available = 200m },
    };

    PlaidService.ApplySyncedAccount(stored, fromPlaid, connectionId: 12, userFamilyId: 3);

    stored.IsActive.Should().BeTrue();
    stored.PlaidConnectionId.Should().Be(12);
    stored.CurrentBalance.Should().Be(250.5m);
    stored.FamilyId.Should().Be(3);
  }
}
