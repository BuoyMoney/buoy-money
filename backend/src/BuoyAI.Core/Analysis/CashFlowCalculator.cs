using BuoyAI.Core.Entities;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Core.Analysis;

/// <summary>
/// What actually came in and went out over a period.
/// </summary>
public sealed record CashFlowSummary
{
    /// <summary>Money genuinely received (paychecks etc.). Excludes internal movement.</summary>
    public decimal IncomeReceived { get; init; }

    /// <summary>Money genuinely spent, net of refunds. Excludes internal movement.</summary>
    public decimal MoneySpent { get; init; }

    public decimal NetCashFlow => IncomeReceived - MoneySpent;

    /// <summary>
    /// Total absolute value of the transfers and credit-card payments left out. Surfaced so a
    /// caller can explain the gap between this and a naive sum of the raw rows.
    /// </summary>
    public decimal InternalMovementExcluded { get; init; }

    /// <summary>Refunds subtracted from <see cref="MoneySpent"/> (a return on a card).</summary>
    public decimal RefundsNetted { get; init; }

    /// <summary>Rows that contributed to income or spend (excludes skipped ones).</summary>
    public int TransactionsCounted { get; init; }

    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEndExclusive { get; init; }
}

/// <summary>
/// The single answer to "how much did I actually make and spend?".
///
/// Summing raw transaction amounts gets this badly wrong, and it did in production: a credit-card
/// payment leaves checking (negative) and lands on the card (positive), so a naive sum counts the
/// same money as BOTH income and spending. A month with $4,000 of real income could show
/// about $7,000, the extra being card payments, an internal transfer and a refund. The same double-count inflates spending.
///
/// So: transfers and credit-card payments are internal movement and belong to neither side, and a
/// positive amount in an expense category is a refund that reduces spending rather than income
/// that increases it.
/// </summary>
public static class CashFlowCalculator
{
    public static CashFlowSummary Summarize(
        IEnumerable<Transaction> transactions,
        DateTime periodStart,
        DateTime periodEndExclusive)
    {
        decimal income = 0, spent = 0, internalMovement = 0, refunds = 0;
        var counted = 0;

        foreach (var t in transactions)
        {
            if (t.Date < periodStart || t.Date >= periodEndExclusive) continue;

            // A split parent carries no money of its own — its children do. Counting both
            // double-counts the charge.
            if (t.IsSplit) continue;

            var categoryId = t.CategoryId;

            // Transfers and credit-card payments move money between the user's own accounts.
            // Both legs are real rows; neither is income or spending.
            if (categoryId.HasValue && TransactionAmountExtensions.IsNonBudgetableCategoryId(categoryId.Value))
            {
                internalMovement += Math.Abs(t.Amount);
                continue;
            }

            // Classify by direction (credit = money in, debit = money out) using the transaction
            // TYPE, not the amount's sign. The backend sets Type from the source, but the amount's
            // sign is not dependable — Plaid signs it, manual/imported/seed rows may not, and a
            // positive-amount expense would otherwise look like a refund and inflate net cash flow.
            // Use the magnitude so the result is the same whichever way the amount is signed.
            var magnitude = Math.Abs(t.Amount);
            if (t.Type == TransactionType.Credit)
            {
                // Money in. Real income if it lands in an income (or no) category; otherwise it's a
                // refund — money back on something already counted as spending, not new income.
                if (!categoryId.HasValue || TransactionAmountExtensions.IsIncomeCategoryId(categoryId.Value))
                {
                    income += magnitude;
                }
                else
                {
                    refunds += magnitude;
                    spent -= magnitude;
                }
                counted++;
            }
            else
            {
                // Money out.
                spent += magnitude;
                counted++;
            }
        }

        return new CashFlowSummary
        {
            IncomeReceived = income,
            MoneySpent = spent,
            InternalMovementExcluded = internalMovement,
            RefundsNetted = refunds,
            TransactionsCounted = counted,
            PeriodStart = periodStart,
            PeriodEndExclusive = periodEndExclusive,
        };
    }
}
