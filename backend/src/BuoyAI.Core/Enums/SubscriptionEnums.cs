namespace BuoyAI.Core.Enums;

/// <summary>
/// BuoyAI subscription tiers for billing
/// </summary>
public enum SubscriptionTier
{
    Standard = 1,
    Premium = 2,
    /// <summary>
    /// Family member inheriting premium access from family owner.
    /// Has all premium features but is not the paying member.
    /// </summary>
    PremiumInherited = 3,
    Admin = 99 // Internal use only
}

/// <summary>
/// Subscription billing cycle options
/// </summary>
public enum SubscriptionBillingCycle
{
    Monthly = 1,
    Yearly = 12
}

/// <summary>Who bills a UserSubscription row. Stripe for web; the stores (via RevenueCat) for in-app purchases.</summary>
public enum SubscriptionProvider
{
    Stripe = 0,
    AppStore = 1,
    PlayStore = 2
}

/// <summary>
/// User subscription status for billing
/// </summary>
public enum UserSubscriptionStatus
{
    Active = 0,
    PastDue = 1,
    Canceled = 2,
    Expired = 3,
    Trialing = 4,
    TrialExpired = 5
}

/// <summary>
/// Payment status for subscription payments
/// </summary>
public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3
}