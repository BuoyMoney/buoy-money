namespace BuoyAI.Core.Receipts;

/// <summary>
/// A bank charge the matcher may pair with a receipt. <see cref="IsExpense"/> comes from the
/// transaction type (Debit): amounts are stored positive for both directions, so the sign says nothing.
/// </summary>
public record ReceiptCandidate(
  int Id,
  decimal Amount,
  bool IsExpense,
  DateTime Date,
  string? MerchantName,
  string? Description,
  bool IsPending,
  bool IsSplit,
  bool IsSplitChild,
  string? Currency = null
);

public record ReceiptMatch(string Method, List<ReceiptCandidate> Charges);

/// <summary>
/// Finds the bank charge(s) a receipt paid for. Pure, so the rules are tested without a database.
///
/// A charge qualifies when it's an expense from the same store, posted (a pending hold changes
/// amount when it settles), not already split, and dated from 3 days before the order to 10 days
/// after the later of the order and the email (Amazon charges when it ships). Then, in order:
///   exact   - one charge equals the total to the cent;
///   charges - the email lists its card charges and each equals a different charge;
///   sum     - two or three charges add up to the total (an order shipped in parts);
///   scaled  - exactly one charge within 15% of the total (Walmart substitutions and weighed
///             produce change the final amount); the split is scaled to the real charge.
/// More than one equally good answer is no answer: a wrong guess would split a different purchase.
/// When the user scanned the receipt from a transaction, that charge is tried first. A charge in a
/// different currency from the receipt never matches (when both are known).
/// </summary>
public static class ReceiptMatcher
{
  public const int DaysBefore = 3;
  public const int DaysAfter = 10;
  public const decimal ScaledTolerance = 0.15m;
  private const int MaxSumPool = 25;

  private static bool SameCurrency(ParsedReceipt receipt, ReceiptCandidate c) =>
    string.IsNullOrEmpty(receipt.Currency)
    || string.IsNullOrEmpty(c.Currency)
    || string.Equals(receipt.Currency, c.Currency, StringComparison.OrdinalIgnoreCase);

  public static ReceiptMatch? Match(
    ParsedReceipt receipt,
    RetailerDirectory.Retailer retailer,
    IEnumerable<ReceiptCandidate> candidates,
    DateTime receivedAt,
    int? hintTransactionId = null
  )
  {
    if (!receipt.IsReceipt || receipt.Total is not > 0 || receipt.Kind == "refund")
      return null;
    var total = receipt.Total.Value;

    // Scanned from a transaction: that charge wins when its amount fits (exactly, or within the
    // 15% a changed order allows), whatever the bank calls the merchant.
    var hinted = hintTransactionId == null ? null : candidates.FirstOrDefault(c => c.Id == hintTransactionId);
    if (hinted != null && hinted.IsExpense && !hinted.IsPending && !hinted.IsSplit && !hinted.IsSplitChild
        && SameCurrency(receipt, hinted))
    {
      if (Same(hinted.Amount, total))
        return new ReceiptMatch("exact", new() { hinted });
      if (Math.Abs(Math.Abs(hinted.Amount) - total) <= total * ScaledTolerance)
        return new ReceiptMatch("scaled", new() { hinted });
    }
    var anchor = (receipt.OrderDate ?? receivedAt).Date;
    var from = anchor.AddDays(-DaysBefore);
    var to = (receivedAt.Date > anchor ? receivedAt.Date : anchor).AddDays(DaysAfter);

    var pool = candidates
      .Where(c => c.IsExpense && !c.IsPending && !c.IsSplit && !c.IsSplitChild)
      .Where(c => SameCurrency(receipt, c))
      .Where(c => c.Date.Date >= from && c.Date.Date <= to)
      .Where(c => RetailerDirectory.MatchesMerchant(retailer, c.MerchantName, c.Description))
      .ToList();
    if (pool.Count == 0)
      return null;

    int Distance(ReceiptCandidate c) => Math.Abs((c.Date.Date - anchor).Days);

    var exact = pool.Where(c => Same(c.Amount, total)).OrderBy(Distance).ToList();
    if (exact.Count == 1)
      return new ReceiptMatch("exact", exact);
    if (exact.Count > 1)
      // Two identical charges equally close to the order: no way to tell which one.
      return Distance(exact[0]) < Distance(exact[1]) ? new ReceiptMatch("exact", new() { exact[0] }) : null;

    if (receipt.Charges.Count > 1)
    {
      var used = new List<ReceiptCandidate>();
      var ambiguous = false;
      foreach (var charge in receipt.Charges)
      {
        var hits = pool.Where(c => !used.Contains(c) && Same(c.Amount, charge)).OrderBy(Distance).Take(2).ToList();
        if (hits.Count == 0)
          break;
        if (hits.Count == 2 && Distance(hits[0]) == Distance(hits[1]))
        {
          ambiguous = true;
          break;
        }
        used.Add(hits[0]);
      }
      if (ambiguous)
        return null;
      if (used.Count == receipt.Charges.Count)
        return new ReceiptMatch("charges", used);
    }

    var ordered = pool.OrderBy(c => c.Date).ThenBy(c => c.Id).Take(MaxSumPool).ToList();
    var sums = Subsets(ordered).Where(s => Same(s.Sum(c => c.Amount), total)).Take(2).ToList();
    if (sums.Count == 1)
      return new ReceiptMatch("sum", sums[0]);
    if (sums.Count > 1)
      return null;

    var near = pool.Where(c => Math.Abs(Math.Abs(c.Amount) - total) <= total * ScaledTolerance).ToList();
    return near.Count == 1 ? new ReceiptMatch("scaled", near) : null;
  }

  // Exact to the cent: money amounts are decimals, and near misses are the scaled rule's job.
  private static bool Same(decimal a, decimal b) => Math.Abs(a) == Math.Abs(b);

  /// <summary>Every pair and triple, in date order.</summary>
  private static IEnumerable<List<ReceiptCandidate>> Subsets(List<ReceiptCandidate> items)
  {
    for (var i = 0; i < items.Count; i++)
    for (var j = i + 1; j < items.Count; j++)
    {
      yield return new List<ReceiptCandidate> { items[i], items[j] };
      for (var k = j + 1; k < items.Count; k++)
        yield return new List<ReceiptCandidate> { items[i], items[j], items[k] };
    }
  }
}
