using System.Text.Json.Serialization;

namespace BuoyAI.Core.Receipts;

/// <summary>What the model read out of one forwarded email. Every number here came from the email.</summary>
public class ParsedReceipt
{
  [JsonPropertyName("isReceipt")]
  public bool IsReceipt { get; set; }

  /// <summary>A <see cref="RetailerDirectory"/> key ("walmart", "amazon"...) or a plain store name.</summary>
  [JsonPropertyName("retailer")]
  public string? Retailer { get; set; }

  [JsonPropertyName("orderNumber")]
  public string? OrderNumber { get; set; }

  [JsonPropertyName("orderDate")]
  public DateTime? OrderDate { get; set; }

  /// <summary>order, shipment, delivered, in_store, refund or other. Refunds are not split.</summary>
  [JsonPropertyName("kind")]
  public string Kind { get; set; } = "order";

  [JsonPropertyName("items")]
  public List<ParsedReceiptItem> Items { get; set; } = new();

  [JsonPropertyName("subtotal")]
  public decimal? Subtotal { get; set; }

  [JsonPropertyName("tax")]
  public decimal? Tax { get; set; }

  [JsonPropertyName("shipping")]
  public decimal? Shipping { get; set; }

  [JsonPropertyName("discount")]
  public decimal? Discount { get; set; }

  /// <summary>Delivery, service and bag fees and tips (a Walmart delivery's driver tip, for one).</summary>
  [JsonPropertyName("fees")]
  public decimal? Fees { get; set; }

  /// <summary>The files show more than one order. They're sent back to be shared one order at a time.</summary>
  [JsonPropertyName("multipleOrders")]
  public bool MultipleOrders { get; set; }

  [JsonPropertyName("total")]
  public decimal? Total { get; set; }

  /// <summary>The 3-letter currency the prices are in (USD), or null when the receipt doesn't say.</summary>
  [JsonPropertyName("currency")]
  public string? Currency { get; set; }

  /// <summary>Card charges the receipt lists separately (Amazon shipments), if any.</summary>
  [JsonPropertyName("charges")]
  public List<decimal> Charges { get; set; } = new();

  /// <summary>Items plus tax, shipping and fees, less discounts: what the total should be.</summary>
  public decimal ComputedTotal() =>
    Items.Sum(i => i.Price) + (Tax ?? 0) + (Shipping ?? 0) + (Fees ?? 0) - Math.Abs(Discount ?? 0);

  /// <summary>Do the parts add up to the total within a few cents? Read cleanly, nothing missing.</summary>
  public bool AddsUp() => Total is > 0 && Items.Count > 0 && Math.Abs(ComputedTotal() - Total.Value) <= 0.05m;

  /// <summary>
  /// Far enough off the total that part of the order is missing: a long order whose screenshots
  /// skipped a stretch of the list. Splitting by the items shown would put the missing items'
  /// money in the wrong categories, so the receipt is sent back. Small gaps (a rounding, a fee the
  /// model missed) are tolerated: 2% or a dollar, whichever is more.
  /// </summary>
  public bool LooksIncomplete() =>
    Total is > 0 && Math.Abs(ComputedTotal() - Total.Value) > Math.Max(1m, Total.Value * 0.02m);
}

public class ParsedReceiptItem
{
  [JsonPropertyName("name")]
  public string Name { get; set; } = string.Empty;

  [JsonPropertyName("quantity")]
  public int Quantity { get; set; } = 1;

  /// <summary>The line's total (quantity times unit price, after any item discount).</summary>
  [JsonPropertyName("price")]
  public decimal Price { get; set; }

  [JsonPropertyName("categoryId")]
  public int? CategoryId { get; set; }
}

/// <summary>The split BuoyAI offers for a receipt: one entry per matched bank charge.</summary>
public class ReceiptProposal
{
  /// <summary>How the charge was found: exact, charges, sum or scaled (see <see cref="ReceiptMatcher"/>).</summary>
  [JsonPropertyName("method")]
  public string Method { get; set; } = string.Empty;

  /// <summary>Safe to apply without asking: an exact amount match on a receipt that adds up.</summary>
  [JsonPropertyName("confident")]
  public bool Confident { get; set; }

  [JsonPropertyName("charges")]
  public List<ProposedCharge> Charges { get; set; } = new();
}

public class ProposedCharge
{
  [JsonPropertyName("transactionId")]
  public int TransactionId { get; set; }

  [JsonPropertyName("amount")]
  public decimal Amount { get; set; }

  [JsonPropertyName("date")]
  public DateTime Date { get; set; }

  [JsonPropertyName("splits")]
  public List<PlannedSplit> Splits { get; set; } = new();
}

public class PlannedSplit
{
  [JsonPropertyName("categoryId")]
  public int CategoryId { get; set; }

  [JsonPropertyName("categoryName")]
  public string? CategoryName { get; set; }

  [JsonPropertyName("amount")]
  public decimal Amount { get; set; }

  [JsonPropertyName("items")]
  public List<string> Items { get; set; } = new();

  /// <summary>The note each split part gets, e.g. "Milk, Bananas, Bread +3 more".</summary>
  public string Note(int maxNames = 3)
  {
    var note = string.Join(", ", Items.Take(maxNames));
    return Items.Count > maxNames ? $"{note} +{Items.Count - maxNames} more" : note;
  }
}
