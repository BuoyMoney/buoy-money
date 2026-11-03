using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entitlements;

/// <summary>
/// What one tier may spend on AI. The five numbers that used to be typed into four classes
/// (rate limiter, monthly budget, daily cost cap, refresh cap, usage screen).
/// </summary>
/// <param name="HourlyRequests">Requests allowed per UTC hour.</param>
/// <param name="DailyRequests">Requests allowed per UTC day.</param>
/// <param name="MonthlyCost">Dollar ceiling per calendar month (warn at 80%, block at 100%).</param>
/// <param name="DailyCost">Dollar ceiling per UTC day.</param>
/// <param name="DailyRefreshes">Insight refreshes per context per UTC day.</param>
public sealed record AiTierLimits(
    int HourlyRequests,
    int DailyRequests,
    decimal MonthlyCost,
    decimal DailyCost,
    int DailyRefreshes);

/// <summary>
/// The single source of AI caps per tier. Standard and Premium daily request counts can be
/// overridden from <c>Gemini:RateLimiting:*</c> (see the Infrastructure loader); every other number is fixed. A family member
/// on <see cref="SubscriptionTier.PremiumInherited"/> gets the Premium row.
/// </summary>
public sealed class AiLimits
{
    public AiTierLimits Standard { get; init; } = new(10, 50, 5.00m, 0.25m, 5);
    public AiTierLimits Premium { get; init; } = new(50, 200, 15.00m, 1.00m, 15);
    public AiTierLimits Admin { get; init; } = new(1000, 5000, 100.00m, 5.00m, 50);

    /// <summary>Built-in defaults, with no configuration applied.</summary>
    public static AiLimits Default { get; } = new();

    /// <summary>
    /// The row for a tier. Inherited Premium reads the Premium row. Any tier outside the
    /// three rows is an error, as it was with the old per-class dictionaries.
    /// </summary>
    public AiTierLimits For(SubscriptionTier tier) => tier.ForLimits() switch
    {
        SubscriptionTier.Standard => Standard,
        SubscriptionTier.Premium => Premium,
        SubscriptionTier.Admin => Admin,
        _ => throw new KeyNotFoundException($"No AI limits for tier {tier}"),
    };
}
