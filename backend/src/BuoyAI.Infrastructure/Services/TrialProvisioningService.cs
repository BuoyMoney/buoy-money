using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc />
public class TrialProvisioningService : ITrialProvisioningService
{
    private readonly BuoyAIDbContext _context;
    private readonly IStripeService _stripeService;
    private readonly ISubscriptionSyncService _syncService;
    private readonly IFamilyService _familyService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TrialProvisioningService> _logger;

    public TrialProvisioningService(
        BuoyAIDbContext context,
        IStripeService stripeService,
        ISubscriptionSyncService syncService,
        IFamilyService familyService,
        IConfiguration configuration,
        ILogger<TrialProvisioningService> logger)
    {
        _context = context;
        _stripeService = stripeService;
        _syncService = syncService;
        _familyService = familyService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Which plan a free trial previews. Trials have always granted Premium-level access, so
    /// the Stripe subscription is created against the Premium monthly price. Nothing is
    /// charged while it is trialing.
    /// </summary>
    private SubscriptionTier TrialTier =>
        _configuration.GetValue<SubscriptionTier?>("Subscription:TrialTier") ?? SubscriptionTier.Premium;

    public async Task<bool> ProvisionStripeTrialAsync(int userId, int trialDays, CancellationToken ct = default)
    {
        if (trialDays <= 0) return false;

        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user == null)
            {
                _logger.LogWarning("Cannot provision trial: user {UserId} not found", userId);
                return false;
            }

            // First idempotency guard: our own row. Cheap, and covers the common case.
            var alreadyProvisioned = await _context.UserSubscriptions
                .AnyAsync(s => s.UserId == userId && s.StripeSubscriptionId != null, ct);
            if (alreadyProvisioned)
            {
                _logger.LogDebug("User {UserId} already has a Stripe subscription; skipping", userId);
                return true;
            }

            var customerId = await EnsureCustomerAsync(user, ct);

            // Second idempotency guard: STRIPE's own state. This is the one that matters.
            //
            // The guard above only knows what our database knows, and there is a window where
            // those disagree: the Stripe subscription is created, then SaveChangesAsync fails.
            // Stripe now has a subscription that we have no row for, so the check above passes
            // on the next attempt and we create a SECOND one. The customer converts and is
            // billed twice.
            //
            // Asking Stripe closes that window. If a live subscription already exists we adopt
            // it instead of creating another, which also repairs the orphan.
            var existing = await _stripeService.GetLiveSubscriptionAsync(customerId);
            if (existing != null)
            {
                _logger.LogWarning(
                    "Stripe already has live subscription {SubscriptionId} for user {UserId}; adopting it instead of creating a duplicate",
                    existing.SubscriptionId, userId);

                await _syncService.ProjectAsync(userId, existing, ct);
                return true;
            }

            var result = await _stripeService.CreateCardlessTrialSubscriptionAsync(
                customerId, TrialTier, trialDays);

            _context.UserSubscriptions.Add(new UserSubscription
            {
                UserId = userId,
                Tier = TrialTier,
                BillingCycle = SubscriptionBillingCycle.Monthly,
                Amount = 0m, // nothing is owed during a trial
                StartDate = DateTime.UtcNow,
                NextBillingDate = result.TrialEndsAt,
                Status = UserSubscriptionStatus.Trialing,
                StripeCustomerId = customerId,
                StripeSubscriptionId = result.SubscriptionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });

            // Take Stripe's trial end as authoritative. Stripe drives both the three-day
            // reminder and the pause, so a locally computed date that differs by even a few
            // seconds would let the two disagree about when access actually stops.
            user.TrialEndDate = result.TrialEndsAt;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Provisioned {TrialDays}-day cardless Stripe trial for user {UserId} ({SubscriptionId}, status={Status})",
                trialDays, userId, result.SubscriptionId, result.Status);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed. The caller is a registration path; a Stripe outage must
            // not stop someone creating an account. The local trial fields were already set,
            // so access works, and ReconcileMissingTrialsAsync will attach Stripe later.
            _logger.LogError(ex,
                "Stripe trial provisioning failed for user {UserId}; local trial stands, will reconcile", userId);
            return false;
        }
    }

    public async Task<bool> EnsureStripeCustomerAsync(int userId, CancellationToken ct = default)
    {
        try
        {
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user == null)
            {
                _logger.LogWarning("Cannot create a Stripe customer: user {UserId} not found", userId);
                return false;
            }

            await EnsureCustomerAsync(user, ct);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately swallowed, like the trial: a Stripe outage must not stop a sign-up.
            _logger.LogError(ex, "Stripe customer creation failed for user {UserId}; Cloud checkout will create it", userId);
            return false;
        }
    }

    public async Task<int> ReconcileMissingTrialsAsync(int batchSize = 50, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Locally trialing, still in date, but nothing in Stripe backing it.
        var orphans = await _context.Users
            .Where(u => u.IsTrialActive
                        && u.TrialEndDate != null
                        && u.TrialEndDate > now
                        && !_context.UserSubscriptions.Any(s => s.UserId == u.Id && s.StripeSubscriptionId != null))
            .OrderBy(u => u.Id)
            .Take(batchSize)
            .Select(u => new { u.Id, u.TrialEndDate })
            .ToListAsync(ct);

        if (orphans.Count == 0) return 0;

        _logger.LogInformation("Reconciling {Count} trial(s) with no Stripe subscription", orphans.Count);

        var repaired = 0;
        foreach (var orphan in orphans)
        {
            if (ct.IsCancellationRequested) break;

            // Provision only the days that actually remain, so a trial that sat un-provisioned
            // for a week is not silently extended back to its full original length.
            var remaining = (int)Math.Ceiling((orphan.TrialEndDate!.Value - DateTime.UtcNow).TotalDays);
            if (remaining <= 0) continue;

            if (await ProvisionStripeTrialAsync(orphan.Id, remaining, ct)) repaired++;
        }

        return repaired;
    }

    public async Task<int> CloseLapsedLocalTrialsAsync(int batchSize = 50, CancellationToken ct = default)
    {
        // The third gap. Gap-filling only looks at trials still in date; drift-checking only at
        // trials WITH a Stripe subscription. A trial that expired before it ever reached Stripe
        // falls through both, and nothing else ever revisits it — so IsTrialActive stays true
        // forever against a date months past, the tier stays null, and every Premium gate answers
        // 403 with no route to upgrade. Two production users sat in exactly that limbo.
        var now = DateTime.UtcNow;

        var lapsed = await _context.Users
            .Where(u => u.IsTrialActive
                        && u.TrialEndDate != null
                        && u.TrialEndDate <= now
                        && !u.IsDeleted
                        && !_context.UserSubscriptions.Any(s => s.UserId == u.Id && s.StripeSubscriptionId != null))
            .OrderBy(u => u.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (lapsed.Count == 0) return 0;

        foreach (var user in lapsed)
        {
            user.IsTrialActive = false;

            // Same end state Stripe's `paused` handler writes, so a locally-granted trial and a
            // Stripe-backed one lapse into the identical shape.
            //
            // Guarded on tier, deliberately: an operator (Admin) or someone already holding a
            // paid tier must not be handed a blocking paywall just because a stale trial flag
            // was still set on their row.
            if (user.SubscriptionTier == null)
            {
                user.RequiresPaidPlan = true;
            }

            user.UpdatedAt = now;
        }

        await _context.SaveChangesAsync(ct);

        // Same family strip the webhook paths perform: these owners' trial
        // Premium just ended, so their invitees' PremiumInherited goes too.
        foreach (var user in lapsed)
        {
            try
            {
                await _familyService.UpdateFamilyMembersTierAsync(user.Id, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to revoke family members' inherited tier for lapsed trial user {UserId}",
                    user.Id);
            }
        }

        _logger.LogInformation("Closed {Count} lapsed local trial(s) that never reached Stripe", lapsed.Count);
        return lapsed.Count;
    }

    public async Task<int> ReconcileDriftedTrialsAsync(int batchSize = 50, CancellationToken ct = default)
    {
        // The other half of the job. Filling gaps only ever found trials with NO Stripe
        // subscription; a trial that HAS one and has since diverged was invisible forever.
        //
        // Targets the population where drift actually costs something: locally still trialing,
        // Stripe subscription attached, and not confirmed recently. A missed `paused` webhook
        // lands here.
        var staleBefore = DateTime.UtcNow.AddHours(-6);

        var candidates = await _context.Users
            .Where(u => u.IsTrialActive
                        && u.StripeCustomerId != null
                        && (u.LastStripeSyncAt == null || u.LastStripeSyncAt < staleBefore)
                        && _context.UserSubscriptions.Any(s => s.UserId == u.Id && s.StripeSubscriptionId != null))
            .OrderBy(u => u.LastStripeSyncAt ?? DateTime.MinValue)
            .Take(batchSize)
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var checked_ = 0;
        foreach (var userId in candidates)
        {
            if (ct.IsCancellationRequested) break;

            // minAge zero: this IS the staleness check, so do not let the debounce skip it.
            if (await _syncService.RefreshFromStripeAsync(userId, "drift-sweep", TimeSpan.Zero, ct))
            {
                checked_++;
            }
        }

        _logger.LogInformation("Drift sweep re-confirmed {Count} trial(s) against Stripe", checked_);
        return checked_;
    }

    /// <summary>
    /// Returns the user's Stripe customer id, reusing an existing customer wherever possible.
    /// Checks our own column first, then Stripe by email: a reconciliation retry after a
    /// partial failure could otherwise mint a second customer for the same person, which
    /// shows up in Stripe's reporting as two separate customers who each half-signed-up.
    /// </summary>
    private async Task<string> EnsureCustomerAsync(User user, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(user.StripeCustomerId)) return user.StripeCustomerId;

        var existing = await _stripeService.FindCustomerIdByEmailAsync(user.Email);
        user.StripeCustomerId = existing ?? await _stripeService.CreateCustomerAsync(user);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return user.StripeCustomerId;
    }
}
