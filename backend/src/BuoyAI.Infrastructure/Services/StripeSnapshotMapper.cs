using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The one place a Stripe subscription becomes a <see cref="StripeSubscriptionSnapshot"/>, used
/// by the webhook controller and by the live fetch so both see every item the same way.
///
/// Items are classified by the price's own metadata (<c>buoy_entitlement</c>), which a webhook
/// payload carries inline, so no second Stripe call is needed and no price id is hard coded.
/// </summary>
public static class StripeSnapshotMapper
{
    public const string EntitlementMetadataKey = "buoy_entitlement";
    public const string FoundingMetadataKey = "founding";

    /// <summary>Prefix of the subscription metadata keys that hold a scheduled removal date.</summary>
    public const string PendingEndPrefix = "pending_end_";

    /// <summary>
    /// A usage-billed item, such as the Cloud AI pay-as-you-go overage item. It grants nothing, bills monthly even on a
    /// yearly plan and has no quantity, so it is left out of the snapshot entirely: it must not read as an item with no known
    /// entitlement, pull the paid-through date a year early, or add its one cent unit price to the plan amount.
    /// </summary>
    public static bool IsMetered(Stripe.SubscriptionItem item) =>
        string.Equals(item.Price?.Recurring?.UsageType, "metered", StringComparison.OrdinalIgnoreCase);

    public static StripeSubscriptionSnapshot From(Stripe.Subscription s)
    {
        var licensed = (s.Items?.Data ?? []).Where(i => !IsMetered(i)).ToList();
        var items = licensed
            .Where(i => i.Price != null)
            .Select(i => new StripeSnapshotItem(
                i.Price.Id,
                i.Price.LookupKey,
                Meta(i.Price.Metadata, EntitlementMetadataKey),
                i.Quantity,
                string.Equals(Meta(i.Price.Metadata, FoundingMetadataKey), "true", StringComparison.OrdinalIgnoreCase),
                i.Price.Recurring?.Interval,
                i.Price.UnitAmount))
            .ToList();

        // Stripe moved the period end onto the items in the flexible-billing API versions; the
        // earliest item end is the safe "paid through" for a mixed-interval subscription.
        var periodEnd = licensed
            .Select(i => (DateTime?)i.CurrentPeriodEnd)
            .Where(d => d.HasValue && d.Value > DateTime.UnixEpoch)
            .OrderBy(d => d)
            .FirstOrDefault();

        return new StripeSubscriptionSnapshot(
            s.Id,
            s.Status,
            s.TrialEnd,
            licensed.FirstOrDefault()?.Price?.Id,
            s.CancelAtPeriodEnd,
            items,
            periodEnd,
            s.Metadata == null ? null : new Dictionary<string, string>(s.Metadata));
    }

    private static string? Meta(IDictionary<string, string>? metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var v) ? v : null;
}
