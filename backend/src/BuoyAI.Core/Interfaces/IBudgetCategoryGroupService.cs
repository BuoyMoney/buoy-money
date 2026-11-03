using BuoyAI.Core.DTOs;
using BuoyAI.Core.DTOs.Budget;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for managing category groups within budgets.
/// Handles group CRUD, inline category updates, and grouped budget overviews.
/// </summary>
public interface IBudgetCategoryGroupService
{
    // Category Group Operations
    Task<IEnumerable<CategoryGroupDto>> GetCategoryGroupsAsync(int budgetId, int userId);
    Task<CategoryGroupDto> CreateCategoryGroupAsync(int budgetId, int userId, CreateCategoryGroupRequest request);
    Task<CategoryGroupDto> UpdateCategoryGroupAsync(int groupId, int userId, UpdateCategoryGroupRequest request);
    Task DeleteCategoryGroupAsync(int groupId, int userId);
    Task ReorderCategoryGroupsAsync(int budgetId, int userId, ReorderCategoryGroupsRequest request);

    // Inline Category Updates
    Task<BudgetCategoryDto> UpdateCategoryInlineAsync(int categoryId, int userId, InlineUpdateCategoryRequest request);
    Task<BudgetCategoryDto> MoveCategoryToGroupAsync(int categoryId, int userId, MoveCategoryToGroupRequest request);

    // Grouped Budget Overview
    Task<GroupedBudgetOverviewDto> GetGroupedBudgetOverviewAsync(int budgetId, int userId);
}
