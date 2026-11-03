using System.ComponentModel.DataAnnotations;

namespace BuoyAI.Core.DTOs.UserCategory;

/// <summary>
/// DTO for returning user category data
/// </summary>
public class UserCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#6B7280";
    public string? Icon { get; set; }
    public bool IsIncome { get; set; }
    public bool IsPersonal { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public int TransactionCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// DTO for creating a new user category
/// </summary>
public class CreateUserCategoryRequest
{
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [StringLength(20)]
    [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Color must be a valid hex color code (e.g., #FF5733)")]
    public string Color { get; set; } = "#6B7280";

    [StringLength(50)]
    public string? Icon { get; set; }

    public bool IsIncome { get; set; } = false;
    
    /// <summary>
    /// Whether this category is personal/private (only visible to this user).
    /// Set to true when creating categories from personal budget wizard.
    /// </summary>
    public bool IsPersonal { get; set; } = false;

    [StringLength(500)]
    public string? Description { get; set; }

    public int SortOrder { get; set; } = 0;
}

/// <summary>
/// DTO for updating an existing user category
/// </summary>
public class UpdateUserCategoryRequest
{
    [StringLength(100, MinimumLength = 1)]
    public string? Name { get; set; }

    [StringLength(20)]
    [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Color must be a valid hex color code (e.g., #FF5733)")]
    public string? Color { get; set; }

    [StringLength(50)]
    public string? Icon { get; set; }

    public bool? IsIncome { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    public int? SortOrder { get; set; }

    public bool? IsActive { get; set; }
}

/// <summary>
/// Request to assign a custom category to a transaction
/// </summary>
public class AssignUserCategoryRequest
{
    /// <summary>
    /// The custom category ID to assign. Set to null to remove custom category.
    /// </summary>
    public int? UserCategoryId { get; set; }
}

/// <summary>
/// Response containing allowance information for a user category.
/// Used to determine if the current user is the allowance owner and can set personal sub-categories.
/// </summary>
public class AllowanceInfoResponse
{
    /// <summary>
    /// Whether this category is used as an allowance in a family budget
    /// </summary>
    public bool IsAllowance { get; set; }
    
    /// <summary>
    /// Whether the current user is the allowance owner (can set personal sub-categories)
    /// </summary>
    public bool IsCurrentUserOwner { get; set; }
    
    /// <summary>
    /// The name of the category (for display in prompts)
    /// </summary>
    public string? CategoryName { get; set; }
    
    /// <summary>
    /// Personal categories available for sub-categorization (only populated if IsCurrentUserOwner is true)
    /// </summary>
    public List<UserCategoryDto> PersonalCategories { get; set; } = [];
}
