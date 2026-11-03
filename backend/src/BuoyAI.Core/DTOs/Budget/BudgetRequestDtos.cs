using BuoyAI.Core.Enums;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Request to create a new budget.
/// </summary>
public class CreateBudgetRequest
{
  public string Name { get; set; } = string.Empty;
  public BudgetPeriod Period { get; set; }
  public bool AutoReset { get; set; } = true;
  public decimal TotalIncome { get; set; }
  public List<CreateBudgetCategoryRequest> Categories { get; set; } = new();
  
  // Hierarchical budget options
  // Type determines visibility: Personal = owner only, Family = all family members
  public BudgetType Type { get; set; } = BudgetType.Personal;
  public int? ParentBudgetId { get; set; }
  public decimal? AllocatedAmount { get; set; }

  // ── Family "fund from" options (RFC #793) ──────────────────────────────
  // For a Type=Personal budget created inside a family, these converge the new personal
  // child onto the linked family-category model (the same mechanism allowances use):
  //   • FundFromCategoryId   — link an EXISTING family expense category to fund this budget.
  //   • NewFamilyCategoryName — create a NEW family category to fund it (name is best-effort;
  //     stored on the child budget because BudgetCategory has no name of its own).
  // Setting either is an allocation action and requires Owner/BudgetAdmin. Leaving both null
  // keeps the pre-existing behavior: a plain member can still create a standalone personal
  // budget with no family link, or one from an allowance already assigned to them.
  public int? FundFromCategoryId { get; set; }
  public string? NewFamilyCategoryName { get; set; }
}

/// <summary>
/// Request to spawn a personal budget from an existing family expense category (RFC #793).
/// Owner/BudgetAdmin only. The target family category is re-purposed as the funding link
/// (IsAllowance + LinkedBudgetId + AssignedUserId) while keeping its real CategoryId so its
/// existing transactions stay attributed to the family budget.
/// </summary>
public class SpawnPersonalBudgetRequest
{
  /// <summary>Family member who receives the personal budget. Null → defaults to the caller.</summary>
  public int? AssignedUserId { get; set; }

  /// <summary>Allocation for the new budget. Null → defaults to the category's current AllocatedAmount.</summary>
  public decimal? AllocatedAmount { get; set; }

  /// <summary>Name for the new budget. Null → defaults to "{member first name}'s Budget".</summary>
  public string? BudgetName { get; set; }
}

/// <summary>
/// Request to create a category within a budget.
/// </summary>
public class CreateBudgetCategoryRequest
{
  /// <summary>
  /// Unified category ID from the Categories table.
  /// System categories use IDs 1-99, user categories use IDs 1000+.
  /// </summary>
  public int CategoryId { get; set; }
  
  public decimal AllocatedAmount { get; set; }
  public bool AllowRollover { get; set; } = true;
  
  /// <summary>
  /// Maximum rollover as a multiplier of AllocatedAmount. Null = unlimited.
  /// </summary>
  public decimal? MaxRolloverMultiplier { get; set; }
  
  /// <summary>
  /// Required when MaxRolloverMultiplier is set. Savings goal for overflow.
  /// </summary>
  public int? RolloverOverflowSavingsGoalId { get; set; }

  /// <summary>
  /// Category-level savings goal that backs this category (auto-transfer at period end
  /// for Count-Up TransferToSavings). Can now be set at creation, not only via update.
  /// </summary>
  public int? SavingsGoalId { get; set; }

  // Income category flag
  public bool IsIncome { get; set; } = false;  // True for income categories (Salary, Bonus, etc.)
  
  // Count-up budgeting
  public bool IsCountUpEnabled { get; set; } = false;
  
  // Allowance options (only for expense categories)
  public bool IsAllowance { get; set; } = false;
  public int? AssignedUserId { get; set; }
}

/// <summary>
/// Request to update a budget category's settings.
/// </summary>
public class UpdateBudgetCategoryRequest
{
  public decimal AllocatedAmount { get; set; }
  public bool AllowRollover { get; set; }
  
  /// <summary>
  /// Maximum rollover as a multiplier of AllocatedAmount. Null = unlimited.
  /// </summary>
  public decimal? MaxRolloverMultiplier { get; set; }
  
  /// <summary>
  /// Savings goal for rollover overflow. Required when MaxRolloverMultiplier is set.
  /// </summary>
  public int? RolloverOverflowSavingsGoalId { get; set; }
  
  /// <summary>
  /// Controls what happens to earned-but-unspent balance at period end for Count-Up categories.
  /// Only applies when IsCountUpEnabled = true.
  /// </summary>
  public CountUpRolloverMode? CountUpRolloverMode { get; set; }
  
  /// <summary>
  /// Category-level savings goal for auto-transfer at period end.
  /// Used when CountUpRolloverMode = TransferToSavings.
  /// </summary>
  public int? SavingsGoalId { get; set; }
  
  /// <summary>
  /// If true, sets DefaultAllocatedAmount to match AllocatedAmount for future periods.
  /// </summary>
  public bool ApplyToFuturePeriods { get; set; } = false;
}

/// <summary>
/// Request to create an allowance budget from a parent budget.
/// </summary>
public class CreateAllowanceBudgetRequest : CreateBudgetRequest
{
  /// <summary>
  /// The user ID who will receive this allowance.
  /// </summary>
  public int AssignedUserId { get; set; }
}

/// <summary>
/// Request to create a child budget (personal sub-budget).
/// </summary>
public class CreateChildBudgetRequest
{
  public string Name { get; set; } = string.Empty;
  public decimal FundingAmount { get; set; }
  public bool IsSharedWithFamily { get; set; }
  public ChildBudgetRolloverMode RolloverMode { get; set; } = ChildBudgetRolloverMode.RolloverToSelf;
  public int? DefaultSavingsGoalId { get; set; }
  public List<CreateBudgetCategoryRequest> Categories { get; set; } = new();
}

/// <summary>
/// Request to update a child budget's settings.
/// </summary>
public class UpdateChildBudgetRequest
{
  public string? Name { get; set; }
  public decimal? FundingAmount { get; set; }
  public bool? IsSharedWithFamily { get; set; }
  public ChildBudgetRolloverMode? RolloverMode { get; set; }
  public int? DefaultSavingsGoalId { get; set; }
}

/// <summary>
/// Request to share a budget with another user.
/// </summary>
public class ShareBudgetRequest
{
  /// <summary>
  /// The user ID to share the budget with.
  /// </summary>
  public int ShareWithUserId { get; set; }
  
  /// <summary>
  /// Whether the shared user can edit the budget.
  /// </summary>
  public bool CanEdit { get; set; } = false;
  
  /// <summary>
  /// Whether the shared user can view individual transactions.
  /// </summary>
  public bool CanViewTransactions { get; set; } = true;
}

/// <summary>
/// Request for inline editing of category amounts/settings.
/// </summary>
public class InlineUpdateCategoryRequest
{
  public decimal? AllocatedAmount { get; set; }
  public bool? AllowRollover { get; set; }
  public bool? IsCountUpEnabled { get; set; }
  public int? SortOrder { get; set; }
}
