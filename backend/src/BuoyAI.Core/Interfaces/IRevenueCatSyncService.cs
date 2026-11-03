using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// The single path by which App Store and Play subscription state (via RevenueCat) reaches
/// our database, the store-side twin of <see cref="ISubscriptionSyncService"/> for Stripe.
///
/// The stores own the subscription lifecycle. <c>UserSubscriptions</c> rows with a non-Stripe
/// <c>Provider</c> are a projection of RevenueCat's view of it, never a second source of
/// truth. Two sources feed the same projection: webhook events (push) and the subscriber
/// lookup (pull, used right after a purchase and on transfers so the client never waits on
/// webhook latency).
///
/// Web billing stays on Stripe; nothing here touches Stripe rows except to decide whether a
/// store expiry may revoke access (it may not while a Stripe subscription is live).
/// </summary>
public interface IRevenueCatSyncService
{
    /// <summary>True when a webhook authorization value is configured, i.e. the integration is on.</summary>
    bool IsConfigured { get; }

    /// <summary>Constant-time comparison of the request's Authorization header with the configured value.</summary>
    bool IsWebhookAuthorized(string? authorizationHeader);

    Task<bool> HasProcessedEventAsync(string eventId, CancellationToken ct = default);
    Task MarkEventProcessedAsync(string eventId, string eventType, CancellationToken ct = default);

    /// <summary>Applies one webhook event. Never throws for a data problem; the result says what happened.</summary>
    Task<RevenueCatApplyResult> ApplyEventAsync(RevenueCatEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Pulls the subscriber from RevenueCat's REST API and projects every store subscription
    /// it holds. Never throws: on any failure it logs and returns false, leaving the cached
    /// projection in place.
    /// </summary>
    Task<bool> RefreshFromRevenueCatAsync(int userId, string reason, CancellationToken ct = default);
}
