using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

public class PlaidBackgroundImportService : BackgroundService
{
    private const string JobName = "PlaidBackgroundImport";

    // Retry backoff between historical-import attempts. The job polls every 60s and bumps
    // ImportAttempts on each run, so without spacing a brief (~5 min) Plaid outage during a
    // user's initial import would burn all MaxImportAttempts (5) back-to-back and land an
    // otherwise-healthy connection in terminal Failed. Backing off exponentially
    // (2, 4, 8, 16 min...) spreads the 5 attempts across ~30+ minutes, so a short outage
    // recovers without exhausting the bounded attempt budget. Capped so it never exceeds the
    // 30-min stale-InProgress threshold for low attempt counts (avoids starving stale requeue).
    private static readonly TimeSpan BaseRetryBackoff = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxRetryBackoff = TimeSpan.FromMinutes(60);

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<PlaidBackgroundImportService> _logger;
    private readonly BuoyAiMetrics _metrics;

    // True when a connection failed/started an attempt recently enough that we should let the
    // backoff window elapse before retrying. Fresh imports (never attempted) are never backed
    // off. ImportStartedAt is stamped at the start of every attempt, so it is the effective
    // last-attempt timestamp.
    private static bool IsWithinRetryBackoff(PlaidConnection connection, DateTime nowUtc)
    {
        if (connection.ImportAttempts <= 0 || connection.ImportStartedAt == null)
            return false;

        // 2^(attempts-1) * base, capped. Shift amount is bounded to avoid overflow.
        var shift = Math.Min(connection.ImportAttempts - 1, 8);
        var backoffMinutes = Math.Min(BaseRetryBackoff.TotalMinutes * (1 << shift), MaxRetryBackoff.TotalMinutes);
        return nowUtc - connection.ImportStartedAt.Value < TimeSpan.FromMinutes(backoffMinutes);
    }

    public PlaidBackgroundImportService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<PlaidBackgroundImportService> logger,
        BuoyAiMetrics metrics)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _metrics.RecordBackgroundJobRun(JobName);
                await ProcessPendingImports(stoppingToken);
                stopwatch.Stop();
                _metrics.RecordBackgroundJobSuccess(JobName);
                _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Check every minute
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
                _logger.LogError(ex, "Error in PlaidBackgroundImportService");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Wait longer on error
            }
        }
    }

    private async Task ProcessPendingImports(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var plaidService = scope.ServiceProvider.GetRequiredService<IPlaidService>();
        var connectionRepository = scope.ServiceProvider.GetRequiredService<IPlaidConnectionRepository>();

        // Get connections that need background import (quick sync completed but full import pending)
        var connectionsNeedingImport = await connectionRepository.GetConnectionsNeedingBackgroundImport();

        foreach (var connection in connectionsNeedingImport)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Space out retries of a previously-failed/attempted import so a brief Plaid outage
            // doesn't burn the whole attempt budget in a few minutes. Fresh (never-attempted)
            // imports are not affected.
            if (IsWithinRetryBackoff(connection, DateTime.UtcNow))
            {
                _logger.LogInformation(
                    "Skipping connection {ConnectionId} this cycle: within retry backoff (attempt {Attempts}, last attempt at {LastAttempt:o})",
                    connection.Id, connection.ImportAttempts, connection.ImportStartedAt);
                continue;
            }

            try
            {
                await ProcessConnectionBackgroundImport(connection, plaidService, connectionRepository, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing background import for connection {ConnectionId}", connection.Id);
                
                // Update connection status to failed
                connection.ImportStatus = ImportStatus.Failed;
                connection.ImportMessage = $"Background import failed: {ex.Message}";
                await connectionRepository.UpdateAsync(connection);
            }
        }
    }

    private async Task ProcessConnectionBackgroundImport(
        PlaidConnection connection,
        IPlaidService plaidService,
        IPlaidConnectionRepository connectionRepository,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting background import for connection {ConnectionId}", connection.Id);

        // Update status to in progress. Bump the attempt counter up-front so a retry of a
        // Failed/stale-InProgress import is bounded (GetConnectionsNeedingBackgroundImport
        // stops requeuing once ImportAttempts hits the cap) — this prevents an infinite
        // retry loop on a genuinely broken connection while still recovering transient failures.
        connection.ImportStatus = ImportStatus.InProgress;
        connection.ImportStartedAt = DateTime.UtcNow;
        connection.ImportAttempts += 1;
        connection.ImportMessage = "Importing historical data (2 years)...";
        await connectionRepository.UpdateAsync(connection);

        // Get the encryption service to decrypt the access token
        using var scope = _serviceScopeFactory.CreateScope();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var accessToken = encryptionService.Decrypt(connection.AccessToken);

        // Import 2 years of historical data in 30-day chunks (skip first 30 days since already imported)
        var endDate = connection.QuickSyncCompletedAt?.AddDays(-30) ?? DateTime.UtcNow.AddDays(-30);
        var startDate = DateTime.UtcNow.AddYears(-2);
        
        var totalDays = (endDate - startDate).Days;
        
        // Create date ranges for parallel processing (30-day chunks)
        var dateRanges = new List<(DateTime start, DateTime end)>();
        var currentEndDate = endDate;
        
        while (currentEndDate > startDate)
        {
            var currentStartDate = currentEndDate.AddDays(-30);
            if (currentStartDate < startDate)
                currentStartDate = startDate;
                
            dateRanges.Add((currentStartDate, currentEndDate));
            currentEndDate = currentStartDate;
        }

        _logger.LogInformation("Processing {ChunkCount} date ranges in parallel for connection {ConnectionId}", 
            dateRanges.Count, connection.Id);

        var processedDays = 0;

        // Process chunks sequentially to avoid DbContext concurrency issues
        // This is simpler and more reliable than parallel processing
        foreach (var dateRange in dateRanges)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var (chunkStart, chunkEnd) = dateRange;
                
                // Import this 30-day chunk using the access token directly
                await plaidService.ImportHistoricalDataAsync(accessToken, chunkStart, chunkEnd);

                // Update progress
                processedDays += (chunkEnd - chunkStart).Days;
                var currentProgress = Math.Min(100, (int)((double)processedDays / totalDays * 100));

                // Update progress in database using the same scope to avoid DbContext conflicts
                connection.ImportProgress = currentProgress;
                connection.ImportMessage = $"Importing historical data... {currentProgress}% complete";
                await connectionRepository.UpdateAsync(connection);

                _logger.LogInformation("Imported {Days} days ({StartDate} to {EndDate}) for connection {ConnectionId}, total progress: {Progress}%", 
                    (chunkEnd - chunkStart).Days, chunkStart.ToString("yyyy-MM-dd"), chunkEnd.ToString("yyyy-MM-dd"), 
                    connection.Id, currentProgress);

                // Rate limiting between chunks
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importing chunk {StartDate} to {EndDate} for connection {ConnectionId}", 
                    dateRange.start, dateRange.end, connection.Id);
                throw; // Re-throw to be handled by the main try-catch
            }
        }

        try
        {
            // Mark as completed
            connection.ImportStatus = ImportStatus.Completed;
            connection.ImportProgress = 100;
            connection.ImportCompletedAt = DateTime.UtcNow;
            connection.ImportMessage = "Historical data import completed successfully";
            await connectionRepository.UpdateAsync(connection);

            _logger.LogInformation("Completed background import for connection {ConnectionId}", connection.Id);

            // Activation nudge: the data is in; if the user still has no budget, tell them the
            // one thing to do next (once per user). Best-effort inside the service itself.
            var nudge = scope.ServiceProvider.GetRequiredService<IFirstBudgetNudgeService>();
            await nudge.NudgeIfNoBudgetAsync(connection.UserId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during import for connection {ConnectionId}", connection.Id);
            
            // Mark as failed
            connection.ImportStatus = ImportStatus.Failed;
            connection.ImportMessage = $"Import failed: {ex.Message}";
            await connectionRepository.UpdateAsync(connection);
            
            throw;
        }
    }
}
