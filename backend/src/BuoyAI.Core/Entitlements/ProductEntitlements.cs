using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entitlements;

/// <summary>
/// The products a Buoy Money Cloud customer can hold, as flags the rest of the app can ask
/// about. These are the names stored in <c>UserEntitlements.Entitlement</c> and written on
/// Stripe prices as <c>buoy_entitlement</c> metadata, so they are a contract: never rename one.
/// </summary>
public static class ProductEntitlements
{
    /// <summary>Connect a bank and sync its transactions.</summary>
    public const string BankSync = "bank_sync";

    /// <summary>The AI features (chat, insights, categorization) at the monthly allowance.</summary>
    public const string Ai = "ai";

    /// <summary>Refresh a bank on demand. Only means something together with <see cref="BankSync"/>.</summary>
    public const string InstantRefresh = "instant_refresh";

    /// <summary>
    /// Extra bank logins. Not a flag: the row's quantity is how many logins are added on top of
    /// the ones <see cref="BankSync"/> includes. Only counts together with <see cref="BankSync"/>.
    /// </summary>
    public const string ExtraLogins = "extra_logins";

    public static readonly IReadOnlyList<string> Flags = [BankSync, Ai, InstantRefresh];

    public static bool IsKnown(string? name) =>
        name == BankSync || name == Ai || name == InstantRefresh || name == ExtraLogins;

    /// <summary>
    /// Splits a Stripe price's <c>buoy_entitlement</c> value ("bank_sync" or, for a bundle,
    /// "bank_sync,ai") into the known names. Unknown names are dropped, so a typo in Stripe
    /// grants nothing instead of something.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Where(IsKnown)
            .Distinct()
            .ToList();
    }
}

/// <summary>
/// What a user may use right now, after the dependency rules. Built by
/// <c>IEntitlementService.GetProductAccessAsync</c>; never trust a JWT claim for this.
/// </summary>
/// <param name="EndsAt">
/// Products that stay on until a known date and then lapse (removal scheduled at period end,
/// or the whole plan cancelled). Absent means no end is scheduled.
/// </param>
public sealed record ProductAccess(
    bool BankSync,
    bool Ai,
    bool InstantRefresh,
    int ExtraLogins,
    IReadOnlyDictionary<string, DateTime> EndsAt,
    bool Founding)
{
    public static readonly ProductAccess None = new(false, false, false, 0, new Dictionary<string, DateTime>(), false);

    /// <summary>Everything on, no extra-login count. Admins and self-hosted servers.</summary>
    public static readonly ProductAccess All = new(true, true, true, 0, new Dictionary<string, DateTime>(), false);

    public bool Has(string entitlement) => entitlement switch
    {
        ProductEntitlements.BankSync => BankSync,
        ProductEntitlements.Ai => Ai,
        ProductEntitlements.InstantRefresh => InstantRefresh,
        _ => false
    };

    public bool IsEmpty => !BankSync && !Ai && !InstantRefresh;
}

/// <summary>
/// The rules that turn a set of raw entitlements into what a user really gets. One place, so
/// the projection (writing) and the entitlement service (reading) cannot disagree.
/// </summary>
public static class ProductRules
{
    /// <summary>
    /// Instant refresh and extra logins both need bank sync. Without it they are dropped, not
    /// merely hidden: a refresh button on an account with no bank would be a bug and a cost.
    /// </summary>
    public static ProductAccess Normalize(
        IEnumerable<string> flags,
        int extraLogins,
        IReadOnlyDictionary<string, DateTime>? endsAt = null,
        bool founding = false)
    {
        var set = new HashSet<string>(flags);
        var bank = set.Contains(ProductEntitlements.BankSync);
        var ends = new Dictionary<string, DateTime>();
        if (endsAt != null)
        {
            foreach (var (k, v) in endsAt)
            {
                if (ProductEntitlements.IsKnown(k)) ends[k] = v;
            }
        }
        if (!bank)
        {
            ends.Remove(ProductEntitlements.InstantRefresh);
            ends.Remove(ProductEntitlements.ExtraLogins);
        }

        return new ProductAccess(
            bank,
            set.Contains(ProductEntitlements.Ai),
            bank && set.Contains(ProductEntitlements.InstantRefresh),
            bank ? Math.Max(0, extraLogins) : 0,
            ends,
            founding);
    }

    /// <summary>
    /// The legacy tier the older gates still read: any AI means Premium (chat is a Premium
    /// feature), bank sync alone is Standard, anything else is no tier. Plaid is gated by
    /// <see cref="ProductEntitlements.BankSync"/> separately, so an AI-only customer gets the
    /// AI features without a free bank connection.
    /// </summary>
    public static SubscriptionTier? LegacyTierFor(ProductAccess access)
    {
        if (access.Ai) return SubscriptionTier.Premium;
        if (access.BankSync) return SubscriptionTier.Standard;
        return null;
    }

    /// <summary>
    /// Products implied by a legacy tier, so hosted Standard and Premium subscribers keep
    /// working under the product checks. Configurable under
    /// <c>Entitlements:LegacyTierProducts:{Tier}</c> (comma list); the defaults give both paid
    /// tiers bank sync and AI, and never instant refresh.
    /// </summary>
    public static ProductAccess ForLegacyTier(SubscriptionTier tier, Func<string, string?> config)
    {
        if (tier == SubscriptionTier.Admin) return ProductAccess.All;

        var key = tier == SubscriptionTier.PremiumInherited ? SubscriptionTier.Premium : tier;
        var configured = config($"Entitlements:LegacyTierProducts:{key}");
        var names = configured != null
            ? ProductEntitlements.Parse(configured)
            : [ProductEntitlements.BankSync, ProductEntitlements.Ai];
        return Normalize(names, 0);
    }

    /// <summary>
    /// Union of two accesses. A product either side keeps with no end date has no end date; when
    /// both sides end it, the later date wins.
    /// </summary>
    public static ProductAccess Union(ProductAccess a, ProductAccess b)
    {
        var ends = new Dictionary<string, DateTime>();
        foreach (var name in ProductEntitlements.Flags)
        {
            var holders = new List<ProductAccess>();
            if (a.Has(name)) holders.Add(a);
            if (b.Has(name)) holders.Add(b);
            if (holders.Count == 0) continue;
            if (holders.Any(h => !h.EndsAt.ContainsKey(name))) continue;
            ends[name] = holders.Max(h => h.EndsAt[name]);
        }

        return Normalize(
            ProductEntitlements.Flags.Where(f => a.Has(f) || b.Has(f)),
            a.ExtraLogins + b.ExtraLogins,
            ends,
            a.Founding || b.Founding);
    }
}
