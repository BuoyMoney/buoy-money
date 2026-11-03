using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

/// <inheritdoc />
public class RevenueCatSyncService : IRevenueCatSyncService
{
    public const string HttpClientName = "revenuecat";
    private const string ApiBase = "https://api.revenuecat.com/v1/";

    private readonly BuoyAIDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RevenueCatSyncService> _logger;

    public RevenueCatSyncService(
        BuoyAIDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RevenueCatSyncService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configuration["RevenueCat:WebhookAuthorization"]);

    /// <summary>
    /// Sandbox events are accepted unless <c>RevenueCat:AcceptSandbox</c> is "false". Production
    /// sets it false so a TestFlight or sandbox purchase can never grant a real entitlement;
    /// staging and local builds leave it on so the sandbox flow can be exercised end to end.
    /// </summary>
    private bool AcceptSandbox =>
        !string.Equals(_configuration["RevenueCat:AcceptSandbox"], "false", StringComparison.OrdinalIgnoreCase);

    public bool IsWebhookAuthorized(string? authorizationHeader)
    {
        var expected = _configuration["RevenueCat:WebhookAuthorization"];
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(authorizationHeader))
            return false;
        var a = Encoding.UTF8.GetBytes(authorizationHeader.Trim());
        var b = Encoding.UTF8.GetBytes(expected.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public Task<bool> HasProcessedEventAsync(string eventId, CancellationToken ct = default) =>
        string.IsNullOrEmpty(eventId)
            ? Task.FromResult(false)
            : _context.ProcessedRevenueCatEvents.AnyAsync(e => e.EventId == eventId, ct);

    public async Task MarkEventProcessedAsync(string eventId, string eventType, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(eventId)) return;
        if (await _context.ProcessedRevenueCatEvents.AnyAsync(e => e.EventId == eventId, ct)) return;
        _context.ProcessedRevenueCatEvents.Add(new ProcessedRevenueCatEvent
        {
            EventId = eventId,
            EventType = eventType,
            ProcessedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync(ct);
    }

    // ── Webhook events ────────────────────────────────────────────────────────

    public async Task<RevenueCatApplyResult> ApplyEventAsync(RevenueCatEvent evt, CancellationToken ct = default)
    {
        var type = (evt.Type ?? string.Empty).ToUpperInvariant();
        if (type == "TEST") return new RevenueCatApplyResult(false, "test event");

        if (string.Equals(evt.Environment, "SANDBOX", StringComparison.OrdinalIgnoreCase) && !AcceptSandbox)
            return new RevenueCatApplyResult(false, "sandbox event ignored in this environment");

        if (type == "TRANSFER")
        {
            // Entitlements moved between app user ids (a restore on a second account). The event
            // does not carry the resulting state, so pull both sides fresh.
            var touched = 0;
            foreach (var id in (evt.TransferredFrom ?? new()).Concat(evt.TransferredTo ?? new()))
            {
                if (TryParseUserId(id, out var uid) && await RefreshFromRevenueCatAsync(uid, "transfer", ct))
                    touched++;
            }
            return new RevenueCatApplyResult(touched > 0, $"transfer: refreshed {touched} user(s)");
        }

        var userId = ResolveUserId(evt);
        if (userId == null)
            return new RevenueCatApplyResult(false, $"no BuoyAI user id in app_user_id '{evt.AppUserId}'");
        if (!await _context.Users.AnyAsync(u => u.Id == userId.Value, ct))
            return new RevenueCatApplyResult(false, $"user {userId} not found", userId);

        var provider = ProviderFor(evt.Store);
        if (provider == null)
            return new RevenueCatApplyResult(false, $"store '{evt.Store}' is not projected here", userId);

        var productId = type == "PRODUCT_CHANGE" ? (evt.NewProductId ?? evt.ProductId) : evt.ProductId;
        var plan = StoreProducts.Resolve(productId, k => _configuration[k]);
        if (plan == null)
        {
            _logger.LogWarning("RevenueCat event {Type} for user {UserId} names unknown product '{Product}'", type, userId, productId);
            return new RevenueCatApplyResult(false, $"unknown product '{productId}'", userId);
        }

        var now = DateTime.UtcNow;
        var expires = FromMs(evt.ExpirationAtMs);
        var purchased = FromMs(evt.PurchasedAtMs);
        UserSubscriptionStatus status;
        var willNotRenew = false;
        switch (type)
        {
            case "INITIAL_PURCHASE":
            case "RENEWAL":
            case "UNCANCELLATION":
            case "PRODUCT_CHANGE":
            case "SUBSCRIPTION_EXTENDED":
            case "NON_RENEWING_PURCHASE":
            case "TEMPORARY_ENTITLEMENT_GRANT":
            case "REFUND_REVERSED":
                status = expires is DateTime e1 && e1 <= now ? UserSubscriptionStatus.Expired : UserSubscriptionStatus.Active;
                break;
            case "CANCELLATION":
            case "SUBSCRIPTION_PAUSED":
                // Auto-renew turned off (or a refund): access runs to the expiry that RevenueCat
                // reports. A refund arrives with an expiry in the past and revokes at once.
                willNotRenew = true;
                status = expires is DateTime e2 && e2 > now ? UserSubscriptionStatus.Active : UserSubscriptionStatus.Canceled;
                break;
            case "EXPIRATION":
                status = UserSubscriptionStatus.Expired;
                break;
            case "BILLING_ISSUE":
                // The store is retrying the card. Keep access through the grace period, as the
                // Stripe projection does for past_due.
                status = UserSubscriptionStatus.PastDue;
                expires = FromMs(evt.GracePeriodExpirationAtMs) ?? expires;
                break;
            default:
                return new RevenueCatApplyResult(false, $"event type {type} not projected", userId);
        }

        var state = new StoreSubscriptionState(
            userId.Value, provider.Value, NormalizeProductId(productId!), plan.Value.Tier, plan.Value.Cycle,
            status, purchased, expires, willNotRenew, evt.Price, evt.Currency);
        await ApplyStateAsync(state, ct);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RevenueCat {Type} projected for user {UserId}: {Provider} {Product} -> {Status} (expires {Expires})",
            type, userId, provider, state.ProductId, status, expires);
        return new RevenueCatApplyResult(true, $"{type} applied", userId);
    }

    // ── REST refresh ──────────────────────────────────────────────────────────

    public async Task<bool> RefreshFromRevenueCatAsync(int userId, string reason, CancellationToken ct = default)
    {
        var secret = _configuration["RevenueCat:SecretApiKey"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogDebug("RevenueCat:SecretApiKey not configured; skipping refresh for user {UserId}", userId);
            return false;
        }
        try
        {
            if (!await _context.Users.AnyAsync(u => u.Id == userId, ct)) return false;

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "subscribers/" + Uri.EscapeDataString(userId.ToString()));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("RevenueCat subscriber lookup for user {UserId} returned {Status} ({Reason})",
                    userId, (int)response.StatusCode, reason);
                return false;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("subscriber", out var subscriber)
                || !subscriber.TryGetProperty("subscriptions", out var subscriptions)
                || subscriptions.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var applied = 0;
            foreach (var entry in subscriptions.EnumerateObject())
            {
                var sub = entry.Value;
                var provider = ProviderFor(Str(sub, "store"));
                if (provider == null) continue;
                if (Bool(sub, "is_sandbox") && !AcceptSandbox) continue;
                var plan = StoreProducts.Resolve(entry.Name, k => _configuration[k]);
                if (plan == null) continue;

                var expires = Date(sub, "expires_date");
                var grace = Date(sub, "grace_period_expires_date");
                var refunded = Date(sub, "refunded_at");
                var unsubscribed = Date(sub, "unsubscribe_detected_at");
                var billingIssue = Date(sub, "billing_issues_detected_at");

                UserSubscriptionStatus status;
                if (refunded != null) status = UserSubscriptionStatus.Canceled;
                else if (expires is DateTime e && e > now) status = billingIssue != null ? UserSubscriptionStatus.PastDue : UserSubscriptionStatus.Active;
                else if (grace is DateTime g && g > now) status = UserSubscriptionStatus.PastDue;
                else status = UserSubscriptionStatus.Expired;

                var state = new StoreSubscriptionState(
                    userId, provider.Value, NormalizeProductId(entry.Name), plan.Value.Tier, plan.Value.Cycle,
                    status, Date(sub, "purchase_date"), expires, unsubscribed != null, null, null);
                await ApplyStateAsync(state, ct);
                applied++;
            }

            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Refreshed RevenueCat state for user {UserId} ({Reason}): {Count} store subscription(s)",
                userId, reason, applied);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RevenueCat refresh failed for user {UserId} ({Reason}); serving cached state", userId, reason);
            return false;
        }
    }

    // ── THE projection ────────────────────────────────────────────────────────

    /// <summary>
    /// One store subscription state onto the local rows and the user's entitlement flags.
    /// Rows are keyed by (user, provider, "<store>:<product>") so a webhook and a REST refresh
    /// describing the same subscription land on the same row. A user holds one live store
    /// subscription at a time (the products share a subscription group), so activating one
    /// product retires any other live row from the same provider.
    /// </summary>
    private async Task ApplyStateAsync(StoreSubscriptionState state, CancellationToken ct)
    {
        var user = await _context.Users.FirstAsync(u => u.Id == state.UserId, ct);
        var rows = await _context.UserSubscriptions.Where(s => s.UserId == state.UserId).ToListAsync(ct);
        var now = DateTime.UtcNow;
        var externalId = $"{state.Provider}:{state.ProductId}".ToLowerInvariant();

        var row = rows.FirstOrDefault(r => r.Provider == state.Provider
                                           && string.Equals(r.ExternalId, externalId, StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            row = new UserSubscription
            {
                UserId = state.UserId,
                Provider = state.Provider,
                ExternalId = externalId,
                Tier = state.Tier,
                BillingCycle = state.Cycle,
                StartDate = state.PurchasedAt ?? now,
                CreatedAt = now,
            };
            _context.UserSubscriptions.Add(row);
            rows.Add(row);
        }

        row.Tier = state.Tier;
        row.BillingCycle = state.Cycle;
        row.Status = state.Status;
        row.Amount = state.Price ?? row.Amount;
        row.NextBillingDate = state.ExpiresAt ?? row.NextBillingDate;
        row.EndDate = state.Status is UserSubscriptionStatus.Active or UserSubscriptionStatus.PastDue
            ? (state.WillNotRenew ? state.ExpiresAt : null)
            : (state.ExpiresAt ?? now);
        row.UpdatedAt = now;

        if (IsLive(row))
        {
            foreach (var other in rows.Where(r => r != row && r.Provider == state.Provider && IsLive(r)))
            {
                other.Status = UserSubscriptionStatus.Canceled;
                other.EndDate ??= now;
                other.UpdatedAt = now;
            }
        }

        RecomputeEntitlement(user, rows, now);
    }

    private static bool IsLive(UserSubscription r) =>
        r.Status is UserSubscriptionStatus.Active or UserSubscriptionStatus.PastDue;

    /// <summary>
    /// The user-level flags the access gate reads, derived from every row regardless of
    /// provider. A live row anywhere (Stripe or a store) keeps the higher tier; with none, a
    /// running trial keeps its access; otherwise the paywall comes back exactly as it does when
    /// a Stripe subscription ends.
    /// </summary>
    private static void RecomputeEntitlement(User user, List<UserSubscription> rows, DateTime now)
    {
        var live = rows.Where(IsLive).ToList();
        if (live.Count > 0)
        {
            user.SubscriptionTier = live.Any(r => r.Tier == SubscriptionTier.Premium) ? SubscriptionTier.Premium : live[0].Tier;
            user.RequiresPaidPlan = false;
        }
        else if (user.IsTrialActive && user.TrialEndDate > now)
        {
            // The trial carries on; nothing to revoke.
        }
        else
        {
            user.SubscriptionTier = null;
            user.RequiresPaidPlan = true;
        }
        user.UpdatedAt = now;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int? ResolveUserId(RevenueCatEvent evt)
    {
        foreach (var candidate in new[] { evt.AppUserId, evt.OriginalAppUserId }.Concat(evt.Aliases ?? new()))
        {
            if (TryParseUserId(candidate, out var id)) return id;
        }
        return null;
    }

    /// <summary>Our app user ids are the numeric BuoyAI user id; RevenueCat's anonymous ids ($RCAnonymousID:…) are not ours.</summary>
    private static bool TryParseUserId(string? value, out int id)
    {
        id = 0;
        return !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out id) && id > 0;
    }

    private static SubscriptionProvider? ProviderFor(string? store) => (store ?? string.Empty).ToUpperInvariant() switch
    {
        "APP_STORE" or "MAC_APP_STORE" => SubscriptionProvider.AppStore,
        "PLAY_STORE" => SubscriptionProvider.PlayStore,
        _ => null, // STRIPE, PROMOTIONAL, AMAZON: not projected here
    };

    private static string NormalizeProductId(string productId) => productId.Split(':')[0].Trim().ToLowerInvariant();

    private static DateTime? FromMs(long? ms) =>
        ms is long v && v > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s != null && DateTimeOffset.TryParse(s, out var dto) ? dto.UtcDateTime : null;
    }
}
