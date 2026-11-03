using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BuoyAI.Core.DTOs.Sync;
using BuoyAI.Core.Interfaces;

namespace BuoyAI.API.Controllers;

/// <summary>
/// Offline-first sync endpoint (#493). Accepts a batch of queued client mutations and dispatches
/// each through its normal service + family-authorization path. Thin: envelope validation here,
/// all dispatch/idempotency/conflict logic in <see cref="ISyncService"/>.
/// </summary>
[Authorize]
public class SyncController : BaseController
{
  private const int MaxOperationsPerBatch = 50;

  private readonly ISyncService _syncService;
  private readonly ILogger<SyncController> _logger;

  public SyncController(ISyncService syncService, ILogger<SyncController> logger)
  {
    _syncService = syncService;
    _logger = logger;
  }

  /// <summary>
  /// Process a batch of queued offline operations. Returns one result per operation (same order):
  /// applied | duplicate | conflict | failed. A malformed envelope (empty, &gt;50, or an op missing
  /// its idempotencyKey/operationType) rejects the whole batch with 400; per-op dispatch failures
  /// are reported as <c>failed</c> results, not a 400.
  /// </summary>
  [HttpPost("batch")]
  public async Task<ActionResult<SyncBatchResponse>> ProcessBatch([FromBody] SyncBatchRequest request)
  {
    if (request?.Operations == null || request.Operations.Count == 0)
      return BadRequest(new { message = "At least one operation is required." });

    if (request.Operations.Count > MaxOperationsPerBatch)
      return BadRequest(new { message = $"A batch may contain at most {MaxOperationsPerBatch} operations." });

    if (request.Operations.Any(op =>
          string.IsNullOrWhiteSpace(op.IdempotencyKey) || string.IsNullOrWhiteSpace(op.OperationType)))
      return BadRequest(new { message = "Every operation requires a non-empty idempotencyKey and operationType." });

    var userId = GetCurrentUserId();
    _logger.LogInformation("Processing sync batch of {Count} operations for user {UserId}",
      request.Operations.Count, userId);

    var response = await _syncService.ProcessBatchAsync(userId, request);
    return Ok(response);
  }
}
