import 'package:drift/drift.dart';
import 'package:flutter/foundation.dart' show kIsWeb;

import 'offline_database.dart';

/// A single queued offline write operation (outbox row). Mirrors the sync
/// contract's operation shape; [id] is the local autoincrement PK (null before
/// insert), the rest map 1:1 to POST /sync/batch operation fields.
class SyncOp {
  final int? id;
  final String idempotencyKey;
  final String operationType;
  final String entityId;

  /// Operation payload as a raw JSON string (same body the live PUT/POST sent).
  final String payload;
  final DateTime queuedAt;
  final DateTime? baseUpdatedAt;
  final int retryCount;
  final DateTime? lastAttemptAt;

  const SyncOp({
    this.id,
    required this.idempotencyKey,
    required this.operationType,
    required this.entityId,
    required this.payload,
    required this.queuedAt,
    this.baseUpdatedAt,
    this.retryCount = 0,
    this.lastAttemptAt,
  });
}

/// A cached GET response row.
class CachedEntry {
  final String cacheKey;
  final String endpoint;
  final String payload;
  final DateTime fetchedAt;
  const CachedEntry(this.cacheKey, this.endpoint, this.payload, this.fetchedAt);
}

/// Abstraction over the on-device cache + write outbox.
///
/// Two implementations: [DriftOfflineStore] (mobile, SQLite-backed) and
/// [NoopOfflineStore] (web + default-before-init, everything is a no-op). Every
/// method is failure-tolerant by contract: an implementation must NEVER throw
/// out of a read/write — a broken cache must degrade to plain network behaviour,
/// never break a normal online request. Callers rely on this.
abstract class OfflineStore {
  /// Returns `(payload, fetchedAt)` for [key], or null on miss/error.
  Future<CachedEntry?> readCache(String key);

  /// All cache entries whose key starts with [prefix] (for optimistic patching).
  Future<List<CachedEntry>> readCacheByPrefix(String prefix);

  Future<void> writeCache(String key, String endpoint, String payload);

  /// Delete every cache entry whose key starts with [prefix] (force refetch).
  Future<void> invalidatePrefix(String prefix);

  /// Insert an outbox op; returns the assigned row id (or -1 on failure).
  Future<int> enqueue(SyncOp op);

  /// Pending ops, oldest [queuedAt] first.
  Future<List<SyncOp>> pendingOps();

  Future<void> removeOps(List<int> ids);

  /// Increment retryCount and stamp lastAttemptAt=now for the given rows.
  Future<void> bumpRetry(List<int> ids);

  Future<String?> getMeta(String key);
  Future<void> setMeta(String key, String value);

  /// Drop all cached responses + outbox + meta (user switch / logout).
  Future<void> wipe();

  Future<void> close();
}

/// Web + pre-init implementation: no local persistence, so every read misses and
/// every write is dropped. This is what keeps web on today's network-only path.
class NoopOfflineStore implements OfflineStore {
  const NoopOfflineStore();

  @override
  Future<CachedEntry?> readCache(String key) async => null;
  @override
  Future<List<CachedEntry>> readCacheByPrefix(String prefix) async => const [];
  @override
  Future<void> writeCache(String key, String endpoint, String payload) async {}
  @override
  Future<void> invalidatePrefix(String prefix) async {}
  @override
  Future<int> enqueue(SyncOp op) async => -1;
  @override
  Future<List<SyncOp>> pendingOps() async => const [];
  @override
  Future<void> removeOps(List<int> ids) async {}
  @override
  Future<void> bumpRetry(List<int> ids) async {}
  @override
  Future<String?> getMeta(String key) async => null;
  @override
  Future<void> setMeta(String key, String value) async {}
  @override
  Future<void> wipe() async {}
  @override
  Future<void> close() async {}
}

/// Drift/SQLite-backed store (mobile). Every method swallows its own errors and
/// degrades gracefully — see [OfflineStore]'s failure-tolerance contract.
///
/// Security note (#493): the outbox + cache are NOT encrypted at rest (no
/// SQLCipher — those packages are EOL). Wipe-on-logout / wipe-on-user-switch is
/// the control; see [wipe] and reconcileCacheOwner. No secrets are ever cached —
/// only user-scoped app data behind the OS app sandbox.
class DriftOfflineStore implements OfflineStore {
  DriftOfflineStore(this._db);

  final OfflineDatabase _db;

  @override
  Future<CachedEntry?> readCache(String key) async {
    try {
      final row = await (_db.select(_db.cachedResponses)
            ..where((t) => t.cacheKey.equals(key)))
          .getSingleOrNull();
      if (row == null) return null;
      return CachedEntry(row.cacheKey, row.endpoint, row.payload, row.fetchedAt);
    } catch (_) {
      return null;
    }
  }

  @override
  Future<List<CachedEntry>> readCacheByPrefix(String prefix) async {
    try {
      final rows = await (_db.select(_db.cachedResponses)
            ..where((t) => t.cacheKey.like('$prefix%')))
          .get();
      return rows
          .map((r) => CachedEntry(r.cacheKey, r.endpoint, r.payload, r.fetchedAt))
          .toList();
    } catch (_) {
      return const [];
    }
  }

  @override
  Future<void> writeCache(String key, String endpoint, String payload) async {
    try {
      await _db.into(_db.cachedResponses).insertOnConflictUpdate(
            CachedResponsesCompanion.insert(
              cacheKey: key,
              endpoint: endpoint,
              payload: payload,
              fetchedAt: DateTime.now().toUtc(),
            ),
          );
    } catch (_) {
      // Cache write is best-effort; never break the live request path.
    }
  }

  @override
  Future<void> invalidatePrefix(String prefix) async {
    try {
      await (_db.delete(_db.cachedResponses)
            ..where((t) => t.cacheKey.like('$prefix%')))
          .go();
    } catch (_) {}
  }

  @override
  Future<int> enqueue(SyncOp op) async {
    try {
      return await _db.into(_db.outboxEntries).insert(
            OutboxEntriesCompanion.insert(
              idempotencyKey: op.idempotencyKey,
              operationType: op.operationType,
              entityId: op.entityId,
              payload: op.payload,
              queuedAt: op.queuedAt,
              baseUpdatedAt: Value(op.baseUpdatedAt),
            ),
          );
    } catch (_) {
      return -1;
    }
  }

  @override
  Future<List<SyncOp>> pendingOps() async {
    try {
      final rows = await (_db.select(_db.outboxEntries)
            ..orderBy([(t) => OrderingTerm.asc(t.queuedAt)]))
          .get();
      return rows.map(_toOp).toList();
    } catch (_) {
      return const [];
    }
  }

  @override
  Future<void> removeOps(List<int> ids) async {
    if (ids.isEmpty) return;
    try {
      await (_db.delete(_db.outboxEntries)..where((t) => t.id.isIn(ids))).go();
    } catch (_) {}
  }

  @override
  Future<void> bumpRetry(List<int> ids) async {
    if (ids.isEmpty) return;
    try {
      await _db.customUpdate(
        'UPDATE outbox_entries SET retry_count = retry_count + 1, '
        'last_attempt_at = ? WHERE id IN (${List.filled(ids.length, '?').join(',')})',
        variables: [
          Variable.withDateTime(DateTime.now().toUtc()),
          ...ids.map(Variable.withInt),
        ],
        updates: {_db.outboxEntries},
      );
    } catch (_) {}
  }

  @override
  Future<String?> getMeta(String key) async {
    try {
      final row = await (_db.select(_db.syncMeta)
            ..where((t) => t.metaKey.equals(key)))
          .getSingleOrNull();
      return row?.metaValue;
    } catch (_) {
      return null;
    }
  }

  @override
  Future<void> setMeta(String key, String value) async {
    try {
      await _db.into(_db.syncMeta).insertOnConflictUpdate(
            SyncMetaCompanion.insert(metaKey: key, metaValue: value),
          );
    } catch (_) {}
  }

  @override
  Future<void> wipe() async {
    try {
      await _db.batch((b) {
        b.deleteAll(_db.cachedResponses);
        b.deleteAll(_db.outboxEntries);
        b.deleteAll(_db.syncMeta);
      });
    } catch (_) {}
  }

  @override
  Future<void> close() async {
    try {
      await _db.close();
    } catch (_) {}
  }

  SyncOp _toOp(OutboxEntry r) => SyncOp(
        id: r.id,
        idempotencyKey: r.idempotencyKey,
        operationType: r.operationType,
        entityId: r.entityId,
        payload: r.payload,
        queuedAt: r.queuedAt,
        baseUpdatedAt: r.baseUpdatedAt,
        retryCount: r.retryCount,
        lastAttemptAt: r.lastAttemptAt,
      );
}

/// Builds the platform-appropriate store: Noop on web (network-only), a
/// Drift/SQLite store on mobile.
OfflineStore createOfflineStore() {
  if (kIsWeb) return const NoopOfflineStore();
  return DriftOfflineStore(OfflineDatabase());
}

/// Global store singleton. Defaults to Noop so any code path that runs before
/// [initOfflineStore] (e.g. unit tests that never init) is safe. Non-final so
/// tests can swap in a Drift-backed store over an in-memory database.
OfflineStore offlineStore = const NoopOfflineStore();

/// Initialise the real store. Call once from `main()` before `runApp`. Cheap:
/// drift opens the database lazily on first query, so this doesn't block launch.
Future<void> initOfflineStore() async {
  offlineStore = createOfflineStore();
}

/// Wipe everything (logout / unauthorized). Guarded so it never throws.
Future<void> wipeOfflineCache() async {
  try {
    await offlineStore.wipe();
  } catch (_) {}
}

/// Ensure the cache belongs to [userId]; if a different user owned it, wipe
/// first. Call on every successful auth so a shared device can't serve one
/// user's cached data to another.
Future<void> reconcileCacheOwner(String userId) async {
  try {
    final prev = await offlineStore.getMeta('cache_owner_user_id');
    if (prev != null && prev != userId) {
      await offlineStore.wipe();
    }
    await offlineStore.setMeta('cache_owner_user_id', userId);
  } catch (_) {}
}
