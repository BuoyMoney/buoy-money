using BuoyAI.Core.Entitlements;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using BuoyAI.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Effective tier, Premium access, feature gates and AI caps from the local database and
/// configuration alone. The constructor takes no billing provider on purpose: it must be
/// buildable with no Stripe keys, because every sign-in and every gated request needs it.
/// </summary>
public class EntitlementService : IEntitlementService
{
    // Feature name -> minimum tier. A feature not listed here is open.
    private static readonly Dictionary<string, SubscriptionTier> FeatureRequirements = new()
    {
        // Standard tier features (available to paying users or trial users)
        ["plaid_connections"] = SubscriptionTier.Standard,
        ["ai_insights"] = SubscriptionTier.Standard,
        ["automatic_categorization"] = SubscriptionTier.Standard,
        ["budgeting_tools"] = SubscriptionTier.Standard,

        // Not gated anywhere (SubscriptionsController has no tier check); listed as Standard to match.
        ["subscription_tracking"] = SubscriptionTier.Standard,

        // Premium tier features
        ["ai_chat_bot"] = SubscriptionTier.Premium,
        ["enhanced_ai_insights"] = SubscriptionTier.Premium,
        ["priority_support"] = SubscriptionTier.Premium,
        ["advanced_analytics"] = SubscriptionTier.Premium,

        // Admin features
        ["admin_dashboard"] = SubscriptionTier.Admin
    };

    // Features that cost AI usage; they need the AI product on top of the tier.
    private static readonly HashSet<string> AiFeatures = new()
    {
        "ai_insights", "automatic_categorization", "ai_chat_bot", "enhanced_ai_insights"
    };

    private readonly BuoyAIDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly IReadOnlyList<ITierRefreshHook> _refreshHooks;
    private readonly ILogger<EntitlementService> _logger;
    private readonly Lazy<AiLimits> _aiLimits;

    public EntitlementService(
        BuoyAIDbContext context,
        IConfiguration configuration,
        IEnumerable<ITierRefreshHook> refreshHooks,
        ILogger<EntitlementService> logger)
    {
        _logger = logger;
        _context = context;
        _configuration = configuration;
        _refreshHooks = refreshHooks.ToList();
        _aiLimits = new Lazy<AiLimits>(() => AiLimitsConfiguration.Load(configuration));
    }

    public async Task<SubscriptionTier?> GetEffectiveTierAsync(int userId)
    {
        var tier = await ResolveFromProjectionAsync(userId);
        if (tier != null) return tier;

        // Deny-only rule: only a "no tier" answer is worth an outside round trip, because
        // that is the answer that locks someone out. A grant is never made more correct by it.
        foreach (var hook in _refreshHooks)
        {
            if (await hook.RefreshAsync(userId, "tier-denial"))
            {
                return await ResolveFromProjectionAsync(userId);
            }
        }

        return null;
    }

    public async Task<SubscriptionTier> GetTierOrStandardAsync(int userId)
    {
        try
        {
            var tier = await GetEffectiveTierAsync(userId);
            if (tier.HasValue)
            {
                _logger.LogDebug("User {UserId} has subscription tier: {Tier}", userId, tier.Value);
                return tier.Value;
            }

            // No active subscription: default to Standard (allows access to free trial features)
            _logger.LogDebug("User {UserId} has no active subscription, defaulting to Standard tier", userId);
            return SubscriptionTier.Standard;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error determining user tier for userId {UserId}, defaulting to Standard", userId);
            return SubscriptionTier.Standard;
        }
    }

    public async Task<bool> HasPremiumAccessAsync(int userId) =>
        (await GetEffectiveTierAsync(userId)).HasPremiumAccess();

    public async Task<bool> HasFeatureAsync(int userId, string feature)
    {
        if (!FeatureRequirements.TryGetValue(feature, out var requiredTier))
        {
            // Feature not in map, allow access (for backward compatibility)
            return true;
        }

        var userTier = await GetEffectiveTierAsync(userId);
        if (userTier == null || userTier < requiredTier) return false;

        // Connecting a bank costs real money per account, so it also needs the Bank sync
        // product, and the AI features need the AI product. A legacy Standard or Premium plan
        // carries both (see ProductRules.ForLegacyTier); a Cloud customer who bought only one
        // does not get the other just because the tier it implies is high enough.
        if (userTier != SubscriptionTier.Admin)
        {
            if (feature == "plaid_connections")
            {
                return (await GetProductAccessAsync(userId)).BankSync;
            }
            if (AiFeatures.Contains(feature))
            {
                return (await GetProductAccessAsync(userId)).Ai;
            }
        }

        return true;
    }

    public bool HasPremiumAccess(User user)
    {
        // The allowlist grants ACCESS, not just a label. It previously only rewrote the tier
        // shown in the profile DTO while every server-side gate read User.SubscriptionTier
        // straight from the database, so an operator's profile said "Admin" while family
        // creation answered 403. Any gate asking "may this user use a Premium feature?"
        // belongs here rather than comparing the column itself.
        if (AdminPolicy.IsAdminEmail(_configuration, user.Email))
        {
            return true;
        }

        if (user.IsTrialActive && user.TrialEndDate > DateTime.UtcNow)
        {
            return true;
        }

        return user.SubscriptionTier is SubscriptionTier.Premium
            or SubscriptionTier.PremiumInherited
            or SubscriptionTier.Admin;
    }

    public AiTierLimits GetAiLimits(SubscriptionTier tier) => _aiLimits.Value.For(tier);

    public async Task<bool> HasCloudProductsAsync(int userId)
    {
        var now = DateTime.UtcNow;
        return await _context.UserEntitlements.AsNoTracking().AnyAsync(e => e.UserId == userId
            && (e.Status == EntitlementStatus.Active || e.Status == EntitlementStatus.PastDue)
            && (e.EndsAt == null || e.EndsAt > now));
    }

    public async Task<ProductAccess> GetProductAccessAsync(int userId)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return ProductAccess.None;

        // Operators are never billed.
        if (AdminPolicy.IsAdminEmail(_configuration, user.Email)) return ProductAccess.All;

        var now = DateTime.UtcNow;
        var rows = await _context.UserEntitlements.AsNoTracking()
            .Where(e => e.UserId == userId
                && (e.Status == EntitlementStatus.Active || e.Status == EntitlementStatus.PastDue)
                && (e.EndsAt == null || e.EndsAt > now))
            .ToListAsync();

        var fromRows = FromRows(rows);
        if (rows.Count == 0)
        {
            // Nothing Cloud-shaped: exactly the old plan rules, mapped to products.
            var tier = await GetEffectiveTierAsync(userId);
            return tier == null ? ProductAccess.None : ProductRules.ForLegacyTier(tier.Value, Config);
        }

        // Cloud rows exist. The tier they imply must not be expanded again, so only a source
        // that is NOT one of these Cloud subscriptions adds legacy products: a trial, a store
        // purchase, a hosted plan, or family inheritance.
        var legacyTier = await GetNonCatalogTierAsync(user, rows, now);
        return legacyTier == null
            ? fromRows
            : ProductRules.Union(fromRows, ProductRules.ForLegacyTier(legacyTier.Value, Config));
    }

    private string? Config(string key) => _configuration[key];

    private static ProductAccess FromRows(IReadOnlyList<UserEntitlement> rows)
    {
        var flags = rows.Where(r => r.Entitlement != ProductEntitlements.ExtraLogins).Select(r => r.Entitlement);
        var extra = rows.Where(r => r.Entitlement == ProductEntitlements.ExtraLogins).Sum(r => r.Quantity);
        var ends = new Dictionary<string, DateTime>();
        foreach (var g in rows.Where(r => r.EndsAt != null).GroupBy(r => r.Entitlement))
        {
            // Only a product every row of which ends has an end; a second row keeping it open wins.
            if (g.All(r => r.EndsAt != null)) ends[g.Key] = g.Max(r => r.EndsAt!.Value);
        }
        return ProductRules.Normalize(flags, extra, ends, rows.Any(r => r.Founding));
    }

    private async Task<SubscriptionTier?> GetNonCatalogTierAsync(User user, IReadOnlyList<UserEntitlement> rows, DateTime now)
    {
        var catalogSubscriptionIds = rows.Select(r => r.ExternalId).ToHashSet();

        SubscriptionTier? best = null;
        void Consider(SubscriptionTier? t)
        {
            if (t != null && (best == null || t > best)) best = t;
        }

        var others = await _context.UserSubscriptions.AsNoTracking()
            .Where(s => s.UserId == user.Id && s.Status == UserSubscriptionStatus.Active)
            .ToListAsync();
        foreach (var s in others)
        {
            var id = s.Provider == SubscriptionProvider.Stripe ? s.StripeSubscriptionId : s.ExternalId;
            if (id == null || !catalogSubscriptionIds.Contains(id)) Consider(s.Tier);
        }

        // A trial is a legacy source unless it IS one of the Cloud subscriptions (a Cloud plan
        // in its trial period grants exactly the products on it, not the whole trial tier).
        if (user.IsTrialActive && user.TrialEndDate > now)
        {
            var cloudTrial = await _context.UserSubscriptions.AsNoTracking().AnyAsync(s =>
                s.UserId == user.Id && s.Status == UserSubscriptionStatus.Trialing
                && s.StripeSubscriptionId != null && catalogSubscriptionIds.Contains(s.StripeSubscriptionId));
            if (!cloudTrial) Consider(user.SubscriptionTier ?? SubscriptionTier.Premium);
        }

        if (user.SubscriptionTier == SubscriptionTier.PremiumInherited) Consider(SubscriptionTier.PremiumInherited);
        return best;
    }

    private async Task<SubscriptionTier?> ResolveFromProjectionAsync(int userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return null;

        // Operator accounts (email allowlist) always have Admin tier. This is the only
        // way Admin is granted; it is never purchasable.
        if (AdminPolicy.IsAdminEmail(_configuration, user.Email))
        {
            return SubscriptionTier.Admin;
        }

        if (user.IsTrialActive && user.TrialEndDate > DateTime.UtcNow)
        {
            return user.SubscriptionTier ?? SubscriptionTier.Premium;
        }

        var subscription = await _context.UserSubscriptions
            .Where(s => s.UserId == userId && s.Status == UserSubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();
        if (subscription != null)
        {
            return subscription.Tier;
        }

        return null; // No active subscription
    }
}
