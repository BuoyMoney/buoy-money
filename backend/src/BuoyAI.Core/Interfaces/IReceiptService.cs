using BuoyAI.Core.DTOs;
using BuoyAI.Core.Receipts;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Receipt scanning: the user shares a screenshot of an order page, a photo of a paper receipt or a
/// PDF; BuoyAI reads the items, finds the bank charge and offers a split by category. See
/// docs/receipt_scanning.md.
/// </summary>
public interface IReceiptService
{
  Task<ReceiptStatusDto> GetStatusAsync(int userId);

  /// <summary>
  /// Reads, matches and records one receipt (one to four files showing one order). Throws
  /// <see cref="ReceiptScanBlockedException"/> when the user can't scan, and
  /// <see cref="ReceiptFileException"/> for files that aren't usable images or PDFs.
  /// </summary>
  Task<ReceiptScanDto> ScanAsync(int userId, IReadOnlyList<ReceiptFile> files, int? hintTransactionId, CancellationToken ct = default);

  Task<List<ReceiptScanDto>> GetScansAsync(int userId, int limit = 30);

  Task<ReceiptScanDto?> GetScanAsync(int userId, int scanId);

  /// <summary>The ready or applied receipt for a transaction, if one matched it.</summary>
  Task<ReceiptScanDto?> GetScanForTransactionAsync(int userId, int transactionId);

  /// <summary>Applies a ready receipt's split. Null when it isn't the user's or isn't ready.</summary>
  Task<ReceiptScanDto?> ApplyAsync(int userId, int scanId);

  Task<ReceiptScanDto?> DismissAsync(int userId, int scanId);
}

/// <summary>The files sent aren't a usable receipt upload; the message is written for the user.</summary>
public class ReceiptFileException : Exception
{
  public ReceiptFileException(string message)
    : base(message) { }
}

/// <summary>This user can't scan receipts; <see cref="Reason"/> says why.</summary>
public class ReceiptScanBlockedException : Exception
{
  public string Reason { get; }

  public ReceiptScanBlockedException(string reason, string message)
    : base(message)
  {
    Reason = reason;
  }
}
