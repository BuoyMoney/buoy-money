using BuoyAI.Core.DTOs.Budget;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service for handling budget fund transfers, deficit resolution, and rollover management.
/// Extracted from BudgetService to follow Single Responsibility Principle.
/// </summary>
public interface IBudgetTransferService
{
    /// <summary>
    /// Transfer funds between two categories in the same budget.
    /// </summary>
    Task<TransferFundsResponse> TransferFundsAsync(int targetCategoryId, int userId, TransferFundsRequest request);

    /// <summary>
    /// Transfer funds from a budget category to a savings goal.
    /// </summary>
    Task<TransferToSavingsResponse> TransferToSavingsAsync(int categoryId, int userId, TransferToSavingsRequest request);

    /// <summary>
    /// Get transfer history for a budget.
    /// </summary>
    Task<IEnumerable<BudgetTransferDto>> GetTransferHistoryAsync(int budgetId, int userId);

    /// <summary>
    /// Resolve a category deficit using various strategies (pull from savings, transfer from other category, carry as negative rollover).
    /// </summary>
    Task<ResolveDeficitResponse> ResolveDeficitAsync(int budgetId, int userId, ResolveDeficitRequest request);

    /// <summary>
    /// Reset the rollover balance for a single category.
    /// </summary>
    Task<ResetRolloversResponse> ResetCategoryRolloverAsync(int categoryId, int userId);

    /// <summary>
    /// Reset rollover balances for all categories in a budget.
    /// </summary>
    Task<ResetRolloversResponse> ResetAllRolloversAsync(int budgetId, int userId);
}
