namespace BuoyAI.Core.Entities;

/// <summary>
/// Aggregate percentile cutoffs for one Wrapped ranking metric (e.g.
/// savings_rate), recomputed nightly across the active cohort. Only cutoffs
/// are stored — never per-user values — and the client-facing card is gated
/// behind a minimum cohort size so small populations can't be deanonymized.
/// </summary>
public class WrappedStatCutoff
{
  /// <summary>Metric key: savings_rate | zero_spend_days | longest_streak.</summary>
  public string MetricKey { get; set; } = string.Empty;

  /// <summary>JSON array of decile cutoffs, p10..p90 ascending.</summary>
  public string CutoffsJson { get; set; } = string.Empty;

  /// <summary>How many users contributed to the distribution.</summary>
  public int Cohort { get; set; }

  public DateTime ComputedAt { get; set; }
}

/// <summary>
/// Cached bonus cards for one user's recap period (Month = 0 for the yearly).
/// Written once by the recap job (AI garnish or deterministic rotation), so a
/// story is stable across opens and the read path never calls a model.
/// </summary>
public class WrappedBonusCardCache
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int Year { get; set; }
  public int Month { get; set; }
  public string CardsJson { get; set; } = string.Empty;
  public DateTime GeneratedAt { get; set; }
}
