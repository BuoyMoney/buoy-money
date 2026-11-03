using System.Text.Json;

namespace BuoyAI.Core.DTOs.Sync;

/// <summary>
/// Offline-sync batch request (#493). The Flutter client drains its write outbox into an ordered
/// list of operations and POSTs them to /api/sync/batch. Processed strictly in array order; max 50.
/// </summary>
public class SyncBatchRequest
{
  public List<SyncOperation> Operations { get; set; } = new();
}

/// <summary>
/// A single queued mutation. <see cref="Payload"/> mirrors the JSON body of the equivalent REST
/// endpoint (see the operationType registry in the sync contract) and is deserialized server-side
/// into that endpoint's request DTO.
/// </summary>
public class SyncOperation
{
  /// <summary>Client-generated UUID v4. De-dupes replays; required.</summary>
  public string IdempotencyKey { get; set; } = string.Empty;

  /// <summary>One of the registered operation types (e.g. <c>budget_category_update</c>); required.</summary>
  public string OperationType { get; set; } = string.Empty;

  /// <summary>Target entity id as a string ("" for self-scoped ops like user settings). Parsed to int where needed.</summary>
  public string EntityId { get; set; } = string.Empty;

  /// <summary>The endpoint-specific request body. Optional for body-less ops (mark-read/archive).</summary>
  public JsonElement? Payload { get; set; }

  /// <summary>When the client enqueued the op (diagnostics only).</summary>
  public DateTime? QueuedAt { get; set; }

  /// <summary>
  /// The entity <c>UpdatedAt</c> the client last saw. When provided and the server's current
  /// UpdatedAt is strictly newer (beyond a 1s tolerance), the op is a <c>conflict</c> and is NOT
  /// applied (server-wins). Omit for last-write-wins.
  /// </summary>
  public DateTime? BaseUpdatedAt { get; set; }
}

/// <summary>Batch response — one result per submitted operation, same order.</summary>
public class SyncBatchResponse
{
  public List<SyncOperationResult> Results { get; set; } = new();
}

/// <summary>
/// Outcome of a single operation. <see cref="Status"/> ∈ <c>applied</c> | <c>duplicate</c> |
/// <c>conflict</c> | <c>failed</c>. <see cref="Error"/> is a short reason on failure (no stack traces).
/// </summary>
public class SyncOperationResult
{
  public string IdempotencyKey { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public string? Error { get; set; }
}

/// <summary>
/// Payload for <c>user_settings_budget_update</c> — mirrors PUT /api/user/settings/budget's body.
/// Declared in Core (not the API project) so the Infrastructure sync service can deserialize it.
/// </summary>
public class SyncUserBudgetSettingsPayload
{
  public bool ExcludePendingFromBudget { get; set; }
  public bool ReattributeOnAccountAssociationChange { get; set; }
}

/// <summary>
/// Payload for <c>user_settings_ai_update</c> — mirrors PUT /api/user/settings/ai's body.
/// All fields optional (null = keep unchanged), matching the controller's semantics.
/// </summary>
public class SyncUserAiSettingsPayload
{
  public bool? AiMemoryEnabled { get; set; }
  public string? AiInteractionMode { get; set; }
  public string? AiAutonomyLevel { get; set; }
}

/// <summary>Well-known operation type discriminators for the sync batch endpoint (#493).</summary>
public static class SyncOperationTypes
{
  public const string BudgetCategoryUpdate = "budget_category_update";
  public const string SavingsGoalUpdate = "savings_goal_update";
  public const string NotificationMarkRead = "notification_mark_read";
  public const string NotificationArchive = "notification_archive";
  public const string UserSettingsBudgetUpdate = "user_settings_budget_update";
  public const string UserSettingsAiUpdate = "user_settings_ai_update";
  public const string TransactionNotesUpdate = "transaction_notes_update";
}

/// <summary>Canonical status strings for <see cref="SyncOperationResult.Status"/>.</summary>
public static class SyncOperationStatus
{
  public const string Applied = "applied";
  public const string Duplicate = "duplicate";
  public const string Conflict = "conflict";
  public const string Failed = "failed";
}
