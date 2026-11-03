using BuoyAI.Core.DTOs.CategorizationRule;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for managing user-defined categorization rules
/// </summary>
public interface ICategorizationRuleService
{
    /// <summary>
    /// Get all rules for a user, including family-shared rules if applicable
    /// </summary>
    Task<List<CategorizationRuleDto>> GetUserRulesAsync(int userId, bool includeInactive = false);
    
    /// <summary>
    /// Get all rules for a user and their family
    /// </summary>
    Task<List<CategorizationRuleDto>> GetUserAndFamilyRulesAsync(int userId, int? familyId, bool includeInactive = false);

    /// <summary>
    /// Get a specific rule by ID
    /// </summary>
    Task<CategorizationRuleDto?> GetRuleAsync(int userId, int ruleId);

    /// <summary>
    /// Create a new categorization rule
    /// </summary>
    Task<CategorizationRuleDto> CreateRuleAsync(int userId, CreateCategorizationRuleRequest request);

    /// <summary>
    /// Create a rule from an existing transaction
    /// </summary>
    Task<CategorizationRuleDto> CreateRuleFromTransactionAsync(int userId, CreateRuleFromTransactionRequest request);
    
    /// <summary>
    /// Create a rule when user recategorizes a transaction ("Always categorize as")
    /// </summary>
    Task<CategorizationRuleDto> CreateRuleFromCategorizationAsync(int userId, CreateRuleFromCategorizationRequest request);

    /// <summary>
    /// Update an existing rule
    /// </summary>
    Task<CategorizationRuleDto> UpdateRuleAsync(int userId, int ruleId, UpdateCategorizationRuleRequest request);

    /// <summary>
    /// Delete a rule
    /// </summary>
    Task<bool> DeleteRuleAsync(int userId, int ruleId);

    /// <summary>
    /// Toggle a rule's active status
    /// </summary>
    Task<CategorizationRuleDto> ToggleRuleAsync(int userId, int ruleId);

    /// <summary>
    /// Reorder rules (change priorities)
    /// </summary>
    Task<bool> ReorderRulesAsync(int userId, List<int> ruleIds);

    /// <summary>
    /// Find the first matching rule for a transaction
    /// </summary>
    Task<Entities.CategorizationRule?> FindMatchingRuleAsync(int userId, Transaction transaction);

    /// <summary>
    /// Apply user rules to a transaction (returns true if a rule was applied)
    /// </summary>
    Task<bool> ApplyRulesToTransactionAsync(int userId, Transaction transaction);

    /// <summary>
    /// Apply user rules to multiple transactions
    /// </summary>
    Task<int> ApplyRulesToTransactionsAsync(int userId, IEnumerable<Transaction> transactions);

    /// <summary>
    /// Apply user rules to transactions by ID, returning IDs of transactions that weren't matched
    /// </summary>
    Task<(int appliedCount, List<int> unmatchedTransactionIds)> ApplyRulesToTransactionIdsAsync(int userId, List<int> transactionIds);

    /// <summary>
    /// Get rule statistics
    /// </summary>
    Task<RuleStatisticsDto> GetRuleStatisticsAsync(int userId);

    /// <summary>
    /// Deactivate any rules that assign the given category. Called when a custom
    /// category is deleted so orphaned rules (AssignCategoryId set-null by the FK)
    /// don't keep matching transactions. Returns the number of rules deactivated.
    /// </summary>
    Task<int> DeactivateRulesForCategoryAsync(int categoryId);
}

/// <summary>
/// DTO for rule statistics
/// </summary>
public class RuleStatisticsDto
{
    public int TotalRules { get; set; }
    public int ActiveRules { get; set; }
    public int TotalTimesApplied { get; set; }
    public DateTime? LastRuleApplied { get; set; }
}
