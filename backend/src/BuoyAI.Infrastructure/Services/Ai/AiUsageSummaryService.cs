using BuoyAI.Core.DTOs.AI;
using BuoyAI.Core.Exceptions;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Configuration;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>
/// The numbers behind the AI usage screen. With BuoyAI Cloud as the provider the allowance lives in the gateway, so this
/// asks it (one allowance for the whole server). Otherwise it reads this server's own usage rows over the user's billing
/// period: on hosted against the tier's monthly dollar figure, on self-host as plain statistics with no cap.
/// </summary>
public sealed class AiUsageSummaryService : IAiUsageSummaryService
{
    private readonly BuoyAIDbContext _db;
    private readonly IEntitlementService _entitlements;
    private readonly EditionInfo _edition;
    private readonly CloudAiGatewayClient? _cloud;

    public AiUsageSummaryService(BuoyAIDbContext db, IEntitlementService entitlements, EditionInfo edition, CloudAiGatewayClient? cloud = null)
    {
        _db = db;
        _entitlements = entitlements;
        _edition = edition;
        _cloud = cloud;
    }

    public async Task<AiUsageSummary> GetAsync(int userId, CancellationToken cancellationToken = default)
    {
        if (_cloud != null) return await _cloud.GetUsageAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var period = await AiBillingPeriod.ForUserAsync(_db, userId, now, cancellationToken);
        var rows = await _db.AIUsageRecords
            .Where(r => r.UserId == userId && r.Timestamp >= period.Start && r.Timestamp < period.End)
            .GroupBy(r => r.RequestContext ?? "other")
            .Select(g => new { Feature = g.Key, Calls = g.Count(), Cost = g.Sum(r => r.EstimatedCost) })
            .ToListAsync(cancellationToken);

        var byFeature = rows
            .Select(r => new AiFeatureUsage(r.Feature, r.Calls, ToMicros(r.Cost)))
            .OrderByDescending(f => f.CostMicros)
            .ThenBy(f => f.Feature)
            .ToList();
        var used = byFeature.Sum(f => f.CostMicros);
        var projected = Project(used, period, now);

        if (_edition.IsSelfHost)
        {
            // Nothing caps AI on a self-hosted server that runs its own model or key: spend is a statistic.
            return new AiUsageSummary("local", true, period.Start, period.End, 0, used, 0, "ok", false, AiPaygStatus.NotAvailable, byFeature, projected);
        }

        var tier = await _entitlements.GetTierOrStandardAsync(userId);
        var allowance = ToMicros(_entitlements.GetAiLimits(tier).MonthlyCost);
        var percent = allowance <= 0 ? 100 : (int)Math.Min(int.MaxValue, used * 100 / allowance);
        var state = used >= allowance ? "over" : percent >= 80 ? "warning" : "ok";
        // The hosted monthly dollar figure is shown and warned on, not enforced: requests are gated by the hourly and daily counts.
        return new AiUsageSummary("local", false, period.Start, period.End, allowance, used, percent, state, false, AiPaygStatus.NotAvailable, byFeature, projected);
    }

    public async Task<AiUsageSummary> SetPaygAsync(int userId, bool enabled, long capMicros, CancellationToken cancellationToken = default)
    {
        if (_cloud == null)
            throw new DomainValidationException("Pay-as-you-go is only available when this server's AI comes from BuoyAI Cloud.");
        return await _cloud.SetPaygAsync(enabled, capMicros, cancellationToken);
    }

    private static long ToMicros(decimal usd) => (long)Math.Round(usd * 1_000_000m, MidpointRounding.AwayFromZero);

    // Under a day of data is too noisy to extrapolate.
    private static long Project(long used, AiPeriod period, DateTime now)
    {
        var elapsed = now - period.Start;
        var total = period.End - period.Start;
        if (elapsed < TimeSpan.FromDays(1) || total <= TimeSpan.Zero) return used;
        return (long)Math.Min(long.MaxValue / 2, used * (decimal)(total.TotalSeconds / elapsed.TotalSeconds));
    }
}
