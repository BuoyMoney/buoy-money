using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;
using FluentAssertions;
using Xunit;

namespace BuoyAI.UnitTests.Analysis;

/// <summary>
/// The Transactions page header (Income / Expenses / Net) against the real production month that
/// exposed the AI reporting budget allocations as actuals.
///
/// The suspicion was that this header double-counts the same way a naive sum does — a credit-card
/// payment lands POSITIVE on the card, so summing positives reports it as income. It does not:
/// WhereIncome() requires an income category (or uncategorized), so a payment categorised
/// CreditPayment is already excluded, and TotalExpenses is derived from ExpensesByCategory(),
/// which drops transfers, card payments, split parents and earmarks and nets refunds.
///
/// These tests pin that, so the header can't quietly regress into the same double-count.
/// </summary>
public class TransactionsPageSummaryTests
{
    private static Transaction Tx(decimal amount, TransactionCategory category) => new()
    {
        Amount = amount,
        CategoryId = (int)category,
        // Sign convention: positive = money in = Credit.
        Type = amount > 0 ? TransactionType.Credit : TransactionType.Debit,
        Date = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc),
    };

    /// The user's real July, as stored in production.
    private static List<Transaction> RealJuly() => new()
    {
        Tx(4267.27m, TransactionCategory.Salary),          // the only real income
        Tx(100.00m, TransactionCategory.Transfer),         // internal movement
        Tx(286.86m, TransactionCategory.CreditPayment),    // card payments land positive on the card
        Tx(1307.46m, TransactionCategory.CreditPayment),
        Tx(1200.00m, TransactionCategory.CreditPayment),
        Tx(11.23m, TransactionCategory.Shopping),          // a refund
        Tx(-2794.32m, TransactionCategory.CreditPayment),  // the other leg, out of checking
        Tx(-100.00m, TransactionCategory.Transfer),
        Tx(-2211.08m, TransactionCategory.Rent),
        Tx(-389.40m, TransactionCategory.Groceries),
        Tx(-344.80m, TransactionCategory.Utilities),
    };

    [Fact]
    public void TotalIncome_CountsOnlyTheRealPaycheck_NotCardPaymentsOrTransfers()
    {
        // If this ever returns 7,172.82-style numbers, the header is double-counting the user's
        // own money as income.
        RealJuly().TotalIncome().Should().Be(4267.27m);
    }

    [Fact]
    public void ExpensesByCategory_ExcludesCardPaymentsAndTransfers_AndNetsRefunds()
    {
        var byCategory = RealJuly().ExpensesByCategory();

        byCategory.Should().NotContainKey((int)TransactionCategory.CreditPayment,
            "paying a card moves the user's own money; it is not spending");
        byCategory.Should().NotContainKey((int)TransactionCategory.Transfer);

        byCategory[(int)TransactionCategory.Rent].Should().Be(2211.08m);
        byCategory[(int)TransactionCategory.Groceries].Should().Be(389.40m);

        // Shopping is a net refund (+11.23 with no debit) — negative spend, filtered out of the
        // header by the `netSpent <= 0` guard in BuildTransactionSummaryAsync.
        byCategory[(int)TransactionCategory.Shopping].Should().Be(-11.23m);
    }

    [Fact]
    public void HeaderTotals_MatchTheCashFlowCalculator_OnIncome()
    {
        // Two code paths answer "what did they actually earn" — the page header and the AI's
        // get_cash_flow. They must not disagree, or the app contradicts its own chat.
        var summary = Core.Analysis.CashFlowCalculator.Summarize(
            RealJuly(),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        summary.IncomeReceived.Should().Be(RealJuly().TotalIncome());
    }
}
