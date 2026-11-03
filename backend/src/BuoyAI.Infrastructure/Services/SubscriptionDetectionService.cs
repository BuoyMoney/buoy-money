using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Background service that runs weekly on Sunday at 4:00 AM UTC to detect new subscriptions for all users
/// and creates notifications for those with pending subscriptions to review.
/// Also provides on-demand detection triggered after Plaid sync with a 7-day cooldown per user.
/// </summary>
public class SubscriptionDetectionService : BackgroundService
{
    private const string JobName = "SubscriptionDetection";
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<SubscriptionDetectionService> _logger;
    private readonly BuoyAiMetrics _metrics;
    
    // Run every Sunday at 4:00 AM UTC
    private readonly DayOfWeek _targetDayOfWeek = DayOfWeek.Sunday;
    private readonly int _targetHour = 4;
    
    // On-demand detection cooldown (7 days per user)
    public static readonly TimeSpan OnDemandCooldown = TimeSpan.FromDays(7);

    public SubscriptionDetectionService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<SubscriptionDetectionService> logger,
        BuoyAiMetrics metrics)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Subscription Detection Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until the next scheduled run time
                await WaitUntilNextScheduledTimeAsync(stoppingToken);
                
                if (stoppingToken.IsCancellationRequested)
                    break;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                _metrics.RecordBackgroundJobRun(JobName);
                await RunSubscriptionDetectionAsync(stoppingToken);
                stopwatch.Stop();
                _metrics.RecordBackgroundJobSuccess(JobName);
                _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);

                // After running, wait at least 1 day before checking schedule again
                // This prevents double-runs if the job completes quickly
                await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Subscription Detection Service cancelled");
                break;
            }
            catch (Exception ex)
            {
                _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
                _logger.LogError(ex, "Error in Subscription Detection Service");
                // Wait 1 hour before retrying on error
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("Subscription Detection Service stopped");
    }

    private async Task WaitUntilNextScheduledTimeAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        DateTime targetTime;
        
        // Calculate next Sunday at 4:00 AM UTC
        int daysUntilSunday = ((int)_targetDayOfWeek - (int)now.DayOfWeek + 7) % 7;
        
        // If today is Sunday and we haven't passed the target hour, run today
        if (daysUntilSunday == 0 && now.Hour >= _targetHour)
        {
            // Already past target time this Sunday, wait for next Sunday
            daysUntilSunday = 7;
        }
        else if (daysUntilSunday == 0 && now.Hour < _targetHour)
        {
            // Today is Sunday and we haven't reached the target hour yet
            daysUntilSunday = 0;
        }
        
        targetTime = now.Date.AddDays(daysUntilSunday).AddHours(_targetHour);

        var delay = targetTime - now;
        
        _logger.LogInformation("Subscription Detection Service scheduled for {TargetTime} UTC (in {Days:F1} days)", 
            targetTime, delay.TotalDays);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task RunSubscriptionDetectionAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting weekly subscription detection for all users");
        
        using var scope = _serviceScopeFactory.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();
        var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
        
        var usersProcessed = 0;
        var usersWithDetections = 0;
        var totalDetected = 0;

        try
        {
            // Get all active users - we need to add a method for this
            // For now, we'll use a simple approach through the database context
            var dbContext = scope.ServiceProvider.GetRequiredService<BuoyAI.Infrastructure.Data.BuoyAIDbContext>();
            var activeUserIds = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .ToListAsync(dbContext.Users
                    .Where(u => !u.IsDeleted)
                    .Select(u => u.Id), cancellationToken);

            _logger.LogInformation("Processing {Count} active users for subscription detection", activeUserIds.Count);

            // Auto-expire stale suggestions: dismiss any pending suggestions older than 90 days
            var suggestedRepo = scope.ServiceProvider.GetRequiredService<ISuggestedSubscriptionRepository>();
            var expiredCount = await suggestedRepo.ExpireStaleSuggestionsAsync(TimeSpan.FromDays(90));
            if (expiredCount > 0)
            {
                _logger.LogInformation("Auto-expired {Count} stale pending suggestions (>90 days old)", expiredCount);
            }

            foreach (var userId in activeUserIds)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    // Run detection with stricter thresholds for weekly background runs
                    // (on-demand "Detect" button uses defaults: MinOccurrences=2, confidence>=0.6)
                    var weeklyRequest = new BuoyAI.Core.DTOs.Subscription.SubscriptionDetectionRequest
                    {
                        MinOccurrences = 3, // Require 3+ transactions (vs 2 for on-demand)
                        AmountTolerance = 5.00m,
                        IncludeAIAnalysis = true
                    };
                    var result = await subscriptionService.DetectSubscriptionsAsync(userId, weeklyRequest);
                    usersProcessed++;

                    if (result.TotalDetected > 0)
                    {
                        usersWithDetections++;
                        totalDetected += result.TotalDetected;

                        // Create notification for the user (with throttling - max 3/week handled by NotificationService)
                        await notificationService.CreateSubscriptionDetectionNotificationAsync(
                            userId, result.TotalDetected);
                        
                        _logger.LogDebug("User {UserId}: {Count} subscriptions detected", 
                            userId, result.TotalDetected);
                    }

                    // Small delay between users to avoid overwhelming the system
                    await Task.Delay(100, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing subscription detection for user {UserId}", userId);
                    // Continue with other users
                }
            }

            _logger.LogInformation(
                "Weekly subscription detection complete: {UsersProcessed} users processed, {UsersWithDetections} with new detections, {TotalDetected} total subscriptions detected",
                usersProcessed, usersWithDetections, totalDetected);
            
            _metrics.RecordSubscriptionsDetected(totalDetected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during weekly subscription detection batch");
            throw;
        }
    }
}
