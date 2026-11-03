using BuoyAI.Core.DTOs.Category;

namespace BuoyAI.Core.Interfaces.Services;

/// <summary>
/// Service interface for category operations.
/// </summary>
public interface ICategoryService
{
    /// <summary>
    /// Get all categories accessible to the current user.
    /// Returns system, personal, and family categories (if applicable).
    /// </summary>
    Task<CategoriesResponseDto> GetAllCategoriesAsync(int userId);
    
    /// <summary>
    /// Get a single category by ID.
    /// </summary>
    Task<CategoryDto?> GetCategoryByIdAsync(int categoryId, int userId);
    
    /// <summary>
    /// Create a new personal or family category.
    /// </summary>
    Task<CategoryDto> CreateCategoryAsync(CreateCategoryDto dto, int userId);
    
    /// <summary>
    /// Update an existing category.
    /// </summary>
    Task<CategoryDto?> UpdateCategoryAsync(int categoryId, UpdateCategoryDto dto, int userId);
    
    /// <summary>
    /// Delete a category.
    /// </summary>
    Task<bool> DeleteCategoryAsync(int categoryId, int userId);
    
    /// <summary>
    /// Get the category name for a given ID (for display purposes).
    /// Returns "Unknown" if not found.
    /// </summary>
    Task<string> GetCategoryNameAsync(int categoryId);
    
    /// <summary>
    /// Validate that a category ID is accessible to the user.
    /// </summary>
    Task<bool> ValidateCategoryAccessAsync(int categoryId, int userId);
    
    /// <summary>
    /// Bulk lookup category names by IDs.
    /// Returns a dictionary of ID to name.
    /// </summary>
    Task<Dictionary<int, string>> GetCategoryNamesAsync(IEnumerable<int> categoryIds);
}
