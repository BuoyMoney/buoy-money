namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Creates the Stripe side of a free trial: a customer plus a cardless subscription in
/// <c>trialing</c> status.
///
/// Split out of <c>AuthService</c> because it is called from three registration paths and
/// from a reconciliation sweep, and because the failure contract matters: none of these
/// callers may fail if Stripe does.
/// </summary>
public interface ITrialProvisioningService
{
    /// <summary>
    /// Gives <paramref name="userId"/> a Stripe-backed trial of <paramref name="trialDays"/>.
    ///
    /// <para><b>Never throws.</b> Returns false if Stripe could not be reached or rejected the
    /// call. The user keeps the locally granted trial either way, and
    /// <see cref="ReconcileMissingTrialsAsync"/> retries later. Signing up must not depend on
    /// a third party being available.</para>
    ///
    /// <para>Idempotent: a user who already has a subscription row with a Stripe subscription
    /// id is left alone, so a retry cannot produce two subscriptions for one trial.</para>
    /// </summary>
    Task<bool> ProvisionStripeTrialAsync(int userId, int trialDays, CancellationToken ct = default);

    /// <summary>
    /// Gives <paramref name="userId"/> a Stripe customer and nothing else (a Cloud-only sign-up: Cloud billing
    /// needs the customer, the account has no trial). Reuses a customer Stripe already has for the email.
    /// <para><b>Never throws.</b> Returns false if Stripe could not be reached; the first Cloud checkout creates
    /// the customer then.</para>
    /// </summary>
    Task<bool> EnsureStripeCustomerAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// Finds users whose trial is locally active but has no Stripe subscription behind it
    /// (Stripe was down during signup) and provisions them. Returns how many were repaired.
    /// </summary>
    Task<int> ReconcileMissingTrialsAsync(int batchSize = 50, CancellationToken ct = default);

    /// <summary>
    /// Re-confirms trials that DO have a Stripe subscription but have not been checked
    /// recently, repairing any divergence through the projection. Catches the case gap-filling
    /// never could: a missed webhook leaving local state stale while Stripe moved on.
    /// Returns how many were re-confirmed.
    /// </summary>
    Task<int> ReconcileDriftedTrialsAsync(int batchSize = 50, CancellationToken ct = default);

    /// <summary>
    /// Closes trials that expired before ever reaching Stripe.
    /// </summary>
    /// <remarks>
    /// Neither other sweep can see these: gap-filling requires the trial to still be in date, and
    /// drift-checking requires a Stripe subscription to exist. Without this a lapsed local trial
    /// keeps <c>IsTrialActive</c> true forever with no tier, which reads as "not entitled" at every
    /// Premium gate while offering no way to become entitled.
    /// </remarks>
    Task<int> CloseLapsedLocalTrialsAsync(int batchSize = 50, CancellationToken ct = default);
}
