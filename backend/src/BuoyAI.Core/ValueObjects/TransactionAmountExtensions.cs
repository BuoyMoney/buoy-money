using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;

namespace BuoyAI.Core.ValueObjects;

/// <summary>
/// Extension methods for converting transactions to MonetaryAmount and performing aggregate calculations.
/// </summary>
public static class TransactionAmountExtensions
{
    /// <summary>
    /// Categories that represent income rather than expenses.
    /// </summary>
    public static readonly TransactionCategory[] IncomeCategories = 
    {
        TransactionCategory.Salary,
        TransactionCategory.Bonus,
        TransactionCategory.Investment,
        TransactionCategory.OtherIncome
    };

    /// <summary>
    /// Categories that should be excluded from budget calculations.
    /// These are either transfers between accounts or debt payments for purchases already tracked.
    /// </summary>
    public static readonly TransactionCategory[] NonBudgetableCategories = 
    {
        TransactionCategory.CreditPayment,
        TransactionCategory.Transfer
    };

    /// <summary>
    /// Converts a transaction to its MonetaryAmount representation.
    /// Uses the TransactionType (Debit/Credit) to determine flow direction.
    /// </summary>
    public static MonetaryAmount ToMonetaryAmount(this Transaction transaction)
    {
        return transaction.Type == TransactionType.Credit
            ? MonetaryAmount.Income(transaction.Amount)
            : MonetaryAmount.Expense(transaction.Amount);
    }

    /// <summary>
    /// Returns true if this transaction sits in an income category — either a system income id
    /// (Salary/Bonus/Investment/OtherIncome, 1-4) OR a custom user category flagged IsIncome.
    /// Honors the eager-loaded CategoryNav so user-created income categories (unified id 1000+)
    /// are recognized as income rather than being dropped from income totals (and mis-booked as
    /// expense-category "returns"). Callers relying on the CategoryNav check must eager-load it;
    /// when it isn't loaded this degrades to the system-id-only behavior.
    /// </summary>
    public static bool IsIncomeCategoryTransaction(this Transaction transaction)
    {
        if (transaction.CategoryId.HasValue && IsIncomeCategoryId(transaction.CategoryId.Value))
            return true;

        return transaction.CategoryNav?.IsIncome == true;
    }

    /// <summary>
    /// Returns true if this transaction represents income.
    /// Based on TransactionType.Credit OR being in an income category.
    /// </summary>
    public static bool IsIncome(this Transaction transaction)
    {
        // Check if this is a credit transaction
        if (transaction.Type == TransactionType.Credit)
            return true;

        // Check if the category (system id or custom IsIncome flag) maps to income
        return transaction.IsIncomeCategoryTransaction();
    }

    /// <summary>
    /// Returns true if this transaction represents an expense.
    /// Based on TransactionType.Debit AND NOT being in an income category.
    /// </summary>
    public static bool IsExpense(this Transaction transaction)
    {
        if (transaction.Type != TransactionType.Debit)
            return false;
        
        // Check if CategoryId maps to an income category (if so, not an expense)
        if (transaction.CategoryId.HasValue)
            return !IsIncomeCategoryId(transaction.CategoryId.Value);
        
        return true;
    }

    /// <summary>
    /// Returns true if this transaction should count toward budget calculations.
    /// Excludes credit card payments, transfers, parent split transactions, and
    /// transactions linked to a savings goal.
    ///
    /// Parent split transactions (IsSplit=true) are excluded because their children
    /// contain the actual split amounts that should be counted.
    ///
    /// Savings-linked transactions are excluded because the user has explicitly
    /// designated them as a withdrawal from a savings goal — counting them in the
    /// budget category as well would double-count the spend (once against the
    /// budget, once against the goal balance). This matches the user-facing
    /// semantics: "this purchase came out of my Bali fund, not my Groceries
    /// budget."
    /// </summary>
    public static bool IsBudgetable(this Transaction transaction)
    {
        // Exclude non-budgetable categories (CreditPayment, Transfer)
        if (transaction.CategoryId.HasValue && IsNonBudgetableCategoryId(transaction.CategoryId.Value))
            return false;

        // Exclude parent split transactions - only their children should be counted
        if (transaction.IsSplit)
            return false;

        // Exclude transactions explicitly linked to a savings goal — they are
        // accounted for as a movement in/out of the goal, not budget spend.
        // Exception: when the goal has CountWithdrawalsTowardBudget enabled,
        // the transaction still counts in the budget (user wants both).
        if (transaction.IsEarmarkedToGoalExcludedFromBudget())
            return false;

        return true;
    }

    /// <summary>
    /// Returns true if this transaction is earmarked to a savings goal AND that
    /// earmark should keep it out of the budget (income totals or expense spend).
    ///
    /// This is the single source of truth for savings-goal budget exclusion, used
    /// by both <see cref="IsBudgetable"/> (expense side) and <see cref="WhereIncome"/>
    /// (income side) so the two never drift. An earmarked income transaction is a
    /// DEPOSIT into the goal and an earmarked expense is a WITHDRAWAL — either way
    /// the money is tracked against the goal, not the budget, unless the goal opts
    /// in via CountWithdrawalsTowardBudget.
    ///
    /// NOTE: relies on the SavingsGoal navigation being loaded. Callers that
    /// aggregate over a transaction set (budget assembly) must eager-load
    /// SavingsGoal, otherwise the CountWithdrawalsTowardBudget check defaults to
    /// "excluded" for every earmark.
    /// </summary>
    public static bool IsEarmarkedToGoalExcludedFromBudget(this Transaction transaction)
    {
        if (!transaction.SavingsGoalId.HasValue)
            return false;

        return transaction.SavingsGoal?.CountWithdrawalsTowardBudget != true;
    }

    /// <summary>
    /// Returns true if this transaction is a budgetable expense.
    /// Combines IsExpense() and IsBudgetable() checks.
    /// </summary>
    public static bool IsBudgetableExpense(this Transaction transaction)
    {
        return transaction.IsExpense() && transaction.IsBudgetable();
    }

    /// <summary>
    /// Returns true if the category ID is a large, regularly-recurring debit that is NOT a
    /// subscription: rent/mortgage and loan payments. (Transfers and credit-card payments are
    /// already excluded by <see cref="IsBudgetable"/>.) These are the MOST regular debits a
    /// user has, so including them in subscription detection floods the review sheet with false
    /// positives — the whole point of subscription detection is to surface the small recurring
    /// SaaS/streaming/utility charges, not the mortgage.
    /// </summary>
    public static bool IsNonSubscriptionCategoryId(int categoryId)
    {
        return categoryId == (int)TransactionCategory.Rent ||
               categoryId == (int)TransactionCategory.LoanPayments;
    }

    /// <summary>
    /// Returns true if this transaction is a plausible subscription charge: a budgetable
    /// expense (which already excludes transfers, credit-card payments, split parents, and
    /// savings-goal earmarks) that is also NOT rent/mortgage or a loan payment.
    ///
    /// This replaces the old <see cref="IsExpense"/>-only filter used by subscription detection,
    /// which swept in the most-regular debits (rent, transfers, loan/credit-card payments) and
    /// mislabeled them as monthly subscriptions.
    /// </summary>
    public static bool IsSubscriptionCandidate(this Transaction transaction)
    {
        if (!transaction.IsBudgetableExpense())
            return false;

        if (transaction.CategoryId.HasValue && IsNonSubscriptionCategoryId(transaction.CategoryId.Value))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true if this category represents income.
    /// </summary>
    public static bool IsIncomeCategory(this TransactionCategory category)
    {
        return IncomeCategories.Contains(category);
    }

    /// <summary>
    /// Returns true if this category ID should be excluded from budget calculations.
    /// </summary>
    public static bool IsNonBudgetableCategory(this TransactionCategory category)
    {
        return NonBudgetableCategories.Contains(category);
    }
    
    /// <summary>
    /// Returns true if the category ID represents an income category.
    /// </summary>
    public static bool IsIncomeCategoryId(int categoryId)
    {
        // System income categories have specific enum values
        return categoryId == (int)TransactionCategory.Salary ||
               categoryId == (int)TransactionCategory.Bonus ||
               categoryId == (int)TransactionCategory.Investment ||
               categoryId == (int)TransactionCategory.OtherIncome;
    }
    
    /// <summary>
    /// Returns true if the category ID represents a non-budgetable category.
    /// </summary>
    public static bool IsNonBudgetableCategoryId(int categoryId)
    {
        return categoryId == (int)TransactionCategory.CreditPayment ||
               categoryId == (int)TransactionCategory.Transfer;
    }

    #region Collection Aggregations

    /// <summary>
    /// Sums all income (inflow) amounts in the collection.
    /// </summary>
    public static decimal SumIncome(this IEnumerable<MonetaryAmount> amounts)
    {
        return amounts.Where(a => a.IsInflow).Sum(a => a.AbsoluteValue);
    }

    /// <summary>
    /// Sums all expense (outflow) amounts in the collection.
    /// </summary>
    public static decimal SumExpenses(this IEnumerable<MonetaryAmount> amounts)
    {
        return amounts.Where(a => a.IsOutflow).Sum(a => a.AbsoluteValue);
    }

    /// <summary>
    /// Calculates the net amount (income - expenses).
    /// </summary>
    public static decimal NetAmount(this IEnumerable<MonetaryAmount> amounts)
    {
        return amounts.Sum(a => a.SignedValue);
    }

    /// <summary>
    /// Converts a collection of transactions to MonetaryAmounts.
    /// </summary>
    public static IEnumerable<MonetaryAmount> ToMonetaryAmounts(this IEnumerable<Transaction> transactions)
    {
        return transactions.Select(t => t.ToMonetaryAmount());
    }

    /// <summary>
    /// Gets only budgetable transactions (excludes transfers and credit payments).
    /// </summary>
    public static IEnumerable<Transaction> WhereBudgetable(this IEnumerable<Transaction> transactions)
    {
        return transactions.Where(t => t.IsBudgetable());
    }

    /// <summary>
    /// Gets only expense transactions: debits that are budgetable AND not sitting in
    /// an income category. Uses <see cref="IsBudgetableExpense"/> so this agrees with
    /// <see cref="ExpensesByCategory"/> (which skips income categories) — otherwise a
    /// debit mis-tagged into an income category (e.g. an investment purchase logged as
    /// "Investment") would inflate TotalExpenses but be absent from the per-category
    /// breakdown, leaving the two spending figures inconsistent in the AI context.
    /// </summary>
    public static IEnumerable<Transaction> WhereExpense(this IEnumerable<Transaction> transactions)
    {
        return transactions.Where(t => t.IsBudgetableExpense());
    }

    /// <summary>
    /// Gets only income transactions.
    /// Includes credits for income categories AND uncategorized credits (likely deposits).
    /// Excludes credits in expense categories (those are refunds, not income).
    /// Excludes income earmarked to a savings goal — that money is a deposit into
    /// the goal, not spendable income, so it must drop out of income totals and the
    /// leftover / ready-to-assign pool (mirrors the expense exclusion in IsBudgetable).
    /// </summary>
    public static IEnumerable<Transaction> WhereIncome(this IEnumerable<Transaction> transactions)
    {
        return transactions.Where(t =>
            t.Type == TransactionType.Credit &&
            !t.IsEarmarkedToGoalExcludedFromBudget() &&
            (t.IsIncomeCategoryTransaction() ||
             !t.CategoryId.HasValue ||
             t.CategoryId == (int)TransactionCategory.Uncategorized));
    }

    /// <summary>
    /// Calculates total income from a collection of transactions.
    /// </summary>
    public static decimal TotalIncome(this IEnumerable<Transaction> transactions)
    {
        return transactions.WhereIncome().Sum(t => Math.Abs(t.Amount));
    }

    /// <summary>
    /// Calculates total expenses from a collection of transactions.
    /// </summary>
    public static decimal TotalExpenses(this IEnumerable<Transaction> transactions)
    {
        return transactions.WhereExpense().Sum(t => Math.Abs(t.Amount));
    }

    /// <summary>
    /// Groups transactions by CategoryId and calculates net expenses for each.
    /// For expense categories (non-income), this subtracts returns/refunds (credits) from spending (debits).
    /// This gives an accurate "net spent" amount per category.
    /// </summary>
    public static Dictionary<int, decimal> ExpensesByCategory(this IEnumerable<Transaction> transactions)
    {
        var result = new Dictionary<int, decimal>();
        
        // Get all budgetable transactions (excluding transfers and credit payments)
        var budgetable = transactions.Where(t => t.IsBudgetable());
        
        // Group by CategoryId
        var byCategory = budgetable.GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized);
        
        foreach (var group in byCategory)
        {
            // Skip income categories (system ids 1-4 OR a custom category flagged IsIncome) —
            // they're handled separately by the income path; counting a custom-income credit here
            // would book it as an expense-category "return".
            if (IsIncomeCategoryId(group.Key) || group.Any(t => t.CategoryNav?.IsIncome == true))
                continue;

            // Sum debits (expenses) - these are money going out
            var expenses = group
                .Where(t => t.Type == TransactionType.Debit)
                .Sum(t => Math.Abs(t.Amount));

            // Sum credits (returns/refunds) - these are money coming back from returns
            var returns = group
                .Where(t => t.Type == TransactionType.Credit)
                .Sum(t => Math.Abs(t.Amount));

            // Net spending = expenses - returns
            // e.g., $500 groceries - $50 return = $450 net spent
            // Negative values mean net refunds (returns exceed spending in this period)
            var netSpent = expenses - returns;
            
            // Always include the category if there's any activity
            // Negative = net refund, Positive = net spending
            if (expenses > 0 || returns > 0)
            {
                result[group.Key] = netSpent;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Groups transactions by CategoryId and sums income for each.
    /// </summary>
    public static Dictionary<int, decimal> IncomeByCategory(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereIncome()
            .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
            .ToDictionary(g => g.Key, g => g.Sum(t => Math.Abs(t.Amount)));
    }

    /// <summary>
    /// Counts transactions by CategoryId for expenses.
    /// </summary>
    public static Dictionary<int, int> ExpenseCountsByCategory(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereExpense()
            .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Counts transactions by CategoryId for income.
    /// </summary>
    public static Dictionary<int, int> IncomeCountsByCategory(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereIncome()
            .GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Gets only budgetable expense transactions.
    /// </summary>
    public static IEnumerable<Transaction> WhereBudgetableExpense(this IEnumerable<Transaction> transactions)
    {
        return transactions.Where(t => t.IsBudgetableExpense());
    }

    /// <summary>
    /// Calculates total budgetable expenses from a collection of transactions.
    /// Returns a positive value representing money spent.
    /// </summary>
    public static decimal TotalBudgetableExpenses(this IEnumerable<Transaction> transactions)
    {
        return transactions.WhereBudgetableExpense().Sum(t => Math.Abs(t.Amount));
    }

    /// <summary>
    /// Calculates total spent in a specific CategoryId.
    /// Returns a positive value representing money spent.
    /// </summary>
    public static decimal TotalSpentInCategory(this IEnumerable<Transaction> transactions, int categoryId)
    {
        return transactions
            .WhereBudgetableExpense()
            .Where(t => (t.CategoryId ?? (int)TransactionCategory.Uncategorized) == categoryId)
            .Sum(t => Math.Abs(t.Amount));
    }

    /// <summary>
    /// Groups budgetable expenses by CategoryId and calculates net spending for each.
    /// Returns are subtracted from expenses (e.g., $500 groceries - $50 return = $450 net).
    /// Returns positive values for all categories.
    /// </summary>
    public static Dictionary<int, decimal> BudgetableExpensesByCategory(this IEnumerable<Transaction> transactions)
    {
        var result = new Dictionary<int, decimal>();
        
        // Get all budgetable transactions
        var budgetable = transactions.Where(t => t.IsBudgetable());
        
        // Group by CategoryId
        var byCategory = budgetable.GroupBy(t => t.CategoryId ?? (int)TransactionCategory.Uncategorized);
        
        foreach (var group in byCategory)
        {
            // Skip income categories (system ids 1-4 OR a custom category flagged IsIncome)
            if (IsIncomeCategoryId(group.Key) || group.Any(t => t.CategoryNav?.IsIncome == true))
                continue;

            // Sum debits (expenses)
            var expenses = group
                .Where(t => t.Type == TransactionType.Debit)
                .Sum(t => Math.Abs(t.Amount));
            
            // Sum credits (returns)
            var returns = group
                .Where(t => t.Type == TransactionType.Credit)
                .Sum(t => Math.Abs(t.Amount));
            
            // Net spending - negative means net refunds
            var netSpent = expenses - returns;
            
            // Always include if there's any activity
            if (expenses > 0 || returns > 0)
            {
                result[group.Key] = netSpent;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Calculates net cash flow (income minus expenses) for the transactions.
    /// Positive = net income, Negative = net expense.
    /// </summary>
    public static decimal NetCashFlow(this IEnumerable<Transaction> transactions)
    {
        return transactions.TotalIncome() - transactions.TotalExpenses();
    }

    #endregion

    #region CategoryId-based Expense Tracking

    /// <summary>
    /// Key for tracking expenses by unified CategoryId.
    /// System categories use IDs 1-99, custom categories use IDs 1000+.
    /// </summary>
    public record CategoryKey(int CategoryId);

    /// <summary>
    /// Gets the effective CategoryId for a transaction.
    /// Returns the unified CategoryId or Uncategorized (0) if not set.
    /// </summary>
    private static int GetEffectiveCategoryId(Transaction t)
    {
        return t.CategoryId ?? (int)TransactionCategory.Uncategorized;
    }

    /// <summary>
    /// Groups transactions by CategoryId and calculates net expenses for each.
    /// This allows tracking custom categories separately instead of lumping them into Uncategorized.
    /// Returns are subtracted from expenses (e.g., $500 groceries - $50 return = $450 net).
    /// </summary>
    public static Dictionary<CategoryKey, decimal> ExpensesByCategoryKey(this IEnumerable<Transaction> transactions)
    {
        var result = new Dictionary<CategoryKey, decimal>();
        
        // Get all budgetable transactions (excluding transfers and credit payments)
        var budgetable = transactions.Where(t => t.IsBudgetable());
        
        // Group by CategoryKey (using unified CategoryId)
        var byKey = budgetable.GroupBy(t => new CategoryKey(GetEffectiveCategoryId(t)));
        
        foreach (var group in byKey)
        {
            // Skip income categories (system ids 1-4 OR a custom category flagged IsIncome)
            if (IsIncomeCategoryId(group.Key.CategoryId) || group.Any(t => t.CategoryNav?.IsIncome == true))
                continue;

            // Sum debits (expenses) - money going out
            var expenses = group
                .Where(t => t.Type == TransactionType.Debit)
                .Sum(t => Math.Abs(t.Amount));

            // Sum credits (returns/refunds) - money coming back
            var returns = group
                .Where(t => t.Type == TransactionType.Credit)
                .Sum(t => Math.Abs(t.Amount));

            // Net spending = expenses - returns
            // Negative values mean net refunds
            var netSpent = expenses - returns;

            // Always include if there's any activity
            if (expenses > 0 || returns > 0)
            {
                result[group.Key] = netSpent;
            }
        }

        return result;
    }

    /// <summary>
    /// Counts transactions by CategoryId for expenses.
    /// </summary>
    public static Dictionary<CategoryKey, int> ExpenseCountsByCategoryKey(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereExpense()
            .GroupBy(t => new CategoryKey(GetEffectiveCategoryId(t)))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Groups transactions by CategoryId and sums income for each.
    /// </summary>
    public static Dictionary<CategoryKey, decimal> IncomeByCategoryKey(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereIncome()
            .GroupBy(t => new CategoryKey(GetEffectiveCategoryId(t)))
            .ToDictionary(g => g.Key, g => g.Sum(t => Math.Abs(t.Amount)));
    }

    /// <summary>
    /// Counts transactions by CategoryId for income.
    /// </summary>
    public static Dictionary<CategoryKey, int> IncomeCountsByCategoryKey(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereIncome()
            .GroupBy(t => new CategoryKey(GetEffectiveCategoryId(t)))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Calculates net spent for a specific category key (by unified CategoryId).
    /// Returns net spending = expenses - returns (credits are subtracted).
    /// </summary>
    public static decimal TotalSpentInCategoryKey(this IEnumerable<Transaction> transactions, CategoryKey categoryKey)
    {
        var filtered = transactions
            .Where(t => t.IsBudgetable() && GetEffectiveCategoryId(t) == categoryKey.CategoryId)
            .ToList();
        
        // Sum debits (expenses) - money going out
        var expenses = filtered
            .Where(t => t.Type == TransactionType.Debit)
            .Sum(t => Math.Abs(t.Amount));
        
        // Sum credits (returns/refunds) - money coming back
        var returns = filtered
            .Where(t => t.Type == TransactionType.Credit)
            .Sum(t => Math.Abs(t.Amount));
        
        // Net spending = expenses - returns
        return Math.Max(0, expenses - returns);
    }

    /// <summary>
    /// Calculates net spent for a specific personal budget category.
    /// Uses PersonalUserCategoryId for matching, which is set when an allowance owner 
    /// sub-categorizes a transaction for their personal budget tracking.
    /// Returns net spending = expenses - returns (credits are subtracted).
    /// </summary>
    public static decimal TotalSpentInPersonalCategoryKey(this IEnumerable<Transaction> transactions, CategoryKey categoryKey)
    {
        // Personal categories are 1000+ (user categories)
        if (categoryKey.CategoryId < 1000)
            return 0;
        
        // Extract the original personal category ID from the unified CategoryId (1000+ offset)
        var personalCategoryId = categoryKey.CategoryId - 1000;
        
        var filtered = transactions
            .Where(t => t.IsBudgetable() && t.PersonalUserCategoryId == personalCategoryId)
            .ToList();
        
        // Sum debits (expenses) - money going out
        var expenses = filtered
            .Where(t => t.Type == TransactionType.Debit)
            .Sum(t => Math.Abs(t.Amount));
        
        // Sum credits (returns/refunds) - money coming back
        var returns = filtered
            .Where(t => t.Type == TransactionType.Credit)
            .Sum(t => Math.Abs(t.Amount));
        
        // Net spending = expenses - returns
        return Math.Max(0, expenses - returns);
    }

    /// <summary>
    /// Groups transactions by PersonalUserCategoryId and sums expenses for each.
    /// Used for Personal budgets (allowances) where spending is tracked by personal sub-category.
    /// Returns are subtracted from expenses for accurate net spending.
    /// </summary>
    public static Dictionary<CategoryKey, decimal> ExpensesByPersonalCategoryKey(this IEnumerable<Transaction> transactions)
    {
        var result = new Dictionary<CategoryKey, decimal>();
        
        // Get transactions with PersonalUserCategoryId, excluding transfers and credit payments
        var budgetable = transactions
            .Where(t => t.IsBudgetable() && t.PersonalUserCategoryId.HasValue);
        
        // Group by PersonalUserCategoryId, converting to unified CategoryId (1000+)
        var byKey = budgetable.GroupBy(t => new CategoryKey(1000 + t.PersonalUserCategoryId!.Value));
        
        foreach (var group in byKey)
        {
            // Sum debits (expenses) - money going out
            var expenses = group
                .Where(t => t.Type == TransactionType.Debit)
                .Sum(t => Math.Abs(t.Amount));
            
            // Sum credits (returns/refunds) - money coming back
            var returns = group
                .Where(t => t.Type == TransactionType.Credit)
                .Sum(t => Math.Abs(t.Amount));
            
            // Net spending = expenses - returns
            // Negative values mean net refunds
            var netSpent = expenses - returns;
            
            // Always include if there's any activity
            if (expenses > 0 || returns > 0)
            {
                result[group.Key] = netSpent;
            }
        }
        
        return result;
    }

    /// <summary>
    /// Counts transactions by PersonalUserCategoryId for expenses.
    /// Used for Personal budgets (allowances) where spending is tracked by personal sub-category.
    /// </summary>
    public static Dictionary<CategoryKey, int> ExpenseCountsByPersonalCategoryKey(this IEnumerable<Transaction> transactions)
    {
        return transactions
            .WhereExpense()
            .Where(t => t.PersonalUserCategoryId.HasValue)
            .GroupBy(t => new CategoryKey(1000 + t.PersonalUserCategoryId!.Value))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Allowance bucketing: a sub-categorized transaction maps to its private personal category
    /// (unified id 1000+); a transaction not yet sub-categorized maps to Uncategorized, so a charge
    /// assigned to the allowance still counts (and surfaces under Uncategorized for the owner to split).
    /// </summary>
    private static int AllowanceCategoryKeyId(Transaction t) =>
        t.PersonalUserCategoryId.HasValue
            ? 1000 + t.PersonalUserCategoryId.Value
            : (int)TransactionCategory.Uncategorized;

    /// <summary>
    /// A transaction counts toward an allowance owner's budget when it's claimed for them
    /// (ClaimedByUserId). Own-card spend reaches the owner the same way: ClaimedByUserId is seeded
    /// from the account's AssociatedUserId at ingest, so there's no separate own-account rule —
    /// which would wrongly grab the shared family card the admin happens to own.
    /// </summary>
    private static bool BelongsToAllowanceOwner(Transaction t, int ownerUserId) =>
        t.ClaimedByUserId == ownerUserId;

    /// <summary>
    /// Net expenses for an allowance budget, scoped to its owner.
    /// Counts the budgetable charges that belong to the owner — assigned to them (ClaimedByUserId)
    /// or on an account they own — bucketed by the owner's private sub-category
    /// (PersonalUserCategoryId → unified id 1000+), with not-yet-sub-categorized charges falling
    /// under Uncategorized so they still count. Owner-scoping prevents one child's spend from
    /// leaking onto another's allowance.
    /// </summary>
    public static Dictionary<CategoryKey, decimal> ExpensesByAllowanceCategoryKey(
        this IEnumerable<Transaction> transactions, int ownerUserId)
    {
        var result = new Dictionary<CategoryKey, decimal>();

        var owned = transactions.Where(t => t.IsBudgetable() && BelongsToAllowanceOwner(t, ownerUserId));
        var byKey = owned.GroupBy(t => new CategoryKey(AllowanceCategoryKeyId(t)));

        foreach (var group in byKey)
        {
            var expenses = group.Where(t => t.Type == TransactionType.Debit).Sum(t => Math.Abs(t.Amount));
            var returns = group.Where(t => t.Type == TransactionType.Credit).Sum(t => Math.Abs(t.Amount));

            // Net spending = expenses - returns (refunds reduce spend)
            if (expenses > 0 || returns > 0)
            {
                result[group.Key] = expenses - returns;
            }
        }

        return result;
    }

    /// <summary>
    /// Expense transaction counts for an allowance budget, scoped to its owner.
    /// Mirrors the bucketing of <see cref="ExpensesByAllowanceCategoryKey"/>.
    /// </summary>
    public static Dictionary<CategoryKey, int> ExpenseCountsByAllowanceCategoryKey(
        this IEnumerable<Transaction> transactions, int ownerUserId)
    {
        return transactions
            .WhereExpense()
            .Where(t => BelongsToAllowanceOwner(t, ownerUserId))
            .GroupBy(t => new CategoryKey(AllowanceCategoryKeyId(t)))
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Filters to one personal sub-category (unified id 1000+), matched on PersonalUserCategoryId.
    /// Mirrors <see cref="TotalSpentInPersonalCategoryKey"/>.
    /// </summary>
    public static IEnumerable<Transaction> WherePersonalCategoryKey(this IEnumerable<Transaction> transactions, CategoryKey categoryKey)
    {
        if (categoryKey.CategoryId < 1000)
            return Enumerable.Empty<Transaction>();

        var personalCategoryId = categoryKey.CategoryId - 1000;
        return transactions.Where(t => t.PersonalUserCategoryId == personalCategoryId);
    }

    /// <summary>
    /// Filters to one bucket of an allowance budget, scoped to its owner.
    /// Mirrors the bucketing of <see cref="ExpensesByAllowanceCategoryKey"/>.
    /// </summary>
    public static IEnumerable<Transaction> WhereAllowanceCategoryKey(
        this IEnumerable<Transaction> transactions, int ownerUserId, CategoryKey categoryKey)
    {
        return transactions.Where(t =>
            BelongsToAllowanceOwner(t, ownerUserId) && AllowanceCategoryKeyId(t) == categoryKey.CategoryId);
    }

    /// <summary>
    /// Filters transactions by category key (using unified CategoryId).
    /// </summary>
    public static IEnumerable<Transaction> WhereCategoryKey(this IEnumerable<Transaction> transactions, CategoryKey categoryKey)
    {
        return transactions.Where(t => GetEffectiveCategoryId(t) == categoryKey.CategoryId);
    }

    #endregion
}
