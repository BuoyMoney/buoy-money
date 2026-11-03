using System.Globalization;
using BuoyAI.Core.Enums;
using BuoyAI.Core.ValueObjects;

namespace BuoyAI.Core.Analysis;

/// <summary>How strongly a suspected transfer is evidenced.</summary>
public enum TransferConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// The minimal projection of a transaction the transfer detector needs. Kept independent of the
/// DTO/entity so the detection logic is pure and unit-testable.
/// </summary>
public record TransferCandidate(
    int Id,
    int AccountId,
    int OwnerUserId,
    string AccountName,
    decimal Amount,
    TransactionType Type,
    DateTime Date,
    int? CategoryId,
    bool IsUserCategorized,
    bool IsSplit,
    string? PlaidPrimary,
    string? PlaidDetailed,
    string MerchantName,
    // True once the user dismissed this leg as "not a transfer". Tracked separately from
    // IsUserCategorized so dismissing a suggestion doesn't pull an uncategorized transaction
    // out of the normal categorization queue. Optional so unrelated callers/tests need not set it.
    bool DismissedAsTransfer = false);

/// <summary>One leg of a suspected transfer, for display/confirmation.</summary>
public record SuspectedTransferSide(
    int TransactionId,
    int AccountId,
    string AccountName,
    decimal Amount,
    DateTime Date,
    string MerchantName);

/// <summary>
/// A transaction (or a pair of transactions) that looks like an internal transfer but is NOT
/// categorized as one, so it is inflating the user's spending. Surfaced for the user to confirm —
/// never auto-applied.
/// </summary>
public record SuspectedTransfer(
    TransferConfidence Confidence,
    string Reason,
    IReadOnlyList<SuspectedTransferSide> Sides);

public class TransferDetectionOptions
{
    /// <summary>Max days between the two legs of a matched pair.</summary>
    public int WindowDays { get; init; } = 3;

    /// <summary>Allowed difference between the two legs' magnitudes (0 = exact — the safe default).</summary>
    public decimal AmountTolerance { get; init; } = 0m;

    /// <summary>Ignore movements below this magnitude (tiny exact matches are more likely coincidence).</summary>
    public decimal MinAmount { get; init; } = 1m;

    /// <summary>Cap on how many suspects to surface at once.</summary>
    public int MaxResults { get; init; } = 25;
}

/// <summary>
/// Finds likely internal transfers that were NOT tagged as transfers, using only honest signals
/// (deliberately NOT transaction size on its own):
///  1. PRIMARY — a matched debit/credit pair across the user's OWN accounts: the same magnitude
///     leaves one account and arrives in another within a few days. Two opposite-direction
///     movements of an identical amount on different accounts is about as clean a transfer signal
///     as exists.
///  2. BOOSTER — Plaid's personal_finance_category tagged the leg TRANSFER_IN / TRANSFER_OUT even
///     though our category mapping flattened it to something spendable.
///
/// LOAN_PAYMENTS is intentionally excluded — those are real obligations, not inter-account moves.
/// Nothing here reclassifies anything; every result is a suggestion for the user to confirm.
/// </summary>
public static class TransferPairDetector
{
    public static IReadOnlyList<SuspectedTransfer> Detect(
        IReadOnlyList<TransferCandidate> candidates,
        TransferDetectionOptions? options = null)
    {
        options ??= new TransferDetectionOptions();

        // Candidates: not already a transfer, not user-confirmed (respect explicit choices),
        // not a split parent, and not income. Excluding income is critical — otherwise an
        // auto-categorized paycheck (a Salary credit) could be matched against an unrelated
        // debit of the same amount and wrongly suggested as an internal transfer.
        var eligible = candidates
            .Where(c => !c.IsUserCategorized
                        && !c.DismissedAsTransfer
                        && !c.IsSplit
                        && c.CategoryId != (int)TransactionCategory.Transfer
                        && !(c.CategoryId.HasValue && TransactionAmountExtensions.IsIncomeCategoryId(c.CategoryId.Value)))
            .ToList();

        var results = new List<SuspectedTransfer>();
        var used = new HashSet<int>();

        // 1) Matched debit/credit pairs across different accounts.
        var credits = eligible.Where(c => c.Type == TransactionType.Credit).OrderBy(c => c.Date).ToList();

        foreach (var debit in eligible.Where(c => c.Type == TransactionType.Debit).OrderBy(c => c.Date))
        {
            if (used.Contains(debit.Id) || Math.Abs(debit.Amount) < options.MinAmount)
            {
                continue;
            }

            SuspectedTransferSide? DebitSide() => ToSide(debit);

            var match = credits
                .Where(cr => !used.Contains(cr.Id)
                             && cr.AccountId != debit.AccountId
                             // Both legs must belong to the SAME person's accounts. In a shared
                             // family view the transaction set spans members, and without this two
                             // unrelated members' opposite same-amount transactions would be paired.
                             && cr.OwnerUserId == debit.OwnerUserId
                             && Math.Abs(Math.Abs(cr.Amount) - Math.Abs(debit.Amount)) <= options.AmountTolerance
                             && Math.Abs((cr.Date.Date - debit.Date.Date).TotalDays) <= options.WindowDays)
                .OrderBy(cr => Math.Abs((cr.Date - debit.Date).TotalHours))
                .FirstOrDefault();

            if (match == null)
            {
                continue;
            }

            used.Add(debit.Id);
            used.Add(match.Id);

            var pfcAgrees = IsPfcTransfer(debit) || IsPfcTransfer(match);
            results.Add(new SuspectedTransfer(
                Confidence: pfcAgrees ? TransferConfidence.High : TransferConfidence.Medium,
                Reason: pfcAgrees
                    ? "The same amount moved between two of your accounts and your bank tagged it as a transfer — it looks like an internal transfer, not spending."
                    : $"${Math.Abs(debit.Amount).ToString("F2", CultureInfo.InvariantCulture)} left {debit.AccountName} and the same amount arrived in {match.AccountName} within {options.WindowDays} days — this looks like a transfer between your accounts, not spending.",
                Sides: new[] { DebitSide()!, ToSide(match)! }));
        }

        // 2) Plaid-tagged transfers with no visible counterpart (e.g. transfer to an external account).
        foreach (var c in eligible
                     .Where(c => !used.Contains(c.Id) && IsPfcTransfer(c) && Math.Abs(c.Amount) >= options.MinAmount)
                     .OrderByDescending(c => Math.Abs(c.Amount)))
        {
            used.Add(c.Id);
            results.Add(new SuspectedTransfer(
                Confidence: TransferConfidence.Low,
                Reason: c.Type == TransactionType.Credit
                    ? "Your bank classified this as a transfer, but it's currently counted as income."
                    : "Your bank classified this as a transfer, but it's currently counted as spending.",
                Sides: new[] { ToSide(c)! }));
        }

        return results
            .OrderByDescending(r => r.Confidence)
            .ThenByDescending(r => r.Sides.Max(s => Math.Abs(s.Amount)))
            .Take(options.MaxResults)
            .ToList();
    }

    /// <summary>True when Plaid's PFC tagged this leg as an inter-account transfer (not a loan payment).</summary>
    internal static bool IsPfcTransfer(TransferCandidate c)
    {
        const StringComparison oic = StringComparison.OrdinalIgnoreCase;

        if (!string.IsNullOrWhiteSpace(c.PlaidPrimary) &&
            (c.PlaidPrimary.Equals("TRANSFER_IN", oic) || c.PlaidPrimary.Equals("TRANSFER_OUT", oic)))
        {
            return true;
        }

        // Detailed codes look like TRANSFER_OUT_ACCOUNT_TRANSFER, TRANSFER_IN_DEPOSIT, etc.
        if (!string.IsNullOrWhiteSpace(c.PlaidDetailed) &&
            (c.PlaidDetailed.StartsWith("TRANSFER_IN", oic) || c.PlaidDetailed.StartsWith("TRANSFER_OUT", oic)))
        {
            return true;
        }

        return false;
    }

    private static SuspectedTransferSide ToSide(TransferCandidate c)
        => new(c.Id, c.AccountId, c.AccountName, c.Amount, c.Date,
               string.IsNullOrWhiteSpace(c.MerchantName) ? "(no merchant)" : c.MerchantName);
}
