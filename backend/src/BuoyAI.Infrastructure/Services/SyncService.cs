using System.Text.Json;
using BuoyAI.Core.DTOs.Budget;
using BuoyAI.Core.DTOs.Savings;
using BuoyAI.Core.DTOs.Sync;
using BuoyAI.Core.DTOs.Transaction;
using BuoyAI.Core.Entities;
using BuoyAI.Core.Interfaces;
using BuoyAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BuoyAI.Infrastructure.Services;

/// <summary>
/// Offline-sync batch dispatcher (#493). See <see cref="ISyncService"/>. Every operation is routed
/// through the same service + family-authorization path the equivalent REST controller uses.
/// </summary>
public class SyncService : ISyncService
{
  // Absorbs serialization/clock rounding so a round-tripped-but-unchanged UpdatedAt isn't a conflict.
  private static readonly TimeSpan ConflictTolerance = TimeSpan.FromSeconds(1);
  private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(48);

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  private readonly BuoyAIDbContext _db;
  private readonly IBudgetService _budgetService;
  private readonly ISavingsService _savingsService;
  private readonly INotificationService _notificationService;
  private readonly ITransactionService _transactionService;
  private readonly IFamilyAuthorizationService _authService;
  private readonly IUserRepository _userRepository;
  private readonly ILogger<SyncService> _logger;

  public SyncService(
    BuoyAIDbContext db,
    IBudgetService budgetService,
    ISavingsService savingsService,
    INotificationService notificationService,
    ITransactionService transactionService,
    IFamilyAuthorizationService authService,
    IUserRepository userRepository,
    ILogger<SyncService> logger)
  {
    _db = db;
    _budgetService = budgetService;
    _savingsService = savingsService;
    _notificationService = notificationService;
    _transactionService = transactionService;
    _authService = authService;
    _userRepository = userRepository;
    _logger = logger;
  }

  public async Task<SyncBatchResponse> ProcessBatchAsync(int userId, SyncBatchRequest request)
  {
    await PruneExpiredKeysAsync();

    var response = new SyncBatchResponse();

    foreach (var op in request.Operations)
    {
      try
      {
        // Each op must start from a clean tracker: the dispatched services share this scoped
        // DbContext, and two updates of the same entity graph in one context throw an EF identity
        // conflict (same failure mode as the v1.5.1 transfer-confirm bug).
        _db.ChangeTracker.Clear();

        // (a) Idempotency: a key already recorded for this user returns its original outcome as
        // "duplicate" without re-dispatching. Covers cross-request replays AND duplicate keys within
        // this same batch (each key is saved before the next op is processed).
        var existing = await _db.SyncIdempotencyKeys
          .FirstOrDefaultAsync(k => k.UserId == userId && k.Key == op.IdempotencyKey);
        if (existing != null)
        {
          response.Results.Add(Result(op, SyncOperationStatus.Duplicate));
          continue;
        }

        // (b) Conflict: if the client told us the UpdatedAt it based this edit on, and the server's
        // entity is strictly newer (beyond tolerance), server wins — don't apply.
        if (op.BaseUpdatedAt.HasValue)
        {
          var serverUpdatedAt = await GetEntityUpdatedAtAsync(op);
          if (serverUpdatedAt.HasValue &&
              serverUpdatedAt.Value > op.BaseUpdatedAt.Value.ToUniversalTime().Add(ConflictTolerance))
          {
            await RecordAsync(userId, op, SyncOperationStatus.Conflict);
            response.Results.Add(Result(op, SyncOperationStatus.Conflict));
            continue;
          }
        }

        // (c) Dispatch through the real service + auth path. Throws on validation/auth/not-found.
        await DispatchAsync(userId, op);

        // (d) Record the successful outcome so a replay short-circuits.
        // INVARIANT: the idempotency row is saved AFTER the dispatched service already
        // committed, so a crash between the two re-applies the op on replay. That is safe
        // only because every registered operation sets absolute values (mark-read, set
        // notes/settings, absolute budget/goal amounts) — re-applying is a no-op. Any new
        // operation type added to the registry MUST stay idempotent in this sense.
        await RecordAsync(userId, op, SyncOperationStatus.Applied);
        response.Results.Add(Result(op, SyncOperationStatus.Applied));
      }
      catch (Exception ex)
      {
        // (e) Per-item isolation: one bad op is recorded as failed and the batch continues.
        _logger.LogWarning(ex,
          "Sync op failed: user={UserId} type={OperationType} entity={EntityId} key={Key}",
          userId, op.OperationType, op.EntityId, op.IdempotencyKey);

        await TryRecordFailureAsync(userId, op);
        response.Results.Add(Result(op, SyncOperationStatus.Failed, ShortError(ex)));
      }
    }

    return response;
  }

  /// <summary>
  /// Routes one operation to the same service call + authorization its REST controller performs.
  /// Throws <see cref="UnauthorizedAccessException"/> on denied access, <see cref="KeyNotFoundException"/>
  /// when the target is missing, <see cref="NotSupportedException"/> for an unknown type — all mapped
  /// to a per-item <c>failed</c> by the caller.
  /// </summary>
  private async Task DispatchAsync(int userId, SyncOperation op)
  {
    switch (op.OperationType)
    {
      case SyncOperationTypes.BudgetCategoryUpdate:
        await DispatchBudgetCategoryUpdateAsync(userId, op);
        break;

      case SyncOperationTypes.SavingsGoalUpdate:
        await DispatchSavingsGoalUpdateAsync(userId, op);
        break;

      case SyncOperationTypes.NotificationMarkRead:
        await DispatchNotificationMarkReadAsync(userId, op);
        break;

      case SyncOperationTypes.NotificationArchive:
        await DispatchNotificationArchiveAsync(userId, op);
        break;

      case SyncOperationTypes.UserSettingsBudgetUpdate:
        await DispatchUserBudgetSettingsAsync(userId, op);
        break;

      case SyncOperationTypes.UserSettingsAiUpdate:
        await DispatchUserAiSettingsAsync(userId, op);
        break;

      case SyncOperationTypes.TransactionNotesUpdate:
        await DispatchTransactionNotesUpdateAsync(userId, op);
        break;

      default:
        throw new NotSupportedException("unknown operation type");
    }
  }

  // Mirrors BudgetController.UpdateBudgetCategory: resolve category → authorize its owning budget
  // (Edit) → update. The category id is client-supplied, so the auth check is what stops an IDOR.
  private async Task DispatchBudgetCategoryUpdateAsync(int userId, SyncOperation op)
  {
    var categoryId = ParseEntityId(op);
    var request = Deserialize<UpdateBudgetCategoryRequest>(op.Payload);

    var existing = await _budgetService.GetBudgetCategoryAsync(categoryId)
      ?? throw new KeyNotFoundException("budget category not found");

    var auth = await _authService.CanAccessBudgetAsync(userId, existing.BudgetId, ResourcePermission.Edit);
    if (!auth.IsAuthorized)
      throw new UnauthorizedAccessException(auth.Reason ?? "not authorized to modify this budget");

    await _budgetService.UpdateBudgetCategoryAsync(categoryId, request);
  }

  // Mirrors SavingsController.UpdateSavingsGoal: authorize goal (Edit) → update.
  private async Task DispatchSavingsGoalUpdateAsync(int userId, SyncOperation op)
  {
    var goalId = ParseEntityId(op);
    var request = Deserialize<UpdateSavingsGoalRequest>(op.Payload);

    var auth = await _authService.CanAccessGoalAsync(userId, goalId, ResourcePermission.Edit);
    if (!auth.IsAuthorized)
    {
      if (auth.Reason?.Contains("not found", StringComparison.OrdinalIgnoreCase) == true)
        throw new KeyNotFoundException("savings goal not found");
      throw new UnauthorizedAccessException(auth.Reason ?? "not authorized to edit this savings goal");
    }

    await _savingsService.UpdateSavingsGoalAsync(goalId, request, userId);
  }

  // Mirrors NotificationsController.MarkAsRead: the service scopes to userId and returns false when
  // the notification isn't the user's (or doesn't exist).
  private async Task DispatchNotificationMarkReadAsync(int userId, SyncOperation op)
  {
    var notificationId = ParseEntityId(op);
    var ok = await _notificationService.MarkAsReadAsync(userId, notificationId);
    if (!ok)
      throw new KeyNotFoundException("notification not found or access denied");
  }

  // Mirrors NotificationsController.Archive.
  private async Task DispatchNotificationArchiveAsync(int userId, SyncOperation op)
  {
    var notificationId = ParseEntityId(op);
    var ok = await _notificationService.ArchiveAsync(userId, notificationId);
    if (!ok)
      throw new KeyNotFoundException("notification not found or access denied");
  }

  // Mirrors UserController.UpdateBudgetSettings (self-scoped; no family auth).
  private async Task DispatchUserBudgetSettingsAsync(int userId, SyncOperation op)
  {
    var payload = Deserialize<SyncUserBudgetSettingsPayload>(op.Payload);
    var user = await _userRepository.GetByIdAsync(userId)
      ?? throw new KeyNotFoundException("user not found");

    user.ExcludePendingFromBudget = payload.ExcludePendingFromBudget;
    user.ReattributeOnAccountAssociationChange = payload.ReattributeOnAccountAssociationChange;
    await _userRepository.UpdateAsync(user);
  }

  // Mirrors UserController.UpdateAiSettings (self-scoped; null fields keep the current value).
  private async Task DispatchUserAiSettingsAsync(int userId, SyncOperation op)
  {
    var payload = Deserialize<SyncUserAiSettingsPayload>(op.Payload);
    var user = await _userRepository.GetByIdAsync(userId)
      ?? throw new KeyNotFoundException("user not found");

    if (payload.AiMemoryEnabled.HasValue)
      user.AiMemoryEnabled = payload.AiMemoryEnabled.Value;

    if (!string.IsNullOrEmpty(payload.AiInteractionMode) &&
        Enum.TryParse<Core.Enums.AIInteractionMode>(payload.AiInteractionMode, true, out var mode))
      user.AiInteractionMode = mode;

    if (!string.IsNullOrEmpty(payload.AiAutonomyLevel) &&
        Enum.TryParse<Core.Enums.AIAutonomyLevel>(payload.AiAutonomyLevel, true, out var autonomy))
      user.AiAutonomyLevel = autonomy;

    await _userRepository.UpdateAsync(user);
  }

  // Mirrors TransactionsController.UpdateTransactionNotes: the service scopes to userId and throws
  // ArgumentException when the transaction isn't the user's (or doesn't exist).
  private async Task DispatchTransactionNotesUpdateAsync(int userId, SyncOperation op)
  {
    var transactionId = ParseEntityId(op);
    var request = Deserialize<UpdateTransactionNotesRequest>(op.Payload);
    await _transactionService.UpdateTransactionNotesAsync(transactionId, request.Notes, userId);
  }

  /// <summary>
  /// Reads the server's current <c>UpdatedAt</c> for conflict detection, per operation type.
  /// Notification and user-settings ops are last-write-wins (low stakes) so they return null and
  /// skip the conflict check. Budget categories carry their own UpdatedAt (no parent fallback needed).
  /// </summary>
  private async Task<DateTime?> GetEntityUpdatedAtAsync(SyncOperation op)
  {
    if (!int.TryParse(op.EntityId, out var id))
      return null;

    return op.OperationType switch
    {
      SyncOperationTypes.BudgetCategoryUpdate => await _db.BudgetCategories
        .Where(c => c.Id == id).Select(c => (DateTime?)c.UpdatedAt).FirstOrDefaultAsync(),
      SyncOperationTypes.SavingsGoalUpdate => await _db.SavingsGoals
        .Where(g => g.Id == id).Select(g => (DateTime?)g.UpdatedAt).FirstOrDefaultAsync(),
      SyncOperationTypes.TransactionNotesUpdate => await _db.Transactions
        .Where(t => t.Id == id).Select(t => (DateTime?)t.UpdatedAt).FirstOrDefaultAsync(),
      _ => null, // notifications + user settings: LWW, no conflict check
    };
  }

  /// <summary>
  /// Opportunistically deletes idempotency rows older than the retention window at the start of each
  /// batch. Guarded by <c>IsRelational()</c> because ExecuteDelete isn't supported on the InMemory
  /// provider the integration tests use; wrapped in try/catch so cleanup never fails a real batch.
  /// </summary>
  private async Task PruneExpiredKeysAsync()
  {
    if (!_db.Database.IsRelational())
      return;

    try
    {
      var cutoff = DateTime.UtcNow - RetentionWindow;
      await _db.SyncIdempotencyKeys.Where(k => k.ProcessedAt < cutoff).ExecuteDeleteAsync();
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Sync idempotency retention cleanup failed (non-fatal)");
    }
  }

  private async Task RecordAsync(int userId, SyncOperation op, string status)
  {
    _db.SyncIdempotencyKeys.Add(new SyncIdempotencyKey
    {
      UserId = userId,
      Key = op.IdempotencyKey,
      OperationType = op.OperationType,
      Status = status,
      ProcessedAt = DateTime.UtcNow,
    });
    await _db.SaveChangesAsync();
  }

  // A failed dispatch may have left tracked, partially-applied changes in the context. Discard them
  // before recording the failure so the idempotency insert is the only thing saved.
  private async Task TryRecordFailureAsync(int userId, SyncOperation op)
  {
    try
    {
      _db.ChangeTracker.Clear();
      await RecordAsync(userId, op, SyncOperationStatus.Failed);
    }
    catch (Exception ex)
    {
      _logger.LogWarning(ex, "Failed to record sync failure row for key {Key}", op.IdempotencyKey);
    }
  }

  private static int ParseEntityId(SyncOperation op)
  {
    if (!int.TryParse(op.EntityId, out var id))
      throw new ArgumentException("invalid entityId");
    return id;
  }

  private static T Deserialize<T>(JsonElement? payload)
  {
    if (payload == null || payload.Value.ValueKind == JsonValueKind.Null ||
        payload.Value.ValueKind == JsonValueKind.Undefined)
      throw new ArgumentException("missing payload");

    var result = payload.Value.Deserialize<T>(JsonOptions);
    if (result == null)
      throw new ArgumentException("invalid payload");
    return result;
  }

  private static SyncOperationResult Result(SyncOperation op, string status, string? error = null) => new()
  {
    IdempotencyKey = op.IdempotencyKey,
    Status = status,
    Error = error,
  };

  private static string ShortError(Exception ex) => ex switch
  {
    NotSupportedException => "unknown operation type",
    UnauthorizedAccessException => ex.Message,
    KeyNotFoundException => ex.Message,
    ArgumentException => string.IsNullOrWhiteSpace(ex.Message) ? "invalid request" : ex.Message,
    JsonException => "malformed payload",
    _ => "operation failed",
  };
}
