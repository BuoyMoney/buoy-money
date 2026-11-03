using BuoyAI.Core.Enums;

namespace BuoyAI.Core.DTOs.Budget;

public class CountUpBudgetDto
{
  public int CategoryId { get; set; }
  public string CategoryName { get; set; } = string.Empty;
  
  // Traditional budget data
  public decimal AllocatedAmount { get; set; }
  public decimal ActualSpent { get; set; }
  
  // Count-up specific data
  public decimal DailyAllowance { get; set; }
  public int DaysElapsed { get; set; }
  public int TotalDays { get; set; }
  public decimal EarnedToDate { get; set; }
  public decimal AvailableBalance { get; set; }
  public decimal SpendingRate { get; set; }
  public bool IsOverEarned { get; set; }
  public decimal ProjectedSavings { get; set; }
  
  // Gamification data
  public int ConsecutiveDaysUnderBudget { get; set; }
  public int ZeroSpendingDays { get; set; }
  public List<CountUpAchievement> Achievements { get; set; } = new();
}

public class CountUpAchievement
{
  public string Title { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public DateTime EarnedDate { get; set; }
}

public class EnableCountUpRequest
{
  public int CategoryId { get; set; }
  public bool Enable { get; set; }
}

public class CountUpSettingsRequest
{
  public int CategoryId { get; set; }
  public decimal? CustomDailyAllowance { get; set; }
}
