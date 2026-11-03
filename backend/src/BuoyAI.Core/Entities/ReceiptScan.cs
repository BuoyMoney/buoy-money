namespace BuoyAI.Core.Entities;

/// <summary>
/// One receipt a user sent in (a shared screenshot of an order page, a photo of a paper receipt,
/// or a PDF), and what became of it. The image itself is never stored: only the parsed store,
/// items and totals (<see cref="ParsedJson"/>) and the split we offered (<see cref="ProposalJson"/>).
/// </summary>
public class ReceiptScan
{
  public int Id { get; set; }
  public int UserId { get; set; }
  public User User { get; set; } = null!;

  public string Status { get; set; } = ReceiptScanStatus.Unmatched;

  public string? Retailer { get; set; }
  public string? OrderNumber { get; set; }
  public DateTime? OrderDate { get; set; }
  public decimal? Total { get; set; }

  /// <summary>The parsed receipt (<see cref="Receipts.ParsedReceipt"/>) as JSON.</summary>
  public string? ParsedJson { get; set; }

  /// <summary>The offered split (<see cref="Receipts.ReceiptProposal"/>) as JSON.</summary>
  public string? ProposalJson { get; set; }

  /// <summary>The first matched charge. An Amazon order can match several; all are in the proposal.</summary>
  public int? TransactionId { get; set; }

  /// <summary>The charge the user scanned it from, if any; matching tries it first.</summary>
  public int? HintTransactionId { get; set; }

  public string? Error { get; set; }
  public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
  public DateTime? MatchedAt { get; set; }
  public DateTime? AppliedAt { get; set; }
  public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
  public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Where a receipt is in its life. Stored as text so the app can show it as-is.</summary>
public static class ReceiptScanStatus
{
  /// <summary>Read, but no bank charge matches yet. Retried as charges sync in.</summary>
  public const string Unmatched = "unmatched";

  /// <summary>Matched; a split is waiting for the user.</summary>
  public const string Ready = "ready";

  /// <summary>An Apply is in progress; claimed so two Applies can't run at once.</summary>
  public const string Applying = "applying";

  public const string Applied = "applied";
  public const string Dismissed = "dismissed";

  /// <summary>The image didn't show a receipt with item prices and a total.</summary>
  public const string NotAReceipt = "not_a_receipt";

  /// <summary>No charge matched within the wait window.</summary>
  public const string Expired = "expired";

  public static readonly string[] Open = { Unmatched, Ready };
}
