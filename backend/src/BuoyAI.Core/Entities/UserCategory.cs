namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a custom user-defined transaction category.
/// Users can create their own categories to supplement the built-in TransactionCategory enum.
/// </summary>
public class UserCategory
{
    public int Id { get; set; }
    
    /// <summary>
    /// The user who owns this custom category
    /// </summary>
    public int UserId { get; set; }
    
    /// <summary>
    /// Display name for the category (e.g., "Pet Expenses", "Side Hustle Income")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Hex color code for the category badge (e.g., "#FF5733")
    /// </summary>
    public string Color { get; set; } = "#6B7280";
    
    /// <summary>
    /// Optional icon identifier for the category (e.g., "pet", "car", "home")
    /// </summary>
    public string? Icon { get; set; }
    
    /// <summary>
    /// Whether this is an income category (true) or expense category (false)
    /// </summary>
    public bool IsIncome { get; set; } = false;
    
    /// <summary>
    /// Whether this category is personal/private (only visible to this user).
    /// Personal categories are only shown in the user's personal budget and are not
    /// visible to other family members or in family budget editing.
    /// Categories created via family budget edit or transactions page are shared (IsPersonal = false).
    /// </summary>
    public bool IsPersonal { get; set; } = false;
    
    /// <summary>
    /// Optional description for the category
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Sort order for display purposes
    /// </summary>
    public int SortOrder { get; set; } = 0;
    
    /// <summary>
    /// Whether the category is currently active/visible
    /// </summary>
    public bool IsActive { get; set; } = true;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation properties
    public User User { get; set; } = null!;
}
