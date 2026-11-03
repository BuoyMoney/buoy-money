namespace BuoyAI.Core.Entities;

/// <summary>
/// Unified category entity for both system-defined and user-created categories.
/// System categories have IDs 1-999 (matching the old TransactionCategory enum values).
/// User-created categories have IDs 1000+.
/// </summary>
public class Category
{
    public int Id { get; set; }
    
    /// <summary>
    /// Display name for the category (e.g., "Groceries", "Pet Expenses")
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Hex color code for the category badge (e.g., "#FF5733")
    /// </summary>
    public string Color { get; set; } = "#6B7280";
    
    /// <summary>
    /// Icon identifier for the category (e.g., "groceries", "pet", "car")
    /// </summary>
    public string? Icon { get; set; }
    
    /// <summary>
    /// Whether this is an income category (true) or expense category (false)
    /// </summary>
    public bool IsIncome { get; set; } = false;
    
    /// <summary>
    /// True for built-in system categories that cannot be deleted or renamed.
    /// System categories are visible to all users.
    /// </summary>
    public bool IsSystem { get; set; } = false;
    
    /// <summary>
    /// Sort order for display purposes within category groups
    /// </summary>
    public int SortOrder { get; set; } = 0;
    
    /// <summary>
    /// Optional description for the category
    /// </summary>
    public string? Description { get; set; }
    
    // ==================== Scoping (mutually exclusive) ====================
    
    /// <summary>
    /// User ID for personal categories (only visible to this user).
    /// NULL for system categories and family-shared categories.
    /// </summary>
    public int? UserId { get; set; }
    
    /// <summary>
    /// Family ID for family-shared categories (visible to all family members).
    /// NULL for system categories and personal categories.
    /// </summary>
    public int? FamilyId { get; set; }
    
    /// <summary>
    /// User who created this category (for family categories, tracks the creator).
    /// NULL for system categories.
    /// </summary>
    public int? CreatedByUserId { get; set; }
    
    // ==================== Timestamps ====================
    // Note: Do NOT use DateTime.UtcNow as default here - it causes EF Core
    // PendingModelChangesWarning because the model changes each time it's built.
    // The database handles defaults via CURRENT_TIMESTAMP.
    
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
    // ==================== Navigation Properties ====================
    
    /// <summary>
    /// Owner of this personal category (null for system/family categories)
    /// </summary>
    public User? User { get; set; }
    
    /// <summary>
    /// Family that shares this category (null for system/personal categories)
    /// </summary>
    public Family? Family { get; set; }
    
    /// <summary>
    /// User who created this category
    /// </summary>
    public User? CreatedByUser { get; set; }
    
    /// <summary>
    /// Transactions using this category
    /// </summary>
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    
    /// <summary>
    /// Budget categories using this category
    /// </summary>
    public ICollection<BudgetCategory> BudgetCategories { get; set; } = new List<BudgetCategory>();
}

/// <summary>
/// Static class containing the IDs of system categories for easy reference.
/// These match the old TransactionCategory enum values for migration compatibility.
/// </summary>
public static class SystemCategoryIds
{
    // Income (1-4)
    public const int Salary = 1;
    public const int Bonus = 2;
    public const int Investment = 3;
    public const int OtherIncome = 4;
    
    // Fixed Expenses (10-14)
    public const int Rent = 10;
    public const int Utilities = 11;
    public const int Insurance = 12;
    public const int Subscriptions = 13;
    public const int LoanPayments = 14;
    
    // Variable Expenses (20-29)
    public const int Groceries = 20;
    public const int DiningOut = 21;
    public const int Transportation = 22;
    public const int Entertainment = 23;
    public const int Shopping = 24;
    public const int Healthcare = 25;
    public const int Travel = 26;
    public const int PersonalCare = 27;
    public const int Education = 28;
    public const int Gifts = 29;
    
    // Transfers & Payments (30-31)
    public const int CreditPayment = 30;
    public const int Transfer = 31;
    
    // Special (98-99)
    public const int Other = 98;
    public const int Uncategorized = 99;
    
    /// <summary>
    /// All income category IDs
    /// </summary>
    public static readonly int[] IncomeIds = { Salary, Bonus, Investment, OtherIncome };
    
    /// <summary>
    /// All system category IDs
    /// </summary>
    public static readonly int[] AllIds = 
    {
        Salary, Bonus, Investment, OtherIncome,
        Rent, Utilities, Insurance, Subscriptions, LoanPayments,
        Groceries, DiningOut, Transportation, Entertainment, Shopping,
        Healthcare, Travel, PersonalCare, Education, Gifts,
        CreditPayment, Transfer,
        Other, Uncategorized
    };
    
    /// <summary>
    /// Check if a category ID is a system category
    /// </summary>
    public static bool IsSystemCategory(int categoryId) => categoryId < 1000;
    
    /// <summary>
    /// Check if a category ID represents an income category
    /// </summary>
    public static bool IsIncomeCategory(int categoryId) => IncomeIds.Contains(categoryId);
    
    /// <summary>
    /// Check if transactions with this category should be excluded from budgets
    /// </summary>
    public static bool IsNonBudgetable(int categoryId) => 
        categoryId == CreditPayment || categoryId == Transfer;
}
