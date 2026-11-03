namespace BuoyAI.Core.Receipts;

/// <summary>
/// Turns a receipt's items into category splits for one bank charge. Items are grouped by
/// category and the charge is shared out by each group's share of the item prices, so tax,
/// shipping, discounts and any price change at delivery are spread the same way. Parts are
/// rounded to the cent with the leftover cents going to the largest remainders, so they always
/// add up to the charge exactly.
/// </summary>
public static class ReceiptSplitPlanner
{
  /// <summary>Used when the model gave an item no category.</summary>
  public const int FallbackCategoryId = 24; // Shopping

  public const int MaxSplits = 10;

  private record Group(int CategoryId, decimal Weight, List<string> Items);

  public static List<PlannedSplit> Plan(
    ParsedReceipt receipt,
    decimal chargeAmount,
    IReadOnlyDictionary<int, string>? names = null
  )
  {
    var charge = Math.Abs(chargeAmount);
    var groups = receipt
      .Items.Where(i => !string.IsNullOrWhiteSpace(i.Name))
      .GroupBy(i => i.CategoryId is > 0 ? i.CategoryId.Value : FallbackCategoryId)
      .Select(g => new Group(
        g.Key,
        g.Sum(i => i.Price),
        g.Where(i => i.Price > 0).Select(i => i.Name.Trim()).ToList()
      ))
      .Where(g => g.Weight > 0)
      .OrderByDescending(g => g.Weight)
      .ToList();
    if (groups.Count == 0 || charge <= 0)
      return new List<PlannedSplit>();

    // Too many small groups: fold the smallest into one part under the biggest of them.
    if (groups.Count > MaxSplits)
    {
      var rest = groups.Skip(MaxSplits - 1).ToList();
      groups = groups
        .Take(MaxSplits - 1)
        .Append(new Group(rest[0].CategoryId, rest.Sum(g => g.Weight), rest.SelectMany(g => g.Items).ToList()))
        .ToList();
    }

    var totalWeight = groups.Sum(g => g.Weight);
    var cents = (long)Math.Round(charge * 100m, MidpointRounding.AwayFromZero);
    var shares = groups.Select(g => cents * g.Weight / totalWeight).ToList();
    var parts = shares.Select(s => (long)Math.Floor(s)).ToList();
    var leftover = (int)(cents - parts.Sum());
    var byRemainder = shares
      .Select((s, i) => (rem: s - Math.Floor(s), i))
      .OrderByDescending(x => x.rem)
      .ThenBy(x => x.i)
      .Take(leftover);
    foreach (var (_, i) in byRemainder)
      parts[i]++;

    return groups
      .Select(
        (g, i) =>
          new PlannedSplit
          {
            CategoryId = g.CategoryId,
            CategoryName = names != null && names.TryGetValue(g.CategoryId, out var n) ? n : null,
            Amount = parts[i] / 100m,
            Items = g.Items,
          }
      )
      .Where(s => s.Amount > 0)
      .ToList();
  }

  /// <summary>The full offer: a plan for every matched charge.</summary>
  public static ReceiptProposal Propose(
    ParsedReceipt receipt,
    ReceiptMatch match,
    IReadOnlyDictionary<int, string>? names = null
  ) =>
    new()
    {
      Method = match.Method,
      Confident = match.Method is "exact" or "charges" && receipt.AddsUp(),
      Charges = match
        .Charges.Select(c => new ProposedCharge
        {
          TransactionId = c.Id,
          Amount = Math.Abs(c.Amount),
          Date = c.Date,
          Splits = Plan(receipt, c.Amount, names),
        })
        .ToList(),
    };
}
