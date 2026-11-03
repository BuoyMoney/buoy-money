namespace BuoyAI.Core.DTOs.Billing;

/// <summary>
/// A discount currently applied to a subscription, shaped for display.
///
/// Read from Stripe rather than stored: Stripe owns the discount lifecycle (it expires on
/// its own schedule and vanishes when spent), so a local copy would be a second source of
/// truth that goes stale silently. Same reasoning as subscription status itself.
/// </summary>
public class SubscriptionDiscountDto
{
    /// <summary>The customer-facing code the user redeemed, e.g. "WELCOMEBACK".</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Percent off, when the coupon is percentage-based. Null for amount-based.</summary>
    public decimal? PercentOff { get; set; }

    /// <summary>Amount off in minor units (cents), when fixed-amount. Null for percentage.</summary>
    public long? AmountOff { get; set; }

    /// <summary>Currency for <see cref="AmountOff"/>. Null for percentage coupons.</summary>
    public string? Currency { get; set; }

    /// <summary>Stripe duration: "once", "repeating" or "forever".</summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>Months the discount repeats, when <see cref="Duration"/> is "repeating".</summary>
    public int? DurationInMonths { get; set; }

    /// <summary>When the discount stops applying. Null for "forever" or open-ended.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>
    /// When the discount will actually be spent — the date of the next invoice. During a
    /// trial that is the trial end date, because no invoice exists until then.
    ///
    /// This is the difference between "your discount is active" and "your discount applies on
    /// 4 October". Without it a user who redeems on day 1 of a 14-day trial sees a badge, is
    /// charged nothing for two weeks either way, and has no way to tell whether the code
    /// worked.
    /// </summary>
    public DateTime? AppliesAt { get; set; }

    /// <summary>
    /// True when this discount expires BEFORE it is ever charged against.
    ///
    /// The trap: a coupon with duration "repeating" counts down in wall-clock time from the
    /// moment it is applied, NOT from first billing. Apply a one-month repeating coupon on
    /// day 1 of a 14-day trial and it is gone before the first invoice exists, so the
    /// user is charged in full having been told they had a free month. Only duration "once"
    /// survives a trial, because it waits for an invoice rather than a clock.
    ///
    /// Surfaced rather than silently tolerated so the UI can say so and the reward coupon can
    /// be caught in configuration instead of in a support ticket.
    /// </summary>
    public bool ExpiresBeforeItApplies { get; set; }

    /// <summary>
    /// Human-readable summary for the subscription card, e.g. "100% off for 1 month".
    /// Built server-side so the wording stays identical across web and mobile.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}
