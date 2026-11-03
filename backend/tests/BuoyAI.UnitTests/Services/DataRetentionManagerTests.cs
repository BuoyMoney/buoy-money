using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Moq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Services;
using BuoyAI.Infrastructure.Data;
using FluentAssertions;

namespace BuoyAI.UnitTests.Services;

public class DataRetentionManagerTests : IDisposable
{
    private readonly BuoyAIDbContext _context;
    private readonly Mock<IDataRetentionAuditService> _auditServiceMock;
    private readonly Mock<ILogger<DataRetentionManager>> _loggerMock;
    private readonly Mock<IStripeService> _stripeServiceMock;
    private readonly Mock<IBankAggregator> _plaidServiceMock;
    private readonly IConfiguration _configuration;
    private readonly DataRetentionManager _dataRetentionManager;

    public DataRetentionManagerTests()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        _context = new BuoyAIDbContext(options);
        _auditServiceMock = new Mock<IDataRetentionAuditService>();
        _loggerMock = new Mock<ILogger<DataRetentionManager>>();
        _stripeServiceMock = new Mock<IStripeService>();
        // Default: the customer belongs to this environment, so Stripe actually deletes it.
        // The false case (another environment owns it) has its own test below.
        _stripeServiceMock
            .Setup(s => s.DeleteCustomerAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _plaidServiceMock = new Mock<IBankAggregator>();
        _plaidServiceMock.SetupGet(p => p.Provider).Returns(BankProvider.Plaid);
        // Default: Plaid revocation succeeds. Admin allowlist covers the admin-guard test.
        _plaidServiceMock
            .Setup(p => p.TryRemoveConnectionAsync(It.IsAny<PlaidConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Emails:0"] = "operator@buoy-ai.com",
                ["DataRetention:PlaidChurnGraceDays"] = "60",
            })
            .Build();

        _dataRetentionManager = new DataRetentionManager(
            _context,
            _loggerMock.Object,
            _auditServiceMock.Object,
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddSingleton(_stripeServiceMock.Object).BuildServiceProvider(),
            new Lazy<IBankAggregatorRegistry>(() => new BankAggregatorRegistry(new[] { _plaidServiceMock.Object })),
            _configuration
        );
    }

    // ── Churned-account Plaid item removal (RFC: stop the $0.30/item/mo leak) ──────────

    private async Task<(User user, PlaidConnection connection)> SeedChurnedWithConnectionAsync(
        int id, string email, DateTime? trialEndDate, SubscriptionTier? tier = null, bool isActiveConnection = true)
    {
        var user = new User
        {
            Id = id,
            Email = email,
            PasswordHash = "hash",
            SubscriptionTier = tier,
            TrialEndDate = trialEndDate,
            IsTrialActive = false,
            CreatedAt = DateTime.UtcNow.AddDays(-400),
        };
        var connection = new PlaidConnection
        {
            Id = id,
            UserId = id,
            Provider = BankProvider.Plaid,
            AccessToken = "encrypted-token",
            ItemId = $"item-{id}",
            InstitutionName = "Test Bank",
            IsActive = isActiveConnection,
            CreatedAt = DateTime.UtcNow.AddDays(-300),
            UpdatedAt = DateTime.UtcNow.AddDays(-200),
        };
        _context.Users.Add(user);
        _context.PlaidConnections.Add(connection);
        await _context.SaveChangesAsync();
        return (user, connection);
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_RevokesItem_ForTrialLapsedPastGrace()
    {
        // Trial ended 90 days ago, no subscription → churned past the 60-day grace.
        await SeedChurnedWithConnectionAsync(1, "churned@example.com", DateTime.UtcNow.AddDays(-90));

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(1);
        _plaidServiceMock.Verify(p => p.TryRemoveConnectionAsync(
            It.Is<PlaidConnection>(c => c.Id == 1), It.IsAny<CancellationToken>()), Times.Once);
        var conn = await _context.PlaidConnections.FindAsync(1);
        conn!.PlaidRemovedAt.Should().NotBeNull();
        conn.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_DeactivatesTheRevokedConnectionsAccounts_KeepsOthers()
    {
        await SeedChurnedWithConnectionAsync(1, "churned@example.com", DateTime.UtcNow.AddDays(-90));
        _context.Accounts.Add(new Account { Id = 1, UserId = 1, PlaidConnectionId = 1, PlaidAccountId = "a1", Name = "Checking", IsActive = true });
        _context.Accounts.Add(new Account { Id = 2, UserId = 1, PlaidConnectionId = null, PlaidAccountId = "a2", Name = "Imported", IsActive = true });
        await _context.SaveChangesAsync();

        await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        (await _context.Accounts.FindAsync(1))!.IsActive.Should().BeFalse();
        (await _context.Accounts.FindAsync(2))!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_SkipsAdminAccounts()
    {
        await SeedChurnedWithConnectionAsync(1, "operator@buoy-ai.com", DateTime.UtcNow.AddDays(-90));

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
        _plaidServiceMock.Verify(p => p.TryRemoveConnectionAsync(
            It.IsAny<PlaidConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_SkipsFamilyInheritedPremium()
    {
        await SeedChurnedWithConnectionAsync(
            1, "member@example.com", DateTime.UtcNow.AddDays(-90), SubscriptionTier.PremiumInherited);

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_SkipsWithinGraceWindow()
    {
        // Trial ended only 10 days ago — inside the 60-day grace.
        await SeedChurnedWithConnectionAsync(1, "recent@example.com", DateTime.UtcNow.AddDays(-10));

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_SkipsUserWithActiveSubscription()
    {
        var (user, _) = await SeedChurnedWithConnectionAsync(
            1, "paying@example.com", DateTime.UtcNow.AddDays(-90));
        _context.UserSubscriptions.Add(new UserSubscription
        {
            Id = 1,
            UserId = user.Id,
            Tier = SubscriptionTier.Premium,
            Status = UserSubscriptionStatus.Active,
            StartDate = DateTime.UtcNow.AddDays(-30),
            NextBillingDate = DateTime.UtcNow.AddDays(1),
        });
        await _context.SaveChangesAsync();

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_DoesNotMark_OnTransientPlaidFailure()
    {
        await SeedChurnedWithConnectionAsync(1, "churned@example.com", DateTime.UtcNow.AddDays(-90));
        _plaidServiceMock
            .Setup(p => p.TryRemoveConnectionAsync(It.IsAny<PlaidConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false); // Plaid outage — retry next run

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
        var conn = await _context.PlaidConnections.FindAsync(1);
        conn!.PlaidRemovedAt.Should().BeNull(); // left for retry, not locked in
    }

    [Fact]
    public async Task CleanupChurnedAccountPlaidItemsAsync_SkipsAlreadyRemoved()
    {
        var (_, connection) = await SeedChurnedWithConnectionAsync(
            1, "churned@example.com", DateTime.UtcNow.AddDays(-90));
        connection.PlaidRemovedAt = DateTime.UtcNow.AddDays(-5);
        await _context.SaveChangesAsync();

        var removed = await _dataRetentionManager.CleanupChurnedAccountPlaidItemsAsync();

        removed.Should().Be(0);
        _plaidServiceMock.Verify(p => p.TryRemoveConnectionAsync(
            It.IsAny<PlaidConnection>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CleanupClosedUserAccountsAsync_ShouldDeleteAccountsDeletedMoreThan30DaysAgo()
    {
        // Arrange
        var oldUser = new User
        {
            Id = 1,
            Email = "old@example.com",
            PasswordHash = "hash",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-35),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };

        var recentUser = new User
        {
            Id = 2,
            Email = "recent@example.com",
            PasswordHash = "hash",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-25),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        };

        _context.Users.AddRange(oldUser, recentUser);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.CleanupClosedUserAccountsAsync();

        // Assert
        result.Should().Be(1);

        var remainingUsers = await _context.Users.ToListAsync();
        remainingUsers.Should().HaveCount(1);
        remainingUsers.First().Id.Should().Be(2); // Recent user should remain
    }

    [Fact]
    public async Task CleanupSecurityLogsAsync_ShouldDeleteLogsOlderThan90Days()
    {
        // Arrange
        var oldLog = new SecurityAuditLog
        {
            Id = 1,
            UserId = 1,
            EventType = "login_attempt",
            Description = "User attempted to log in",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow.AddDays(-95)
        };

        var recentLog = new SecurityAuditLog
        {
            Id = 2,
            UserId = 1,
            EventType = "login_success",
            Description = "User logged in successfully",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow.AddDays(-85)
        };

        _context.SecurityAuditLogs.AddRange(oldLog, recentLog);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.CleanupSecurityLogsAsync();

        // Assert
        result.Should().Be(1);

        var remainingLogs = await _context.SecurityAuditLogs.ToListAsync();
        remainingLogs.Should().HaveCount(1);
        remainingLogs.First().Id.Should().Be(2); // Recent log should remain
    }

    [Fact]
    public async Task DeleteUserDataImmediatelyAsync_ShouldMarkUserAsDeleted()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "hash",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _dataRetentionManager.DeleteUserDataImmediatelyAsync(1);

        // Assert
        var updatedUser = await _context.Users.FindAsync(1);
        updatedUser.Should().NotBeNull();
        updatedUser!.IsDeleted.Should().BeTrue();
        updatedUser.DeletedAt.Should().NotBeNull();
        updatedUser.DeletionReason.Should().Be("immediate_request");
    }

    [Fact]
    public async Task CleanupClosedUserAccountsAsync_WithStripeCustomer_ShouldDeleteStripeCustomer()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Email = "paid@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-35),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _dataRetentionManager.CleanupClosedUserAccountsAsync();

        // Assert — the Stripe customer is deleted (cascades to subscriptions + payment methods)
        _stripeServiceMock.Verify(s => s.DeleteCustomerAsync("cus_test123"), Times.Once);
    }

    [Fact]
    public async Task CleanupClosedUserAccountsAsync_WithoutStripeCustomer_ShouldNotCallStripe()
    {
        // Arrange — a user who never subscribed has no Stripe customer id
        var user = new User
        {
            Id = 1,
            Email = "free@example.com",
            PasswordHash = "hash",
            StripeCustomerId = null,
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-35),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _dataRetentionManager.CleanupClosedUserAccountsAsync();

        // Assert
        _stripeServiceMock.Verify(s => s.DeleteCustomerAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CleanupClosedUserAccountsAsync_WhenStripeDeleteFails_ShouldStillDeleteLocalData()
    {
        // Arrange — Stripe deletion is fail-open: a Stripe error must not block GDPR/CCPA local deletion
        _stripeServiceMock
            .Setup(s => s.DeleteCustomerAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Stripe API unavailable"));

        var user = new User
        {
            Id = 1,
            Email = "paid@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_test123",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow.AddDays(-35),
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-40)
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.CleanupClosedUserAccountsAsync();

        // Assert — local deletion still succeeded despite the Stripe failure
        result.Should().Be(1);
        (await _context.Users.FindAsync(1)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteUserDataImmediatelyAsync_WithStripeCustomer_ShouldDeleteStripeCustomer()
    {
        // Arrange — the immediate (GDPR) path must also delete the Stripe customer, because it
        // nulls StripeCustomerId during anonymization and the later 30-day sweep can't recover it.
        var user = new User
        {
            Id = 1,
            Email = "paid@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_immediate",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _dataRetentionManager.DeleteUserDataImmediatelyAsync(1);

        // Assert
        _stripeServiceMock.Verify(s => s.DeleteCustomerAsync("cus_immediate"), Times.Once);
    }

    [Fact]
    public async Task DeleteUserDataImmediatelyAsync_WhenStripeAuditWriteFails_ShouldNotThrow()
    {
        // Arrange — the retention audit service rethrows on failure. By the time we audit the
        // Stripe deletion the user's data is already gone, so a lost audit line must not fail
        // the request (it would 500 a deletion that actually succeeded).
        _auditServiceMock
            .Setup(a => a.LogRetentionExecutionAsync(It.IsAny<object>()))
            .ThrowsAsync(new Exception("audit store unavailable"));

        var user = new User
        {
            Id = 1,
            Email = "paid@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_audit",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var act = async () => await _dataRetentionManager.DeleteUserDataImmediatelyAsync(1);

        // Assert
        await act.Should().NotThrowAsync();
        _stripeServiceMock.Verify(s => s.DeleteCustomerAsync("cus_audit"), Times.Once);
    }

    [Fact]
    public async Task DeleteUserDataImmediatelyAsync_WhenStripeDeclinesForeignCustomer_AuditsSkipNotDelete()
    {
        // Staging and production share one live Stripe account, so the delete can be
        // declined. The audit trail is the record of what was actually destroyed, so it must
        // not claim a Stripe deletion that never happened. Local deletion still proceeds.
        _stripeServiceMock
            .Setup(s => s.DeleteCustomerAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        var audited = new List<string>();
        _auditServiceMock
            .Setup(a => a.LogRetentionExecutionAsync(It.IsAny<object>()))
            .Callback<object>(entry =>
            {
                var action = entry.GetType().GetProperty("Action")?.GetValue(entry) as string;
                if (action != null) audited.Add(action);
            })
            .Returns(Task.CompletedTask);

        var user = new User
        {
            Id = 1,
            Email = "foreign@example.com",
            PasswordHash = "hash",
            StripeCustomerId = "cus_owned_by_prod",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        await _dataRetentionManager.DeleteUserDataImmediatelyAsync(1);

        // Assert — we tried, were declined, and said so honestly
        _stripeServiceMock.Verify(s => s.DeleteCustomerAsync("cus_owned_by_prod"), Times.Once);
        audited.Should().Contain("STRIPE_CUSTOMER_DELETE_SKIPPED");
        audited.Should().NotContain("STRIPE_CUSTOMER_DELETE");
    }

    [Fact]
    public async Task VerifyDataDeletionAsync_ShouldReturnTrueWhenUserIsMarkedAsDeleted()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "hash",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.VerifyDataDeletionAsync(1);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyDataDeletionAsync_ShouldReturnFalseWhenUserIsNotDeleted()
    {
        // Arrange
        var user = new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "hash",
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.VerifyDataDeletionAsync(1);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task CleanupErrorLogsAsync_ShouldDeleteLogsOlderThan365Days()
    {
        // Arrange
        var oldError = new ErrorLog
        {
            Id = 1,
            ErrorType = "Error",
            Message = "Old error",
            StackTrace = "Stack trace",
            CreatedAt = DateTime.UtcNow.AddDays(-370)
        };

        var recentError = new ErrorLog
        {
            Id = 2,
            ErrorType = "Error",
            Message = "Recent error",
            StackTrace = "Stack trace",
            CreatedAt = DateTime.UtcNow.AddDays(-300)
        };

        _context.ErrorLogs.AddRange(oldError, recentError);
        await _context.SaveChangesAsync();

        // Act
        var result = await _dataRetentionManager.CleanupErrorLogsAsync();

        // Assert
        result.Should().Be(1);

        var remainingErrors = await _context.ErrorLogs.ToListAsync();
        remainingErrors.Should().HaveCount(1);
        remainingErrors.First().Id.Should().Be(2); // Recent error should remain
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}
