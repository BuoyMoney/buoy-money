using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

public class Subscription
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int? FamilyId { get; set; } // Foreign key to Family
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public SubscriptionFrequency Frequency { get; set; }
  public DateTime NextDueDate { get; set; }
  public DateTime? LastBilledDate { get; set; }
  public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
  public SubscriptionCategory Category { get; set; } = SubscriptionCategory.Other;
  public bool IsDetected { get; set; } = false; // Auto-detected vs manually added
  public string? MerchantName { get; set; }
  public int? LinkedTransactionId { get; set; } // Reference to triggering transaction
  
  /// <summary>
  /// Whether to hide this subscription from ReadOnly family members.
  /// Useful for hiding gift subscriptions or surprises from children, etc.
  /// Only BudgetAdmin+ roles can toggle this.
  /// </summary>
  public bool HiddenFromReadOnly { get; set; } = false;
  
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
  public Family? Family { get; set; }
  public Transaction? LinkedTransaction { get; set; }
  public ICollection<SubscriptionTransaction> SubscriptionTransactions { get; set; } = new List<SubscriptionTransaction>();
}
