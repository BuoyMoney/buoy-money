using System.ComponentModel.DataAnnotations;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.CategorizationRule;

/// <summary>
/// DTO for returning categorization rule data
/// </summary>
public class CategorizationRuleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Priority { get; set; }
    
    /// <summary>How this rule was created</summary>
    public RuleSource Source { get; set; }
    
    /// <summary>If set, this rule applies to all family members</summary>
    public int? FamilyId { get; set; }

    /// <summary>Who created the rule. Only they can pause or delete it (family members see
    /// shared rules but get 404 on toggle/delete), so the app hides those controls for others.</summary>
    public int OwnerUserId { get; set; }
    
    // Match criteria
    public string? PlaidMerchantEntityId { get; set; }
    public string? MerchantContains { get; set; }
    public string? MerchantExact { get; set; }
    public string? DescriptionContains { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    
    // Action
    /// <summary>
    /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
    /// </summary>
    public int? AssignCategoryId { get; set; }
    public string? AssignCategoryName { get; set; }
    
    // Statistics
    public int TimesApplied { get; set; }
    public DateTime? LastAppliedAt { get; set; }
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// DTO for creating a new categorization rule
/// </summary>
public class CreateCategorizationRuleRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;
    
    public bool IsActive { get; set; } = true;
    
    public int Priority { get; set; } = 0;
    
    /// <summary>How this rule was created</summary>
    public RuleSource Source { get; set; } = RuleSource.UserCreated;
    
    /// <summary>If true, share with family members</summary>
    public bool ShareWithFamily { get; set; } = false;
    
    // Match criteria (at least one should be provided)
    [StringLength(100)]
    public string? PlaidMerchantEntityId { get; set; }
    
    [StringLength(200)]
    public string? MerchantContains { get; set; }
    
    [StringLength(200)]
    public string? MerchantExact { get; set; }
    
    [StringLength(200)]
    public string? DescriptionContains { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal? MinAmount { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal? MaxAmount { get; set; }
    
    // Action
    /// <summary>
    /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
    /// </summary>
    public int? AssignCategoryId { get; set; }
}

/// <summary>
/// DTO for updating an existing categorization rule
/// </summary>
public class UpdateCategorizationRuleRequest
{
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }
    
    public bool? IsActive { get; set; }
    
    public int? Priority { get; set; }
    
    // Match criteria
    [StringLength(200)]
    public string? MerchantContains { get; set; }
    
    [StringLength(200)]
    public string? MerchantExact { get; set; }
    
    [StringLength(200)]
    public string? DescriptionContains { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal? MinAmount { get; set; }
    
    [Range(0, double.MaxValue)]
    public decimal? MaxAmount { get; set; }
    
    // Action
    /// <summary>
    /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
    /// </summary>
    public int? AssignCategoryId { get; set; }
    
    /// <summary>
    /// Set to true to clear the MerchantContains field
    /// </summary>
    public bool ClearMerchantContains { get; set; }
    
    /// <summary>
    /// Set to true to clear the MerchantExact field
    /// </summary>
    public bool ClearMerchantExact { get; set; }
    
    /// <summary>
    /// Set to true to clear the DescriptionContains field
    /// </summary>
    public bool ClearDescriptionContains { get; set; }
    
    /// <summary>
    /// Set to true to clear the min/max amount fields
    /// </summary>
    public bool ClearAmountRange { get; set; }
    
    /// <summary>
    /// Set to true to clear the assigned category
    /// </summary>
    public bool ClearAssignCategoryId { get; set; }
}

/// <summary>
/// DTO for creating a rule from a transaction (convenience endpoint)
/// </summary>
public class CreateRuleFromTransactionRequest
{
    [Required]
    public int TransactionId { get; set; }
    
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string RuleName { get; set; } = string.Empty;
    
    /// <summary>
    /// Match type: "exact" (exact merchant match), "contains" (partial match), or "plaid" (use Plaid entity ID)
    /// </summary>
    [Required]
    public string MatchType { get; set; } = "contains";
    
    /// <summary>
    /// Whether to include amount range in the rule
    /// </summary>
    public bool IncludeAmountRange { get; set; } = false;
    
    /// <summary>
    /// Amount tolerance percentage (e.g., 0.1 = 10% tolerance)
    /// </summary>
    [Range(0, 1)]
    public decimal AmountTolerance { get; set; } = 0.1m;
    
    /// <summary>How this rule was created</summary>
    public RuleSource Source { get; set; } = RuleSource.UserCreated;
    
    /// <summary>If true, share with family members</summary>
    public bool ShareWithFamily { get; set; } = false;
    
    /// <summary>
    /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
    /// </summary>
    public int? AssignCategoryId { get; set; }
}

/// <summary>
/// DTO for creating a rule when user recategorizes a transaction
/// </summary>
public class CreateRuleFromCategorizationRequest
{
    [Required]
    public int TransactionId { get; set; }
    
    /// <summary>
    /// Unified category ID to assign: 1-99 for system categories, 1000+ for custom categories.
    /// </summary>
    public int? NewCategoryId { get; set; }
    
    /// <summary>If true, share with family members</summary>
    public bool ShareWithFamily { get; set; } = true;
    
    /// <summary>
    /// Whether to use Plaid entity ID for matching (more reliable)
    /// </summary>
    public bool UsePlaidEntityId { get; set; } = true;
}
