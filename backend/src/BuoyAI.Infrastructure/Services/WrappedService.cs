using Microsoft.EntityFrameworkCore;
using BuoyAI.Core.Analysis;
using BuoyAI.Core.DTOs;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;

namespace BuoyAI.Infrastructure.Services;

public interface IWrappedService
{
  /// <summary>Monthly recap. Null when the user has no transactions in that month.</summary>
  Task<MonthlyWrappedDto?> GetMonthlyAsync(int userId, int year, int month);

  /// <summary>Yearly recap (year-to-date for the current year). Null when there is no data.</summary>
  Task<YearlyWrappedDto?> GetYearlyAsync(int userId, int year);

  /// <summary>The candidate fact sheet for a period (month 0 = yearly). Empty when no data.</summary>
  Task<List<WrappedFactDto>> GetFactsAsync(int userId, int year, int month);
}

/// <summary>
/// Computes the "Money Wrapped" recaps on read. Income/spend totals come from
/// CashFlowCalculator (transfers/card-payments excluded, refunds netted — the
/// blessed source; a naive sum overstates). Category totals mirror the
/// TransactionSummary exclusions via the budgetable-expense extensions.
/// </summary>
public class WrappedService : IWrappedService
{
  private readonly BuoyAIDbContext _context;
  private readonly ITransactionRepository _transactions;

  public WrappedService(BuoyAIDbContext context, ITransactionRepository transactions)
  {
    _context = context;
    _transactions = transactions;
  }

  public async Task<MonthlyWrappedDto?> GetMonthlyAsync(int userId, int year, int month)
  {
    if (month is < 1 or > 12) return null;
    var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
    var endExclusive = start.AddMonths(1);

    var familyId = await GetFamilyIdAsync(userId);
    var txns = (await GetPostedTransactionsAsync(userId, familyId, start, endExclusive)).ToList();
    if (txns.Count == 0) return null;

    var cashFlow = CashFlowCalculator.Summarize(txns, start, endExclusive);
    var expenses = txns.WhereBudgetableExpense().ToList();
    var spendDays = expenses.Select(t => t.Date.Date).ToHashSet();

    // Previous month spend for the delta beat.
    var prevStart = start.AddMonths(-1);
    var prevTxns = (await GetPostedTransactionsAsync(userId, familyId, prevStart, start)).ToList();
    decimal? prevSpent = prevTxns.Count == 0
        ? null
        : CashFlowCalculator.Summarize(prevTxns, prevStart, start).MoneySpent;

    // Completed months count their full day span; an in-progress period only
    // counts through YESTERDAY — today isn't over, so it can't be a confirmed
    // zero-spend day yet (it would inflate the stat, then go stale by dinner).
    var lastCountedDay = endExclusive > DateTime.UtcNow
        ? DateTime.UtcNow.Date.AddDays(-1)
        : endExclusive.AddDays(-1);

    var dto = new MonthlyWrappedDto
    {
      Year = year,
      Month = month,
      TotalIncome = cashFlow.IncomeReceived,
      TotalSpent = cashFlow.MoneySpent,
      PreviousMonthSpent = prevSpent,
      TopCategory = TopCategories(expenses, 1).FirstOrDefault(),
      TopMerchant = TopMerchant(expenses),
      ZeroSpendDays = ZeroSpendDays(txns, start, lastCountedDay),
      BiggestSpendDay = BiggestSpendDay(expenses),
      TransactionCount = cashFlow.TransactionsCounted,
      SavedToGoals = await SavedToGoalsAsync(userId, familyId, start, endExclusive),
      BadgesEarned = await BadgesInPeriodAsync(userId, start, endExclusive),
      LongestStreak = await LongestStreakAsync(userId),
    };

    // Bonus cards: the cached AI/rotation picks pinned by the recap job when
    // available (stable story, no model on the read path), else a live
    // deterministic pick — plus at most one cohort-gated percentile card.
    var facts = WrappedFactSheet.Build(
        expenses, prevTxns.WhereBudgetableExpense().ToList(),
        await PriorMerchantsAsync(userId, familyId, start),
        spendDays, start, lastCountedDay,
        await UserCurrencyAsync(userId));
    dto.BonusCards = await CachedOrRotationAsync(userId, year, month, facts);
    var percentile = await PercentileCardAsync(dto.ZeroSpendDays,
        dto.TotalIncome > 0 ? dto.SavedToGoals / dto.TotalIncome : 0);
    if (percentile != null) dto.BonusCards.Add(percentile);
    return dto;
  }

  public async Task<YearlyWrappedDto?> GetYearlyAsync(int userId, int year)
  {
    var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    var endExclusive = start.AddYears(1);

    var familyId = await GetFamilyIdAsync(userId);
    var txns = (await GetPostedTransactionsAsync(userId, familyId, start, endExclusive)).ToList();
    if (txns.Count == 0) return null;

    var cashFlow = CashFlowCalculator.Summarize(txns, start, endExclusive);
    var expenses = txns.WhereBudgetableExpense().ToList();

    var monthlySpend = new List<decimal>();
    for (var m = 1; m <= 12; m++)
    {
      var ms = new DateTime(year, m, 1, 0, 0, 0, DateTimeKind.Utc);
      var monthTxns = txns.Where(t => t.Date >= ms && t.Date < ms.AddMonths(1)).ToList();
      monthlySpend.Add(monthTxns.Count == 0
          ? 0m
          : CashFlowCalculator.Summarize(monthTxns, ms, ms.AddMonths(1)).MoneySpent);
    }

    var badges = await BadgesInPeriodAsync(userId, start, endExclusive);
    var lastCountedDay = endExclusive > DateTime.UtcNow
        ? DateTime.UtcNow.Date.AddDays(-1)
        : endExclusive.AddDays(-1);
    var spendDays = expenses.Select(t => t.Date.Date).ToHashSet();

    var yearlyDto = new YearlyWrappedDto
    {
      Year = year,
      TotalIncome = cashFlow.IncomeReceived,
      TotalSpent = cashFlow.MoneySpent,
      MonthlySpend = monthlySpend,
      TopCategories = TopCategories(expenses, 5),
      TopMerchant = TopMerchant(expenses),
      ZeroSpendDays = ZeroSpendDays(txns, start, lastCountedDay),
      LongestStreak = await LongestStreakAsync(userId, useBest: true),
      BadgesEarnedCount = badges.Count,
      BestBadge = badges.OrderByDescending(b => b.Points).FirstOrDefault(),
      SavedToGoals = await SavedToGoalsAsync(userId, familyId, start, endExclusive),
      BiggestSpendDay = BiggestSpendDay(expenses),
      TransactionCount = cashFlow.TransactionsCounted,
    };

    var yearFacts = WrappedFactSheet.Build(
        expenses, new List<Transaction>(),
        await PriorMerchantsAsync(userId, familyId, start),
        spendDays, start, lastCountedDay,
        await UserCurrencyAsync(userId));
    yearlyDto.BonusCards = await CachedOrRotationAsync(userId, year, 0, yearFacts);
    return yearlyDto;
  }

  public async Task<List<WrappedFactDto>> GetFactsAsync(int userId, int year, int month)
  {
    var start = month == 0
        ? new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        : new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
    var endExclusive = month == 0 ? start.AddYears(1) : start.AddMonths(1);

    var familyId = await GetFamilyIdAsync(userId);
    var txns = (await GetPostedTransactionsAsync(userId, familyId, start, endExclusive)).ToList();
    if (txns.Count == 0) return new List<WrappedFactDto>();

    var expenses = txns.WhereBudgetableExpense().ToList();
    var prevExpenses = new List<Transaction>();
    if (month != 0)
    {
      var prevStart = start.AddMonths(-1);
      prevExpenses = (await GetPostedTransactionsAsync(userId, familyId, prevStart, start))
          .WhereBudgetableExpense().ToList();
    }
    var lastCountedDay = endExclusive > DateTime.UtcNow
        ? DateTime.UtcNow.Date.AddDays(-1)
        : endExclusive.AddDays(-1);

    return WrappedFactSheet.Build(
        expenses, prevExpenses,
        await PriorMerchantsAsync(userId, familyId, start),
        expenses.Select(t => t.Date.Date).ToHashSet(),
        start, lastCountedDay,
        await UserCurrencyAsync(userId));
  }

  // ── Bonus-card assembly ───────────────────────────────────────────────────

  /// <summary>Only show ranking cards once the cohort is big enough to be
  /// both statistically meaningful and non-deanonymizing.</summary>
  internal const int MinPercentileCohort = 100;

  private async Task<List<WrappedBonusCardDto>> CachedOrRotationAsync(
      int userId, int year, int month, List<WrappedFactDto> facts)
  {
    var cached = await _context.WrappedBonusCardCaches.AsNoTracking()
        .FirstOrDefaultAsync(c => c.UserId == userId && c.Year == year && c.Month == month);
    if (cached != null)
    {
      try
      {
        var cards = System.Text.Json.JsonSerializer
            .Deserialize<List<WrappedBonusCardDto>>(cached.CardsJson);
        if (cards is { Count: > 0 }) return cards;
      }
      catch (System.Text.Json.JsonException)
      {
        // fall through to rotation
      }
    }
    return WrappedGarnishService.RotationPick(facts);
  }

  /// <summary>
  /// At most ONE percentile card, flattering-or-nothing: Wrapped celebrates,
  /// it never negs (the honest mirror lives on the Insights page). Gated on
  /// cohort size.
  /// </summary>
  private async Task<WrappedBonusCardDto?> PercentileCardAsync(
      int zeroSpendDays, decimal savingsRate)
  {
    var cutoffs = await _context.WrappedStatCutoffs.AsNoTracking().ToListAsync();
    WrappedBonusCardDto? best = null;
    var bestTop = 51; // only show top-half results

    void Consider(string metricKey, double value, string headline, string detail)
    {
      var row = cutoffs.FirstOrDefault(c => c.MetricKey == metricKey);
      if (row == null || row.Cohort < MinPercentileCohort) return;
      double[]? deciles;
      try
      {
        deciles = System.Text.Json.JsonSerializer.Deserialize<double[]>(row.CutoffsJson);
      }
      catch (System.Text.Json.JsonException)
      {
        return;
      }
      if (deciles == null || deciles.Length == 0) return;
      // deciles are p10..p90 ascending; count how many the user clears.
      var cleared = deciles.Count(d => value >= d);
      var topPct = 100 - cleared * 10; // cleared all 9 -> top 10%
      if (topPct < bestTop)
      {
        bestTop = topPct;
        best = new WrappedBonusCardDto
        {
          FactKey = metricKey,
          Kicker = "where you stand",
          Headline = headline,
          Value = $"Top {topPct}%",
          Detail = detail,
          Source = "percentile",
        };
      }
    }

    Consider("savings_rate", (double)savingsRate,
        "of Buoy Money savers this period", "Share of income moved to savings goals");
    Consider("zero_spend_days", zeroSpendDays,
        "for zero-spend days this period", "Days without spending a cent");

    return best;
  }

  private async Task<HashSet<string>> PriorMerchantsAsync(
      int userId, int? familyId, DateTime periodStart)
  {
    var lookbackStart = periodStart.AddMonths(-6);
    return (await _context.Transactions.AsNoTracking()
        .Where(t => (t.Account.UserId == userId
                || (familyId != null && t.Account.FamilyId == familyId))
            && t.Date >= lookbackStart && t.Date < periodStart
            && t.MerchantName != null && t.MerchantName != "")
        .Select(t => t.MerchantName!)
        .Distinct()
        .ToListAsync())
        .ToHashSet();
  }

  // ── Building blocks ───────────────────────────────────────────────────────

  // Wrapped copy quotes amounts; write them in the user's primary currency (#941).
  private async Task<string?> UserCurrencyAsync(int userId) =>
      await _context.Users.Where(u => u.Id == userId).Select(u => u.Currency).FirstOrDefaultAsync();

  private async Task<int?> GetFamilyIdAsync(int userId)
  {
    return await _context.Users.AsNoTracking()
        .Where(u => u.Id == userId)
        .Select(u => u.FamilyId)
        .FirstOrDefaultAsync();
  }

  private async Task<IEnumerable<Transaction>> GetPostedTransactionsAsync(
      int userId, int? familyId, DateTime start, DateTime endExclusive)
  {
    // Family-aware scoping per project rules; repository end is inclusive, so
    // subtract a tick to cover time-bearing last-day rows without spilling over.
    var txns = await _transactions.GetByUserOrFamilyAsync(
        userId, familyId, start, endExclusive.AddTicks(-1));
    return txns.Where(t => !t.IsPending);
  }

  private static List<WrappedCategoryDto> TopCategories(List<Transaction> expenses, int take)
  {
    var total = expenses.Sum(t => Math.Abs(t.Amount));
    if (total <= 0) return new List<WrappedCategoryDto>();
    return expenses
        .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
        .Select(g => new WrappedCategoryDto
        {
          CategoryId = g.Key,
          Name = g.First().CategoryNav?.Name
              ?? ((TransactionCategory)g.Key).ToString(),
          Amount = g.Sum(t => Math.Abs(t.Amount)),
          Share = Math.Round(g.Sum(t => Math.Abs(t.Amount)) / total * 100m, 1),
        })
        .OrderByDescending(c => c.Amount)
        .Take(take)
        .ToList();
  }

  private static WrappedMerchantDto? TopMerchant(List<Transaction> expenses)
  {
    return expenses
        .Where(t => !string.IsNullOrWhiteSpace(t.MerchantName))
        .GroupBy(t => t.MerchantName)
        .Select(g => new WrappedMerchantDto
        {
          Name = g.Key,
          Visits = g.Count(),
          Amount = g.Sum(t => Math.Abs(t.Amount)),
        })
        .OrderByDescending(m => m.Visits)
        .ThenByDescending(m => m.Amount)
        .FirstOrDefault();
  }

  /// <summary>
  /// Days in [start, lastCountedDay] with zero budgetable expense — same
  /// definition as CountUpBudgetService.CalculateZeroSpendingDays.
  /// </summary>
  private static int ZeroSpendDays(List<Transaction> txns, DateTime start, DateTime lastCountedDay)
  {
    // One pass to collect the days that had budgetable spend, then a cheap day
    // walk — the old per-day rescan was O(days x N), which the January yearly
    // background run multiplied across every user.
    var spendDays = txns
        .WhereBudgetableExpense()
        .Select(t => t.Date.Date)
        .ToHashSet();
    var count = 0;
    for (var day = start.Date; day <= lastCountedDay; day = day.AddDays(1))
    {
      if (!spendDays.Contains(day)) count++;
    }
    return count;
  }

  private static WrappedBiggestDayDto? BiggestSpendDay(List<Transaction> expenses)
  {
    return expenses
        .GroupBy(t => t.Date.Date)
        .Select(g => new WrappedBiggestDayDto
        {
          Date = g.Key,
          Amount = g.Sum(t => Math.Abs(t.Amount)),
        })
        .OrderByDescending(d => d.Amount)
        .FirstOrDefault();
  }

  private async Task<decimal> SavedToGoalsAsync(
      int userId, int? familyId, DateTime start, DateTime endExclusive)
  {
    // Scoped the same way as the spend/income numbers (user OR family) so the
    // recap's savings beat matches its spending beats — a family recap that
    // shows the household's spending but only one member's savings reads as
    // "we saved nothing" when the other member funded a shared goal.
    // Period-end surplus allocations…
    var allocations = await _context.Set<SavingsAllocation>()
        .Where(a => (a.SavingsGoal.UserId == userId
                || (familyId != null && a.SavingsGoal.FamilyId == familyId))
            && a.AllocationDate >= start && a.AllocationDate < endExclusive)
        .SumAsync(a => (decimal?)a.AllocatedAmount) ?? 0m;
    // …plus deposits earmarked straight to a goal.
    var earmarked = await _context.Transactions
        .Where(t => (t.Account.UserId == userId
                || (familyId != null && t.Account.FamilyId == familyId))
            && t.SavingsGoalId != null
            && !t.IsPending && t.Date >= start && t.Date < endExclusive)
        .SumAsync(t => (decimal?)Math.Abs(t.Amount)) ?? 0m;
    return allocations + earmarked;
  }

  private async Task<List<WrappedBadgeDto>> BadgesInPeriodAsync(
      int userId, DateTime start, DateTime endExclusive)
  {
    return await _context.Set<UserAchievement>()
        .Where(a => a.UserId == userId && a.RevokedAt == null && a.EarnedAt >= start && a.EarnedAt < endExclusive)
        .OrderByDescending(a => a.Points)
        .Select(a => new WrappedBadgeDto
        {
          Key = a.AchievementKey,
          Title = a.Title,
          Icon = a.Icon,
          Points = a.Points,
        })
        .ToListAsync();
  }

  private async Task<int> LongestStreakAsync(int userId, bool useBest = false)
  {
    var streaks = await _context.Set<UserStreak>()
        .Where(s => s.UserId == userId)
        .Select(s => useBest ? s.BestCount : s.CurrentCount)
        .ToListAsync();
    return streaks.Count == 0 ? 0 : streaks.Max();
  }
}
