using System.Globalization;
using BuoyAI.Core.DTOs.Billing;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Entitlements;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>One product a subscription grants, before it is written to <c>UserEntitlements</c>.</summary>
public sealed record ProjectedEntitlement(
    string Entitlement,
    int Quantity,
    string Status,
    DateTime? PaidThrough,
    DateTime? EndsAt,
    bool Founding);

/// <summary>
/// Turns a subscription snapshot into the set of products it grants. Pure: no database, no
/// Stripe. This is the function that decides who gets what for money, so it is deliberately
/// small and has its own table of tests.
/// </summary>
public static class EntitlementProjector
{
    /// <summary>
    /// The products for this snapshot, or null when the status says "leave things as they are"
    /// (an unrecognised status, or a first payment still pending). An empty list means the
    /// subscription grants nothing now, so its rows are removed.
    /// </summary>
    public static IReadOnlyList<ProjectedEntitlement>? Project(StripeSubscriptionSnapshot snapshot, ILogger? logger = null)
    {
        string status;
        switch (snapshot.Status)
        {
            case "active":
            case "trialing":
                status = EntitlementStatus.Active;
                break;
            case "past_due":
            case "unpaid":
                // Same dunning rule as the tier: keep access while Stripe retries the card.
                status = EntitlementStatus.PastDue;
                break;
            case "paused":
            case "canceled":
            case "incomplete_expired":
                return [];
            default:
                // "incomplete" and anything Stripe adds later: neither grant nor revoke.
                return null;
        }

        var items = snapshot.Items ?? [];
        var flags = new Dictionary<string, bool>();   // name -> founding
        var extraLogins = 0;
        var extraFounding = false;
        foreach (var item in items)
        {
            var names = ProductEntitlements.Parse(item.Entitlements);
            if (names.Count == 0)
            {
                logger?.LogWarning(
                    "Subscription {SubscriptionId} has item {PriceId} ({LookupKey}) with no known buoy_entitlement; it grants nothing",
                    snapshot.SubscriptionId, item.PriceId, item.LookupKey);
                continue;
            }

            foreach (var name in names)
            {
                if (name == ProductEntitlements.ExtraLogins)
                {
                    extraLogins += (int)Math.Clamp(item.Quantity, 0, 10_000);
                    extraFounding |= item.Founding;
                }
                else
                {
                    flags[name] = flags.GetValueOrDefault(name) | item.Founding;
                }
            }
        }

        var hasBank = flags.ContainsKey(ProductEntitlements.BankSync);
        if (!hasBank && (flags.ContainsKey(ProductEntitlements.InstantRefresh) || extraLogins > 0))
        {
            logger?.LogWarning(
                "Subscription {SubscriptionId} has instant refresh or extra logins without bank sync; ignoring them",
                snapshot.SubscriptionId);
        }

        var ends = ScheduledEnds(snapshot);
        // Removing bank sync takes its dependants with it on the same date.
        if (ends.TryGetValue(ProductEntitlements.BankSync, out var bankEnd))
        {
            foreach (var dep in new[] { ProductEntitlements.InstantRefresh, ProductEntitlements.ExtraLogins })
            {
                ends[dep] = ends.TryGetValue(dep, out var own) && own < bankEnd ? own : bankEnd;
            }
        }

        var rows = new List<ProjectedEntitlement>();
        foreach (var (name, founding) in flags.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (name == ProductEntitlements.InstantRefresh && !hasBank) continue;
            rows.Add(new ProjectedEntitlement(name, 1, status, snapshot.CurrentPeriodEnd, EndFor(ends, name), founding));
        }
        if (hasBank && extraLogins > 0)
        {
            rows.Add(new ProjectedEntitlement(
                ProductEntitlements.ExtraLogins, extraLogins, status, snapshot.CurrentPeriodEnd,
                EndFor(ends, ProductEntitlements.ExtraLogins), extraFounding));
        }
        return rows;
    }

    /// <summary>The access these rows add up to, with the dependency rules applied.</summary>
    public static ProductAccess ToAccess(IEnumerable<ProjectedEntitlement> rows)
    {
        var list = rows.ToList();
        var ends = list.Where(r => r.EndsAt != null)
            .ToDictionary(r => r.Entitlement, r => r.EndsAt!.Value);
        return ProductRules.Normalize(
            list.Where(r => r.Entitlement != ProductEntitlements.ExtraLogins).Select(r => r.Entitlement),
            list.Where(r => r.Entitlement == ProductEntitlements.ExtraLogins).Sum(r => r.Quantity),
            ends,
            list.Any(r => r.Founding));
    }

    /// <summary>
    /// Dates a product is known to lapse: a removal the cloud service scheduled
    /// (<c>pending_end_&lt;name&gt;</c> metadata) or the whole plan being cancelled at period end.
    /// Access ends at this date even if Stripe has not acted yet (the entitlement reader ignores
    /// rows past their end); Stripe dropping the item later just removes the row.
    /// </summary>
    private static Dictionary<string, DateTime> ScheduledEnds(StripeSubscriptionSnapshot snapshot)
    {
        var ends = new Dictionary<string, DateTime>();
        if (snapshot.Metadata != null)
        {
            foreach (var (key, value) in snapshot.Metadata)
            {
                if (!key.StartsWith(StripeSnapshotMapper.PendingEndPrefix, StringComparison.Ordinal)) continue;
                var name = key[StripeSnapshotMapper.PendingEndPrefix.Length..];
                if (!ProductEntitlements.IsKnown(name)) continue;
                // A date already in the past is a leftover (the removal happened and the key was
                // never cleared). Ignoring it keeps a product bought again from being cut off.
                if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                    && when > DateTime.UtcNow)
                {
                    ends[name] = when;
                }
            }
        }

        if (snapshot.CancelAtPeriodEnd && snapshot.CurrentPeriodEnd is DateTime periodEnd && periodEnd > DateTime.UtcNow)
        {
            foreach (var name in new[]
                     {
                         ProductEntitlements.BankSync, ProductEntitlements.Ai,
                         ProductEntitlements.InstantRefresh, ProductEntitlements.ExtraLogins
                     })
            {
                ends[name] = ends.TryGetValue(name, out var own) && own < periodEnd ? own : periodEnd;
            }
        }
        return ends;
    }

    private static DateTime? EndFor(Dictionary<string, DateTime> ends, string name) =>
        ends.TryGetValue(name, out var d) ? d : null;
}
