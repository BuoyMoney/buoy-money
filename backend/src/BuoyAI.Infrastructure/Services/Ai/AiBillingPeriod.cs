using BuoyAI.Core.Enums;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BuoyAI.Infrastructure.Services.Ai;

/// <summary>A half-open window [Start, End) the monthly AI figures are summed over.</summary>
public readonly record struct AiPeriod(DateTime Start, DateTime End);

/// <summary>
/// The window a user's monthly AI spend is counted over. A subscriber's window follows their billing date (the monthly
/// period their subscription renews on, which for a yearly plan is still the monthly anniversary), so the allowance resets
/// when they are billed, not on the 1st. Everyone else (no subscription, a trial, the free tier, self-host) gets the UTC
/// calendar month, which is what every user had before.
/// </summary>
public static class AiBillingPeriod
{
    public static AiPeriod CalendarMonth(DateTime now)
    {
        var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new AiPeriod(start, start.AddMonths(1));
    }

    /// <summary>The monthly window containing <paramref name="now"/> when months are counted from <paramref name="anchor"/>'s day.</summary>
    public static AiPeriod FromAnchor(DateTime anchor, DateTime now)
    {
        anchor = DateTime.SpecifyKind(anchor, DateTimeKind.Utc);
        var k = (now.Year - anchor.Year) * 12 + (now.Month - anchor.Month);
        // Step to the first anniversary after now, then the one before it is the start. AddMonths from the anchor (not
        // from the previous step) keeps a 31st-of-the-month billing date from drifting to the 28th for good.
        while (anchor.AddMonths(k) <= now) k++;
        while (anchor.AddMonths(k - 1) > now) k--;
        return new AiPeriod(anchor.AddMonths(k - 1), anchor.AddMonths(k));
    }

    public static async Task<AiPeriod> ForUserAsync(BuoyAIDbContext db, int userId, DateTime now, CancellationToken ct = default)
    {
        var anchor = await db.UserSubscriptions
            .Where(s => s.UserId == userId && (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue))
            .OrderByDescending(s => s.NextBillingDate)
            .Select(s => (DateTime?)s.NextBillingDate)
            .FirstOrDefaultAsync(ct);

        // A billing date that is missing or implausibly old can't anchor a window; fall back rather than guess.
        return anchor is { } a && a.Year >= 2020 ? FromAnchor(a, now) : CalendarMonth(now);
    }
}
