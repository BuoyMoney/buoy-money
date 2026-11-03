namespace BuoyAI.Core.DTOs.Category;

/// <summary>
/// DTO for category information returned from the API.
/// </summary>
public class CategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6B7280";
    public string? Icon { get; set; }
    public bool IsIncome { get; set; }
    public bool IsSystem { get; set; }
    public int SortOrder { get; set; }
    public string? Description { get; set; }
    
    /// <summary>
    /// Scope of the category: "system", "personal", or "family"
    /// </summary>
    public string Scope { get; set; } = "system";
    
    /// <summary>
    /// For family categories, the ID of the family
    /// </summary>
    public int? FamilyId { get; set; }
    
    /// <summary>
    /// Whether the current user can edit/delete this category.
    /// System categories cannot be modified.
    /// </summary>
    public bool CanEdit { get; set; }
}

/// <summary>
/// DTO for creating a new category.
/// </summary>
public class CreateCategoryDto
{
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6B7280";
    public string? Icon { get; set; }
    public bool IsIncome { get; set; }
    public int SortOrder { get; set; }
    public string? Description { get; set; }
    
    /// <summary>
    /// If true, creates a family-shared category (requires user to be in a family with appropriate permissions).
    /// If false (default), creates a personal category.
    /// </summary>
    public bool IsFamily { get; set; } = false;
}

/// <summary>
/// DTO for updating an existing category.
/// </summary>
public class UpdateCategoryDto
{
    public string? Name { get; set; }
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public bool? IsIncome { get; set; }
    public int? SortOrder { get; set; }
    public string? Description { get; set; }
}

/// <summary>
/// Response containing categorized lists of categories for the frontend.
/// </summary>
public class CategoriesResponseDto
{
    /// <summary>
    /// Built-in system categories (available to all users).
    /// </summary>
    public IReadOnlyList<CategoryDto> SystemCategories { get; set; } = new List<CategoryDto>();
    
    /// <summary>
    /// Personal categories created by the current user.
    /// </summary>
    public IReadOnlyList<CategoryDto> PersonalCategories { get; set; } = new List<CategoryDto>();
    
    /// <summary>
    /// Family-shared categories (only if user is in a family).
    /// </summary>
    public IReadOnlyList<CategoryDto> FamilyCategories { get; set; } = new List<CategoryDto>();
    
    /// <summary>
    /// All categories combined and sorted for dropdown display.
    /// </summary>
    public IReadOnlyList<CategoryDto> AllCategories { get; set; } = new List<CategoryDto>();
}
