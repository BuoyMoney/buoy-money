using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Entities;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Background service that enforces data retention policies by automatically
/// deleting data that has exceeded its retention period
/// </summary>
public class DataRetentionService : BackgroundService
{
    private const string JobName = "data_retention";
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<DataRetentionService> _logger;
    private readonly BuoyAiMetrics _metrics;

    public DataRetentionService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<DataRetentionService> logger,
        BuoyAiMetrics metrics)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Data Retention Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until 2 AM UTC for the first run, then every 24 hours
                await WaitUntilScheduledTime(stoppingToken);
                
                if (stoppingToken.IsCancellationRequested)
                    break;

                await PerformDataRetentionCleanup(stoppingToken);

                // NOTE: do NOT add a fixed 24h delay here. WaitUntilScheduledTime already gates the
                // loop to the next 02:00 UTC. An extra Task.Delay(24h) pushed each run ~24h past the
                // just-completed run, so the next WaitUntilScheduledTime scheduled the day AFTER —
                // making retention run every ~48h instead of daily and letting PII linger.
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Data Retention Service cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Data Retention Service");
                // Wait 1 hour before retrying on error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("Data Retention Service stopped");
    }

    private async Task WaitUntilScheduledTime(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var targetTime = new DateTime(now.Year, now.Month, now.Day, 2, 0, 0, DateTimeKind.Utc);
        
        // If it's already past 2 AM today, schedule for 2 AM tomorrow
        if (now >= targetTime)
        {
            targetTime = targetTime.AddDays(1);
        }

        var delay = targetTime - now;
        _logger.LogInformation("Data Retention Service scheduled to run at {TargetTime} UTC (in {Delay})", 
            targetTime, delay);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task PerformDataRetentionCleanup(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting data retention cleanup at {Time}", DateTime.UtcNow);

        using var scope = _serviceScopeFactory.CreateScope();
        var retentionManager = scope.ServiceProvider.GetRequiredService<IDataRetentionManager>();

        // Emit job metrics so a persistently-failing nightly compliance job is visible in
        // Grafana/alerting instead of only in container logs nobody tails.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _metrics.RecordBackgroundJobRun(JobName);
        try
        {
            var retentionSummary = new DataRetentionSummary();

            // 1. Clean up closed user accounts (30 days)
            retentionSummary.DeletedClosedAccounts = await retentionManager.CleanupClosedUserAccountsAsync(cancellationToken);

            // 2. Clean up old security logs (90 days)
            retentionSummary.DeletedSecurityLogs = await retentionManager.CleanupSecurityLogsAsync(cancellationToken);

            // 3. Clean up old error logs (30 days)
            retentionSummary.DeletedErrorLogs = await retentionManager.CleanupErrorLogsAsync(cancellationToken);

            // 4. Clean up old AI insights (1 year)
            retentionSummary.DeletedAIInsights = await retentionManager.CleanupOldAIInsightsAsync(cancellationToken);

            // 5. Clean up old AI refresh usage data (90 days)
            retentionSummary.DeletedAIRefreshData = await retentionManager.CleanupAIRefreshUsageAsync(cancellationToken);

            // 6. Clean up disconnected account transaction data (immediate)
            retentionSummary.DeletedDisconnectedTransactions = await retentionManager.CleanupDisconnectedAccountDataAsync(cancellationToken);

            // 7. Clean up expired Plaid tokens (immediate on expiry)
            retentionSummary.DeletedExpiredTokens = await retentionManager.CleanupExpiredPlaidTokensAsync(cancellationToken);

            // 8. Clean up expired passkey challenges (1 hour)
            retentionSummary.DeletedPasskeyChallenges = await retentionManager.CleanupExpiredPasskeyChallengesAsync(cancellationToken);

            // 9. Revoke Plaid items for churned accounts (stops ongoing Transactions billing)
            retentionSummary.RemovedChurnedPlaidItems = await retentionManager.CleanupChurnedAccountPlaidItemsAsync(cancellationToken);

            // Log summary
            await LogRetentionSummary(retentionSummary);

            stopwatch.Stop();
            _metrics.RecordBackgroundJobSuccess(JobName);
            _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
            _logger.LogInformation("Data retention cleanup completed successfully at {Time}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
            _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
            _logger.LogError(ex, "Error during data retention cleanup");
            throw;
        }
    }

    private async Task LogRetentionSummary(DataRetentionSummary summary)
    {
        _logger.LogInformation("Data Retention Summary: " +
            "Closed Accounts: {ClosedAccounts}, " +
            "Security Logs: {SecurityLogs}, " +
            "Error Logs: {ErrorLogs}, " +
            "AI Insights: {AIInsights}, " +
            "AI Refresh Data: {AIRefreshData}, " +
            "Disconnected Transactions: {DisconnectedTransactions}, " +
            "Expired Tokens: {ExpiredTokens}, " +
            "Passkey Challenges: {PasskeyChallenges}, " +
            "Churned Plaid Items Revoked: {ChurnedPlaidItems}",
            summary.DeletedClosedAccounts,
            summary.DeletedSecurityLogs,
            summary.DeletedErrorLogs,
            summary.DeletedAIInsights,
            summary.DeletedAIRefreshData,
            summary.DeletedDisconnectedTransactions,
            summary.DeletedExpiredTokens,
            summary.DeletedPasskeyChallenges,
            summary.RemovedChurnedPlaidItems);

        // Store audit record
        using var scope = _serviceScopeFactory.CreateScope();
        var auditService = scope.ServiceProvider.GetRequiredService<IDataRetentionAuditService>();
        await auditService.LogRetentionExecutionAsync(summary);
    }

    public class DataRetentionSummary
    {
        public int DeletedClosedAccounts { get; set; }
        public int DeletedSecurityLogs { get; set; }
        public int DeletedErrorLogs { get; set; }
        public int DeletedAIInsights { get; set; }
        public int DeletedAIRefreshData { get; set; }
        public int DeletedDisconnectedTransactions { get; set; }
        public int DeletedExpiredTokens { get; set; }
        public int DeletedPasskeyChallenges { get; set; }
        public int RemovedChurnedPlaidItems { get; set; }
        public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
    }
}
