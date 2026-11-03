using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc />
public class SubscriptionSyncService : ISubscriptionSyncService
{
    private readonly BuoyAIDbContext _context;
    private readonly IStripeService _stripeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubscriptionSyncService> _logger;

    /// <summary>
    /// Default debounce. Short enough that a customer who just paid sees it almost at once,
    /// long enough that a hot gated endpoint cannot become a Stripe load test.
    /// </summary>
    private static readonly TimeSpan DefaultMinAge = TimeSpan.FromSeconds(60);

    public SubscriptionSyncService(
        BuoyAIDbContext context,
        IStripeService stripeService,
        IConfiguration configuration,
        ILogger<SubscriptionSyncService> logger)
    {
        _context = context;
        _stripeService = stripeService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> RefreshFromStripeAsync(
        int userId, string reason, TimeSpan? minAge = null, CancellationToken ct = default)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user == null) return false;

            // No Stripe customer means nothing to project. Notably this covers every user
            // whose trial predates Stripe-backed trials; their local state is all there is and
            // must be left exactly as it is.
            if (string.IsNullOrEmpty(user.StripeCustomerId)) return false;

            var window = minAge ?? DefaultMinAge;
            if (window > TimeSpan.Zero
                && user.LastStripeSyncAt is DateTime last
                && DateTime.UtcNow - last < window)
            {
                return false;
            }

            var snapshot = await _stripeService.GetLiveSubscriptionAsync(user.StripeCustomerId);

            // Record the attempt even when Stripe has nothing, so a customer with no
            // subscription does not re-query on every single denied request.
            user.LastStripeSyncAt = DateTime.UtcNow;

            if (snapshot == null)
            {
                // SAFETY RULE: "Stripe has no subscription" is NOT "revoke their trial".
                // Provisioning can legitimately be in flight, and pre-migration trials never
                // had a subscription at all. Revoking here would delete access we already
                // granted, on the strength of an absence.
                await _context.SaveChangesAsync(ct);
                _logger.LogDebug("No live Stripe subscription for user {UserId}; local state untouched", userId);
                return false;
            }

            await ApplyAsync(user, snapshot, ct);
            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Projected Stripe state for user {UserId} ({Reason}): status={Status}, trialEnd={TrialEnd}",
                userId, reason, snapshot.Status, snapshot.TrialEnd);
            return true;
        }
        catch (Exception ex)
        {
            // Swallowed on purpose. Callers are user-facing. A Stripe outage degrades this to
            // "the projection is stale", never to "the product is down".
            _logger.LogError(ex, "Stripe refresh failed for user {UserId} ({Reason}); serving cached state",
                userId, reason);
            return false;
        }
    }

    public async Task ProjectAsync(
        int userId, StripeSubscriptionSnapshot snapshot, CancellationToken ct = default)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null) return;

        await ApplyAsync(user, snapshot, ct);
        user.LastStripeSyncAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Local state, then the products, from one snapshot. The product rows are written in the
    /// same unit of work as the tier, so a reader never sees a tier from one Stripe state and
    /// products from another.
    /// </summary>
    private async Task ApplyAsync(User user, StripeSubscriptionSnapshot snapshot, CancellationToken ct)
    {
        var row = await LoadOrCreateSubscriptionRowAsync(user, snapshot, ct);
        var projected = snapshot.IsCatalog ? EntitlementProjector.Project(snapshot, _logger) : null;
        var handled = ApplyProjection(user, row, snapshot,
            await HasOtherLiveSubscriptionAsync(user.Id, snapshot.SubscriptionId, ct), projected);
        if (!handled) return;

        await ApplyEntitlementsAsync(user.Id, snapshot, projected, ct);
    }

    /// <summary>
    /// Rewrites this subscription's <c>UserEntitlements</c> rows to match the projection. Rows
    /// for products that are gone are deleted; the table is a cache of Stripe, not a history.
    /// A snapshot that did not carry its items (Items == null) is never allowed to delete.
    /// </summary>
    private async Task ApplyEntitlementsAsync(
        int userId, StripeSubscriptionSnapshot snapshot, IReadOnlyList<ProjectedEntitlement>? projected, CancellationToken ct)
    {
        if (snapshot.Items == null) return;

        var existing = await _context.UserEntitlements
            .Where(e => e.UserId == userId && e.Provider == SubscriptionProvider.Stripe && e.ExternalId == snapshot.SubscriptionId)
            .ToListAsync(ct);

        // Not a catalog subscription (hosted Standard or Premium): it must hold no product rows.
        if (!snapshot.IsCatalog) projected = [];
        // Pending first payment or an unknown status: leave what is there.
        if (projected == null) return;

        var now = DateTime.UtcNow;
        foreach (var gone in existing.Where(e => projected.All(p => p.Entitlement != e.Entitlement)))
        {
            _context.UserEntitlements.Remove(gone);
        }

        foreach (var p in projected)
        {
            var row = existing.FirstOrDefault(e => e.Entitlement == p.Entitlement);
            if (row == null)
            {
                row = new UserEntitlement
                {
                    UserId = userId,
                    Entitlement = p.Entitlement,
                    Provider = SubscriptionProvider.Stripe,
                    ExternalId = snapshot.SubscriptionId,
                    CreatedAt = now
                };
                _context.UserEntitlements.Add(row);
            }
            row.Quantity = p.Quantity;
            row.Status = p.Status;
            row.PaidThrough = p.PaidThrough;
            row.EndsAt = p.EndsAt;
            row.Founding = p.Founding;
            row.UpdatedAt = now;
        }
    }

    /// <summary>
    /// Another live subscription (a different Stripe subscription, or an App Store / Play one)
    /// outranks this one ending: someone who bought on the phone while their cardless web trial
    /// was still ticking, or who moved from a trial to a Cloud plan, must not lose access when
    /// the old subscription pauses or is cancelled.
    /// </summary>
    private Task<bool> HasOtherLiveSubscriptionAsync(int userId, string thisSubscriptionId, CancellationToken ct) =>
        _context.UserSubscriptions.AnyAsync(s => s.UserId == userId
            && (s.StripeSubscriptionId == null || s.StripeSubscriptionId != thisSubscriptionId)
            // A Stripe-provider row with no Stripe id is a half-written row, not a live plan.
            && (s.Provider != SubscriptionProvider.Stripe || s.StripeSubscriptionId != null)
            && (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue), ct);

    /// <summary>
    /// THE mapping from Stripe status to local state. Every path that learns something from
    /// Stripe ends up here, which is the whole point: one function to audit.
    ///
    /// Deliberately does not touch <c>HasUsedTrial</c>. That is a lifetime anti-abuse fact
    /// owned by us; letting a Stripe status flip it would hand anyone a second free trial by
    /// cancelling and re-subscribing.
    /// </summary>
    /// <returns>False when the status was not recognised and nothing was changed.</returns>
    private bool ApplyProjection(User user, UserSubscription row, StripeSubscriptionSnapshot snapshot,
        bool liveStoreSubscription = false, IReadOnlyList<ProjectedEntitlement>? projected = null)
    {
        var now = DateTime.UtcNow;

        // A Cloud (catalog) subscription's tier comes from the products on it; a hosted
        // Standard or Premium subscription's from its price, exactly as before.
        var catalog = snapshot.IsCatalog && projected != null;
        var tier = catalog
            ? ProductRules.LegacyTierFor(EntitlementProjector.ToAccess(projected!)) ?? row.Tier
            : ResolveTier(snapshot.PriceId) ?? row.Tier;
        if (catalog && projected!.Count > 0) ApplyCatalogRow(row, snapshot, tier);

        switch (snapshot.Status)
        {
            case "trialing":
                user.IsTrialActive = true;
                user.TrialStartDate ??= now;
                // Stripe's clock wins. This is the field that silently drifted before: a trial
                // extended in the Stripe dashboard never reached us, so access still expired on
                // our stale date.
                user.TrialEndDate = snapshot.TrialEnd ?? user.TrialEndDate;
                user.RequiresPaidPlan = false;
                row.Status = UserSubscriptionStatus.Trialing;
                row.NextBillingDate = snapshot.TrialEnd ?? row.NextBillingDate;
                row.EndDate = null;
                if (catalog && projected!.Count > 0) user.SubscriptionTier = tier;
                break;

            case "active":
                user.IsTrialActive = false;
                user.RequiresPaidPlan = false;
                user.SubscriptionTier = tier;
                row.Status = UserSubscriptionStatus.Active;
                row.EndDate = null;
                break;

            case "past_due":
            case "unpaid":
                // Keep access. Stripe is still retrying the card, and cutting someone off
                // mid-dunning turns a recoverable payment blip into a cancellation.
                user.RequiresPaidPlan = false;
                row.Status = UserSubscriptionStatus.PastDue;
                break;

            case "paused":
                // A cardless trial that ran out without a card.
                user.IsTrialActive = false;
                if (!liveStoreSubscription)
                {
                    user.RequiresPaidPlan = true;
                    user.SubscriptionTier = null;
                }
                row.Status = UserSubscriptionStatus.TrialExpired;
                row.EndDate ??= now;
                break;

            case "canceled":
            case "incomplete_expired":
                user.IsTrialActive = false;
                if (!liveStoreSubscription)
                {
                    user.RequiresPaidPlan = true;
                    user.SubscriptionTier = null;
                }
                row.Status = UserSubscriptionStatus.Canceled;
                row.EndDate ??= now;
                break;

            case "incomplete":
                // Awaiting a first payment. Not yet entitled, but not finished either, so
                // nothing is revoked and nothing is granted.
                row.Status = UserSubscriptionStatus.PastDue;
                break;

            default:
                // An unrecognised status is left alone rather than guessed at. Guessing here
                // would silently revoke or grant access on a Stripe API change.
                _logger.LogWarning(
                    "Unrecognised Stripe status '{Status}' for subscription {SubscriptionId}; local state untouched",
                    snapshot.Status, snapshot.SubscriptionId);
                return false;
        }

        row.UpdatedAt = now;
        user.UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Mirrors a Cloud subscription's shape onto its local row: what is billed, how often, and
    /// until when. The tier is the legacy one the older gates read.
    /// </summary>
    private static void ApplyCatalogRow(UserSubscription row, StripeSubscriptionSnapshot snapshot, SubscriptionTier tier)
    {
        row.Tier = tier;
        var items = snapshot.Items ?? [];
        row.Amount = items.Sum(i => (i.UnitAmountCents ?? 0) * Math.Max(i.Quantity, 1)) / 100m;
        var interval = items.Select(i => i.Interval).FirstOrDefault(i => i != null);
        row.BillingCycle = interval == "year" ? SubscriptionBillingCycle.Yearly : SubscriptionBillingCycle.Monthly;
        if (snapshot.CurrentPeriodEnd is DateTime end) row.NextBillingDate = end;
    }

    /// <summary>
    /// The local row mirroring this Stripe subscription, created if absent.
    ///
    /// Creating it matters for repair: if the row was lost (or the subscription was made
    /// outside our flow, e.g. by hand in the dashboard), this is what re-attaches it so the
    /// account stops looking like it has no subscription at all.
    /// </summary>
    private async Task<UserSubscription> LoadOrCreateSubscriptionRowAsync(
        User user, StripeSubscriptionSnapshot snapshot, CancellationToken ct)
    {
        var row = await _context.UserSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == snapshot.SubscriptionId, ct);
        if (row != null) return row;

        row = new UserSubscription
        {
            UserId = user.Id,
            Tier = snapshot.IsCatalog
                ? ProductRules.LegacyTierFor(EntitlementProjector.ToAccess(
                      EntitlementProjector.Project(snapshot) ?? [])) ?? SubscriptionTier.Standard
                : ResolveTier(snapshot.PriceId) ?? SubscriptionTier.Premium,
            BillingCycle = SubscriptionBillingCycle.Monthly,
            Amount = 0m,
            StartDate = DateTime.UtcNow,
            NextBillingDate = snapshot.TrialEnd ?? DateTime.UtcNow,
            Status = UserSubscriptionStatus.Trialing,
            StripeCustomerId = user.StripeCustomerId,
            StripeSubscriptionId = snapshot.SubscriptionId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _context.UserSubscriptions.Add(row);

        _logger.LogInformation(
            "Re-attached Stripe subscription {SubscriptionId} to user {UserId} (no local row existed)",
            snapshot.SubscriptionId, user.Id);
        return row;
    }

    /// <summary>Maps a Stripe price id back to a tier, or null when it is not one of ours.</summary>
    private SubscriptionTier? ResolveTier(string? priceId)
    {
        if (string.IsNullOrEmpty(priceId)) return null;

        foreach (var tier in new[] { SubscriptionTier.Standard, SubscriptionTier.Premium })
        {
            foreach (var cycle in new[] { SubscriptionBillingCycle.Monthly, SubscriptionBillingCycle.Yearly })
            {
                if (_configuration[$"Stripe:Products:{tier}:{(cycle == SubscriptionBillingCycle.Monthly ? "Monthly" : "Yearly")}"] == priceId)
                {
                    return tier;
                }
            }
        }
        return null;
    }
}
