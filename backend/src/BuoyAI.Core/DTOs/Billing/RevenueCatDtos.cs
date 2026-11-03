using System.Text.Json.Serialization;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Billing;

/// <summary>Envelope RevenueCat posts to the webhook: <c>{"api_version":"1.0","event":{...}}</c>.</summary>
public class RevenueCatWebhookEnvelope
{
    [JsonPropertyName("api_version")] public string? ApiVersion { get; set; }
    [JsonPropertyName("event")] public RevenueCatEvent? Event { get; set; }
}

/// <summary>
/// The subset of RevenueCat webhook fields the projection reads. Field names follow the
/// webhook contract (snake_case); anything not listed is ignored on deserialization.
/// </summary>
public class RevenueCatEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("event_timestamp_ms")] public long? EventTimestampMs { get; set; }
    [JsonPropertyName("app_user_id")] public string? AppUserId { get; set; }
    [JsonPropertyName("original_app_user_id")] public string? OriginalAppUserId { get; set; }
    [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
    [JsonPropertyName("product_id")] public string? ProductId { get; set; }
    [JsonPropertyName("new_product_id")] public string? NewProductId { get; set; }
    [JsonPropertyName("entitlement_ids")] public List<string>? EntitlementIds { get; set; }
    [JsonPropertyName("period_type")] public string? PeriodType { get; set; }
    [JsonPropertyName("purchased_at_ms")] public long? PurchasedAtMs { get; set; }
    [JsonPropertyName("expiration_at_ms")] public long? ExpirationAtMs { get; set; }
    [JsonPropertyName("grace_period_expiration_at_ms")] public long? GracePeriodExpirationAtMs { get; set; }
    [JsonPropertyName("environment")] public string? Environment { get; set; }
    [JsonPropertyName("store")] public string? Store { get; set; }
    [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
    [JsonPropertyName("original_transaction_id")] public string? OriginalTransactionId { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("cancel_reason")] public string? CancelReason { get; set; }
    [JsonPropertyName("expiration_reason")] public string? ExpirationReason { get; set; }
    [JsonPropertyName("transferred_from")] public List<string>? TransferredFrom { get; set; }
    [JsonPropertyName("transferred_to")] public List<string>? TransferredTo { get; set; }
}

/// <summary>Outcome of applying one webhook event, for logging and the 200 body.</summary>
public record RevenueCatApplyResult(bool Applied, string Reason, int? UserId = null);

/// <summary>
/// What a store subscription looks like after normalisation, whether it came from a webhook
/// event or from the REST subscriber lookup. One shape, one projection.
/// </summary>
public record StoreSubscriptionState(
    int UserId,
    SubscriptionProvider Provider,
    string ProductId,
    SubscriptionTier Tier,
    SubscriptionBillingCycle Cycle,
    UserSubscriptionStatus Status,
    DateTime? PurchasedAt,
    DateTime? ExpiresAt,
    /// <summary>Set when auto-renew is off: access continues until <see cref="ExpiresAt"/>.</summary>
    bool WillNotRenew,
    decimal? Price,
    string? Currency);

/// <summary>
/// The App Store / Play product identifiers, one per tier and cycle. The same ids are created
/// in both stores so the client can address a plan without knowing which store it is on.
/// Overridable per key via <c>RevenueCat:Products:{StandardMonthly|StandardYearly|PremiumMonthly|PremiumYearly}</c>.
/// </summary>
public static class StoreProducts
{
    public const string StandardMonthly = "buoyai_standard_monthly";
    public const string StandardYearly = "buoyai_standard_yearly";
    public const string PremiumMonthly = "buoyai_premium_monthly";
    public const string PremiumYearly = "buoyai_premium_yearly";

    /// <summary>RevenueCat entitlement identifiers, one per tier.</summary>
    public const string StandardEntitlement = "standard";
    public const string PremiumEntitlement = "premium";

    public static string ConfigKey(SubscriptionTier tier, SubscriptionBillingCycle cycle) =>
        $"RevenueCat:Products:{tier}{(cycle == SubscriptionBillingCycle.Yearly ? "Yearly" : "Monthly")}";

    public static string DefaultId(SubscriptionTier tier, SubscriptionBillingCycle cycle) => (tier, cycle) switch
    {
        (SubscriptionTier.Standard, SubscriptionBillingCycle.Yearly) => StandardYearly,
        (SubscriptionTier.Standard, _) => StandardMonthly,
        (_, SubscriptionBillingCycle.Yearly) => PremiumYearly,
        _ => PremiumMonthly,
    };

    /// <summary>
    /// Maps a store product id back to its tier and cycle, or null when it is not one of ours.
    /// Play product ids can arrive as <c>product:base_plan</c>; the base-plan suffix is ignored.
    /// </summary>
    public static (SubscriptionTier Tier, SubscriptionBillingCycle Cycle)? Resolve(
        string? productId, Func<string, string?> config)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        var id = productId.Split(':')[0].Trim();
        foreach (var tier in new[] { SubscriptionTier.Standard, SubscriptionTier.Premium })
        {
            foreach (var cycle in new[] { SubscriptionBillingCycle.Monthly, SubscriptionBillingCycle.Yearly })
            {
                var configured = config(ConfigKey(tier, cycle));
                var expected = string.IsNullOrWhiteSpace(configured) ? DefaultId(tier, cycle) : configured;
                if (string.Equals(id, expected, StringComparison.OrdinalIgnoreCase))
                    return (tier, cycle);
            }
        }
        return null;
    }

    public static string IdFor(SubscriptionTier tier, SubscriptionBillingCycle cycle, Func<string, string?> config)
    {
        var configured = config(ConfigKey(tier, cycle));
        return string.IsNullOrWhiteSpace(configured) ? DefaultId(tier, cycle) : configured;
    }
}
