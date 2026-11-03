using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Manages data retention policies and automated cleanup operations
/// </summary>
public class DataRetentionManager : IDataRetentionManager
{
    private readonly BuoyAIDbContext _context;
    private readonly ILogger<DataRetentionManager> _logger;
    private readonly IDataRetentionAuditService _auditService;
    private readonly IServiceProvider _services;
    // Lazy: not registered in self-host, and only the hosted churn sweep needs it.
    private readonly Lazy<IBankAggregatorRegistry> _aggregators;
    private readonly IConfiguration _configuration;

    public DataRetentionManager(
        BuoyAIDbContext context,
        ILogger<DataRetentionManager> logger,
        IDataRetentionAuditService auditService,
        IServiceProvider services,
        Lazy<IBankAggregatorRegistry> aggregators,
        IConfiguration configuration)
    {
        _context = context;
        _logger = logger;
        _auditService = auditService;
        _services = services;
        _aggregators = aggregators;
        _configuration = configuration;
    }

    /// <summary>
    /// Cleans up user accounts that have been marked as deleted for more than 30 days
    /// </summary>
    public async Task<int> CleanupClosedUserAccountsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            var usersToDelete = await _context.Users
                .Where(u => u.IsDeleted && u.DeletedAt.HasValue && u.DeletedAt.Value <= cutoffDate)
                .ToListAsync(cancellationToken);

            int deletedCount = 0;
            foreach (var user in usersToDelete)
            {
                try
                {
                    // Permanently delete user record and all data after 30-day retention period
                    await DeleteUserDataCompletely(user.Id, cancellationToken, deleteUserRecord: true);
                    deletedCount++;
                    
                    await _auditService.LogRetentionExecutionAsync(new
                    {
                        Action = "USER_DATA_CLEANUP",
                        Count = 1,
                        Description = $"Permanently deleted user {user.Id} and all associated data"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete user {UserId} during cleanup", user.Id);
                }
            }

            _logger.LogInformation("Cleaned up {Count} user accounts", deletedCount);
            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during user account cleanup");
            throw;
        }
    }

    /// <summary>
    /// Cleans up security audit logs older than 90 days
    /// </summary>
    public async Task<int> CleanupSecurityLogsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-90);
            var logsToDelete = await _context.SecurityAuditLogs
                .Where(log => log.CreatedAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (logsToDelete.Any())
            {
                _context.SecurityAuditLogs.RemoveRange(logsToDelete);
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "SECURITY_LOG_CLEANUP",
                    Count = logsToDelete.Count,
                    Description = $"Deleted {logsToDelete.Count} security audit logs older than 90 days"
                });
            }

            _logger.LogInformation("Cleaned up {Count} security audit logs", logsToDelete.Count);
            return logsToDelete.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during security log cleanup");
            throw;
        }
    }

    /// <summary>
    /// Cleans up error logs older than 365 days
    /// </summary>
    public async Task<int> CleanupErrorLogsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-365);
            var logsToDelete = await _context.ErrorLogs
                .Where(log => log.CreatedAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (logsToDelete.Any())
            {
                _context.ErrorLogs.RemoveRange(logsToDelete);
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "ERROR_LOG_CLEANUP",
                    Count = logsToDelete.Count,
                    Description = $"Deleted {logsToDelete.Count} error logs older than 365 days"
                });
            }

            _logger.LogInformation("Cleaned up {Count} error logs", logsToDelete.Count);
            return logsToDelete.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during error log cleanup");
            throw;
        }
    }

    /// <summary>
    /// Archive or delete AI insights older than 1 year
    /// </summary>
    public async Task<int> CleanupOldAIInsightsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-365);
            var insightsToDelete = await _context.AIInsights
                .Where(insight => insight.CreatedAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (insightsToDelete.Any())
            {
                _context.AIInsights.RemoveRange(insightsToDelete);
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "AI_INSIGHTS_CLEANUP",
                    Count = insightsToDelete.Count,
                    Description = $"Deleted {insightsToDelete.Count} AI insights older than 1 year"
                });
            }

            _logger.LogInformation("Cleaned up {Count} old AI insights", insightsToDelete.Count);
            return insightsToDelete.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AI insights cleanup");
            throw;
        }
    }

    /// <summary>
    /// Delete AI refresh usage records older than 90 days
    /// </summary>
    public async Task<int> CleanupAIRefreshUsageAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-90);
            var usagesToDelete = await _context.AIRefreshUsages
                .Where(usage => usage.LastRefreshAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (usagesToDelete.Any())
            {
                _context.AIRefreshUsages.RemoveRange(usagesToDelete);
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "AI_REFRESH_USAGE_CLEANUP",
                    Count = usagesToDelete.Count,
                    Description = $"Deleted {usagesToDelete.Count} AI refresh usage records older than 90 days"
                });
            }

            _logger.LogInformation("Cleaned up {Count} AI refresh usage records", usagesToDelete.Count);
            return usagesToDelete.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during AI refresh usage cleanup");
            throw;
        }
    }

    /// <summary>
    /// Delete transaction data for disconnected accounts that have been inactive for at least 30 days.
    /// Only targets accounts whose owning user is marked as deleted, preventing accidental data loss
    /// from temporarily disconnected connections (e.g. failed deletion attempts, re-authentication).
    /// </summary>
    public async Task<int> CleanupDisconnectedAccountDataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var gracePeriodCutoff = DateTime.UtcNow.AddDays(-30);

            var disconnectedAccounts = await _context.Accounts
                .Include(a => a.User)
                .Where(a => a.PlaidConnectionId != null &&
                           !_context.PlaidConnections.Any(pc => pc.Id == a.PlaidConnectionId && pc.IsActive) &&
                           a.User != null && a.User.IsDeleted &&
                           a.User.DeletedAt.HasValue && a.User.DeletedAt.Value <= gracePeriodCutoff)
                .ToListAsync(cancellationToken);

            int deletedCount = 0;
            foreach (var account in disconnectedAccounts)
            {
                var transactions = await _context.Transactions
                    .Where(t => t.AccountId == account.Id)
                    .ToListAsync(cancellationToken);

                _context.Transactions.RemoveRange(transactions);
                deletedCount += transactions.Count;
            }

            if (deletedCount > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "DISCONNECTED_ACCOUNT_CLEANUP",
                    Count = deletedCount,
                    Description = $"Deleted {deletedCount} transactions from {disconnectedAccounts.Count} disconnected accounts (30-day grace period, deleted users only)"
                });
            }

            _logger.LogInformation("Cleaned up {Count} transactions from disconnected accounts", deletedCount);
            return deletedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnected account cleanup");
            throw;
        }
    }

    /// <summary>
    /// Clean up expired Plaid access tokens
    /// </summary>
    public async Task<int> CleanupExpiredPlaidTokensAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Find Plaid connections that have been disconnected for more than 30 days
            var cutoffDate = DateTime.UtcNow.AddDays(-30);
            var expiredConnections = await _context.PlaidConnections
                .Where(pc => !pc.IsActive && pc.UpdatedAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (expiredConnections.Any())
            {
                // Clear the access tokens but keep the connection record for audit purposes
                foreach (var connection in expiredConnections)
                {
                    connection.AccessToken = string.Empty;
                }

                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "EXPIRED_PLAID_TOKEN_CLEANUP",
                    Count = expiredConnections.Count,
                    Description = $"Cleared access tokens for {expiredConnections.Count} expired Plaid connections"
                });
            }

            _logger.LogInformation("Cleaned up {Count} expired Plaid tokens", expiredConnections.Count);
            return expiredConnections.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during expired Plaid token cleanup");
            throw;
        }
    }

    public async Task<int> CleanupChurnedAccountPlaidItemsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Grace measured from when the account's coverage lapsed. Tunable via config; a
            // conservative default so we never yank a bank link from someone briefly lapsed.
            var graceDays = _configuration.GetValue<int?>("DataRetention:PlaidChurnGraceDays") ?? 60;
            var cutoff = DateTime.UtcNow.AddDays(-graceDays);

            // Live Plaid links we have not yet revoked. NOT gated on IsActive: a churned user
            // who simply stopped paying keeps IsActive=true, and that live access_token is exactly
            // what Plaid keeps billing Transactions (~$0.30/item/month) for. PlaidRemovedAt makes
            // the sweep idempotent so we never re-hit /item/remove on the same connection nightly.
            var candidates = await _context.PlaidConnections
                .Where(pc => pc.PlaidRemovedAt == null
                    && pc.Provider == BankProvider.Plaid
                    && pc.AccessToken != null && pc.AccessToken != "")
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0)
            {
                return 0;
            }

            // Load each owning user once (with their billing subscriptions) to classify churn.
            var userIds = candidates.Select(pc => pc.UserId).Distinct().ToList();
            var usersById = await _context.Users
                .Where(u => userIds.Contains(u.Id))
                .Include(u => u.UserSubscriptions)
                .ToDictionaryAsync(u => u.Id, cancellationToken);

            var removed = 0;
            foreach (var connection in candidates)
            {
                if (!usersById.TryGetValue(connection.UserId, out var user))
                {
                    continue;
                }
                if (!IsChurnedPastGrace(user, cutoff))
                {
                    continue;
                }

                // The candidates are Plaid connections; a server with no Plaid has none, so the
                // lookup only fails if the wiring is wrong, and then there is nothing to revoke.
                if (!_aggregators.Value.TryGetForLifecycle(connection.Provider, out var aggregator))
                {
                    continue;
                }

                var revoked = await aggregator!.TryRemoveConnectionAsync(connection, cancellationToken);
                if (!revoked)
                {
                    // Transient Plaid failure — leave PlaidRemovedAt null so we retry next run
                    // rather than marking a still-billing Item as handled.
                    continue;
                }

                connection.IsActive = false;
                // Same as a user removal: the accounts leave with the connection, transactions stay.
                foreach (var account in await _context.Accounts
                    .Where(a => a.PlaidConnectionId == connection.Id && a.IsActive)
                    .ToListAsync(cancellationToken))
                {
                    account.IsActive = false;
                    account.UpdatedAt = DateTime.UtcNow;
                }
                connection.PlaidRemovedAt = DateTime.UtcNow;
                connection.UpdatedAt = DateTime.UtcNow;
                removed++;
            }

            if (removed > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "CHURNED_PLAID_ITEM_REMOVAL",
                    Count = removed,
                    Description = $"Revoked {removed} Plaid item(s) for accounts churned > {graceDays} days (stops ongoing Transactions billing)"
                });
            }

            _logger.LogInformation(
                "Revoked {Count} Plaid item(s) for churned accounts (grace {GraceDays}d)", removed, graceDays);
            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during churned-account Plaid item removal");
            throw;
        }
    }

    /// <summary>
    /// True when the account has churned (trial expired AND no active subscription) and its
    /// coverage lapsed more than the grace window ago. Deliberately conservative: never touches
    /// operator/admin accounts, family members on inherited premium, users still in trial, or
    /// users with an active subscription.
    /// </summary>
    private bool IsChurnedPastGrace(User user, DateTime cutoff)
    {
        // Operator/admin accounts are never churned — Admin is granted purely by the email
        // allowlist and never expires.
        if (AdminPolicy.IsAdminEmail(_configuration, user.Email))
        {
            return false;
        }
        if (user.SubscriptionTier == SubscriptionTier.Admin)
        {
            return false;
        }
        // Family members' inherited premium is gated purely on the persisted tier, which is
        // stripped the moment the owner's coverage ends. So PremiumInherited == still covered.
        if (user.SubscriptionTier == SubscriptionTier.PremiumInherited)
        {
            return false;
        }
        // Deleted accounts are handled by the account-deletion path (which already revokes Plaid).
        if (user.IsDeleted)
        {
            return false;
        }
        // Still inside an active trial → covered.
        if (user.IsTrialActive && user.TrialEndDate.HasValue && user.TrialEndDate.Value > DateTime.UtcNow)
        {
            return false;
        }
        // An active own subscription → covered.
        if (user.UserSubscriptions.Any(s => s.Status == UserSubscriptionStatus.Active))
        {
            return false;
        }

        // Anchor the grace window on when coverage actually lapsed: the later of the trial end and
        // the newest subscription end (EndDate, or NextBillingDate if a canceled sub left EndDate
        // unset). A sub still inside its paid-through window (future end) keeps the user covered.
        DateTime? coverageEnd = user.TrialEndDate;
        foreach (var sub in user.UserSubscriptions)
        {
            var subEnd = sub.EndDate ?? sub.NextBillingDate;
            if (coverageEnd == null || subEnd > coverageEnd)
            {
                coverageEnd = subEnd;
            }
        }

        // No anchor (never trialed or subscribed) → do not touch; can't prove churn-past-grace.
        if (coverageEnd == null)
        {
            return false;
        }
        return coverageEnd.Value <= cutoff;
    }

    /// <summary>
    /// Cleans up expired passkey challenges (expired or older than 1 hour)
    /// Prevents stale challenge buildup from incomplete registration flows
    /// </summary>
    public async Task<int> CleanupExpiredPasskeyChallengesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Remove challenges that are either:
            // 1. Expired (based on ExpiresAt field)
            // 2. Older than 1 hour (for orphaned challenges)
            var cutoffDate = DateTime.UtcNow.AddHours(-1);
            var now = DateTime.UtcNow;
            
            var expiredChallenges = await _context.PasskeyChallenges
                .Where(pc => pc.ExpiresAt <= now || pc.CreatedAt <= cutoffDate)
                .ToListAsync(cancellationToken);

            if (expiredChallenges.Any())
            {
                _context.PasskeyChallenges.RemoveRange(expiredChallenges);
                await _context.SaveChangesAsync(cancellationToken);

                await _auditService.LogRetentionExecutionAsync(new
                {
                    Action = "PASSKEY_CHALLENGE_CLEANUP",
                    Count = expiredChallenges.Count,
                    Description = $"Deleted {expiredChallenges.Count} expired passkey challenges"
                });
            }

            _logger.LogInformation("Cleaned up {Count} expired passkey challenges", expiredChallenges.Count);
            return expiredChallenges.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during passkey challenge cleanup");
            throw;
        }
    }

    /// <summary>
    /// Immediately deletes a user's data per GDPR/CCPA requirements
    /// </summary>
    public async Task DeleteUserDataImmediatelyAsync(int userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (user == null)
            {
                _logger.LogWarning("User {UserId} not found for immediate deletion", userId);
                return;
            }

            // Mark user as deleted with immediate deletion flag
            user.IsDeleted = true;
            user.DeletedAt = DateTime.UtcNow;
            user.DeletionReason = "immediate_request";

            await _context.SaveChangesAsync(cancellationToken);

            // Perform complete data deletion but preserve user record for audit trail
            await DeleteUserDataCompletely(userId, cancellationToken, deleteUserRecord: false);

            await _auditService.LogUserDataDeletionAsync(userId, "immediate_request", "complete_deletion");

            _logger.LogInformation("Immediately deleted user {UserId} and all data", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during immediate user data deletion for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// Verifies that a user's data has been completely deleted
    /// </summary>
    public async Task<bool> VerifyDataDeletionAsync(int userId, CancellationToken cancellationToken = default)
    {
        try
        {
            // Check if user exists and is properly marked as deleted
            var user = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (user == null)
            {
                // User is completely deleted - this is also valid
                return true;
            }
            
            if (!user.IsDeleted)
            {
                // User exists but not marked as deleted - deletion failed
                return false;
            }

            // User is marked as deleted, now check for any remaining data
            var accountsExist = await _context.Accounts.AnyAsync(a => a.UserId == userId, cancellationToken);
            var transactionsExist = await _context.Transactions.AnyAsync(t => t.Account.UserId == userId, cancellationToken);
            var budgetsExist = await _context.Budgets.AnyAsync(b => b.UserId == userId, cancellationToken);
            var savingsExist = await _context.SavingsGoals.AnyAsync(s => s.UserId == userId, cancellationToken);
            var subscriptionsExist = await _context.Subscriptions.AnyAsync(s => s.UserId == userId, cancellationToken);
            var aiInsightsExist = await _context.AIInsights.AnyAsync(ai => ai.UserId == userId, cancellationToken);
            var plaidConnectionsExist = await _context.PlaidConnections.AnyAsync(pc => pc.UserId == userId, cancellationToken);

            var isDataDeleted = !accountsExist && !transactionsExist && !budgetsExist && !savingsExist && 
                               !subscriptionsExist && !aiInsightsExist && !plaidConnectionsExist;

            _logger.LogInformation("Data deletion verification for user {UserId}: User marked as deleted = {UserDeleted}, Data cleared = {DataDeleted}", 
                userId, user.IsDeleted, isDataDeleted);

            return isDataDeleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during data deletion verification for user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Performs complete deletion of all user data across all tables.
    /// Uses raw SQL for performance and correct FK ordering.
    /// Wraps in CreateExecutionStrategy().ExecuteAsync() to work with NpgsqlRetryingExecutionStrategy.
    /// </summary>
    private async Task DeleteUserDataCompletely(int userId, CancellationToken cancellationToken, bool deleteUserRecord = true)
    {
        // Capture the Stripe customer id BEFORE the local wipe: the SQL/EF deletion nulls
        // StripeCustomerId (anonymize) or drops the row entirely, so we'd otherwise lose the
        // reference and orphan the customer on Stripe's side.
        var stripeCustomerId = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => u.StripeCustomerId)
            .FirstOrDefaultAsync(cancellationToken);

        var isInMemory = _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory";

        if (isInMemory)
        {
            await DeleteUserDataViaEF(userId, cancellationToken, deleteUserRecord);
        }
        else
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    await DeleteUserDataViaSql(userId, cancellationToken, deleteUserRecord);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });
        }

        // Delete the Stripe customer AFTER the local deletion has committed. Deleting the
        // customer on Stripe cascades: it cancels active subscriptions and detaches saved
        // payment methods, satisfying the retention policy's payment-data disposal promise.
        // Fail-open: a Stripe API hiccup must never roll back or block the GDPR/CCPA-required
        // local deletion — we log the customer id so an orphan can be reconciled manually.
        await TryDeleteStripeCustomerAsync(userId, stripeCustomerId);

        _logger.LogInformation("Successfully deleted all data for user {UserId}", userId);
    }

    /// <summary>
    /// Best-effort deletion of the user's Stripe customer (cancels subscriptions + detaches
    /// payment methods as a side effect). Never throws — Stripe is an external dependency and
    /// its failure must not block local data deletion.
    /// </summary>
    private async Task TryDeleteStripeCustomerAsync(int userId, string? stripeCustomerId)
    {
        if (string.IsNullOrWhiteSpace(stripeCustomerId))
        {
            return; // user never had a Stripe customer (never subscribed)
        }

        try
        {
            // Resolved on demand: the Stripe client cannot be built without keys, and
            // data deletion must still run (and be catchable below) in that case.
            var deleted = await _services.GetRequiredService<IStripeService>().DeleteCustomerAsync(stripeCustomerId);
            if (!deleted)
            {
                // Declined because the customer belongs to another environment. Local data
                // deletion still proceeds; the audit trail must say what actually happened
                // rather than claiming a deletion Stripe never performed.
                _logger.LogWarning(
                    "Stripe customer {StripeCustomerId} for user {UserId} was NOT deleted: it belongs to another environment. Local data deletion continues.",
                    stripeCustomerId, userId);
                await TryAuditAsync(userId, new
                {
                    Action = "STRIPE_CUSTOMER_DELETE_SKIPPED",
                    UserId = userId,
                    StripeCustomerId = stripeCustomerId,
                    Description = $"Skipped deleting Stripe customer {stripeCustomerId} for user {userId}: owned by another environment"
                });
                return;
            }

            await TryAuditAsync(userId, new
            {
                Action = "STRIPE_CUSTOMER_DELETE",
                UserId = userId,
                StripeCustomerId = stripeCustomerId,
                Description = $"Deleted Stripe customer {stripeCustomerId} for user {userId} (cancels subscriptions + detaches payment methods)"
            });
        }
        catch (Exception ex)
        {
            // Log with the customer id so the orphaned Stripe customer can be reconciled by hand.
            _logger.LogError(ex,
                "Failed to delete Stripe customer {StripeCustomerId} for user {UserId}; local data was deleted but the Stripe customer may be orphaned",
                stripeCustomerId, userId);
            await TryAuditAsync(userId, new
            {
                Action = "STRIPE_CUSTOMER_DELETE_FAILED",
                UserId = userId,
                StripeCustomerId = stripeCustomerId,
                Description = $"Stripe customer {stripeCustomerId} deletion FAILED for user {userId}: {ex.Message}. Manual reconciliation required."
            });
        }
    }

    /// <summary>
    /// Writes a retention audit record without letting an audit failure surface. The audit
    /// service rethrows on error; by this point the user's data is already deleted, so throwing
    /// would fail the caller (and, on the immediate-deletion path, 500 a request that actually
    /// succeeded). Losing an audit line is strictly better than that.
    /// </summary>
    private async Task TryAuditAsync(int userId, object summary)
    {
        try
        {
            await _auditService.LogRetentionExecutionAsync(summary);
        }
        catch (Exception auditEx)
        {
            _logger.LogError(auditEx,
                "Failed to write Stripe-deletion retention audit record for user {UserId}", userId);
        }
    }

    /// <summary>
    /// Deletes all user data using raw SQL in correct FK dependency order.
    /// Every table referencing Users or its children is covered.
    /// </summary>
    private async Task DeleteUserDataViaSql(int userId, CancellationToken cancellationToken, bool deleteUserRecord)
    {
        // Phase 1: Null out cross-reference FKs that would block deletion
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Budgets"" SET ""DefaultSavingsGoalId"" = NULL WHERE ""UserId"" = {0};
              UPDATE ""Budgets"" SET ""ParentBudgetId"" = NULL WHERE ""UserId"" = {0};
              UPDATE ""Subscriptions"" SET ""LinkedTransactionId"" = NULL WHERE ""UserId"" = {0};
              UPDATE ""Notifications"" SET ""RelatedConnectionId"" = NULL WHERE ""UserId"" = {0};
              UPDATE ""Accounts"" SET ""PlaidConnectionId"" = NULL WHERE ""UserId"" = {0};
              UPDATE ""Transactions"" SET ""ClaimedByUserId"" = NULL WHERE ""ClaimedByUserId"" = {0};
              UPDATE ""Categories"" SET ""CreatedByUserId"" = NULL WHERE ""CreatedByUserId"" = {0};",
            userId);

        // Phase 2: Delete leaf tables (no children referencing them)
        // AI & categorization
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""AICategorizationQueue"" WHERE ""UserId"" = {0};
              DELETE FROM ""AIRefreshUsages"" WHERE ""UserId"" = {0};
              DELETE FROM ""AIUsageRecords"" WHERE ""UserId"" = {0};
              DELETE FROM ""AIInsights"" WHERE ""UserId"" = {0};
              DELETE FROM ""ReceiptScans"" WHERE ""UserId"" = {0};",
            userId);

        // AI conversations & tools
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""AiConversationMessages"" WHERE ""ConversationId"" IN (SELECT ""Id"" FROM ""AiConversations"" WHERE ""UserId"" = {0});
              DELETE FROM ""AiConversations"" WHERE ""UserId"" = {0};
              DELETE FROM ""AiToolAuditLogs"" WHERE ""UserId"" = {0};
              DELETE FROM ""AiToolExecutions"" WHERE ""UserId"" = {0};
              DELETE FROM ""AiActionFeedback"" WHERE ""UserId"" = {0};
              DELETE FROM ""PendingToolConfirmations"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserAiMemories"" WHERE ""UserId"" = {0};",
            userId);

        // Budget children (must come before Budgets and SavingsGoals)
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""BudgetPeriodCategorySnapshots"" WHERE ""BudgetPeriodSnapshotId"" IN (SELECT ""Id"" FROM ""BudgetPeriodSnapshots"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0}));
              DELETE FROM ""BudgetPeriodSnapshots"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0});
              DELETE FROM ""BudgetShares"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0});
              DELETE FROM ""BudgetTransfers"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0});
              DELETE FROM ""BudgetTransfers"" WHERE ""CreatedByUserId"" = {0};
              DELETE FROM ""SavingsAllocations"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0});
              DELETE FROM ""SavingsAllocations"" WHERE ""SavingsGoalId"" IN (SELECT ""Id"" FROM ""SavingsGoals"" WHERE ""UserId"" = {0});
              DELETE FROM ""BudgetCategories"" WHERE ""BudgetId"" IN (SELECT ""Id"" FROM ""Budgets"" WHERE ""UserId"" = {0});",
            userId);

        // Subscription children (must come before Subscriptions and UserSubscriptions)
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SubscriptionTransactions"" WHERE ""SubscriptionId"" IN (SELECT ""Id"" FROM ""Subscriptions"" WHERE ""UserId"" = {0});
              DELETE FROM ""SubscriptionPayments"" WHERE ""UserSubscriptionId"" IN (SELECT ""Id"" FROM ""UserSubscriptions"" WHERE ""UserId"" = {0});
              DELETE FROM ""SuggestedSubscriptions"" WHERE ""UserId"" = {0};
              DELETE FROM ""DismissedSubscriptionPatterns"" WHERE ""UserId"" = {0};
              DELETE FROM ""Subscriptions"" WHERE ""UserId"" = {0};",
            userId);

        // Savings
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SavingsGoals"" WHERE ""UserId"" = {0};",
            userId);

        // Budgets (children already deleted above)
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""Budgets"" WHERE ""UserId"" = {0};",
            userId);

        // Transactions (must come before Accounts)
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""Transactions"" WHERE ""AccountId"" IN (SELECT ""Id"" FROM ""Accounts"" WHERE ""UserId"" = {0});",
            userId);

        // Accounts & Plaid
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""Accounts"" WHERE ""UserId"" = {0};
              DELETE FROM ""PlaidConnections"" WHERE ""UserId"" = {0};",
            userId);

        // Categories & rules
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""CategorizationRules"" WHERE ""UserId"" = {0};
              DELETE FROM ""CategoryGroups"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserCategories"" WHERE ""UserId"" = {0};
              DELETE FROM ""Categories"" WHERE ""UserId"" = {0};",
            userId);

        // Gamification & engagement
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""UserAchievements"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserChallenges"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserStreaks"" WHERE ""UserId"" = {0};
              DELETE FROM ""Referrals"" WHERE ""ReferrerUserId"" = {0} OR ""RefereeUserId"" = {0};
              DELETE FROM ""UserVisualizations"" WHERE ""UserId"" = {0};
              DELETE FROM ""Notifications"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserOnboardingQuizzes"" WHERE ""UserId"" = {0};",
            userId);

        // Auth & security
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""LoginAttempts"" WHERE ""UserId"" = {0};
              DELETE FROM ""TrustedDevices"" WHERE ""UserId"" = {0};
              DELETE FROM ""TwoFactorCodes"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserMfaPreferences"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserPasskeys"" WHERE ""UserId"" = {0};
              DELETE FROM ""PasskeyChallenges"" WHERE ""UserId"" = {0};
              DELETE FROM ""SecurityAuditLogs"" WHERE ""UserId"" = {0};
              DELETE FROM ""ErrorLogs"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserConsents"" WHERE ""UserId"" = {0};
              DELETE FROM ""password_reset_tokens"" WHERE ""UserId"" = {0};
              DELETE FROM ""user_email_preferences"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserEntitlements"" WHERE ""UserId"" = {0};
              DELETE FROM ""UserSubscriptions"" WHERE ""UserId"" = {0};",
            userId);

        // Family (handle ownership transfer or removal)
        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""FamilyInvitations"" WHERE ""InvitedByUserId"" = {0};
              DELETE FROM ""FamilyMembers"" WHERE ""UserId"" = {0};",
            userId);

        // Transfer family ownership if this user created the family
        var ownedFamilies = await _context.Families
            .Where(f => f.CreatedByUserId == userId)
            .ToListAsync(cancellationToken);
        foreach (var family in ownedFamilies)
        {
            var otherMember = await _context.FamilyMembers
                .Where(fm => fm.FamilyId == family.Id && fm.UserId != userId)
                .FirstOrDefaultAsync(cancellationToken);
            if (otherMember != null)
            {
                family.CreatedByUserId = otherMember.UserId;
            }
            else
            {
                _context.Families.Remove(family);
            }
        }
        await _context.SaveChangesAsync(cancellationToken);

        // Phase 3: Optionally delete or anonymize the user record
        if (deleteUserRecord)
        {
            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""Users"" WHERE ""Id"" = {0};", userId);
        }
        else
        {
            // Anonymize PII while preserving the audit record
            await _context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Users"" SET
                    ""Email"" = 'deleted-' || ""Id"" || '@deleted.local',
                    ""FirstName"" = 'Deleted',
                    ""LastName"" = 'User',
                    ""Username"" = 'deleted-' || ""Id"",
                    ""PasswordHash"" = '',
                    ""GoogleId"" = NULL,
                    ""AppleId"" = NULL,
                    ""ProfilePictureUrl"" = NULL,
                    ""TotpSecret"" = NULL,
                    ""RecoveryCodes"" = NULL,
                    ""StripeCustomerId"" = NULL,
                    ""FamilyId"" = NULL,
                    ""ReferralCode"" = NULL,
                    ""SubscriptionPromptCategories"" = '[]'
                  WHERE ""Id"" = {0};", userId);
        }
    }

    /// <summary>
    /// EF Core fallback for InMemory database (tests). Covers the same tables as DeleteUserDataViaSql.
    /// </summary>
    private async Task DeleteUserDataViaEF(int userId, CancellationToken cancellationToken, bool deleteUserRecord)
    {
        _context.AIRefreshUsages.RemoveRange(await _context.AIRefreshUsages.Where(x => x.UserId == userId).ToListAsync(cancellationToken));
        _context.AIInsights.RemoveRange(await _context.AIInsights.Where(x => x.UserId == userId).ToListAsync(cancellationToken));

        var subscriptions = await _context.Subscriptions.Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        foreach (var sub in subscriptions)
        {
            _context.SubscriptionTransactions.RemoveRange(
                await _context.SubscriptionTransactions.Where(st => st.SubscriptionId == sub.Id).ToListAsync(cancellationToken));
        }
        _context.Subscriptions.RemoveRange(subscriptions);

        var userBudgetIds = await _context.Budgets.Where(b => b.UserId == userId).Select(b => b.Id).ToListAsync(cancellationToken);
        var budgetTransfers = await _context.BudgetTransfers.Where(bt => userBudgetIds.Contains(bt.BudgetId) || bt.CreatedByUserId == userId).ToListAsync(cancellationToken);
        _context.BudgetTransfers.RemoveRange(budgetTransfers);

        var savingsGoals = await _context.SavingsGoals.Where(sg => sg.UserId == userId).ToListAsync(cancellationToken);
        _context.SavingsGoals.RemoveRange(savingsGoals);

        var budgets = await _context.Budgets.Where(b => b.UserId == userId).ToListAsync(cancellationToken);
        _context.Budgets.RemoveRange(budgets);

        var accounts = await _context.Accounts.Where(a => a.UserId == userId).ToListAsync(cancellationToken);
        foreach (var acct in accounts)
        {
            _context.Transactions.RemoveRange(
                await _context.Transactions.Where(t => t.AccountId == acct.Id).ToListAsync(cancellationToken));
        }
        _context.Accounts.RemoveRange(accounts);

        _context.PlaidConnections.RemoveRange(await _context.PlaidConnections.Where(pc => pc.UserId == userId).ToListAsync(cancellationToken));
        _context.SecurityAuditLogs.RemoveRange(await _context.SecurityAuditLogs.Where(sal => sal.UserId == userId).ToListAsync(cancellationToken));

        if (deleteUserRecord)
        {
            var user = await _context.Users.FindAsync(new object[] { userId }, cancellationToken);
            if (user != null) _context.Users.Remove(user);
        }

        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Successfully deleted all data for user {UserId} via EF (InMemory)", userId);
    }
}
