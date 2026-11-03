namespace BuoyAI.Core.Enums;

/// <summary>
/// One place for "does this tier get Premium?". A family member on
/// <see cref="SubscriptionTier.PremiumInherited"/> has Premium through the family owner, so for
/// feature gates, AI limits and model choice they are treated exactly like Premium. Tier-keyed
/// tables (limits, models) only list Standard/Premium/Admin; look them up with
/// <see cref="ForLimits"/> so an inherited tier never misses the table and fails closed.
/// </summary>
public static class SubscriptionTierExtensions
{
  /// <summary>Premium, PremiumInherited or Admin. Null (no plan, trial over) is false.</summary>
  public static bool HasPremiumAccess(this SubscriptionTier? tier) =>
    tier is SubscriptionTier.Premium or SubscriptionTier.PremiumInherited or SubscriptionTier.Admin;

  /// <inheritdoc cref="HasPremiumAccess(SubscriptionTier?)"/>
  public static bool HasPremiumAccess(this SubscriptionTier tier) => ((SubscriptionTier?)tier).HasPremiumAccess();

  /// <summary>
  /// Premium-class check for the <c>subscription_tier</c> JWT claim value, used by rate-limit
  /// partitioning that only has the token. Same set as <see cref="HasPremiumAccess(SubscriptionTier?)"/>.
  /// </summary>
  public static bool IsPremiumClaim(string? claim) =>
    claim is "Premium" or "PremiumInherited" or "Admin";

  /// <summary>The tier to use as a key into Standard/Premium/Admin tables.</summary>
  public static SubscriptionTier ForLimits(this SubscriptionTier tier) =>
    tier == SubscriptionTier.PremiumInherited ? SubscriptionTier.Premium : tier;
}
