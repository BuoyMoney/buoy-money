using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for Stripe payment processing and subscription management.
/// Note: Webhook handling is done by StripeWebhookController, which calls BillingService
/// for database operations. StripeService is only for Stripe API calls.
/// </summary>
public interface IStripeService
{
    // Customer management
    Task<string> CreateCustomerAsync(User user);
    Task UpdateCustomerAsync(string customerId, User user);
    /// <summary>
    /// Deletes a Stripe customer, which also cancels its subscriptions and detaches its
    /// payment methods. Returns false WITHOUT deleting when the customer belongs to a
    /// different environment, since staging and production share one live Stripe account.
    /// Callers must not report a deletion they did not get.
    /// </summary>
    Task<bool> DeleteCustomerAsync(string customerId);
    Task AttachPaymentMethodAsync(string customerId, string paymentMethodId);

    // SetupIntent for card collection (card-upfront trials)
    Task<SetupIntentResult> CreateSetupIntentAsync(string customerId);

    // Hosted Stripe Customer Portal (self-serve invoices, receipts, payment methods, cancel).
    // Returns the URL to open.
    Task<string> CreateBillingPortalSessionAsync(string customerId, string returnUrl);

    // Hosted Stripe Checkout (subscription mode). Returns the URL to redirect the user to.
    Task<string> CreateCheckoutSessionAsync(
        string customerId,
        string priceId,
        string successUrl,
        string cancelUrl,
        string clientReferenceId,
        IDictionary<string, string> metadata,
        string? promoCode = null,
        string? customerCountry = null);

    // Subscription management
    Task<string> CreateSubscriptionAsync(string customerId, string priceId, string? promoCode = null, bool startTrial = false);
    Task<string> CreateTrialSubscriptionAsync(string customerId, string priceId, string paymentMethodId, int trialDays = 14);
    Task UpdateSubscriptionAsync(string subscriptionId, string newPriceId, string? promoCode = null);
    Task CancelSubscriptionAsync(string subscriptionId, bool immediately = false);

    // Trial management

    /// <summary>
    /// Creates a cardless trial: a real Stripe subscription in <c>trialing</c> with no payment
    /// method, pausing at trial end if no card was added. Stripe owns the lifecycle from here,
    /// including the three-day <c>trial_will_end</c> reminder.
    /// </summary>
    Task<CardlessTrialResult> CreateCardlessTrialSubscriptionAsync(
        string customerId, SubscriptionTier tier, int trialDays);

    /// <summary>Resumes a subscription Stripe paused when a cardless trial lapsed.</summary>
    Task ResumePausedSubscriptionAsync(string subscriptionId);

    /// <summary>Existing Stripe customer id for this email, or null. Prevents duplicate customers.</summary>
    Task<string?> FindCustomerIdByEmailAsync(string email);

    /// <summary>
    /// The customer's current live subscription, or null. Excludes canceled/incomplete_expired;
    /// a paused subscription counts as live because a lapsed cardless trial is resumable.
    /// This is the read that makes Stripe the authority.
    /// </summary>
    Task<StripeSubscriptionSnapshot?> GetLiveSubscriptionAsync(string customerId);

    /// <summary>Snapshot of one subscription by id, or null when Stripe does not know it.</summary>
    Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(string subscriptionId);

    // Payment management
    Task<PaymentStatus> GetPaymentStatusAsync(string paymentIntentId);
    Task RefundPaymentAsync(string paymentIntentId, decimal? amount = null);
    Task<bool> PayPendingInvoicesAsync(string customerId);

    // Promo codes
    Task<bool> ValidatePromoCodeAsync(string promoCode);
    Task<PromoCodeValidationResult> ValidatePromoCodeWithPriceAsync(string promoCode, string priceId);

    /// <summary>
    /// Applies a promotion code to an EXISTING subscription and returns the resulting
    /// discount. Returns null when the code is unknown, inactive, or rejected by its own
    /// restrictions — callers must not distinguish those, so a redemption form cannot be used
    /// to enumerate which codes exist.
    /// </summary>
    Task<SubscriptionDiscountDto?> ApplyPromotionCodeToSubscriptionAsync(string subscriptionId, string promoCode);

    /// <summary>
    /// The discount currently on a subscription, or null when there is none. Read live from
    /// Stripe, which owns the lifecycle — a discount expires and disappears on its own.
    /// </summary>
    Task<SubscriptionDiscountDto?> GetSubscriptionDiscountAsync(string subscriptionId);

    /// <summary>
    /// Applies a coupon by id to a subscription, with no customer-facing code involved.
    /// Used for system-granted rewards. Returns null if Stripe rejects the coupon.
    /// </summary>
    Task<SubscriptionDiscountDto?> ApplyCouponToSubscriptionAsync(string subscriptionId, string couponId);
    Task<decimal> GetPromoCodeDiscountAsync(string promoCode);

    // Pricing
    Task<List<(string PriceId, long UnitAmount, string Currency, string Interval)>> GetPricesAsync(IEnumerable<string> priceIds);
}

/// <summary>
/// Result from creating a SetupIntent for card collection
/// </summary>
public record SetupIntentResult(string SetupIntentId, string ClientSecret);