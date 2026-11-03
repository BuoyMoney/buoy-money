using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Nightly (05:00 UTC) aggregate job computing the percentile cutoffs behind
/// Wrapped's "where you stand" card. Stores ONLY decile cutoffs + cohort size
/// (never per-user values); the card itself is additionally gated client-side
/// of this table on WrappedService.MinPercentileCohort so a small population
/// can't be ranked or deanonymized.
///
/// Metrics are last-full-month approximations, deliberately simple: this is a
/// story ranking, not accounting. savings_rate uses income-category credits;
/// zero_spend_days excludes transfers/card payments; longest_streak reads the
/// UserStreaks high-water marks.
/// </summary>
public class WrappedStatsService : BackgroundService
{
  private const string JobName = "WrappedStats";
  private static readonly TimeSpan Freshness = TimeSpan.FromHours(20);

  private readonly IServiceScopeFactory _serviceScopeFactory;
  private readonly ILogger<WrappedStatsService> _logger;
  private readonly BuoyAiMetrics _metrics;

  private readonly int _targetHour = 5;

  public WrappedStatsService(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<WrappedStatsService> logger,
    BuoyAiMetrics metrics)
  {
    _serviceScopeFactory = serviceScopeFactory;
    _logger = logger;
    _metrics = metrics;
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    _logger.LogInformation("Wrapped Stats Service started");
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await WaitUntilNextScheduledTimeAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) break;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _metrics.RecordBackgroundJobRun(JobName);
        await RunAsync(stoppingToken);
        stopwatch.Stop();
        _metrics.RecordBackgroundJobSuccess(JobName);
        _metrics.RecordBackgroundJobDuration(JobName, stopwatch.Elapsed.TotalSeconds);

        await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
      }
      catch (OperationCanceledException)
      {
        break;
      }
      catch (Exception ex)
      {
        _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
        _logger.LogError(ex, "Error in Wrapped Stats Service");
        await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
      }
    }
    _logger.LogInformation("Wrapped Stats Service stopped");
  }

  private async Task WaitUntilNextScheduledTimeAsync(CancellationToken cancellationToken)
  {
    var now = DateTime.UtcNow;
    var target = new DateTime(now.Year, now.Month, now.Day, _targetHour, 0, 0, DateTimeKind.Utc);
    if (now >= target) target = target.AddDays(1);
    await Task.Delay(target - now, cancellationToken);
  }

  internal async Task RunAsync(CancellationToken cancellationToken)
  {
    using var scope = _serviceScopeFactory.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

    // Short-circuit if a recent run already computed the cutoffs.
    var newest = await context.WrappedStatCutoffs
        .OrderByDescending(c => c.ComputedAt)
        .Select(c => (DateTime?)c.ComputedAt)
        .FirstOrDefaultAsync(cancellationToken);
    if (newest != null && DateTime.UtcNow - newest < Freshness) return;

    var now = DateTime.UtcNow;
    var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1);
    var monthEnd = monthStart.AddMonths(1);
    var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

    var userIds = await context.Set<Budget>()
        .Where(b => b.IsActive)
        .Select(b => b.UserId)
        .Distinct()
        .ToListAsync(cancellationToken);

    var savingsRates = new List<double>();
    var zeroSpendCounts = new List<double>();

    foreach (var userId in userIds)
    {
      if (cancellationToken.IsCancellationRequested) return;

      // Lightweight projected query — no Includes, no tracking.
      var rows = await context.Transactions.AsNoTracking()
          .Where(t => t.Account.UserId == userId && !t.IsPending
              && t.Date >= monthStart && t.Date < monthEnd)
          .Select(t => new { t.Type, t.Amount, t.CategoryId, t.Date, t.SavingsGoalId })
          .ToListAsync(cancellationToken);
      if (rows.Count == 0) continue; // no activity, not part of the cohort

      var income = rows
          .Where(r => r.Type == TransactionType.Credit
              && TransactionAmountExtensions.IsIncomeCategoryId(r.CategoryId ?? 0))
          .Sum(r => Math.Abs(r.Amount));
      var saved = rows
          .Where(r => r.SavingsGoalId != null)
          .Sum(r => Math.Abs(r.Amount));
      if (income > 0) savingsRates.Add((double)(saved / income));

      var spendDays = rows
          .Where(r => r.Type == TransactionType.Debit
              && !TransactionAmountExtensions.IsNonBudgetableCategoryId(r.CategoryId ?? 0))
          .Select(r => r.Date.Date)
          .Distinct()
          .Count();
      zeroSpendCounts.Add(daysInMonth - spendDays);
    }

    var streakBests = await context.Set<UserStreak>().AsNoTracking()
        .GroupBy(s => s.UserId)
        .Select(g => (double)g.Max(s => s.BestCount))
        .ToListAsync(cancellationToken);

    await UpsertAsync(context, "savings_rate", savingsRates);
    await UpsertAsync(context, "zero_spend_days", zeroSpendCounts);
    await UpsertAsync(context, "longest_streak", streakBests);
    await context.SaveChangesAsync(cancellationToken);

    _logger.LogInformation(
        "Wrapped stats computed: cohorts savings={S}, zero-spend={Z}, streak={K}",
        savingsRates.Count, zeroSpendCounts.Count, streakBests.Count);
  }

  private static async Task UpsertAsync(
      BuoyAIDbContext context, string metricKey, List<double> values)
  {
    values.Sort();
    var deciles = new double[9];
    if (values.Count > 0)
    {
      for (var i = 1; i <= 9; i++)
      {
        var idx = (int)Math.Clamp(
            Math.Round(values.Count * (i / 10.0)) - 1, 0, values.Count - 1);
        deciles[i - 1] = values[idx];
      }
    }

    var row = await context.WrappedStatCutoffs
        .FirstOrDefaultAsync(c => c.MetricKey == metricKey);
    if (row == null)
    {
      row = new WrappedStatCutoff { MetricKey = metricKey };
      context.WrappedStatCutoffs.Add(row);
    }
    row.CutoffsJson = JsonSerializer.Serialize(deciles);
    row.Cohort = values.Count;
    row.ComputedAt = DateTime.UtcNow;
  }
}
