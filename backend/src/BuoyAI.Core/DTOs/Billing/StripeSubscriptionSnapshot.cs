namespace BuoyAI.Core.DTOs.Billing;

/// <summary>
/// What Stripe currently says about a subscription. This is the authoritative shape that the
/// local projection is derived from.
///
/// Deliberately a small DTO rather than the Stripe SDK object: it keeps <c>Stripe.*</c> types
/// out of Core, and it pins down exactly which fields we treat as authoritative. Anything not
/// listed here is, by definition, not something we mirror.
/// </summary>
/// <param name="SubscriptionId">Stripe subscription id.</param>
/// <param name="Status">
/// Raw Stripe status: trialing, active, past_due, paused, canceled, unpaid, incomplete.
/// Kept as the raw string so an unrecognised future status is passed through and logged
/// rather than being silently coerced into the wrong local state.
/// </param>
/// <param name="TrialEnd">Trial end per Stripe's clock, null when not trialing.</param>
/// <param name="PriceId">Price on the first item, used to resolve the legacy tier.</param>
/// <param name="CancelAtPeriodEnd">Set when the customer has cancelled but still has access.</param>
/// <param name="Items">
/// Every item on the subscription. Null from callers that only know the first price (older
/// tests); treated the same as an empty list.
/// </param>
/// <param name="CurrentPeriodEnd">End of the paid period, when Stripe tells us.</param>
/// <param name="Metadata">Subscription metadata (carries the pending-removal dates the cloud service writes).</param>
public record StripeSubscriptionSnapshot(
    string SubscriptionId,
    string Status,
    DateTime? TrialEnd,
    string? PriceId,
    bool CancelAtPeriodEnd,
    IReadOnlyList<StripeSnapshotItem>? Items = null,
    DateTime? CurrentPeriodEnd = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>
    /// True when at least one item is a catalog product (its price carries <c>buoy_entitlement</c>).
    /// The hosted Standard and Premium prices never do, so they stay on the legacy path.
    /// </summary>
    public bool IsCatalog => Items != null && Items.Any(i => !string.IsNullOrWhiteSpace(i.Entitlements));
}

/// <summary>One subscription item reduced to what the projection needs.</summary>
/// <param name="PriceId">Stripe price id.</param>
/// <param name="LookupKey">The price's lookup key, e.g. <c>bank_sync_yearly_founding</c>.</param>
/// <param name="Entitlements">The price's <c>buoy_entitlement</c> metadata (comma list), or null for a non-catalog price.</param>
/// <param name="Quantity">Item quantity (extra logins use it).</param>
/// <param name="Founding">Price metadata <c>founding=true</c>.</param>
/// <param name="Interval">"month" or "year".</param>
/// <param name="UnitAmountCents">Price unit amount, for the local amount column.</param>
public record StripeSnapshotItem(
    string PriceId,
    string? LookupKey,
    string? Entitlements,
    long Quantity,
    bool Founding,
    string? Interval,
    long? UnitAmountCents);
