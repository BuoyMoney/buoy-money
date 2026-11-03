using BuoyAI.Core.DTOs.Sync;

namespace BuoyAI.Core.Interfaces;

/// <summary>
/// Dispatches a batch of queued offline mutations (#493). Each operation is routed through the SAME
/// service methods and family-authorization checks the corresponding REST controller uses, so an op
/// can never succeed for an entity the user couldn't modify through the normal endpoint. Idempotency,
/// per-item conflict detection and per-item isolation are handled here; the controller stays thin.
/// </summary>
public interface ISyncService
{
  /// <summary>
  /// Processes <paramref name="request"/> operations in order for <paramref name="userId"/> and
  /// returns one result per operation (applied/duplicate/conflict/failed). A single failing op never
  /// aborts the batch. Envelope validation (null/empty/&gt;50/blank keys) is the controller's job.
  /// </summary>
  Task<SyncBatchResponse> ProcessBatchAsync(int userId, SyncBatchRequest request);
}
