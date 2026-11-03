using System.Globalization;
using System.Text;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Core.Analysis;

/// <summary>
/// One transaction that is both UNCERTAIN (unreviewed / low-confidence) and MATERIAL
/// (large enough to move the headline spending figures on its own). These are the only
/// transactions the AI is told to hedge on — see <see cref="InsightReliabilityAnalyzer"/>.
/// </summary>
public record ReliabilityFlaggedItem(
    DateTime Date,
    string Merchant,
    string Category,
    decimal Amount,
    decimal ShareOfSpend,
    string Reason);

/// <summary>
/// A snapshot of how trustworthy the spending numbers in an AI context are, given how much
/// of the underlying spend the user has actually reviewed.
/// </summary>
public record ReliabilityAssessment(
    decimal TotalExpenses,
    decimal ReviewedExpenses,
    decimal UnreviewedExpenses,
    int UnreviewedCount,
    IReadOnlyList<ReliabilityFlaggedItem> MaterialUncertainItems)
{
    public bool HasMaterialUncertainty => MaterialUncertainItems.Count > 0;
}

/// <summary>
/// Turns the review/confidence fields that already live on each transaction
/// (<c>IsUserCategorized</c>, <c>NeedsReview</c>, <c>CategorySource</c>, <c>AIConfidence</c>)
/// into a reliability picture for the AI insight context.
///
/// Design intent (guardrails agreed with the product owner):
///  1. Confidence changes what the AI *says*, never the numbers themselves — this class
///     never mutates or excludes a transaction, it only annotates.
///  2. "Unreviewed" alone is too blunt to hedge on: a freshly-linked account is 100%
///     unreviewed, and blanket-hedging every insight makes them useless. We only flag a
///     transaction when it is BOTH unreviewed/low-confidence AND materially drives the
///     total (<see cref="MaterialShareThreshold"/> of spend). That keeps signal high and
///     points the user at the exact review that would fix the data.
///  3. We never guess that something is a transfer from its size — we only surface that an
///     unreviewed item *might* be a transfer or miscategorised and ask the user to confirm.
/// </summary>
public static class InsightReliabilityAnalyzer
{
    /// <summary>A single unreviewed expense at or above this share of total spend is "material".</summary>
    public const decimal MaterialShareThreshold = 0.15m;

    /// <summary>Never name more than this many flagged items in the prompt (keep it focused).</summary>
    public const int MaxFlaggedItems = 5;

    /// <summary>
    /// Assesses reliability over a window of transactions. <paramref name="totalExpenses"/> is
    /// the authoritative expense total for the window (from the transaction summary) and is
    /// used as the denominator for the "material" test — pass the same figure shown to the AI
    /// so the shares line up with the numbers it sees.
    /// </summary>
    public static ReliabilityAssessment Assess(
        IEnumerable<TransactionDto> windowTransactions,
        decimal totalExpenses)
    {
        var expenses = (windowTransactions ?? Enumerable.Empty<TransactionDto>())
            .Where(IsExpense)
            .ToList();

        var reviewed = expenses.Where(t => t.IsUserCategorized).Sum(t => Math.Abs(t.Amount));
        var unreviewedExpenses = expenses.Where(t => !t.IsUserCategorized).ToList();
        var unreviewedSum = unreviewedExpenses.Sum(t => Math.Abs(t.Amount));

        var absTotal = Math.Abs(totalExpenses);
        var flagged = new List<ReliabilityFlaggedItem>();

        if (absTotal > 0)
        {
            foreach (var t in unreviewedExpenses
                         .OrderByDescending(t => Math.Abs(t.Amount)))
            {
                var share = Math.Abs(t.Amount) / absTotal;
                if (share < MaterialShareThreshold)
                {
                    // Ordered by amount desc, so once one falls below the bar the rest do too.
                    break;
                }

                flagged.Add(new ReliabilityFlaggedItem(
                    Date: t.Date,
                    Merchant: string.IsNullOrWhiteSpace(t.MerchantName) ? "(no merchant)" : t.MerchantName,
                    Category: string.IsNullOrWhiteSpace(t.CategoryName) ? "Uncategorized" : t.CategoryName,
                    Amount: Math.Abs(t.Amount),
                    ShareOfSpend: share,
                    Reason: DescribeUncertainty(t)));

                if (flagged.Count >= MaxFlaggedItems)
                {
                    break;
                }
            }
        }

        return new ReliabilityAssessment(
            TotalExpenses: absTotal,
            ReviewedExpenses: reviewed,
            UnreviewedExpenses: unreviewedSum,
            UnreviewedCount: unreviewedExpenses.Count,
            MaterialUncertainItems: flagged);
    }

    /// <summary>
    /// Renders the assessment as a "=== DATA RELIABILITY ===" block for the AI prompt. When
    /// nothing material is uncertain it reassures instead of hedging, so the model doesn't
    /// water down insights on clean data.
    /// </summary>
    public static string FormatForPrompt(ReliabilityAssessment a)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("=== DATA RELIABILITY ===");

        if (a.TotalExpenses <= 0)
        {
            sb.AppendLine("No spending in this window to assess.");
            return sb.ToString();
        }

        var reviewedPct = a.ReviewedExpenses / a.TotalExpenses * 100m;
        sb.AppendLine(
            "Some transactions below were categorized automatically (by the bank feed or our " +
            "categorizer) and have NOT been confirmed by the user. Unreviewed transactions may " +
            "be miscategorized, or may not be real spending at all (for example an internal " +
            "transfer between the user's own accounts that wasn't tagged as one).");
        sb.AppendLine(
            $"User-reviewed spend: ${a.ReviewedExpenses.ToString("F2", ci)} of " +
            $"${a.TotalExpenses.ToString("F2", ci)} ({reviewedPct.ToString("F0", ci)}%).");
        sb.AppendLine(
            $"Unreviewed / low-confidence in this window: {a.UnreviewedCount} " +
            $"transaction(s) totaling ${a.UnreviewedExpenses.ToString("F2", ci)}.");

        if (a.HasMaterialUncertainty)
        {
            sb.AppendLine();
            sb.AppendLine(
                "GUIDANCE: The following unreviewed transactions are individually large enough " +
                "to move the totals above. Treat any conclusion that depends on them as " +
                "TENTATIVE. Do not state a spending figure driven by them as fact — instead, " +
                "point the user to review/confirm them (they may be transfers or miscategorized) " +
                "before drawing a conclusion. Never assume on your own that one is a transfer.");
            foreach (var item in a.MaterialUncertainItems)
            {
                var sharePct = (item.ShareOfSpend * 100m).ToString("F0", ci);
                sb.AppendLine(
                    $"  ⚠ {item.Date:MMM dd}: ${item.Amount.ToString("F2", ci)} at {item.Merchant} " +
                    $"({item.Category}) — {sharePct}% of total spend, {item.Reason}");
            }
        }
        else
        {
            sb.AppendLine(
                "Every transaction large enough to materially affect these figures has been " +
                "reviewed or is high-confidence — the spending numbers above are reliable.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Expense test for a DTO, mirroring <c>TransactionAmountExtensions.IsBudgetableExpense</c>
    /// (debit, not an income category, not a transfer / credit-card payment). Split parents are
    /// already filtered out before the DTO is produced.
    /// </summary>
    private static bool IsExpense(TransactionDto t)
    {
        if (t.Type != TransactionType.Debit)
        {
            return false;
        }

        if (t.CategoryId.HasValue)
        {
            return !TransactionAmountExtensions.IsIncomeCategoryId(t.CategoryId.Value)
                && !TransactionAmountExtensions.IsNonBudgetableCategoryId(t.CategoryId.Value);
        }

        return true;
    }

    private static string DescribeUncertainty(TransactionDto t)
    {
        var source = string.IsNullOrWhiteSpace(t.CategorySource) ? "the import" : t.CategorySource;

        if (t.NeedsReview)
        {
            return t.AIConfidence.HasValue
                ? $"flagged for review (AI confidence {(t.AIConfidence.Value * 100).ToString("F0", CultureInfo.InvariantCulture)}%)"
                : $"flagged for review, category from {source}";
        }

        return $"category from {source}, not yet confirmed";
    }
}
