using BuoyAI.Core.Configuration;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// What a signed-in user can do about their Cloud plan. The decisions that belong to the app live
/// here (who the customer is, whether they qualify for founding prices, whether they may buy at all);
/// the Stripe work and the product rules live in the Cloud billing service this calls.
///
/// The customer id, the user id and the environment tag always come from the server's own records,
/// never from the request, so one user can never act on another's subscription.
/// </summary>
public class CloudBillingService : ICloudBillingService
{
    private readonly BuoyAIDbContext _context;
    private readonly ICloudBillingClient _client;
    private readonly IReservationService _reservations;
    private readonly IStripeService _stripe;
    private readonly ISubscriptionSyncService _sync;
    private readonly IEntitlementService _entitlements;
    private readonly IConfiguration _configuration;
    private readonly CloudBillingOptions _options;
    private readonly ILogger<CloudBillingService> _logger;
    private readonly string _environmentTag;

    public CloudBillingService(
        BuoyAIDbContext context,
        ICloudBillingClient client,
        IReservationService reservations,
        IStripeService stripe,
        ISubscriptionSyncService sync,
        IEntitlementService entitlements,
        IConfiguration configuration,
        IOptions<CloudBillingOptions> options,
        IHostEnvironment hostEnvironment,
        ILogger<CloudBillingService> logger)
    {
        _context = context;
        _client = client;
        _reservations = reservations;
        _stripe = stripe;
        _sync = sync;
        _entitlements = entitlements;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
        _environmentTag = StripeEnvironmentPolicy.Tag(hostEnvironment.EnvironmentName);
    }

    public bool IsEnabled => _options.Enabled;

    public async Task<CloudBillingStatus> GetStatusAsync(int userId, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId, ct);
        var access = ToDto(await _entitlements.GetProductAccessAsync(userId));
        if (!IsEnabled)
        {
            return new CloudBillingStatus(false, false, null, access);
        }

        var founding = await IsFoundingEligibleAsync(user, ct);
        var hasLivePlan = await _context.UserSubscriptions.AnyAsync(s => s.UserId == userId
            && (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue), ct);
        CloudSubscriptionState? subscription = null;
        var lookupFailed = false;
        if (!string.IsNullOrEmpty(user.StripeCustomerId))
        {
            try
            {
                subscription = await _client.GetSubscriptionAsync(user.StripeCustomerId, _environmentTag, ct);
            }
            catch (CloudBillingException ex)
            {
                // The status screen must still open when the billing service fails (down, or a 409 such as environment_mismatch): what the user
                // may USE comes from our own database, only the plan details are missing.
                _logger.LogWarning(ex, "Cloud billing lookup failed for user {UserId}", userId);
                lookupFailed = true;
            }
        }
        return new CloudBillingStatus(true, founding, subscription, access, lookupFailed, hasLivePlan);
    }

    public async Task<CloudCatalog> GetCatalogAsync(int userId, CancellationToken ct = default)
    {
        EnsureEnabled();
        var user = await RequireUserAsync(userId, ct);
        return await _client.GetCatalogAsync(await IsFoundingEligibleAsync(user, ct), ct);
    }

    public async Task<string> StartCheckoutAsync(
        int userId, string interval, IReadOnlyList<CloudProductSelection> products,
        string? platform, string? idempotencyKey, CancellationToken ct = default)
    {
        EnsureEnabled();

        // Apple's rules (3.1.1): digital products sold inside the iOS app go through the App Store,
        // not a web checkout. New Cloud products are bought on the web until a store path exists.
        if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase))
        {
            throw new CloudBillingException(
                "web_purchase_required", "BuoyAI Cloud plans are bought on the web at buoy-ai.com.", 403);
        }

        var user = await RequireUserAsync(userId, ct);

        // A live plan from any provider is changed, not bought again. A past-due user recovers the
        // plan they have rather than starting a second one that would bill twice.
        // A hosted trial (Trialing) does not block buying: it is cancelled when the Cloud plan
        // activates (BillingService.ActivateSubscriptionFromCheckoutAsync).
        var live = await _context.UserSubscriptions.AnyAsync(s => s.UserId == userId
            && (s.Status == UserSubscriptionStatus.Active || s.Status == UserSubscriptionStatus.PastDue), ct);
        if (live)
        {
            throw new CloudBillingException(
                "already_subscribed", "You already have a plan. Change it instead of buying another.", 409);
        }

        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            user.StripeCustomerId = await _stripe.CreateCustomerAsync(user);
            await _context.SaveChangesAsync(ct);
        }

        var baseUrl = (_configuration["App:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("App:BaseUrl is not configured; cannot build Checkout return URLs.");
        }
        // Server-derived, never client-supplied, so there is no open-redirect risk. The client only picks which of
        // the two configured places (the app, or the Cloud portal) it came from.
        var prefix = ReturnPrefix(string.Equals(platform, PortalPlatform, StringComparison.OrdinalIgnoreCase));

        var founding = await IsFoundingEligibleAsync(user, ct);
        // The paid reservation comes off the first payment. The billing service makes it a single-use coupon per
        // customer, so a second checkout (or a later plan after a cancel) does not get it again.
        long? credit = founding ? await _reservations.GetPaidReservationCreditCentsAsync(user.Email, ct) : null;
        var response = await _client.CheckoutAsync(new CloudCheckoutRequest(
            user.StripeCustomerId!, userId, _environmentTag, interval, founding, products,
            $"{baseUrl}{prefix}/billing?checkout=success", $"{baseUrl}{prefix}/billing?checkout=cancel",
            idempotencyKey, credit is > 0 ? credit : null), ct);

        _logger.LogInformation(
            "Started Cloud checkout for user {UserId}: {Products}, {Interval}, founding={Founding}",
            userId, string.Join(",", products.Select(p => p.Product)), interval, founding);
        return response.Url;
    }

    public async Task<CloudPreview> PreviewAsync(
        int userId, string type, string? product, int? quantity, DateTime? prorationDate, CancellationToken ct = default)
    {
        EnsureEnabled();
        var user = await RequireCustomerAsync(userId, ct);
        return await _client.PreviewAsync(
            new CloudChangeRequest(user.StripeCustomerId!, _environmentTag, type, product, quantity, prorationDate), ct);
    }

    public async Task<CloudSubscriptionState> ApplyAsync(
        int userId, string type, string? product, int? quantity, DateTime? prorationDate,
        string? idempotencyKey, CancellationToken ct = default)
    {
        EnsureEnabled();
        var user = await RequireCustomerAsync(userId, ct);
        var state = await _client.ApplyAsync(
            new CloudChangeRequest(user.StripeCustomerId!, _environmentTag, type, product, quantity, prorationDate, idempotencyKey), ct);

        // Do not wait for the webhook: re-project now so the very next request sees the new products.
        // Best effort; the webhook is the safety net and the sync service never throws.
        await _sync.RefreshFromStripeAsync(userId, "cloud-change", TimeSpan.Zero, ct);

        _logger.LogInformation("Applied Cloud change {Type} {Product} for user {UserId}", type, product, userId);
        return state;
    }

    public async Task<CloudBillingSummary> GetSummaryAsync(int userId, CancellationToken ct = default)
    {
        EnsureEnabled();
        var user = await RequireUserAsync(userId, ct);
        // A user who never started a Cloud (or any Stripe) purchase has no customer to ask about: an empty summary, not an error.
        if (string.IsNullOrEmpty(user.StripeCustomerId)) return new CloudBillingSummary(null, null);

        CloudSubscriptionState? subscription;
        try
        {
            subscription = await _client.GetSubscriptionAsync(user.StripeCustomerId, _environmentTag, ct);
        }
        catch (CloudBillingException ex)
        {
            // The billing screen still opens; it says the figures could not be loaded.
            _logger.LogWarning(ex, "Cloud billing summary lookup failed for user {UserId}", userId);
            return new CloudBillingSummary(null, null, LookupFailed: true);
        }
        if (subscription == null) return new CloudBillingSummary(null, null);

        try
        {
            return new CloudBillingSummary(subscription, await _client.GetUpcomingAsync(user.StripeCustomerId, _environmentTag, ct));
        }
        catch (CloudBillingException ex)
        {
            // The plan is known; only the next invoice is not. The screen shows the plan and says the invoice is missing.
            _logger.LogWarning(ex, "Cloud upcoming invoice lookup failed for user {UserId}", userId);
            return new CloudBillingSummary(subscription, null, LookupFailed: true);
        }
    }

    /// <summary>The checkout <c>platform</c> the Cloud portal (buoy-ai.com/cloud/) sends: a web purchase that returns there.</summary>
    public const string PortalPlatform = "portal";

    /// <summary>
    /// Where Stripe sends the customer back to: the app (<c>App:CheckoutReturnPrefix</c>, "/app" in production) or the
    /// Cloud portal (<c>App:PortalReturnPrefix</c>, default "/cloud").
    /// </summary>
    private string ReturnPrefix(bool fromPortal) => fromPortal
        ? (_configuration["App:PortalReturnPrefix"] ?? "/cloud")
        : (_configuration["App:CheckoutReturnPrefix"] ?? "/#");

    public Task<string> CreatePortalSessionAsync(int userId, CancellationToken ct = default) =>
        CreatePortalSessionAsync(userId, fromPortal: false, ct);

    public async Task<string> CreatePortalSessionAsync(int userId, bool fromPortal, CancellationToken ct = default)
    {
        EnsureEnabled();
        var user = await RequireCustomerAsync(userId, ct);

        var baseUrl = (_configuration["App:BaseUrl"] ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("App:BaseUrl is not configured; cannot build the portal return URL.");
        }
        // Server-derived, never client-supplied, so there is no open-redirect risk.
        var returnPath = fromPortal ? $"{ReturnPrefix(true)}/billing" : $"{ReturnPrefix(false)}/settings/cloud/billing";
        var session = await _client.CreatePortalSessionAsync(
            new CloudPortalSessionRequest(user.StripeCustomerId!, _environmentTag, $"{baseUrl}{returnPath}"), ct);
        return session.Url;
    }

    /// <summary>
    /// A paid, not refunded reservation under the user's own VERIFIED email. An unverified address
    /// proves nothing: anyone could sign up with someone else's.
    /// </summary>
    private async Task<bool> IsFoundingEligibleAsync(User user, CancellationToken ct) =>
        user.IsEmailVerified && await _reservations.HasPaidReservationAsync(user.Email, ct);

    private void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw new CloudBillingException("not_available", "Cloud plans are not available yet.", 404);
        }
    }

    private async Task<User> RequireUserAsync(int userId, CancellationToken ct) =>
        await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
        ?? throw new CloudBillingException("user_not_found", "User not found.", 404);

    private async Task<User> RequireCustomerAsync(int userId, CancellationToken ct)
    {
        var user = await RequireUserAsync(userId, ct);
        if (string.IsNullOrEmpty(user.StripeCustomerId))
        {
            throw new CloudBillingException("no_subscription", "You have no Cloud subscription.", 404);
        }
        return user;
    }

    private static CloudProductAccess ToDto(ProductAccess a) =>
        new(a.BankSync, a.Ai, a.InstantRefresh, a.ExtraLogins, a.EndsAt, a.Founding);
}
