using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.Interfaces;

public interface IBudgetPeriodService
{
  /// <summary>
  /// Calculate the current period start and end dates based on budget period type.
  /// Pass utcOffsetMinutes to anchor period boundaries to the user's local midnight instead of UTC.
  /// </summary>
  (DateTime start, DateTime end) CalculateCurrentPeriod(BudgetPeriod period, DateTime? referenceDate = null, int? utcOffsetMinutes = null, DayOfWeek? weekStart = null);

  /// <summary>
  /// Calculate the next period start and end dates
  /// </summary>
  (DateTime start, DateTime end) CalculateNextPeriod(BudgetPeriod period, DateTime currentPeriodEnd);

  /// <summary>
  /// Get the number of days in the current period (handles variable month lengths)
  /// </summary>
  int GetDaysInPeriod(BudgetPeriod period, DateTime periodStart, DateTime periodEnd);

  /// <summary>
  /// Check if a budget needs to be reset based on current date.
  /// Pass utcOffsetMinutes to compare against the user's local date instead of UTC date.
  /// </summary>
  bool ShouldResetBudget(Budget budget, DateTime currentDate, int? utcOffsetMinutes = null);
  
  /// <summary>
  /// Process budget reset with rollover calculations
  /// </summary>
  Task<BudgetPeriodSummaryDto> ResetBudgetAsync(Budget budget);
  
  /// <summary>
  /// Generate encouraging summary message for completed period
  /// </summary>
  string GenerateEncouragementMessage(BudgetPeriodSummaryDto summary, string? currencyCode = null);
}
