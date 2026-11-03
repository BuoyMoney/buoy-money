using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Monthly "Money Wrapped" delivery. Wakes daily at 15:00 UTC; on the first
/// three days of a month it offers last month's recap to every active user who
/// had activity, deduped per user per cycle. On Jan 1-3 it also delivers the
/// yearly recap for the year just ended. The notification flows through
/// INotificationService, so the push copy comes for free; the ActionUrl deep-
/// links straight into the story page.
///
/// Three-day window (not just day 1) so users whose backend was down or who
/// signed up on the 2nd still get their recap — the dedupe check keeps it to
/// exactly one per user per month.
/// </summary>
public class WrappedRecapService : BackgroundService
{
  private const string JobName = "WrappedRecap";
  internal const string MonthlyType = "money_wrapped";
  internal const string YearlyType = "money_wrapped_yearly";

  private readonly IServiceScopeFactory _serviceScopeFactory;
  private readonly ILogger<WrappedRecapService> _logger;
  private readonly BuoyAiMetrics _metrics;

  private readonly int _targetHour = 15;

  public WrappedRecapService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WrappedRecapService> logger,
    BuoyAiMetrics metrics)
  {
    _serviceScopeFactory = serviceScopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Wrapped Recap Service started");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await WaitUntilNextScheduledTimeAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
          break;

        if (DateTime.UtcNow.Day <= 3)
        {
          var stopwatch = System.Diagnostics.Stopwatch.StartNew();
          _metrics.RecordBackgroundJobRun(JobName);
          await RunRecapsAsync(stoppingToken);
          stopwatch.Stop();
          _metrics.RecordBackgroundJobSuccess(JobName);
          _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);
        }

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
        _logger.LogError(ex, "Error in Wrapped Recap Service");
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
      }
    }

    _logger.LogInformation("Wrapped Recap Service stopped");
  }

  private async Task WaitUntilNextScheduledTimeAsync(CancellationToken cancellationToken)
  {
    var now = DateTime.UtcNow;
    var targetTime = new DateTime(now.Year, now.Month, now.Day, _targetHour, 0, 0, DateTimeKind.Utc);
    if (now >= targetTime)
    {
      targetTime = targetTime.AddDays(1);
    }
    await Task.Delay(targetTime - now, cancellationToken);
  }

  internal async Task RunRecapsAsync(CancellationToken cancellationToken)
  {
    List<int> userIds;
    using (var enumerationScope = _serviceScopeFactory.CreateScope())
    {
      var context = enumerationScope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();
      userIds = await context.Set<Budget>()
          .Where(b => b.IsActive)
          .Select(b => b.UserId)
          .Distinct()
          .ToListAsync(cancellationToken);
    }

    var now = DateTime.UtcNow;
    var lastMonth = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    var monthName = lastMonth.ToString("MMMM", CultureInfo.GetCultureInfo("en-US"));
    var isJanuary = now.Month == 1;

    var sent = 0;
    foreach (var userId in userIds)
    {
      if (cancellationToken.IsCancellationRequested) break;
      try
      {
        // Fresh scope per user: the recap hydrates a month (or year) of
        // transactions with heavy Includes, and one shared DbContext would
        // keep every user's tracked graph alive for the entire fleet run.
        using var scope = _serviceScopeFactory.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var notificationRepository = scope.ServiceProvider.GetRequiredService<INotificationRepository>();
        var wrapped = scope.ServiceProvider.GetRequiredService<IWrappedService>();
        var garnish = scope.ServiceProvider.GetRequiredService<IWrappedGarnishService>();
        var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

        // One per user per cycle — restart-safe dedupe on the notification type.
        var already = await notificationRepository.GetNotificationCountByTypeAsync(
            userId, MonthlyType, lastMonth.AddMonths(1));
        if (already == 0)
        {
          // Only offer a recap when there is one (no data -> null).
          var recap = await wrapped.GetMonthlyAsync(userId, lastMonth.Year, lastMonth.Month);
          if (recap != null)
          {
            // Pin this month's bonus cards (AI garnish, rotation fallback) so
            // the story is identical on every open and the read path never
            // touches a model. Garnish only with AI consent (5.1.2(i)); without
            // it the read path serves the deterministic rotation, no cache needed.
            if (await HasAiConsentAsync(db, userId))
            {
              await CacheBonusCardsAsync(db, garnish, wrapped, userId,
                  lastMonth.Year, lastMonth.Month);
            }
            await notifications.CreateNotificationAsync(userId, new CreateNotificationRequest
            {
              Type = MonthlyType,
              Title = $"Your {monthName} Wrapped is ready 🎁",
              Message = recap.ZeroSpendDays > 0
                  ? $"{recap.ZeroSpendDays} zero-spend days and more — see your month in 30 seconds."
                  : "Your month in 30 seconds — spending, wins, and where it all went.",
              ActionUrl = $"/wrapped/{lastMonth.Year}/{lastMonth.Month}",
              ActionText = "Open",
            });
            sent++;
          }
        }

        if (isJanuary)
        {
          var yearlyAlready = await notificationRepository.GetNotificationCountByTypeAsync(
              userId, YearlyType, new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc));
          if (yearlyAlready == 0)
          {
            var yearly = await wrapped.GetYearlyAsync(userId, now.Year - 1);
            if (yearly != null)
            {
              if (await HasAiConsentAsync(db, userId))
              {
                await CacheBonusCardsAsync(db, garnish, wrapped, userId, now.Year - 1, 0);
              }
              await notifications.CreateNotificationAsync(userId, new CreateNotificationRequest
              {
                Type = YearlyType,
                Title = $"Your {now.Year - 1} Wrapped 🎉",
                Message = "A full year of your money — the big picture, the wins, the badges.",
                ActionUrl = $"/wrapped/yearly/{now.Year - 1}",
                ActionText = "Open",
              });
              sent++;
            }
          }
        }
      }
      catch (Exception ex)
      {
        // One user's failure must not starve the rest of the fleet.
        _logger.LogWarning(ex, "Wrapped recap failed for user {UserId}", userId);
      }
    }

    _logger.LogInformation("Wrapped recap run complete: {Sent} notifications", sent);
  }

  private static Task<bool> HasAiConsentAsync(BuoyAIDbContext db, int userId) =>
    db.Users.AsNoTracking().Where(u => u.Id == userId).Where(AiConsent.IsCurrentExpr).AnyAsync();

  private static async Task CacheBonusCardsAsync(
    BuoyAIDbContext db, IWrappedGarnishService garnish, IWrappedService wrapped,
    int userId, int year, int month)
  {
    var existing = await db.WrappedBonusCardCaches.FirstOrDefaultAsync(
        c => c.UserId == userId && c.Year == year && c.Month == month);
    if (existing != null) return; // pinned once, stable forever

    var facts = await wrapped.GetFactsAsync(userId, year, month);
    if (facts.Count == 0) return;
    using var usageScope = BuoyAI.Infrastructure.Services.Ai.AiUsageScope.ForUser(userId);
    var cards = await garnish.GenerateAsync(facts);
    if (cards.Count == 0) return;

    db.WrappedBonusCardCaches.Add(new WrappedBonusCardCache
    {
      UserId = userId,
      Year = year,
      Month = month,
      CardsJson = System.Text.Json.JsonSerializer.Serialize(cards),
      GeneratedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();
  }
}
