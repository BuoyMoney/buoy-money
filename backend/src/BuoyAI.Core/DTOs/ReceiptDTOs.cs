using BuoyAI.Core.Receipts;

namespace BuoyAI.Core.DTOs;

/// <summary>Whether this user can scan receipts, and how many splits are waiting.</summary>
public class ReceiptStatusDto
{
  /// <summary>Why not (premium_required, ai_consent_required), or null when they can.</summary>
  public string? Blocker { get; set; }

  public int ReadyCount { get; set; }

  /// <summary>The stores receipt scanning knows by name.</summary>
  public List<string> Stores { get; set; } = new();
}

/// <summary>One receipt the user sent in and what became of it.</summary>
public class ReceiptScanDto
{
  public int Id { get; set; }
  public string Status { get; set; } = string.Empty;
  public string? Retailer { get; set; }
  public string? OrderNumber { get; set; }
  public DateTime? OrderDate { get; set; }
  public decimal? Total { get; set; }
  public DateTime ReceivedAt { get; set; }
  public DateTime? AppliedAt { get; set; }
  public int? TransactionId { get; set; }

  /// <summary>A sentence for the user: what's happening or what went wrong.</summary>
  public string? Message { get; set; }

  public int ItemCount { get; set; }
  public ReceiptProposal? Proposal { get; set; }
}
