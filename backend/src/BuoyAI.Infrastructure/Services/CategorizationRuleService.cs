using BuoyAI.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.DTOs.CategorizationRule;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Interfaces.Repositories;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

public class CategorizationRuleService : ICategorizationRuleService
{
    private readonly BuoyAIDbContext _context;
    private readonly IUserRepository _userRepository;
    private readonly ICategoryRepository _categoryRepository;
    private readonly ILogger<CategorizationRuleService> _logger;

    public CategorizationRuleService(
        BuoyAIDbContext context,
        IUserRepository userRepository,
        ICategoryRepository categoryRepository,
        ILogger<CategorizationRuleService> logger)
    {
        _context = context;
        _userRepository = userRepository;
        _categoryRepository = categoryRepository;
        _logger = logger;
    }

    /// <summary>
    /// Validates that a rule's target category actually exists and is reachable
    /// by the user. System categories are enum-backed (1-99); custom categories
    /// (1000+) live in the unified Categories table (the FK target) and must be
    /// accessible to the user or their family. Mirrors
    /// TransactionService.UpdateTransactionCategory so rule creation/update
    /// validates against the same table the FK points at.
    /// </summary>
    private async Task ValidateAssignCategoryAsync(int assignCategoryId, int userId, int? familyId)
    {
        bool valid = assignCategoryId < 1000
            ? Enum.IsDefined(typeof(TransactionCategory), assignCategoryId)
            : await _categoryRepository.ExistsAndAccessibleAsync(assignCategoryId, userId, familyId);

        if (!valid)
        {
            throw new InvalidOperationException("Invalid category specified.");
        }
    }

    /// <summary>
    /// Loads active, family-aware rules that actually assign a category, ordered by
    /// priority. Shared by every match/apply path so single-transaction, bulk, and
    /// AI categorization behave identically: family-shared rules are honored, and
    /// rules orphaned to a null AssignCategoryId (e.g. their category was deleted —
    /// FK is SetNull) are excluded rather than forcing Uncategorized.
    /// </summary>
    private async Task<List<CategorizationRule>> LoadApplicableRulesAsync(int userId)
    {
        var user = await _userRepository.GetByIdAsync(userId);
        return await _context.CategorizationRules
            .Where(r => r.IsActive && r.AssignCategoryId != null &&
                (r.UserId == userId || (user != null && user.FamilyId.HasValue && r.FamilyId == user.FamilyId)))
            .OrderBy(r => r.Priority)
            .ToListAsync();
    }

    public async Task<List<CategorizationRuleDto>> GetUserRulesAsync(int userId, bool includeInactive = false)
    {
        try
        {
            // Get user's family ID for family-aware query
            var user = await _userRepository.GetByIdAsync(userId);
            return await GetUserAndFamilyRulesAsync(userId, user?.FamilyId, includeInactive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting categorization rules for user {UserId}", userId);
            throw;
        }
    }
    
    public async Task<List<CategorizationRuleDto>> GetUserAndFamilyRulesAsync(int userId, int? familyId, bool includeInactive = false)
    {
        try
        {
            var query = _context.CategorizationRules
                .Include(r => r.AssignCategoryNav)
                .Where(r => r.UserId == userId || (familyId.HasValue && r.FamilyId == familyId));

            if (!includeInactive)
            {
                query = query.Where(r => r.IsActive);
            }

            var rules = await query
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.Name)
                .ToListAsync();

            return rules.Select(MapToDto).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting categorization rules for user {UserId} and family {FamilyId}", userId, familyId);
            throw;
        }
    }

    public async Task<CategorizationRuleDto?> GetRuleAsync(int userId, int ruleId)
    {
        try
        {
            var rule = await _context.CategorizationRules
                .Include(r => r.AssignCategoryNav)
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId);

            return rule != null ? MapToDto(rule) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting rule {RuleId} for user {UserId}", ruleId, userId);
            throw;
        }
    }

    public async Task<CategorizationRuleDto> CreateRuleAsync(int userId, CreateCategorizationRuleRequest request)
    {
        try
        {
            // Validate that at least one match criteria is provided
            if (string.IsNullOrWhiteSpace(request.PlaidMerchantEntityId) &&
                string.IsNullOrWhiteSpace(request.MerchantContains) &&
                string.IsNullOrWhiteSpace(request.MerchantExact) &&
                string.IsNullOrWhiteSpace(request.DescriptionContains) &&
                !request.MinAmount.HasValue && !request.MaxAmount.HasValue)
            {
                throw new InvalidOperationException("At least one match criteria must be provided.");
            }

            // Validate that at least one action is provided
            if (!request.AssignCategoryId.HasValue)
            {
                throw new InvalidOperationException("A category to assign must be provided.");
            }

            // Get user for family ID if sharing with family
            var user = await _userRepository.GetByIdAsync(userId);
            int? familyId = request.ShareWithFamily && user?.FamilyId != null ? user.FamilyId : null;

            // Validate the target category against the unified Categories table the
            // FK actually points at. Custom categories created via the live app
            // (POST /categories) have Id >= 1000 and NO legacy UserCategories row,
            // so the old UserCategories check rejected every such category.
            await ValidateAssignCategoryAsync(request.AssignCategoryId.Value, userId, user?.FamilyId);

            // Get max priority for new rule placement
            var maxPriority = await _context.CategorizationRules
                .Where(r => r.UserId == userId)
                .MaxAsync(r => (int?)r.Priority) ?? -1;

            var rule = new CategorizationRule
            {
                UserId = userId,
                FamilyId = familyId,
                Name = request.Name.Trim(),
                Source = request.Source,
                IsActive = request.IsActive,
                Priority = request.Priority > 0 ? request.Priority : maxPriority + 1,
                PlaidMerchantEntityId = request.PlaidMerchantEntityId?.Trim(),
                MerchantContains = request.MerchantContains?.Trim(),
                MerchantExact = request.MerchantExact?.Trim(),
                DescriptionContains = request.DescriptionContains?.Trim(),
                MinAmount = request.MinAmount,
                MaxAmount = request.MaxAmount,
                AssignCategoryId = request.AssignCategoryId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.CategorizationRules.Add(rule);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Created categorization rule {RuleId} '{RuleName}' for user {UserId} (family: {FamilyId}, source: {Source})", 
                rule.Id, rule.Name, userId, familyId, request.Source);

            // Reload with category navigation
            await _context.Entry(rule).Reference(r => r.AssignCategoryNav).LoadAsync();

            return MapToDto(rule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating categorization rule for user {UserId}", userId);
            throw;
        }
    }

    public async Task<CategorizationRuleDto> CreateRuleFromTransactionAsync(int userId, CreateRuleFromTransactionRequest request)
    {
        try
        {
            // Get the transaction
            var transaction = await _context.Transactions
                .Include(t => t.Account)
                .FirstOrDefaultAsync(t => t.Id == request.TransactionId && t.Account.UserId == userId);

            if (transaction == null)
            {
                throw new InvalidOperationException("Transaction not found.");
            }

            // Validate that a category is provided
            if (!request.AssignCategoryId.HasValue)
            {
                throw new InvalidOperationException("A category to assign must be provided.");
            }

            // Create the rule based on match type
            var createRequest = new CreateCategorizationRuleRequest
            {
                Name = request.RuleName.Trim(),
                IsActive = true,
                Source = request.Source,
                ShareWithFamily = request.ShareWithFamily,
                AssignCategoryId = request.AssignCategoryId
            };

            // Set match criteria based on match type
            if (request.MatchType.Equals("plaid", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(transaction.PlaidMerchantEntityId))
            {
                // Use Plaid entity ID for most reliable matching
                createRequest.PlaidMerchantEntityId = transaction.PlaidMerchantEntityId;
            }
            else if (request.MatchType.Equals("exact", StringComparison.OrdinalIgnoreCase))
            {
                createRequest.MerchantExact = transaction.MerchantName;
            }
            else
            {
                // Default to contains match
                createRequest.MerchantContains = transaction.MerchantName;
            }

            // Add amount range if requested
            if (request.IncludeAmountRange)
            {
                var amount = Math.Abs(transaction.Amount);
                var tolerance = amount * request.AmountTolerance;
                createRequest.MinAmount = amount - tolerance;
                createRequest.MaxAmount = amount + tolerance;
            }

            return await CreateRuleAsync(userId, createRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating rule from transaction {TransactionId} for user {UserId}", 
                request.TransactionId, userId);
            throw;
        }
    }
    
    public async Task<CategorizationRuleDto> CreateRuleFromCategorizationAsync(int userId, CreateRuleFromCategorizationRequest request)
    {
        try
        {
            // Get the transaction with account info
            var transaction = await _context.Transactions
                .Include(t => t.Account)
                .FirstOrDefaultAsync(t => t.Id == request.TransactionId && t.Account.UserId == userId);

            if (transaction == null)
            {
                throw new InvalidOperationException("Transaction not found.");
            }

            // Validate that a category is provided
            if (!request.NewCategoryId.HasValue)
            {
                throw new InvalidOperationException("A category to assign must be provided.");
            }

            // Build a rule name from merchant
            var merchantName = transaction.MerchantName ?? transaction.Description ?? "Unknown";
            string categoryName;
            if (request.NewCategoryId >= 1000)
            {
                // Custom category - look up in the unified Categories table by its real Id
                categoryName = (await _categoryRepository.GetByIdAsync(request.NewCategoryId.Value))?.Name ?? "Custom";
            }
            else
            {
                // System category
                categoryName = ((TransactionCategory)request.NewCategoryId).ToString();
            }
            var ruleName = $"Always: {merchantName} → {categoryName}";
            
            // Truncate if too long
            ruleName = SafeText.Ellipsize(ruleName, 100);

            // Create the rule - prefer Plaid entity ID for reliable matching
            var createRequest = new CreateCategorizationRuleRequest
            {
                Name = ruleName,
                IsActive = true,
                Source = RuleSource.MerchantPreference,
                ShareWithFamily = request.ShareWithFamily,
                AssignCategoryId = request.NewCategoryId
            };

            // Use Plaid entity ID if available and requested, otherwise use exact merchant match
            if (request.UsePlaidEntityId && !string.IsNullOrEmpty(transaction.PlaidMerchantEntityId))
            {
                createRequest.PlaidMerchantEntityId = transaction.PlaidMerchantEntityId;
            }
            else if (!string.IsNullOrEmpty(transaction.MerchantName))
            {
                createRequest.MerchantExact = transaction.MerchantName;
            }
            else
            {
                throw new InvalidOperationException("Transaction has no merchant name or Plaid entity ID to create a rule from.");
            }

            _logger.LogInformation("Creating merchant preference rule from transaction {TransactionId} for merchant '{Merchant}'",
                request.TransactionId, merchantName);

            return await CreateRuleAsync(userId, createRequest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating rule from categorization for transaction {TransactionId}, user {UserId}", 
                request.TransactionId, userId);
            throw;
        }
    }

    public async Task<CategorizationRuleDto> UpdateRuleAsync(int userId, int ruleId, UpdateCategorizationRuleRequest request)
    {
        try
        {
            var rule = await _context.CategorizationRules
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId);

            if (rule == null)
            {
                throw new InvalidOperationException("Rule not found.");
            }

            // Update fields if provided
            if (!string.IsNullOrWhiteSpace(request.Name))
                rule.Name = request.Name.Trim();

            if (request.IsActive.HasValue)
                rule.IsActive = request.IsActive.Value;

            if (request.Priority.HasValue)
                rule.Priority = request.Priority.Value;

            // Handle match criteria updates
            if (!string.IsNullOrWhiteSpace(request.MerchantContains))
                rule.MerchantContains = request.MerchantContains.Trim();
            else if (request.ClearMerchantContains)
                rule.MerchantContains = null;

            if (!string.IsNullOrWhiteSpace(request.MerchantExact))
                rule.MerchantExact = request.MerchantExact.Trim();
            else if (request.ClearMerchantExact)
                rule.MerchantExact = null;

            if (!string.IsNullOrWhiteSpace(request.DescriptionContains))
                rule.DescriptionContains = request.DescriptionContains.Trim();
            else if (request.ClearDescriptionContains)
                rule.DescriptionContains = null;

            if (request.MinAmount.HasValue)
                rule.MinAmount = request.MinAmount.Value;
            if (request.MaxAmount.HasValue)
                rule.MaxAmount = request.MaxAmount.Value;
            if (request.ClearAmountRange)
            {
                rule.MinAmount = null;
                rule.MaxAmount = null;
            }

            // Handle category assignment updates
            if (request.AssignCategoryId.HasValue)
            {
                // Validate against the unified Categories table (the FK target),
                // supporting custom categories (Id >= 1000) created in the live app.
                var user = await _userRepository.GetByIdAsync(userId);
                await ValidateAssignCategoryAsync(request.AssignCategoryId.Value, userId, user?.FamilyId);
                rule.AssignCategoryId = request.AssignCategoryId.Value;
            }
            else if (request.ClearAssignCategoryId)
            {
                rule.AssignCategoryId = null;
            }

            rule.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Updated categorization rule {RuleId} for user {UserId}", ruleId, userId);

            await _context.Entry(rule).Reference(r => r.AssignCategoryNav).LoadAsync();
            return MapToDto(rule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating rule {RuleId} for user {UserId}", ruleId, userId);
            throw;
        }
    }

    public async Task<bool> DeleteRuleAsync(int userId, int ruleId)
    {
        try
        {
            var rule = await _context.CategorizationRules
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId);

            if (rule == null)
            {
                return false;
            }

            _context.CategorizationRules.Remove(rule);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Deleted categorization rule {RuleId} for user {UserId}", ruleId, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting rule {RuleId} for user {UserId}", ruleId, userId);
            throw;
        }
    }

    public async Task<CategorizationRuleDto> ToggleRuleAsync(int userId, int ruleId)
    {
        try
        {
            var rule = await _context.CategorizationRules
                .Include(r => r.AssignCategoryNav)
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.UserId == userId);

            if (rule == null)
            {
                throw new InvalidOperationException("Rule not found.");
            }

            rule.IsActive = !rule.IsActive;
            rule.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Toggled rule {RuleId} to {IsActive} for user {UserId}", 
                ruleId, rule.IsActive, userId);

            return MapToDto(rule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error toggling rule {RuleId} for user {UserId}", ruleId, userId);
            throw;
        }
    }

    public async Task<bool> ReorderRulesAsync(int userId, List<int> ruleIds)
    {
        try
        {
            var rules = await _context.CategorizationRules
                .Where(r => r.UserId == userId && ruleIds.Contains(r.Id))
                .ToListAsync();

            if (rules.Count != ruleIds.Count)
            {
                throw new InvalidOperationException("Some rule IDs are invalid.");
            }

            for (int i = 0; i < ruleIds.Count; i++)
            {
                var rule = rules.First(r => r.Id == ruleIds[i]);
                rule.Priority = i;
                rule.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Reordered {Count} rules for user {UserId}", ruleIds.Count, userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reordering rules for user {UserId}", userId);
            throw;
        }
    }

    public async Task<CategorizationRule?> FindMatchingRuleAsync(int userId, Transaction transaction)
    {
        try
        {
            var rules = await LoadApplicableRulesAsync(userId);
            return rules.FirstOrDefault(r => r.Matches(transaction));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding matching rule for transaction {TransactionId}", transaction.Id);
            throw;
        }
    }

    public async Task<bool> ApplyRulesToTransactionAsync(int userId, Transaction transaction)
    {
        try
        {
            var rule = await FindMatchingRuleAsync(userId, transaction);

            // FindMatchingRuleAsync only returns rules that assign a category, but
            // guard defensively: never clear NeedsReview without a real category.
            if (rule?.AssignCategoryId == null)
            {
                return false;
            }

            // Apply the rule - use unified CategoryId
            transaction.CategoryId = rule.AssignCategoryId;
            transaction.IsUserCategorized = true;
            transaction.NeedsReview = false;

            // Update rule statistics
            rule.TimesApplied++;
            rule.LastAppliedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogDebug("Applied rule {RuleId} to transaction {TransactionId}", rule.Id, transaction.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying rules to transaction {TransactionId}", transaction.Id);
            throw;
        }
    }

    public async Task<int> ApplyRulesToTransactionsAsync(int userId, IEnumerable<Transaction> transactions)
    {
        try
        {
            // Family-aware + excludes rules orphaned to a null category (finding: bulk
            // apply previously dropped family-shared rules and could force Uncategorized).
            var rules = await LoadApplicableRulesAsync(userId);

            if (rules.Count == 0)
            {
                return 0;
            }

            int appliedCount = 0;

            foreach (var transaction in transactions)
            {
                var matchingRule = rules.FirstOrDefault(r => r.Matches(transaction));
                if (matchingRule != null && matchingRule.AssignCategoryId.HasValue)
                {
                    // Apply the rule - use unified CategoryId
                    transaction.CategoryId = matchingRule.AssignCategoryId;
                    transaction.IsUserCategorized = true;
                    transaction.NeedsReview = false;

                    // Update rule statistics
                    matchingRule.TimesApplied++;
                    matchingRule.LastAppliedAt = DateTime.UtcNow;

                    appliedCount++;
                }
            }

            if (appliedCount > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Applied rules to {Count} transactions for user {UserId}", appliedCount, userId);
            }

            return appliedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying rules to transactions for user {UserId}", userId);
            throw;
        }
    }

    public async Task<RuleStatisticsDto> GetRuleStatisticsAsync(int userId)
    {
        try
        {
            var rules = await _context.CategorizationRules
                .Where(r => r.UserId == userId)
                .ToListAsync();

            return new RuleStatisticsDto
            {
                TotalRules = rules.Count,
                ActiveRules = rules.Count(r => r.IsActive),
                TotalTimesApplied = rules.Sum(r => r.TimesApplied),
                LastRuleApplied = rules.Max(r => r.LastAppliedAt)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting rule statistics for user {UserId}", userId);
            throw;
        }
    }

    public async Task<(int appliedCount, List<int> unmatchedTransactionIds)> ApplyRulesToTransactionIdsAsync(int userId, List<int> transactionIds)
    {
        try
        {
            if (transactionIds.Count == 0)
            {
                return (0, new List<int>());
            }

            // Family-aware + excludes rules orphaned to a null category.
            var rules = await LoadApplicableRulesAsync(userId);

            if (rules.Count == 0)
            {
                // No rules, all transactions are unmatched
                return (0, transactionIds);
            }

            // Load transactions
            var transactions = await _context.Transactions
                .Include(t => t.Account)
                .Where(t => transactionIds.Contains(t.Id) && t.Account.UserId == userId)
                .ToListAsync();

            int appliedCount = 0;
            var matchedIds = new List<int>();

            foreach (var transaction in transactions)
            {
                var matchingRule = rules.FirstOrDefault(r => r.Matches(transaction));
                if (matchingRule != null && matchingRule.AssignCategoryId.HasValue)
                {
                    // Apply the rule - use unified CategoryId
                    transaction.CategoryId = matchingRule.AssignCategoryId;
                    transaction.IsUserCategorized = true;
                    transaction.NeedsReview = false;

                    // Update rule statistics
                    matchingRule.TimesApplied++;
                    matchingRule.LastAppliedAt = DateTime.UtcNow;

                    appliedCount++;
                    matchedIds.Add(transaction.Id);
                }
            }

            if (appliedCount > 0)
            {
                await _context.SaveChangesAsync();
                _logger.LogInformation("Applied rules to {Count} transactions for user {UserId}", appliedCount, userId);
            }

            var unmatchedIds = transactionIds.Except(matchedIds).ToList();
            return (appliedCount, unmatchedIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying rules to transaction IDs for user {UserId}", userId);
            throw;
        }
    }

    public async Task<int> DeactivateRulesForCategoryAsync(int categoryId)
    {
        try
        {
            // Only custom categories (1000+) can be deleted, so only those can orphan
            // rules; but match on the id directly to stay correct regardless.
            var affected = await _context.CategorizationRules
                .Where(r => r.AssignCategoryId == categoryId && r.IsActive)
                .ToListAsync();

            if (affected.Count == 0)
            {
                return 0;
            }

            foreach (var rule in affected)
            {
                rule.IsActive = false;
                rule.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            _logger.LogInformation("Deactivated {Count} rule(s) referencing deleted category {CategoryId}",
                affected.Count, categoryId);

            return affected.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deactivating rules for category {CategoryId}", categoryId);
            throw;
        }
    }

    private static CategorizationRuleDto MapToDto(CategorizationRule rule)
    {
        return new CategorizationRuleDto
        {
            Id = rule.Id,
            Name = rule.Name,
            IsActive = rule.IsActive,
            Priority = rule.Priority,
            Source = rule.Source,
            FamilyId = rule.FamilyId,
            OwnerUserId = rule.UserId,
            PlaidMerchantEntityId = rule.PlaidMerchantEntityId,
            MerchantContains = rule.MerchantContains,
            MerchantExact = rule.MerchantExact,
            DescriptionContains = rule.DescriptionContains,
            MinAmount = rule.MinAmount,
            MaxAmount = rule.MaxAmount,
            // Use unified AssignCategoryId
            AssignCategoryId = rule.AssignCategoryId,
            AssignCategoryName = rule.AssignCategoryNav?.Name,
            TimesApplied = rule.TimesApplied,
            LastAppliedAt = rule.LastAppliedAt,
            CreatedAt = rule.CreatedAt,
            UpdatedAt = rule.UpdatedAt
        };
    }
}
