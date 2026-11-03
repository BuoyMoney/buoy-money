using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// The single path by which Stripe subscription state reaches our database.
///
/// Stripe owns the subscription lifecycle. The columns on <c>User</c> and
/// <c>UserSubscriptions</c> are a PROJECTION of it, not a second source of truth. Everything
/// that learns something from Stripe (webhooks, forced refresh, the reconciler) funnels
/// through here, so there is exactly one mapping to reason about and exactly one place a
/// mistake can live.
///
/// What this service does NOT own: <c>HasUsedTrial</c> and <c>TrialHistory</c>. Those record
/// that an email once consumed its lifetime trial, they must survive account deletion, and we
/// delete the Stripe customer on deletion, so Stripe structurally cannot hold them. They stay
/// ours, permanently, and nothing in here writes them.
/// </summary>
public interface ISubscriptionSyncService
{
    /// <summary>
    /// Pulls the user's live subscription from Stripe and projects it onto local state.
    ///
    /// <para><b>Never throws.</b> On any Stripe failure it logs and returns false, leaving the
    /// cached projection in place. Callers are user-facing paths, and an outage at Stripe must
    /// degrade to "slightly stale", never to "cannot use the product".</para>
    /// </summary>
    /// <param name="reason">Short tag for logs/metrics, e.g. "session", "paywall", "checkout".</param>
    /// <param name="minAge">
    /// Debounce. Skips the call when the last sync is newer than this, so a hammered gated
    /// endpoint cannot turn one decision per request into one Stripe call per request. Pass
    /// <see cref="TimeSpan.Zero"/> to force.
    /// </param>
    /// <returns>True when a fresh projection was applied.</returns>
    Task<bool> RefreshFromStripeAsync(
        int userId, string reason, TimeSpan? minAge = null, CancellationToken ct = default);

    /// <summary>
    /// Applies a snapshot Stripe already handed us (a webhook payload) without a further API
    /// call. Same mapping as <see cref="RefreshFromStripeAsync"/>, just a cheaper source.
    /// </summary>
    Task ProjectAsync(int userId, StripeSubscriptionSnapshot snapshot, CancellationToken ct = default);
}
