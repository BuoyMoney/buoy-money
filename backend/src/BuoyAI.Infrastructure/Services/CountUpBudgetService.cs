using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using static BuoyAI.Core.ValueObjects.TransactionAmountExtensions;

namespace BuoyAI.Infrastructure.Services;

public class CountUpBudgetService : ICountUpBudgetService
{
    // Achievement threshold constants
    private const int WeekWarriorDays = 7;
    private const int StreakMasterDays = 3;

    private readonly IBudgetRepository _budgetRepository;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IFamilyAuthorizationService _authService;
    private readonly ILogger<CountUpBudgetService> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CountUpBudgetService(
        IBudgetRepository budgetRepository,
        ITransactionRepository transactionRepository,
        IFamilyAuthorizationService authService,
        ILogger<CountUpBudgetService> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _budgetRepository = budgetRepository;
        _transactionRepository = transactionRepository;
        _authService = authService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    private DateTime GetLocalToday()
    {
        var header = _httpContextAccessor.HttpContext?.Request.Headers["X-Timezone-Offset"].FirstOrDefault();
        if (int.TryParse(header, out var offset) && offset >= -840 && offset <= 840)
            return DateTime.UtcNow.AddMinutes(offset).Date;
        return DateTime.UtcNow.Date;
    }

    public async Task<CountUpBudgetDto> CalculateCountUpStatusAsync(int budgetCategoryId)
    {
        try
        {
            _logger.LogInformation("Calculating count-up status for budget category {CategoryId}", budgetCategoryId);

            var category = await _budgetRepository.GetBudgetCategoryAsync(budgetCategoryId);
            if (category == null)
            {
                throw new ArgumentException($"Budget category with ID {budgetCategoryId} not found.");
            }

            var budget = await _budgetRepository.GetByIdAsync(category.BudgetId);
            if (budget == null)
            {
                throw new ArgumentException($"Budget with ID {category.BudgetId} not found.");
            }

            // Calculate days and time-based values
            var budgetPeriodStart = budget.CurrentPeriodStart;
            var budgetPeriodEnd = budget.CurrentPeriodEnd;
            var today = GetLocalToday();
            
            var totalDays = (budgetPeriodEnd - budgetPeriodStart).Days + 1;
            var daysElapsed = Math.Max(0, (today - budgetPeriodStart).Days + 1);
            daysElapsed = Math.Min(daysElapsed, totalDays); // Cap at total days

            // Get current spending using unified CategoryId for proper category tracking
            // For family budgets, include all family members' transactions
            // Scoped by the claim rule, so the card matches the Budgets screen and the month close: a
            // Family budget counts unclaimed charges, a member's budget only the ones claimed to it.
            var allTransactions = (await LoadBudgetTransactionsAsync(budget)).ToList();
            var familyFunded = await IsFamilyFundedAsync(budget);

            // The same transactions the spent figure counts, so the streaks and zero-spend days see
            // the charges the balance does (an allowance buckets by its owner's sub-category).
            var categoryTransactions = allTransactions.InCategory(budget, category, familyFunded).ToList();
            var actualSpent = allTransactions.SpentInCategory(budget, category, familyFunded);

            // Calculate daily allowance and earned amount
            var dailyAllowance = category.DailyAllowance > 0 
                ? category.DailyAllowance 
                : category.AllocatedAmount / totalDays;

            var earnedToDate = dailyAllowance * daysElapsed;
            var availableBalance = earnedToDate - actualSpent;
            
            // Calculate spending rate (average daily spending)
            var spendingRate = daysElapsed > 0 ? actualSpent / daysElapsed : 0;
            
            // Calculate projected savings
            var remainingDays = totalDays - daysElapsed;
            var projectedFutureSpending = remainingDays * spendingRate;
            var totalProjectedSpending = actualSpent + projectedFutureSpending;
            var projectedSavings = category.AllocatedAmount - totalProjectedSpending;

            // Calculate gamification metrics using the already-fetched transactions
            var consecutiveDaysUnderBudget = CalculateConsecutiveDaysUnderBudget(
                categoryTransactions, budgetPeriodStart, today, dailyAllowance);
            
            var zeroSpendingDays = CalculateZeroSpendingDays(
                categoryTransactions, budgetPeriodStart, today);

            // Generate achievements
            var achievements = GenerateAchievements(
                consecutiveDaysUnderBudget, 
                zeroSpendingDays, 
                availableBalance, 
                projectedSavings,
                daysElapsed,
                spendingRate,
                dailyAllowance);

            return new CountUpBudgetDto
            {
                CategoryId = category.Id,
                CategoryName = category.CategoryNav?.Name ?? "Unknown",
                AllocatedAmount = category.AllocatedAmount,
                ActualSpent = actualSpent,
                DailyAllowance = dailyAllowance,
                DaysElapsed = daysElapsed,
                TotalDays = totalDays,
                EarnedToDate = earnedToDate,
                AvailableBalance = availableBalance,
                SpendingRate = spendingRate,
                IsOverEarned = availableBalance < 0,
                ProjectedSavings = projectedSavings,
                ConsecutiveDaysUnderBudget = consecutiveDaysUnderBudget,
                ZeroSpendingDays = zeroSpendingDays,
                Achievements = achievements
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating count-up status for budget category {CategoryId}", budgetCategoryId);
            throw;
        }
    }

    public async Task<CountUpBudgetDto> UpdateDailyAllowanceAsync(int budgetCategoryId)
    {
        try
        {
            _logger.LogInformation("Updating daily allowance for budget category {CategoryId}", budgetCategoryId);

            var category = await _budgetRepository.GetBudgetCategoryAsync(budgetCategoryId);
            if (category == null)
            {
                throw new ArgumentException($"Budget category with ID {budgetCategoryId} not found.");
            }

            var budget = await _budgetRepository.GetByIdAsync(category.BudgetId);
            if (budget == null)
            {
                throw new ArgumentException($"Budget with ID {category.BudgetId} not found.");
            }

            // Recalculate daily allowance based on remaining budget and time
            var today = GetLocalToday();
            var remainingDays = Math.Max(1, (budget.CurrentPeriodEnd - today).Days + 1);
            
            // Current spending, counted as the Budgets screen counts it (family and claims included;
            // this used to read only the owner's own accounts)
            var allTransactions = (await LoadBudgetTransactionsAsync(budget)).ToList();
            var actualSpent = allTransactions.SpentInCategory(budget, category, await IsFamilyFundedAsync(budget));
            
            var remainingBudget = Math.Max(0, category.AllocatedAmount - actualSpent);
            var newDailyAllowance = remainingBudget / remainingDays;

            // Update the category
            category.DailyAllowance = newDailyAllowance;
            category.LastAllowanceCalculation = today;
            await _budgetRepository.UpdateBudgetCategoryAsync(category);

            return await CalculateCountUpStatusAsync(budgetCategoryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating daily allowance for budget category {CategoryId}", budgetCategoryId);
            throw;
        }
    }

    public async Task<IEnumerable<CountUpBudgetDto>> GetAllCountUpCategoriesAsync(int userId)
    {
        try
        {
            _logger.LogInformation("Getting all count-up budget categories for user {UserId}", userId);

            var budget = await _budgetRepository.GetActivePersonalBudgetAsync(userId);
            if (budget == null)
            {
                return new List<CountUpBudgetDto>();
            }

            var countUpCategories = budget.Categories.Where(c => c.IsCountUpEnabled);
            var results = new List<CountUpBudgetDto>();

            foreach (var category in countUpCategories)
            {
                var countUpStatus = await CalculateCountUpStatusAsync(category.Id);
                results.Add(countUpStatus);
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting count-up categories for user {UserId}", userId);
            throw;
        }
    }

    public async Task<IEnumerable<CountUpBudgetDto>> GetCountUpCategoriesForBudgetAsync(int budgetId, int userId)
    {
        try
        {
            _logger.LogInformation("Getting count-up budget categories for budget {BudgetId} and user {UserId}", budgetId, userId);

            var budget = await _budgetRepository.GetByIdAsync(budgetId);
            if (budget == null)
            {
                return new List<CountUpBudgetDto>();
            }

            // Verify user has access to this budget using centralized RBAC
            var authResult = await _authService.CanAccessBudgetAsync(userId, budgetId, ResourcePermission.Read);
            if (!authResult.IsAuthorized)
            {
                _logger.LogWarning("User {UserId} denied access to budget {BudgetId} count-up categories: {Reason}", 
                    userId, budgetId, authResult.Reason);
                return new List<CountUpBudgetDto>();
            }

            var countUpCategories = budget.Categories.Where(c => c.IsCountUpEnabled);
            var results = new List<CountUpBudgetDto>();

            foreach (var category in countUpCategories)
            {
                var countUpStatus = await CalculateCountUpStatusAsync(category.Id);
                results.Add(countUpStatus);
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting count-up categories for budget {BudgetId}", budgetId);
            throw;
        }
    }

    public async Task<CountUpBudgetDto> EnableCountUpBudgetingAsync(int categoryId, bool enable)
    {
        try
        {
            _logger.LogInformation("Setting count-up budgeting to {Enable} for category {CategoryId}", enable, categoryId);

            var category = await _budgetRepository.GetBudgetCategoryAsync(categoryId);
            if (category == null)
            {
                throw new ArgumentException($"Budget category with ID {categoryId} not found.");
            }

            category.IsCountUpEnabled = enable;

            // Don't freeze a copy of the allowance. With DailyAllowance at 0,
            // CalculateCountUpStatusAsync derives it from the current amount and period length on
            // every read, so a changed amount or a 28- vs 31-day period is always reflected. The
            // old code stored amount/days once on enable and never updated it (and, because it only
            // wrote when the value was <= 0, toggling count-up off and on didn't refresh it either).
            // A non-zero value now only comes from an explicit custom allowance (countup-settings).
            if (enable)
            {
                category.DailyAllowance = 0;
            }

            await _budgetRepository.UpdateBudgetCategoryAsync(category);

            return await CalculateCountUpStatusAsync(categoryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enabling count-up budgeting for category {CategoryId}", categoryId);
            throw;
        }
    }

    public async Task<CountUpBudgetDto> UpdateCountUpSettingsAsync(int categoryId, CountUpSettingsRequest settings)
    {
        try
        {
            _logger.LogInformation("Updating count-up settings for category {CategoryId}", categoryId);

            var category = await _budgetRepository.GetBudgetCategoryAsync(categoryId);
            if (category == null)
            {
                throw new ArgumentException($"Budget category with ID {categoryId} not found.");
            }

            if (settings.CustomDailyAllowance.HasValue)
            {
                category.DailyAllowance = settings.CustomDailyAllowance.Value;
                category.LastAllowanceCalculation = DateTime.UtcNow;
            }

            await _budgetRepository.UpdateBudgetCategoryAsync(category);

            return await CalculateCountUpStatusAsync(categoryId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating count-up settings for category {CategoryId}", categoryId);
            throw;
        }
    }

    private int CalculateConsecutiveDaysUnderBudget(
        IEnumerable<Core.Entities.Transaction> transactions,
        DateTime periodStart, 
        DateTime currentDate, 
        decimal dailyAllowance)
    {
        var consecutiveDays = 0;
        var checkDate = currentDate;

        while (checkDate >= periodStart)
        {
            var dayStart = checkDate.Date;
            var dayEnd = checkDate.Date.AddDays(1).AddTicks(-1);
            
            var dayTransactions = transactions.Where(t => t.Date >= dayStart && t.Date <= dayEnd);
            // Use extension method for consistent expense calculation
            var daySpending = dayTransactions.TotalBudgetableExpenses();
            
            if (daySpending <= dailyAllowance)
            {
                consecutiveDays++;
                checkDate = checkDate.AddDays(-1);
            }
            else
            {
                break;
            }
        }

        return consecutiveDays;
    }

    private int CalculateZeroSpendingDays(
        IEnumerable<Core.Entities.Transaction> transactions,
        DateTime periodStart, 
        DateTime currentDate)
    {
        var zeroSpendingDays = 0;
        var checkDate = periodStart;

        while (checkDate <= currentDate)
        {
            var dayStart = checkDate.Date;
            var dayEnd = checkDate.Date.AddDays(1).AddTicks(-1);
            
            var dayTransactions = transactions.Where(t => t.Date >= dayStart && t.Date <= dayEnd);
            // Use extension method for consistent expense calculation
            var daySpending = dayTransactions.TotalBudgetableExpenses();
            
            if (daySpending == 0)
            {
                zeroSpendingDays++;
            }
            
            checkDate = checkDate.AddDays(1);
        }

        return zeroSpendingDays;
    }

    private List<CountUpAchievement> GenerateAchievements(
        int consecutiveDaysUnderBudget,
        int zeroSpendingDays,
        decimal availableBalance,
        decimal projectedSavings,
        int daysElapsed,
        decimal spendingRate,
        decimal dailyAllowance)
    {
        var achievements = new List<CountUpAchievement>();

        // Consecutive days achievements — only award if not overdrawn
        if (consecutiveDaysUnderBudget >= WeekWarriorDays && availableBalance >= 0)
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "Week Warrior",
                Description = $"Stayed under budget for {consecutiveDaysUnderBudget} consecutive days",
                Icon = "🏆",
                EarnedDate = DateTime.UtcNow
            });
        }
        else if (consecutiveDaysUnderBudget >= StreakMasterDays && availableBalance >= 0)
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "Streak Master",
                Description = $"Stayed under budget for {consecutiveDaysUnderBudget} consecutive days",
                Icon = "🔥",
                EarnedDate = DateTime.UtcNow
            });
        }

        // Zero spending achievements — only award if not overdrawn
        if (zeroSpendingDays >= 5 && availableBalance >= 0)
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "No-Spend Champion",
                Description = $"Had {zeroSpendingDays} days with zero spending",
                Icon = "💎",
                EarnedDate = DateTime.UtcNow
            });
        }
        else if (zeroSpendingDays >= 2 && availableBalance >= 0)
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "Saver",
                Description = $"Had {zeroSpendingDays} days with zero spending",
                Icon = "💰",
                EarnedDate = DateTime.UtcNow
            });
        }

        // Balance achievements
        if (availableBalance > 0 && projectedSavings > 0)
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "Budget Master",
                Description = "Staying on track with positive balance and projected savings",
                Icon = "⭐",
                EarnedDate = DateTime.UtcNow
            });
        }

        // Early period low spending
        if (daysElapsed >= 7 && spendingRate < (dailyAllowance * 0.5m))
        {
            achievements.Add(new CountUpAchievement
            {
                Title = "Frugal Start",
                Description = "Spending less than half your daily allowance on average",
                Icon = "🌱",
                EarnedDate = DateTime.UtcNow
            });
        }

        return achievements;
    }

    /// <summary>
    /// The budget's transactions for the current period: across the family for a budget in one,
    /// then the claim rule and the owner's pending setting (BudgetSpending.ScopeToBudget), the same
    /// as the Budgets screen and the month close. GetByIdAsync loads the owner.
    /// </summary>
    private async Task<IEnumerable<BuoyAI.Core.Entities.Transaction>> LoadBudgetTransactionsAsync(BuoyAI.Core.Entities.Budget budget)
    {
        var loaded = budget.FamilyId.HasValue
            ? await _transactionRepository.GetByUserOrFamilyAsync(
                budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)
            : await _transactionRepository.GetByUserIdAsync(
                budget.UserId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive);
        return loaded.ScopeToBudget(budget, budget.User?.ExcludePendingFromBudget == true);
    }

    /// <summary>An allowance, or a personal budget funded by a family allowance line.</summary>
    private async Task<bool> IsFamilyFundedAsync(BuoyAI.Core.Entities.Budget budget) =>
        budget.Type == BudgetType.Allowance
        || (budget.Type == BudgetType.Personal && budget.ParentBudgetId != null
            && await _budgetRepository.GetLinkedCategoryForChildAsync(budget.Id) != null);
}