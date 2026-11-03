using BuoyAI.Core.DTOs.Import;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Two-step file import. <see cref="PreviewAsync"/> reads and classifies a file without writing
/// anything; <see cref="CommitAsync"/> writes what the user confirmed through the shared
/// transaction ingest path; <see cref="UndoAsync"/> takes a batch back out.
/// </summary>
public interface IImportService
{
  /// <summary>
  /// Parses the file(s), classifies every row against what is already stored, and stages the
  /// result for 30 minutes under the returned import id. Nothing is written to the database.
  /// </summary>
  Task<ImportPreviewResult> PreviewAsync(int userId, IReadOnlyList<ImportFileInput> files, ImportPreviewRequest request);

  /// <summary>Re-reads the staged file with new choices (date order, columns, target account).</summary>
  Task<ImportPreviewResult> ReparseAsync(int userId, string importId, ImportPreviewRequest request);

  /// <summary>One page of the staged rows, optionally only one class.</summary>
  Task<ImportRowsPage> GetRowsAsync(int userId, string importId, string? rowClass, int page, int pageSize);

  /// <summary>Writes the confirmed rows. Safe to repeat: rows already stored are skipped.</summary>
  Task<ImportCommitResult> CommitAsync(int userId, string importId, ImportCommitRequest request);

  /// <summary>Removes the transactions of a batch (and accounts the batch created that are now empty). Allowed for 24 hours.</summary>
  Task<ImportUndoResult> UndoAsync(int userId, string batchId);
}
