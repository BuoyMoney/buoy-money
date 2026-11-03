using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// The original one-shot import (<c>POST /api/transactions/import</c>), kept so existing clients
/// keep working. It is now preview plus commit with the default choices, so it shares the
/// parsers, the account-scoped ids, the duplicate rules and the ingest path with the new
/// import screen (<see cref="ImportService"/>).
/// </summary>
public class TransactionImportService : ITransactionImportService
{
  private readonly IImportService _importService;
  private readonly ILogger<TransactionImportService> _logger;

  public TransactionImportService(IImportService importService, ILogger<TransactionImportService> logger)
  {
    _importService = importService;
    _logger = logger;
  }

  public async Task<TransactionImportResult> ImportFromFileAsync(
    int userId,
    int accountId,
    Stream fileStream,
    string fileName)
  {
    var result = new TransactionImportResult();

    try
    {
      if (!AllowedTransactionImportTypes.IsAllowedExtension(fileName))
      {
        result.Errors.Add("File type not supported. Allowed: .ofx, .qfx, .csv");
        return result;
      }

      using var buffer = new MemoryStream();
      await fileStream.CopyToAsync(buffer);
      var files = new List<ImportFileInput> { new() { FileName = fileName, Content = buffer.ToArray() } };

      var request = new ImportPreviewRequest { AccountId = accountId };
      var preview = await _importService.PreviewAsync(userId, files, request);

      // Day-first versus month-first cannot be told apart. This endpoint has no screen to ask on,
      // so it keeps the long-standing month-first reading and says so.
      if (preview.NeedsDateOrderChoice)
      {
        request.Mapping.DateOrder = ImportDateOrder.MonthFirst;
        preview = await _importService.ReparseAsync(userId, preview.ImportId, request);
        result.Warnings.Add("Dates in this file could be day-first or month-first. They were read month first.");
      }

      result.Warnings.AddRange(preview.Warnings);
      result.TotalRowsInFile = preview.Summary.Total;

      if (preview.Summary.Total == 0)
      {
        result.Success = true;
        result.Message = "No transactions found in file.";
        return result;
      }

      var commit = await _importService.CommitAsync(userId, preview.ImportId, new ImportCommitRequest());
      result.Success = true;
      result.ImportedCount = commit.Imported;
      result.SkippedDuplicates = commit.SkippedDuplicates;
      result.SkippedInvalid = commit.SkippedInvalid;
      result.Message = $"Successfully imported {result.ImportedCount} transactions.";
      if (result.SkippedDuplicates > 0) result.Message += $" Skipped {result.SkippedDuplicates} duplicates.";
      if (result.SkippedInvalid > 0) result.Message += $" Skipped {result.SkippedInvalid} invalid rows.";
      return result;
    }
    catch (ImportException ex)
    {
      result.Success = false;
      result.Errors.Add(ex.Message);
      return result;
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error importing transactions from {FileName} for user {UserId}", fileName, userId);
      result.Success = false;
      result.Errors.Add($"Failed to import file: {ex.Message}");
      return result;
    }
  }
}
