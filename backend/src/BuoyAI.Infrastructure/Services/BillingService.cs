using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Telemetry;
using BuoyAI.Infrastructure.Utilities;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for managing BuoyAI subscription billing (not to be confused with SubscriptionService for expense tracking)
/// </summary>
public class BillingService : IBillingService
{
    private readonly BuoyAIDbContext _context;
    private readonly IStripeService _stripeService;
    private readonly ISubscriptionSyncService _syncService;
    private readonly IEntitlementService _entitlements;
    private readonly IEmailService _emailService;
    private readonly IFamilyService _familyService;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BillingService> _logger;
    private readonly BuoyAiMetrics _metrics;

    private const string PricingPlansCacheKey = "PricingPlans";
    private static readonly TimeSpan PricingCacheDuration = TimeSpan.FromHours(1);

    public BillingService(
        BuoyAIDbContext context, 
        IStripeService stripeService,
        ISubscriptionSyncService syncService,
        IEntitlementService entitlements,
        IEmailService emailService,
        IFamilyService familyService,
        IConfiguration configuration,
        IMemoryCache cache,
        ILogger<BillingService> logger,
        BuoyAiMetrics metrics)
    {
        _context = context;
        _stripeService = stripeService;
        _syncService = syncService;
        _entitlements = entitlements;
        _emailService = emailService;
        _familyService = familyService;
        _configuration = configuration;
        _cache = cache;
        _logger = logger;
        _metrics = metrics;
    }

    #region User Retrieval

    public async Task<User?> GetUserByIdAsync(int userId)
    {
        return await _context.Users.FindAsync(userId);
    }

    #endregion

    #region Trial Management (Card-Upfront via Stripe)

    public async Task<bool> IsTrialActiveAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user?.IsTrialActive == true && user.TrialEndDate > DateTime.UtcNow;
    }

    public async Task<int> GetTrialDaysRemainingAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user?.IsTrialActive != true || user.TrialEndDate == null)
        {
            return 0;
        }

        var daysRemaining = (user.TrialEndDate.Value - DateTime.UtcNow).Days;
        return Math.Max(0, daysRemaining);
    }

    public async Task<bool> CanStartTrialAsync(int userId)
    {
        // Same two checks StartTrialAsync enforces: this account's flag and the per-email trial
        // history (a deleted-and-re-registered email must not be offered a trial it can't start).
        // Sign-up grants the trial, so this is only true for accounts created before that policy.
        var user = await _context.Users.FindAsync(userId);
        return user != null && !user.HasUsedTrial && !await TrialHistoryRecorder.HasUsedTrialAsync(_context, user.Email);
    }

    /// <summary>
    /// Ensures a Stripe customer exists for the user, creating one if necessary.
    /// Returns the Stripe customer ID.
    /// </summary>
    public async Task<string> EnsureStripeCustomerAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        if (!string.IsNullOrEmpty(user.StripeCustomerId))
        {
            return user.StripeCustomerId;
        }

        user.StripeCustomerId = await _stripeService.CreateCustomerAsync(user);
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created Stripe customer {CustomerId} for user {UserId}", 
            user.StripeCustomerId, userId);

        return user.StripeCustomerId;
    }

    /// <summary>
    /// Starts a trial with a pre-validated payment method (card-upfront).
    /// Creates a Stripe subscription with trial that auto-converts when trial ends.
    /// </summary>
    public async Task<StartTrialWithCardResult> StartTrialWithCardAsync(
        int userId, 
        string paymentMethodId, 
        SubscriptionTier tier, 
        SubscriptionBillingCycle billingCycle)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        // Block both this account's own prior trial and any prior trial on this email
        // (e.g. a since-deleted account) so delete + re-register can't yield a 2nd trial.
        if (user.HasUsedTrial || await TrialHistoryRecorder.HasUsedTrialAsync(_context, user.Email))
        {
            throw new InvalidOperationException("User has already used their trial period");
        }

        BillingHelpers.ValidatePurchasablePlan(tier, billingCycle);

        // Get the appropriate price ID
        var priceId = GetStripePriceId(tier, billingCycle);

        // Ensure Stripe customer exists
        var customerId = await EnsureStripeCustomerAsync(userId);

        // Create subscription with trial via Stripe
        var trialDays = _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);
        var stripeSubscriptionId = await _stripeService.CreateTrialSubscriptionAsync(
            customerId, priceId, paymentMethodId, trialDays);

        // Calculate trial end date
        var trialEndDate = DateTime.UtcNow.AddDays(trialDays);

        // Update user record
        user.HasUsedTrial = true;
        user.IsTrialActive = true;
        user.TrialStartDate = DateTime.UtcNow;
        user.TrialEndDate = trialEndDate;
        user.SubscriptionTier = tier;
        user.UpdatedAt = DateTime.UtcNow;

        // Create subscription record (status = trialing)
        var subscription = new UserSubscription
        {
            UserId = userId,
            Tier = tier,
            BillingCycle = billingCycle,
            Amount = CalculateSubscriptionAmount(tier, billingCycle),
            StartDate = DateTime.UtcNow,
            NextBillingDate = trialEndDate, // First charge after trial
            Status = UserSubscriptionStatus.Active, // Trialing is still "active"
            StripeCustomerId = customerId,
            StripeSubscriptionId = stripeSubscriptionId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        // Durable record so a future delete + re-register can't obtain another trial
        // (and can resume remaining days up to this end date).
        await TrialHistoryRecorder.RecordGrantAsync(_context, user.Email, userId, "card-trial", trialEndDate);

        _logger.LogInformation(
            "Started card-upfront trial for user {UserId}: tier={Tier}, subscriptionId={SubscriptionId}, trialEnds={TrialEnd}",
            userId, tier, stripeSubscriptionId, trialEndDate);

        _metrics.RecordTrialStarted(tier.ToString());

        // Send welcome email
        try
        {
            var firstName = user.FirstName ?? user.Username ?? "User";
            await _emailService.SendTrialStartedAsync(user.Email, firstName, trialDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send trial welcome email to user {UserId}", userId);
            // Don't fail the trial start if email fails
        }

        return new StartTrialWithCardResult
        {
            StripeSubscriptionId = stripeSubscriptionId,
            Tier = tier,
            BillingCycle = billingCycle,
            TrialEndDate = trialEndDate,
            SubscriptionStatus = "trialing"
        };
    }

    /// <summary>
    /// Starts a no-card free trial. No Stripe subscription is created and no card is
    /// collected, so nothing is ever charged. Premium access is granted for the trial
    /// window and expires automatically: GetUserTierAsync re-checks TrialEndDate on
    /// every call, so no scheduled job is needed. Deliberately does NOT set
    /// user.SubscriptionTier (that would persist Premium past the trial with nothing
    /// to reset it).
    /// </summary>
    public async Task<User> StartTrialAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId)
            ?? throw new ArgumentException("User not found", nameof(userId));

        // Block both this account's own prior trial and any prior trial on this email
        // (e.g. a since-deleted account) so delete + re-register can't yield a 2nd trial.
        if (user.HasUsedTrial || await TrialHistoryRecorder.HasUsedTrialAsync(_context, user.Email))
        {
            throw new InvalidOperationException("User has already used their trial period");
        }

        var trialDays = _configuration.GetValue<int>("Subscription:TrialPeriodDays", 14);
        var now = DateTime.UtcNow;
        var trialEndDate = now.AddDays(trialDays);

        user.HasUsedTrial = true;
        user.IsTrialActive = true;
        user.TrialStartDate = now;
        user.TrialEndDate = trialEndDate;
        // NOTE: SubscriptionTier is intentionally left as-is (null for a new user).
        // Trial access comes from GetUserTierAsync's IsTrialActive/TrialEndDate branch.
        user.UpdatedAt = now;

        await _context.SaveChangesAsync();

        // Durable record so a future delete + re-register can't obtain another trial.
        await TrialHistoryRecorder.RecordGrantAsync(_context, user.Email, userId, "no-card-trial", trialEndDate);

        _logger.LogInformation(
            "Started no-card trial for user {UserId}, trialEnds={TrialEnd}", userId, trialEndDate);
        _metrics.RecordTrialStarted(SubscriptionTier.Premium.ToString());

        // Send welcome email (best-effort)
        try
        {
            var firstName = user.FirstName ?? user.Username ?? "User";
            await _emailService.SendTrialStartedAsync(user.Email, firstName, trialDays);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send trial welcome email to user {UserId}", userId);
        }

        return user;
    }

    #endregion

    #region Subscription Management

    public async Task<UserSubscription?> GetUserSubscriptionAsync(int userId)
    {
        return await _context.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == UserSubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Returns the user's current subscription that still represents a live billing
    /// relationship — Active OR PastDue. Guards against creating a second subscription
    /// while the first is merely PastDue: GetUserSubscriptionAsync filters to Active only,
    /// so a past-due user would otherwise see "no plan", check out again, and be billed
    /// twice once the original recovers (dunning succeeds or they update their card).
    /// </summary>
    private async Task<UserSubscription?> GetActiveOrPastDueSubscriptionAsync(int userId)
    {
        return await _context.UserSubscriptions
            .Where(s => s.UserId == userId &&
                        (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<UserSubscription> CreateSubscriptionAsync(int userId, CreateBillingSubscriptionRequest request)
    {
        BillingHelpers.ValidatePurchasablePlan(request.Tier, request.BillingCycle);

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        // Check if user already has a live subscription (Active OR PastDue). Blocking
        // PastDue too prevents a second subscription (and a second charge) while the first
        // is recoverable.
        var existingSubscription = await GetActiveOrPastDueSubscriptionAsync(userId);
        if (existingSubscription != null)
        {
            throw new InvalidOperationException("User already has an active subscription");
        }

        // Ensure Stripe customer exists
        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            user.StripeCustomerId = await _stripeService.CreateCustomerAsync(user);
        }

        // Attach payment method to customer if provided
        if (!string.IsNullOrEmpty(request.PaymentMethodToken))
        {
            await _stripeService.AttachPaymentMethodAsync(user.StripeCustomerId, request.PaymentMethodToken);
            _logger.LogInformation("Attached payment method to customer {CustomerId} for user {UserId}", 
                user.StripeCustomerId, userId);
        }
        else
        {
            _logger.LogWarning("No payment method token provided for subscription creation for user {UserId}", userId);
        }

        // Calculate amount based on tier and billing cycle
        var amount = CalculateSubscriptionAmount(request.Tier, request.BillingCycle);

        // Create Stripe subscription
        var priceId = GetStripePriceId(request.Tier, request.BillingCycle);
        var stripeSubscriptionId = await _stripeService.CreateSubscriptionAsync(
            user.StripeCustomerId, priceId, request.PromoCode);

        // Create our subscription record
        var subscription = new UserSubscription
        {
            UserId = userId,
            Tier = request.Tier,
            BillingCycle = request.BillingCycle,
            Amount = amount,
            StartDate = DateTime.UtcNow,
            NextBillingDate = CalculateNextBillingDate(request.BillingCycle),
            Status = UserSubscriptionStatus.Active,
            StripeCustomerId = user.StripeCustomerId,
            StripeSubscriptionId = stripeSubscriptionId,
            PromoCode = request.PromoCode,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Update user subscription info
        user.SubscriptionTier = request.Tier;
        user.SubscriptionStartDate = DateTime.UtcNow;
        user.IsTrialActive = false; // End trial if active
        // NOTE: RequiresPaidPlan is intentionally NOT cleared here. PaymentBehavior is
        // "allow_incomplete", so the subscription can exist before the first charge
        // succeeds. The paywall is lifted only by the webhook once Stripe reports the
        // subscription active (HandleSubscriptionStatusChangeAsync).
        user.UpdatedAt = DateTime.UtcNow;

        _context.UserSubscriptions.Add(subscription);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created subscription for user {UserId} with tier {Tier}", 
            userId, request.Tier);
        _metrics.RecordSubscriptionCreated(request.Tier.ToString());

        // Update family members' inherited tier if user is a family owner
        try
        {
            await _familyService.UpdateFamilyMembersTierAsync(userId, request.Tier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update family members tier for user {UserId}", userId);
            // Don't fail the subscription creation if family tier update fails
        }

        // Send subscription confirmation email
        try
        {
            var firstName = user.FirstName ?? user.Username ?? "User";
            var oldTier = user.HasUsedTrial ? "Free Trial" : "Free";
            var newTier = request.Tier.ToString();
            await _emailService.SendSubscriptionChangedAsync(user.Email, firstName, oldTier, newTier);
            _logger.LogInformation("Sent subscription confirmation email to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription confirmation email to user {UserId}", userId);
            // Don't fail the subscription creation if email fails
        }

        return subscription;
    }

    public async Task<string> CreateBillingPortalSessionAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId)
            ?? throw new ArgumentException("User not found", nameof(userId));

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            throw new InvalidOperationException("No billing profile to manage yet.");
        }

        var baseUrl = (_configuration["App:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("App:BaseUrl is not configured; cannot build the billing portal return URL.");
        }
        // Mirror the checkout return-URL convention (hash routing unless overridden).
        var returnPrefix = _configuration["App:CheckoutReturnPrefix"] ?? "/#";
        var returnUrl = $"{baseUrl}{returnPrefix}/billing";

        return await _stripeService.CreateBillingPortalSessionAsync(user.StripeCustomerId, returnUrl);
    }

    public async Task<string> CreateCheckoutSessionAsync(int userId, SubscriptionTier tier, SubscriptionBillingCycle billingCycle, string? promoCode = null, string? platform = null)
    {
        BillingHelpers.ValidatePurchasablePlan(tier, billingCycle);

        var user = await _context.Users.FindAsync(userId)
            ?? throw new ArgumentException("User not found", nameof(userId));

        // Block checkout when a live subscription already exists (Active OR PastDue). A
        // past-due user must recover the existing subscription rather than start a second
        // one that would double-bill them.
        if (await GetActiveOrPastDueSubscriptionAsync(userId) != null)
        {
            throw new InvalidOperationException("User already has an active subscription");
        }

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            user.StripeCustomerId = await _stripeService.CreateCustomerAsync(user);
            await _context.SaveChangesAsync();
        }

        var priceId = GetStripePriceId(tier, billingCycle);
        var baseUrl = (_configuration["App:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            // Stripe requires absolute return URLs; fail clearly instead of letting Stripe
            // reject the session with an opaque error.
            throw new InvalidOperationException("App:BaseUrl is not configured; cannot build Checkout return URLs.");
        }
        // iOS uses hosted-Checkout link-out and returns via a Universal Link, which
        // matches on PATH (fragments are never sent), so the iOS return must be a
        // plain path URL (…/app/billing?checkout=…) that the app's AASA claims — not
        // the web hash route. "App:IosCheckoutReturnPrefix" (default "/app") controls it.
        // Web keeps its existing convention ("App:CheckoutReturnPrefix", default "/#").
        // The prefix is server-derived, never client-supplied, so no open-redirect risk.
        var returnPrefix = string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
            ? (_configuration["App:IosCheckoutReturnPrefix"] ?? "/app")
            : (_configuration["App:CheckoutReturnPrefix"] ?? "/#");
        var successUrl = $"{baseUrl}{returnPrefix}/billing?checkout=success";
        var cancelUrl = $"{baseUrl}{returnPrefix}/billing?checkout=cancel";

        var metadata = new Dictionary<string, string>
        {
            ["userId"] = userId.ToString(),
            ["tier"] = ((int)tier).ToString(),
            ["billingCycle"] = ((int)billingCycle).ToString(),
        };

        var url = await _stripeService.CreateCheckoutSessionAsync(
            user.StripeCustomerId!, priceId, successUrl, cancelUrl, userId.ToString(), metadata, promoCode,
            customerCountry: user.SignupCountry);

        _logger.LogInformation(
            "Created Checkout session for user {UserId}, tier {Tier}, cycle {Cycle}",
            userId, tier, billingCycle);
        return url;
    }

    public async Task ActivateSubscriptionFromCheckoutAsync(
        string? clientReferenceId, string? stripeSubscriptionId, string? stripeCustomerId,
        IDictionary<string, string>? metadata, string? stripeStatus = null, decimal? actualAmount = null)
    {
        if (string.IsNullOrEmpty(stripeSubscriptionId))
        {
            _logger.LogWarning("Checkout completed without a subscription id; ignoring");
            return;
        }

        // Only activate once the subscription is actually paid/active. For an
        // incomplete/past_due subscription we leave the paywall up and let the
        // status-change handler reconcile when (if) it becomes active.
        if (!string.IsNullOrEmpty(stripeStatus) && stripeStatus != "active" && stripeStatus != "trialing")
        {
            _logger.LogInformation(
                "Checkout subscription {SubscriptionId} has status {Status}; not activating yet",
                stripeSubscriptionId, stripeStatus);
            return;
        }

        // Idempotency: if we've already recorded this Stripe subscription, do nothing.
        if (await _context.UserSubscriptions.AnyAsync(s => s.StripeSubscriptionId == stripeSubscriptionId))
        {
            _logger.LogInformation("Checkout subscription {SubscriptionId} already recorded; skipping", stripeSubscriptionId);
            return;
        }

        // Resolve the user from client_reference_id (checkout.session.completed) or our
        // subscription metadata (customer.subscription.created). We intentionally do NOT
        // fall back to a Stripe-customer lookup: that signature only identifies
        // Checkout-originated subscriptions, so this never collides with the normal
        // /subscribe flow (whose subscriptions carry neither marker and create their own
        // UserSubscription row synchronously).
        int userId = 0;
        if (!int.TryParse(clientReferenceId, out userId) &&
            (metadata == null || !metadata.TryGetValue("userId", out var metaUserId) || !int.TryParse(metaUserId, out userId)))
        {
            _logger.LogInformation(
                "Subscription {SubscriptionId} has no Checkout user marker; not activating here",
                stripeSubscriptionId);
            return;
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            _logger.LogWarning("User {UserId} from Checkout not found", userId);
            return;
        }

        // Guard against a second live subscription for the same user. A past-due user can
        // reach a second checkout (GetUserSubscriptionAsync returns null for PastDue); if it
        // completes we'd insert a duplicate active row and bill them twice. If any
        // Active/PastDue subscription with a different Stripe id already exists, don't
        // activate this one. (The /subscribe and card-upfront-trial rows share this Stripe
        // id and are already handled by the idempotency check above.)
        var existingLive = await _context.UserSubscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId &&
                s.StripeSubscriptionId != stripeSubscriptionId &&
                (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue));
        if (existingLive != null)
        {
            _logger.LogWarning(
                "User {UserId} already has a live subscription {ExistingSubId}; not activating duplicate {SubscriptionId}",
                userId, existingLive.StripeSubscriptionId, stripeSubscriptionId);
            return;
        }

        var tier = metadata != null && metadata.TryGetValue("tier", out var tierStr) && int.TryParse(tierStr, out var tierVal)
            ? (SubscriptionTier)tierVal
            : SubscriptionTier.Standard;
        // Defense-in-depth: Admin/PremiumInherited are never purchasable. If a checkout
        // somehow carried one, clamp to Standard rather than granting it.
        if (tier != SubscriptionTier.Standard && tier != SubscriptionTier.Premium)
        {
            _logger.LogWarning("Checkout subscription {SubscriptionId} carried non-purchasable tier {Tier}; clamping to Standard",
                stripeSubscriptionId, tier);
            tier = SubscriptionTier.Standard;
        }
        var cycle = metadata != null && metadata.TryGetValue("billingCycle", out var cycleStr) && int.TryParse(cycleStr, out var cycleVal)
            && (cycleVal == (int)SubscriptionBillingCycle.Monthly || cycleVal == (int)SubscriptionBillingCycle.Yearly)
            ? (SubscriptionBillingCycle)cycleVal
            : SubscriptionBillingCycle.Monthly;

        // A Cloud checkout names the products it sold ("entitlements") and the interval instead
        // of a tier number. The tier is derived from the products, with the same rule the
        // projection uses, so the row is right from the first moment and the projection that
        // follows only confirms it.
        if (metadata != null && metadata.TryGetValue("entitlements", out var soldProducts))
        {
            var sold = ProductRules.Normalize(ProductEntitlements.Parse(soldProducts), 0);
            tier = ProductRules.LegacyTierFor(sold) ?? SubscriptionTier.Standard;
            if (metadata.TryGetValue("interval", out var soldInterval))
            {
                cycle = soldInterval == "year" ? SubscriptionBillingCycle.Yearly : SubscriptionBillingCycle.Monthly;
            }
        }

        // Prefer the amount actually charged by Stripe (reflects the real price and any
        // promotion code); fall back to the list price only if Stripe didn't supply one.
        var amount = actualAmount ?? CalculateSubscriptionAmount(tier, cycle);

        var now = DateTime.UtcNow;
        _context.UserSubscriptions.Add(new UserSubscription
        {
            UserId = userId,
            Tier = tier,
            BillingCycle = cycle,
            Amount = amount,
            StartDate = now,
            NextBillingDate = CalculateNextBillingDate(cycle),
            Status = UserSubscriptionStatus.Active,
            StripeCustomerId = stripeCustomerId ?? user.StripeCustomerId,
            StripeSubscriptionId = stripeSubscriptionId,
            CreatedAt = now,
            UpdatedAt = now,
        });

        user.SubscriptionTier = tier;
        user.SubscriptionStartDate = now;
        user.IsTrialActive = false;
        user.RequiresPaidPlan = false; // Plan chosen and paid — lift the paywall
        user.StripeCustomerId ??= stripeCustomerId;
        user.UpdatedAt = now;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when ((ex.InnerException as Npgsql.PostgresException)?.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            // The other webhook (checkout.session.completed vs customer.subscription.created)
            // won the race and inserted first; the unique index on StripeSubscriptionId
            // turned this into the no-op the AnyAsync check intended. ONLY a unique-violation
            // is a benign duplicate — any other DbUpdateException (NOT NULL/check-constraint,
            // decimal overflow, FK, connection drop) means the customer was charged but has no
            // subscription row, so it must NOT be swallowed.
            _logger.LogInformation(ex,
                "Concurrent activation for subscription {SubscriptionId} ignored (already recorded)",
                stripeSubscriptionId);
            return;
        }
        catch (DbUpdateException ex)
        {
            // A real persistence failure after a completed checkout: the customer paid but the
            // subscription did not save. Surface it loudly and rethrow so the webhook returns
            // non-2xx and Stripe retries (the AnyAsync idempotency pre-check makes retry safe).
            _metrics.RecordPaymentFailed("activation_persist_failure");
            _logger.LogError(ex,
                "Failed to persist checkout activation for subscription {SubscriptionId}; customer may be charged without an active subscription",
                stripeSubscriptionId);
            throw;
        }

        // A Cloud plan replaces a hosted trial. Left alive, the trial would keep firing "add a card"
        // prompts, and a card added to it would convert into a second paid subscription.
        if (metadata != null && metadata.ContainsKey("entitlements"))
        {
            await CancelHostedTrialsAsync(userId, stripeSubscriptionId);
        }

        _metrics.RecordSubscriptionCreated(tier.ToString());
        _logger.LogInformation(
            "Activated Checkout subscription {SubscriptionId} for user {UserId} (tier {Tier})",
            stripeSubscriptionId, userId, tier);

        try
        {
            await _familyService.UpdateFamilyMembersTierAsync(userId, tier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update family members tier for user {UserId} after Checkout", userId);
        }
    }

    private async Task CancelHostedTrialsAsync(int userId, string keepStripeSubscriptionId)
    {
        try
        {
            var trials = await _context.UserSubscriptions
                .Where(s => s.UserId == userId && s.Status == UserSubscriptionStatus.Trialing
                    && s.StripeSubscriptionId != null && s.StripeSubscriptionId != keepStripeSubscriptionId)
                .ToListAsync();
            foreach (var trial in trials)
            {
                trial.Status = UserSubscriptionStatus.Canceled;
                trial.EndDate = DateTime.UtcNow;
                trial.UpdatedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                try
                {
                    await _stripeService.CancelSubscriptionAsync(trial.StripeSubscriptionId!, immediately: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not cancel hosted trial {SubscriptionId} in Stripe after a Cloud purchase", trial.StripeSubscriptionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clean up hosted trials for user {UserId} after a Cloud purchase", userId);
        }
    }

    public async Task<UserSubscription> ChangeSubscriptionAsync(int userId, ChangeSubscriptionRequest request)
    {
        // Only validate the tier/cycle the caller actually wants to change to.
        BillingHelpers.ValidatePurchasablePlan(
            request.NewTier ?? SubscriptionTier.Standard,
            request.NewBillingCycle ?? SubscriptionBillingCycle.Monthly);

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        var subscription = await GetUserSubscriptionAsync(userId);
        
        // If user is on trial but has no paid subscription, create a new subscription instead
        if (subscription == null)
        {
            if (user.IsTrialActive)
            {
                _logger.LogInformation("User {UserId} is upgrading from trial to paid subscription", userId);
                
                // Convert trial to paid subscription
                var createRequest = new CreateBillingSubscriptionRequest
                {
                    Tier = request.NewTier ?? SubscriptionTier.Standard,
                    BillingCycle = request.NewBillingCycle ?? SubscriptionBillingCycle.Monthly,
                    PromoCode = request.PromoCode
                };
                
                // Note: CreateSubscriptionAsync requires a payment method token for new subscriptions
                // For trial users upgrading, we need to collect payment info
                // For now, throw a more helpful error
                throw new InvalidOperationException("Trial users must use the subscribe flow with payment information to upgrade");
            }
            
            throw new InvalidOperationException("User does not have an active subscription");
        }

        // Use current values if not specified
        var oldTier = subscription.Tier;
        var newTier = request.NewTier ?? subscription.Tier;
        var newBillingCycle = request.NewBillingCycle ?? subscription.BillingCycle;

        // Calculate new amount
        var newAmount = CalculateSubscriptionAmount(newTier, newBillingCycle);
        var newPriceId = GetStripePriceId(newTier, newBillingCycle);

        // Update Stripe subscription (with promo code if provided)
        await _stripeService.UpdateSubscriptionAsync(subscription.StripeSubscriptionId!, newPriceId, request.PromoCode);

        // Update our records
        subscription.Tier = newTier;
        subscription.BillingCycle = newBillingCycle;
        subscription.Amount = newAmount;
        subscription.NextBillingDate = CalculateNextBillingDate(newBillingCycle);
        subscription.UpdatedAt = DateTime.UtcNow;
        
        // Store promo code if provided
        if (!string.IsNullOrEmpty(request.PromoCode))
        {
            subscription.PromoCode = request.PromoCode;
        }

        user.SubscriptionTier = newTier;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Changed subscription for user {UserId} from {OldTier} to {NewTier}{PromoInfo}",
            userId, oldTier, newTier,
            string.IsNullOrEmpty(request.PromoCode) ? "" : $" with promo code {request.PromoCode}");

        // Emit the subscription-upgraded counter the Business dashboard breaks down by
        // from_plan/to_plan. Only record when the tier actually moved (a pure billing-cycle
        // change isn't an upgrade).
        if (newTier != oldTier)
        {
            _metrics.RecordSubscriptionUpgraded(oldTier.ToString(), newTier.ToString());
        }

        // Update family members' inherited tier if user is a family owner
        try
        {
            await _familyService.UpdateFamilyMembersTierAsync(userId, newTier);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update family members tier for user {UserId}", userId);
        }

        // Send subscription changed email
        try
        {
            var firstName = user.FirstName ?? user.Username ?? "User";
            await _emailService.SendSubscriptionChangedAsync(user.Email, firstName, oldTier.ToString(), newTier.ToString());
            _logger.LogInformation("Sent subscription change email to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription change email to user {UserId}", userId);
            // Don't fail the subscription change if email fails
        }

        return subscription;
    }

    public async Task CancelSubscriptionAsync(int userId, CancelSubscriptionRequest request)
    {
        var subscription = await GetUserSubscriptionAsync(userId);
        if (subscription == null)
        {
            throw new InvalidOperationException("User does not have an active subscription");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        var oldTier = subscription.Tier;

        // Cancel Stripe subscription
        await _stripeService.CancelSubscriptionAsync(subscription.StripeSubscriptionId!, request.CancelImmediately);

        // Update our records
        subscription.Status = UserSubscriptionStatus.Canceled;
        subscription.EndDate = request.CancelImmediately ? DateTime.UtcNow : subscription.NextBillingDate;
        subscription.UpdatedAt = DateTime.UtcNow;

        if (request.CancelImmediately)
        {
            user.SubscriptionTier = null; // Remove access immediately
            user.SubscriptionEndDate = DateTime.UtcNow;
            
            // Update family members' inherited tier immediately
            try
            {
                await _familyService.UpdateFamilyMembersTierAsync(userId, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update family members tier for cancelled user {UserId}", userId);
            }
        }
        else
        {
            user.SubscriptionEndDate = subscription.NextBillingDate; // Access until period end
            // Family members keep inherited access until period end. At period end Stripe
            // fires customer.subscription.deleted -> CancelSubscriptionByStripeIdAsync,
            // which nulls the owner's tier AND revokes members' inherited access.
        }

        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Cancelled subscription for user {UserId} (immediate: {Immediate})", 
            userId, request.CancelImmediately);
        _metrics.RecordSubscriptionCancelled(oldTier.ToString());

        // Send cancellation email
        try
        {
            var firstName = user.FirstName ?? user.Username ?? "User";
            await _emailService.SendSubscriptionChangedAsync(user.Email, firstName, oldTier.ToString(), "Cancelled");
            _logger.LogInformation("Sent subscription cancellation email to user {UserId}", userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send subscription cancellation email to user {UserId}", userId);
            // Don't fail the cancellation if email fails
        }
    }

    public async Task DeleteStripeCustomerAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null || string.IsNullOrEmpty(user.StripeCustomerId))
        {
            return; // Nothing to delete
        }

        var customerId = user.StripeCustomerId;

        // Delete the customer at Stripe. Deleting also cancels any remaining subscriptions,
        // but callers should cancel first so family-inheritance side effects run.
        var deleted = await _stripeService.DeleteCustomerAsync(customerId);
        if (!deleted)
        {
            // Another environment owns this customer, so nothing changed at Stripe. Keep the
            // local reference: it still points at a customer that genuinely exists, and
            // clearing it would silently mint a duplicate on the next provisioning pass.
            _logger.LogWarning(
                "Stripe customer {CustomerId} for user {UserId} was not deleted: it belongs to another environment. Local reference left intact.",
                customerId, userId);
            return;
        }

        // Clear the local reference so nothing tries to reuse a now-deleted customer.
        user.StripeCustomerId = null;
        user.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted Stripe customer {CustomerId} for user {UserId}", customerId, userId);
    }

    #endregion

    #region Billing and Payments

    public async Task<List<SubscriptionPayment>> GetBillingHistoryAsync(int userId)
    {
        return await _context.SubscriptionPayments
            .Include(p => p.UserSubscription)
            .Where(p => p.UserSubscription.UserId == userId)
            .OrderByDescending(p => p.PaymentDate)
            .ToListAsync();
    }

    public async Task<PaymentStatus> RetryPaymentAsync(int userId, int paymentId)
    {
        var payment = await _context.SubscriptionPayments
            .Include(p => p.UserSubscription)
            .FirstOrDefaultAsync(p => p.Id == paymentId && p.UserSubscription.UserId == userId);

        if (payment == null)
        {
            throw new ArgumentException("Payment not found", nameof(paymentId));
        }

        if (payment.Status != PaymentStatus.Failed)
        {
            throw new InvalidOperationException("Payment is not in a failed state");
        }

        // Get payment status from Stripe
        var status = await _stripeService.GetPaymentStatusAsync(payment.StripePaymentIntentId!);
        
        payment.Status = status;
        payment.UpdatedAt = DateTime.UtcNow;
        
        await _context.SaveChangesAsync();

        return status;
    }

    public async Task<UpdatePaymentMethodResult> UpdatePaymentMethodAsync(int userId, string paymentMethodToken)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            throw new ArgumentException("User not found", nameof(userId));
        }

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            throw new InvalidOperationException("User does not have a Stripe customer ID");
        }

        // Attach the new payment method to the customer
        await _stripeService.AttachPaymentMethodAsync(user.StripeCustomerId, paymentMethodToken);
        _logger.LogInformation("Updated payment method for user {UserId}", userId);

        // Try to pay any pending invoices
        var paidInvoices = false;
        try
        {
            paidInvoices = await _stripeService.PayPendingInvoicesAsync(user.StripeCustomerId);
            if (paidInvoices)
            {
                _logger.LogInformation("Paid pending invoices for user {UserId} after payment method update", userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pay pending invoices for user {UserId}, but payment method was updated", userId);
            // Don't throw - payment method was updated successfully
        }

        return new UpdatePaymentMethodResult
        {
            Success = true,
            PendingInvoicesPaid = paidInvoices
        };
    }

    #endregion

    #region Tier Validation and Access Control

    public Task<bool> CanAccessFeatureAsync(int userId, string featureName) =>
        _entitlements.HasFeatureAsync(userId, featureName);

    public Task<SubscriptionTier?> GetUserTierAsync(int userId) =>
        _entitlements.GetEffectiveTierAsync(userId);

    public async Task<bool> HasActiveSubscriptionOrTrialAsync(int userId)
    {
        if (await HasAccessFromProjectionAsync(userId)) return true;

        // About to deny. Re-confirm against Stripe first, then decide again.
        //
        // Refresh ONLY on denial, never on a grant. Two reasons: a grant is never made more
        // correct by an API call, and putting Stripe in the path of every allowed request
        // would add a round trip to the hot path and make a Stripe outage an outage here.
        // Denials are rare, and being wrong on one (turning away someone who just paid) is
        // the single most expensive mistake this code can make.
        //
        // The refresh is debounced inside the sync service, so hammering a gated endpoint
        // cannot turn one decision per request into one Stripe call per request.
        if (await _syncService.RefreshFromStripeAsync(userId, "paywall"))
        {
            return await HasAccessFromProjectionAsync(userId);
        }

        return false;
    }

    /// <summary>Access decision from the local projection alone, with no Stripe call.</summary>
    private async Task<bool> HasAccessFromProjectionAsync(int userId)
    {
        if (await IsTrialActiveAsync(userId)) return true;

        var subscription = await GetUserSubscriptionAsync(userId);
        return subscription?.Status == UserSubscriptionStatus.Active;
    }

    public async Task UpdateUserTierAsync(int userId, SubscriptionTier tier)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user != null)
        {
            user.SubscriptionTier = tier;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }
    }

    #endregion

    #region Promo Codes

    public async Task<bool> ValidatePromoCodeAsync(string promoCode)
    {
        return await _stripeService.ValidatePromoCodeAsync(promoCode);
    }

    public async Task<PromoCodeValidationResult> ValidatePromoCodeAsync(string promoCode, string priceId)
    {
        return await _stripeService.ValidatePromoCodeWithPriceAsync(promoCode, priceId);
    }

    /// <summary>
    /// Redeems a promotion code against the user's existing Stripe subscription.
    ///
    /// This replaced a stub that looked the user up, logged "Applied promo code", and applied
    /// nothing — it had no callers, which is the only reason nobody noticed.
    ///
    /// Discounts deliberately live only at Stripe. It owns the lifecycle (a discount expires
    /// and disappears on its own), so a local copy would be a second source of truth that goes
    /// stale silently — the same reasoning that made subscription status a projection.
    ///
    /// Returns null when the code is unusable for ANY reason: unknown, inactive, restricted,
    /// or the user has no subscription to attach it to. Callers must not distinguish those, or
    /// the redeem form becomes an oracle for which codes exist.
    /// </summary>
    public async Task<SubscriptionDiscountDto?> RedeemPromoCodeAsync(int userId, string promoCode)
    {
        if (string.IsNullOrWhiteSpace(promoCode))
        {
            return null;
        }

        var subscriptionId = await ResolveStripeSubscriptionIdAsync(userId);
        if (subscriptionId == null)
        {
            // Includes the pre-migration case: a locally granted trial that has no Stripe
            // subscription behind it yet. TrialReconciliationService attaches one within 30
            // minutes, so this is "try again shortly", not "your code is bad".
            _logger.LogInformation("User {UserId} tried to redeem a promo code with no Stripe subscription", userId);
            return null;
        }

        try
        {
            var discount = await _stripeService.ApplyPromotionCodeToSubscriptionAsync(subscriptionId, promoCode);
            if (discount == null)
            {
                _logger.LogInformation("Promo code redemption rejected for user {UserId}", userId);
                return null;
            }

            _logger.LogInformation(
                "User {UserId} redeemed a promo code on subscription {SubscriptionId}: {Description}",
                userId, subscriptionId, discount.Description);
            return discount;
        }
        catch (Exception ex)
        {
            // A Stripe failure must not surface as "invalid code" — that would tell the user
            // their perfectly good code is bad.
            _logger.LogError(ex, "Stripe rejected promo code application for user {UserId}", userId);
            throw;
        }
    }

    /// <summary>
    /// Applies a reward coupon directly, bypassing the promotion-code lookup.
    ///
    /// Separate from RedeemPromoCodeAsync on purpose: this is the SYSTEM granting something
    /// the user earned, not the user typing a code. There is no shareable string involved, so
    /// there is nothing to leak, forward, or brute-force. Returns null when the user has no
    /// subscription to attach it to, which the caller treats as "retry later".
    /// </summary>
    public async Task<SubscriptionDiscountDto?> ApplyRewardCouponAsync(int userId, string couponId)
    {
        var subscriptionId = await ResolveStripeSubscriptionIdAsync(userId);
        if (subscriptionId == null)
        {
            return null;
        }

        return await _stripeService.ApplyCouponToSubscriptionAsync(subscriptionId, couponId);
    }

    /// <summary>
    /// The discount currently applied to the user's subscription, or null. Display-only.
    /// </summary>
    public async Task<SubscriptionDiscountDto?> GetActiveDiscountAsync(int userId)
    {
        var subscriptionId = await ResolveStripeSubscriptionIdAsync(userId);
        return subscriptionId == null
            ? null
            : await _stripeService.GetSubscriptionDiscountAsync(subscriptionId);
    }

    /// <summary>
    /// The user's Stripe subscription id. Covers paid subscriptions and cardless trials
    /// alike, since trial provisioning writes its subscription to the same table.
    /// </summary>
    private async Task<string?> ResolveStripeSubscriptionIdAsync(int userId)
    {
        var local = await _context.UserSubscriptions
            .Where(s => s.UserId == userId && s.StripeSubscriptionId != null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.StripeSubscriptionId)
            .FirstOrDefaultAsync();

        if (!string.IsNullOrEmpty(local))
        {
            return local;
        }

        // Local row missing but Stripe may still have one — the same drift
        // TrialReconciliationService repairs. Ask Stripe rather than declaring the user
        // subscription-less and rejecting a valid code.
        var user = await _context.Users.FindAsync(userId);
        if (string.IsNullOrEmpty(user?.StripeCustomerId))
        {
            return null;
        }

        var live = await _stripeService.GetLiveSubscriptionAsync(user.StripeCustomerId);
        return live?.SubscriptionId;
    }

    public async Task<PricingPlansResponse> GetPricingPlansAsync()
    {
        // Try to get from cache first
        if (_cache.TryGetValue(PricingPlansCacheKey, out PricingPlansResponse? cachedResponse) && cachedResponse != null)
        {
            _logger.LogDebug("Returning cached pricing plans (cached at {FetchedAt})", cachedResponse.FetchedAt);
            return cachedResponse;
        }

        _logger.LogInformation("Fetching pricing plans from Stripe (cache miss or expired)");

        // Get all price IDs from configuration
        var priceIds = new[]
        {
            _configuration["Stripe:PriceIds:StandardMonthly"],
            _configuration["Stripe:PriceIds:StandardYearly"],
            _configuration["Stripe:PriceIds:PremiumMonthly"],
            _configuration["Stripe:PriceIds:PremiumYearly"]
        }.Where(id => !string.IsNullOrEmpty(id)).ToList();

        // Fetch actual prices from Stripe
        var stripePrices = await _stripeService.GetPricesAsync(priceIds!);
        var priceMap = stripePrices.ToDictionary(p => p.PriceId, p => p);

        var response = new PricingPlansResponse
        {
            FetchedAt = DateTime.UtcNow,
            Plans = new List<PricingPlanDto>()
        };

        // Standard tier
        var standardMonthlyId = _configuration["Stripe:PriceIds:StandardMonthly"] ?? "";
        var standardYearlyId = _configuration["Stripe:PriceIds:StandardYearly"] ?? "";
        var standardMonthlyPrice = priceMap.TryGetValue(standardMonthlyId, out var sm) ? sm.UnitAmount : 1000;
        var standardYearlyPrice = priceMap.TryGetValue(standardYearlyId, out var sy) ? sy.UnitAmount : 10000;

        response.Plans.Add(new PricingPlanDto
        {
            Tier = "Standard",
            Name = "Standard",
            Description = "Perfect for personal finance management",
            MonthlyPriceCents = standardMonthlyPrice,
            YearlyPriceCents = standardYearlyPrice,
            MonthlyPriceId = standardMonthlyId,
            YearlyPriceId = standardYearlyId,
            StoreProductIdMonthly = StoreProducts.IdFor(SubscriptionTier.Standard, SubscriptionBillingCycle.Monthly, k => _configuration[k]),
            StoreProductIdYearly = StoreProducts.IdFor(SubscriptionTier.Standard, SubscriptionBillingCycle.Yearly, k => _configuration[k]),
            Currency = priceMap.TryGetValue(standardMonthlyId, out var smCur) ? smCur.Currency : "usd",
            IsPopular = false,
            // What the plan card shows. Keep it to what the code actually gives this tier:
            // no plan gate exists on bank sync, budgets, categorization or subscriptions.
            Features = new[]
            {
                "Connect bank accounts with Plaid",
                "Budgets with count-up categories",
                "Automatic transaction categorization",
                "Subscription tracking",
                "AI insights",
                "Streaks, badges and Money Wrapped",
                "Download all your data (JSON or CSV), free on every plan"
            }
        });

        // Premium tier
        var premiumMonthlyId = _configuration["Stripe:PriceIds:PremiumMonthly"] ?? "";
        var premiumYearlyId = _configuration["Stripe:PriceIds:PremiumYearly"] ?? "";
        var premiumMonthlyPrice = priceMap.TryGetValue(premiumMonthlyId, out var pm) ? pm.UnitAmount : 1500;
        var premiumYearlyPrice = priceMap.TryGetValue(premiumYearlyId, out var py) ? py.UnitAmount : 15000;

        response.Plans.Add(new PricingPlanDto
        {
            Tier = "Premium",
            Name = "Premium",
            Description = "Advanced features for power users",
            MonthlyPriceCents = premiumMonthlyPrice,
            YearlyPriceCents = premiumYearlyPrice,
            MonthlyPriceId = premiumMonthlyId,
            YearlyPriceId = premiumYearlyId,
            StoreProductIdMonthly = StoreProducts.IdFor(SubscriptionTier.Premium, SubscriptionBillingCycle.Monthly, k => _configuration[k]),
            StoreProductIdYearly = StoreProducts.IdFor(SubscriptionTier.Premium, SubscriptionBillingCycle.Yearly, k => _configuration[k]),
            Currency = priceMap.TryGetValue(premiumMonthlyId, out var pmCur) ? pmCur.Currency : "usd",
            IsPopular = true,
            // Premium-only in code: AI chat and BuoyAI Connect (AIController, McpConnectToolService),
            // family sharing (FamilyService.EnsurePremiumSubscriptionAsync), higher AI limits and
            // models (AIUsageTracker, AIModelConfiguration).
            // Data export is free on every plan (listed under Standard), not Premium-only.
            Features = new[]
            {
                "Everything in Standard",
                "AI chat about your own money",
                "Connect Claude or ChatGPT to your budget",
                "Family sharing",
                "Higher AI limits and newer AI models",
                "Priority support"
            }
        });

        // Cache the response for 1 hour
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(PricingCacheDuration)
            .SetPriority(CacheItemPriority.High);
        
        _cache.Set(PricingPlansCacheKey, response, cacheOptions);
        _logger.LogInformation("Cached pricing plans for {Duration}", PricingCacheDuration);

        return response;
    }

    #endregion

    #region Private Helper Methods

    private static decimal CalculateSubscriptionAmount(SubscriptionTier tier, SubscriptionBillingCycle billingCycle)
    {
        var monthlyAmount = tier switch
        {
            SubscriptionTier.Standard => 10.00m,
            SubscriptionTier.Premium => 15.00m,
            SubscriptionTier.Admin => 0.00m, // Admin is free
            _ => throw new ArgumentException($"Invalid tier: {tier}")
        };

        return billingCycle switch
        {
            SubscriptionBillingCycle.Monthly => monthlyAmount,
            SubscriptionBillingCycle.Yearly => monthlyAmount * 10, // 2 months discount (10x instead of 12x)
            _ => throw new ArgumentException($"Invalid billing cycle: {billingCycle}")
        };
    }

    private static DateTime CalculateNextBillingDate(SubscriptionBillingCycle billingCycle)
    {
        return billingCycle switch
        {
            SubscriptionBillingCycle.Monthly => DateTime.UtcNow.AddMonths(1),
            SubscriptionBillingCycle.Yearly => DateTime.UtcNow.AddYears(1),
            _ => throw new ArgumentException($"Invalid billing cycle: {billingCycle}")
        };
    }

    private string GetStripePriceId(SubscriptionTier tier, SubscriptionBillingCycle billingCycle)
    {
        return BillingHelpers.GetStripePriceId(_configuration, tier, billingCycle);
    }

    #endregion

    #region Webhook Synchronization

    public async Task SyncSubscriptionFromStripeAsync(string stripeSubscriptionId)
    {
        try
        {
            // Find subscription by Stripe ID
            var subscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found in database", stripeSubscriptionId);
                return;
            }

            // In a real implementation, we would fetch the latest subscription details from Stripe
            // and update our database accordingly. For now, we'll just log the sync.
            _logger.LogInformation("Synced subscription {SubscriptionId} from Stripe", subscription.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing subscription {StripeSubscriptionId} from Stripe", stripeSubscriptionId);
            throw;
        }
    }

    public async Task HandleFailedPaymentAsync(string stripeSubscriptionId, string failureReason, string? stripeInvoiceId = null)
    {
        try
        {
            var subscription = await _context.UserSubscriptions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found", stripeSubscriptionId);
                return;
            }

            // Idempotency: Stripe retries the same invoice event on any non-2xx (e.g. a
            // notification failure downstream). Keying on the invoice id prevents a retry from
            // double-inserting the failed-payment row (which would also inflate the dunning
            // retry-attempt counter below and the failed-payment metric).
            if (!string.IsNullOrEmpty(stripeInvoiceId) &&
                await _context.SubscriptionPayments.AnyAsync(p =>
                    p.StripeInvoiceId == stripeInvoiceId && p.Status == PaymentStatus.Failed))
            {
                _logger.LogInformation(
                    "Failed payment for invoice {InvoiceId} already recorded; skipping duplicate insert.",
                    stripeInvoiceId);
                return;
            }

            // Count recent failed payments for retry attempt number
            var recentFailedPayments = await _context.SubscriptionPayments
                .Where(p => p.UserSubscriptionId == subscription.Id && 
                           p.Status == PaymentStatus.Failed && 
                           p.PaymentDate > DateTime.UtcNow.AddDays(-30))
                .CountAsync();
            var retryAttempt = recentFailedPayments + 1;

            // Create failed payment record
            var failedPayment = new SubscriptionPayment
            {
                UserSubscriptionId = subscription.Id,
                Amount = subscription.Amount,
                Status = PaymentStatus.Failed,
                PaymentDate = DateTime.UtcNow,
                FailureDate = DateTime.UtcNow,
                StripePaymentIntentId = null,
                StripeInvoiceId = stripeInvoiceId,
                FailureReason = failureReason,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.SubscriptionPayments.Add(failedPayment);

            // Update subscription status to PastDue
            subscription.Status = UserSubscriptionStatus.PastDue;
            subscription.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Record the metric only now that a failed-payment row was actually inserted (past
            // the StripeInvoiceId idempotency short-circuit above). Recording this in the webhook
            // controller instead would re-fire on every Stripe retry of an already-processed
            // invoice, inflating the failed-payment count without a corresponding DB row.
            _metrics.RecordPaymentFailed(failureReason);

            _logger.LogWarning("Recorded failed payment for subscription {SubscriptionId}, reason: {FailureReason}, attempt: {RetryAttempt}",
                subscription.Id, failureReason, retryAttempt);

            // Send payment failed email if user has email
            if (subscription.User != null && !string.IsNullOrEmpty(subscription.User.Email))
            {
                var planName = subscription.Tier.ToString();
                var nextRetryDate = retryAttempt < 4 ? DateTime.UtcNow.AddDays(3) : (DateTime?)null;
                
                await _emailService.SendPaymentFailedAsync(
                    subscription.User.Email,
                    subscription.User.FirstName ?? "Customer",
                    planName,
                    subscription.Amount,
                    "****", // We don't store card details - show masked
                    retryAttempt,
                    nextRetryDate
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling failed payment for subscription {StripeSubscriptionId}", stripeSubscriptionId);
            throw;
        }
    }

    public async Task HandleSuccessfulPaymentAsync(string stripeSubscriptionId, decimal amount, string? stripeInvoiceId = null)
    {
        try
        {
            var subscription = await _context.UserSubscriptions
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found", stripeSubscriptionId);
                return;
            }

            // Idempotency: if the pod is killed between this SaveChanges and the webhook's
            // processed-event marker, Stripe retries the same invoice event. Keying on the
            // invoice id keeps the payment insert (and the billing-history it drives) a no-op
            // on retry.
            if (!string.IsNullOrEmpty(stripeInvoiceId) &&
                await _context.SubscriptionPayments.AnyAsync(p =>
                    p.StripeInvoiceId == stripeInvoiceId && p.Status == PaymentStatus.Succeeded))
            {
                _logger.LogInformation(
                    "Successful payment for invoice {InvoiceId} already recorded; skipping duplicate insert.",
                    stripeInvoiceId);
                return;
            }

            // Create successful payment record
            var payment = new SubscriptionPayment
            {
                UserSubscriptionId = subscription.Id,
                Amount = amount,
                Status = PaymentStatus.Succeeded,
                PaymentDate = DateTime.UtcNow,
                StripeInvoiceId = stripeInvoiceId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.SubscriptionPayments.Add(payment);

            // Update subscription to Active if it was in PastDue status
            if (subscription.Status == UserSubscriptionStatus.PastDue)
            {
                subscription.Status = UserSubscriptionStatus.Active;
            }

            // Update next billing date
            subscription.NextBillingDate = CalculateNextBillingDate(subscription.BillingCycle);
            subscription.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Record the metric only now that a succeeded-payment row was actually inserted (past
            // the StripeInvoiceId idempotency short-circuit above). Recording this in the webhook
            // controller instead would re-fire on every Stripe retry of an already-processed
            // invoice, inflating the payment count/amount without a corresponding DB row.
            _metrics.RecordPaymentSucceeded(amount);

            _logger.LogInformation("Recorded successful payment of ${Amount} for subscription {SubscriptionId}",
                amount, subscription.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling successful payment for subscription {StripeSubscriptionId}", stripeSubscriptionId);
            throw;
        }
    }

    public async Task CancelSubscriptionByStripeIdAsync(string stripeSubscriptionId)
    {
        try
        {
            var subscription = await _context.UserSubscriptions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found", stripeSubscriptionId);
                return;
            }

            // Update subscription status
            subscription.Status = UserSubscriptionStatus.Canceled;
            subscription.EndDate = DateTime.UtcNow;
            subscription.UpdatedAt = DateTime.UtcNow;

            // An old subscription ending (a trial deleted after the customer bought a Cloud plan)
            // must not strip the access another live subscription still carries.
            var otherLive = await _context.UserSubscriptions.AnyAsync(s => s.UserId == subscription.UserId
                && s.Id != subscription.Id
                && (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue));
            if (otherLive)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation(
                    "Cancelled subscription {SubscriptionId} via webhook; user {UserId} has another live subscription so their tier is kept",
                    subscription.Id, subscription.UserId);
                return;
            }

            // Update user tier
            if (subscription.User != null)
            {
                subscription.User.SubscriptionTier = null;
                subscription.User.SubscriptionEndDate = DateTime.UtcNow;
                subscription.User.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            // The owner just lost their paid tier, so any family members holding
            // PremiumInherited must lose it too. Portal/dunning/period-end cancellations
            // all arrive here (customer.subscription.deleted) — this is the only place
            // that strips inherited access for them. See IFamilyService contract.
            try
            {
                await _familyService.UpdateFamilyMembersTierAsync(subscription.UserId, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to revoke family members' inherited tier after webhook cancellation for user {UserId}",
                    subscription.UserId);
            }

            _logger.LogInformation("Cancelled subscription {SubscriptionId} via webhook", subscription.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cancelling subscription {StripeSubscriptionId}", stripeSubscriptionId);
            throw;
        }
    }

    public async Task<int?> GetUserIdByStripeSubscriptionIdAsync(string stripeSubscriptionId)
    {
        var subscription = await _context.UserSubscriptions
            .Where(s => s.StripeSubscriptionId == stripeSubscriptionId)
            .Select(s => new { s.UserId })
            .FirstOrDefaultAsync();

        return subscription?.UserId;
    }

    public async Task<bool> HasProcessedStripeEventAsync(string eventId)
    {
        if (string.IsNullOrEmpty(eventId))
            return false;
        return await _context.ProcessedStripeEvents.AnyAsync(e => e.EventId == eventId);
    }

    public async Task MarkStripeEventProcessedAsync(string eventId, string eventType)
    {
        if (string.IsNullOrEmpty(eventId))
            return;

        // Re-check to avoid a redundant insert; the primary key is the final guard.
        if (await _context.ProcessedStripeEvents.AnyAsync(e => e.EventId == eventId))
            return;

        var entry = _context.ProcessedStripeEvents.Add(new ProcessedStripeEvent
        {
            EventId = eventId,
            EventType = eventType,
            ProcessedAt = DateTime.UtcNow
        });

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A concurrent delivery of the same event won the primary-key race — it is
            // already recorded, so detach our pending insert and treat this as a no-op
            // rather than failing the webhook.
            entry.State = EntityState.Detached;
            _logger.LogInformation("Stripe event {EventId} was recorded concurrently; skipping duplicate insert.", eventId);
        }
    }

    /// <summary>
    /// Handle Stripe webhook for trial ending soon (3 days before).
    /// Sends reminder email to user.
    /// </summary>
    /// <summary>
    /// A cardless trial lapsed: Stripe paused the subscription because the trial ended with
    /// no payment method. Mirrors that into local state so access checks stop granting the
    /// trial tier.
    ///
    /// Idempotent and safe out of order. Two guards matter: replaying the event must not
    /// re-close an already-closed trial into a different state, and a paused event arriving
    /// AFTER the customer converted (Stripe retries for up to three days) must not revoke a
    /// subscription they are now paying for.
    /// </summary>
    public async Task HandleTrialLapsedAsync(string stripeSubscriptionId)
    {
        var subscription = await _context.UserSubscriptions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

        if (subscription?.User == null)
        {
            _logger.LogWarning(
                "Subscription {StripeSubscriptionId} not found for trial lapse", stripeSubscriptionId);
            return;
        }

        // The customer already converted; a late/replayed pause must not take that away.
        if (subscription.Status == UserSubscriptionStatus.Active)
        {
            _logger.LogInformation(
                "Ignoring trial lapse for {StripeSubscriptionId}: subscription is already active",
                stripeSubscriptionId);
            return;
        }

        var user = subscription.User;
        user.IsTrialActive = false;
        // HasUsedTrial stays true — it is the lifetime marker, not a state flag.
        user.RequiresPaidPlan = true;
        user.SubscriptionTier = null;
        user.UpdatedAt = DateTime.UtcNow;

        subscription.Status = UserSubscriptionStatus.TrialExpired;
        subscription.EndDate = DateTime.UtcNow;
        subscription.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        // The trial's Premium coverage just ended, so any family members holding
        // PremiumInherited from this owner must lose it too — a cardless trial
        // PAUSES rather than deletes, so the deleted-webhook strip never runs
        // for this path. Without this, a lapsed trial's invitees kept inherited
        // Premium forever.
        try
        {
            await _familyService.UpdateFamilyMembersTierAsync(user.Id, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to revoke family members' inherited tier after trial lapse for user {UserId}",
                user.Id);
        }

        _logger.LogInformation(
            "Trial lapsed for user {UserId} (subscription {StripeSubscriptionId}); paywalled",
            user.Id, stripeSubscriptionId);
    }

    /// <summary>
    /// Stripe resumed a paused subscription after a payment method was added. Restores paid
    /// access. Idempotent.
    /// </summary>
    public async Task HandleSubscriptionResumedAsync(string stripeSubscriptionId)
    {
        var subscription = await _context.UserSubscriptions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

        if (subscription?.User == null)
        {
            _logger.LogWarning(
                "Subscription {StripeSubscriptionId} not found for resume", stripeSubscriptionId);
            return;
        }

        var user = subscription.User;
        user.RequiresPaidPlan = false;
        user.SubscriptionTier = subscription.Tier;
        user.UpdatedAt = DateTime.UtcNow;

        subscription.Status = UserSubscriptionStatus.Active;
        subscription.EndDate = null;
        subscription.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Resumed subscription {StripeSubscriptionId} for user {UserId}", stripeSubscriptionId, user.Id);
    }

    public async Task HandleTrialWillEndAsync(string stripeSubscriptionId, DateTime trialEnd)
    {
        try
        {
            var subscription = await _context.UserSubscriptions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription?.User == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found for trial_will_end", 
                    stripeSubscriptionId);
                return;
            }

            var daysRemaining = (int)Math.Ceiling((trialEnd - DateTime.UtcNow).TotalDays);
            var user = subscription.User;
            var firstName = user.FirstName ?? user.Username ?? "User";

            // Send trial ending email
            try
            {
                // A cardless trial's subscription row carries Amount = 0, because nothing is
                // owed while it is trialing. Quoting that in the "your trial is ending" email
                // would advertise the plan at $0.00/month, so fall back to the list price for
                // the tier when the stored amount is zero.
                var quotedPrice = subscription.Amount > 0
                    ? subscription.Amount
                    : CalculateSubscriptionAmount(subscription.Tier, subscription.BillingCycle);

                await _emailService.SendTrialEndingAsync(
                    user.Email,
                    firstName,
                    daysRemaining,
                    subscription.Tier.ToString(),
                    quotedPrice);
                
                _logger.LogInformation(
                    "Sent trial ending email to user {UserId}, {DaysRemaining} days remaining", 
                    user.Id, daysRemaining);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send trial ending email to user {UserId}", user.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling trial_will_end for subscription {StripeSubscriptionId}", 
                stripeSubscriptionId);
            throw;
        }
    }

    /// <summary>
    /// Handle Stripe webhook for subscription status changes.
    /// Updates local database to match Stripe state.
    /// </summary>
    public async Task HandleSubscriptionStatusChangeAsync(string stripeSubscriptionId, string status)
    {
        try
        {
            var subscription = await _context.UserSubscriptions
                .Include(s => s.User)
                .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId);

            if (subscription == null)
            {
                _logger.LogWarning("Subscription with Stripe ID {StripeSubscriptionId} not found for status change", 
                    stripeSubscriptionId);
                return;
            }

            var previousStatus = subscription.Status;

            // Map Stripe status to our status
            subscription.Status = status switch
            {
                "trialing" => UserSubscriptionStatus.Active, // Trialing is considered active
                "active" => UserSubscriptionStatus.Active,
                "past_due" => UserSubscriptionStatus.PastDue,
                "canceled" => UserSubscriptionStatus.Canceled,
                "unpaid" => UserSubscriptionStatus.PastDue,
                "incomplete" => UserSubscriptionStatus.PastDue,
                "incomplete_expired" => UserSubscriptionStatus.Canceled,
                _ => subscription.Status
            };

            // Update trial status based on subscription status
            if (subscription.User != null)
            {
                if (status == "trialing")
                {
                    subscription.User.IsTrialActive = true;
                }
                else if (status == "active" && subscription.User.IsTrialActive)
                {
                    // Trial just ended and converted to paid
                    subscription.User.IsTrialActive = false;
                    _logger.LogInformation("Trial converted to paid for user {UserId}", subscription.User.Id);
                    _metrics.RecordTrialConverted(subscription.Tier.ToString());
                }
                else if (status == "canceled")
                {
                    subscription.User.IsTrialActive = false;
                    subscription.User.SubscriptionTier = null;
                }

                // Any active/trialing subscription means the user has chosen a plan;
                // lift the returning-user paywall.
                if (status is "active" or "trialing")
                {
                    subscription.User.RequiresPaidPlan = false;
                }

                subscription.User.UpdatedAt = DateTime.UtcNow;
            }

            subscription.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // If the owner's subscription was canceled, revoke inherited access from any
            // family members (their PremiumInherited is gated purely on the persisted tier).
            if (status == "canceled")
            {
                try
                {
                    await _familyService.UpdateFamilyMembersTierAsync(subscription.UserId, null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to revoke family members' inherited tier after status change to canceled for user {UserId}",
                        subscription.UserId);
                }
            }

            _logger.LogInformation(
                "Updated subscription {SubscriptionId} status: {PreviousStatus} -> {NewStatus} (Stripe: {StripeStatus})",
                subscription.Id, previousStatus, subscription.Status, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling status change for subscription {StripeSubscriptionId}", 
                stripeSubscriptionId);
            throw;
        }
    }

    #endregion
}