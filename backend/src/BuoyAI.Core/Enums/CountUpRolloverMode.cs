namespace BuoyAI.Core.Enums;

/// <summary>
/// Controls how Count-Up category surplus is handled at period end.
/// Only applies when IsCountUpEnabled = true.
/// </summary>
public enum CountUpRolloverMode
{
  /// <summary>
  /// Earned-but-unspent balance carries forward to next period (default).
  /// </summary>
  RolloverToSelf = 0,
  
  /// <summary>
  /// Surplus automatically transfers to linked savings goal at period end.
  /// Uses category.SavingsGoalId or falls back to budget.DefaultSavingsGoalId.
  /// </summary>
  TransferToSavings = 1,
  
  /// <summary>
  /// Balance resets to zero at period end. Surplus is lost.
  /// </summary>
  ResetToZero = 2
}
