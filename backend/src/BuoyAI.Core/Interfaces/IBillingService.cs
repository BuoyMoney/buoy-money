using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for managing user subscription billing (BuoyAI subscriptions)
/// Note: This is different from ISubscriptionService which tracks user expense subscriptions
/// </summary>
public interface IBillingService
{
    // User retrieval
    Task<User?> GetUserByIdAsync(int userId);
    
    #region Trial Management (Card-Upfront via Stripe)
    /// <summary>
    /// Check if user's trial is currently active.
    /// </summary>
    Task<bool> IsTrialActiveAsync(int userId);
    
    /// <summary>
    /// Get number of days remaining in trial.
    /// </summary>
    Task<int> GetTrialDaysRemainingAsync(int userId);
    
    /// <summary>
    /// Check if user is eligible to start a trial (hasn't used one before).
    /// </summary>
    Task<bool> CanStartTrialAsync(int userId);
    
    /// <summary>
    /// Ensures user has a Stripe customer record, creating one if needed.
    /// </summary>
    Task<string> EnsureStripeCustomerAsync(int userId);
    
    /// <summary>
    /// Start a trial with payment method collected upfront (card-upfront).
    /// Creates a Stripe subscription with trial that auto-converts when trial ends.
    /// </summary>
    Task<StartTrialWithCardResult> StartTrialWithCardAsync(int userId, string paymentMethodId, SubscriptionTier tier, SubscriptionBillingCycle billingCycle);

    /// <summary>
    /// Start a no-card free trial (no Stripe subscription, no charge). Grants Premium
    /// trial access for the configured trial period. Access is resolved through
    /// GetUserTierAsync, which checks TrialEndDate on every call, so the trial expires
    /// automatically with no scheduled job. Throws if the user (or their email) has
    /// already used a trial.
    /// </summary>
    Task<User> StartTrialAsync(int userId);
    #endregion

    // Subscription management
    Task<UserSubscription?> GetUserSubscriptionAsync(int userId);
    Task<UserSubscription> CreateSubscriptionAsync(int userId, CreateBillingSubscriptionRequest request);

    /// <summary>
    /// Creates a hosted Stripe Checkout session for the chosen plan and returns the URL to
    /// open. Activation happens via the checkout.session.completed webhook.
    /// </summary>
    Task<string> CreateCheckoutSessionAsync(int userId, SubscriptionTier tier, SubscriptionBillingCycle billingCycle, string? promoCode = null, string? platform = null);

    /// <summary>
    /// Creates a hosted Stripe Customer Portal session for self-serve billing management
    /// (invoices, receipts, payment methods, cancellation) and returns the URL to open.
    /// </summary>
    Task<string> CreateBillingPortalSessionAsync(int userId);

    /// <summary>
    /// Activates a subscription created through hosted Stripe Checkout (no pre-existing
    /// UserSubscription row). Idempotent on the Stripe subscription id.
    /// </summary>
    Task ActivateSubscriptionFromCheckoutAsync(
        string? clientReferenceId, string? stripeSubscriptionId, string? stripeCustomerId,
        IDictionary<string, string>? metadata, string? stripeStatus = null, decimal? actualAmount = null);

    Task<UserSubscription> ChangeSubscriptionAsync(int userId, ChangeSubscriptionRequest request);
    Task CancelSubscriptionAsync(int userId, CancelSubscriptionRequest request);

    /// <summary>
    /// Permanently deletes the user's Stripe customer object (PII + payment history) at Stripe
    /// and clears the stored StripeCustomerId. Used on account deletion — canceling a
    /// subscription alone leaves the customer record behind. No-op if there is no customer.
    /// </summary>
    Task DeleteStripeCustomerAsync(int userId);

    // Billing and payments
    Task<List<SubscriptionPayment>> GetBillingHistoryAsync(int userId);
    Task<PaymentStatus> RetryPaymentAsync(int userId, int paymentId);
    Task<UpdatePaymentMethodResult> UpdatePaymentMethodAsync(int userId, string paymentMethodToken);

    // Tier validation and access control
    Task<bool> CanAccessFeatureAsync(int userId, string featureName);
    Task<SubscriptionTier?> GetUserTierAsync(int userId);
    Task<bool> HasActiveSubscriptionOrTrialAsync(int userId);
    Task UpdateUserTierAsync(int userId, SubscriptionTier tier);

    // Promo codes
    Task<bool> ValidatePromoCodeAsync(string promoCode);
    Task<PromoCodeValidationResult> ValidatePromoCodeAsync(string promoCode, string priceId);
    /// <summary>
    /// Redeems a promotion code against the user's existing Stripe subscription and returns
    /// the resulting discount. Returns null when the code is unusable for any reason —
    /// unknown, inactive, restricted, or no subscription to attach it to. Callers must not
    /// distinguish those, or the redeem form becomes an oracle for which codes exist.
    /// </summary>
    Task<SubscriptionDiscountDto?> RedeemPromoCodeAsync(int userId, string promoCode);

    /// <summary>
    /// The discount currently applied to the user's subscription, or null. Read live from
    /// Stripe, which owns the lifecycle. Display-only.
    /// </summary>
    Task<SubscriptionDiscountDto?> GetActiveDiscountAsync(int userId);

    /// <summary>
    /// Applies a reward coupon directly, bypassing the promotion-code lookup. This is the
    /// system granting something earned, not a user redeeming a string, so there is no
    /// shareable code to leak. Null when there is no subscription to attach it to.
    /// </summary>
    Task<SubscriptionDiscountDto?> ApplyRewardCouponAsync(int userId, string couponId);

    // Pricing plans (fetched from Stripe)
    Task<PricingPlansResponse> GetPricingPlansAsync();

    // Webhook synchronization (Stripe -> Database)
    Task SyncSubscriptionFromStripeAsync(string stripeSubscriptionId);
    Task HandleFailedPaymentAsync(string stripeSubscriptionId, string failureReason, string? stripeInvoiceId = null);
    Task HandleSuccessfulPaymentAsync(string stripeSubscriptionId, decimal amount, string? stripeInvoiceId = null);
    Task CancelSubscriptionByStripeIdAsync(string stripeSubscriptionId);
    Task HandleTrialWillEndAsync(string stripeSubscriptionId, DateTime trialEnd);

    /// <summary>
    /// A cardless trial ended without a payment method and Stripe paused the subscription.
    /// Closes out local trial access and puts the account behind the paywall. Idempotent.
    /// </summary>
    Task HandleTrialLapsedAsync(string stripeSubscriptionId);

    /// <summary>
    /// Stripe resumed a paused subscription because a payment method was added. Idempotent.
    /// </summary>
    Task HandleSubscriptionResumedAsync(string stripeSubscriptionId);
    Task HandleSubscriptionStatusChangeAsync(string stripeSubscriptionId, string status);
    
    /// <summary>
    /// Gets the user ID associated with a Stripe subscription ID.
    /// Returns null if no subscription is found.
    /// </summary>
    Task<int?> GetUserIdByStripeSubscriptionIdAsync(string stripeSubscriptionId);

    /// <summary>
    /// Whether a Stripe webhook event with this id has already been processed.
    /// Used to make webhook handling idempotent against redeliveries.
    /// </summary>
    Task<bool> HasProcessedStripeEventAsync(string eventId);

    /// <summary>
    /// Records a Stripe webhook event id as processed. Idempotent: a concurrent
    /// duplicate insert is treated as already-recorded.
    /// </summary>
    Task MarkStripeEventProcessedAsync(string eventId, string eventType);
}