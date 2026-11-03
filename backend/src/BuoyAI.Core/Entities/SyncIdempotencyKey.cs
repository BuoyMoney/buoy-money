namespace BuoyAI.Core.Entities;

/// <summary>
/// Idempotency ledger for the offline-sync batch endpoint (#493). One row per
/// (<see cref="UserId"/>, <see cref="Key"/>) client-generated operation key. The sync
/// handler records an operation's final <see cref="Status"/> only after it processes,
/// and short-circuits any key already present so a replayed outbox (the Flutter client
/// retries the whole batch on transport failure) returns the ORIGINAL outcome instead of
/// re-applying the mutation. Modeled after <see cref="ProcessedStripeEvent"/>.
///
/// Rows are retained ~48h (opportunistically pruned at the start of each batch call) —
/// long enough to absorb realistic offline windows + retry backoff, short enough that the
/// table stays small.
/// </summary>
public class SyncIdempotencyKey
{
  /// <summary>The user who submitted the operation. Half of the composite key.</summary>
  public int UserId { get; set; }

  /// <summary>Client-generated idempotency key (UUID v4). Unique per user.</summary>
  public string Key { get; set; } = string.Empty;

  /// <summary>The operation type this key processed (e.g. <c>budget_category_update</c>), for diagnostics.</summary>
  public string OperationType { get; set; } = string.Empty;

  /// <summary>Final recorded outcome: <c>applied</c> | <c>conflict</c> | <c>failed</c>. Replays return this.</summary>
  public string Status { get; set; } = string.Empty;

  /// <summary>When the operation was processed (server clock, UTC).</summary>
  public DateTime ProcessedAt { get; set; }
}
