using BuoyAI.Core.Interfaces;
using BuoyAI.Core.Receipts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Receipt scanning: the user shares a screenshot of an order page, a photo of a paper receipt or a
/// PDF, and gets back the split BuoyAI offers for the matching bank charge.
/// </summary>
[ApiController]
[Route("api/receipts")]
[Authorize]
public class ReceiptsController : BaseController
{
  private readonly IReceiptService _receipts;
  private readonly ILogger<ReceiptsController> _logger;

  public ReceiptsController(IReceiptService receipts, ILogger<ReceiptsController> logger)
  {
    _receipts = receipts;
    _logger = logger;
  }

  [HttpGet("status")]
  public async Task<IActionResult> Status() => Ok(await _receipts.GetStatusAsync(GetCurrentUserId()));

  /// <summary>
  /// Read one receipt (one to four files showing one order) and return the offered split. Files are
  /// read while the request waits and never stored. <paramref name="transactionId"/> is the charge
  /// the user scanned it from, if any.
  /// </summary>
  [HttpPost]
  [EnableRateLimiting("ai")]
  [RequestSizeLimit(ReceiptFiles.MaxTotalBytes + 1024 * 1024)]
  [RequestFormLimits(MultipartBodyLengthLimit = ReceiptFiles.MaxTotalBytes + 1024 * 1024)]
  public async Task<IActionResult> Scan([FromForm] List<IFormFile> files, [FromForm] int? transactionId, CancellationToken ct)
  {
    var userId = GetCurrentUserId();
    var read = new List<ReceiptFile>();
    foreach (var file in files.Take(ReceiptFiles.MaxFiles + 1))
    {
      if (file.Length > ReceiptFiles.MaxFileBytes)
        return BadRequest(new { message = "Each file must be under 8 MB." });
      using var stream = new MemoryStream();
      await file.CopyToAsync(stream, ct);
      read.Add(new ReceiptFile(file.ContentType ?? string.Empty, stream.ToArray()));
    }

    try
    {
      return Ok(await _receipts.ScanAsync(userId, read, transactionId, ct));
    }
    catch (ReceiptScanBlockedException ex)
    {
      return ex.Reason == "premium_required"
        ? StatusCode(StatusCodes.Status403Forbidden, new { reason = ex.Reason, message = ex.Message })
        : Conflict(new { reason = ex.Reason, message = ex.Message });
    }
    catch (ReceiptFileException ex)
    {
      return BadRequest(new { message = ex.Message });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
      // Gemini down or an unusable reply: nothing was stored, so the user can just try again.
      _logger.LogWarning(ex, "Receipt scan failed for user {UserId}", userId);
      return StatusCode(StatusCodes.Status502BadGateway, new { message = "The receipt couldn't be read right now. Try again in a minute." });
    }
  }

  [HttpGet]
  public async Task<IActionResult> List([FromQuery] int limit = 30) =>
    Ok(await _receipts.GetScansAsync(GetCurrentUserId(), limit));

  [HttpGet("{id:int}")]
  public async Task<IActionResult> Get(int id) =>
    HandleServiceResponse(await _receipts.GetScanAsync(GetCurrentUserId(), id), "Receipt not found");

  [HttpGet("for-transaction/{transactionId:int}")]
  public async Task<IActionResult> ForTransaction(int transactionId)
  {
    var scan = await _receipts.GetScanForTransactionAsync(GetCurrentUserId(), transactionId);
    return scan == null ? NoContent() : Ok(scan);
  }

  [HttpPost("{id:int}/apply")]
  public async Task<IActionResult> Apply(int id) =>
    HandleServiceResponse(await _receipts.ApplyAsync(GetCurrentUserId(), id), "Receipt not found or not ready");

  [HttpPost("{id:int}/dismiss")]
  public async Task<IActionResult> Dismiss(int id) =>
    HandleServiceResponse(await _receipts.DismissAsync(GetCurrentUserId(), id), "Receipt not found");
}
