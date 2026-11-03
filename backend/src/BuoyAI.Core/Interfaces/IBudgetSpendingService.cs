using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for calculating actual spending from transactions.
/// 
/// SpentAmount is derived data, not stored data. This service calculates
/// the actual spending for budget categories by summing transactions
/// within the budget's current period.
/// 
/// Use this service when you need accurate spending data for business logic
/// decisions (like transfer validation, deficit detection, etc).
/// 
/// For read operations (displaying budgets to users), use BudgetDto which
/// already has SpentAmount calculated during the mapping process.
/// </summary>
public interface IBudgetSpendingService
{
    /// <summary>
    /// Calculate actual spending for all categories in a budget.
    /// Returns a dictionary mapping category ID to spent amount.
    /// </summary>
    /// <param name="budgetId">The budget to calculate spending for</param>
    /// <returns>Dictionary of categoryId -> spentAmount</returns>
    Task<Dictionary<int, decimal>> GetCategorySpendingAsync(int budgetId);
    
    /// <summary>
    /// Calculate actual spending for a single category.
    /// More efficient than GetCategorySpendingAsync when you only need one category.
    /// </summary>
    /// <param name="category">The budget category to calculate spending for</param>
    /// <returns>The total spent amount for this category in the current period</returns>
    Task<decimal> GetCategorySpentAmountAsync(BudgetCategory category);
    
    /// <summary>
    /// Calculate actual spending for a budget and return categories with spending populated.
    /// This is useful for services that need to work with category entities but need accurate spending.
    /// </summary>
    /// <param name="budgetId">The budget to get categories with spending for</param>
    /// <returns>List of tuples containing (category, spentAmount)</returns>
    Task<List<(BudgetCategory Category, decimal SpentAmount)>> GetCategoriesWithSpendingAsync(int budgetId);
}
