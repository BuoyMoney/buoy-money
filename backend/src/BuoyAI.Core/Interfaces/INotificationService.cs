using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

public interface INotificationService
{
    Task<List<NotificationDto>> GetUserNotificationsAsync(int userId, int page = 1, int pageSize = 20);
    Task<NotificationSummaryDto> GetNotificationSummaryAsync(int userId);
    Task<NotificationDto> CreateNotificationAsync(int userId, CreateNotificationRequest request);
    Task<bool> MarkAsReadAsync(int userId, int notificationId);
    Task<bool> MarkAllAsReadAsync(int userId);
    /// <summary>Archives a single notification for the user (sets ArchivedAt). Returns
    /// false if it doesn't exist or belongs to another user.</summary>
    Task<bool> ArchiveAsync(int userId, int notificationId);
    Task<int> GetUnreadCountAsync(int userId);
    
    // Specific methods for Plaid notifications
    Task CreatePlaidUpdateNotificationAsync(int userId, int connectionId, string institutionName, string errorType);
    /// <summary>Same alert for a SimpleFIN connection; its action opens the SimpleFIN settings page, not Plaid update mode.</summary>
    Task CreateSimpleFinAttentionNotificationAsync(int userId, int connectionId, string institutionName, string errorType);
    /// <summary>Same alert for a BuoyAI Cloud bank feed (Bridge) connection; its action opens the accounts page, where Bridge connections are reconnected.</summary>
    Task CreateBridgeAttentionNotificationAsync(int userId, int connectionId, string institutionName, string errorType);
    Task CreatePlaidExpirationNotificationAsync(int userId, int connectionId, string institutionName, DateTime expirationDate);
    Task CreatePlaidReconnectedNotificationAsync(int userId, int connectionId, string institutionName);

    /// <summary>
    /// NEW_ACCOUNTS_AVAILABLE webhook: prompt the user into update mode with account
    /// selection enabled so they can add the newly available accounts.
    /// </summary>
    Task CreatePlaidNewAccountsNotificationAsync(int userId, int connectionId, string institutionName);
    Task MarkConnectionNotificationsAsReadAsync(int userId, int connectionId);
    Task UpdateConnectionNotificationToResolvedAsync(int userId, int connectionId, string institutionName);
    
    // Subscription detection notifications
    /// <summary>
    /// Create a subscription detection notification. Subject to throttling (max 3/week).
    /// </summary>
    Task CreateSubscriptionDetectionNotificationAsync(int userId, int detectedCount);
    
    /// <summary>
    /// Get the count of subscription detection notifications created in the past week.
    /// </summary>
    Task<int> GetRecentSubscriptionNotificationCountAsync(int userId);
    
    /// <summary>
    /// Check if subscription detection notifications should be throttled for a user.
    /// </summary>
    Task<bool> ShouldThrottleSubscriptionNotificationsAsync(int userId);
    
    // Transaction sub-categorization notifications (for allowances)
    /// <summary>
    /// Create a notification for the allowance owner when a transaction is categorized to their allowance,
    /// prompting them to assign a personal sub-category.
    /// </summary>
    /// <param name="userId">The allowance owner's user ID</param>
    /// <param name="transactionId">The transaction ID that was categorized</param>
    /// <param name="transactionDescription">Description of the transaction for display</param>
    /// <param name="allowanceCategoryName">Name of the allowance category</param>
    /// <param name="categorizedByUserName">Name of the user who categorized the transaction</param>
    Task CreateTransactionSubCategoryNotificationAsync(
        int userId, 
        int transactionId, 
        string transactionDescription,
        string allowanceCategoryName,
        string categorizedByUserName);
    
    // Archiving and cleanup methods
    Task ArchiveOldNotificationsAsync();
    Task DeleteOldNotificationsAsync();
    Task<(List<NotificationDto> notifications, bool hasMore)> GetUserNotificationsPaginatedAsync(
        int userId, 
        int page = 1, 
        int pageSize = 20, 
        bool includeArchived = false);
}

public interface INotificationRepository
{
    Task<List<Notification>> GetUserNotificationsAsync(int userId, int page, int pageSize);
    Task<List<Notification>> GetAllAsync(); // For archiving operations
    Task<Notification?> GetByIdAsync(int id);
    Task<Notification> CreateAsync(Notification notification);
    Task<Notification> UpdateAsync(Notification notification);
    Task<bool> DeleteAsync(int id); // For cleanup operations
    Task<int> GetUnreadCountAsync(int userId);
    Task<bool> MarkAllAsReadAsync(int userId);
    Task<List<Notification>> GetUserConnectionNotificationsAsync(int userId, int connectionId, string[] notificationTypes);
    
    /// <summary>
    /// Count notifications of a specific type for a user since a given date.
    /// </summary>
    Task<int> GetNotificationCountByTypeAsync(int userId, string type, DateTime since);
}
