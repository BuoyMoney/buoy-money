namespace BuoyAI.Core.DTOs;

/// <summary>
/// "Money Wrapped" recap DTOs — the monthly short story (opened from the
/// money_wrapped notification) and the full yearly recap. Computed on read;
/// nothing is persisted. Share surfaces on the client must not include dollar
/// amounts (GLBA/privacy rule shared with ShareCard) — amounts here are for
/// the in-app story only.
/// </summary>
public class MonthlyWrappedDto
{
  public int Year { get; set; }
  public int Month { get; set; }

  public decimal TotalIncome { get; set; }
  public decimal TotalSpent { get; set; }

  /// <summary>Previous month's spend, for the delta beat. Null when there is no prior data.</summary>
  public decimal? PreviousMonthSpent { get; set; }

  public WrappedCategoryDto? TopCategory { get; set; }
  public WrappedMerchantDto? TopMerchant { get; set; }

  public int ZeroSpendDays { get; set; }

  /// <summary>Longest current streak (any type) at recap time.</summary>
  public int LongestStreak { get; set; }

  public List<WrappedBadgeDto> BadgesEarned { get; set; } = new();

  /// <summary>Money moved into savings goals during the month (allocations + earmarked deposits).</summary>
  public decimal SavedToGoals { get; set; }

  public WrappedBiggestDayDto? BiggestSpendDay { get; set; }

  public int TransactionCount { get; set; }

  /// <summary>Up to 3 extra story cards: rotation/AI picks + a percentile.</summary>
  public List<WrappedBonusCardDto> BonusCards { get; set; } = new();
}

public class YearlyWrappedDto
{
  public int Year { get; set; }

  public decimal TotalIncome { get; set; }
  public decimal TotalSpent { get; set; }

  /// <summary>Spend per calendar month, index 0 = January. Months with no data are 0.</summary>
  public List<decimal> MonthlySpend { get; set; } = new();

  public List<WrappedCategoryDto> TopCategories { get; set; } = new();
  public WrappedMerchantDto? TopMerchant { get; set; }

  public int ZeroSpendDays { get; set; }
  public int LongestStreak { get; set; }

  public int BadgesEarnedCount { get; set; }

  /// <summary>The highest-point badge earned this year — the trophy shot.</summary>
  public WrappedBadgeDto? BestBadge { get; set; }

  public decimal SavedToGoals { get; set; }
  public WrappedBiggestDayDto? BiggestSpendDay { get; set; }

  public int TransactionCount { get; set; }

  /// <summary>Up to 3 extra story cards: rotation/AI picks + a percentile.</summary>
  public List<WrappedBonusCardDto> BonusCards { get; set; } = new();
}

public class WrappedCategoryDto
{
  public int CategoryId { get; set; }
  public string Name { get; set; } = string.Empty;
  public decimal Amount { get; set; }

  /// <summary>Share of total spend, 0–100.</summary>
  public decimal Share { get; set; }
}

public class WrappedMerchantDto
{
  public string Name { get; set; } = string.Empty;
  public int Visits { get; set; }
  public decimal Amount { get; set; }
}

public class WrappedBadgeDto
{
  public string Key { get; set; } = string.Empty;
  public string Title { get; set; } = string.Empty;
  public string Icon { get; set; } = string.Empty;
  public int Points { get; set; }
}

public class WrappedBiggestDayDto
{
  public DateTime Date { get; set; }
  public decimal Amount { get; set; }
}

/// <summary>
/// One candidate stat from the monthly fact sheet — fully preformatted
/// server-side. The AI garnish may PICK and PHRASE these; it never computes:
/// Value/Detail strings are rendered from our numbers verbatim.
/// </summary>
public class WrappedFactDto
{
  public string Key { get; set; } = string.Empty;
  public string Kicker { get; set; } = string.Empty;
  public string Headline { get; set; } = string.Empty;
  public string Value { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;

  /// <summary>0-1 interest score driving the deterministic rotation.</summary>
  public double Surprise { get; set; }
}

/// <summary>A bonus story card. Source: rotation | ai | percentile.</summary>
public class WrappedBonusCardDto
{
  public string FactKey { get; set; } = string.Empty;
  public string Kicker { get; set; } = string.Empty;
  public string Headline { get; set; } = string.Empty;
  public string Value { get; set; } = string.Empty;
  public string Detail { get; set; } = string.Empty;
  public string Source { get; set; } = "rotation";
}
