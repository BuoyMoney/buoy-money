using BuoyAI.Core.Utilities;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.Infrastructure.Services;

public class NotificationService : INotificationService
{
    private readonly INotificationRepository _notificationRepository;
    private readonly IPushNotificationService _pushNotificationService;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        INotificationRepository notificationRepository,
        IPushNotificationService pushNotificationService,
        ILogger<NotificationService> logger)
    {
        _notificationRepository = notificationRepository;
        _pushNotificationService = pushNotificationService;
        _logger = logger;
    }

    public async Task<List<NotificationDto>> GetUserNotificationsAsync(int userId, int page = 1, int pageSize = 20)
    {
        var notifications = await _notificationRepository.GetUserNotificationsAsync(userId, page, pageSize);
        
        return notifications.Select(n => new NotificationDto
        {
            Id = n.Id,
            Type = n.Type,
            Title = n.Title,
            Message = n.Message,
            ActionUrl = n.ActionUrl,
            ActionText = n.ActionText,
            RelatedConnectionId = n.RelatedConnectionId,
            InstitutionName = n.RelatedConnection?.InstitutionName,
            IsRead = n.IsRead,
            ReadAt = n.ReadAt,
            CreatedAt = n.CreatedAt
        }).ToList();
    }

    public async Task<NotificationSummaryDto> GetNotificationSummaryAsync(int userId)
    {
        var recent = await GetUserNotificationsAsync(userId, 1, 5);
        var unreadCount = await _notificationRepository.GetUnreadCountAsync(userId);
        
        return new NotificationSummaryDto
        {
            TotalCount = recent.Count,
            UnreadCount = unreadCount,
            Recent = recent
        };
    }

    public async Task<NotificationDto> CreateNotificationAsync(int userId, CreateNotificationRequest request)
    {
        var notification = new Notification
        {
            UserId = userId,
            Type = request.Type,
            Title = request.Title,
            Message = request.Message,
            ActionUrl = request.ActionUrl,
            ActionText = request.ActionText,
            RelatedConnectionId = request.RelatedConnectionId
        };

        await _notificationRepository.CreateAsync(notification);

        _logger.LogInformation("Created notification for user {UserId}: {Title}", userId, request.Title);

        // Every in-app notification also gets a push copy — preference gating, quiet hours,
        // and error swallowing all live inside SendToUserAsync (it never throws).
        await _pushNotificationService.SendToUserAsync(
            userId, request.Type, request.Title, request.Message, request.ActionUrl);

        return new NotificationDto
        {
            Id = notification.Id,
            Type = notification.Type,
            Title = notification.Title,
            Message = notification.Message,
            ActionUrl = notification.ActionUrl,
            ActionText = notification.ActionText,
            RelatedConnectionId = notification.RelatedConnectionId,
            IsRead = notification.IsRead,
            ReadAt = notification.ReadAt,
            CreatedAt = notification.CreatedAt
        };
    }

    public async Task<bool> MarkAsReadAsync(int userId, int notificationId)
    {
        var notification = await _notificationRepository.GetByIdAsync(notificationId);
        
        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            notification.ReadAt = DateTime.UtcNow;
            await _notificationRepository.UpdateAsync(notification);
        }

        return true;
    }

    public async Task<bool> MarkAllAsReadAsync(int userId)
    {
        return await _notificationRepository.MarkAllAsReadAsync(userId);
    }

    public async Task<bool> ArchiveAsync(int userId, int notificationId)
    {
        var notification = await _notificationRepository.GetByIdAsync(notificationId);

        if (notification == null || notification.UserId != userId)
        {
            return false;
        }

        if (notification.ArchivedAt == null)
        {
            notification.ArchivedAt = DateTime.UtcNow;
            await _notificationRepository.UpdateAsync(notification);
        }

        return true;
    }

    public async Task<int> GetUnreadCountAsync(int userId)
    {
        return await _notificationRepository.GetUnreadCountAsync(userId);
    }

    // Plaid-specific notification methods
    public Task CreatePlaidUpdateNotificationAsync(int userId, int connectionId, string institutionName, string errorType)
        => CreateConnectionAttentionAsync(userId, connectionId, institutionName, errorType, $"/update-connection/{connectionId}");

    public Task CreateSimpleFinAttentionNotificationAsync(int userId, int connectionId, string institutionName, string errorType)
        => CreateConnectionAttentionAsync(userId, connectionId, institutionName, errorType, "/settings/simplefin");

    // Bridge connections are reconnected from the accounts page (it opens the Cloud sign-in, then hands the connection back).
    public Task CreateBridgeAttentionNotificationAsync(int userId, int connectionId, string institutionName, string errorType)
        => CreateConnectionAttentionAsync(userId, connectionId, institutionName, errorType, "/accounts");

    private async Task CreateConnectionAttentionAsync(int userId, int connectionId, string institutionName, string errorType, string actionUrl)
    {
        var request = new CreateNotificationRequest
        {
            Type = "plaid_update_required",
            Title = $"Bank Connection Issue - {institutionName}",
            Message = errorType switch
            {
                "ITEM_LOGIN_REQUIRED" => $"Your {institutionName} login credentials have changed and need to be updated.",
                "PENDING_EXPIRATION" => $"Your {institutionName} connection will expire soon and needs renewal.",
                "PENDING_DISCONNECT" => $"Your {institutionName} connection will be disconnected due to security requirements.",
                "NEW_ACCOUNTS_AVAILABLE" => $"{institutionName} reports new accounts you haven't connected yet. Reconnect to add them.",
                _ => $"Your {institutionName} connection needs attention."
            },
            ActionUrl = actionUrl,
            ActionText = "Update Connection",
            RelatedConnectionId = connectionId
        };

        await CreateNotificationAsync(userId, request);
    }

    public async Task CreatePlaidExpirationNotificationAsync(int userId, int connectionId, string institutionName, DateTime expirationDate)
    {
        var daysUntilExpiration = (expirationDate - DateTime.UtcNow).Days;
        
        var request = new CreateNotificationRequest
        {
            Type = "plaid_expiration_warning",
            Title = $"Connection Expires Soon - {institutionName}",
            Message = $"Your {institutionName} connection will expire in {daysUntilExpiration} days. Renew it to continue accessing your account data.",
            ActionUrl = $"/update-connection/{connectionId}",
            ActionText = "Renew Connection",
            RelatedConnectionId = connectionId
        };

        await CreateNotificationAsync(userId, request);
    }

    public async Task CreatePlaidNewAccountsNotificationAsync(int userId, int connectionId, string institutionName)
    {
        var request = new CreateNotificationRequest
        {
            Type = "plaid_new_accounts",
            Title = $"New Accounts Available - {institutionName}",
            Message = $"{institutionName} reports new accounts you haven't connected yet. " +
                      "Add them to keep your full financial picture in Buoy Money.",
            // newAccounts=1 puts the update-connection page into account-selection mode.
            ActionUrl = $"/update-connection/{connectionId}?newAccounts=1",
            ActionText = "Add Accounts",
            RelatedConnectionId = connectionId
        };

        await CreateNotificationAsync(userId, request);
    }

    public async Task CreatePlaidReconnectedNotificationAsync(int userId, int connectionId, string institutionName)
    {
        // Instead of creating a new notification, update the existing error notification
        await UpdateConnectionNotificationToResolvedAsync(userId, connectionId, institutionName);
    }

    public async Task UpdateConnectionNotificationToResolvedAsync(int userId, int connectionId, string institutionName)
    {
        try
        {
            // Get specific connection notifications efficiently
            var plaidNotificationTypes = new[] { 
                "plaid_update_required", 
                "plaid_expiration_warning", 
                "plaid_permission_revoked", 
                "plaid_institution_down" 
            };
            
            var connectionNotifications = await _notificationRepository.GetUserConnectionNotificationsAsync(
                userId, connectionId, plaidNotificationTypes);
            
            var mostRecentNotification = connectionNotifications.FirstOrDefault();

            if (mostRecentNotification != null)
            {
                // Update the existing notification to show it's resolved
                mostRecentNotification.Type = "plaid_reconnected";
                mostRecentNotification.Title = $"✅ Connection Restored - {institutionName}";
                mostRecentNotification.Message = $"Your {institutionName} connection has been successfully updated and is working normally.";
                mostRecentNotification.ActionUrl = null; // Remove action button
                mostRecentNotification.ActionText = null;
                mostRecentNotification.IsRead = false; // Keep it unread so user sees the resolution
                mostRecentNotification.UpdatedAt = DateTime.UtcNow;

                await _notificationRepository.UpdateAsync(mostRecentNotification);

                _logger.LogInformation("Updated notification {NotificationId} to resolved status for connection {ConnectionId}", 
                    mostRecentNotification.Id, connectionId);
            }
            else
            {
                // Fallback: create a new notification if no existing one found
                var request = new CreateNotificationRequest
                {
                    Type = "plaid_reconnected",
                    Title = $"Connection Restored - {institutionName}",
                    Message = $"Your {institutionName} connection has been successfully updated and is working normally.",
                    ActionUrl = null,
                    ActionText = null,
                    RelatedConnectionId = connectionId
                };

                await CreateNotificationAsync(userId, request);
                _logger.LogInformation("Created new resolved notification for connection {ConnectionId} (no existing notification found)", connectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating connection notification to resolved for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task MarkConnectionNotificationsAsReadAsync(int userId, int connectionId)
    {
        try
        {
            // Get specific connection notifications efficiently
            var plaidNotificationTypes = new[] { "plaid_update_required", "plaid_expiration_warning" };
            var connectionNotifications = await _notificationRepository.GetUserConnectionNotificationsAsync(
                userId, connectionId, plaidNotificationTypes);

            foreach (var notification in connectionNotifications)
            {
                notification.IsRead = true;
                notification.ReadAt = DateTime.UtcNow;
                await _notificationRepository.UpdateAsync(notification);
            }

            _logger.LogInformation("Marked {Count} notifications as read for connection {ConnectionId}", 
                connectionNotifications.Count, connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking connection notifications as read for connection {ConnectionId}", connectionId);
            throw;
        }
    }

    public async Task CreateSubscriptionDetectionNotificationAsync(int userId, int detectedCount)
    {
        // Check throttling - max 3 subscription detection notifications per week
        if (await ShouldThrottleSubscriptionNotificationsAsync(userId))
        {
            _logger.LogInformation(
                "Throttled subscription detection notification for user {UserId} - already at max 3/week", 
                userId);
            return;
        }

        var request = new CreateNotificationRequest
        {
            Type = "subscription_detection",
            Title = $"📋 {detectedCount} New Subscription{(detectedCount == 1 ? "" : "s")} Detected",
            Message = detectedCount == 1 
                ? "We found a potential subscription in your transactions. Review it to track your recurring expenses."
                : $"We found {detectedCount} potential subscriptions in your transactions. Review them to track your recurring expenses.",
            ActionUrl = "/subscriptions?tab=detected",
            ActionText = "Review Now"
        };

        await CreateNotificationAsync(userId, request);
        _logger.LogInformation("Created subscription detection notification for user {UserId}: {Count} detected", 
            userId, detectedCount);
    }

    public async Task<int> GetRecentSubscriptionNotificationCountAsync(int userId)
    {
        var oneWeekAgo = DateTime.UtcNow.AddDays(-7);
        return await _notificationRepository.GetNotificationCountByTypeAsync(
            userId, 
            "subscription_detection", 
            oneWeekAgo);
    }

    public async Task<bool> ShouldThrottleSubscriptionNotificationsAsync(int userId)
    {
        const int maxNotificationsPerWeek = 3;
        var count = await GetRecentSubscriptionNotificationCountAsync(userId);
        return count >= maxNotificationsPerWeek;
    }

    public async Task CreateTransactionSubCategoryNotificationAsync(
        int userId,
        int transactionId,
        string transactionDescription,
        string allowanceCategoryName,
        string categorizedByUserName)
    {
        // Truncate description if too long for display
        var displayDescription = SafeText.Ellipsize(transactionDescription, 50);
            
        var request = new CreateNotificationRequest
        {
            Type = "transaction_subcategory_needed",
            Title = $"📝 Sub-categorize Transaction",
            Message = $"{categorizedByUserName} assigned \"{displayDescription}\" to your {allowanceCategoryName} allowance. " +
                      $"Tap to choose which personal budget category it belongs to.",
            ActionUrl = $"/transactions?filter=uncategorized&userCategoryName={Uri.EscapeDataString(allowanceCategoryName)}",
            ActionText = "Categorize"
        };

        await CreateNotificationAsync(userId, request);
        _logger.LogInformation(
            "Created sub-category notification for user {UserId}: transaction {TransactionId} assigned to {AllowanceCategory} by {CategorizedBy}", 
            userId, transactionId, allowanceCategoryName, categorizedByUserName);
    }

    public async Task ArchiveOldNotificationsAsync()
    {
        try
        {
            var oneMonthAgo = DateTime.UtcNow.AddMonths(-1);
            
            // Get all notifications older than 1 month that aren't already archived
            var notifications = await _notificationRepository.GetAllAsync();
            var oldNotifications = notifications
                .Where(n => n.CreatedAt < oneMonthAgo && n.ArchivedAt == null)
                .ToList();

            foreach (var notification in oldNotifications)
            {
                notification.ArchivedAt = DateTime.UtcNow;
                notification.UpdatedAt = DateTime.UtcNow;
                await _notificationRepository.UpdateAsync(notification);
            }

            _logger.LogInformation("Archived {Count} old notifications", oldNotifications.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error archiving old notifications");
            throw;
        }
    }

    public async Task DeleteOldNotificationsAsync()
    {
        try
        {
            var sixMonthsAgo = DateTime.UtcNow.AddMonths(-6);
            
            // Get all notifications older than 6 months
            var notifications = await _notificationRepository.GetAllAsync();
            var veryOldNotifications = notifications
                .Where(n => n.CreatedAt < sixMonthsAgo)
                .ToList();

            foreach (var notification in veryOldNotifications)
            {
                await _notificationRepository.DeleteAsync(notification.Id);
            }

            _logger.LogInformation("Deleted {Count} very old notifications", veryOldNotifications.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting old notifications");
            throw;
        }
    }

    public async Task<(List<NotificationDto> notifications, bool hasMore)> GetUserNotificationsPaginatedAsync(
        int userId, 
        int page = 1, 
        int pageSize = 20, 
        bool includeArchived = false)
    {
        try
        {
            var allNotifications = await _notificationRepository.GetUserNotificationsAsync(userId, page, pageSize * 2); // Get more to check for hasMore
            
            var filteredNotifications = allNotifications
                .Where(n => includeArchived || n.ArchivedAt == null)
                .OrderByDescending(n => n.CreatedAt)
                .Take(pageSize + 1) // Take one extra to check if there are more
                .ToList();

            var hasMore = filteredNotifications.Count > pageSize;
            var notifications = filteredNotifications.Take(pageSize).ToList();

            var notificationDtos = notifications.Select(n => new NotificationDto
            {
                Id = n.Id,
                Type = n.Type,
                Title = n.Title,
                Message = n.Message,
                ActionUrl = n.ActionUrl,
                ActionText = n.ActionText,
                RelatedConnectionId = n.RelatedConnectionId,
                InstitutionName = n.RelatedConnection?.InstitutionName,
                IsRead = n.IsRead,
                ReadAt = n.ReadAt,
                CreatedAt = n.CreatedAt,
                IsArchived = n.ArchivedAt.HasValue
            }).ToList();

            return (notificationDtos, hasMore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting paginated notifications for user {UserId}", userId);
            throw;
        }
    }
}
