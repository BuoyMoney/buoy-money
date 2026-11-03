using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a subscription detected by the system but not yet confirmed by the user.
/// Users can confirm, dismiss, or snooze these suggestions.
/// </summary>
public class SuggestedSubscription
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int? FamilyId { get; set; }
  
  /// <summary>
  /// The normalized merchant name for matching (lowercase, trimmed, common variations unified)
  /// </summary>
  public string NormalizedMerchantName { get; set; } = string.Empty;
  
  /// <summary>
  /// Display-friendly merchant name
  /// </summary>
  public string DisplayMerchantName { get; set; } = string.Empty;
  
  /// <summary>
  /// Detected subscription amount (average or most recent)
  /// </summary>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Detected frequency (weekly, monthly, annual, etc.)
  /// </summary>
  public SubscriptionFrequency Frequency { get; set; }
  
  /// <summary>
  /// Confidence score from 0.0 to 1.0 (includes crowdsource boost if applicable)
  /// </summary>
  public double Confidence { get; set; }
  
  /// <summary>
  /// Base confidence before crowdsource boost
  /// </summary>
  public double BaseConfidence { get; set; }
  
  /// <summary>
  /// Boost added from crowdsourced pattern matching (0.0 to 0.2)
  /// </summary>
  public double CrowdsourceBoost { get; set; }
  
  /// <summary>
  /// Current status of this suggestion
  /// </summary>
  public SuggestedSubscriptionStatus Status { get; set; } = SuggestedSubscriptionStatus.Pending;
  
  /// <summary>
  /// If snoozed, when should this be shown again?
  /// </summary>
  public DateTime? SnoozeUntil { get; set; }
  
  /// <summary>
  /// Reference to crowdsource pattern if matched
  /// </summary>
  public int? CrowdsourcePatternId { get; set; }
  
  /// <summary>
  /// Whether to hide this from ReadOnly family members (for gift surprises, etc.)
  /// </summary>
  public bool HiddenFromReadOnly { get; set; } = false;
  
  /// <summary>
  /// AI-suggested category if available
  /// </summary>
  public SubscriptionCategory? AISuggestedCategory { get; set; }
  
  /// <summary>
  /// AI insight about this subscription
  /// </summary>
  public string? AIInsight { get; set; }
  
  /// <summary>
  /// Number of transactions that matched this pattern
  /// </summary>
  public int MatchedTransactionCount { get; set; }

  /// <summary>
  /// Comma-separated IDs of the transactions that matched this pattern at detection time.
  /// Persisted so that confirming the suggestion can link the real Subscription to exactly
  /// those transactions (populating billing history / LastBilledDate) without re-running
  /// detection. Null/empty when unknown (older rows).
  /// </summary>
  public string? MatchedTransactionIds { get; set; }
  
  /// <summary>
  /// Date of the most recent matching transaction
  /// </summary>
  public DateTime? LastTransactionDate { get; set; }
  
  /// <summary>
  /// Predicted next billing date based on pattern
  /// </summary>
  public DateTime? PredictedNextDate { get; set; }
  
  /// <summary>
  /// When this suggestion was first detected
  /// </summary>
  public DateTime DetectedAt { get; set; }
  
  /// <summary>
  /// When the user took action (confirmed/dismissed/snoozed)
  /// </summary>
  public DateTime? ActionTakenAt { get; set; }
  
  /// <summary>
  /// Reason for dismissal if applicable
  /// </summary>
  public string? DismissReason { get; set; }
  
  /// <summary>
  /// If confirmed, the ID of the created Subscription
  /// </summary>
  public int? ConfirmedSubscriptionId { get; set; }
  
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
  public Family? Family { get; set; }
  public CrowdsourcedSubscriptionPattern? CrowdsourcePattern { get; set; }
  public Subscription? ConfirmedSubscription { get; set; }

  /// <summary>Serialize matched transaction IDs to a compact CSV (null when empty).</summary>
  public static string? SerializeTransactionIds(IEnumerable<int>? ids)
  {
    var list = ids?.ToList() ?? new List<int>();
    return list.Count == 0 ? null : string.Join(",", list);
  }

  /// <summary>Parse the CSV of matched transaction IDs back into a list (empty when null/blank).</summary>
  public static List<int> ParseTransactionIds(string? csv)
  {
    if (string.IsNullOrWhiteSpace(csv))
      return new List<int>();

    return csv
      .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
      .Where(id => id.HasValue)
      .Select(id => id!.Value)
      .ToList();
  }
}
