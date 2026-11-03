using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;
using Xunit;

namespace BuoyAI.UnitTests.ValueObjects;

public class TransactionAmountExtensionsTests
{
    #region ToMonetaryAmount Tests

    [Fact]
    public void ToMonetaryAmount_CreditTransaction_CreatesIncome()
    {
        var transaction = CreateTransaction(100, TransactionType.Credit, TransactionCategory.Salary);

        var amount = transaction.ToMonetaryAmount();

        Assert.True(amount.IsInflow);
        Assert.Equal(100, amount.AbsoluteValue);
    }

    [Fact]
    public void ToMonetaryAmount_DebitTransaction_CreatesExpense()
    {
        var transaction = CreateTransaction(50, TransactionType.Debit, TransactionCategory.Groceries);

        var amount = transaction.ToMonetaryAmount();

        Assert.True(amount.IsOutflow);
        Assert.Equal(50, amount.AbsoluteValue);
    }

    #endregion

    #region IsIncome Tests

    [Theory]
    [InlineData(TransactionCategory.Salary)]
    [InlineData(TransactionCategory.Bonus)]
    [InlineData(TransactionCategory.Investment)]
    [InlineData(TransactionCategory.OtherIncome)]
    public void IsIncome_IncomeCategoryDebit_ReturnsTrue(TransactionCategory category)
    {
        // Even if Type is Debit, income categories are income
        var transaction = CreateTransaction(100, TransactionType.Debit, category);

        Assert.True(transaction.IsIncome());
    }

    [Fact]
    public void IsIncome_CreditType_ReturnsTrue()
    {
        var transaction = CreateTransaction(100, TransactionType.Credit, TransactionCategory.Uncategorized);

        Assert.True(transaction.IsIncome());
    }

    [Fact]
    public void IsIncome_DebitNonIncomeCategory_ReturnsFalse()
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries);

        Assert.False(transaction.IsIncome());
    }

    #endregion

    #region IsBudgetable Tests

    [Theory]
    [InlineData(TransactionCategory.CreditPayment)]
    [InlineData(TransactionCategory.Transfer)]
    public void IsBudgetable_NonBudgetableCategory_ReturnsFalse(TransactionCategory category)
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, category);

        Assert.False(transaction.IsBudgetable());
    }

    [Theory]
    [InlineData(TransactionCategory.Groceries)]
    [InlineData(TransactionCategory.Entertainment)]
    [InlineData(TransactionCategory.Salary)]
    [InlineData(TransactionCategory.Utilities)]
    public void IsBudgetable_BudgetableCategory_ReturnsTrue(TransactionCategory category)
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, category);

        Assert.True(transaction.IsBudgetable());
    }

    [Fact]
    public void IsBudgetable_LinkedToSavingsGoal_ReturnsFalse()
    {
        // A transaction explicitly linked to a savings goal must not also count
        // toward its budget category — it has been recorded as a withdrawal from
        // the goal and should be excluded from budget spend.
        var transaction = CreateTransaction(75, TransactionType.Debit, TransactionCategory.Groceries);
        transaction.SavingsGoalId = 42;

        Assert.False(transaction.IsBudgetable());
    }

    [Fact]
    public void IsBudgetable_LinkedToSavingsGoalWithCountFlag_ReturnsTrue()
    {
        // When the goal has CountWithdrawalsTowardBudget enabled, the
        // transaction should still count in the budget.
        var transaction = CreateTransaction(75, TransactionType.Debit, TransactionCategory.Groceries);
        transaction.SavingsGoalId = 42;
        transaction.SavingsGoal = new SavingsGoal
        {
            Id = 42,
            CountWithdrawalsTowardBudget = true,
        };

        Assert.True(transaction.IsBudgetable());
    }

    [Fact]
    public void IsBudgetable_LinkedToSavingsGoalWithCountFlagDisabled_ReturnsFalse()
    {
        var transaction = CreateTransaction(75, TransactionType.Debit, TransactionCategory.Groceries);
        transaction.SavingsGoalId = 42;
        transaction.SavingsGoal = new SavingsGoal
        {
            Id = 42,
            CountWithdrawalsTowardBudget = false,
        };

        Assert.False(transaction.IsBudgetable());
    }

    #endregion

    #region IsIncomeCategory and IsNonBudgetableCategory Tests

    [Theory]
    [InlineData(TransactionCategory.Salary, true)]
    [InlineData(TransactionCategory.Bonus, true)]
    [InlineData(TransactionCategory.Investment, true)]
    [InlineData(TransactionCategory.OtherIncome, true)]
    [InlineData(TransactionCategory.Groceries, false)]
    [InlineData(TransactionCategory.Entertainment, false)]
    [InlineData(TransactionCategory.CreditPayment, false)]
    public void IsIncomeCategory_ReturnsCorrectValue(TransactionCategory category, bool expected)
    {
        Assert.Equal(expected, category.IsIncomeCategory());
    }

    [Theory]
    [InlineData(TransactionCategory.CreditPayment, true)]
    [InlineData(TransactionCategory.Transfer, true)]
    [InlineData(TransactionCategory.Groceries, false)]
    [InlineData(TransactionCategory.Salary, false)]
    public void IsNonBudgetableCategory_ReturnsCorrectValue(TransactionCategory category, bool expected)
    {
        Assert.Equal(expected, category.IsNonBudgetableCategory());
    }

    #endregion

    #region Collection Aggregation Tests

    [Fact]
    public void TotalIncome_SumsOnlyIncomeTransactions()
    {
        var transactions = new[]
        {
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(500, TransactionType.Credit, TransactionCategory.Bonus),
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
        };

        var totalIncome = transactions.TotalIncome();

        Assert.Equal(1500, totalIncome);
    }

    [Fact]
    public void TotalIncome_IncludesUncategorizedCredits()
    {
        // Uncategorized credits (deposits) should count as income
        var transactions = new[]
        {
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(500, TransactionType.Credit, TransactionCategory.Uncategorized), // Deposit
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Uncategorized), // Expense, excluded
        };

        var totalIncome = transactions.TotalIncome();

        Assert.Equal(1500, totalIncome); // 1000 + 500, not the 100 debit
    }

    [Fact]
    public void TotalIncome_ExcludesRefunds()
    {
        // Credits in expense categories are refunds, not income
        var transactions = new[]
        {
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(50, TransactionType.Credit, TransactionCategory.Shopping), // Refund, NOT income
        };

        var totalIncome = transactions.TotalIncome();

        Assert.Equal(1000, totalIncome); // Only salary, not refund
    }

    [Fact]
    public void TotalExpenses_SumsOnlyBudgetableExpenses()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Entertainment),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.CreditPayment), // Should be excluded
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary), // Income credit, excluded
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.Investment), // Debit in an income category (e.g. investment purchase), excluded
        };

        var totalExpenses = transactions.TotalExpenses();

        Assert.Equal(150, totalExpenses);
    }

    [Fact]
    public void ExpensesByCategory_GroupsCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.Entertainment),
        };

        var byCategory = transactions.ExpensesByCategory();

        Assert.Equal(2, byCategory.Count);
        Assert.Equal(150, byCategory[(int)TransactionCategory.Groceries]);
        Assert.Equal(200, byCategory[(int)TransactionCategory.Entertainment]);
    }

    [Fact]
    public void ExpensesByCategory_ExcludesNonBudgetableCategories()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.CreditPayment),
            CreateTransaction(300, TransactionType.Debit, TransactionCategory.Transfer),
        };

        var byCategory = transactions.ExpensesByCategory();

        Assert.Single(byCategory);
        Assert.Equal(100, byCategory[(int)TransactionCategory.Groceries]);
    }

    [Fact]
    public void IncomeByCategory_GroupsCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Bonus),
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries), // Not income
        };

        var byCategory = transactions.IncomeByCategory();

        Assert.Equal(2, byCategory.Count);
        Assert.Equal(5000, byCategory[(int)TransactionCategory.Salary]);
        Assert.Equal(1000, byCategory[(int)TransactionCategory.Bonus]);
    }

    [Fact]
    public void ExpenseCountsByCategory_CountsCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.Entertainment),
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.CreditPayment), // Should be excluded
        };

        var counts = transactions.ExpenseCountsByCategory();

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[(int)TransactionCategory.Groceries]);
        Assert.Equal(1, counts[(int)TransactionCategory.Entertainment]);
    }

    [Fact]
    public void IncomeCountsByCategory_CountsCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Bonus),
            CreateTransaction(500, TransactionType.Credit, TransactionCategory.Bonus),
        };

        var counts = transactions.IncomeCountsByCategory();

        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts[(int)TransactionCategory.Salary]);
        Assert.Equal(2, counts[(int)TransactionCategory.Bonus]);
    }

    [Fact]
    public void WhereBudgetable_ExcludesNonBudgetableTransactions()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.CreditPayment),
            CreateTransaction(300, TransactionType.Debit, TransactionCategory.Transfer),
            CreateTransaction(400, TransactionType.Debit, TransactionCategory.Entertainment),
        };

        var budgetable = transactions.WhereBudgetable().ToList();

        Assert.Equal(2, budgetable.Count);
        Assert.All(budgetable, t => Assert.True(t.IsBudgetable()));
    }

    [Fact]
    public void WhereExpense_FiltersCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(300, TransactionType.Debit, TransactionCategory.CreditPayment),
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.Investment), // Debit in income category, excluded
        };

        var expenses = transactions.WhereExpense().ToList();

        Assert.Single(expenses);
        Assert.Equal((int)TransactionCategory.Groceries, expenses[0].CategoryId);
    }

    [Fact]
    public void WhereIncome_FiltersCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(300, TransactionType.Credit, TransactionCategory.Uncategorized), // Uncategorized credits = income (deposits)
            CreateTransaction(50, TransactionType.Credit, TransactionCategory.Shopping), // Credit in expense category = refund, excluded
        };

        var income = transactions.WhereIncome().ToList();

        // Should include Salary and Uncategorized credits, but exclude refunds (Shopping credit)
        Assert.Equal(2, income.Count);
        Assert.Contains(income, t => t.CategoryId == (int)TransactionCategory.Salary);
        Assert.Contains(income, t => t.CategoryId == (int)TransactionCategory.Uncategorized);
        Assert.DoesNotContain(income, t => t.CategoryId == (int)TransactionCategory.Shopping);
    }

    [Fact]
    public void WhereIncome_ExcludesIncomeEarmarkedToGoal()
    {
        // Income deposited into a savings goal is not spendable income — it must
        // drop out of the income pool so it isn't double-counted (RSU → goal).
        var earmarked = CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Bonus);
        earmarked.SavingsGoalId = 7;
        earmarked.SavingsGoal = new SavingsGoal { Id = 7, CountWithdrawalsTowardBudget = false };

        var transactions = new[]
        {
            CreateTransaction(2000, TransactionType.Credit, TransactionCategory.Salary),
            earmarked,
        };

        var income = transactions.WhereIncome().ToList();

        Assert.Single(income);
        Assert.Contains(income, t => t.CategoryId == (int)TransactionCategory.Salary);
        Assert.DoesNotContain(income, t => t.SavingsGoalId == 7);
    }

    [Fact]
    public void TotalIncome_ExcludesIncomeEarmarkedToGoal()
    {
        var earmarked = CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Bonus);
        earmarked.SavingsGoalId = 7;
        earmarked.SavingsGoal = new SavingsGoal { Id = 7, CountWithdrawalsTowardBudget = false };

        var transactions = new[]
        {
            CreateTransaction(2000, TransactionType.Credit, TransactionCategory.Salary),
            earmarked,
        };

        Assert.Equal(2000, transactions.TotalIncome());
    }

    [Fact]
    public void WhereIncome_IncludesEarmarkedIncomeWhenCountTowardBudgetEnabled()
    {
        // Escape hatch symmetric with IsBudgetable: a goal that counts on both
        // sides keeps its earmarked income in the income pool.
        var earmarked = CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Bonus);
        earmarked.SavingsGoalId = 7;
        earmarked.SavingsGoal = new SavingsGoal { Id = 7, CountWithdrawalsTowardBudget = true };

        var transactions = new[]
        {
            CreateTransaction(2000, TransactionType.Credit, TransactionCategory.Salary),
            earmarked,
        };

        Assert.Equal(7000, transactions.TotalIncome());
    }

    [Fact]
    public void ToMonetaryAmounts_ConvertsAllTransactions()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Credit, TransactionCategory.Salary),
        };

        var amounts = transactions.ToMonetaryAmounts().ToList();

        Assert.Equal(2, amounts.Count);
        Assert.True(amounts[0].IsOutflow);
        Assert.Equal(100, amounts[0].AbsoluteValue);
        Assert.True(amounts[1].IsInflow);
        Assert.Equal(200, amounts[1].AbsoluteValue);
    }

    [Fact]
    public void MonetaryAmounts_SumIncome_CalculatesCorrectly()
    {
        var amounts = new[]
        {
            MonetaryAmount.Income(100),
            MonetaryAmount.Income(200),
            MonetaryAmount.Expense(50),
        };

        var sumIncome = amounts.SumIncome();

        Assert.Equal(300, sumIncome);
    }

    [Fact]
    public void MonetaryAmounts_SumExpenses_CalculatesCorrectly()
    {
        var amounts = new[]
        {
            MonetaryAmount.Income(100),
            MonetaryAmount.Expense(50),
            MonetaryAmount.Expense(75),
        };

        var sumExpenses = amounts.SumExpenses();

        Assert.Equal(125, sumExpenses);
    }

    [Fact]
    public void MonetaryAmounts_NetAmount_CalculatesCorrectly()
    {
        var amounts = new[]
        {
            MonetaryAmount.Income(1000),
            MonetaryAmount.Expense(300),
            MonetaryAmount.Expense(200),
        };

        var net = amounts.NetAmount();

        Assert.Equal(500, net); // 1000 - 300 - 200 = 500
    }

    [Fact]
    public void EmptyCollection_ReturnsZero()
    {
        var transactions = Array.Empty<Transaction>();

        Assert.Equal(0, transactions.TotalIncome());
        Assert.Equal(0, transactions.TotalExpenses());
        Assert.Empty(transactions.ExpensesByCategory());
        Assert.Empty(transactions.IncomeByCategory());
    }

    #endregion

    #region IsExpense and IsBudgetableExpense Tests

    [Fact]
    public void IsExpense_DebitNonIncomeCategory_ReturnsTrue()
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries);

        Assert.True(transaction.IsExpense());
    }

    [Fact]
    public void IsExpense_DebitIncomeCategory_ReturnsFalse()
    {
        // Even if type is Debit, income categories are not expenses
        var transaction = CreateTransaction(100, TransactionType.Debit, TransactionCategory.Salary);

        Assert.False(transaction.IsExpense());
    }

    [Fact]
    public void IsExpense_CreditType_ReturnsFalse()
    {
        var transaction = CreateTransaction(100, TransactionType.Credit, TransactionCategory.Groceries);

        Assert.False(transaction.IsExpense());
    }

    [Theory]
    [InlineData(TransactionCategory.Groceries)]
    [InlineData(TransactionCategory.Entertainment)]
    [InlineData(TransactionCategory.Utilities)]
    [InlineData(TransactionCategory.Rent)]
    public void IsBudgetableExpense_BudgetableExpenseCategory_ReturnsTrue(TransactionCategory category)
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, category);

        Assert.True(transaction.IsBudgetableExpense());
    }

    [Theory]
    [InlineData(TransactionCategory.CreditPayment)]
    [InlineData(TransactionCategory.Transfer)]
    public void IsBudgetableExpense_NonBudgetableCategory_ReturnsFalse(TransactionCategory category)
    {
        var transaction = CreateTransaction(100, TransactionType.Debit, category);

        Assert.False(transaction.IsBudgetableExpense());
    }

    [Fact]
    public void IsBudgetableExpense_IncomeTransaction_ReturnsFalse()
    {
        var transaction = CreateTransaction(100, TransactionType.Credit, TransactionCategory.Salary);

        Assert.False(transaction.IsBudgetableExpense());
    }

    #endregion

    #region WhereBudgetableExpense Tests

    [Fact]
    public void WhereBudgetableExpense_FiltersCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),       // Included
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.CreditPayment),   // Excluded (non-budgetable)
            CreateTransaction(300, TransactionType.Credit, TransactionCategory.Salary),          // Excluded (income)
            CreateTransaction(400, TransactionType.Debit, TransactionCategory.Entertainment),   // Included
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.Transfer),        // Excluded (non-budgetable)
        };

        var budgetableExpenses = transactions.WhereBudgetableExpense().ToList();

        Assert.Equal(2, budgetableExpenses.Count);
        Assert.Contains(budgetableExpenses, t => t.CategoryId == (int)TransactionCategory.Groceries);
        Assert.Contains(budgetableExpenses, t => t.CategoryId == (int)TransactionCategory.Entertainment);
    }

    #endregion

    #region TotalBudgetableExpenses Tests

    [Fact]
    public void TotalBudgetableExpenses_SumsOnlyBudgetableExpenses()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Entertainment),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.CreditPayment), // Excluded
            CreateTransaction(300, TransactionType.Debit, TransactionCategory.Transfer),      // Excluded
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),       // Excluded
        };

        var total = transactions.TotalBudgetableExpenses();

        Assert.Equal(150, total); // 100 + 50
    }

    [Fact]
    public void TotalBudgetableExpenses_ReturnsPositiveValue()
    {
        var transactions = new[]
        {
            CreateTransaction(-100, TransactionType.Debit, TransactionCategory.Groceries), // Negative in DB
        };

        var total = transactions.TotalBudgetableExpenses();

        Assert.Equal(100, total); // Absolute value
    }

    #endregion

    #region TotalSpentInCategory Tests

    [Fact]
    public void TotalSpentInCategory_SumsOnlySpecifiedCategory()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.Entertainment),
        };

        var groceryTotal = transactions.TotalSpentInCategory((int)TransactionCategory.Groceries);

        Assert.Equal(150, groceryTotal);
    }

    [Fact]
    public void TotalSpentInCategory_ExcludesNonBudgetable()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.CreditPayment),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.CreditPayment),
        };

        var total = transactions.TotalSpentInCategory((int)TransactionCategory.CreditPayment);

        Assert.Equal(0, total); // CreditPayment is non-budgetable
    }

    [Fact]
    public void TotalSpentInCategory_ReturnsZeroForMissingCategory()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
        };

        var entertainmentTotal = transactions.TotalSpentInCategory((int)TransactionCategory.Entertainment);

        Assert.Equal(0, entertainmentTotal);
    }

    #endregion

    #region BudgetableExpensesByCategory Tests

    [Fact]
    public void BudgetableExpensesByCategory_GroupsCorrectly()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(50, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.Entertainment),
            CreateTransaction(300, TransactionType.Debit, TransactionCategory.CreditPayment), // Excluded
        };

        var byCategory = transactions.BudgetableExpensesByCategory();

        Assert.Equal(2, byCategory.Count);
        Assert.Equal(150, byCategory[(int)TransactionCategory.Groceries]);
        Assert.Equal(200, byCategory[(int)TransactionCategory.Entertainment]);
        Assert.False(byCategory.ContainsKey((int)TransactionCategory.CreditPayment));
    }

    [Fact]
    public void BudgetableExpensesByCategory_ExcludesIncome()
    {
        var transactions = new[]
        {
            CreateTransaction(100, TransactionType.Debit, TransactionCategory.Groceries),
            CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Salary),
        };

        var byCategory = transactions.BudgetableExpensesByCategory();

        Assert.Single(byCategory);
        Assert.False(byCategory.ContainsKey((int)TransactionCategory.Salary));
    }

    #endregion

    #region NetCashFlow Tests

    [Fact]
    public void NetCashFlow_CalculatesIncomeMinusExpenses()
    {
        var transactions = new[]
        {
            CreateTransaction(5000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Bonus),
            CreateTransaction(2000, TransactionType.Debit, TransactionCategory.Rent),
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.Groceries),
        };

        var netCashFlow = transactions.NetCashFlow();

        Assert.Equal(3500, netCashFlow); // (5000 + 1000) - (2000 + 500) = 3500
    }

    [Fact]
    public void NetCashFlow_ReturnsNegativeWhenExpensesExceedIncome()
    {
        var transactions = new[]
        {
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(2000, TransactionType.Debit, TransactionCategory.Rent),
        };

        var netCashFlow = transactions.NetCashFlow();

        Assert.Equal(-1000, netCashFlow);
    }

    [Fact]
    public void NetCashFlow_ExcludesNonBudgetableFromExpenses()
    {
        var transactions = new[]
        {
            CreateTransaction(1000, TransactionType.Credit, TransactionCategory.Salary),
            CreateTransaction(500, TransactionType.Debit, TransactionCategory.CreditPayment), // Excluded from expenses
            CreateTransaction(200, TransactionType.Debit, TransactionCategory.Groceries),
        };

        var netCashFlow = transactions.NetCashFlow();

        Assert.Equal(800, netCashFlow); // 1000 - 200 = 800 (CreditPayment excluded)
    }

    [Fact]
    public void NetCashFlow_EmptyCollection_ReturnsZero()
    {
        var transactions = Array.Empty<Transaction>();

        Assert.Equal(0, transactions.NetCashFlow());
    }

    #endregion

    #region Allowance Category Key Tests

    [Fact]
    public void ExpensesByAllowanceCategoryKey_ClaimedForOwnerNotSubCategorized_CountsUnderUncategorized()
    {
        var t = CreateTransaction(40, TransactionType.Debit, TransactionCategory.Shopping);
        t.ClaimedByUserId = 7;            // a charge assigned to the allowance owner
        t.PersonalUserCategoryId = null;  // not yet sub-categorized

        var result = new[] { t }.ExpensesByAllowanceCategoryKey(ownerUserId: 7);

        var uncategorized = new TransactionAmountExtensions.CategoryKey((int)TransactionCategory.Uncategorized);
        Assert.Equal(40, result[uncategorized]);
    }

    [Fact]
    public void ExpensesByAllowanceCategoryKey_SubCategorized_CountsUnderPersonalCategory()
    {
        var t = CreateTransaction(25, TransactionType.Debit, TransactionCategory.Shopping);
        t.ClaimedByUserId = 7;
        t.PersonalUserCategoryId = 12;   // owner sub-categorized to personal category (unified 1012)

        var result = new[] { t }.ExpensesByAllowanceCategoryKey(ownerUserId: 7);

        Assert.Equal(25, result[new TransactionAmountExtensions.CategoryKey(1000 + 12)]);
        Assert.False(result.ContainsKey(
            new TransactionAmountExtensions.CategoryKey((int)TransactionCategory.Uncategorized)));
    }

    [Fact]
    public void ExpensesByAllowanceCategoryKey_ClaimedForAnotherUser_Excluded()
    {
        var t = CreateTransaction(99, TransactionType.Debit, TransactionCategory.Shopping);
        t.ClaimedByUserId = 8;            // belongs to a different child's allowance
        t.PersonalUserCategoryId = null;

        var result = new[] { t }.ExpensesByAllowanceCategoryKey(ownerUserId: 7);

        Assert.Empty(result);
    }

    [Fact]
    public void ExpensesByAllowanceCategoryKey_RefundOffsetsSpend()
    {
        var spend = CreateTransaction(50, TransactionType.Debit, TransactionCategory.Shopping);
        spend.ClaimedByUserId = 7;
        var refund = CreateTransaction(20, TransactionType.Credit, TransactionCategory.Shopping);
        refund.ClaimedByUserId = 7;

        var result = new[] { spend, refund }.ExpensesByAllowanceCategoryKey(ownerUserId: 7);

        var uncategorized = new TransactionAmountExtensions.CategoryKey((int)TransactionCategory.Uncategorized);
        Assert.Equal(30, result[uncategorized]);  // 50 spent - 20 refund
    }

    [Fact]
    public void ExpensesByAllowanceCategoryKey_OwnAccountButUnclaimed_NotCounted()
    {
        // Own-account spend reaches the owner via ClaimedByUserId (seeded from the account's
        // AssociatedUserId at ingest), not a raw account-owner rule — otherwise a shared family
        // card the admin owns would dump into their budget. Unclaimed => not this owner's.
        var t = CreateTransaction(30, TransactionType.Debit, TransactionCategory.Shopping);
        t.ClaimedByUserId = null;
        t.Account = new Account { Id = 5, UserId = 7 };

        var result = new[] { t }.ExpensesByAllowanceCategoryKey(ownerUserId: 7);

        Assert.Empty(result);
    }

    #endregion

    #region Custom Income Category (IsIncome flag) Tests

    // A user-created income category (unified id 1000+, CategoryNav.IsIncome = true) must be
    // treated as income even though its id isn't a system income id (1-4). Previously such a
    // $2,000 credit was dropped from income entirely and mis-booked as an expense "return".
    private static Transaction CreateCustomIncomeCredit(decimal amount, int unifiedCategoryId)
    {
        return new Transaction
        {
            Id = Random.Shared.Next(1, 10000),
            Amount = amount,
            Type = TransactionType.Credit,
            CategoryId = unifiedCategoryId,
            CategoryNav = new Category { Id = unifiedCategoryId, Name = "Freelance", IsIncome = true },
            Date = DateTime.UtcNow,
            AccountId = 1,
            Account = new Account { Id = 1, UserId = 1 }
        };
    }

    [Fact]
    public void IsIncomeCategoryTransaction_CustomIncomeCategory_ReturnsTrue()
    {
        var tx = CreateCustomIncomeCredit(2000m, 1001);

        Assert.True(tx.IsIncomeCategoryTransaction());
        Assert.True(tx.IsIncome());
    }

    [Fact]
    public void WhereIncome_CustomIncomeCategoryCredit_IsIncluded()
    {
        var transactions = new[] { CreateCustomIncomeCredit(2000m, 1001) };

        var income = transactions.WhereIncome().ToList();

        Assert.Single(income);
        Assert.Equal(2000m, income[0].Amount);
    }

    [Fact]
    public void IncomeByCategoryKey_CustomIncomeCategory_BucketsUnderItsCategory()
    {
        var transactions = new[] { CreateCustomIncomeCredit(2000m, 1001) };

        var byKey = transactions.IncomeByCategoryKey();

        Assert.Equal(2000m, byKey[new TransactionAmountExtensions.CategoryKey(1001)]);
    }

    [Fact]
    public void ExpensesByCategoryKey_CustomIncomeCategoryCredit_IsExcluded()
    {
        // The same credit must NOT show up as an expense-category "return".
        var transactions = new[] { CreateCustomIncomeCredit(2000m, 1001) };

        var byKey = transactions.ExpensesByCategoryKey();

        Assert.False(byKey.ContainsKey(new TransactionAmountExtensions.CategoryKey(1001)));
    }

    #endregion

    #region Helper Methods

    private static Transaction CreateTransaction(decimal amount, TransactionType type, TransactionCategory category)
    {
        return new Transaction
        {
            Id = Random.Shared.Next(1, 10000),
            Amount = amount,
            Type = type,
            CategoryId = (int)category,
            Date = DateTime.UtcNow,
            AccountId = 1,
            Account = new Account { Id = 1, UserId = 1 }
        };
    }

    #endregion
}
