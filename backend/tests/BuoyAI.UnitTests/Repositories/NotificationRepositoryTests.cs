using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Repositories;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Repositories;

public class NotificationRepositoryTests
{
    private BuoyAIDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<BuoyAIDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        return new BuoyAIDbContext(options);
    }

    private User CreateTestUser(int id)
    {
        return new User
        {
            Id = id,
            Username = $"user{id}",
            Email = $"user{id}@test.com",
            PasswordHash = "hashedpassword"
        };
    }

    #region GetUserConnectionNotificationsAsync Tests (Existing)

    [Fact]
    public async Task GetUserConnectionNotificationsAsync_ReturnsFilteredNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);
        
        var user1Id = 1;
        var user2Id = 2;
        var connectionId = 123;
        var notificationTypes = new[] { "plaid_update_required", "plaid_expiration_warning" };

        // Add test notifications
        var notifications = new[]
        {
            new Notification
            {
                UserId = user1Id,
                RelatedConnectionId = connectionId,
                Type = "plaid_update_required",
                Title = "Update Required",
                Message = "Test message",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddHours(-2)
            },
            new Notification
            {
                UserId = user1Id,
                RelatedConnectionId = connectionId,
                Type = "plaid_expiration_warning",
                Title = "Expiration Warning",
                Message = "Test message",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            },
            new Notification
            {
                UserId = user1Id,
                RelatedConnectionId = connectionId,
                Type = "plaid_update_required",
                Title = "Update Required (Read)",
                Message = "Test message",
                IsRead = true, // This should be filtered out
                CreatedAt = DateTime.UtcNow.AddHours(-3)
            },
            new Notification
            {
                UserId = user1Id,
                RelatedConnectionId = 456, // Different connection
                Type = "plaid_update_required",
                Title = "Different Connection",
                Message = "Test message",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            },
            new Notification
            {
                UserId = user2Id, // Different user
                RelatedConnectionId = connectionId,
                Type = "plaid_update_required",
                Title = "Different User",
                Message = "Test message",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            },
            new Notification
            {
                UserId = user1Id,
                RelatedConnectionId = connectionId,
                Type = "other_notification", // Different type
                Title = "Other Type",
                Message = "Test message",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddMinutes(-30)
            }
        };

        await context.Notifications.AddRangeAsync(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetUserConnectionNotificationsAsync(user1Id, connectionId, notificationTypes);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, n => Assert.Equal(user1Id, n.UserId));
        Assert.All(result, n => Assert.Equal(connectionId, n.RelatedConnectionId));
        Assert.All(result, n => Assert.False(n.IsRead));
        Assert.All(result, n => Assert.Contains(n.Type, notificationTypes));
        
        // Verify ordering (most recent first)
        Assert.Equal("plaid_expiration_warning", result[0].Type);
        Assert.Equal("plaid_update_required", result[1].Type);
    }

    [Fact]
    public async Task GetUserConnectionNotificationsAsync_ReturnsEmptyList_WhenNoMatchingNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);
        
        var notificationTypes = new[] { "plaid_update_required" };

        // Act
        var result = await repository.GetUserConnectionNotificationsAsync(999, 123, notificationTypes);

        // Assert
        Assert.Empty(result);
    }

    #endregion

    #region GetUserNotificationsAsync Tests

    [Fact]
    public async Task GetUserNotificationsAsync_ReturnsPaginatedNotifications_OrderedByCreatedAtDescending()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notifications = new[]
        {
            new Notification
            {
                UserId = user.Id,
                Type = "plaid_update_required",
                Title = "Notification 1",
                Message = "Message 1",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddDays(-5)
            },
            new Notification
            {
                UserId = user.Id,
                Type = "plaid_expiration_warning",
                Title = "Notification 2",
                Message = "Message 2",
                IsRead = false,
                CreatedAt = DateTime.UtcNow.AddDays(-3)
            },
            new Notification
            {
                UserId = user.Id,
                Type = "subscription_detected",
                Title = "Notification 3",
                Message = "Message 3",
                IsRead = true,
                CreatedAt = DateTime.UtcNow.AddDays(-1)
            }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act - Get first page with 2 items
        var result = await repository.GetUserNotificationsAsync(user.Id, page: 1, pageSize: 2);

        // Assert
        result.Should().HaveCount(2);
        result[0].Title.Should().Be("Notification 3"); // Most recent
        result[1].Title.Should().Be("Notification 2");
        result.Should().OnlyContain(n => n.UserId == user.Id);
    }

    [Fact]
    public async Task GetUserNotificationsAsync_ReturnsSecondPage_WhenPageIsTwo()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notifications = new[]
        {
            new Notification { UserId = user.Id, Type = "type1", Title = "N1", Message = "M1", CreatedAt = DateTime.UtcNow.AddDays(-5) },
            new Notification { UserId = user.Id, Type = "type2", Title = "N2", Message = "M2", CreatedAt = DateTime.UtcNow.AddDays(-4) },
            new Notification { UserId = user.Id, Type = "type3", Title = "N3", Message = "M3", CreatedAt = DateTime.UtcNow.AddDays(-3) },
            new Notification { UserId = user.Id, Type = "type4", Title = "N4", Message = "M4", CreatedAt = DateTime.UtcNow.AddDays(-2) },
            new Notification { UserId = user.Id, Type = "type5", Title = "N5", Message = "M5", CreatedAt = DateTime.UtcNow.AddDays(-1) }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act - Get second page with 2 items per page
        var result = await repository.GetUserNotificationsAsync(user.Id, page: 2, pageSize: 2);

        // Assert
        result.Should().HaveCount(2);
        result[0].Title.Should().Be("N3");
        result[1].Title.Should().Be("N2");
    }

    [Fact]
    public async Task GetUserNotificationsAsync_ReturnsEmptyList_WhenNoNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        // Act
        var result = await repository.GetUserNotificationsAsync(999, page: 1, pageSize: 10);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetUserNotificationsAsync_FiltersCorrectlyByUser()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var notifications = new[]
        {
            new Notification { UserId = user1.Id, Type = "type1", Title = "User1 N1", Message = "M1", CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user2.Id, Type = "type2", Title = "User2 N1", Message = "M2", CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetUserNotificationsAsync(user1.Id, page: 1, pageSize: 10);

        // Assert
        result.Should().HaveCount(1);
        result[0].UserId.Should().Be(user1.Id);
        result[0].Title.Should().Be("User1 N1");
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_ReturnsNotification_WhenExists()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notification = new Notification
        {
            UserId = user.Id,
            Type = "plaid_update_required",
            Title = "Test Notification",
            Message = "Test message content",
            IsRead = false,
            ActionUrl = "/connections/123",
            ActionText = "Update Connection",
            CreatedAt = DateTime.UtcNow
        };
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetByIdAsync(notification.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(notification.Id);
        result.Type.Should().Be("plaid_update_required");
        result.Title.Should().Be("Test Notification");
        result.Message.Should().Be("Test message content");
        result.IsRead.Should().BeFalse();
        result.ActionUrl.Should().Be("/connections/123");
        result.ActionText.Should().Be("Update Connection");
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        // Act
        var result = await repository.GetByIdAsync(999);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region CreateAsync Tests

    [Fact]
    public async Task CreateAsync_AddsNotification_AndReturnsEntity()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var notification = new Notification
        {
            UserId = user.Id,
            Type = "subscription_detected",
            Title = "New Subscription",
            Message = "Found a new subscription",
            IsRead = false,
            ActionUrl = "/subscriptions",
            ActionText = "View Subscriptions",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        var result = await repository.CreateAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().BeGreaterThan(0);
        result.Type.Should().Be("subscription_detected");
        result.Title.Should().Be("New Subscription");

        var savedNotification = await context.Notifications.FindAsync(result.Id);
        savedNotification.Should().NotBeNull();
        savedNotification!.Message.Should().Be("Found a new subscription");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesNotification_AndSetsUpdatedAt()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var originalUpdatedAt = DateTime.UtcNow.AddDays(-5);
        var notification = new Notification
        {
            UserId = user.Id,
            Type = "plaid_update_required",
            Title = "Original Title",
            Message = "Original Message",
            IsRead = false,
            CreatedAt = DateTime.UtcNow.AddDays(-5),
            UpdatedAt = originalUpdatedAt
        };
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        // Detach to simulate fresh retrieval
        context.Entry(notification).State = EntityState.Detached;

        // Act
        notification.IsRead = true;
        notification.ReadAt = DateTime.UtcNow;
        notification.Title = "Updated Title";
        var result = await repository.UpdateAsync(notification);

        // Assert
        result.Should().NotBeNull();
        result.IsRead.Should().BeTrue();
        result.ReadAt.Should().NotBeNull();
        result.Title.Should().Be("Updated Title");
        result.UpdatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        result.UpdatedAt.Should().NotBe(originalUpdatedAt);
    }

    #endregion

    #region GetUnreadCountAsync Tests

    [Fact]
    public async Task GetUnreadCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notifications = new[]
        {
            new Notification { UserId = user.Id, Type = "type1", Title = "N1", Message = "M1", IsRead = false, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type2", Title = "N2", Message = "M2", IsRead = false, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type3", Title = "N3", Message = "M3", IsRead = true, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type4", Title = "N4", Message = "M4", IsRead = false, CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetUnreadCountAsync(user.Id);

        // Assert
        result.Should().Be(3);
    }

    [Fact]
    public async Task GetUnreadCountAsync_ReturnsZero_WhenAllRead()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notifications = new[]
        {
            new Notification { UserId = user.Id, Type = "type1", Title = "N1", Message = "M1", IsRead = true, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type2", Title = "N2", Message = "M2", IsRead = true, CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetUnreadCountAsync(user.Id);

        // Assert
        result.Should().Be(0);
    }

    [Fact]
    public async Task GetUnreadCountAsync_ReturnsZero_WhenNoNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        // Act
        var result = await repository.GetUnreadCountAsync(999);

        // Assert
        result.Should().Be(0);
    }

    #endregion

    #region MarkAllAsReadAsync Tests

    [Fact]
    public async Task MarkAllAsReadAsync_MarksAllUnreadNotificationsAsRead()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notifications = new[]
        {
            new Notification { UserId = user.Id, Type = "type1", Title = "N1", Message = "M1", IsRead = false, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type2", Title = "N2", Message = "M2", IsRead = false, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user.Id, Type = "type3", Title = "N3", Message = "M3", IsRead = true, ReadAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.MarkAllAsReadAsync(user.Id);

        // Assert
        result.Should().BeTrue();

        var allNotifications = await context.Notifications.Where(n => n.UserId == user.Id).ToListAsync();
        allNotifications.Should().OnlyContain(n => n.IsRead);
        allNotifications.Where(n => n.Title == "N1" || n.Title == "N2").Should().OnlyContain(n => n.ReadAt.HasValue);
    }

    [Fact]
    public async Task MarkAllAsReadAsync_ReturnsTrue_WhenNoUnreadNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notification = new Notification
        {
            UserId = user.Id,
            Type = "type1",
            Title = "N1",
            Message = "M1",
            IsRead = true,
            ReadAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow
        };
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.MarkAllAsReadAsync(user.Id);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task MarkAllAsReadAsync_OnlyMarksSpecificUserNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var notifications = new[]
        {
            new Notification { UserId = user1.Id, Type = "type1", Title = "User1 N1", Message = "M1", IsRead = false, CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user2.Id, Type = "type2", Title = "User2 N1", Message = "M2", IsRead = false, CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.MarkAllAsReadAsync(user1.Id);

        // Assert
        result.Should().BeTrue();

        var user1Notifications = await context.Notifications.Where(n => n.UserId == user1.Id).ToListAsync();
        user1Notifications.Should().OnlyContain(n => n.IsRead);

        var user2Notifications = await context.Notifications.Where(n => n.UserId == user2.Id).ToListAsync();
        user2Notifications.Should().OnlyContain(n => !n.IsRead);
    }

    #endregion

    #region GetAllAsync Tests

    [Fact]
    public async Task GetAllAsync_ReturnsAllNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user1 = CreateTestUser(1);
        var user2 = CreateTestUser(2);
        context.Users.AddRange(user1, user2);

        var notifications = new[]
        {
            new Notification { UserId = user1.Id, Type = "type1", Title = "N1", Message = "M1", CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user2.Id, Type = "type2", Title = "N2", Message = "M2", CreatedAt = DateTime.UtcNow },
            new Notification { UserId = user1.Id, Type = "type3", Title = "N3", Message = "M3", CreatedAt = DateTime.UtcNow }
        };
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.GetAllAsync();

        // Assert
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsEmptyList_WhenNoNotifications()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        // Act
        var result = await repository.GetAllAsync();

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesNotification_AndReturnsTrue()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        var user = CreateTestUser(1);
        context.Users.Add(user);

        var notification = new Notification
        {
            UserId = user.Id,
            Type = "test_type",
            Title = "Test",
            Message = "Test message",
            CreatedAt = DateTime.UtcNow
        };
        context.Notifications.Add(notification);
        await context.SaveChangesAsync();

        // Act
        var result = await repository.DeleteAsync(notification.Id);

        // Assert
        result.Should().BeTrue();

        var deletedNotification = await context.Notifications.FindAsync(notification.Id);
        deletedNotification.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_ReturnsFalse_WhenNotificationNotFound()
    {
        // Arrange
        using var context = GetInMemoryContext();
        var repository = new NotificationRepository(context);

        // Act
        var result = await repository.DeleteAsync(999);

        // Assert
        result.Should().BeFalse();
    }

    #endregion
}
