using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Utilities;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BuoyAI.Core.ValueObjects.TransactionAmountExtensions;

using System.Globalization;

namespace BuoyAI.Infrastructure.Services;

public class BudgetPeriodService : IBudgetPeriodService
{
  private readonly ILogger<BudgetPeriodService> _logger;
  private readonly IBudgetRepository _budgetRepository;
  private readonly ITransactionRepository _transactionRepository;
  private readonly Lazy<ISavingsService> _savingsService;
  private readonly BuoyAIDbContext _dbContext;

  public BudgetPeriodService(
    ILogger<BudgetPeriodService> logger,
    IBudgetRepository budgetRepository,
    ITransactionRepository transactionRepository,
    Lazy<ISavingsService> savingsService,
    BuoyAIDbContext dbContext)
  {
    _logger = logger;
    _budgetRepository = budgetRepository;
    _transactionRepository = transactionRepository;
    _savingsService = savingsService;
    _dbContext = dbContext;
  }

  public (DateTime start, DateTime end) CalculateCurrentPeriod(BudgetPeriod period, DateTime? referenceDate = null, int? utcOffsetMinutes = null, DayOfWeek? weekStart = null)
  {
    var utcNow = referenceDate ?? DateTime.UtcNow;
    // Apply timezone offset so period boundaries anchor to the user's local midnight, not UTC midnight
    var localDate = utcOffsetMinutes.HasValue
      ? utcNow.AddMinutes(utcOffsetMinutes.Value).Date
      : utcNow.Date;
    var today = DateTime.SpecifyKind(localDate, DateTimeKind.Utc);

    return period switch
    {
      BudgetPeriod.Weekly => CalculateWeeklyPeriod(today, weekStart),
      BudgetPeriod.BiWeekly => CalculateBiWeeklyPeriod(today, weekStart),
      BudgetPeriod.Monthly => CalculateMonthlyPeriod(today),
      BudgetPeriod.Quarterly => CalculateQuarterlyPeriod(today),
      BudgetPeriod.Yearly => CalculateYearlyPeriod(today),
      _ => throw new ArgumentException($"Unsupported budget period: {period}")
    };
  }

  // No utcOffsetMinutes here intentionally: currentPeriodEnd is already a calendar date (no time component),
  // so advancing by days/months is timezone-agnostic. ShouldResetBudget applies the offset upstream.
  public (DateTime start, DateTime end) CalculateNextPeriod(BudgetPeriod period, DateTime currentPeriodEnd)
  {
    var nextPeriodStart = DateTime.SpecifyKind(currentPeriodEnd.AddDays(1), DateTimeKind.Utc);
    
    return period switch
    {
      BudgetPeriod.Weekly => (nextPeriodStart, DateTime.SpecifyKind(nextPeriodStart.AddDays(6), DateTimeKind.Utc)),
      BudgetPeriod.BiWeekly => (nextPeriodStart, DateTime.SpecifyKind(nextPeriodStart.AddDays(13), DateTimeKind.Utc)),
      BudgetPeriod.Monthly => (nextPeriodStart, DateTime.SpecifyKind(nextPeriodStart.AddMonths(1).AddDays(-1), DateTimeKind.Utc)),
      BudgetPeriod.Quarterly => (nextPeriodStart, DateTime.SpecifyKind(nextPeriodStart.AddMonths(3).AddDays(-1), DateTimeKind.Utc)),
      BudgetPeriod.Yearly => (nextPeriodStart, DateTime.SpecifyKind(nextPeriodStart.AddYears(1).AddDays(-1), DateTimeKind.Utc)),
      _ => throw new ArgumentException($"Unsupported budget period: {period}")
    };
  }

  public int GetDaysInPeriod(BudgetPeriod period, DateTime periodStart, DateTime periodEnd)
  {
    return (periodEnd - periodStart).Days + 1;
  }

  public bool ShouldResetBudget(Budget budget, DateTime currentDate, int? utcOffsetMinutes = null)
  {
    if (!budget.AutoReset) return false;
    // Compare local dates so reset fires at local midnight, not UTC midnight
    var localDate = utcOffsetMinutes.HasValue
      ? currentDate.AddMinutes(utcOffsetMinutes.Value).Date
      : currentDate.Date;
    return localDate > budget.CurrentPeriodEnd.Date;
  }

  public async Task<BudgetPeriodSummaryDto> ResetBudgetAsync(Budget budget)
  {
    try
    {
      // What the period counts, loaded once and shared by the summary and every category's rollover
      var spending = await LoadPeriodSpendingAsync(budget);

      // Calculate summary for the completed period
      var summary = await GeneratePeriodSummaryAsync(budget, spending);

      // Create a snapshot of the completed period BEFORE processing rollovers. The snapshot's unique
      // (BudgetId, PeriodStart) index is our idempotency key: it is created before any savings write,
      // so its pre-existence means a prior/concurrent reset for THIS period already applied the
      // (non-idempotent) savings allocations and rollovers. Re-applying them double-incremented
      // savings balances (two 'budget_surplus' rows per goal) under parallel dashboard/budget loads
      // — the six GET paths all call this — and on any post-snapshot partial failure that re-triggered
      // the reset. Gate those side effects on first-time creation.
      var (snapshot, alreadyExisted) = await CreatePeriodSnapshotAsync(budget, summary);

      if (!alreadyExisted)
      {
        // Process automatic allocations to savings goals BEFORE processing category rollovers
        // This ensures leftover budget amounts are transferred to savings goals
        _logger.LogInformation("Processing automatic savings allocations for budget {BudgetId} at period end", budget.Id);
        await _savingsService.Value.ProcessAutomaticAllocationsAsync(budget.Id);

        // Process rollover for each category (now supports negative rollover). This also runs the
        // category->savings surplus transfers, so it is gated on first-time reset too.
        foreach (var category in budget.Categories)
        {
          await ProcessCategoryRolloverAsync(category, budget, snapshot, spending);
        }

        // Update snapshot totals after rollover processing
        snapshot.TotalRolloverOut = snapshot.CategorySnapshots.Sum(c => c.RolloverOut);
        await _dbContext.SaveChangesAsync();
      }
      else
      {
        _logger.LogWarning(
          "Budget {BudgetId} reset for period starting {PeriodStart} already processed; skipping duplicate savings allocation and category rollover.",
          budget.Id, budget.CurrentPeriodStart);
      }

      // Advance budget to next period. Idempotent: CalculateNextPeriod is a pure function of the
      // (unchanged) CurrentPeriodEnd, so a duplicate reset lands on the same window rather than
      // skipping a period.
      var (nextStart, nextEnd) = CalculateNextPeriod(budget.Period, budget.CurrentPeriodEnd);
      budget.CurrentPeriodStart = nextStart;
      budget.CurrentPeriodEnd = nextEnd;
      budget.LastReset = DateTime.UtcNow;
      
      await _budgetRepository.UpdateAsync(budget);
      
      _logger.LogInformation("Budget {BudgetId} reset for period {Start} to {End}. Snapshot {SnapshotId} created.", 
        budget.Id, nextStart, nextEnd, snapshot.Id);
      
      return summary;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error resetting budget {BudgetId}", budget.Id);
      throw;
    }
  }
  
  /// <summary>
  /// Creates a snapshot of the budget period for historical tracking.
  /// Uses upsert pattern to handle concurrent calls (race condition protection).
  /// Returns the snapshot and whether it ALREADY EXISTED — the caller gates the (non-idempotent)
  /// savings allocations and category rollovers on first-time creation so a concurrent/duplicate
  /// reset for the same (BudgetId, PeriodStart) can't apply them twice.
  /// </summary>
  private async Task<(BudgetPeriodSnapshot snapshot, bool alreadyExisted)> CreatePeriodSnapshotAsync(Budget budget, BudgetPeriodSummaryDto summary)
  {
    // Check if snapshot already exists for this period (handles race conditions from parallel tool calls)
    var existingSnapshot = await _dbContext.BudgetPeriodSnapshots
      .Include(s => s.CategorySnapshots)
      .FirstOrDefaultAsync(s => s.BudgetId == budget.Id && s.PeriodStart == budget.CurrentPeriodStart);

    if (existingSnapshot != null)
    {
      _logger.LogInformation("Budget period snapshot already exists for BudgetId={BudgetId}, PeriodStart={PeriodStart}. Returning existing snapshot.",
        budget.Id, budget.CurrentPeriodStart);
      return (existingSnapshot, true);
    }
    
    var snapshot = new BudgetPeriodSnapshot
    {
      BudgetId = budget.Id,
      PeriodStart = budget.CurrentPeriodStart,
      PeriodEnd = budget.CurrentPeriodEnd,
      TotalAllocated = budget.Categories.Sum(c => c.AllocatedAmount),
      TotalSpent = summary.CategorySummaries.Sum(c => c.Spent),
      TotalIncome = summary.CategorySummaries.Where(c => c.IsIncome).Sum(c => c.Spent),
      TotalRolloverIn = budget.Categories.Sum(c => c.RolloverBalance),
      TotalRolloverOut = 0, // Will be calculated after rollover processing
      CreatedAt = DateTime.UtcNow
    };
    
    _dbContext.BudgetPeriodSnapshots.Add(snapshot);
    
    try
    {
      await _dbContext.SaveChangesAsync(); // Save to get the ID
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505")
    {
      // Another concurrent request created the snapshot - fetch and return it (flag as pre-existing
      // so the caller skips the savings allocation / rollover that the winning reset already applied).
      _logger.LogWarning("Concurrent snapshot creation detected for BudgetId={BudgetId}. Fetching existing snapshot.", budget.Id);
      _dbContext.Entry(snapshot).State = EntityState.Detached;

      var winning = await _dbContext.BudgetPeriodSnapshots
        .Include(s => s.CategorySnapshots)
        .FirstAsync(s => s.BudgetId == budget.Id && s.PeriodStart == budget.CurrentPeriodStart);
      return (winning, true);
    }
    
    // Create category snapshots
    foreach (var category in budget.Categories)
    {
      var categoryDisplayName = category.CategoryNav?.Name ?? "Unknown";
      var categorySummary = summary.CategorySummaries.FirstOrDefault(
        s => s.CategoryName == categoryDisplayName);
      
      var categorySnapshot = new BudgetPeriodCategorySnapshot
      {
        BudgetPeriodSnapshotId = snapshot.Id,
        BudgetCategoryId = category.Id,
        // Use unified CategoryId directly
        CategoryId = category.CategoryId ?? 0,
        CategoryName = categoryDisplayName,
        AllocatedAmount = category.AllocatedAmount,
        SpentAmount = categorySummary?.Spent ?? 0,
        RolloverIn = category.RolloverBalance, // This is the rollover FROM previous period
        RolloverOut = 0, // Will be set during ProcessCategoryRolloverAsync
        AllowRollover = category.AllowRollover,
        IsIncome = category.IsIncome
      };
      
      snapshot.CategorySnapshots.Add(categorySnapshot);
      _dbContext.BudgetPeriodCategorySnapshots.Add(categorySnapshot);
    }

    await _dbContext.SaveChangesAsync();
    return (snapshot, false);
  }


  /// <summary>User-facing money copy in the user's primary currency (#941). A bare
  /// ":C" renders "¤" in the locale-less container; en-US pinned it to "$".</summary>
  private static string FormatMoney(decimal amount, string? currencyCode) =>
      CurrencyCodes.Format(amount, currencyCode);

  public string GenerateEncouragementMessage(BudgetPeriodSummaryDto summary, string? currencyCode = null)
  {
    var totalSavings = summary.TotalSavings;
    // "Under budget in N of M categories" is a claim about SPENDING. Income categories always
    // look under budget (nothing is spent from them), so leaving them in inflated both counts
    // and could report a champion month on a budget that overspent everywhere it mattered.
    var spendingCategories = summary.CategorySummaries.Where(c => !c.IsIncome).ToList();
    var categoriesUnderBudget = spendingCategories.Count(c => c.WasUnderBudget);
    var totalCategories = spendingCategories.Count;

    // These summaries describe a period that has ENDED (the caller reads the most recent
    // completed snapshot), so "this period" pointed at the month the user was looking at
    // while quoting numbers from the one before it. Name the month instead of guessing.
    var period = summary.PeriodStart == default
      ? "last period"
      : summary.PeriodStart.ToString("MMMM", CultureInfo.InvariantCulture);

    // Overspending is a real outcome and must not be dressed up. The old copy read the same
    // for "broke even" and "went $2,000 over", which is the one thing a budgeting app cannot
    // afford to blur.
    if (totalSavings < 0)
    {
      return $"📊 In {period} you spent {FormatMoney(-totalSavings, currencyCode)} more than you budgeted. "
           + "Worth a look at which categories ran over — small adjustments compound. 💪";
    }

    if (totalSavings == 0)
    {
      return $"🎯 In {period} you landed exactly on budget. Every dollar has a purpose! 💪";
    }

    // totalCategories > 0 guard: an income-only budget has no spending categories, and
    // "under budget in ALL categories" is not a claim you can make about none of them.
    if (totalCategories > 0 && categoriesUnderBudget == totalCategories)
    {
      return $"🎉 BUDGET CHAMPION! In {period} you stayed under budget in ALL categories and kept {FormatMoney(totalSavings, currencyCode)}! You're absolutely crushing your financial goals! 🏆✨";
    }
    
    if (totalCategories > 0 && categoriesUnderBudget >= totalCategories * 0.75)
    {
      return $"🌟 Amazing work! In {period} you stayed under budget in {categoriesUnderBudget} of {totalCategories} categories, keeping {FormatMoney(totalSavings, currencyCode)}! That's some serious financial discipline! 💫";
    }
    
    if (totalSavings >= 50)
    {
      return $"💰 Nice! You came in {FormatMoney(totalSavings, currencyCode)} under budget in {period} - that's money that can work for your future goals! Keep building that momentum! 🚀";
    }
    
    return $"💸 You came in {FormatMoney(totalSavings, currencyCode)} under budget in {period}! Every dollar saved is a step toward financial freedom. Small wins add up to big victories! 🌱";
  }

  // weekStart semantics (#941): null = legacy (Weekly starts Sunday, BiWeekly anchors
  // Monday — historically inconsistent, preserved so existing budgets never shift).
  // A set value anchors BOTH period types to that day; US/CA locale convention is
  // Sunday, UK/EU/ANZ is Monday (ISO 8601), payday-anchored users pick their own.
  private (DateTime start, DateTime end) CalculateWeeklyPeriod(DateTime date, DayOfWeek? weekStart)
  {
    var anchor = weekStart ?? DayOfWeek.Sunday;
    var daysBack = (((int)date.DayOfWeek - (int)anchor) + 7) % 7;
    var startOfWeek = date.AddDays(-daysBack);
    var endOfWeek = startOfWeek.AddDays(6);
    return (DateTime.SpecifyKind(startOfWeek, DateTimeKind.Utc), DateTime.SpecifyKind(endOfWeek, DateTimeKind.Utc));
  }

  private (DateTime start, DateTime end) CalculateBiWeeklyPeriod(DateTime date, DayOfWeek? weekStart)
  {
    var anchor = weekStart ?? DayOfWeek.Monday; // legacy anchor
    var daysBack = (((int)date.DayOfWeek - (int)anchor) + 7) % 7;
    var mostRecentAnchor = date.AddDays(-daysBack);

    // Fixed epoch keeps periods consistent across years: Jan 1 2024 was a Monday;
    // shift it forward to the first occurrence of the anchor day so any anchor
    // gets a deterministic 14-day grid.
    var epoch = DateTime.SpecifyKind(new DateTime(2024, 1, 1), DateTimeKind.Utc)
      .AddDays((((int)anchor - (int)DayOfWeek.Monday) + 7) % 7);
    var daysSinceEpoch = (mostRecentAnchor - epoch).Days;
    var biWeeklyPeriodIndex = daysSinceEpoch / 14;

    var periodStart = DateTime.SpecifyKind(epoch.AddDays(biWeeklyPeriodIndex * 14), DateTimeKind.Utc);
    var periodEnd = DateTime.SpecifyKind(periodStart.AddDays(13), DateTimeKind.Utc);

    return (periodStart, periodEnd);
  }

  private (DateTime start, DateTime end) CalculateMonthlyPeriod(DateTime date)
  {
    var startOfMonth = DateTime.SpecifyKind(new DateTime(date.Year, date.Month, 1), DateTimeKind.Utc);
    var endOfMonth = startOfMonth.AddMonths(1).AddDays(-1);
    return (startOfMonth, endOfMonth);
  }

  private (DateTime start, DateTime end) CalculateQuarterlyPeriod(DateTime date)
  {
    var quarter = (date.Month - 1) / 3;
    var startOfQuarter = DateTime.SpecifyKind(new DateTime(date.Year, quarter * 3 + 1, 1), DateTimeKind.Utc);
    var endOfQuarter = startOfQuarter.AddMonths(3).AddDays(-1);
    return (startOfQuarter, endOfQuarter);
  }

  private (DateTime start, DateTime end) CalculateYearlyPeriod(DateTime date)
  {
    var startOfYear = DateTime.SpecifyKind(new DateTime(date.Year, 1, 1), DateTimeKind.Utc);
    var endOfYear = DateTime.SpecifyKind(new DateTime(date.Year, 12, 31), DateTimeKind.Utc);
    return (startOfYear, endOfYear);
  }

  /// <summary>
  /// The period's transactions as the Budgets screen counts them, so the close (summary and
  /// rollover) lands on the numbers the family saw all month: loaded across the family for a budget
  /// in one, then the claim rule and the owner's pending setting (BudgetSpending.ScopeToBudget). A
  /// Family budget counts unclaimed charges; a member's or child's budget only the ones claimed to
  /// its owner, so another member's spending never lands on it and no charge counts twice.
  /// </summary>
  private async Task<List<Transaction>> GetPeriodTransactionsAsync(Budget budget)
  {
    var loaded = budget.FamilyId.HasValue
      ? await _transactionRepository.GetByUserOrFamilyAsync(
        budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)
      : await _transactionRepository.GetByUserIdAsync(
        budget.UserId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive);
    var excludePending = await _dbContext.Users
      .Where(u => u.Id == budget.UserId)
      .Select(u => u.ExcludePendingFromBudget)
      .FirstOrDefaultAsync();
    return loaded.ScopeToBudget(budget, excludePending).ToList();
  }

  /// <summary>
  /// An allowance, or a personal budget funded by a family allowance line: spending is bucketed by
  /// the owner's own sub-categories, as on the Budgets screen.
  /// </summary>
  private async Task<bool> IsFamilyFundedAsync(Budget budget) =>
    budget.Type == BudgetType.Allowance
    || (budget.Type == BudgetType.Personal && budget.ParentBudgetId != null
        && await _dbContext.BudgetCategories.AnyAsync(c => c.LinkedBudgetId == budget.Id && c.IsAllowance));

  /// <summary>
  /// What a close counts: the period's scoped transactions and whether the budget is family-funded.
  /// Loaded once per reset and shared by the summary and every category's rollover.
  /// </summary>
  private sealed record PeriodSpending(List<Transaction> Transactions, bool FamilyFunded);

  private async Task<PeriodSpending> LoadPeriodSpendingAsync(Budget budget) =>
    new(await GetPeriodTransactionsAsync(budget), await IsFamilyFundedAsync(budget));

  private async Task<BudgetPeriodSummaryDto> GeneratePeriodSummaryAsync(Budget budget, PeriodSpending spending)
  {
    var summary = new BudgetPeriodSummaryDto
    {
      PeriodStart = budget.CurrentPeriodStart,
      PeriodEnd = budget.CurrentPeriodEnd
    };

    foreach (var category in budget.Categories)
    {
      var categorySpent = spending.Transactions.SpentInCategory(budget, category, spending.FamilyFunded);

      var saved = Math.Max(0, category.AllocatedAmount + category.RolloverBalance - categorySpent);
      var rollover = category.AllowRollover ? saved : 0;
      var categoryDisplayName = category.CategoryNav?.Name ?? "Unknown";

      summary.CategorySummaries.Add(new BudgetCategorySummaryDto
      {
        CategoryName = categoryDisplayName,
        Allocated = category.AllocatedAmount,
        Spent = categorySpent,
        Saved = saved,
        RolledOver = rollover,
        WasUnderBudget = categorySpent <= category.AllocatedAmount + category.RolloverBalance,
        IsIncome = category.IsIncome
      });

      // Income is excluded: a salary line allocated $8,535 and "spent" $0 is income arriving as
      // planned, not $8,535 saved. Counting it here inflated the period summary by the whole
      // paycheque. Rollover categories are excluded too — their surplus carries forward as
      // rollover rather than being swept to savings.
      summary.TotalSavings += (category.IsIncome || category.AllowRollover) ? 0 : saved;
      summary.TotalRollover += rollover;
    }

    var ownerCurrency = await _dbContext.Users
      .Where(u => u.Id == budget.UserId)
      .Select(u => u.Currency)
      .FirstOrDefaultAsync();
    summary.EncouragementMessage = GenerateEncouragementMessage(summary, ownerCurrency);
    return summary;
  }

  private async Task ProcessCategoryRolloverAsync(
    BudgetCategory category, Budget budget, BudgetPeriodSnapshot snapshot, PeriodSpending spending)
  {
    // Actual spending for this category in the period, counted as the Budgets screen counts it
    var categorySpent = spending.Transactions.SpentInCategory(budget, category, spending.FamilyFunded);

    // Calculate remaining: allocated + any previous rollover - spent
    var remainingAmount = category.AllocatedAmount + category.RolloverBalance - categorySpent;

    // Find the category snapshot to record the rollover out
    var categorySnapshot = snapshot.CategorySnapshots.FirstOrDefault(s => s.BudgetCategoryId == category.Id);
    var categoryName = category.CategoryNav?.Name ?? "Unknown";
    
    // ===========================================================
    // ROLLOVER LOGIC - handles multiple modes (#177, #178)
    // ===========================================================
    
    // First, check if this is a Count-Up category with specific rollover mode
    if (category.IsCountUpEnabled && category.CountUpRolloverMode.HasValue)
    {
      await ProcessCountUpRolloverAsync(category, budget, remainingAmount, categorySnapshot, categoryName);
    }
    // Next, check if budget-level rollover mode says to auto-transfer to savings
    else if (budget.RolloverMode == ChildBudgetRolloverMode.AllocateToSavings && remainingAmount > 0)
    {
      await ProcessBudgetLevelSavingsTransferAsync(category, budget, remainingAmount, categorySnapshot, categoryName);
    }
    // Standard rollover logic
    else if (category.AllowRollover)
    {
      await ProcessStandardRolloverAsync(category, remainingAmount, categorySnapshot, categoryName);
    }
    else
    {
      // No rollover allowed - reset to 0
      category.RolloverBalance = 0;
      
      if (categorySnapshot != null)
      {
        categorySnapshot.RolloverOut = 0;
      }
    }

    // Note: SpentAmount is no longer stored on the entity - it's calculated from transactions
    // No reset needed as spending is always calculated fresh from transactions within the period
  }
  
  /// <summary>
  /// Handles Count-Up specific rollover modes (TransferToSavings, ResetToZero, RolloverToSelf)
  /// </summary>
  private async Task ProcessCountUpRolloverAsync(
    BudgetCategory category, 
    Budget budget, 
    decimal remainingAmount, 
    BudgetPeriodCategorySnapshot? categorySnapshot,
    string categoryName)
  {
    switch (category.CountUpRolloverMode)
    {
      case CountUpRolloverMode.TransferToSavings:
        if (remainingAmount > 0)
        {
          // Use category-level savings goal, or fall back to budget-level
          var targetGoalId = category.SavingsGoalId ?? budget.DefaultSavingsGoalId;
          
          if (targetGoalId.HasValue)
          {
            var transferred = await TransferSurplusToSavingsAsync(category, targetGoalId.Value, remainingAmount, categoryName, "Count-Up surplus");
            if (transferred)
            {
              category.RolloverBalance = 0;

              _logger.LogInformation(
                "Count-Up category {CategoryId} ({CategoryName}): Transferred {Amount} surplus to savings goal {GoalId}",
                category.Id, categoryName, remainingAmount, targetGoalId.Value);
            }
            // If the transfer failed, TransferSurplusToSavingsAsync already carried the surplus into
            // RolloverBalance as a fallback — do NOT zero it here, or the surplus is silently lost.
          }
          else
          {
            // No savings goal configured - fall back to rollover
            _logger.LogWarning(
              "Count-Up category {CategoryId} has TransferToSavings mode but no savings goal configured. Falling back to rollover.",
              category.Id);
            category.RolloverBalance = remainingAmount;
          }
        }
        else
        {
          // Negative or zero - carry forward as debt
          category.RolloverBalance = remainingAmount;
        }
        break;
        
      case CountUpRolloverMode.ResetToZero:
        category.RolloverBalance = 0;
        
        if (remainingAmount > 0)
        {
          _logger.LogInformation(
            "Count-Up category {CategoryId} ({CategoryName}): Reset to zero. Surplus of {Amount} was lost.",
            category.Id, categoryName, remainingAmount);
        }
        else if (remainingAmount < 0)
        {
          _logger.LogInformation(
            "Count-Up category {CategoryId} ({CategoryName}): Reset to zero. Deficit of {Amount} was forgiven.",
            category.Id, categoryName, Math.Abs(remainingAmount));
        }
        break;
        
      case CountUpRolloverMode.RolloverToSelf:
      default:
        // Standard rollover for Count-Up categories
        category.RolloverBalance = remainingAmount;
        
        if (remainingAmount < 0)
        {
          _logger.LogInformation(
            "Count-Up category {CategoryId} ({CategoryName}) has negative rollover of {Amount}. User is borrowing from future months.",
            category.Id, categoryName, Math.Abs(remainingAmount));
        }
        break;
    }
    
    if (categorySnapshot != null)
    {
      categorySnapshot.RolloverOut = category.RolloverBalance;
    }
  }
  
  /// <summary>
  /// Handles budget-level AllocateToSavings rollover mode
  /// </summary>
  private async Task ProcessBudgetLevelSavingsTransferAsync(
    BudgetCategory category, 
    Budget budget, 
    decimal remainingAmount, 
    BudgetPeriodCategorySnapshot? categorySnapshot,
    string categoryName)
  {
    // Use category-level savings goal, or fall back to budget-level
    var targetGoalId = category.SavingsGoalId ?? budget.DefaultSavingsGoalId;
    
    if (targetGoalId.HasValue)
    {
      var transferred = await TransferSurplusToSavingsAsync(category, targetGoalId.Value, remainingAmount, categoryName, "End-of-period surplus");
      if (transferred)
      {
        category.RolloverBalance = 0;

        _logger.LogInformation(
          "Category {CategoryId} ({CategoryName}): Transferred {Amount} to savings goal {GoalId} (Budget RolloverMode=AllocateToSavings)",
          category.Id, categoryName, remainingAmount, targetGoalId.Value);
      }
      // If the transfer failed, TransferSurplusToSavingsAsync already carried the surplus into
      // RolloverBalance as a fallback — do NOT zero it here, or the surplus is silently lost.
    }
    else
    {
      // No savings goal configured - fall back to standard rollover if allowed
      _logger.LogWarning(
        "Budget {BudgetId} has RolloverMode=AllocateToSavings but no DefaultSavingsGoalId configured. Falling back to standard rollover.",
        budget.Id);
      
      if (category.AllowRollover)
      {
        category.RolloverBalance = remainingAmount;
      }
      else
      {
        category.RolloverBalance = 0;
      }
    }
    
    if (categorySnapshot != null)
    {
      categorySnapshot.RolloverOut = category.RolloverBalance;
    }
  }
  
  /// <summary>
  /// Handles standard rollover with max multiplier and overflow to savings
  /// </summary>
  private async Task ProcessStandardRolloverAsync(
    BudgetCategory category, 
    decimal remainingAmount, 
    BudgetPeriodCategorySnapshot? categorySnapshot,
    string categoryName)
  {
    // Check if max rollover is configured
    if (category.MaxRolloverMultiplier.HasValue && remainingAmount > 0)
    {
      var maxRollover = category.AllocatedAmount * category.MaxRolloverMultiplier.Value;
      
      if (remainingAmount > maxRollover)
      {
        var overflow = remainingAmount - maxRollover;

        // Transfer overflow to savings goal if configured
        var overflowTransferred = false;
        if (category.RolloverOverflowSavingsGoalId.HasValue)
        {
          overflowTransferred = await TransferSurplusToSavingsAsync(category, category.RolloverOverflowSavingsGoalId.Value, overflow, categoryName, "Rollover overflow");

          if (overflowTransferred)
            _logger.LogInformation(
              "Transferred rollover overflow of {Amount} from category {CategoryId} to savings goal {GoalId}",
              overflow, category.Id, category.RolloverOverflowSavingsGoalId.Value);
        }
        else
        {
          _logger.LogWarning(
            "Category {CategoryId} has max rollover {MaxRollover:C} but no overflow savings goal configured. Overflow of {Overflow:C} lost.",
            category.Id, maxRollover, overflow);
        }

        // Cap rollover at the maximum when the overflow was deposited (or intentionally dropped because
        // no overflow goal is configured). But if a transfer was attempted and FAILED, keep the full
        // remaining amount in rollover so the overflow isn't silently lost.
        category.RolloverBalance = (category.RolloverOverflowSavingsGoalId.HasValue && !overflowTransferred)
          ? remainingAmount
          : maxRollover;

        _logger.LogInformation(
          "Category {CategoryId} rollover capped at {MaxRollover:C} ({Multiplier:P0} of allocated). Overflow: {Overflow:C}",
          category.Id, maxRollover, category.MaxRolloverMultiplier.Value, overflow);
      }
      else
      {
        // Under the cap, use full remaining amount
        category.RolloverBalance = remainingAmount;
      }
    }
    else
    {
      // Support NEGATIVE rollover - borrowing from future months!
      // Positive = surplus carried forward
      // Negative = deficit carried forward (debt to future self)
      category.RolloverBalance = remainingAmount;
    }
    
    if (categorySnapshot != null)
    {
      categorySnapshot.RolloverOut = category.RolloverBalance;
    }
    
    if (remainingAmount < 0)
    {
      _logger.LogInformation(
        "Category {CategoryId} ({CategoryName}) has negative rollover of {Amount}. User is borrowing from future months.",
        category.Id, categoryName, Math.Abs(remainingAmount));
    }
  }
  
  /// <summary>
  /// Transfers surplus amount to a savings goal (internal helper for system-initiated transfers).
  /// Returns <c>true</c> if the deposit succeeded, <c>false</c> if it failed (e.g. the goal was deleted,
  /// or concurrency retries were exhausted). On failure it leaves the surplus in the category's
  /// RolloverBalance as a fallback; callers MUST honor the return value and NOT overwrite RolloverBalance
  /// to 0 when this returns false, or the surplus is silently lost (was neither saved nor carried).
  /// </summary>
  private async Task<bool> TransferSurplusToSavingsAsync(
    BudgetCategory category,
    int savingsGoalId,
    decimal amount,
    string categoryName,
    string reason)
  {
    try
    {
      await _savingsService.Value.AllocateToGoalAsync(
        savingsGoalId,
        new Core.DTOs.Savings.ManualAllocationRequest
        {
          SavingsGoalId = savingsGoalId,
          Amount = amount,
          Source = $"{reason} from {categoryName}"
        });
      return true;
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex,
        "Failed to transfer {Amount} to savings goal {GoalId} from category {CategoryId}. Amount will be rolled over instead.",
        amount, savingsGoalId, category.Id);

      // Fall back to rolling over if transfer fails
      category.RolloverBalance = amount;
      return false;
    }
  }
}
