using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.ValueObjects;
using BuoyAI.Infrastructure.Data;
using static BuoyAI.Core.ValueObjects.TransactionAmountExtensions;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Service for calculating actual spending from transactions.
/// 
/// SpentAmount is derived data - always calculated from transactions,
/// never stored in the database. This ensures data accuracy and eliminates
/// stale/incorrect spending values.
/// </summary>
public class BudgetSpendingService : IBudgetSpendingService
{
    private readonly BuoyAIDbContext _context;
    private readonly ITransactionRepository _transactionRepository;
    private readonly IUserRepository _userRepository;
    private readonly ILogger<BudgetSpendingService> _logger;

    public BudgetSpendingService(
        BuoyAIDbContext context,
        ITransactionRepository transactionRepository,
        IUserRepository userRepository,
        ILogger<BudgetSpendingService> logger)
    {
        _context = context;
        _transactionRepository = transactionRepository;
        _userRepository = userRepository;
        _logger = logger;
    }

    public async Task<Dictionary<int, decimal>> GetCategorySpendingAsync(int budgetId)
    {
        var budget = await _context.Budgets
            .Include(b => b.Categories)
            .FirstOrDefaultAsync(b => b.Id == budgetId);

        if (budget == null)
        {
            _logger.LogWarning("Budget {BudgetId} not found for spending calculation", budgetId);
            return new Dictionary<int, decimal>();
        }

        // Get transactions for the budget's current period
        var transactions = await GetTransactionsForBudgetAsync(budget);

        // Calculate spending using CategoryKey-based tracking
        var (expenseSpending, incomeSpending) = CalculateSpendingByKey(budget, transactions);

        // Map category IDs to their spending
        var result = new Dictionary<int, decimal>();
        
        foreach (var category in budget.Categories)
        {
            // Use helper method for consistent CategoryId computation
            var categoryId = category.GetEffectiveCategoryId();
            var categoryKey = new CategoryKey(categoryId);
            // Check if income based on IsIncome flag or if categoryId maps to income category (1-4 = Salary, Bonus, Investment, OtherIncome)
            var isIncome = category.IsIncome || (categoryId >= 1 && categoryId <= 4);
            
            var spending = isIncome
                ? incomeSpending.GetValueOrDefault(categoryKey, 0)
                : expenseSpending.GetValueOrDefault(categoryKey, 0);
                
            result[category.Id] = spending;
        }

        return result;
    }

    public async Task<decimal> GetCategorySpentAmountAsync(BudgetCategory category)
    {
        var budget = await _context.Budgets.FindAsync(category.BudgetId);
        if (budget == null)
        {
            _logger.LogWarning("Budget {BudgetId} not found for category {CategoryId} spending calculation", 
                category.BudgetId, category.Id);
            return 0;
        }

        // Get transactions for the budget's current period
        var transactions = await GetTransactionsForBudgetAsync(budget);

        // Calculate spending for this specific category
        // Use helper method for consistent CategoryId computation
        var categoryId = category.GetEffectiveCategoryId();
        var categoryKey = new CategoryKey(categoryId);
        // Check if income based on IsIncome flag or if categoryId maps to income category (1-4 = Salary, Bonus, Investment, OtherIncome)
        var isIncome = category.IsIncome || (categoryId >= 1 && categoryId <= 4);

        if (isIncome)
        {
            var incomeByKey = transactions.IncomeByCategoryKey();
            return incomeByKey.GetValueOrDefault(categoryKey, 0);
        }
        else
        {
            // For allowance budgets, use PersonalUserCategoryId (sub-categorization)
            Dictionary<CategoryKey, decimal> expenseByKey;
            if (budget.Type == BudgetType.Allowance)
            {
                expenseByKey = transactions.ExpensesByPersonalCategoryKey();
            }
            else
            {
                expenseByKey = transactions.ExpensesByCategoryKey();
            }
            return expenseByKey.GetValueOrDefault(categoryKey, 0);
        }
    }

    public async Task<List<(BudgetCategory Category, decimal SpentAmount)>> GetCategoriesWithSpendingAsync(int budgetId)
    {
        var budget = await _context.Budgets
            .Include(b => b.Categories)
                .ThenInclude(c => c.CategoryNav)
            .FirstOrDefaultAsync(b => b.Id == budgetId);

        if (budget == null)
        {
            _logger.LogWarning("Budget {BudgetId} not found for categories with spending", budgetId);
            return new List<(BudgetCategory, decimal)>();
        }

        var spending = await GetCategorySpendingAsync(budgetId);

        return budget.Categories
            .Select(c => (c, spending.GetValueOrDefault(c.Id, 0)))
            .ToList();
    }

    /// <summary>
    /// Gets transactions for a budget's current period, respecting family membership
    /// and user preferences for pending transactions.
    /// </summary>
    private async Task<IEnumerable<Transaction>> GetTransactionsForBudgetAsync(Budget budget)
    {
        // For family budgets, pull transactions from all family members' accounts
        var transactions = budget.FamilyId.HasValue
            ? await _transactionRepository.GetByUserOrFamilyAsync(
                budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive)
            : await _transactionRepository.GetByUserIdAsync(
                budget.UserId, budget.CurrentPeriodStart, budget.CurrentPeriodEndInclusive);

        // Check if user wants to exclude pending transactions
        var user = await _userRepository.GetByIdAsync(budget.UserId);
        if (user?.ExcludePendingFromBudget == true)
        {
            transactions = transactions.Where(t => !t.IsPending).ToList();
            _logger.LogDebug("Excluding pending transactions from spending calculation for user {UserId}", budget.UserId);
        }

        _logger.LogDebug(
            "GetTransactionsForBudget budgetId={BudgetId} userId={UserId} familyId={FamilyId} period={Start:yyyy-MM-dd}..{End:yyyy-MM-dd} count={Count}",
            budget.Id, budget.UserId, budget.FamilyId, budget.CurrentPeriodStart, budget.CurrentPeriodEnd, transactions.Count());

        return transactions;
    }

    /// <summary>
    /// Calculates spending by CategoryKey, handling personal budget differences.
    /// Returns (expenseSpending, incomeSpending) dictionaries.
    /// </summary>
    private static (Dictionary<CategoryKey, decimal> Expenses, Dictionary<CategoryKey, decimal> Income) 
        CalculateSpendingByKey(Budget budget, IEnumerable<Transaction> transactions)
    {
        Dictionary<CategoryKey, decimal> expenseSpending;
        
        if (budget.Type == BudgetType.Allowance)
        {
            // Allowance budgets track spending by PersonalUserCategoryId (sub-categorization)
            expenseSpending = transactions.ExpensesByPersonalCategoryKey();
        }
        else
        {
            // Personal/Family budgets track spending by CategoryId
            expenseSpending = transactions.ExpensesByCategoryKey();
        }
        
        var incomeSpending = transactions.IncomeByCategoryKey();
        
        return (expenseSpending, incomeSpending);
    }
}
