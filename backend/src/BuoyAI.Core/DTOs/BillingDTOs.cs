using BuoyAI.Core.Enums;
using System.Text.Json.Serialization;

namespace BuoyAI.Core.DTOs.Billing;

/// <summary>
/// Request to create a new Stripe billing subscription (not user-tracked subscriptions)
/// </summary>
public class CreateBillingSubscriptionRequest
{
    /// <summary>
    /// Subscription tier (Standard or Premium)
    /// </summary>
    public SubscriptionTier Tier { get; set; }

    /// <summary>
    /// Billing cycle (Monthly or Yearly)
    /// </summary>
    public SubscriptionBillingCycle BillingCycle { get; set; }

    /// <summary>
    /// Payment method token from Stripe
    /// </summary>
    public string PaymentMethodToken { get; set; } = string.Empty;

    /// <summary>
    /// Optional promo code
    /// </summary>
    public string? PromoCode { get; set; }
}

/// <summary>
/// Request to create a hosted Stripe Checkout session for a chosen plan.
/// </summary>
public class CreateCheckoutSessionRequest
{
    public SubscriptionTier Tier { get; set; }
    public SubscriptionBillingCycle BillingCycle { get; set; }
    public string? PromoCode { get; set; }

    /// <summary>
    /// Optional client platform. When "ios", the backend builds a Universal-Link
    /// path return URL (…/app/billing?checkout=…) that the installed iOS app
    /// intercepts on return from hosted Stripe Checkout (digital-goods link-out).
    /// Any other value (or null) uses the standard web return URL. The client
    /// never supplies the URL itself — the backend derives it — so there is no
    /// open-redirect surface.
    /// </summary>
    public string? Platform { get; set; }
}

/// <summary>
/// Request to change an existing subscription
/// </summary>
public class ChangeSubscriptionRequest
{
    /// <summary>
    /// New subscription tier
    /// </summary>
    [JsonPropertyName("tier")]
    public SubscriptionTier? NewTier { get; set; }

    /// <summary>
    /// New billing cycle
    /// </summary>
    [JsonPropertyName("billingCycle")]
    public SubscriptionBillingCycle? NewBillingCycle { get; set; }

    /// <summary>
    /// New payment method token (optional)
    /// </summary>
    [JsonPropertyName("paymentMethodToken")]
    public string? PaymentMethodToken { get; set; }

    /// <summary>
    /// Optional promo code for the change
    /// </summary>
    [JsonPropertyName("promoCode")]
    public string? PromoCode { get; set; }
}

/// <summary>
/// Request to cancel subscription
/// </summary>
public class CancelSubscriptionRequest
{
    /// <summary>
    /// Whether to cancel immediately or at end of billing period
    /// </summary>
    public bool CancelImmediately { get; set; } = false;

    /// <summary>
    /// Optional reason for cancellation
    /// </summary>
    public string? CancellationReason { get; set; }

    /// <summary>
    /// Optional feedback about the service
    /// </summary>
    public string? Feedback { get; set; }
}

/// <summary>
/// Request to update payment method
/// </summary>
public class UpdatePaymentMethodRequest
{
    /// <summary>
    /// Stripe payment method token from frontend
    /// </summary>
    public string PaymentMethodToken { get; set; } = string.Empty;
}

/// <summary>
/// Result of payment method update
/// </summary>
public class UpdatePaymentMethodResult
{
    public bool Success { get; set; }
    public bool PendingInvoicesPaid { get; set; }
}

/// <summary>
/// Response containing subscription information
/// </summary>
public class SubscriptionResponse
{
    public int Id { get; set; }
    public SubscriptionTier Tier { get; set; }
    public SubscriptionBillingCycle BillingCycle { get; set; }
    public decimal Amount { get; set; }
    public UserSubscriptionStatus Status { get; set; }
    public DateTime? TrialStartDate { get; set; }
    public DateTime? TrialEndDate { get; set; }
    public DateTime? NextBillingDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? StripeSubscriptionId { get; set; }
}

/// <summary>
/// Response containing billing payment information
/// </summary>
public class BillingPaymentResponse
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public PaymentStatus Status { get; set; }
    public DateTime? PaymentDate { get; set; }
    public string? FailureReason { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public string? StripeInvoiceId { get; set; }
}

/// <summary>
/// Response for trial status
/// </summary>
public class TrialStatusResponse
{
    public bool IsTrialActive { get; set; }
    public int DaysRemaining { get; set; }
    public bool CanStartTrial { get; set; }
    public DateTime? TrialEndDate { get; set; }
}

/// <summary>
/// Response for feature access check
/// </summary>
public class FeatureAccessResponse
{
    public bool CanAccess { get; set; }
    public string? CurrentTier { get; set; }
    public string FeatureName { get; set; } = string.Empty;
    public string? RequiredTier { get; set; }
}

/// <summary>
/// Response for subscription plans
/// </summary>
public class SubscriptionPlanResponse
{
    public string Tier { get; set; } = string.Empty;
    public decimal MonthlyPrice { get; set; }
    public decimal YearlyPrice { get; set; }
    public decimal YearlySavings { get; set; }
    public string[] Features { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Pricing plan fetched from Stripe - single source of truth for prices
/// </summary>
public class PricingPlanDto
{
    public string Tier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public long MonthlyPriceCents { get; set; }
    public long YearlyPriceCents { get; set; }
    public string MonthlyPriceId { get; set; } = string.Empty;
    public string YearlyPriceId { get; set; } = string.Empty;
    /// <summary>App Store / Play product identifiers; the client asks the store for the price.</summary>
    public string StoreProductIdMonthly { get; set; } = string.Empty;
    public string StoreProductIdYearly { get; set; } = string.Empty;
    public string[] Features { get; set; } = Array.Empty<string>();
    public bool IsPopular { get; set; }
    public string Currency { get; set; } = "usd";
}

/// <summary>
/// Response containing all pricing plans from Stripe
/// </summary>
public class PricingPlansResponse
{
    public List<PricingPlanDto> Plans { get; set; } = new();
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Request to create a SetupIntent for card collection (card-upfront trial)
/// </summary>
public class CreateSetupIntentRequest
{
    /// <summary>
    /// Desired subscription tier for the trial
    /// </summary>
    public SubscriptionTier? Tier { get; set; }

    /// <summary>
    /// Desired billing cycle after trial ends
    /// </summary>
    public SubscriptionBillingCycle? BillingCycle { get; set; }
}

/// <summary>
/// Request to start trial with a pre-validated payment method
/// </summary>
public class StartTrialWithCardRequest
{
    /// <summary>
    /// Payment method ID from successful SetupIntent confirmation
    /// </summary>
    public string PaymentMethodId { get; set; } = string.Empty;

    /// <summary>
    /// Subscription tier for the trial (defaults to Standard)
    /// </summary>
    public SubscriptionTier? Tier { get; set; }

    /// <summary>
    /// Billing cycle after trial ends (defaults to Monthly)
    /// </summary>
    public SubscriptionBillingCycle? BillingCycle { get; set; }
}

/// <summary>
/// Result of starting a trial with card-upfront
/// </summary>
public class StartTrialWithCardResult
{
    public string StripeSubscriptionId { get; set; } = string.Empty;
    public SubscriptionTier Tier { get; set; }
    public SubscriptionBillingCycle BillingCycle { get; set; }
    public DateTime TrialEndDate { get; set; }
    public string SubscriptionStatus { get; set; } = "trialing";
}