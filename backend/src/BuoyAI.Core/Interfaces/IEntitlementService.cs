using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// The one answer to "what may this user do?". Resolves the effective tier (operator
/// allowlist, then trial, then an active subscription from any provider) and everything
/// that hangs off it: Premium access, feature gates and AI caps.
///
/// Deliberately has no Stripe dependency. Sign-in and every gate depend on this, and a
/// deployment without billing keys must still be able to construct it. The hosted build
/// plugs the Stripe re-check in through <see cref="ITierRefreshHook"/>.
/// </summary>
public interface IEntitlementService
{
    /// <summary>
    /// Admin, the trial tier, or the active subscription's tier. Null when the user has
    /// nothing active (or does not exist). A null answer asks any registered
    /// <see cref="ITierRefreshHook"/> once, then reads again.
    /// </summary>
    Task<SubscriptionTier?> GetEffectiveTierAsync(int userId);

    /// <summary>
    /// The effective tier, or Standard when there is none or the lookup fails. For callers that
    /// pick limits or models and must always have a tier; never throws.
    /// </summary>
    Task<SubscriptionTier> GetTierOrStandardAsync(int userId);

    /// <summary>Premium, PremiumInherited or Admin right now.</summary>
    Task<bool> HasPremiumAccessAsync(int userId);

    /// <summary>
    /// Whether the user's tier meets the minimum for <paramref name="feature"/>. A feature
    /// that is not in the map is allowed.
    /// </summary>
    Task<bool> HasFeatureAsync(int userId, string feature);

    /// <summary>
    /// Local Premium answer for a user row already in hand (allowlist, active trial, or a
    /// Premium-class tier on the row). No query and no refresh hook.
    /// </summary>
    bool HasPremiumAccess(User user);

    /// <summary>The caps for a tier. Inherited Premium reads the Premium row.</summary>
    AiTierLimits GetAiLimits(SubscriptionTier tier);

    /// <summary>
    /// The products (bank sync, AI, instant refresh, extra logins) the user may use right now.
    /// Built from the <c>UserEntitlements</c> projection plus, for subscribers on a legacy
    /// Standard or Premium plan, the products that tier implies. Instant refresh and extra
    /// logins only count together with bank sync. Reads the database, never a JWT claim.
    /// Never throws for an unknown user; they get nothing.
    /// </summary>
    Task<ProductAccess> GetProductAccessAsync(int userId);

    /// <summary>
    /// True when the user holds at least one Buoy Money Cloud product of their own (an active or past-due
    /// <c>UserEntitlements</c> row). Unlike <see cref="GetProductAccessAsync"/> a legacy plan, a trial or the admin
    /// allowlist does not count: this asks "did they buy a Cloud add-on", for showing Cloud-only screens.
    /// </summary>
    Task<bool> HasCloudProductsAsync(int userId);
}

/// <summary>
/// Optional hook for deployments where something outside the database knows better than the
/// local projection (hosted: Stripe). Called only when the local answer is "no tier", never
/// on a grant. Returns true when it changed local state and the tier should be read again.
/// Implementations must not throw.
/// </summary>
public interface ITierRefreshHook
{
    Task<bool> RefreshAsync(int userId, string reason);
}
