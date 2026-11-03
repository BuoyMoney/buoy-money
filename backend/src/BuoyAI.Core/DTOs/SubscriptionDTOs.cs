using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Subscription;

public class SubscriptionDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public DateTime NextDueDate { get; set; }
  public DateTime? LastBilledDate { get; set; }
  public SubscriptionStatus Status { get; set; }
  public SubscriptionCategory Category { get; set; }
  public bool IsDetected { get; set; }
  public string? MerchantName { get; set; }
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
}

public class CreateSubscriptionRequest
{
  public string Name { get; set; } = string.Empty;
  // Optional: the FE marks Description "(optional)" and sends null when blank. Kept
  // nullable so a non-nullable string doesn't trip the implicit [Required] model
  // validation (which 400'd subscription creation whenever no description was given).
  public string? Description { get; set; }
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public DateTime NextDueDate { get; set; }
  public SubscriptionCategory Category { get; set; }
  public string? MerchantName { get; set; }
}

public class UpdateSubscriptionRequest
{
  public string? Name { get; set; }
  public string? Description { get; set; }
  public decimal? Amount { get; set; }
  public SubscriptionFrequency? Frequency { get; set; }
  public DateTime? NextDueDate { get; set; }
  public SubscriptionStatus? Status { get; set; }
  public SubscriptionCategory? Category { get; set; }
  
  /// <summary>
  /// Hide this subscription from ReadOnly family members (e.g., for gift surprises)
  /// </summary>
  public bool? HiddenFromReadOnly { get; set; }
}

public class SubscriptionDetectionRequest
{
  public DateTime? StartDate { get; set; } // Default: 2 years ago
  public DateTime? EndDate { get; set; } // Default: now
  // Require at least 3 real occurrences before asserting a cadence. A 2-transaction group
  // has a single interval that is trivially "perfectly regular", so any two similar charges
  // would masquerade as a monthly subscription. 3 occurrences give 2+ intervals to compare.
  public int MinOccurrences { get; set; } = 3;
  public decimal AmountTolerance { get; set; } = 5.00m; // Legacy $ floor; clustering now uses a proportional tolerance
  public bool IncludeAIAnalysis { get; set; } = true; // Request AI-first analysis for Premium users
}

public class CalendarSubscriptionDto
{
  public DateTime Date { get; set; }
  public List<SubscriptionDto> Subscriptions { get; set; } = new List<SubscriptionDto>();
  public decimal TotalAmount { get; set; }
}

public class SubscriptionSummaryDto
{
  public decimal MonthlyTotal { get; set; }
  public decimal YearlyTotal { get; set; }
  public int ActiveCount { get; set; }
  public List<SubscriptionCategorySummaryDto> CategoryBreakdown { get; set; } = new List<SubscriptionCategorySummaryDto>();
}

/// <summary>
/// Summary of subscriptions by category (count and amount).
/// Different from BudgetCategorySummaryDto which tracks budget performance.
/// </summary>
public class SubscriptionCategorySummaryDto
{
  public SubscriptionCategory Category { get; set; }
  public decimal Amount { get; set; }
  public int Count { get; set; }
}

/// <summary>
/// Detailed confidence breakdown for subscription detection.
/// Each score is 0.0 to 1.0, with the overall confidence being a weighted average.
/// </summary>
public class ConfidenceBreakdown
{
  /// <summary>
  /// How regular are the intervals between transactions? (0-1)
  /// 1.0 = perfect regularity (e.g., exactly 30 days apart each time)
  /// 0.0 = highly irregular intervals
  /// </summary>
  public double IntervalRegularityScore { get; set; }
  
  /// <summary>
  /// How consistent are the amounts? (0-1)
  /// 1.0 = exact same amount each time
  /// 0.0 = wildly varying amounts
  /// </summary>
  public double AmountConsistencyScore { get; set; }
  
  /// <summary>
  /// Score based on number of occurrences (0-1)
  /// More occurrences = higher confidence
  /// </summary>
  public double OccurrenceScore { get; set; }
  
  /// <summary>
  /// Bonus from crowdsourced patterns (0-0.15)
  /// Other users confirming this merchant is a subscription
  /// </summary>
  public double CrowdsourceBoost { get; set; }
  
  /// <summary>
  /// Score from known subscription merchant matching (0-0.1)
  /// Bonus if merchant name matches known subscription services
  /// </summary>
  public double KnownMerchantBoost { get; set; }
  
  /// <summary>
  /// Penalty for gaps in the pattern (-0.2 to 0)
  /// Applied when there are missed billing cycles
  /// </summary>
  public double GapPenalty { get; set; }
  
  /// <summary>
  /// Human-readable explanation of the confidence level
  /// </summary>
  public string Explanation { get; set; } = string.Empty;
  
  /// <summary>
  /// Classification: VeryHigh (90%+), High (75-90%), Medium (60-75%), Low (below 60%)
  /// </summary>
  public string Classification { get; set; } = "Unknown";
}

public class DetectedSubscriptionDto
{
  public string MerchantName { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public DateTime EstimatedNextDue { get; set; }
  public double Confidence { get; set; }
  public List<int> RelatedTransactionIds { get; set; } = new List<int>();
  public bool IsSubscriptionLikely { get; set; }

  /// <summary>
  /// Date of the most-recent matched transaction. Carried through confirm so the
  /// created Subscription's LastBilledDate is populated (drives "possibly cancelled").
  /// </summary>
  public DateTime? LastTransactionDate { get; set; }
  
  /// <summary>
  /// Detailed breakdown of how confidence was calculated.
  /// Useful for AI to understand detection quality and for users to see why something was flagged.
  /// </summary>
  public ConfidenceBreakdown? ConfidenceDetails { get; set; }
  
  // AI-enhanced fields (populated for Premium users)
  public SubscriptionCategory? AISuggestedCategory { get; set; }
  public string? AIInsight { get; set; } // e.g., "Price increased $2 from last year"
  public bool IsAIEnhanced { get; set; } = false;
  
  // Price change detection
  public decimal? PreviousAmount { get; set; }
  public decimal? PriceChangeAmount { get; set; }
  public double? PriceChangePercent { get; set; }
  
  // Detection source
  public string DetectionMethod { get; set; } = "pattern"; // "pattern" or "ai-enhanced"
}

/// <summary>
/// Response for subscription detection including pending review items
/// </summary>
public class SubscriptionDetectionResponse
{
  public List<DetectedSubscriptionDto> DetectedSubscriptions { get; set; } = new();
  public int TotalDetected { get; set; }
  public int HighConfidenceCount { get; set; }
  public int PendingReviewCount { get; set; }
  public bool IsAIEnhanced { get; set; }
  public DateTime DetectionDate { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Request to dismiss a detected subscription pattern
/// </summary>
public class DismissSubscriptionRequest
{
  public string MerchantName { get; set; } = string.Empty;
  public decimal? Amount { get; set; }
  public string? Reason { get; set; }
}

/// <summary>
/// Types of billing events in the subscription timeline
/// </summary>
public enum BillingEventType
{
  /// <summary>Past confirmed transaction</summary>
  Past = 0,
  /// <summary>The immediate next expected charge</summary>
  Next = 1,
  /// <summary>Future expected charge (after the next one)</summary>
  Future = 2
}

/// <summary>
/// A single billing event in a subscription's timeline
/// </summary>
public class BillingEventDto
{
  public DateTime Date { get; set; }
  public decimal Amount { get; set; }
  public BillingEventType EventType { get; set; }
  /// <summary>Transaction ID if this is a past confirmed payment</summary>
  public int? TransactionId { get; set; }
  /// <summary>True if this was an actual transaction, false if calculated/expected</summary>
  public bool IsConfirmed { get; set; }
}

/// <summary>
/// Timeline data for a single subscription showing historical and future billing events
/// </summary>
public class SubscriptionTimelineDto
{
  public int SubscriptionId { get; set; }
  public string Name { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public SubscriptionCategory Category { get; set; }
  public SubscriptionStatus Status { get; set; }
  public List<BillingEventDto> BillingEvents { get; set; } = new();
  /// <summary>Number of billing cycles missed (may indicate cancelled subscription)</summary>
  public int MissedBillingCycles { get; set; }
  /// <summary>True if this subscription appears to be cancelled (no recent charges)</summary>
  public bool IsPotentiallyCancelled { get; set; }
}

/// <summary>
/// Request parameters for timeline data
/// </summary>
public class SubscriptionTimelineRequest
{
  /// <summary>Start date for historical data (defaults to 6 months ago)</summary>
  public DateTime? StartDate { get; set; }
  /// <summary>End date for future projections (defaults to 2 months ahead)</summary>
  public DateTime? EndDate { get; set; }
  /// <summary>Number of future billing events to project per subscription (defaults to 3)</summary>
  public int FutureProjections { get; set; } = 3;
}

/// <summary>
/// Calendar event for the enhanced calendar view, combining past/current/future billing events
/// </summary>
public class CalendarBillingEventDto
{
  public DateTime Date { get; set; }
  public int SubscriptionId { get; set; }
  public string SubscriptionName { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public BillingEventType EventType { get; set; }
  /// <summary>True if this is from an actual transaction</summary>
  public bool IsConfirmed { get; set; }
  public int? TransactionId { get; set; }
}
