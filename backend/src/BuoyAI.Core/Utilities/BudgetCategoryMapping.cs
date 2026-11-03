using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Utilities;

/// <summary>
/// Extension methods for mapping BudgetCategory entities to DTOs.
/// Consolidates duplicate MapToCategoryDto methods from BudgetService, 
/// BudgetTransferService, and BudgetCategoryGroupService.
/// </summary>
public static class BudgetCategoryMapping
{
    /// <summary>
    /// Maps a BudgetCategory entity to a BudgetCategoryDto.
    /// 
    /// IMPORTANT: SpentAmount is derived data, not stored in the entity.
    /// Use the spentAmount parameter to provide the calculated spending,
    /// or it will default to 0.
    /// 
    /// For accurate spending data, use IBudgetSpendingService.GetCategorySpendingAsync()
    /// to calculate spending from transactions before calling this method.
    /// </summary>
    /// <param name="category">The budget category entity to map.</param>
    /// <param name="spentAmount">The calculated spent amount for this category (from transactions).</param>
    /// <returns>A BudgetCategoryDto with all relevant properties populated.</returns>
    public static BudgetCategoryDto ToDto(this BudgetCategory category, decimal spentAmount = 0)
    {
        // Derive category name from navigation property or use CategoryId to get enum name
        string categoryName = category.CategoryNav?.Name
            ?? GetCategoryNameFromId(category.CategoryId);
        
        return new BudgetCategoryDto
        {
            Id = category.Id,
            CategoryId = category.CategoryId ?? 0,
            CategoryName = categoryName,
            AllocatedAmount = category.AllocatedAmount,
            SpentAmount = spentAmount,
            GroupId = category.GroupId,
            GroupName = category.Group?.Name,
            SortOrder = category.SortOrder,
            AllowRollover = category.AllowRollover,
            RolloverBalance = category.RolloverBalance,
            MaxRolloverMultiplier = category.MaxRolloverMultiplier,
            RolloverOverflowSavingsGoalId = category.RolloverOverflowSavingsGoalId,
            RolloverOverflowSavingsGoalName = category.RolloverOverflowSavingsGoal?.Name,
            IsCountUpEnabled = category.IsCountUpEnabled,
            CountUpRolloverMode = category.CountUpRolloverMode,
            SavingsGoalId = category.SavingsGoalId,
            SavingsGoalName = category.SavingsGoal?.Name,
            IsIncome = category.IsIncome,
            DefaultAllocatedAmount = category.DefaultAllocatedAmount,
            IsAllowance = category.IsAllowance,
            LinkedBudgetId = category.LinkedBudgetId,
            AssignedUserId = category.AssignedUserId,
            AssignedUserName = category.AssignedUser != null 
                ? $"{category.AssignedUser.FirstName} {category.AssignedUser.LastName}".Trim()
                : null
        };
    }
    
    /// <summary>
    /// Gets category name from unified CategoryId.
    /// System categories (1-99) use TransactionCategory enum names.
    /// Custom categories (1000+) return a placeholder - use CategoryNav for actual name.
    /// </summary>
    private static string GetCategoryNameFromId(int? categoryId)
    {
        if (!categoryId.HasValue)
            return TransactionCategory.Uncategorized.GetDisplayName();
        
        // System categories: use enum
        if (categoryId.Value < 1000 && Enum.IsDefined(typeof(TransactionCategory), categoryId.Value))
        {
            return ((TransactionCategory)categoryId.Value).GetDisplayName();
        }
        
        // Custom category - name should come from CategoryNav navigation property
        return $"Custom Category {categoryId.Value}";
    }
}
