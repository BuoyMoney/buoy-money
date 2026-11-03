using System.ComponentModel.DataAnnotations;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Core budget data transfer object representing a complete budget view.
/// </summary>
public class BudgetDto
{
  public int Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public BudgetPeriod Period { get; set; }
  
  // Flexible period model
  public DateTime CurrentPeriodStart { get; set; }
  public DateTime CurrentPeriodEnd { get; set; }
  public DateTime LastReset { get; set; }
  public bool AutoReset { get; set; }
  public int DaysInCurrentPeriod { get; set; }
  public int DaysElapsed { get; set; }
  
  public decimal TotalIncome { get; set; }
  public decimal TotalExpenses { get; set; }
  public bool IsActive { get; set; }
  public List<BudgetCategoryDto> Categories { get; set; } = new();
  
  // Hierarchical budget properties
  public int UserId { get; set; }
  public int? FamilyId { get; set; }
  public int? ParentBudgetId { get; set; }
  public decimal? AllocatedAmount { get; set; }
  public BudgetType Type { get; set; }
  public bool IsPrimary { get; set; }
  
  // Child budget rollover settings
  public ChildBudgetRolloverMode RolloverMode { get; set; }
  public int? DefaultSavingsGoalId { get; set; }
  public string? DefaultSavingsGoalName { get; set; }
  
  /// <summary>
  /// Computed from Type == BudgetType.Family. Kept for backward compatibility.
  /// </summary>
  public bool IsSharedWithFamily => Type == BudgetType.Family;

  /// <summary>
  /// RFC #793 — a personal budget and an allowance budget are ONE user-facing concept: a
  /// personal budget that is funded by a family allocation. This is TRUE for both a linked
  /// Type=Personal child AND a legacy Type=Allowance budget, so the client can treat them
  /// identically without ever branching on the <see cref="Type"/> enum. Standalone personal
  /// budgets (no family funding) are FALSE.
  /// </summary>
  public bool IsFamilyFunded { get; set; }

  /// <summary>
  /// The family allocation backing this budget when <see cref="IsFamilyFunded"/> is true
  /// (mirrors <see cref="AllocatedAmount"/>). Null for standalone personal / non-funded budgets.
  /// </summary>
  public decimal? FamilyAllocationAmount { get; set; }
  
  // Additional context
  public string? OwnerName { get; set; }
  public List<BudgetDto>? ChildBudgets { get; set; }
  public List<BudgetShareDto>? Shares { get; set; }
  
  // Period summary for encouragement
  public BudgetPeriodSummaryDto? LastPeriodSummary { get; set; }

  // ── Savings transfers ───────────────────────────────────────────
  // Transactions this period that were earmarked to a savings goal instead of a
  // budget category. Surfaced as their own section so users can see money diverted
  // to/from goals (it is excluded from TotalIncome / TotalExpenses / category spend).
  public List<BudgetSavingsTransferDto> SavingsTransfers { get; set; } = new();

  /// <summary>Sum of income earmarked into goals this period (money in).</summary>
  public decimal TotalSavingsDeposits { get; set; }

  /// <summary>Sum of expenses earmarked out of goals this period (money out).</summary>
  public decimal TotalSavingsWithdrawals { get; set; }

  /// <summary>Server-side last-modified timestamp (UTC); client uses it as baseUpdatedAt for sync (#493).</summary>
  public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single transaction that was earmarked to a savings goal within a budget period.
/// </summary>
public class BudgetSavingsTransferDto
{
  public int TransactionId { get; set; }
  public string Description { get; set; } = string.Empty;

  /// <summary>Absolute amount moved (always positive; direction is in IsDeposit).</summary>
  public decimal Amount { get; set; }

  /// <summary>True = income deposited into the goal; false = expense withdrawn from it.</summary>
  public bool IsDeposit { get; set; }

  public int SavingsGoalId { get; set; }
  public string SavingsGoalName { get; set; } = string.Empty;
  public DateTime Date { get; set; }
}

/// <summary>
/// Represents a budget share with another user.
/// </summary>
public class BudgetShareDto
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  public int SharedWithUserId { get; set; }
  public string SharedWithUserName { get; set; } = string.Empty;
  public bool CanEdit { get; set; }
  public bool CanViewTransactions { get; set; }
}

/// <summary>
/// Represents a category within a budget with spending tracking.
/// </summary>
public class BudgetCategoryDto
{
  public int Id { get; set; }
  public int BudgetId { get; set; }
  
  /// <summary>
  /// Unified category ID from the Categories table.
  /// System categories use IDs 1-99, user categories use IDs 1000+.
  /// </summary>
  public int CategoryId { get; set; }
  
  public string CategoryName { get; set; } = string.Empty;
  public decimal AllocatedAmount { get; set; }
  public decimal SpentAmount { get; set; }
  public decimal RemainingAmount => EffectiveAmount - SpentAmount;
  public decimal PercentageUsed => EffectiveAmount > 0 ? (SpentAmount / EffectiveAmount) * 100 : 0;
  
  // Grouping
  public int? GroupId { get; set; }
  public string? GroupName { get; set; }
  public int SortOrder { get; set; }
  
  // Income vs Expense
  public bool IsIncome { get; set; }
  public decimal? DefaultAllocatedAmount { get; set; }
  
  // Rollover functionality
  public bool AllowRollover { get; set; }
  public decimal RolloverBalance { get; set; }
  public decimal EffectiveAmount => AllocatedAmount + RolloverBalance;
  
  /// <summary>
  /// Maximum rollover as a multiplier of AllocatedAmount (e.g., 1.0 = 100%, 2.0 = 200%).
  /// Null means no limit.
  /// </summary>
  public decimal? MaxRolloverMultiplier { get; set; }
  
  /// <summary>Savings goal ID where rollover overflow is sent.</summary>
  public int? RolloverOverflowSavingsGoalId { get; set; }
  
  /// <summary>Name of the savings goal for rollover overflow (for display).</summary>
  public string? RolloverOverflowSavingsGoalName { get; set; }
  
  // Count-up budgeting
  public bool IsCountUpEnabled { get; set; }
  
  /// <summary>
  /// Controls what happens to earned-but-unspent balance at period end for Count-Up categories.
  /// Only applies when IsCountUpEnabled = true.
  /// </summary>
  public CountUpRolloverMode? CountUpRolloverMode { get; set; }
  
  /// <summary>
  /// Category-level savings goal for auto-transfer at period end.
  /// Used when CountUpRolloverMode = TransferToSavings, or when budget.RolloverMode = AllocateToSavings.
  /// </summary>
  public int? SavingsGoalId { get; set; }
  
  /// <summary>Name of the savings goal for auto-transfer (for display).</summary>
  public string? SavingsGoalName { get; set; }
  
  // Allowance support
  public bool IsAllowance { get; set; }
  public int? LinkedBudgetId { get; set; }
  public int? AssignedUserId { get; set; }
  public string? AssignedUserName { get; set; }
  
  /// <summary>
  /// True if this is the virtual "Uncategorized" category (not a user-defined budget category).
  /// </summary>
  public bool IsVirtual { get; set; } = false;
  
  /// <summary>
  /// Number of transactions in this category for the current period.
  /// </summary>
  public int TransactionCount { get; set; }

  /// <summary>Server-side last-modified timestamp (UTC); client uses it as baseUpdatedAt for sync (#493).</summary>
  public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Period summary for encouragement messaging.
/// </summary>
public class BudgetPeriodSummaryDto
{
  public DateTime PeriodStart { get; set; }
  public DateTime PeriodEnd { get; set; }
  public decimal TotalSavings { get; set; }
  public decimal TotalRollover { get; set; }
  public List<BudgetCategorySummaryDto> CategorySummaries { get; set; } = new();
  public string EncouragementMessage { get; set; } = string.Empty;
}

/// <summary>
/// Summary of a budget category's performance over a period.
/// Different from SubscriptionCategorySummaryDto which tracks subscription counts/amounts.
/// </summary>
public class BudgetCategorySummaryDto
{
  public string CategoryName { get; set; } = string.Empty;
  public decimal Allocated { get; set; }
  public decimal Spent { get; set; }
  public decimal Saved { get; set; }
  public decimal RolledOver { get; set; }
  public bool WasUnderBudget { get; set; }
  public bool IsIncome { get; set; }
}

// ─────────────────────────────────────────────────────────────────
// Period History (multi-month navigation)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Lightweight summary of a completed budget period, used in the period picker.
/// </summary>
public class BudgetPeriodListItemDto
{
  public int Id { get; set; }
  public DateTime PeriodStart { get; set; }
  public DateTime PeriodEnd { get; set; }
  public decimal TotalAllocated { get; set; }
  public decimal TotalSpent { get; set; }
}

/// <summary>
/// A single category's state as captured at the end of a completed period.
/// </summary>
public class BudgetPeriodCategoryHistoryDto
{
  public int Id { get; set; }
  public int CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  public decimal AllocatedAmount { get; set; }
  public decimal SpentAmount { get; set; }
  public decimal RolloverIn { get; set; }
  public decimal RolloverOut { get; set; }
  public bool IsIncome { get; set; }
  public bool AllowRollover { get; set; }
}

/// <summary>
/// Full detail of a completed period snapshot, including per-category data.
/// </summary>
public class BudgetPeriodDetailDto : BudgetPeriodListItemDto
{
  public decimal TotalIncome { get; set; }
  public List<BudgetPeriodCategoryHistoryDto> Categories { get; set; } = new();
}

/// <summary>
/// Request body for patching an allocated amount inside a historical period snapshot.
/// </summary>
public class UpdatePeriodCategoryAmountRequest
{
  [Range(0, 1_000_000, ErrorMessage = "AllocatedAmount must be between 0 and 1,000,000.")]
  public decimal AllocatedAmount { get; set; }
}
