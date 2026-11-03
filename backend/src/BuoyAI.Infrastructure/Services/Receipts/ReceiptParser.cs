using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BuoyAI.Core.Enums;
using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Receipts;
using BuoyAI.Core.Utilities;
using BuoyAI.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services.Receipts;

public interface IReceiptParser
{
  /// <summary>
  /// Reads a receipt from screenshots, photos or a PDF, with each item put in one of
  /// <paramref name="categories"/> (id to name). Null when the model's answer can't be used.
  /// </summary>
  Task<ParsedReceipt?> ParseAsync(
    IReadOnlyList<ReceiptFile> files,
    DateTime receivedAt,
    IReadOnlyDictionary<int, string> categories
  );
}

/// <summary>Gemini reads the receipt; every number it returns must be printed on it.</summary>
public class ReceiptParser : IReceiptParser
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
  };

  private readonly IGeminiApiClient _gemini;
  private readonly AIModelConfiguration _models;
  private readonly ILogger<ReceiptParser> _logger;

  public ReceiptParser(IGeminiApiClient gemini, AIModelConfiguration models, ILogger<ReceiptParser> logger)
  {
    _gemini = gemini;
    _models = models;
    _logger = logger;
  }

  public async Task<ParsedReceipt?> ParseAsync(
    IReadOnlyList<ReceiptFile> files,
    DateTime receivedAt,
    IReadOnlyDictionary<int, string> categories
  )
  {
    var prompt = BuildPrompt(files.Count, receivedAt, categories);
    var reply = await _gemini.GenerateContentAsync(
      prompt,
      _models.GetPrimaryModel(SubscriptionTier.Premium),
      files.Select(f => (f.MimeType, f.Data)).ToList()
    );
    var parsed = Parse(reply, categories.Keys.ToHashSet());
    if (parsed == null)
      _logger.LogWarning("Receipt parse: model reply was not usable JSON ({Length} chars)", reply?.Length ?? 0);
    return parsed;
  }

  internal static string BuildPrompt(int fileCount, DateTime receivedAt, IReadOnlyDictionary<int, string> categories)
  {
    var sb = new StringBuilder();
    sb.AppendLine("You read store receipts for a personal budgeting app. The user sent the attached file(s) so the");
    sb.AppendLine("app can split the matching bank charge by category. They are a screenshot of an order page in a");
    sb.AppendLine("store's app or website, an order or invoice page saved as PDF, or a photo of a paper receipt.");
    sb.AppendLine($"There are {fileCount} file(s), meant to show ONE order. Return ONLY a JSON object:");
    sb.AppendLine(
      "{\"isReceipt\":bool,\"multipleOrders\":bool,\"retailer\":string,\"orderNumber\":string|null,"
        + "\"orderDate\":\"YYYY-MM-DD\"|null,\"kind\":\"order|shipment|delivered|in_store|refund|other\","
        + "\"items\":[{\"name\":string,\"quantity\":int,\"price\":number,\"categoryId\":int}],\"subtotal\":number|null,"
        + "\"tax\":number|null,\"shipping\":number|null,\"fees\":number|null,\"discount\":number|null,"
        + "\"total\":number|null,\"currency\":string|null,\"charges\":[number]}"
    );
    if (fileCount > 1)
    {
      sb.AppendLine();
      sb.AppendLine("Several screenshots of one long receipt:");
      sb.AppendLine("- They are consecutive parts of the same page, usually top to bottom, though maybe not in order.");
      sb.AppendLine("  The store and order number may appear only on the first, the totals only on the last.");
      sb.AppendLine("  Piece them together into one list of items.");
      sb.AppendLine("- Consecutive screenshots often overlap: the last items of one are the first items of the next.");
      sb.AppendLine("  Count each item once. Only list an item twice if the receipt itself lists it twice (for example");
      sb.AppendLine("  the same product bought in two separate lines, both visible on one screenshot).");
      sb.AppendLine("- They may also not overlap at all; that's fine, list everything shown.");
      sb.AppendLine("- If the files show two or more different orders (different order numbers, dates, stores or");
      sb.AppendLine("  totals), set multipleOrders true and stop.");
    }
    sb.AppendLine();
    sb.AppendLine("Rules:");
    sb.AppendLine("- isReceipt is true only when the files show purchased items with prices and an order total.");
    sb.AppendLine("  Anything else (a product page, a cart, a photo that isn't a receipt): isReceipt false, items [].");
    sb.AppendLine("- If the files show only part of a long order, still list what is shown and the total if visible;");
    sb.AppendLine("  the app checks whether the items add up.");
    sb.AppendLine("- Copy every number from the files. Never estimate or invent a price, total or date.");
    sb.AppendLine("- price is the line total for that item (quantity times unit price, after any item discount).");
    sb.AppendLine("- discount is a positive number for order-level savings (coupons, promotions), else null.");
    sb.AppendLine("- fees: delivery, service and bag fees and tips added on top, else null. shipping is shipping only.");
    sb.AppendLine("- When an order changed (substitutions, weighed items, removed items), use the final items and");
    sb.AppendLine("  final total shown.");
    sb.AppendLine("- charges: only when the files list separate card charges (for example one per shipment); else [].");
    sb.AppendLine("- retailer: one of amazon, walmart, samsclub, target, costco, instacart, or the store's own name.");
    sb.AppendLine("- orderDate: the order or purchase date shown, else null.");
    sb.AppendLine("- currency: the 3-letter code of the currency the prices are in (USD for $ in the US), else null.");
    sb.AppendLine("- Ignore anything else on screen (notifications, status bar, card numbers, addresses).");
    sb.AppendLine("- categoryId: the best category for each item, from this list only (id: name):");
    foreach (var (id, name) in categories.OrderBy(c => c.Key))
      sb.AppendLine($"  {id}: {name}");
    sb.AppendLine("  Food and drink for home, cleaning supplies, paper goods and other household basics are Groceries");
    sb.AppendLine("  unless the list has a closer category (for example Household or Pets).");
    sb.AppendLine();
    sb.AppendLine($"Sent {receivedAt:yyyy-MM-dd}.");
    return sb.ToString();
  }

  internal static ParsedReceipt? Parse(string? reply, ISet<int> allowedCategoryIds)
  {
    if (string.IsNullOrWhiteSpace(reply))
      return null;
    var start = reply.IndexOf('{');
    var end = reply.LastIndexOf('}');
    if (start < 0 || end <= start)
      return null;
    ParsedReceipt? r;
    try
    {
      r = JsonSerializer.Deserialize<ParsedReceipt>(reply[start..(end + 1)], JsonOptions);
    }
    catch (JsonException)
    {
      return null;
    }
    if (r == null)
      return null;

    r.Items = (r.Items ?? new())
      .Where(i => !string.IsNullOrWhiteSpace(i.Name))
      .Select(i =>
      {
        i.Name = SafeText.Truncate(i.Name.Trim(), 80);
        i.CategoryId = i.CategoryId is int c && allowedCategoryIds.Contains(c) ? c : null;
        i.Quantity = Math.Max(1, i.Quantity);
        return i;
      })
      .Take(200)
      .ToList();
    r.Charges = (r.Charges ?? new()).Where(c => c > 0).ToList();
    r.Kind = string.IsNullOrWhiteSpace(r.Kind) ? "order" : r.Kind.Trim().ToLowerInvariant();
    if (r.Total is <= 0)
      r.Total = null;
    var currency = r.Currency?.Trim().ToUpperInvariant();
    r.Currency = currency is { Length: 3 } && currency.All(char.IsAsciiLetterUpper) ? currency : null;
    return r;
  }
}
