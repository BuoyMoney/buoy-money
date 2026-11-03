using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Daily job (14:00 UTC) that reminds users about subscriptions charging in exactly two
/// days. One digest notification per user per run — grouping avoids a burst of pushes when
/// several bills land on the same day — and the two-day lead means each bill is reminded
/// exactly once per cycle. The notification flows through INotificationService, so the
/// push copy (gated by the BillReminders preference) comes for free.
/// </summary>
public class BillReminderService : BackgroundService
{
  private const string JobName = "BillReminder";
  private const string NotificationType = "bill_reminder";

  private readonly IServiceScopeFactory _serviceScopeFactory;
  private readonly ILogger<BillReminderService> _logger;
  private readonly BuoyAiMetrics _metrics;

  // Run daily at 14:00 UTC — 9-10 AM US Eastern/Central, a reasonable reminder hour for
  // the current user base; per-user local scheduling can come later if needed.
  private readonly int _targetHour = 14;

  public BillReminderService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<BillReminderService> logger,
    BuoyAiMetrics metrics)
  {
    _serviceScopeFactory = serviceScopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Bill Reminder Service started");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await WaitUntilNextScheduledTimeAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
          break;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _metrics.RecordBackgroundJobRun(JobName);
        await RunRemindersAsync(stoppingToken);
        stopwatch.Stop();
        _metrics.RecordBackgroundJobSuccess(JobName);
        _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);

        // Guard against double-runs within the same scheduled slot.
        await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Error in Bill Reminder Service");
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
      }
    }

    _logger.LogInformation("Bill Reminder Service stopped");
  }

  private async Task WaitUntilNextScheduledTimeAsync(CancellationToken cancellationToken)
  {
    var now = DateTime.UtcNow;
    var targetTime = new DateTime(now.Year, now.Month, now.Day, _targetHour, 0, 0, DateTimeKind.Utc);
    if (now >= targetTime)
    {
      targetTime = targetTime.AddDays(1);
    }

    _logger.LogInformation("Bill reminders scheduled to run at {TargetTime}", targetTime);
    await Task.Delay(targetTime - now, cancellationToken);
  }

  internal async Task RunRemindersAsync(CancellationToken cancellationToken)
  {
    using var scope = _serviceScopeFactory.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
    var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
    var notificationRepository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

    var windowStart = DateTime.UtcNow.Date.AddDays(2);
    var windowEnd = windowStart.AddDays(1);

    var upcoming = await context.Subscriptions
      .AsNoTracking()
      .Where(s => s.Status == SubscriptionStatus.Active
        && s.NextDueDate >= windowStart
        && s.NextDueDate < windowEnd)
      .OrderBy(s => s.UserId)
      .ToListAsync(cancellationToken);

    // Each user's reminder is written in their own primary currency (#941).
    var reminderUserIds = upcoming.Select(s => s.UserId).Distinct().ToList();
    var currencyByUser = await context.Users
      .AsNoTracking()
      .Where(u => reminderUserIds.Contains(u.Id))
      .ToDictionaryAsync(u => u.Id, u => u.Currency, cancellationToken);

    var usersReminded = 0;
    foreach (var group in upcoming.GroupBy(s => s.UserId))
    {
      cancellationToken.ThrowIfCancellationRequested();

      // Dedupe: one bill digest per user per day, restart-safe.
      var recentCount = await notificationRepository.GetNotificationCountByTypeAsync(
        group.Key, NotificationType, DateTime.UtcNow.AddHours(-20));
      if (recentCount > 0)
      {
        continue;
      }

      var bills = group.OrderByDescending(s => s.Amount).ToList();
      var total = bills.Sum(s => s.Amount);

      string title;
      string message;
      if (bills.Count == 1)
      {
        var bill = bills[0];
        title = $"Upcoming bill: {bill.Name}";
        message = $"{bill.Name} ({CurrencyCodes.Format(bill.Amount, currencyByUser.GetValueOrDefault(group.Key))}) is due in 2 days, " +
          $"on {bill.NextDueDate:MMMM d}.";
      }
      else
      {
        var names = string.Join(", ", bills.Take(3).Select(s => s.Name));
        if (bills.Count > 3)
        {
          names += $" and {bills.Count - 3} more";
        }
        title = $"{bills.Count} bills due in 2 days";
        message = $"{names} — {CurrencyCodes.Format(total, currencyByUser.GetValueOrDefault(group.Key))} total, due on {windowStart:MMMM d}.";
      }

      await notificationService.CreateNotificationAsync(group.Key, new CreateNotificationRequest
      {
        Type = NotificationType,
        Title = title,
        Message = message,
        ActionUrl = "/subscriptions",
        ActionText = "View Bills"
      });
      usersReminded++;
    }

    _logger.LogInformation(
      "Bill reminder run complete: {Users} users reminded about {Bills} upcoming bills",
      usersReminded, upcoming.Count);
  }
}
