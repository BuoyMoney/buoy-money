using System.Text.Json;
using System.Text.Json.Serialization;
using BuoyAI.Core.DTOs.Import;
using BuoyAI.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BuoyAI.API.Controllers;

/// <summary>
/// File import in two steps: upload and preview (nothing is written), then commit what the user
/// confirmed. Handles bank files (CSV, OFX, QFX) and Actual Budget and YNAB exports.
/// </summary>
[Authorize]
public class ImportController : BaseController
{
  private const long MaxBodyBytes = 30_000_000;

  private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
  {
    Converters = { new JsonStringEnumConverter() }
  };

  private readonly IImportService _importService;
  private readonly ILogger<ImportController> _logger;

  public ImportController(IImportService importService, ILogger<ImportController> logger)
  {
    _importService = importService;
    _logger = logger;
  }

  /// <summary>
  /// Reads the uploaded file(s) and returns what would be imported. YNAB exports may arrive as
  /// two CSV files (Register and Plan) or one zip. Optional form fields: source, accountId,
  /// mapping (JSON).
  /// </summary>
  [HttpPost("preview")]
  [RequestSizeLimit(MaxBodyBytes)]
  public async Task<IActionResult> Preview(
    [FromForm] List<IFormFile> files,
    [FromForm] string? source,
    [FromForm] int? accountId,
    [FromForm] string? mapping)
  {
    var request = BuildRequest(source, accountId, mapping, out var error);
    if (error != null) return BadRequest(new { message = error });

    var inputs = new List<ImportFileInput>();
    foreach (var file in files.Where(f => f.Length > 0))
    {
      using var buffer = new MemoryStream();
      await file.CopyToAsync(buffer);
      inputs.Add(new ImportFileInput { FileName = file.FileName, Content = buffer.ToArray() });
    }

    return await Run(() => _importService.PreviewAsync(GetCurrentUserId(), inputs, request));
  }

  /// <summary>Re-reads the staged file with new choices (date order, columns, target account).</summary>
  [HttpPost("{importId}/reparse")]
  public async Task<IActionResult> Reparse(string importId, [FromBody] ImportPreviewRequest request) =>
    await Run(() => _importService.ReparseAsync(GetCurrentUserId(), importId, request ?? new ImportPreviewRequest()));

  /// <summary>A page of the staged rows. Filter with cls (new, duplicateExact, duplicateLikely, possibleDuplicate, invalid).</summary>
  [HttpGet("{importId}/rows")]
  public async Task<IActionResult> Rows(string importId, [FromQuery] string? cls, [FromQuery] int page = 0, [FromQuery] int pageSize = 50) =>
    await Run(() => _importService.GetRowsAsync(GetCurrentUserId(), importId, cls, page, pageSize));

  /// <summary>Writes the confirmed rows. Returns a batch id that can be passed to undo for 24 hours.</summary>
  [HttpPost("{importId}/commit")]
  public async Task<IActionResult> Commit(string importId, [FromBody] ImportCommitRequest request) =>
    await Run(() => _importService.CommitAsync(GetCurrentUserId(), importId, request ?? new ImportCommitRequest()));

  /// <summary>Removes the transactions a batch created (and accounts it created that are now empty).</summary>
  [HttpPost("{batchId}/undo")]
  public async Task<IActionResult> Undo(string batchId) =>
    await Run(() => _importService.UndoAsync(GetCurrentUserId(), batchId));

  private async Task<IActionResult> Run<T>(Func<Task<T>> action)
  {
    try
    {
      return Ok(await action());
    }
    catch (ImportException ex)
    {
      _logger.LogInformation("Import request refused: {Message}", ex.Message);
      return StatusCode(ex.StatusCode, new { message = ex.Message });
    }
  }

  private static ImportPreviewRequest BuildRequest(string? source, int? accountId, string? mapping, out string? error)
  {
    error = null;
    var request = new ImportPreviewRequest { AccountId = accountId };
    if (!string.IsNullOrWhiteSpace(source))
    {
      if (!Enum.TryParse<ImportSource>(source, true, out var parsed))
      {
        error = "Unknown source. Use auto, csv, ofx, ynab or actual.";
        return request;
      }
      request.Source = parsed;
    }
    if (!string.IsNullOrWhiteSpace(mapping))
    {
      try
      {
        request.Mapping = JsonSerializer.Deserialize<ImportMapping>(mapping, JsonOptions) ?? new ImportMapping();
      }
      catch (JsonException)
      {
        error = "The mapping could not be read.";
      }
    }
    return request;
  }
}
