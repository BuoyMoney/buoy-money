using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

public class PlaidTransactionSyncService : BackgroundService
{
    private const string JobName = "PlaidTransactionSync";
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PlaidTransactionSyncService> _logger;
    private readonly BuoyAiMetrics _metrics;

    public PlaidTransactionSyncService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PlaidTransactionSyncService> logger,
        BuoyAiMetrics metrics)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a bit on startup to let the application fully initialize
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _metrics.RecordBackgroundJobRun(JobName);
                await SyncAllActiveConnections(stoppingToken);
                stopwatch.Stop();
                _metrics.RecordBackgroundJobSuccess(JobName);
                _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
                
                // Sync every 10 minutes for fully imported accounts
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown — exit gracefully
                break;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
                _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
                _logger.LogError(ex, "Error in PlaidTransactionSyncService");
                
                // Wait 2 minutes on error before retrying
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            }
        }
    }

    private async Task SyncAllActiveConnections(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        // Absent on a self-hosted server (no Plaid of its own); then there is no Plaid-only cleanup to run.
        var plaidService = scope.ServiceProvider.GetService<IPlaidService>();
        var registry = scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>();
        var connectionRepository = scope.ServiceProvider.GetRequiredService<IPlaidConnectionRepository>();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        // Get all active connections (completed initial import and not in error state)
        var allActiveConnections = await connectionRepository.GetActiveConnectionsForSync();

        // Leave out connections whose provider asks for more time between calls (SimpleFIN allows
        // about 24 requests a day, so it is not touched on every ten-minute wake).
        var activeConnections = SelectDueConnections(allActiveConnections, registry, DateTime.UtcNow);
        
        await ReconcileBridgeConnectionsAsync(connectionRepository, registry, _logger, DateTime.UtcNow);

        // Update active Plaid connections gauge
        _metrics.SetActivePlaidConnections(allActiveConnections.Count(c => c.Provider == BankProvider.Plaid));
        
        // Get count of users with recent activity (logged in within last 5 minutes)
        var recentActiveUsers = await userRepository.GetRecentlyActiveUserCountAsync(TimeSpan.FromMinutes(5));
        _metrics.SetActiveUsers(recentActiveUsers);

        if (activeConnections.Count == 0)
        {
            _logger.LogDebug("No active connections found for transaction sync");
            return;
        }

        _logger.LogInformation("Starting transaction sync for {ConnectionCount} active connections", activeConnections.Count());

        // Track unique users for cleanup. ConcurrentDictionary keeps this thread-safe under
        // the parallel fan-out below (HashSet.Add is not safe for concurrent writers).
        var processedUserIds = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();

        // Bound the concurrency: each connection opens its own DI scope + DbContext (and thus
        // a pooled DB connection). Fanning out unbounded across every active connection could
        // exhaust the ~40-connection Npgsql pool and starve request-path traffic. Cap the
        // number of connections synced at once well under that pool size.
        const int maxDegreeOfParallelism = 5;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(activeConnections, parallelOptions, async (connection, ct) =>
        {
            try
            {
                await SyncConnectionTransactions(connection, ct);
                processedUserIds.TryAdd(connection.UserId, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing transactions for connection {ConnectionId} ({InstitutionName})",
                    connection.Id, connection.InstitutionName);

                // Update the connection's last sync attempt and error info
                await UpdateConnectionSyncError(connection.Id, ex.Message);
            }
        });

        // After syncing, cleanup stale pending transactions for all processed users
        foreach (var userId in processedUserIds.Keys)
        {
            if (plaidService == null)
            {
                break;
            }

            try
            {
                await plaidService.CleanupStalePendingTransactionsAsync(userId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cleaning up stale pending transactions for user {UserId}", userId);
            }
        }

        _logger.LogInformation("Completed transaction sync cycle for {ConnectionCount} connections", activeConnections.Count());
    }

    /// <summary>
    /// Cloud bank feed connections that wait for a sign-in are not synced, so nothing would ever notice that the Cloud
    /// healed them (LOGIN_REPAIRED). One cheap list call per due interval asks the Cloud and puts them back.
    /// </summary>
    internal static async Task ReconcileBridgeConnectionsAsync(
        IPlaidConnectionRepository connectionRepository, IBankAggregatorRegistry registry, ILogger logger, DateTime nowUtc)
    {
        try
        {
            if (!registry.TryGet(BankProvider.Bridge, out var bridge))
            {
                return;
            }

            var due = SelectDueConnections(await connectionRepository.GetBridgeConnectionsNeedingReconcileAsync(), registry, nowUtc);
            if (due.Count > 0)
            {
                await bridge!.ReconcileConnectionsAsync(due);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not check the Cloud for connections that need attention");
        }
    }

    /// <summary>
    /// The connections to sync this cycle: those whose provider is registered and whose minimum wait
    /// since the last attempt has passed. Plaid asks for no wait, so all of its connections stay in.
    /// </summary>
    internal static List<PlaidConnection> SelectDueConnections(
        IEnumerable<PlaidConnection> connections, IBankAggregatorRegistry registry, DateTime nowUtc) =>
        connections
            .Where(c => registry.TryGet(c.Provider, out var aggregator)
                && SyncThrottle.IsDue(c, aggregator!.MinimumSyncInterval, aggregator.SyncJitter, nowUtc))
            .ToList();

    private async Task SyncConnectionTransactions(
        PlaidConnection connection,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting transaction sync for connection {ConnectionId} ({InstitutionName})",
            connection.Id, connection.InstitutionName);

        try
        {
            // Each connection runs concurrently, so it must use its OWN scope (and thus its
            // own DbContext + aggregator). Sharing a single DbContext across the parallel
            // tasks caused "a second operation was started on this context" errors.
            using var scope = _serviceScopeFactory.CreateScope();

            // Dispatch to the connection's provider (#310) — Plaid today, MX/others once registered.
            // The aggregator resolves its own auth from the connection record.
            var registry = scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>();
            var aggregator = registry.Get(connection.Provider);

            // Sync transactions since last sync (or last 7 days if never synced)
            // Use a 1-day overlap to ensure we don't miss any transactions
            var fromDate = connection.LastTransactionSyncAt?.AddDays(-1) ?? DateTime.UtcNow.AddDays(-7);
            var toDate = DateTime.UtcNow;

            // For efficiency, limit the sync window to maximum 7 days for regular syncs
            if ((toDate - fromDate).TotalDays > 7)
            {
                fromDate = toDate.AddDays(-7);
            }

            _logger.LogDebug("Syncing transactions for connection {ConnectionId} ({Provider}) from {FromDate} to {ToDate}",
                connection.Id, connection.Provider, fromDate, toDate);

            // Fetch new/updated transactions from the provider.
            var newTransactionCount = await aggregator.SyncConnectionTransactionsAsync(connection, fromDate, toDate);

            // Update the connection's last sync timestamp
            await UpdateConnectionLastSync(connection.Id, newTransactionCount);

            _logger.LogInformation("Completed transaction sync for connection {ConnectionId} - {TransactionCount} new transactions", 
                connection.Id, newTransactionCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync transactions for connection {ConnectionId}", connection.Id);
            await UpdateConnectionSyncError(connection.Id, ex.Message);
        }
    }

    private async Task UpdateConnectionLastSync(int connectionId, int transactionCount)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var connectionRepository = scope.ServiceProvider.GetRequiredService<IPlaidConnectionRepository>();
            
            var connection = await connectionRepository.GetByIdAsync(connectionId);
            if (connection != null)
            {
                var now = DateTime.UtcNow;
                connection.LastTransactionSyncAt = now;
                connection.LastSyncAt = now; // Update the field that frontend displays
                connection.LastSyncTransactionCount = transactionCount;
                connection.LastSyncError = null; // Clear any previous errors
                connection.ConsecutiveFailures = 0; // A clean sync clears the failure streak
                connection.LastSyncAttemptAt = now; // Track successful attempt
                await connectionRepository.UpdateAsync(connection);
                // Healthy again: the next outage alerts again. Conditional, so a webhook that just
                // flagged the connection keeps its email claim.
                await connectionRepository.ClearAlertEmailClaimIfActiveAsync(connectionId);
                
                _logger.LogDebug("Updated sync timestamp for connection {ConnectionId}", connectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last sync timestamp for connection {ConnectionId}", connectionId);
        }
    }

    private async Task UpdateConnectionSyncError(int connectionId, string errorMessage)
    {
        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var connection = await scope.ServiceProvider
                .GetRequiredService<IPlaidConnectionRepository>().GetByIdAsync(connectionId);
            if (connection == null)
            {
                return;
            }

            // Classify the failure: a login-required error (or too many consecutive failures)
            // moves the connection out of Active and alerts the user, so a broken connection
            // stops being re-selected for sync every cycle. Transient failures below the
            // threshold just record LastSyncError and retry next cycle. Each provider owns the
            // classification of its own errors.
            var registry = scope.ServiceProvider.GetRequiredService<IBankAggregatorRegistry>();
            await registry.Get(connection.Provider).HandleSyncFailureAsync(connectionId, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update sync error for connection {ConnectionId}", connectionId);
        }
    }
}
