using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// One product a user holds through one subscription (migration 0052). A projection of what
/// the billing provider says, rebuilt whenever that subscription changes; never edited by hand.
/// A subscription with Bank sync and AI is two rows. Extra bank logins are one row whose
/// <see cref="Quantity"/> is the count.
/// </summary>
public class UserEntitlement
{
    public int Id { get; set; }
    public int UserId { get; set; }

    /// <summary>A name from <see cref="BuoyAI.Core.Entitlements.ProductEntitlements"/>.</summary>
    public string Entitlement { get; set; } = string.Empty;

    /// <summary>1 for a flag; the number of extra logins for <c>extra_logins</c>.</summary>
    public int Quantity { get; set; } = 1;

    public SubscriptionProvider Provider { get; set; } = SubscriptionProvider.Stripe;

    /// <summary>The provider's subscription id (Stripe: <c>sub_...</c>).</summary>
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>"active", or "past_due" while the provider retries a card (access is kept).</summary>
    public string Status { get; set; } = EntitlementStatus.Active;

    /// <summary>End of the period the customer has paid for.</summary>
    public DateTime? PaidThrough { get; set; }

    /// <summary>When this product lapses (removal scheduled at period end, or the plan is cancelled). Null if none.</summary>
    public DateTime? EndsAt { get; set; }

    /// <summary>The customer is on founding-member prices.</summary>
    public bool Founding { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class EntitlementStatus
{
    public const string Active = "active";
    public const string PastDue = "past_due";
}
