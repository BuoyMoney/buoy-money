using BuoyAI.Core.Analysis;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Analysis;

/// <summary>
/// "How much did I actually make and spend?"
///
/// Reconstructed from the real production month that exposed this. The AI chat told the user they
/// had $8,535.00 of income and $8,284.00 of spending; both were budget allocations, not actuals.
/// A naive sum of the raw rows isn't right either — it reports $7,172.82 of income, because three
/// credit-card payments land on the card as POSITIVE amounts and get counted as money coming in
/// (and their matching debits from checking get counted as spending).
///
/// The truth for that month: $4,267.27 in (one paycheck), $5,902.78 out.
/// </summary>
public class CashFlowCalculatorTests
{
    private static readonly DateTime Start = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EndExclusive = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    // Real (Plaid) data is signed: money in positive, money out negative. Type follows the sign,
    // as it does in production. `type` overrides that for the cases where they diverge (a refund
    // is a Credit despite an expense category; and the unsigned-expense test below).
    private static Transaction Tx(decimal amount, TransactionCategory category, int day = 10,
        bool isSplit = false, TransactionType? type = null) =>
        new()
        {
            Amount = amount,
            CategoryId = (int)category,
            Type = type ?? (amount >= 0 ? TransactionType.Credit : TransactionType.Debit),
            Date = new DateTime(2026, 7, day, 0, 0, 0, DateTimeKind.Utc),
            IsSplit = isSplit,
        };

    /// The real July, as stored in production.
    private static List<Transaction> RealJuly() => new()
    {
        Tx(4267.27m, TransactionCategory.Salary, 14),        // the only real income
        Tx(100.00m, TransactionCategory.Transfer, 1),        // internal move between own accounts
        Tx(286.86m, TransactionCategory.CreditPayment, 10),  // card payments land POSITIVE on the card
        Tx(1307.46m, TransactionCategory.CreditPayment, 10),
        Tx(1200.00m, TransactionCategory.CreditPayment, 10),
        Tx(11.23m, TransactionCategory.Shopping, 5),         // a Walmart refund, not income
        Tx(-2794.32m, TransactionCategory.CreditPayment, 10),// the other leg, out of checking
        Tx(-100.00m, TransactionCategory.Transfer, 1),       // the other leg of the transfer
        Tx(-2211.08m, TransactionCategory.Rent, 1),
        Tx(-561.03m, TransactionCategory.Entertainment),
        Tx(-399.33m, TransactionCategory.Shopping),
        Tx(-389.40m, TransactionCategory.Groceries),
        Tx(-344.80m, TransactionCategory.Utilities),
        Tx(-306.86m, TransactionCategory.DiningOut),
        Tx(-297.23m, TransactionCategory.LoanPayments),
        Tx(-250.24m, TransactionCategory.Subscriptions),
        // Stands in for the remaining real categories (Jon, Gifts, Pets, Transportation, …),
        // sized so the fixture totals the real month: $5,914.01 gross, $5,902.78 net of refund.
        Tx(-1154.04m, TransactionCategory.Uncategorized),
    };

    [Fact]
    public void Summarize_RealProductionMonth_ReportsOnlyTheActualPaycheckAsIncome()
    {
        var result = CashFlowCalculator.Summarize(RealJuly(), Start, EndExclusive);

        // NOT $8,535 (the budgeted salary) and NOT $7,172.82 (a naive sum).
        result.IncomeReceived.Should().Be(4267.27m);
    }

    [Fact]
    public void Summarize_RealProductionMonth_ExcludesCardPaymentsAndTransfersFromSpending()
    {
        var result = CashFlowCalculator.Summarize(RealJuly(), Start, EndExclusive);

        // Real spending, net of the $11.23 refund. NOT the $8,284 budget allocation.
        result.MoneySpent.Should().Be(5902.78m);
        result.NetCashFlow.Should().Be(4267.27m - 5902.78m);
        result.RefundsNetted.Should().Be(11.23m);
    }

    [Fact]
    public void Summarize_ExpenseStoredAsPositiveAmount_CountsAsSpendNotRefund()
    {
        // The seed data (and any absolute-value source) stores expenses as POSITIVE amounts with a
        // Debit type. Classifying by amount sign treated these as refunds and inflated net cash
        // flow — a real dashboard showed +$24,518 net. Classify by type: a Debit is money out.
        var txns = new List<Transaction>
        {
            Tx(2800m, TransactionCategory.Rent, type: TransactionType.Debit),   // positive expense
            Tx(245.67m, TransactionCategory.Groceries, type: TransactionType.Debit),
            Tx(5000m, TransactionCategory.Salary, type: TransactionType.Credit),
        };

        var result = CashFlowCalculator.Summarize(txns, Start, EndExclusive);

        result.IncomeReceived.Should().Be(5000m);
        result.MoneySpent.Should().Be(3045.67m);
        result.RefundsNetted.Should().Be(0m);
        result.NetCashFlow.Should().Be(1954.33m);
    }

    [Fact]
    public void Summarize_CreditCardPayment_IsNeitherIncomeNorSpending()
    {
        // The exact double-count: the same $1,200 lands positive on the card and negative in
        // checking. Counting either side reports money that never entered or left.
        var txns = new List<Transaction>
        {
            Tx(1200m, TransactionCategory.CreditPayment),
            Tx(-1200m, TransactionCategory.CreditPayment),
        };

        var result = CashFlowCalculator.Summarize(txns, Start, EndExclusive);

        result.IncomeReceived.Should().Be(0);
        result.MoneySpent.Should().Be(0);
        result.InternalMovementExcluded.Should().Be(2400m);
    }

    [Fact]
    public void Summarize_Refund_ReducesSpendingRatherThanAddingIncome()
    {
        var txns = new List<Transaction>
        {
            Tx(-100m, TransactionCategory.Shopping),
            Tx(30m, TransactionCategory.Shopping), // returned an item
        };

        var result = CashFlowCalculator.Summarize(txns, Start, EndExclusive);

        result.MoneySpent.Should().Be(70m);
        result.IncomeReceived.Should().Be(0);
    }

    [Fact]
    public void Summarize_SplitParent_IsSkippedSoTheChargeIsNotCountedTwice()
    {
        var txns = new List<Transaction>
        {
            Tx(-100m, TransactionCategory.Groceries, isSplit: true), // parent
            Tx(-60m, TransactionCategory.Groceries),                 // children carry the money
            Tx(-40m, TransactionCategory.DiningOut),
        };

        var result = CashFlowCalculator.Summarize(txns, Start, EndExclusive);

        result.MoneySpent.Should().Be(100m);
    }

    [Fact]
    public void Summarize_OutsideThePeriod_IsIgnored()
    {
        var txns = new List<Transaction>
        {
            Tx(-50m, TransactionCategory.Groceries),
            new()
            {
                Amount = -999m,
                CategoryId = (int)TransactionCategory.Groceries,
                Date = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc),
            },
        };

        var result = CashFlowCalculator.Summarize(txns, Start, EndExclusive);

        result.MoneySpent.Should().Be(50m);
        result.TransactionsCounted.Should().Be(1);
    }
}
