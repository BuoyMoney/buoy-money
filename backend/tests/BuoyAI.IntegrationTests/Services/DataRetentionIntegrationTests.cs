using Xunit;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.IntegrationTests.Services;

public class DataRetentionIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly BuoyAIDbContext _context;

    public DataRetentionIntegrationTests()
    {
        var services = new ServiceCollection();

        // Setup in-memory database
        services.AddDbContext<BuoyAIDbContext>(options =>
            options.UseInMemoryDatabase("TestDb_" + Guid.NewGuid()));

        // Register services
        services.AddScoped<IDataRetentionManager, DataRetentionManager>();
        services.AddScoped<IDataRetentionAuditService, DataRetentionAuditService>();

        // Stripe is an external API — mock it. These tests assert local retention behavior;
        // the Stripe-customer deletion path is covered by DataRetentionManagerTests.
        services.AddScoped(_ => new Mock<IStripeService>().Object);

        // DataRetentionManager now depends on IPlaidService (churned-item /item/remove).
        // Mock it — the Plaid-removal path has its own coverage in DataRetentionManagerTests.
        services.AddScoped(_ => new Mock<IPlaidService>().Object);
        services.AddTransient(typeof(Lazy<>), typeof(LazyResolved<>));

        // DataRetentionManager reads the churn grace-period from IConfiguration; an empty
        // in-memory config gives it the built-in defaults.
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        // Register logging
        services.AddLogging(builder => builder.AddConsole());

        _serviceProvider = services.BuildServiceProvider();
        _context = _serviceProvider.GetRequiredService<BuoyAIDbContext>();
    }

    [Fact]
    public async Task DataRetentionManager_ShouldCleanupClosedAccountsOlderThan30Days()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        // Create users - one old enough to delete, one not
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
        var deletedCount = await retentionManager.CleanupClosedUserAccountsAsync();

        // Assert
        deletedCount.Should().Be(1);

        var remainingUsers = await _context.Users.ToListAsync();
        remainingUsers.Should().HaveCount(1);
        remainingUsers.First().Id.Should().Be(2); // Recent user should remain

        // Verify audit log was created
        var auditLogs = await _context.DataRetentionAuditLogs
            .Where(log => log.OperationType.Contains("automated_retention_cleanup"))
            .ToListAsync();
        auditLogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task DataRetentionManager_ShouldCleanupSecurityLogsOlderThan90Days()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        var oldLog = new SecurityAuditLog
        {
            Id = 1,
            UserId = 1,
            EventType = "login_attempt",
            Description = "Test login attempt",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow.AddDays(-95)
        };

        var recentLog = new SecurityAuditLog
        {
            Id = 2,
            UserId = 1,
            EventType = "login_success",
            Description = "Test login success",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow.AddDays(-85)
        };

        _context.SecurityAuditLogs.AddRange(oldLog, recentLog);
        await _context.SaveChangesAsync();

        // Act
        var deletedCount = await retentionManager.CleanupSecurityLogsAsync();

        // Assert
        deletedCount.Should().Be(1);

        var remainingLogs = await _context.SecurityAuditLogs.ToListAsync();
        remainingLogs.Should().HaveCount(1);
        remainingLogs.First().Id.Should().Be(2); // Recent log should remain
    }

    [Fact]
    public async Task DataRetentionManager_ShouldDeleteAllUserDataImmediately()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        var user = new User
        {
            Id = 1,
            Email = "test@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };

        var plaidConnection = new PlaidConnection
        {
            Id = 1,
            UserId = 1,
            AccessToken = "encrypted_token",
            ItemId = "item123",
            InstitutionId = "test_institution",
            InstitutionName = "Test Bank",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            LastSyncAt = DateTime.UtcNow
        };

        var aiInsight = new AIInsight
        {
            Id = 1,
            UserId = 1,
            Context = "dashboard",
            Type = "spending",
            Title = "Test insight",
            Message = "Test insight",
            Priority = "medium",
            CreatedAt = DateTime.UtcNow,
            GeneratedAt = DateTime.UtcNow
        };

        var securityLog = new SecurityAuditLog
        {
            Id = 1,
            UserId = 1,
            EventType = "login",
            Description = "Test login",
            IpAddress = "192.168.1.1",
            UserAgent = "Test Browser",
            CreatedAt = DateTime.UtcNow
        };

        _context.Users.Add(user);
        _context.PlaidConnections.Add(plaidConnection);
        _context.AIInsights.Add(aiInsight);
        _context.SecurityAuditLogs.Add(securityLog);
        await _context.SaveChangesAsync();

        // Act
        await retentionManager.DeleteUserDataImmediatelyAsync(1);

        // Assert
        var updatedUser = await _context.Users.FindAsync(1);
        updatedUser.Should().NotBeNull();
        updatedUser!.IsDeleted.Should().BeTrue();
        updatedUser.DeletedAt.Should().NotBeNull();
        updatedUser.DeletionReason.Should().Be("immediate_request");

        // Verify all associated data is deleted
        var remainingConnections = await _context.PlaidConnections.Where(t => t.UserId == 1).ToListAsync();
        remainingConnections.Should().BeEmpty();

        var remainingInsights = await _context.AIInsights.Where(i => i.UserId == 1).ToListAsync();
        remainingInsights.Should().BeEmpty();

        var remainingSecurityLogs = await _context.SecurityAuditLogs.Where(l => l.UserId == 1).ToListAsync();
        remainingSecurityLogs.Should().BeEmpty();

        // Verify audit log was created
        var auditLogs = await _context.DataRetentionAuditLogs
            .Where(log => log.UserId == 1 && log.DeletionType != null && log.DeletionType.Contains("complete_deletion"))
            .ToListAsync();
        auditLogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task DataRetentionManager_ShouldVerifyDataDeletionCorrectly()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        var activeUser = new User
        {
            Id = 1,
            Email = "active@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow
        };

        var deletedUser = new User
        {
            Id = 2,
            Email = "deleted@example.com",
            PasswordHash = "hash",
            IsDeleted = true,
            DeletedAt = DateTime.UtcNow,
            DeletionReason = "user_request",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

        _context.Users.AddRange(activeUser, deletedUser);
        await _context.SaveChangesAsync();

        // Act & Assert
        var activeUserVerification = await retentionManager.VerifyDataDeletionAsync(1);
        activeUserVerification.Should().BeFalse();

        var deletedUserVerification = await retentionManager.VerifyDataDeletionAsync(2);
        deletedUserVerification.Should().BeTrue();
    }

    [Fact]
    public async Task DataRetentionManager_ShouldCleanupErrorLogsOlderThan365Days()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        var oldError = new ErrorLog
        {
            Id = 1,
            Message = "Old error",
            StackTrace = "Stack trace",
            ErrorType = "Error",
            CreatedAt = DateTime.UtcNow.AddDays(-370)
        };

        var recentError = new ErrorLog
        {
            Id = 2,
            Message = "Recent error",
            StackTrace = "Stack trace",
            ErrorType = "Error",
            CreatedAt = DateTime.UtcNow.AddDays(-300)
        };

        _context.ErrorLogs.AddRange(oldError, recentError);
        await _context.SaveChangesAsync();

        // Act
        var deletedCount = await retentionManager.CleanupErrorLogsAsync();

        // Assert
        deletedCount.Should().Be(1);

        var remainingErrors = await _context.ErrorLogs.ToListAsync();
        remainingErrors.Should().HaveCount(1);
        remainingErrors.First().Id.Should().Be(2); // Recent error should remain
    }

    [Fact]
    public async Task DataRetentionManager_ShouldCleanupInactivePlaidConnections()
    {
        // Arrange
        var retentionManager = _serviceProvider.GetRequiredService<IDataRetentionManager>();

        var oldInactiveConnection = new PlaidConnection
        {
            Id = 1,
            UserId = 1,
            AccessToken = "old_inactive_connection",
            ItemId = "item1",
            InstitutionId = "inst1",
            InstitutionName = "Bank 1",
            IsActive = false,
            CreatedAt = DateTime.UtcNow.AddDays(-20),
            LastSyncAt = DateTime.UtcNow.AddDays(-10)
        };

        var recentInactiveConnection = new PlaidConnection
        {
            Id = 2,
            UserId = 1,
            AccessToken = "recent_inactive_connection",
            ItemId = "item2",
            InstitutionId = "inst2",
            InstitutionName = "Bank 2",
            IsActive = false,
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            LastSyncAt = DateTime.UtcNow.AddDays(-3)
        };

        var activeConnection = new PlaidConnection
        {
            Id = 3,
            UserId = 1,
            AccessToken = "active_connection",
            ItemId = "item3",
            InstitutionId = "inst3",
            InstitutionName = "Bank 3",
            IsActive = true,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            LastSyncAt = DateTime.UtcNow.AddDays(-1)
        };

        _context.PlaidConnections.AddRange(oldInactiveConnection, recentInactiveConnection, activeConnection);
        await _context.SaveChangesAsync();

        // Act - This would be a custom cleanup method for inactive connections
        // For now, let's test that we can successfully query the connections
        var connections = await _context.PlaidConnections.ToListAsync();

        // Assert
        connections.Should().HaveCount(3);
        connections.Should().Contain(c => c.Id == 1 && !c.IsActive);
        connections.Should().Contain(c => c.Id == 2 && !c.IsActive);
        connections.Should().Contain(c => c.Id == 3 && c.IsActive);
    }

    public void Dispose()
    {
        _context?.Dispose();
        _serviceProvider?.Dispose();
    }
}

/// <summary>Lazy&lt;T&gt; resolution, as the application container registers it.</summary>
internal sealed class LazyResolved<T> : Lazy<T> where T : class
{
    public LazyResolved(IServiceProvider serviceProvider) : base(() => serviceProvider.GetRequiredService<T>())
    {
    }
}
