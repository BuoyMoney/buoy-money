namespace BuoyAI.Core.Entities;

/// <summary>
/// Anonymized, aggregated subscription pattern data from all users.
/// Used to improve detection accuracy by learning from confirmed/dismissed patterns.
/// No user PII is stored - only aggregated pattern data.
/// Users can opt-out of contributing to this data via their preferences.
/// </summary>
public class CrowdsourcedSubscriptionPattern
{
  public int Id { get; set; }
  
  /// <summary>
  /// Normalized merchant name (lowercase, trimmed, common variations unified)
  /// e.g., "netflix", "spotify", "adobe creative cloud"
  /// </summary>
  public string NormalizedMerchantName { get; set; } = string.Empty;
  
  /// <summary>
  /// Typical amount range - minimum
  /// </summary>
  public decimal TypicalAmountMin { get; set; }
  
  /// <summary>
  /// Typical amount range - maximum
  /// </summary>
  public decimal TypicalAmountMax { get; set; }
  
  /// <summary>
  /// Most common frequency for this merchant
  /// </summary>
  public int TypicalFrequency { get; set; } // Maps to SubscriptionFrequency enum
  
  /// <summary>
  /// Number of users who confirmed this as a subscription
  /// </summary>
  public int ConfirmationCount { get; set; }
  
  /// <summary>
  /// Number of users who dismissed this as not a subscription
  /// </summary>
  public int DismissalCount { get; set; }
  
  /// <summary>
  /// Calculated confidence boost based on confirmation rate.
  /// Formula: if ConfirmationCount >= 10 and DismissalRate < 30%, boost = 0.15
  /// Stored for quick lookup without recalculation.
  /// </summary>
  public double ConfidenceBoost { get; set; }
  
  /// <summary>
  /// Most commonly confirmed category for this merchant
  /// </summary>
  public int? MostCommonCategory { get; set; } // Maps to SubscriptionCategory enum
  
  /// <summary>
  /// When this pattern was first created
  /// </summary>
  public DateTime CreatedAt { get; set; }
  
  /// <summary>
  /// When this pattern was last updated (confirmation/dismissal)
  /// </summary>
  public DateTime UpdatedAt { get; set; }
  
  /// <summary>
  /// Version for optimistic concurrency
  /// </summary>
  public int Version { get; set; }

  // Navigation properties
  public ICollection<SuggestedSubscription> SuggestedSubscriptions { get; set; } = new List<SuggestedSubscription>();
  
  /// <summary>
  /// Calculate the confirmation rate (0.0 to 1.0)
  /// </summary>
  public double GetConfirmationRate()
  {
    var total = ConfirmationCount + DismissalCount;
    return total > 0 ? (double)ConfirmationCount / total : 0;
  }
  
  /// <summary>
  /// Recalculate confidence boost based on current confirmation/dismissal counts.
  /// Returns 0.15 if 10+ confirmations and less than 30% dismissal rate.
  /// Returns 0.10 if 5+ confirmations and less than 40% dismissal rate.
  /// Returns 0 otherwise.
  /// </summary>
  public double CalculateConfidenceBoost()
  {
    var total = ConfirmationCount + DismissalCount;
    if (total == 0) return 0;
    
    var dismissalRate = (double)DismissalCount / total;
    
    if (ConfirmationCount >= 10 && dismissalRate < 0.30)
      return 0.15;
    
    if (ConfirmationCount >= 5 && dismissalRate < 0.40)
      return 0.10;
    
    return 0;
  }
}
