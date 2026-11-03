using BuoyAI.Core.DTOs.UserCategory;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Service interface for managing user-defined custom categories
/// </summary>
public interface IUserCategoryService
{
    /// <summary>
    /// Get all categories for a user (both active and inactive)
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="includeInactive">Include inactive categories</param>
    /// <param name="includePersonal">Include personal/private categories (set to false for family budget editing)</param>
    Task<List<UserCategoryDto>> GetUserCategoriesAsync(int userId, bool includeInactive = false, bool includePersonal = true);

    /// <summary>
    /// Get only personal/private categories for a user (for personal budget wizard)
    /// </summary>
    Task<List<UserCategoryDto>> GetPersonalCategoriesAsync(int userId, bool includeInactive = false);

    /// <summary>
    /// Get a specific category by ID
    /// </summary>
    Task<UserCategoryDto?> GetUserCategoryAsync(int userId, int categoryId);

    /// <summary>
    /// Create a new custom category
    /// </summary>
    Task<UserCategoryDto> CreateUserCategoryAsync(int userId, CreateUserCategoryRequest request);

    /// <summary>
    /// Update an existing custom category
    /// </summary>
    Task<UserCategoryDto> UpdateUserCategoryAsync(int userId, int categoryId, UpdateUserCategoryRequest request);

    /// <summary>
    /// Soft delete a category (mark as inactive)
    /// </summary>
    Task<bool> DeleteUserCategoryAsync(int userId, int categoryId);

    /// <summary>
    /// Permanently delete a category and remove from all transactions
    /// </summary>
    Task<bool> PermanentlyDeleteUserCategoryAsync(int userId, int categoryId);

    /// <summary>
    /// Assign a custom category to a transaction
    /// </summary>
    Task<bool> AssignCategoryToTransactionAsync(int userId, int transactionId, int? userCategoryId);

    /// <summary>
    /// Reorder categories
    /// </summary>
    Task<bool> ReorderCategoriesAsync(int userId, List<int> categoryIds);
    
    /// <summary>
    /// Get allowance information for a user category.
    /// Returns whether this category is an allowance and if the current user is the owner.
    /// </summary>
    /// <param name="userId">The current user ID</param>
    /// <param name="userCategoryId">The user category ID to check</param>
    /// <returns>Allowance info including personal categories if user is owner</returns>
    Task<AllowanceInfoResponse> GetAllowanceInfoAsync(int userId, int userCategoryId);
}
