using Microsoft.Extensions.Configuration;
using BuoyAI.Core.Enums;

namespace BuoyAI.Infrastructure.Utilities;

/// <summary>
/// Shared helper methods for billing and subscription operations
/// Extracted to avoid code duplication between BillingService and StripeService
/// </summary>
public static class BillingHelpers
{
    /// <summary>
    /// Gets the Stripe price ID for a given tier and billing cycle from configuration
    /// </summary>
    public static string GetStripePriceId(IConfiguration configuration, SubscriptionTier tier, SubscriptionBillingCycle billingCycle)
    {
        var tierName = tier.ToString();
        var cycleName = billingCycle == SubscriptionBillingCycle.Monthly ? "Monthly" : "Yearly";
        var configKey = $"Stripe:Products:{tierName}:{cycleName}";

        var priceId = configuration[configKey];
        if (string.IsNullOrEmpty(priceId))
        {
            throw new InvalidOperationException($"Stripe price ID not configured for {configKey}");
        }

        return priceId;
    }

    /// <summary>
    /// Validates that a purchasable plan was requested. Admin is operator-only (granted by
    /// email allowlist, never bought) and PremiumInherited is assigned via family
    /// membership — neither may be selected by a customer. Throws on anything else.
    /// </summary>
    public static void ValidatePurchasablePlan(SubscriptionTier tier, SubscriptionBillingCycle billingCycle)
    {
        if (tier != SubscriptionTier.Standard && tier != SubscriptionTier.Premium)
        {
            throw new InvalidOperationException("Only the Standard and Premium plans can be purchased.");
        }

        if (billingCycle != SubscriptionBillingCycle.Monthly && billingCycle != SubscriptionBillingCycle.Yearly)
        {
            throw new InvalidOperationException("Billing cycle must be Monthly or Yearly.");
        }
    }
}
