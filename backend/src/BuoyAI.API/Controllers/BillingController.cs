using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Enums;
using BuoyAI.Core.DTOs.Billing;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Controller for managing BuoyAI subscription billing
/// Note: Different from SubscriptionsController which handles user expense subscriptions
/// </summary>
[Authorize]
[BuoyAI.API.Filters.HostedOnly] // self-host: 404
public class BillingController : BaseController
{
    private readonly IBillingService _billingService;
    private readonly IEntitlementService _entitlements;
    private readonly ISubscriptionSyncService _syncService;
    private readonly IStripeService _stripeService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IBillingService billingService,
        IEntitlementService entitlements,
        ISubscriptionSyncService syncService,
        IStripeService stripeService,
        IConfiguration configuration,
        ILogger<BillingController> logger)
    {
        _billingService = billingService;
        _entitlements = entitlements;
        _syncService = syncService;
        _stripeService = stripeService;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Public Stripe configuration needed by the client to initialize Stripe.js /
    /// flutter_stripe (the publishable key is not secret). Anonymous so the client can
    /// initialize before authenticating.
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult GetBillingConfig()
    {
        return Ok(new { publishableKey = _configuration["Stripe:PublishableKey"] ?? string.Empty });
    }

    #region Trial Endpoints

    /// <summary>
    /// Get trial status and days remaining
    /// </summary>
    [HttpGet("trial/status")]
    public async Task<IActionResult> GetTrialStatus()
    {
        var userId = GetCurrentUserId();

        // Explicit refresh point. This is the screen someone opens when they think their
        // billing state is wrong, so it is exactly where a stale projection is least
        // acceptable and where the extra round trip is most justified. Debounced, and a
        // Stripe failure just serves the cached answer.
        await _syncService.RefreshFromStripeAsync(userId, "billing-status");

        var isActive = await _billingService.IsTrialActiveAsync(userId);
        var daysRemaining = await _billingService.GetTrialDaysRemainingAsync(userId);
        var canStart = await _billingService.CanStartTrialAsync(userId);

        return Ok(new {
            IsTrialActive = isActive,
            DaysRemaining = daysRemaining,
            CanStartTrial = canStart
        });
    }

    /// <summary>
    /// Create a SetupIntent for collecting payment method (card-upfront trial).
    /// Returns clientSecret for Stripe Elements on frontend.
    /// </summary>
    [HttpPost("trial/setup-intent")]
    public async Task<IActionResult> CreateTrialSetupIntent([FromBody] CreateSetupIntentRequest request)
    {
        var userId = GetCurrentUserId();

        // Verify user can start trial
        if (!await _billingService.CanStartTrialAsync(userId))
        {
            return BadRequest(new { Message = "Trial not available for this user" });
        }

        // Get or create Stripe customer
        var customerId = await _billingService.EnsureStripeCustomerAsync(userId);

        // Create SetupIntent for card collection
        var result = await _stripeService.CreateSetupIntentAsync(customerId);

        _logger.LogInformation("Created SetupIntent for user {UserId} trial signup", userId);

        return Ok(new
        {
            ClientSecret = result.ClientSecret,
            SetupIntentId = result.SetupIntentId,
            CustomerId = customerId,
            Tier = request.Tier?.ToString() ?? "Standard",
            Message = "Use clientSecret with Stripe Elements to collect card. Your card will not be charged until the free trial ends."
        });
    }

    /// <summary>
    /// Start trial with pre-validated payment method (card-upfront).
    /// Creates a Stripe subscription with the configured free trial (Subscription:TrialPeriodDays) that auto-converts.
    /// </summary>
    [HttpPost("trial/start-with-card")]
    public async Task<IActionResult> StartTrialWithCard([FromBody] StartTrialWithCardRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();

            // Verify user can start trial
            if (!await _billingService.CanStartTrialAsync(userId))
            {
                return BadRequest(new { Message = "Trial not available for this user" });
            }

            if (string.IsNullOrEmpty(request.PaymentMethodId))
            {
                return BadRequest(new { Message = "Payment method is required" });
            }

            // Start trial with Stripe-managed subscription
            var result = await _billingService.StartTrialWithCardAsync(
                userId,
                request.PaymentMethodId,
                request.Tier ?? SubscriptionTier.Standard,
                request.BillingCycle ?? SubscriptionBillingCycle.Monthly);

            _logger.LogInformation(
                "Started card-upfront trial for user {UserId}, subscription {SubscriptionId}",
                userId, result.StripeSubscriptionId);

            return Ok(new
            {
                Message = "Trial started successfully. Your card will be charged when the free trial ends.",
                SubscriptionId = result.StripeSubscriptionId,
                TrialEnd = result.TrialEndDate,
                Tier = result.Tier.ToString(),
                WillAutoConvert = true
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Trial with card failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Start a no-card free trial. No card is collected and nothing is ever charged;
    /// Premium access is granted for the trial window and expires automatically.
    /// </summary>
    [HttpPost("trial/start")]
    public async Task<IActionResult> StartTrial()
    {
        var userId = GetCurrentUserId();

        if (!await _billingService.CanStartTrialAsync(userId))
        {
            return BadRequest(new { Message = "Trial not available for this user" });
        }

        try
        {
            var user = await _billingService.StartTrialAsync(userId);

            _logger.LogInformation("Started no-card trial for user {UserId}", userId);

            // Return the trial as a subscription-shaped object so the client's
            // CurrentSubscription parser can consume it (mirrors GET /billing/current).
            return Ok(new
            {
                id = 0,
                stripeSubscriptionId = (string?)null,
                tier = (int)SubscriptionTier.Premium, // Trial gives Premium access
                interval = (int)SubscriptionBillingCycle.Monthly,
                status = (int)UserSubscriptionStatus.Trialing,
                currentPeriodStart = user.TrialStartDate?.ToString("o"),
                currentPeriodEnd = user.TrialEndDate?.ToString("o"),
                cancelAtPeriodEnd = false,
                trialEnd = user.TrialEndDate?.ToString("o"),
                amount = 0m
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Trial start failed for user {UserId}", userId);
            return BadRequest(new { Message = ex.Message });
        }
    }

    #endregion

    #region Subscription Endpoints

    /// <summary>
    /// Get available subscription plans with prices fetched from Stripe
    /// </summary>
    [HttpGet("plans")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)] // Cache for 1 hour
    public async Task<IActionResult> GetSubscriptionPlans()
    {
        try
        {
            var pricingPlans = await _billingService.GetPricingPlansAsync();
            return Ok(pricingPlans);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching pricing plans from Stripe");
            // Return fallback pricing if Stripe fetch fails
            return Ok(new
            {
                FetchedAt = DateTime.UtcNow,
                Plans = new[]
                {
                    new
                    {
                        Tier = "Standard",
                        Name = "Standard",
                        Description = "Perfect for personal finance management",
                        MonthlyPriceCents = 1000L,
                        YearlyPriceCents = 10000L,
                        MonthlyPriceId = "",
                        YearlyPriceId = "",
                        Currency = "usd",
                        IsPopular = false,
                        Features = new[] {
                            "Connect bank accounts with Plaid",
                            "Basic AI insights",
                            "Transaction tracking",
                            "Budget management",
                            "Subscription tracking",
                            "Download all your data (JSON or CSV)"
                        }
                    },
                    new
                    {
                        Tier = "Premium",
                        Name = "Premium",
                        Description = "Advanced features for power users",
                        MonthlyPriceCents = 1500L,
                        YearlyPriceCents = 15000L,
                        MonthlyPriceId = "",
                        YearlyPriceId = "",
                        Currency = "usd",
                        IsPopular = true,
                        Features = new[] {
                            "Everything in Standard",
                            "Advanced AI insights (50/hour)",
                            "Priority support",
                            "Advanced analytics"
                        }
                    }
                }
            });
        }
    }

    /// <summary>
    /// Get user's current subscription
    /// </summary>
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrentSubscription()
    {
        return Ok(await BuildCurrentSubscriptionAsync(GetCurrentUserId()));
    }

    /// <summary>
    /// Called by the app right after a store purchase or restore: pulls the subscriber from
    /// RevenueCat so the plan is active before the webhook lands, then returns the same shape
    /// as GET current. Returns the cached state (never fails the purchase) when RevenueCat is
    /// unreachable; the webhook will catch up.
    /// </summary>
    [HttpPost("revenuecat/sync")]
    public async Task<IActionResult> SyncStorePurchase([FromServices] IRevenueCatSyncService revenueCat)
    {
        var userId = GetCurrentUserId();
        var refreshed = await revenueCat.RefreshFromRevenueCatAsync(userId, "client-sync");
        _logger.LogInformation("Store purchase sync for user {UserId}: refreshed={Refreshed}", userId, refreshed);
        return Ok(await BuildCurrentSubscriptionAsync(userId));
    }

    private async Task<object?> BuildCurrentSubscriptionAsync(int userId)
    {
        var subscription = await _billingService.GetUserSubscriptionAsync(userId);

        if (subscription != null)
        {
            // Return paid subscription data
            return new {
                id = subscription.Id,
                stripeSubscriptionId = subscription.StripeSubscriptionId,
                provider = (int)subscription.Provider,
                tier = (int)subscription.Tier,
                interval = (int)subscription.BillingCycle,
                status = (int)subscription.Status,
                currentPeriodStart = subscription.StartDate.ToString("o"),
                // A store row with an EndDate while still Active is "auto-renew off, access until then".
                currentPeriodEnd = (subscription.EndDate ?? (subscription.Provider != SubscriptionProvider.Stripe ? subscription.NextBillingDate : (DateTime?)null))?.ToString("o"),
                cancelAtPeriodEnd = subscription.Provider != SubscriptionProvider.Stripe && subscription.EndDate.HasValue,
                trialEnd = (string?)null,
                amount = subscription.Amount
            };
        }

        // Check if user has an active trial
        var isTrialActive = await _billingService.IsTrialActiveAsync(userId);
        if (isTrialActive)
        {
            var user = await _billingService.GetUserByIdAsync(userId);
            if (user != null)
            {
                // Return trial as a "subscription" so frontend shows it properly
                return new {
                    id = 0, // No subscription ID for trial
                    stripeSubscriptionId = (string?)null,
                    provider = (int)SubscriptionProvider.Stripe,
                    tier = (int)SubscriptionTier.Premium, // Trial gives Premium access
                    interval = (int)SubscriptionBillingCycle.Monthly, // Display as monthly
                    status = (int)UserSubscriptionStatus.Trialing,
                    currentPeriodStart = user.TrialStartDate?.ToString("o"),
                    currentPeriodEnd = user.TrialEndDate?.ToString("o"),
                    cancelAtPeriodEnd = false,
                    trialEnd = user.TrialEndDate?.ToString("o"),
                    amount = 0m // Trial is free
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Create new subscription
    /// </summary>
    [HttpPost("subscribe")]
    public async Task<IActionResult> CreateSubscription([FromBody] BuoyAI.Core.DTOs.Billing.CreateBillingSubscriptionRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var subscription = await _billingService.CreateSubscriptionAsync(userId, request);

            return Ok(new {
                Message = "Subscription created successfully",
                SubscriptionId = subscription.Id,
                Tier = subscription.Tier.ToString(),
                Amount = subscription.Amount,
                NextBillingDate = subscription.NextBillingDate
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Subscription creation failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Create a hosted Stripe Checkout session for the chosen plan. Returns a URL the
    /// client opens in the browser; the subscription is activated via webhook on return.
    /// </summary>
    [HttpPost("checkout-session")]
    public async Task<IActionResult> CreateCheckoutSession([FromBody] CreateCheckoutSessionRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var url = await _billingService.CreateCheckoutSessionAsync(userId, request.Tier, request.BillingCycle, request.PromoCode, request.Platform);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Checkout session creation failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Create a Stripe Customer Portal session for self-serve billing management
    /// (invoices, receipts, payment methods, cancellation). Returns the URL to open.
    /// </summary>
    [HttpPost("portal-session")]
    public async Task<IActionResult> CreateBillingPortalSession()
    {
        try
        {
            var userId = GetCurrentUserId();
            var url = await _billingService.CreateBillingPortalSessionAsync(userId);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Billing portal session creation failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Change subscription tier/billing cycle
    /// </summary>
    [HttpPut("change")]
    public async Task<IActionResult> ChangeSubscription([FromBody] BuoyAI.Core.DTOs.Billing.ChangeSubscriptionRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var subscription = await _billingService.ChangeSubscriptionAsync(userId, request);

            return Ok(new {
                Message = "Subscription updated successfully",
                NewTier = subscription.Tier.ToString(),
                NewAmount = subscription.Amount,
                NextBillingDate = subscription.NextBillingDate
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Subscription change failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Cancel subscription
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> CancelSubscription([FromBody] BuoyAI.Core.DTOs.Billing.CancelSubscriptionRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            await _billingService.CancelSubscriptionAsync(userId, request);

            var message = request.CancelImmediately
                ? "Subscription cancelled immediately"
                : "Subscription will cancel at the end of current billing period";

            return Ok(new { Message = message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Subscription cancellation failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Update payment method and optionally pay pending invoices
    /// </summary>
    [HttpPut("payment-method")]
    public async Task<IActionResult> UpdatePaymentMethod([FromBody] UpdatePaymentMethodRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var result = await _billingService.UpdatePaymentMethodAsync(userId, request.PaymentMethodToken);

            return Ok(new {
                Message = result.PendingInvoicesPaid
                    ? "Payment method updated and pending invoices paid"
                    : "Payment method updated successfully",
                PendingInvoicesPaid = result.PendingInvoicesPaid
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Payment method update failed for user {UserId}", GetCurrentUserId());
            return BadRequest(new { Message = ex.Message });
        }
    }

    #endregion

    #region Billing History

    /// <summary>
    /// Get billing history
    /// </summary>
    [HttpGet("history")]
    public async Task<IActionResult> GetBillingHistory()
    {
        var userId = GetCurrentUserId();
        var history = await _billingService.GetBillingHistoryAsync(userId);

        var formattedHistory = history.Select(p => new {
            Id = p.Id,
            Amount = p.Amount,
            Status = p.Status.ToString(),
            PaymentDate = p.PaymentDate,
            FailureReason = p.FailureReason
        });

        return Ok(formattedHistory);
    }

    /// <summary>
    /// Retry failed payment
    /// </summary>
    [HttpPost("retry-payment/{paymentId}")]
    public async Task<IActionResult> RetryPayment(int paymentId)
    {
        try
        {
            var userId = GetCurrentUserId();
            var status = await _billingService.RetryPaymentAsync(userId, paymentId);

            return Ok(new {
                Message = "Payment retry initiated",
                Status = status.ToString()
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Payment retry failed for user {UserId}, payment {PaymentId}",
                GetCurrentUserId(), paymentId);
            return BadRequest(new { Message = ex.Message });
        }
    }

    #endregion

    #region Feature Access

    /// <summary>
    /// Check if user can access a specific feature
    /// </summary>
    [HttpGet("access/{featureName}")]
    public async Task<IActionResult> CheckFeatureAccess(string featureName)
    {
        var userId = GetCurrentUserId();
        var canAccess = await _entitlements.HasFeatureAsync(userId, featureName);
        var currentTier = await _entitlements.GetEffectiveTierAsync(userId);

        return Ok(new {
            CanAccess = canAccess,
            CurrentTier = currentTier?.ToString(),
            FeatureName = featureName
        });
    }

    #endregion

    #region Promo Codes

    /// <summary>
    /// Redeems a promo code against the caller's existing subscription (trial or paid) and
    /// returns the discount now applied.
    ///
    /// 404 for every unusable code — unknown, inactive, restricted, or no subscription to
    /// attach it to. Distinguishing those would turn this into an oracle for which codes
    /// exist, and codes are guessable by design.
    /// </summary>
    [HttpPost("promo/redeem")]
    [EnableRateLimiting("api")]
    public async Task<IActionResult> RedeemPromoCode([FromBody] RedeemPromoCodeRequest request)
    {
        var userId = GetCurrentUserId();
        var discount = await _billingService.RedeemPromoCodeAsync(userId, request.PromoCode);

        return discount == null
            ? NotFound(new { message = "That code isn't valid for your account." })
            : Ok(discount);
    }

    /// <summary>
    /// The discount currently applied to the caller's subscription, or 204 when there is none.
    /// Drives the indicator on the subscription card.
    /// </summary>
    [HttpGet("promo/active")]
    public async Task<IActionResult> GetActiveDiscount()
    {
        var discount = await _billingService.GetActiveDiscountAsync(GetCurrentUserId());
        return discount == null ? NoContent() : Ok(discount);
    }

    /// <summary>
    /// Validate a promo code and calculate discounted price
    /// </summary>
    [HttpPost("validate-promo")]
    public async Task<IActionResult> ValidatePromoCode([FromBody] ValidatePromoCodeRequest request)
    {
        var result = await _billingService.ValidatePromoCodeAsync(request.PromoCode, request.PriceId);

        if (!result.IsValid)
        {
            return BadRequest(new { Message = "Invalid promo code", IsValid = false });
        }

        return Ok(new
        {
            IsValid = true,
            PromoCode = result.PromoCode,
            DiscountPercent = result.DiscountPercent,
            DiscountAmount = result.DiscountAmount,
            OriginalPrice = result.OriginalPrice,
            FinalPrice = result.FinalPrice,
            Description = result.Description
        });
    }

    #endregion
}

#region Request/Response Models

/// <summary>
/// Request for validating promo code
/// </summary>
public class ValidatePromoCodeRequest
{
    public string PromoCode { get; set; } = string.Empty;
    public string PriceId { get; set; } = string.Empty;
}

/// <summary>
/// Request for redeeming a promo code against the caller's existing subscription. No price id:
/// the subscription already knows what it is priced at.
/// </summary>
public class RedeemPromoCodeRequest
{
    public string PromoCode { get; set; } = string.Empty;
}

#endregion
