namespace BuoyAI.Core.DTOs.Budget;

/// <summary>
/// Resolution strategy for handling budget deficits.
/// </summary>
public enum DeficitResolutionStrategy
{
  /// <summary>
  /// Pull funds from the default savings goal.
  /// </summary>
  PullFromSavings = 0,
  
  /// <summary>
  /// Transfer funds from another budget category with surplus.
  /// </summary>
  TransferFromCategory = 1,
  
  /// <summary>
  /// Carry deficit as negative rollover to next period.
  /// </summary>
  CarryAsNegativeRollover = 2
}

/// <summary>
/// Request to resolve a budget category deficit.
/// </summary>
public class ResolveDeficitRequest
{
  /// <summary>
  /// The category ID that has a deficit.
  /// </summary>
  public int CategoryId { get; set; }
  
  /// <summary>
  /// The total deficit amount to resolve.
  /// </summary>
  public decimal DeficitAmount { get; set; }
  
  /// <summary>
  /// List of resolution actions to apply (can be partial resolutions).
  /// </summary>
  public List<DeficitResolutionAction> Actions { get; set; } = new();
}

/// <summary>
/// A single resolution action for part or all of a deficit.
/// </summary>
public class DeficitResolutionAction
{
  /// <summary>
  /// The strategy to use.
  /// </summary>
  public DeficitResolutionStrategy Strategy { get; set; }
  
  /// <summary>
  /// Amount to resolve with this action.
  /// </summary>
  public decimal Amount { get; set; }
  
  /// <summary>
  /// Source category ID (required for TransferFromCategory).
  /// </summary>
  public int? SourceCategoryId { get; set; }
  
  /// <summary>
  /// Savings goal ID (required for PullFromSavings).
  /// </summary>
  public int? SavingsGoalId { get; set; }
}

/// <summary>
/// Response after resolving a budget deficit.
/// </summary>
public class ResolveDeficitResponse
{
  /// <summary>
  /// Total amount that was resolved.
  /// </summary>
  public decimal TotalResolved { get; set; }
  
  /// <summary>
  /// Remaining deficit (if any) carried as negative rollover.
  /// </summary>
  public decimal RemainingDeficit { get; set; }
  
  /// <summary>
  /// The updated category after resolution.
  /// </summary>
  public BudgetCategoryDto UpdatedCategory { get; set; } = null!;
  
  /// <summary>
  /// Details of each resolution action performed.
  /// </summary>
  public List<ResolutionActionResult> ActionResults { get; set; } = new();
}

/// <summary>
/// Result of a single resolution action.
/// </summary>
public class ResolutionActionResult
{
  public DeficitResolutionStrategy Strategy { get; set; }
  public decimal Amount { get; set; }
  public bool Success { get; set; }
  public string? Message { get; set; }
  
  /// <summary>
  /// Source category name (for transfer).
  /// </summary>
  public string? SourceName { get; set; }
  
  /// <summary>
  /// Savings goal name (for pull from savings).
  /// </summary>
  public string? SavingsGoalName { get; set; }
}
