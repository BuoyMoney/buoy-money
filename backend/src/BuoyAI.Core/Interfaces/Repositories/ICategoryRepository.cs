using BuoyAI.Core.Entities;

namespace BuoyAI.Core.Interfaces.Repositories;

/// <summary>
/// Repository interface for unified category operations.
/// </summary>
public interface ICategoryRepository
{
    /// <summary>
    /// Get a category by ID.
    /// </summary>
    Task<Category?> GetByIdAsync(int id);
    
    /// <summary>
    /// Get all system categories (IsSystem = true).
    /// </summary>
    Task<IReadOnlyList<Category>> GetSystemCategoriesAsync();
    
    /// <summary>
    /// Get all personal categories for a specific user.
    /// </summary>
    Task<IReadOnlyList<Category>> GetUserCategoriesAsync(int userId);
    
    /// <summary>
    /// Get all family-shared categories for a specific family.
    /// </summary>
    Task<IReadOnlyList<Category>> GetFamilyCategoriesAsync(int familyId);
    
    /// <summary>
    /// Get all categories accessible to a user: system + personal + family (if in a family).
    /// </summary>
    Task<IReadOnlyList<Category>> GetAllForUserAsync(int userId, int? familyId);
    
    /// <summary>
    /// Get categories by multiple IDs.
    /// </summary>
    Task<IReadOnlyList<Category>> GetByIdsAsync(IEnumerable<int> ids);
    
    /// <summary>
    /// Check if a category exists and is accessible to the user.
    /// </summary>
    Task<bool> ExistsAndAccessibleAsync(int categoryId, int userId, int? familyId);
    
    /// <summary>
    /// Check if a category name is unique within the given scope.
    /// For personal categories: unique within user's categories.
    /// For family categories: unique within family's categories.
    /// </summary>
    Task<bool> IsNameUniqueAsync(string name, int? userId, int? familyId, int? excludeCategoryId = null);
    
    /// <summary>
    /// Add a new category.
    /// </summary>
    Task<Category> AddAsync(Category category);
    
    /// <summary>
    /// Update an existing category.
    /// </summary>
    Task UpdateAsync(Category category);
    
    /// <summary>
    /// Delete a category by ID.
    /// </summary>
    Task DeleteAsync(int id);
    
    /// <summary>
    /// Get the next available ID for user-created categories (1000+).
    /// </summary>
    Task<int> GetNextUserCategoryIdAsync();
}
