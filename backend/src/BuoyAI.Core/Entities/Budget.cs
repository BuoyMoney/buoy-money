using System.ComponentModel.DataAnnotations.Schema;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Entities;

/// <summary>
/// Represents a grouping of budget categories (e.g., "Income", "Housing", "Food & Dining")
/// System groups have UserId = null. User-created groups have UserId set.
/// </summary>
public class CategoryGroup
{
  /// <summary>
  /// The ID of the system "Other" category group, used as fallback when moving categories from deleted groups.
  /// </summary>
  public const int OtherGroupId = 9;
  
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string? Icon { get; set; }  // Emoji or icon name (e.g., "💰", "🏠")
  public int SortOrder { get; set; }
  
  // Ownership - null = system-defined group, set = user/family custom group
  public int? UserId { get; set; }
  public int? FamilyId { get; set; }
  
  public bool IsSystem => UserId == null && FamilyId == null;
  
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
  
  // Navigation properties
  public User? User { get; set; }
  public Family? Family { get; set; }
}

public class Budget
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public int? FamilyId { get; set; } // Foreign key to Family
  public string Name { get; set; } = string.Empty;
  public BudgetPeriod Period { get; set; }
  
  // NEW: Flexible period model
  public DateTime CurrentPeriodStart { get; set; }
  public DateTime CurrentPeriodEnd { get; set; }

  /// <summary>
  /// Inclusive upper bound for querying this period's transactions. CurrentPeriodEnd is the last
  /// calendar day at midnight (e.g. May 31 00:00), but transactions entered later that same day
  /// carry a time-of-day component (e.g. May 31 23:15) — the default Add-Transaction UI, manual
  /// create, and the MCP tool all send time-bearing dates. Querying with a plain `Date &lt;= CurrentPeriodEnd`
  /// therefore drops last-day spend from the period entirely (it's also before the next period's
  /// midnight start), understating category spend and over-rolling surplus. This extends the bound
  /// to the very end of the last calendar day so the whole day is covered.
  /// </summary>
  [NotMapped]
  public DateTime CurrentPeriodEndInclusive => CurrentPeriodEnd.Date.AddDays(1).AddTicks(-1);

  public DateTime LastReset { get; set; }
  public bool AutoReset { get; set; } = true;
  
  public decimal TotalIncome { get; set; }
  public decimal TotalExpenses { get; set; }
  public bool IsActive { get; set; } = true;
  
  // Hierarchical budget support
  public int? ParentBudgetId { get; set; }        // For allowance budgets funded by a parent
  public decimal? AllocatedAmount { get; set; }   // Amount allocated from parent budget (for allowances)
  public BudgetType Type { get; set; } = BudgetType.Personal;
  public bool IsPrimary { get; set; } = false;    // User's primary budget (cannot be deleted)
  
  // Child budget rollover settings
  public ChildBudgetRolloverMode RolloverMode { get; set; } = ChildBudgetRolloverMode.RolloverToSelf;
  public int? DefaultSavingsGoalId { get; set; }  // Where surplus goes if RolloverMode = AllocateToSavings
  
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public User User { get; set; } = null!;
  public Family? Family { get; set; }
  public Budget? ParentBudget { get; set; }
  public SavingsGoal? DefaultSavingsGoal { get; set; }
  public ICollection<Budget> ChildBudgets { get; set; } = new List<Budget>();
  public ICollection<BudgetCategory> Categories { get; set; } = new List<BudgetCategory>();
  public ICollection<BudgetShare> Shares { get; set; } = new List<BudgetShare>();
}

public class BudgetCategory
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  
  /// <summary>
  /// Reference to the unified Categories table.
  /// Contains both system categories (IDs 1-99) and user-created categories (IDs 1000+).
  /// </summary>
  public int? CategoryId { get; set; }
  
  // Grouping
  public int? GroupId { get; set; }
  public int SortOrder { get; set; }
  
  // Income vs Expense
  public bool IsIncome { get; set; } = false;       // True if this is an income category (Salary, Bonus, etc.)
  
  // Amounts
  // For expense categories: AllocatedAmount = budgeted spending limit
  // For income categories: AllocatedAmount = expected income amount
  public decimal AllocatedAmount { get; set; }
  // NOTE: SpentAmount is NOT stored in the database. It's derived data calculated
  // from transactions at read time. Use IBudgetSpendingService to get accurate
  // spending data, or use BudgetDto which has SpentAmount calculated during mapping.
  
  // Template for future periods
  public decimal? DefaultAllocatedAmount { get; set; }  // Default amount for new periods when "Apply to future" is used
  
  // Rollover functionality
  public bool AllowRollover { get; set; } = true;
  public decimal RolloverBalance { get; set; } = 0;
  
  /// <summary>
  /// Maximum rollover amount as a multiplier of AllocatedAmount.
  /// E.g., 1.0 = max rollover is 100% of allocated, 2.0 = 200%, 0.5 = 50%.
  /// Null means no limit (unlimited rollover).
  /// </summary>
  public decimal? MaxRolloverMultiplier { get; set; }
  
  /// <summary>
  /// When MaxRolloverMultiplier is set and rollover exceeds the cap,
  /// the surplus is automatically transferred to this savings goal.
  /// Required when MaxRolloverMultiplier is set.
  /// </summary>
  public int? RolloverOverflowSavingsGoalId { get; set; }
  
  // Count-up budgeting fields
  public bool IsCountUpEnabled { get; set; } = false;
  public decimal DailyAllowance { get; set; }
  public DateTime LastAllowanceCalculation { get; set; }
  public decimal EarnedToDate { get; set; }
  
  /// <summary>
  /// Controls what happens to earned-but-unspent balance at period end for Count-Up categories.
  /// Only applies when IsCountUpEnabled = true.
  /// </summary>
  public Enums.CountUpRolloverMode? CountUpRolloverMode { get; set; }
  
  /// <summary>
  /// Category-level savings goal for auto-transfer at period end.
  /// Used when CountUpRolloverMode = TransferToSavings, or when budget.RolloverMode = AllocateToSavings.
  /// Falls back to budget.DefaultSavingsGoalId if not set.
  /// </summary>
  public int? SavingsGoalId { get; set; }
  
  // Allowance/Child budget support - link category to personal budget
  public bool IsAllowance { get; set; } = false;        // Is this category funding a personal allowance?
  public int? LinkedBudgetId { get; set; }              // The allowance budget this category funds
  public int? AssignedUserId { get; set; }              // User who receives this allowance
  
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }

  // Navigation properties
  public Budget Budget { get; set; } = null!;
  public CategoryGroup? Group { get; set; }
  
  /// <summary>
  /// Navigation property for the unified category.
  /// </summary>
  public Category? CategoryNav { get; set; }
  
  public Budget? LinkedBudget { get; set; }
  public User? AssignedUser { get; set; }
  public SavingsGoal? RolloverOverflowSavingsGoal { get; set; }
  public SavingsGoal? SavingsGoal { get; set; }
  
  /// <summary>
  /// Gets the effective CategoryId for this budget category.
  /// Returns the unified CategoryId or 0 (Uncategorized) if not set.
  /// Use this instead of directly accessing CategoryId ?? 0 for consistency.
  /// </summary>
  public int GetEffectiveCategoryId() => CategoryId ?? 0;
}

/// <summary>
/// Defines custom visibility for budgets - which specific users can view a budget
/// </summary>
public class BudgetShare
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  public int SharedWithUserId { get; set; }
  public bool CanEdit { get; set; } = false;           // Can this user edit the budget?
  public bool CanViewTransactions { get; set; } = false; // Can they see individual transactions?
  public DateTime CreatedAt { get; set; }
  public DateTime UpdatedAt { get; set; }
  
  // Navigation properties
  public Budget Budget { get; set; } = null!;
  public User SharedWithUser { get; set; } = null!;
}

/// <summary>
/// Records a transfer of funds between budget categories and/or savings goals
/// </summary>
public class BudgetTransfer
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  public int CreatedByUserId { get; set; }
  
  // Source (one of these will be set)
  public int? SourceCategoryId { get; set; }
  public int? SourceSavingsGoalId { get; set; }
  
  // Destination (one of these will be set)
  public int? DestinationCategoryId { get; set; }
  public int? DestinationSavingsGoalId { get; set; }
  
  public decimal Amount { get; set; }
  public string? Note { get; set; }
  public TransferReason Reason { get; set; } = TransferReason.Manual;
  public DateTime CreatedAt { get; set; }
  
  // Navigation properties
  public Budget Budget { get; set; } = null!;
  public User CreatedByUser { get; set; } = null!;
  public BudgetCategory? SourceCategory { get; set; }
  public BudgetCategory? DestinationCategory { get; set; }
  public SavingsGoal? SourceSavingsGoal { get; set; }
  public SavingsGoal? DestinationSavingsGoal { get; set; }
}

/// <summary>
/// Snapshot of a budget period's state - enables viewing historical budget performance
/// Created when a budget period ends and rolls over to the next period
/// </summary>
public class BudgetPeriodSnapshot
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  
  // Period dates
  public DateTime PeriodStart { get; set; }
  public DateTime PeriodEnd { get; set; }
  
  // Period summary totals (denormalized for fast lookup)
  public decimal TotalAllocated { get; set; }
  public decimal TotalSpent { get; set; }
  public decimal TotalIncome { get; set; }
  public decimal TotalRolloverIn { get; set; }   // Sum of all positive rollovers from previous period
  public decimal TotalRolloverOut { get; set; }  // Sum of all rollovers (positive or negative) to next period
  
  public DateTime CreatedAt { get; set; }
  
  // Navigation properties
  public Budget Budget { get; set; } = null!;
  public ICollection<BudgetPeriodCategorySnapshot> CategorySnapshots { get; set; } = new List<BudgetPeriodCategorySnapshot>();
}

/// <summary>
/// Snapshot of a single category's state at the end of a budget period
/// </summary>
public class BudgetPeriodCategorySnapshot
{
  public int Id { get; set; }
  public int BudgetPeriodSnapshotId { get; set; }
  // Nullable: the FK to BudgetCategories is ON DELETE SET NULL, so editing a budget (which deletes
  // and recreates its category rows) nulls this link on historical snapshots rather than 500ing on
  // a NOT NULL violation. The denormalized CategoryId/CategoryName below preserve the history.
  public int? BudgetCategoryId { get; set; }
  
  // Category identification (denormalized for historical reference)
  /// <summary>
  /// Unified category ID. System categories use IDs 1-99, custom categories use IDs 1000+.
  /// </summary>
  public int CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  
  // Amounts at period end
  public decimal AllocatedAmount { get; set; }
  public decimal SpentAmount { get; set; }
  public decimal RolloverIn { get; set; }    // Rollover balance at start of period (from previous)
  public decimal RolloverOut { get; set; }   // Rollover balance at end of period (to next) - can be negative!
  
  public bool AllowRollover { get; set; }
  public bool IsIncome { get; set; }
  
  // Navigation properties
  public BudgetPeriodSnapshot BudgetPeriodSnapshot { get; set; } = null!;
  public BudgetCategory? BudgetCategory { get; set; }
}

public enum TransferReason
{
  Manual = 0,              // User initiated transfer
  OverBudgetCoverage = 1,  // Covering overspending
  EndOfPeriodSurplus = 2,  // Moving leftover to savings
  ChildBudgetFunding = 3,  // Funding a child budget
  DeficitResolution = 4    // Resolving a budget deficit
}

/// <summary>
/// Controls how child budget surplus is handled at period end
/// </summary>
public enum ChildBudgetRolloverMode
{
  RolloverToSelf = 0,    // Unused funds carry forward in child budget
  AllocateToSavings = 1, // Unused funds go to linked savings goal
  PromptUser = 2         // Ask user at end of period
}
