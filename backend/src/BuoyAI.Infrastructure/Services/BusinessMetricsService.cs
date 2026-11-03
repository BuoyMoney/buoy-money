using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Periodically snapshots business KPIs (MRR, paying users by tier, active trials,
/// churn) from the database into the OpenTelemetry gauges on the "BuoyAI" meter.
/// Those gauges are exported to Grafana Cloud and drive the "Goals" dashboard (#811).
///
/// Read-only. The MRR math mirrors <c>AdminController.GetOverview</c> (Standard $10 +
/// Premium $15 + Admin $50; PremiumInherited family members carry no direct revenue).
/// </summary>
public class BusinessMetricsService : BackgroundService
{
    private const string JobName = "BusinessMetrics";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BusinessMetricsService> _logger;
    private readonly BuoyAiMetrics _metrics;

    public BusinessMetricsService(
        IServiceScopeFactory scopeFactory,
        ILogger<BusinessMetricsService> logger,
        BuoyAiMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Business Metrics Service started");

        // Prime once at startup so the gauges are populated before the first tick.
        await SnapshotAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SnapshotAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }

    private async Task SnapshotAsync(CancellationToken ct)
    {
        try
        {
            _metrics.RecordBackgroundJobRun(JobName);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BuoyAIDbContext>();

            var now = DateTime.UtcNow;

            var tierCounts = await db.Users
                .Where(u => u.SubscriptionTier != null && !u.IsTrialActive)
                .GroupBy(u => u.SubscriptionTier)
                .Select(g => new { Tier = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            int standard = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Standard)?.Count ?? 0;
            int premium = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Premium)?.Count ?? 0;
            int admin = tierCounts.FirstOrDefault(t => t.Tier == SubscriptionTier.Admin)?.Count ?? 0;

            int activeTrials = await db.Users.CountAsync(u => u.IsTrialActive && u.TrialEndDate > now, ct);
            int churned = await db.Users.CountAsync(
                u => u.HasUsedTrial && !u.IsTrialActive && u.SubscriptionTier == null, ct);

            int mrr = (standard * 10) + (premium * 15) + (admin * 50);

            _metrics.SetBusinessSnapshot(mrr, standard, premium, admin, activeTrials, churned);
            _metrics.RecordBackgroundJobSuccess(JobName);

            _logger.LogDebug(
                "Business metrics snapshot: MRR=${Mrr} standard={Std} premium={Prem} admin={Adm} trials={Trials} churned={Churn}",
                mrr, standard, premium, admin, activeTrials, churned);
        }
        catch (OperationCanceledException)
        {
            throw; // let ExecuteAsync handle shutdown
        }
        catch (Exception ex)
        {
            _metrics.RecordBackgroundJobFailure(JobName, ex.GetType().Name);
            _logger.LogError(ex, "Business metrics snapshot failed");
        }
    }
}
