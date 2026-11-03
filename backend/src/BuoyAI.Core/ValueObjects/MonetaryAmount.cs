namespace BuoyAI.Core.ValueObjects;

/// <summary>
/// Represents a monetary amount with explicit flow direction (inflow vs outflow).
/// This eliminates confusion around sign conventions when working with transactions.
/// </summary>
/// <remarks>
/// Different sources use different sign conventions:
/// - Plaid: positive = expense (money out), negative = income (money in)
/// - Some systems: positive = income, negative = expense
/// 
/// MonetaryAmount normalizes this by storing the absolute value and explicit direction.
/// </remarks>
public readonly record struct MonetaryAmount : IComparable<MonetaryAmount>
{
    /// <summary>
    /// The absolute (positive) value of this amount.
    /// </summary>
    public decimal AbsoluteValue { get; }

    /// <summary>
    /// The direction of money flow.
    /// </summary>
    public MoneyFlow Flow { get; }

    /// <summary>
    /// Creates a new MonetaryAmount.
    /// </summary>
    /// <param name="absoluteValue">The absolute value (will be converted to positive if negative).</param>
    /// <param name="flow">The direction of money flow.</param>
    public MonetaryAmount(decimal absoluteValue, MoneyFlow flow)
    {
        AbsoluteValue = Math.Abs(absoluteValue);
        Flow = flow;
    }

    /// <summary>
    /// Gets the signed value: positive for inflows, negative for outflows.
    /// </summary>
    public decimal SignedValue => Flow == MoneyFlow.Inflow ? AbsoluteValue : -AbsoluteValue;

    /// <summary>
    /// Returns true if this represents money coming in (income).
    /// </summary>
    public bool IsInflow => Flow == MoneyFlow.Inflow;

    /// <summary>
    /// Returns true if this represents money going out (expense).
    /// </summary>
    public bool IsOutflow => Flow == MoneyFlow.Outflow;

    /// <summary>
    /// Returns true if this amount is zero.
    /// </summary>
    public bool IsZero => AbsoluteValue == 0;

    #region Factory Methods

    /// <summary>
    /// Creates an income/inflow amount.
    /// </summary>
    public static MonetaryAmount Income(decimal amount) => new(amount, MoneyFlow.Inflow);

    /// <summary>
    /// Creates an expense/outflow amount.
    /// </summary>
    public static MonetaryAmount Expense(decimal amount) => new(amount, MoneyFlow.Outflow);

    /// <summary>
    /// Creates a zero amount (neither income nor expense).
    /// </summary>
    public static MonetaryAmount Zero => new(0, MoneyFlow.Outflow);

    /// <summary>
    /// Converts a Plaid transaction amount to MonetaryAmount.
    /// Plaid convention: positive = expense, negative = income.
    /// </summary>
    public static MonetaryAmount FromPlaid(decimal plaidAmount)
    {
        return plaidAmount >= 0
            ? Expense(plaidAmount)
            : Income(Math.Abs(plaidAmount));
    }

    /// <summary>
    /// Creates from a raw amount where positive means income and negative means expense.
    /// </summary>
    public static MonetaryAmount FromPositiveIncome(decimal amount)
    {
        return amount >= 0
            ? Income(amount)
            : Expense(Math.Abs(amount));
    }

    /// <summary>
    /// Creates from a raw amount where positive means expense and negative means income.
    /// </summary>
    public static MonetaryAmount FromPositiveExpense(decimal amount)
    {
        return amount >= 0
            ? Expense(amount)
            : Income(Math.Abs(amount));
    }

    #endregion

    #region Arithmetic Operations

    /// <summary>
    /// Adds two monetary amounts, respecting their flow directions.
    /// Returns the net result with appropriate flow direction.
    /// </summary>
    public static MonetaryAmount operator +(MonetaryAmount left, MonetaryAmount right)
    {
        var netValue = left.SignedValue + right.SignedValue;
        return FromPositiveIncome(netValue);
    }

    /// <summary>
    /// Subtracts two monetary amounts.
    /// </summary>
    public static MonetaryAmount operator -(MonetaryAmount left, MonetaryAmount right)
    {
        var netValue = left.SignedValue - right.SignedValue;
        return FromPositiveIncome(netValue);
    }

    /// <summary>
    /// Negates the flow direction.
    /// </summary>
    public static MonetaryAmount operator -(MonetaryAmount amount)
    {
        return new MonetaryAmount(amount.AbsoluteValue, amount.Flow == MoneyFlow.Inflow ? MoneyFlow.Outflow : MoneyFlow.Inflow);
    }

    /// <summary>
    /// Multiplies the amount by a scalar.
    /// </summary>
    public static MonetaryAmount operator *(MonetaryAmount amount, decimal multiplier)
    {
        if (multiplier < 0)
        {
            return new MonetaryAmount(amount.AbsoluteValue * Math.Abs(multiplier), 
                amount.Flow == MoneyFlow.Inflow ? MoneyFlow.Outflow : MoneyFlow.Inflow);
        }
        return new MonetaryAmount(amount.AbsoluteValue * multiplier, amount.Flow);
    }

    /// <summary>
    /// Divides the amount by a scalar.
    /// </summary>
    public static MonetaryAmount operator /(MonetaryAmount amount, decimal divisor)
    {
        if (divisor == 0) throw new DivideByZeroException();
        if (divisor < 0)
        {
            return new MonetaryAmount(amount.AbsoluteValue / Math.Abs(divisor),
                amount.Flow == MoneyFlow.Inflow ? MoneyFlow.Outflow : MoneyFlow.Inflow);
        }
        return new MonetaryAmount(amount.AbsoluteValue / divisor, amount.Flow);
    }

    #endregion

    #region Comparison

    public int CompareTo(MonetaryAmount other) => SignedValue.CompareTo(other.SignedValue);

    public static bool operator <(MonetaryAmount left, MonetaryAmount right) => left.SignedValue < right.SignedValue;
    public static bool operator >(MonetaryAmount left, MonetaryAmount right) => left.SignedValue > right.SignedValue;
    public static bool operator <=(MonetaryAmount left, MonetaryAmount right) => left.SignedValue <= right.SignedValue;
    public static bool operator >=(MonetaryAmount left, MonetaryAmount right) => left.SignedValue >= right.SignedValue;

    #endregion

    public override string ToString() => $"{(IsInflow ? "+" : "-")}${AbsoluteValue:N2}";
}

/// <summary>
/// Represents the direction of money flow.
/// </summary>
public enum MoneyFlow
{
    /// <summary>
    /// Money coming in (income, credits, refunds).
    /// </summary>
    Inflow,

    /// <summary>
    /// Money going out (expenses, debits, purchases).
    /// </summary>
    Outflow
}
