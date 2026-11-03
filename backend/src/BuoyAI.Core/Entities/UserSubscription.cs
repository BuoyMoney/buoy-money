using BuoyAI.Core.Enums;
using BuoyAI.Core.DTOs;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a user's BuoyAI subscription (billing)
/// This is different from Subscription entity which tracks user's external subscriptions
/// </summary>
public class UserSubscription
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public SubscriptionTier Tier { get; set; }
    public SubscriptionBillingCycle BillingCycle { get; set; } // Monthly, Yearly
    public decimal Amount { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime NextBillingDate { get; set; }
    public UserSubscriptionStatus Status { get; set; }
    public string? StripeCustomerId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public string? PromoCode { get; set; }

    /// <summary>Stripe (web) or a store via RevenueCat. Store rows never carry Stripe ids.</summary>
    public SubscriptionProvider Provider { get; set; } = SubscriptionProvider.Stripe;

    /// <summary>For store rows: "&lt;provider&gt;:&lt;product id&gt;", the key both the webhook and the REST refresh land on.</summary>
    public string? ExternalId { get; set; }
    public decimal? DiscountAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // Navigation properties
    public User User { get; set; } = null!;
    public ICollection<SubscriptionPayment> Payments { get; set; } = new List<SubscriptionPayment>();
}