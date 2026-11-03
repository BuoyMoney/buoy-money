using BuoyAI.Core.ValueObjects;
using Xunit;

namespace BuoyAI.UnitTests.ValueObjects;

public class MonetaryAmountTests
{
    #region Construction Tests

    [Fact]
    public void Income_CreatesInflowAmount()
    {
        var amount = MonetaryAmount.Income(100);

        Assert.Equal(100, amount.AbsoluteValue);
        Assert.Equal(MoneyFlow.Inflow, amount.Flow);
        Assert.True(amount.IsInflow);
        Assert.False(amount.IsOutflow);
    }

    [Fact]
    public void Expense_CreatesOutflowAmount()
    {
        var amount = MonetaryAmount.Expense(100);

        Assert.Equal(100, amount.AbsoluteValue);
        Assert.Equal(MoneyFlow.Outflow, amount.Flow);
        Assert.False(amount.IsInflow);
        Assert.True(amount.IsOutflow);
    }

    [Fact]
    public void Income_WithNegativeValue_ConvertsToPositive()
    {
        var amount = MonetaryAmount.Income(-100);

        Assert.Equal(100, amount.AbsoluteValue);
        Assert.True(amount.IsInflow);
    }

    [Fact]
    public void Expense_WithNegativeValue_ConvertsToPositive()
    {
        var amount = MonetaryAmount.Expense(-100);

        Assert.Equal(100, amount.AbsoluteValue);
        Assert.True(amount.IsOutflow);
    }

    [Fact]
    public void Zero_CreatesZeroAmount()
    {
        var amount = MonetaryAmount.Zero;

        Assert.Equal(0, amount.AbsoluteValue);
        Assert.True(amount.IsZero);
    }

    #endregion

    #region Plaid Conversion Tests

    [Fact]
    public void FromPlaid_PositiveAmount_CreatesExpense()
    {
        // Plaid: positive = money out (expense)
        var amount = MonetaryAmount.FromPlaid(50.00m);

        Assert.Equal(50.00m, amount.AbsoluteValue);
        Assert.True(amount.IsOutflow);
    }

    [Fact]
    public void FromPlaid_NegativeAmount_CreatesIncome()
    {
        // Plaid: negative = money in (income)
        var amount = MonetaryAmount.FromPlaid(-1500.00m);

        Assert.Equal(1500.00m, amount.AbsoluteValue);
        Assert.True(amount.IsInflow);
    }

    [Fact]
    public void FromPlaid_Zero_CreatesZeroExpense()
    {
        var amount = MonetaryAmount.FromPlaid(0);

        Assert.Equal(0, amount.AbsoluteValue);
        Assert.True(amount.IsZero);
    }

    #endregion

    #region SignedValue Tests

    [Fact]
    public void SignedValue_ForIncome_IsPositive()
    {
        var amount = MonetaryAmount.Income(100);

        Assert.Equal(100, amount.SignedValue);
    }

    [Fact]
    public void SignedValue_ForExpense_IsNegative()
    {
        var amount = MonetaryAmount.Expense(100);

        Assert.Equal(-100, amount.SignedValue);
    }

    #endregion

    #region Arithmetic Tests

    [Fact]
    public void Addition_IncomeAndExpense_CalculatesNet()
    {
        var income = MonetaryAmount.Income(100);
        var expense = MonetaryAmount.Expense(30);

        var result = income + expense;

        Assert.Equal(70, result.AbsoluteValue);
        Assert.True(result.IsInflow); // Net positive = income
    }

    [Fact]
    public void Addition_ExpenseGreaterThanIncome_ResultsInOutflow()
    {
        var income = MonetaryAmount.Income(50);
        var expense = MonetaryAmount.Expense(100);

        var result = income + expense;

        Assert.Equal(50, result.AbsoluteValue);
        Assert.True(result.IsOutflow); // Net negative = expense
    }

    [Fact]
    public void Addition_TwoIncomes_SumsAsIncome()
    {
        var income1 = MonetaryAmount.Income(100);
        var income2 = MonetaryAmount.Income(200);

        var result = income1 + income2;

        Assert.Equal(300, result.AbsoluteValue);
        Assert.True(result.IsInflow);
    }

    [Fact]
    public void Addition_TwoExpenses_SumsAsExpense()
    {
        var expense1 = MonetaryAmount.Expense(100);
        var expense2 = MonetaryAmount.Expense(200);

        var result = expense1 + expense2;

        Assert.Equal(300, result.AbsoluteValue);
        Assert.True(result.IsOutflow);
    }

    [Fact]
    public void Subtraction_IncomeMinusExpense_AddsValues()
    {
        var income = MonetaryAmount.Income(100);
        var expense = MonetaryAmount.Expense(30);

        var result = income - expense;

        // 100 - (-30) = 130
        Assert.Equal(130, result.AbsoluteValue);
        Assert.True(result.IsInflow);
    }

    [Fact]
    public void Negation_FlipsDirection()
    {
        var income = MonetaryAmount.Income(100);

        var negated = -income;

        Assert.Equal(100, negated.AbsoluteValue);
        Assert.True(negated.IsOutflow);
    }

    [Fact]
    public void Multiplication_ByPositiveScalar_PreservesDirection()
    {
        var expense = MonetaryAmount.Expense(100);

        var result = expense * 2;

        Assert.Equal(200, result.AbsoluteValue);
        Assert.True(result.IsOutflow);
    }

    [Fact]
    public void Multiplication_ByNegativeScalar_FlipsDirection()
    {
        var income = MonetaryAmount.Income(100);

        var result = income * -1;

        Assert.Equal(100, result.AbsoluteValue);
        Assert.True(result.IsOutflow);
    }

    [Fact]
    public void Division_ByPositiveScalar_PreservesDirection()
    {
        var expense = MonetaryAmount.Expense(100);

        var result = expense / 2;

        Assert.Equal(50, result.AbsoluteValue);
        Assert.True(result.IsOutflow);
    }

    [Fact]
    public void Division_ByZero_ThrowsException()
    {
        var amount = MonetaryAmount.Income(100);

        Assert.Throws<DivideByZeroException>(() => amount / 0);
    }

    #endregion

    #region Comparison Tests

    [Fact]
    public void CompareTo_IncomeGreaterThanExpense()
    {
        var income = MonetaryAmount.Income(100);
        var expense = MonetaryAmount.Expense(100);

        Assert.True(income > expense);
        Assert.True(expense < income);
    }

    [Fact]
    public void CompareTo_SameValueSameDirection_AreEqual()
    {
        var amount1 = MonetaryAmount.Income(100);
        var amount2 = MonetaryAmount.Income(100);

        Assert.True(amount1 == amount2);
        Assert.True(amount1 >= amount2);
        Assert.True(amount1 <= amount2);
    }

    [Fact]
    public void CompareTo_LargerExpenseIsLessThanSmallerIncome()
    {
        var bigExpense = MonetaryAmount.Expense(1000);
        var smallIncome = MonetaryAmount.Income(1);

        Assert.True(bigExpense < smallIncome);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_Income_ShowsPlusSign()
    {
        var amount = MonetaryAmount.Income(100);

        Assert.Equal("+$100.00", amount.ToString());
    }

    [Fact]
    public void ToString_Expense_ShowsMinusSign()
    {
        var amount = MonetaryAmount.Expense(100);

        Assert.Equal("-$100.00", amount.ToString());
    }

    #endregion

    #region FromPositiveIncome and FromPositiveExpense Tests

    [Fact]
    public void FromPositiveIncome_PositiveValue_CreatesIncome()
    {
        var amount = MonetaryAmount.FromPositiveIncome(100);

        Assert.True(amount.IsInflow);
        Assert.Equal(100, amount.AbsoluteValue);
    }

    [Fact]
    public void FromPositiveIncome_NegativeValue_CreatesExpense()
    {
        var amount = MonetaryAmount.FromPositiveIncome(-100);

        Assert.True(amount.IsOutflow);
        Assert.Equal(100, amount.AbsoluteValue);
    }

    [Fact]
    public void FromPositiveExpense_PositiveValue_CreatesExpense()
    {
        var amount = MonetaryAmount.FromPositiveExpense(100);

        Assert.True(amount.IsOutflow);
        Assert.Equal(100, amount.AbsoluteValue);
    }

    [Fact]
    public void FromPositiveExpense_NegativeValue_CreatesIncome()
    {
        var amount = MonetaryAmount.FromPositiveExpense(-100);

        Assert.True(amount.IsInflow);
        Assert.Equal(100, amount.AbsoluteValue);
    }

    #endregion
}
