namespace BuoyAI.Core.DTOs.Billing;

/// <summary>
/// Outcome of creating a cardless trial subscription in Stripe.
/// </summary>
/// <param name="SubscriptionId">Stripe subscription id, stored so webhooks can resolve the user.</param>
/// <param name="TrialEndsAt">
/// Stripe's own trial end, not a locally computed date. The reminder email and the lockout
/// are both driven by Stripe's clock, so our cached copy has to be the same value or the two
/// will disagree at the boundary.
/// </param>
/// <param name="Status">Stripe subscription status at creation, expected to be "trialing".</param>
public record CardlessTrialResult(string SubscriptionId, DateTime TrialEndsAt, string Status);
