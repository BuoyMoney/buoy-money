using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Services;

namespace BuoyAI.UnitTests.Services;

public class NotificationServiceTests
{
  private readonly Mock<INotificationRepository> _notificationRepository = new();
  private readonly Mock<IPushNotificationService> _pushNotificationService = new();
  private readonly Mock<ILogger<NotificationService>> _logger = new();

  private NotificationService CreateService()
  {
    return new NotificationService(
      _notificationRepository.Object,
      _pushNotificationService.Object,
      _logger.Object);
  }

  [Fact]
  public async Task GetUserNotificationsAsync_ShouldMapEntitiesToDtos()
  {
    var userId = 71;
    var createdAt = new DateTime(2025, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    var notifications = new List<Notification>
    {
      new()
      {
        Id = 11,
        UserId = userId,
        Type = "plaid_update_required",
        Title = "Action required",
        Message = "Update your connection",
        ActionUrl = "/update",
        ActionText = "Update",
        RelatedConnectionId = 42,
        RelatedConnection = new PlaidConnection { InstitutionName = "Sample Bank" },
        IsRead = false,
        ReadAt = null,
        CreatedAt = createdAt
      }
    };

    _notificationRepository
      .Setup(repo => repo.GetUserNotificationsAsync(userId, 2, 10))
      .ReturnsAsync(notifications);

    var service = CreateService();

    var result = await service.GetUserNotificationsAsync(userId, 2, 10);

    result.Should().HaveCount(1);
    var dto = result.Single();
    dto.Id.Should().Be(11);
    dto.Type.Should().Be("plaid_update_required");
    dto.ActionText.Should().Be("Update");
    dto.InstitutionName.Should().Be("Sample Bank");
    dto.CreatedAt.Should().Be(createdAt);
  }

  [Fact]
  public async Task MarkAsReadAsync_WhenNotificationBelongsToUser_ShouldUpdateState()
  {
    var userId = 9;
    var notificationId = 123;
    var notification = new Notification
    {
      Id = notificationId,
      UserId = userId,
      Type = "generic",
      Title = "Title",
      Message = "Message",
      IsRead = false
    };

    _notificationRepository
      .Setup(repo => repo.GetByIdAsync(notificationId))
      .ReturnsAsync(notification);

    _notificationRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification updated) => updated);

    var service = CreateService();

    var result = await service.MarkAsReadAsync(userId, notificationId);

    result.Should().BeTrue();
    notification.IsRead.Should().BeTrue();
    notification.ReadAt.Should().NotBeNull();
    _notificationRepository.Verify(repo => repo.UpdateAsync(It.Is<Notification>(n => n.Id == notificationId && n.IsRead)), Times.Once);
  }

  [Fact]
  public async Task MarkAsReadAsync_WhenNotificationDoesNotBelongToUser_ShouldReturnFalse()
  {
    var userId = 9;
    var notificationId = 555;

    _notificationRepository
      .Setup(repo => repo.GetByIdAsync(notificationId))
      .ReturnsAsync(new Notification { Id = notificationId, UserId = 999, IsRead = false });

    var service = CreateService();

    var result = await service.MarkAsReadAsync(userId, notificationId);

    result.Should().BeFalse();
    _notificationRepository.Verify(repo => repo.UpdateAsync(It.IsAny<Notification>()), Times.Never);
  }

  [Fact]
  public async Task ArchiveAsync_WhenNotificationBelongsToUser_ShouldSetArchivedAt()
  {
    var userId = 9;
    var notificationId = 321;
    var notification = new Notification
    {
      Id = notificationId,
      UserId = userId,
      Type = "generic",
      Title = "Title",
      Message = "Message",
      ArchivedAt = null
    };

    _notificationRepository
      .Setup(repo => repo.GetByIdAsync(notificationId))
      .ReturnsAsync(notification);
    _notificationRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification updated) => updated);

    var service = CreateService();

    var result = await service.ArchiveAsync(userId, notificationId);

    result.Should().BeTrue();
    notification.ArchivedAt.Should().NotBeNull();
    _notificationRepository.Verify(
      repo => repo.UpdateAsync(It.Is<Notification>(n => n.Id == notificationId && n.ArchivedAt != null)),
      Times.Once);
  }

  [Fact]
  public async Task ArchiveAsync_WhenNotificationDoesNotBelongToUser_ShouldReturnFalse()
  {
    var userId = 9;
    var notificationId = 777;

    _notificationRepository
      .Setup(repo => repo.GetByIdAsync(notificationId))
      .ReturnsAsync(new Notification { Id = notificationId, UserId = 999 });

    var service = CreateService();

    var result = await service.ArchiveAsync(userId, notificationId);

    result.Should().BeFalse();
    _notificationRepository.Verify(repo => repo.UpdateAsync(It.IsAny<Notification>()), Times.Never);
  }

  [Theory]
  [InlineData("ITEM_LOGIN_REQUIRED", "Your Contoso Bank login credentials have changed and need to be updated.")]
  [InlineData("PENDING_EXPIRATION", "Your Contoso Bank connection will expire soon and needs renewal.")]
  [InlineData("PENDING_DISCONNECT", "Your Contoso Bank connection will be disconnected due to security requirements.")]
  [InlineData("SOMETHING_ELSE", "Your Contoso Bank connection needs attention.")]
  public async Task CreatePlaidUpdateNotificationAsync_ShouldSurfaceErrorSpecificMessage(string errorType, string expectedMessage)
  {
    var userId = 44;
    var connectionId = 501;
    Notification? createdNotification = null;

    _notificationRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification notification) =>
      {
        createdNotification = notification;
        return notification;
      });

    var service = CreateService();

    await service.CreatePlaidUpdateNotificationAsync(userId, connectionId, "Contoso Bank", errorType);

    createdNotification.Should().NotBeNull();
    createdNotification!.Type.Should().Be("plaid_update_required");
    createdNotification.RelatedConnectionId.Should().Be(connectionId);
    createdNotification.Message.Should().Be(expectedMessage);
    _notificationRepository.Verify(repo => repo.CreateAsync(It.IsAny<Notification>()), Times.Once);
  }

  [Fact]
  public async Task UpdateConnectionNotificationToResolvedAsync_WhenExistingNotification_ShouldUpdateInPlace()
  {
    var userId = 21;
    var connectionId = 88;
    var notification = new Notification
    {
      Id = 1,
      UserId = userId,
      Type = "plaid_update_required",
      Title = "Needs update",
      Message = "Fix it",
      ActionUrl = "/update",
      ActionText = "Update",
      IsRead = true,
      UpdatedAt = DateTime.UtcNow.AddDays(-2)
    };

    _notificationRepository
      .Setup(repo => repo.GetUserConnectionNotificationsAsync(userId, connectionId, It.IsAny<string[]>()))
      .ReturnsAsync(new List<Notification> { notification });

    _notificationRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification updated) => updated);

    var service = CreateService();

    await service.UpdateConnectionNotificationToResolvedAsync(userId, connectionId, "Contoso Bank");

    notification.Type.Should().Be("plaid_reconnected");
    notification.Title.Should().Be("✅ Connection Restored - Contoso Bank");
    notification.Message.Should().Be("Your Contoso Bank connection has been successfully updated and is working normally.");
    notification.ActionUrl.Should().BeNull();
    notification.ActionText.Should().BeNull();
    notification.IsRead.Should().BeFalse();
    notification.UpdatedAt.Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
    _notificationRepository.Verify(repo => repo.UpdateAsync(notification), Times.Once);
  }

  [Fact]
  public async Task UpdateConnectionNotificationToResolvedAsync_WhenNoneExist_ShouldCreateNew()
  {
    var userId = 22;
    var connectionId = 77;
    Notification? created = null;

    _notificationRepository
      .Setup(repo => repo.GetUserConnectionNotificationsAsync(userId, connectionId, It.IsAny<string[]>()))
      .ReturnsAsync(new List<Notification>());

    _notificationRepository
      .Setup(repo => repo.CreateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification notification) =>
      {
        created = notification;
        return notification;
      });

    var service = CreateService();

    await service.UpdateConnectionNotificationToResolvedAsync(userId, connectionId, "Contoso Bank");

    created.Should().NotBeNull();
    created!.Type.Should().Be("plaid_reconnected");
    created.Title.Should().Be("Connection Restored - Contoso Bank");
    created.Message.Should().Contain("successfully updated");
    created.RelatedConnectionId.Should().Be(connectionId);
  }

  [Fact]
  public async Task ArchiveOldNotificationsAsync_ShouldArchiveOnlyEligibleEntries()
  {
    var now = DateTime.UtcNow;
    var oldNotification = new Notification { Id = 1, CreatedAt = now.AddMonths(-2), ArchivedAt = null };
    var alreadyArchived = new Notification { Id = 2, CreatedAt = now.AddMonths(-3), ArchivedAt = now.AddDays(-5) };
    var recentNotification = new Notification { Id = 3, CreatedAt = now.AddDays(-10), ArchivedAt = null };
    var updates = new List<Notification>();

    _notificationRepository
      .Setup(repo => repo.GetAllAsync())
      .ReturnsAsync(new List<Notification> { oldNotification, alreadyArchived, recentNotification });

    _notificationRepository
      .Setup(repo => repo.UpdateAsync(It.IsAny<Notification>()))
      .ReturnsAsync((Notification notification) =>
      {
        updates.Add(notification);
        return notification;
      });

    var service = CreateService();

    await service.ArchiveOldNotificationsAsync();

    updates.Should().ContainSingle(n => n.Id == 1 && n.ArchivedAt.HasValue);
    updates.Should().NotContain(n => n.Id == 2);
    updates.Should().NotContain(n => n.Id == 3);
  }

  [Fact]
  public async Task GetUserNotificationsPaginatedAsync_ShouldRespectPagingAndArchivalFilter()
  {
    var userId = 35;
    var now = DateTime.UtcNow;
    var notifications = new List<Notification>
    {
      new() { Id = 1, UserId = userId, Title = "Newest", Message = "m1", CreatedAt = now, ArchivedAt = null },
      new() { Id = 2, UserId = userId, Title = "Older", Message = "m2", CreatedAt = now.AddMinutes(-10), ArchivedAt = null },
      new() { Id = 3, UserId = userId, Title = "Archived", Message = "m3", CreatedAt = now.AddMinutes(-20), ArchivedAt = now.AddDays(-1) }
    };

    _notificationRepository
      .Setup(repo => repo.GetUserNotificationsAsync(userId, 1, 2))
      .ReturnsAsync(notifications);

    var service = CreateService();

    var (results, hasMore) = await service.GetUserNotificationsPaginatedAsync(userId, page: 1, pageSize: 1, includeArchived: false);

    hasMore.Should().BeTrue();
    results.Should().HaveCount(1);
    results.Single().Title.Should().Be("Newest");
  }
}
