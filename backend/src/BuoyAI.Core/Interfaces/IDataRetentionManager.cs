namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Interface for managing automated data retention and deletion operations
/// </summary>
public interface IDataRetentionManager
{
    /// <summary>
    /// Delete user accounts that have been closed for more than 30 days
    /// </summary>
    Task<int> CleanupClosedUserAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete security audit logs older than 90 days
    /// </summary>
    Task<int> CleanupSecurityLogsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete error logs older than 30 days
    /// </summary>
    Task<int> CleanupErrorLogsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Archive or delete AI insights older than 1 year
    /// </summary>
    Task<int> CleanupOldAIInsightsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete AI refresh usage records older than 90 days
    /// </summary>
    Task<int> CleanupAIRefreshUsageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete transaction data for disconnected accounts (immediate)
    /// </summary>
    Task<int> CleanupDisconnectedAccountDataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up expired Plaid access tokens
    /// </summary>
    Task<int> CleanupExpiredPlaidTokensAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Revoke the Plaid Item (/item/remove) for accounts that have churned — trial expired
    /// AND no active subscription — for longer than the configured grace period. As long as a
    /// live access_token exists, Plaid keeps billing Transactions (~$0.30/item/month), so a
    /// walked-away account leaks money indefinitely. Idempotent via PlaidConnection.PlaidRemovedAt.
    /// Returns the number of Plaid items revoked.
    /// </summary>
    Task<int> CleanupChurnedAccountPlaidItemsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up expired passkey challenges (expired or older than 1 hour)
    /// </summary>
    Task<int> CleanupExpiredPasskeyChallengesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete specific user data immediately (for user deletion requests)
    /// </summary>
    Task DeleteUserDataImmediatelyAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verify data deletion completion for audit purposes
    /// </summary>
    Task<bool> VerifyDataDeletionAsync(int userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for auditing data retention operations
/// </summary>
public interface IDataRetentionAuditService
{
    /// <summary>
    /// Log the execution of a data retention cleanup operation
    /// </summary>
    Task LogRetentionExecutionAsync(object retentionSummary);

    /// <summary>
    /// Log user data deletion requests
    /// </summary>
    Task LogUserDataDeletionAsync(int userId, string requestSource, string deletionType);

    /// <summary>
    /// Get retention audit logs for compliance reporting
    /// </summary>
    Task<IEnumerable<object>> GetRetentionAuditLogsAsync(DateTime fromDate, DateTime toDate);
}
