import 'dart:async';
import 'dart:convert';
import 'dart:math' as math;

import 'package:dio/dio.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'package:uuid/uuid.dart';

import '../services/api_client.dart';
import 'connectivity_service.dart';
import 'network_error.dart' as neterr;
import 'offline_status.dart';
import 'offline_store.dart';

/// Orchestrates the offline write outbox: queue-on-failure, drain-on-reconnect,
/// per-op status mapping against the POST /sync/batch contract, and optimistic
/// local cache patching so queued writes are visible immediately.
///
/// Reads the global [offlineStore]/[offlineStatus]/[connectivityService]/
/// [apiClient] at call time (not cached in fields) so tests that swap those
/// globals are honoured.
class SyncService {
  SyncService();

  static const _uuid = Uuid();

  /// Max operations per batch (contract limit).
  static const int _maxBatch = 50;

  /// Drop an op after this many failed transport attempts (with a Sentry event).
  static const int _maxRetries = 20;

  // Operation types — must match the sync contract registry exactly.
  static const String opBudgetCategoryUpdate = 'budget_category_update';
  static const String opSavingsGoalUpdate = 'savings_goal_update';
  static const String opNotificationMarkRead = 'notification_mark_read';
  static const String opNotificationArchive = 'notification_archive';
  static const String opUserSettingsBudgetUpdate = 'user_settings_budget_update';
  static const String opUserSettingsAiUpdate = 'user_settings_ai_update';
  static const String opTransactionNotesUpdate = 'transaction_notes_update';

  bool _draining = false;
  StreamSubscription<bool>? _connSub;

  /// Convenience passthrough so callers can classify a Dio error the same way
  /// the offline layer does.
  static bool isNetworkError(DioException e) => neterr.isNetworkError(e);

  /// Wire up triggers: drain whenever connectivity transitions to online. Call
  /// once from `main()`.
  void start() {
    _connSub ??= connectivityService.onlineStream.listen((online) {
      if (online) unawaited(drainOutbox());
    });
    unawaited(refreshPending());
  }

  /// Sync the pending-count badge from the outbox.
  Future<void> refreshPending() async {
    try {
      final ops = await offlineStore.pendingOps();
      offlineStatus.setPendingCount(ops.length);
    } catch (_) {}
  }

  /// Queue a write for later sync. Generates a fresh idempotency key. Best-effort
  /// and never throws (a lost queue insert is reported to Sentry, not surfaced).
  /// Returns the idempotency key.
  Future<String> enqueue({
    required String operationType,
    required String entityId,
    required Map<String, dynamic> payload,
    DateTime? baseUpdatedAt,
  }) async {
    final key = _uuid.v4();
    try {
      await offlineStore.enqueue(SyncOp(
        idempotencyKey: key,
        operationType: operationType,
        entityId: entityId,
        payload: jsonEncode(payload),
        queuedAt: DateTime.now().toUtc(),
        baseUpdatedAt: baseUpdatedAt,
      ));
      await refreshPending();
    } catch (e, st) {
      unawaited(Sentry.captureException(e, stackTrace: st, withScope: (s) {
        s.setTag('offline.enqueue', operationType);
      }));
    }
    return key;
  }

  /// Try the live write ([apply]) first. On a transport-type failure, queue the
  /// op and return [onQueued]()'s optimistic result instead. Non-network errors
  /// propagate unchanged (validation, permission, etc. are real failures).
  Future<T> enqueueOrSend<T>({
    required String operationType,
    required String entityId,
    required Map<String, dynamic> payload,
    DateTime? baseUpdatedAt,
    required Future<T> Function() apply,
    required T Function() onQueued,
  }) async {
    try {
      final result = await apply();
      // We're online — opportunistically flush anything queued earlier.
      unawaited(drainOutbox());
      return result;
    } on DioException catch (e) {
      if (!neterr.isNetworkError(e)) rethrow;
      await enqueue(
        operationType: operationType,
        entityId: entityId,
        payload: payload,
        baseUpdatedAt: baseUpdatedAt,
      );
      return onQueued();
    }
  }

  /// Push queued ops to POST /sync/batch and reconcile per the contract.
  ///
  /// Concurrency-guarded: a second call while one is in flight is a no-op.
  /// Loops in ≤50-op batches until the eligible backlog is empty (capped),
  /// so a large offline session fully syncs on a single trigger instead of
  /// stalling 50 ops per reconnect.
  Future<void> drainOutbox() async {
    if (_draining) return;
    // Auth-gated: draining while logged out just burns 401s (seen live in the
    // staging test — the reconnect trigger fired before re-login). The post-
    // login hook in AuthProvider drains as soon as a session exists.
    try {
      final token = await apiClient.getAuthToken();
      if (token == null || token.isEmpty) return;
    } catch (_) {
      return;
    }
    // Believed-offline: don't attempt (and don't inflate retry backoff) — the
    // online-transition listener re-triggers the drain when we're back.
    if (!connectivityService.isOnline) return;
    _draining = true;
    try {
      const maxRounds = 10; // safety cap: 500 ops per drain invocation
      for (var round = 0; round < maxRounds; round++) {
        final progressed = await _drainOneBatch();
        if (!progressed) break;
      }
    } finally {
      _draining = false;
    }
  }

  /// Sends one batch. Returns true when a full batch resolved successfully and
  /// more eligible ops may remain (caller loops); false when done/blocked.
  Future<bool> _drainOneBatch() async {
    {
      final all = await offlineStore.pendingOps();
      if (all.isEmpty) {
        offlineStatus.setPendingCount(0);
        return false;
      }

      // Permanently drop ops that have exhausted their retry budget.
      final exhausted = all.where((o) => o.retryCount >= _maxRetries).toList();
      if (exhausted.isNotEmpty) {
        await offlineStore.removeOps(_ids(exhausted));
        for (final op in exhausted) {
          unawaited(Sentry.captureMessage(
            'Offline sync op dropped after $_maxRetries retries',
            level: SentryLevel.error,
            withScope: (s) {
              s.setTag('offline.drop', op.operationType);
              s.setContexts('offline_op', {'entityId': op.entityId});
            },
          ));
        }
      }

      final now = DateTime.now().toUtc();
      final remaining =
          all.where((o) => o.retryCount < _maxRetries).toList();
      offlineStatus.setPendingCount(remaining.length);

      // Respect exponential backoff — only ops whose gate has elapsed go out.
      final eligible =
          remaining.where((o) => _eligible(o, now)).take(_maxBatch).toList();
      if (eligible.isEmpty) return false;

      final body = <String, dynamic>{
        'operations': eligible.map(_opToJson).toList(),
      };

      Map<String, dynamic> resp;
      try {
        resp = await apiClient.post<Map<String, dynamic>>(
          '/sync/batch',
          data: body,
        );
      } on DioException catch (e) {
        // 401: not logged in (or token expired and refresh unavailable). The ops
        // are fine — do NOT burn their retry budget; the post-login drain hook
        // sends them as soon as a session exists.
        if (e.response?.statusCode == 401) return false;
        // Transport error / 5xx / 429 on the batch call itself → keep the ops,
        // bump retry, try again later behind the backoff gate.
        await offlineStore.bumpRetry(_ids(eligible));
        if (!neterr.isNetworkError(e) && !_isRetryableStatus(e)) {
          // Unexpected non-retryable failure of the batch endpoint — surface it.
          unawaited(Sentry.captureException(e, withScope: (s) {
            s.setTag('offline.batch', 'non-retryable');
          }));
        }
        return false;
      }

      final results = (resp['results'] ?? resp['Results']) as List<dynamic>? ??
          const <dynamic>[];
      final byKey = <String, Map<String, dynamic>>{};
      for (final r in results) {
        if (r is Map) {
          final k = (r['idempotencyKey'] ?? r['IdempotencyKey'])?.toString();
          if (k != null) byKey[k] = r.cast<String, dynamic>();
        }
      }

      final toRemove = <int>[];
      final toBump = <int>[];
      final conflictPrefixes = <String>{};

      for (final op in eligible) {
        final r = byKey[op.idempotencyKey];
        final status =
            (r?['status'] ?? r?['Status'])?.toString().toLowerCase();
        switch (status) {
          case 'applied':
          case 'duplicate':
            toRemove.add(op.id!);
            break;
          case 'conflict':
            toRemove.add(op.id!);
            conflictPrefixes.addAll(_cachePrefixesFor(op.operationType));
            Sentry.addBreadcrumb(Breadcrumb(
              category: 'offline.sync',
              level: SentryLevel.info,
              message: 'sync conflict (server-wins) for ${op.operationType}',
              data: {'entityId': op.entityId},
            ));
            break;
          case 'failed':
            toRemove.add(op.id!);
            final err = (r?['error'] ?? r?['Error'])?.toString();
            Sentry.addBreadcrumb(Breadcrumb(
              category: 'offline.sync',
              level: SentryLevel.warning,
              message: 'sync op failed permanently: ${op.operationType}',
              data: {'entityId': op.entityId, 'error': err ?? ''},
            ));
            unawaited(Sentry.captureMessage(
              'Offline sync op rejected (${op.operationType}): ${err ?? 'unknown'}',
              level: SentryLevel.warning,
            ));
            break;
          default:
            // Server returned no result for this op — keep it, retry later.
            toBump.add(op.id!);
        }
      }

      await offlineStore.removeOps(toRemove);
      await offlineStore.bumpRetry(toBump);
      for (final prefix in conflictPrefixes) {
        await offlineStore.invalidatePrefix(prefix);
      }
      await refreshPending();
      // Keep looping only when this batch was full AND every op in it resolved
      // (nothing bumped for retry) — i.e. more eligible ops may be waiting and
      // the endpoint is healthy. A partially-resolved batch stops the loop so
      // backoff gates are respected.
      return eligible.length == _maxBatch && toBump.isEmpty;
    }
  }

  // ── Optimistic cache patching ────────────────────────────────────────────

  /// Patch cached notification payloads so a queued read/archive is reflected
  /// on re-render. No-op when nothing is cached.
  Future<void> patchNotification(int id, {bool? isRead, bool? isArchived}) async {
    await _patchCachedById(
      prefix: '/notifications',
      id: id,
      mutate: (m) {
        if (isRead != null) {
          m['isRead'] = isRead;
          if (isRead) {
            m['readAt'] = DateTime.now().toUtc().toIso8601String();
          }
        }
        if (isArchived != null) m['isArchived'] = isArchived;
      },
    );
  }

  /// Optimistically mark EVERY cached notification as read — the offline
  /// fallback for mark-all-read, which has no dedicated batch sync op. Walks all
  /// cache entries under '/notifications', flips `isRead=true` (+`readAt`) on
  /// every notification-shaped object, zeroes any `unreadCount` (summary badge),
  /// writes the patched payloads back, and returns the ids that were previously
  /// UNREAD so the caller can enqueue a per-id `notification_mark_read` op (those
  /// DO have a batch op). Best-effort: returns [] on failure / nothing cached.
  Future<List<int>> markAllNotificationsReadInCache() async {
    final unreadIds = <int>{};
    try {
      final entries = await offlineStore.readCacheByPrefix('/notifications');
      final nowIso = DateTime.now().toUtc().toIso8601String();
      for (final entry in entries) {
        dynamic decoded;
        try {
          decoded = jsonDecode(entry.payload);
        } catch (_) {
          continue;
        }
        var mutated = false;

        void walk(dynamic node) {
          if (node is Map<String, dynamic>) {
            // Zero the summary's unread badge count if present.
            if (node.containsKey('unreadCount') && node['unreadCount'] != 0) {
              node['unreadCount'] = 0;
              mutated = true;
            } else if (node.containsKey('UnreadCount') &&
                node['UnreadCount'] != 0) {
              node['UnreadCount'] = 0;
              mutated = true;
            }
            // Notification-shaped objects carry an isRead flag.
            if (_hasKey(node, 'isRead')) {
              final wasRead = (node['isRead'] ?? node['IsRead']) == true;
              if (!wasRead) {
                final raw = node['id'] ?? node['Id'];
                final id =
                    raw is num ? raw.toInt() : int.tryParse('${raw ?? ''}');
                if (id != null) unreadIds.add(id);
                node['isRead'] = true;
                node['readAt'] = nowIso;
                mutated = true;
              }
            }
            for (final v in node.values) {
              walk(v);
            }
          } else if (node is List) {
            for (final item in node) {
              walk(item);
            }
          }
        }

        walk(decoded);
        if (mutated) {
          await offlineStore.writeCache(
              entry.cacheKey, entry.endpoint, jsonEncode(decoded));
        }
      }
    } catch (_) {
      // Patching is best-effort; failure just means no optimistic update.
    }
    return unreadIds.toList();
  }

  /// Key probe tolerant of the backend's mixed camelCase/PascalCase payloads
  /// (same contract the models' fromJson handles).
  static bool _hasKey(Map<String, dynamic> m, String camel) =>
      m.containsKey(camel) ||
      m.containsKey(camel[0].toUpperCase() + camel.substring(1));

  /// Patch a cached transaction's user notes across any cached list. Returns the
  /// patched item JSON (for the caller to re-parse), or null if nothing cached.
  Future<Map<String, dynamic>?> patchTransactionNotes(int id, String? notes) {
    return _patchCachedById(
      prefix: '/transactions',
      id: id,
      isTarget: (m) => _hasKey(m, 'amount') && _hasKey(m, 'accountId'),
      mutate: (m) => m['userNotes'] = notes,
    );
  }

  static double _numOf(Map<String, dynamic> m, String camel) {
    final pascal = camel[0].toUpperCase() + camel.substring(1);
    final v = m[camel] ?? m[pascal];
    return v is num ? v.toDouble() : 0;
  }

  /// Patch a cached budget category's fields. Returns the patched category JSON.
  /// Server payloads carry DERIVED numbers (remaining, percentage) alongside the
  /// raw ones — recompute them so every number on the card moves, not just the
  /// allocated amount (live test: allocated updated but "remaining" showed stale).
  Future<Map<String, dynamic>?> patchBudgetCategory(
    int categoryId,
    Map<String, dynamic> changes,
  ) {
    return _patchCachedById(
      prefix: '/budget',
      id: categoryId,
      isTarget: (m) => _hasKey(m, 'spentAmount'),
      mutate: (m) {
        m.addAll(changes);
        final allocated = _numOf(m, 'allocatedAmount');
        final spent = _numOf(m, 'spentAmount');
        m['remainingAmount'] = allocated - spent;
        m['percentageUsed'] =
            allocated > 0 ? (spent / allocated * 100) : 0.0;
      },
    );
  }

  /// Patch a cached savings goal's fields. Returns the patched goal JSON.
  /// Prefix is '/savings' (not '/savings/goals') because the savings page loads
  /// goals embedded in GET /savings/overview — the narrower prefix missed them
  /// entirely and offline goal edits failed (live-test finding).
  Future<Map<String, dynamic>?> patchSavingsGoal(
    int goalId,
    Map<String, dynamic> changes,
  ) {
    return _patchCachedById(
      prefix: '/savings',
      id: goalId,
      isTarget: (m) => _hasKey(m, 'targetAmount'),
      mutate: (m) => m.addAll(changes),
    );
  }

  /// Directly overwrite a cache key with a fresh JSON body (used by settings
  /// writes to reflect the new value locally while offline).
  Future<void> primeCache(String key, Map<String, dynamic> body) async {
    try {
      await offlineStore.writeCache(key, key, jsonEncode(body));
    } catch (_) {}
  }

  // ── Internals ─────────────────────────────────────────────────────────────

  List<int> _ids(List<SyncOp> ops) =>
      ops.where((o) => o.id != null).map((o) => o.id!).toList();

  bool _eligible(SyncOp op, DateTime now) {
    if (op.retryCount == 0 || op.lastAttemptAt == null) return true;
    final minutes =
        math.min(math.pow(2, op.retryCount).toDouble(), 24 * 60).round();
    final next = op.lastAttemptAt!.add(Duration(minutes: minutes));
    return !now.isBefore(next);
  }

  bool _isRetryableStatus(DioException e) {
    final s = e.response?.statusCode;
    return s != null && (s == 429 || s >= 500);
  }

  Map<String, dynamic> _opToJson(SyncOp op) {
    dynamic payload;
    try {
      payload = jsonDecode(op.payload);
    } catch (_) {
      payload = <String, dynamic>{};
    }
    return {
      'idempotencyKey': op.idempotencyKey,
      'operationType': op.operationType,
      'entityId': op.entityId,
      'payload': payload,
      'queuedAt': op.queuedAt.toUtc().toIso8601String(),
      if (op.baseUpdatedAt != null)
        'baseUpdatedAt': op.baseUpdatedAt!.toUtc().toIso8601String(),
    };
  }

  /// Cache prefixes to invalidate when an op of [operationType] conflicts.
  Set<String> _cachePrefixesFor(String operationType) {
    switch (operationType) {
      case opBudgetCategoryUpdate:
        return {'/budget'};
      case opSavingsGoalUpdate:
        return {'/savings'};
      case opTransactionNotesUpdate:
        return {'/transactions'};
      case opNotificationMarkRead:
      case opNotificationArchive:
        return {'/notifications'};
      case opUserSettingsBudgetUpdate:
        return {'/user/settings/budget'};
      case opUserSettingsAiUpdate:
        return {'/user/settings/ai'};
      default:
        return const {};
    }
  }

  /// Walk every cache entry under [prefix], mutate any nested JSON object whose
  /// `id` equals [id] (and passes [isTarget]), and write the patched payload
  /// back. Returns a copy of the first matched object (or null on no match).
  Future<Map<String, dynamic>?> _patchCachedById({
    required String prefix,
    required int id,
    required void Function(Map<String, dynamic>) mutate,
    bool Function(Map<String, dynamic>)? isTarget,
  }) async {
    Map<String, dynamic>? firstMatch;
    try {
      final entries = await offlineStore.readCacheByPrefix(prefix);
      for (final entry in entries) {
        dynamic decoded;
        try {
          decoded = jsonDecode(entry.payload);
        } catch (_) {
          continue;
        }
        var mutated = false;

        void walk(dynamic node) {
          if (node is Map<String, dynamic>) {
            final raw = node['id'] ?? node['Id'];
            final mid =
                raw is num ? raw.toInt() : int.tryParse('${raw ?? ''}');
            if (mid == id && (isTarget == null || isTarget(node))) {
              mutate(node);
              mutated = true;
              firstMatch ??= Map<String, dynamic>.from(node);
            }
            for (final v in node.values) {
              walk(v);
            }
          } else if (node is List) {
            for (final item in node) {
              walk(item);
            }
          }
        }

        walk(decoded);
        if (mutated) {
          await offlineStore.writeCache(
              entry.cacheKey, entry.endpoint, jsonEncode(decoded));
        }
      }
    } catch (_) {
      // Patching is best-effort; failure just means no optimistic update.
    }
    return firstMatch;
  }
}

/// Global singleton. Non-final so tests can swap in a fresh instance.
SyncService syncService = SyncService();
