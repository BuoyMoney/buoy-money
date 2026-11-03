using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Once a day, enqueues every active user for gamification re-evaluation. This is the
/// reconciliation pass that catches time-based outcomes the event hooks can't see in the
/// moment — streak continuation/break, "N months under budget," and any award a user
/// qualified for without triggering a write path. Reuses the evaluation queue + consumer.
/// </summary>
public class GamificationDailyService : BackgroundService
{
  private const int TargetHourUtc = 9; // 09:00 UTC, after the daily budget/streak rollovers
  private const int TargetMinuteUtc = 0;

  private const string JobName = "gamification_daily";
  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IGamificationEvaluationQueue _queue;
  private readonly ILogger<GamificationDailyService> _logger;
  private readonly Telemetry.BuoyAiMetrics _metrics;

  public GamificationDailyService(
    IServiceScopeFactory scopeFactory,
    IGamificationEvaluationQueue queue,
    ILogger<GamificationDailyService> logger,
    Telemetry.BuoyAiMetrics metrics)
  {
    _scopeFactory = scopeFactory;
    _queue = queue;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await WaitUntilNextScheduledTimeAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) break;
        _metrics.RecordBackgroundJobRun(JobName);
        await EnqueueActiveUsersAsync(stoppingToken);
        _metrics.RecordBackgroundJobSuccess(JobName);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Gamification daily reconciliation failed");
        // Back off briefly so a persistent failure doesn't hot-loop.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
      }
    }
  }

  private async Task EnqueueActiveUsersAsync(CancellationToken ct)
  {
    using var scope = _scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

    var userIds = await db.Set<BuoyAI.Core.Entities.Budget>()
      .Where(b => b.IsActive)
      .Select(b => b.UserId)
      .Distinct()
      .ToListAsync(ct);

    foreach (var userId in userIds)
    {
      _queue.Enqueue(userId);
    }

    _logger.LogInformation(
      "Gamification daily reconciliation enqueued {Count} active users", userIds.Count);
  }

  private static async Task WaitUntilNextScheduledTimeAsync(CancellationToken ct)
  {
    var now = DateTime.UtcNow;
    var todayTarget = new DateTime(
      now.Year, now.Month, now.Day, TargetHourUtc, TargetMinuteUtc, 0, DateTimeKind.Utc);
    var target = now < todayTarget ? todayTarget : todayTarget.AddDays(1);
    var delay = target - now;
    if (delay > TimeSpan.Zero)
    {
      await Task.Delay(delay, ct);
    }
  }
}
