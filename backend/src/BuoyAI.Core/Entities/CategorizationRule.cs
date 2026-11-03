using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// How the categorization rule was created
/// </summary>
public enum RuleSource
{
    /// <summary>User manually created the rule</summary>
    UserCreated = 0,
    
    /// <summary>AI learned from user recategorization behavior</summary>
    AILearned = 1,
    
    /// <summary>User clicked "Always categorize as" for a merchant</summary>
    MerchantPreference = 2
}

/// <summary>
/// Represents a user-defined rule for automatically categorizing transactions.
/// Rules are applied in priority order before Plaid and AI categorization.
/// </summary>
public class CategorizationRule
{
    public int Id { get; set; }
    
    /// <summary>
    /// The user who owns this rule
    /// </summary>
    public int UserId { get; set; }
    
    /// <summary>
    /// Optional family ID - if set, this rule applies to all family members
    /// </summary>
    public int? FamilyId { get; set; }
    
    /// <summary>
    /// Display name for the rule (e.g., "Coffee Shops", "Netflix Subscription")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// How this rule was created
    /// </summary>
    public RuleSource Source { get; set; } = RuleSource.UserCreated;
    
    /// <summary>
    /// Whether the rule is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;
    
    /// <summary>
    /// Priority order for rule evaluation (lower = higher priority)
    /// </summary>
    public int Priority { get; set; } = 0;
    
    // ==================== Match Criteria ====================
    
    /// <summary>
    /// Plaid merchant entity ID for reliable matching across transaction variations
    /// </summary>
    public string? PlaidMerchantEntityId { get; set; }
    
    /// <summary>
    /// Merchant name to match (case-insensitive contains match)
    /// </summary>
    public string? MerchantContains { get; set; }
    
    /// <summary>
    /// Exact merchant name match (case-insensitive)
    /// </summary>
    public string? MerchantExact { get; set; }
    
    /// <summary>
    /// Description contains match (case-insensitive)
    /// </summary>
    public string? DescriptionContains { get; set; }
    
    /// <summary>
    /// Minimum amount for the rule to apply (inclusive)
    /// </summary>
    public decimal? MinAmount { get; set; }
    
    /// <summary>
    /// Maximum amount for the rule to apply (inclusive)
    /// </summary>
    public decimal? MaxAmount { get; set; }
    
    // ==================== Action (Category Assignment) ====================
    
    /// <summary>
    /// Category to assign when rule matches (references unified Categories table).
    /// </summary>
    public int? AssignCategoryId { get; set; }
    
    // ==================== Statistics ====================
    
    /// <summary>
    /// Number of times this rule has been applied
    /// </summary>
    public int TimesApplied { get; set; } = 0;
    
    /// <summary>
    /// Last time this rule was applied
    /// </summary>
    public DateTime? LastAppliedAt { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public User User { get; set; } = null!;
    public Family? Family { get; set; }
    
    /// <summary>
    /// Navigation property for the unified category assignment.
    /// </summary>
    public Category? AssignCategoryNav { get; set; }
    
    /// <summary>
    /// Checks if this rule matches the given transaction
    /// </summary>
    public bool Matches(Transaction transaction)
    {
        // Plaid merchant entity ID match (most reliable)
        if (!string.IsNullOrEmpty(PlaidMerchantEntityId) &&
            (string.IsNullOrEmpty(transaction.PlaidMerchantEntityId) ||
             !string.Equals(transaction.PlaidMerchantEntityId, PlaidMerchantEntityId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        
        // Merchant exact match (case-insensitive)
        if (!string.IsNullOrEmpty(MerchantExact))
        {
            if (!string.Equals(transaction.MerchantName, MerchantExact, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        
        // Merchant contains match (case-insensitive)
        if (!string.IsNullOrEmpty(MerchantContains))
        {
            if (string.IsNullOrEmpty(transaction.MerchantName) || 
                !transaction.MerchantName.Contains(MerchantContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        
        // Description contains match (case-insensitive)
        if (!string.IsNullOrEmpty(DescriptionContains))
        {
            if (string.IsNullOrEmpty(transaction.Description) ||
                !transaction.Description.Contains(DescriptionContains, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        
        // Amount range check
        var amount = Math.Abs(transaction.Amount);
        if (MinAmount.HasValue && amount < MinAmount.Value)
        {
            return false;
        }
        
        if (MaxAmount.HasValue && amount > MaxAmount.Value)
        {
            return false;
        }
        
        return true;
    }
}
