namespace BuoyAI.Core.Receipts;

/// <summary>
/// The stores receipt scanning knows by name, and how their charges look on a bank statement.
/// Any other store works too, matched by the name printed on the receipt.
/// </summary>
public static class RetailerDirectory
{
  public record Retailer(string Key, string Name, string[] MerchantPatterns);

  public static readonly IReadOnlyList<Retailer> All = new[]
  {
    new Retailer("amazon", "Amazon", new[] { "amazon", "amzn" }),
    new Retailer("walmart", "Walmart", new[] { "walmart", "wal-mart", "wal mart", "wm supercenter", "wm.com" }),
    new Retailer("samsclub", "Sam's Club", new[] { "sams club", "sam's club", "samsclub", "sams scan" }),
    new Retailer("target", "Target", new[] { "target" }),
    new Retailer("costco", "Costco", new[] { "costco" }),
    new Retailer("instacart", "Instacart", new[] { "instacart" }),
  };

  public static Retailer? ByKey(string? key)
  {
    if (string.IsNullOrWhiteSpace(key))
      return null;
    var k = key.Trim();
    return All.FirstOrDefault(r =>
      r.Key.Equals(k, StringComparison.OrdinalIgnoreCase) || r.Name.Equals(k, StringComparison.OrdinalIgnoreCase)
    );
  }

  /// <summary>Words a receipt's store name carries that the bank's merchant name often doesn't.</summary>
  private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
  {
    "the", "market", "markets", "store", "stores", "inc", "llc", "co", "company", "supermarket",
    "supercenter", "superstore", "grocery", "groceries", "foodstore", "shop", "online",
  };

  /// <summary>
  /// A known store, or for any other store one matched by the name on the receipt, without filler
  /// words ("Whole Foods Market" matches a bank's "WHOLE FOODS #10234").
  /// </summary>
  public static Retailer? Resolve(string? name)
  {
    var known = ByKey(name);
    if (known != null)
      return known;
    var n = name?.Trim();
    if (string.IsNullOrEmpty(n))
      return null;
    var core = string.Join(' ', Normalize(n).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !FillerWords.Contains(w)));
    return core.Length < 3 ? null : new Retailer(core, n, new[] { core });
  }

  /// <summary>Does a bank charge's merchant name or description belong to this store?</summary>
  public static bool MatchesMerchant(Retailer retailer, string? merchantName, string? description)
  {
    var text = Normalize($"{merchantName} {description}");
    return retailer.MerchantPatterns.Any(p => text.Contains(Normalize(p)));
  }

  private static string Normalize(string s) =>
    new string(
      s.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) || c is ' ' or '\'' or '-' or '.' ? c : ' ')
        .ToArray()
    );
}
