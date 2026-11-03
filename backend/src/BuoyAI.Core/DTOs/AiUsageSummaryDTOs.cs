namespace BuoyAI.Core.DTOs.AI;

/// <summary>One feature's share of the period's AI spend. Money is whole micro-dollars (1 USD = 1,000,000).</summary>
public sealed record AiFeatureUsage(string Feature, int Calls, long CostMicros);

/// <summary>Pay-as-you-go: spend beyond the allowance, only with the customer's own cap.</summary>
public sealed record AiPaygStatus(bool Available, bool Enabled, long CapMicros, long UsedMicros, long MaxCapMicros)
{
    public static readonly AiPaygStatus NotAvailable = new(false, false, 0, 0, 0);
}

/// <summary>
/// What the usage screen shows. <see cref="Source"/> is "cloud" when the allowance lives in BuoyAI Cloud (one allowance for the
/// whole server) and "local" when it is this server's own per-account tracking. <see cref="Unlimited"/> is true when nothing
/// caps AI here (a self-hosted server on its own model or key), so the screen shows spend as statistics only.
/// </summary>
public sealed record AiUsageSummary(
    string Source,
    bool Unlimited,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    long AllowanceMicros,
    long UsedMicros,
    int Percent,
    string State,
    bool Blocked,
    AiPaygStatus Payg,
    IReadOnlyList<AiFeatureUsage> ByFeature,
    long ProjectedMicros);

/// <summary>
/// One AI call to record. Tokens are what the provider reported unless <paramref name="TokensEstimated"/> says otherwise.
/// <paramref name="CountsAgainstRequestLimits"/> keeps the hourly and daily request counters exactly as they were: only the
/// paths that always counted (chat, categorization) set it.
/// </summary>
public sealed record AiUsageEntry(
    int UserId,
    string Model,
    string? Provider,
    int InputTokens,
    int OutputTokens,
    bool TokensEstimated,
    decimal Cost,
    string RequestContext,
    bool CountsAgainstRequestLimits = false,
    bool EmitMetrics = true);
