using BuoyAI.Core.Enums;
using BuoyAI.Core.Entities;

namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Request to transfer funds from one category to another.
/// </summary>
public class TransferFundsRequest
{
  /// <summary>
  /// The category ID to transfer funds FROM.
  /// </summary>
  public int SourceCategoryId { get; set; }
  
  /// <summary>
  /// The amount to transfer.
  /// </summary>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Optional note explaining the transfer.
  /// </summary>
  public string? Note { get; set; }
}

/// <summary>
/// Request to transfer funds from a category to a savings goal.
/// </summary>
public class TransferToSavingsRequest
{
  /// <summary>
  /// The savings goal ID to transfer funds TO.
  /// </summary>
  public int SavingsGoalId { get; set; }
  
  /// <summary>
  /// The amount to transfer.
  /// </summary>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Optional note explaining the transfer.
  /// </summary>
  public string? Note { get; set; }
  
  /// <summary>
  /// The reason for the transfer (manual, rollover, etc.).
  /// </summary>
  public TransferReason Reason { get; set; } = TransferReason.Manual;
}

/// <summary>
/// Response after transferring funds to savings.
/// </summary>
public class TransferToSavingsResponse
{
  public int TransferId { get; set; }
  public BudgetCategoryDto SourceCategory { get; set; } = null!;
  public int SavingsGoalId { get; set; }
  public string SavingsGoalName { get; set; } = string.Empty;
  public decimal Amount { get; set; }
  public decimal NewGoalCurrentAmount { get; set; }
  public DateTime TransferredAt { get; set; }
}

/// <summary>
/// Response after transferring funds between categories.
/// </summary>
public class TransferFundsResponse
{
  public int TransferId { get; set; }
  public BudgetCategoryDto SourceCategory { get; set; } = null!;
  public BudgetCategoryDto TargetCategory { get; set; } = null!;
  public decimal Amount { get; set; }
  public DateTime TransferredAt { get; set; }
}

/// <summary>
/// DTO for budget transfer history.
/// </summary>
public class BudgetTransferDto
{
  public int Id { get; set; }
  
  // Source (one of these will be set)
  public int? SourceCategoryId { get; set; }
  public string? SourceCategoryName { get; set; }
  public int? SourceSavingsGoalId { get; set; }
  public string? SourceSavingsGoalName { get; set; }
  
  // Destination (one of these will be set)
  public int? TargetCategoryId { get; set; }
  public string? TargetCategoryName { get; set; }
  public int? TargetSavingsGoalId { get; set; }
  public string? TargetSavingsGoalName { get; set; }
  
  public decimal Amount { get; set; }
  public string? Note { get; set; }
  public string? Reason { get; set; }
  public string CreatedByUserName { get; set; } = string.Empty;
  public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Response after resetting rollovers.
/// </summary>
public class ResetRolloversResponse
{
  public int CategoriesReset { get; set; }
  public decimal TotalRolloverCleared { get; set; }
  public List<CategoryRolloverResetDto> ResetCategories { get; set; } = new();
}

/// <summary>
/// Details of a single category rollover reset.
/// </summary>
public class CategoryRolloverResetDto
{
  public int CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  public decimal PreviousRollover { get; set; }
}
