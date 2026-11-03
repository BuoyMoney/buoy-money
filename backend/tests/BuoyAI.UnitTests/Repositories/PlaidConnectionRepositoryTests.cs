using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class PlaidConnectionRepositoryTests
{
    private static BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private static User CreateTestUser(int id, string username)
    {
        return new User
        {
            Id = id,
            Username = username,
            Email = $"{username}@example.com",
            PasswordHash = "hash",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private static PlaidConnection CreateTestConnection(int id, int userId, string itemId, bool isActive = true)
    {
        return new PlaidConnection
        {
            Id = id,
            UserId = userId,
            ItemId = itemId,
            AccessToken = "encrypted_token",
            InstitutionId = "inst_123",
            InstitutionName = "Test Bank",
            IsActive = isActive,
            LastSyncAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow.AddDays(-10),
            UpdatedAt = DateTime.UtcNow.AddDays(-10),
            ImportStatus = ImportStatus.Completed,
            Status = PlaidItemStatus.Active
        };
    }

    #region GetByUserIdAsync Tests

    [Fact]
    public async Task GetByUserIdAsync_ReturnsActiveConnections()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var activeConnection = CreateTestConnection(1, 1, "item_active", isActive: true);
        var inactiveConnection = CreateTestConnection(2, 1, "item_inactive", isActive: false);

        context.PlaidConnections.AddRange(activeConnection, inactiveConnection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var connections = result.ToList();
        connections.Should().HaveCount(1);
        connections[0].ItemId.Should().Be("item_active");
        connections[0].IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetByUserIdAsync_ReturnsEmpty_WhenNoConnections()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(999);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByUserIdAsync_FiltersInactiveConnections()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection1 = CreateTestConnection(1, 1, "item_1", isActive: true);
        var connection2 = CreateTestConnection(2, 1, "item_2", isActive: false);
        var connection3 = CreateTestConnection(3, 1, "item_3", isActive: true);

        context.PlaidConnections.AddRange(connection1, connection2, connection3);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByUserIdAsync(1);

        // Assert
        var connections = result.ToList();
        connections.Should().HaveCount(2);
        connections.Should().OnlyContain(c => c.IsActive);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsConnection_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_123");
        connection.InstitutionName = "Chase Bank";
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.ItemId.Should().Be("item_123");
        result.InstitutionName.Should().Be("Chase Bank");
    }

    [Fact]
    public async Task GetByIdAsync_IncludesUser()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_123");
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(1);

        // Assert
        result.Should().NotBeNull();
        result!.User.Should().NotBeNull();
        result.User.Username.Should().Be("testuser");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetByItemIdAsync Tests

    [Fact]
    public async Task GetByItemIdAsync_ReturnsConnection_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "unique_item_id");
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByItemIdAsync("unique_item_id");

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
        result.ItemId.Should().Be("unique_item_id");
    }

    [Fact]
    public async Task GetByItemIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetByItemIdAsync("nonexistent");

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsConnection_AndSetsTimestamps()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = new PlaidConnection
        {
            UserId = 1,
            ItemId = "new_item_id",
            AccessToken = "new_encrypted_token",
            InstitutionId = "inst_new",
            InstitutionName = "New Bank",
            IsActive = true,
            ImportStatus = ImportStatus.Pending
        };

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.CreateAsync(connection);

        // Assert
        result.Id.Should().BeGreaterThan(0);
        result.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        result.LastSyncAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));

        var saved = await context.PlaidConnections.FindAsync(result.Id);
        saved.Should().NotBeNull();
        saved!.InstitutionName.Should().Be("New Bank");
    }

    [Fact]
    public async Task CreateAsync_SetsDefaultValues()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var connection = new PlaidConnection
        {
            UserId = 1,
            ItemId = "default_item",
            AccessToken = "token",
            InstitutionId = "inst_default",
            InstitutionName = "Default Bank"
        };

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.CreateAsync(connection);

        // Assert
        result.IsActive.Should().BeTrue();
        result.ImportStatus.Should().Be(ImportStatus.Pending);
        result.Status.Should().Be(PlaidItemStatus.Active);
        result.ImportProgress.Should().Be(0);
        result.ConsecutiveFailures.Should().Be(0);
        result.RequiresUserAction.Should().BeFalse();
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesConnection_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_update");
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        context.Entry(connection).State = EntityState.Detached;

        var repository = new PlaidConnectionRepository(context);

        // Act
        await Task.Delay(100);
        connection.InstitutionName = "Updated Bank";
        connection.LastSyncAt = DateTime.UtcNow;
        connection.ImportStatus = ImportStatus.Completed;
        var result = await repository.UpdateAsync(connection);

        // Assert
        result.InstitutionName.Should().Be("Updated Bank");
        result.ImportStatus.Should().Be(ImportStatus.Completed);
        result.UpdatedAt.Should().BeAfter(result.CreatedAt);

        var updated = await context.PlaidConnections.FindAsync(1);
        updated!.InstitutionName.Should().Be("Updated Bank");
    }

    [Fact]
    public async Task UpdateAsync_UpdatesImportStatus()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_import");
        connection.ImportStatus = ImportStatus.Pending;
        connection.ImportProgress = 0;
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        context.Entry(connection).State = EntityState.Detached;

        var repository = new PlaidConnectionRepository(context);

        // Act
        connection.ImportStatus = ImportStatus.Completed;
        connection.ImportProgress = 100;
        connection.ImportCompletedAt = DateTime.UtcNow;
        await repository.UpdateAsync(connection);

        // Assert
        var updated = await context.PlaidConnections.FindAsync(1);
        updated!.ImportStatus.Should().Be(ImportStatus.Completed);
        updated.ImportProgress.Should().Be(100);
        updated.ImportCompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateAsync_UpdatesStatusFields()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_status");
        connection.Status = PlaidItemStatus.Active;
        connection.ConsecutiveFailures = 0;
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        context.Entry(connection).State = EntityState.Detached;

        var repository = new PlaidConnectionRepository(context);

        // Act
        connection.Status = PlaidItemStatus.LoginRequired;
        connection.RequiresUserAction = true;
        connection.ConsecutiveFailures = 3;
        connection.LastError = "Invalid credentials";
        await repository.UpdateAsync(connection);

        // Assert
        var updated = await context.PlaidConnections.FindAsync(1);
        updated!.Status.Should().Be(PlaidItemStatus.LoginRequired);
        updated.RequiresUserAction.Should().BeTrue();
        updated.ConsecutiveFailures.Should().Be(3);
        updated.LastError.Should().Be("Invalid credentials");
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_SoftDeletesConnection_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_delete");
        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        await Task.Delay(100);
        await repository.DeleteAsync(1);

        // Assert
        var deleted = await context.PlaidConnections.FindAsync(1);
        deleted.Should().NotBeNull();
        deleted!.IsActive.Should().BeFalse();
        deleted.UpdatedAt.Should().BeAfter(deleted.CreatedAt);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenConnectionNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new PlaidConnectionRepository(context);

        // Act & Assert
        await repository.Invoking(r => r.DeleteAsync(999))
            .Should().NotThrowAsync();
    }

    #endregion

    #region GetConnectionsNeedingBackgroundImport Tests

    [Fact]
    public async Task GetConnectionsNeedingBackgroundImport_ReturnsCorrectConnections()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        // Should be returned: Quick sync done, full import pending
        var connection1 = CreateTestConnection(1, 1, "item_1");
        connection1.QuickSyncCompletedAt = DateTime.UtcNow.AddHours(-1);
        connection1.ImportStatus = ImportStatus.Pending;

        // Should NOT be returned: Quick sync not done yet
        var connection2 = CreateTestConnection(2, 1, "item_2");
        connection2.QuickSyncCompletedAt = null;
        connection2.ImportStatus = ImportStatus.Pending;

        // Should also be returned: Pending import (quick sync not required anymore)
        var connection3 = CreateTestConnection(3, 1, "item_3");
        connection3.QuickSyncCompletedAt = DateTime.UtcNow.AddHours(-2);
        connection3.ImportStatus = ImportStatus.Completed;

        // Should NOT be returned: Inactive
        var connection4 = CreateTestConnection(4, 1, "item_4", isActive: false);
        connection4.QuickSyncCompletedAt = DateTime.UtcNow.AddHours(-1);
        connection4.ImportStatus = ImportStatus.Pending;

        context.PlaidConnections.AddRange(connection1, connection2, connection3, connection4);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetConnectionsNeedingBackgroundImport();

        // Assert - should return connection1 and connection2 (both active and pending)
        var connections = result.ToList();
        connections.Should().HaveCount(2);
        connections.Should().OnlyContain(c => c.IsActive && c.ImportStatus == ImportStatus.Pending);
    }

    [Fact]
    public async Task GetConnectionsNeedingBackgroundImport_LeavesOtherProvidersConnectionsAlone()
    {
        // The historical import is Plaid's. A SimpleFIN connection mid-import must not be handed to it.
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser"));
        var plaid = CreateTestConnection(1, 1, "item_plaid");
        plaid.ImportStatus = ImportStatus.Pending;
        var simpleFin = CreateTestConnection(2, 1, "sfin:abc");
        simpleFin.Provider = BankProvider.SimpleFin;
        simpleFin.ImportStatus = ImportStatus.Pending;
        context.PlaidConnections.AddRange(plaid, simpleFin);
        await context.SaveChangesAsync();

        var result = await new PlaidConnectionRepository(context).GetConnectionsNeedingBackgroundImport();

        result.Select(c => c.Id).Should().Equal(1);
    }

    [Fact]
    public async Task GetConnectionsNeedingBackgroundImport_RequeuesFailedAndStaleInProgress_Bounded()
    {
        // Arrange
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser"));

        // Failed under the attempt cap -> retryable
        var failedRetryable = CreateTestConnection(1, 1, "item_failed_retry");
        failedRetryable.ImportStatus = ImportStatus.Failed;
        failedRetryable.ImportAttempts = 1;

        // Failed but exhausted attempts -> NOT retried (bounded)
        var failedExhausted = CreateTestConnection(2, 1, "item_failed_exhausted");
        failedExhausted.ImportStatus = ImportStatus.Failed;
        failedExhausted.ImportAttempts = 5;

        // InProgress but stale (worker died mid-run) -> requeued
        var staleInProgress = CreateTestConnection(3, 1, "item_stale");
        staleInProgress.ImportStatus = ImportStatus.InProgress;
        staleInProgress.ImportStartedAt = DateTime.UtcNow.AddHours(-2);
        staleInProgress.ImportAttempts = 1;

        // InProgress and fresh (a worker is legitimately still running) -> NOT requeued
        var freshInProgress = CreateTestConnection(4, 1, "item_fresh");
        freshInProgress.ImportStatus = ImportStatus.InProgress;
        freshInProgress.ImportStartedAt = DateTime.UtcNow.AddMinutes(-1);
        freshInProgress.ImportAttempts = 1;

        context.PlaidConnections.AddRange(failedRetryable, failedExhausted, staleInProgress, freshInProgress);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = (await repository.GetConnectionsNeedingBackgroundImport()).ToList();

        // Assert
        result.Select(c => c.ItemId).Should().BeEquivalentTo(new[] { "item_failed_retry", "item_stale" });
    }

    [Fact]
    public async Task GetActiveConnectionsForSync_IncludesInProgress_SoOngoingSyncStillRuns()
    {
        // Arrange
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser"));

        var inProgress = CreateTestConnection(1, 1, "item_inprogress");
        inProgress.ImportStatus = ImportStatus.InProgress; // historical import still/again running
        inProgress.Status = PlaidItemStatus.Active;

        context.PlaidConnections.Add(inProgress);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = (await repository.GetActiveConnectionsForSync()).ToList();

        // Assert - InProgress must not stop the 10-minute ongoing transaction sync.
        result.Should().ContainSingle(c => c.ItemId == "item_inprogress");
    }

    [Fact]
    public async Task GetConnectionsNeedingBackgroundImport_ReturnsEmpty_WhenNoneQualify()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        var connection = CreateTestConnection(1, 1, "item_completed");
        connection.QuickSyncCompletedAt = DateTime.UtcNow;
        connection.ImportStatus = ImportStatus.Completed;

        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetConnectionsNeedingBackgroundImport();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region GetActiveConnectionsForSync Tests

    [Fact]
    public async Task GetActiveConnectionsForSync_ReturnsCompletedActiveConnections()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        // Should be returned: Active and fully imported
        var connection1 = CreateTestConnection(1, 1, "item_1");
        connection1.ImportStatus = ImportStatus.Completed;

        var connection2 = CreateTestConnection(2, 1, "item_2");
        connection2.ImportStatus = ImportStatus.Completed;

        // Should also be returned: Active and pending (included for retry support)
        var connection3 = CreateTestConnection(3, 1, "item_3");
        connection3.ImportStatus = ImportStatus.Pending;

        // Should NOT be returned: Inactive
        var connection4 = CreateTestConnection(4, 1, "item_4", isActive: false);
        connection4.ImportStatus = ImportStatus.Completed;

        context.PlaidConnections.AddRange(connection1, connection2, connection3, connection4);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetActiveConnectionsForSync();

        // Assert - should return connection1, connection2, and connection3 (all active, completed or pending)
        var connections = result.ToList();
        connections.Should().HaveCount(3);
        connections.Should().OnlyContain(c => c.IsActive && (c.ImportStatus == ImportStatus.Completed || c.ImportStatus == ImportStatus.Pending));
    }

    [Fact]
    public async Task GetActiveConnectionsForSync_IncludesTerminalFailedBackfill_WhenItemHealthy()
    {
        // Arrange - a historical backfill that exhausted all retry attempts (terminal Failed)
        // but whose Plaid item is otherwise Active/healthy. A failed one-time BACKFILL must not
        // stop go-forward transaction sync, otherwise the account silently stops receiving new
        // transactions forever (the orphaned-connection symptom this fix addresses).
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser"));

        var failedBackfill = CreateTestConnection(1, 1, "item_failed_backfill");
        failedBackfill.ImportStatus = ImportStatus.Failed;
        failedBackfill.ImportAttempts = 5; // cap exhausted -> terminal, no longer requeued for import
        failedBackfill.Status = PlaidItemStatus.Active; // item itself is healthy

        context.PlaidConnections.Add(failedBackfill);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = (await repository.GetActiveConnectionsForSync()).ToList();

        // Assert
        result.Should().ContainSingle(c => c.ItemId == "item_failed_backfill");
    }

    [Fact]
    public async Task GetActiveConnectionsForSync_ExcludesFailedImport_WhenItemBroken()
    {
        // Arrange - guard against the fix re-introducing sync of genuinely broken items:
        // a Failed import whose item status is broken (LoginRequired / Error) must still be
        // excluded so the job doesn't hammer Plaid with a token that no longer authenticates.
        using var context = GetInMemoryContext();
        context.Users.Add(CreateTestUser(1, "testuser"));

        var loginRequired = CreateTestConnection(1, 1, "item_login_required");
        loginRequired.ImportStatus = ImportStatus.Failed;
        loginRequired.Status = PlaidItemStatus.LoginRequired;

        var errored = CreateTestConnection(2, 1, "item_error");
        errored.ImportStatus = ImportStatus.Failed;
        errored.Status = PlaidItemStatus.Error;

        context.PlaidConnections.AddRange(loginRequired, errored);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = (await repository.GetActiveConnectionsForSync()).ToList();

        // Assert - broken items are excluded even though their ImportStatus is Failed.
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveConnectionsForSync_ReturnsEmpty_WhenNoneQualify()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var user = CreateTestUser(1, "testuser");
        context.Users.Add(user);

        // Inactive connection - should not be returned
        var connection = CreateTestConnection(1, 1, "item_pending", isActive: false);
        connection.ImportStatus = ImportStatus.Pending;

        context.PlaidConnections.Add(connection);
        await context.SaveChangesAsync();

        var repository = new PlaidConnectionRepository(context);

        // Act
        var result = await repository.GetActiveConnectionsForSync();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion
}
