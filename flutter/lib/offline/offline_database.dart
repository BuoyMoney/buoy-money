import 'package:drift/drift.dart';
import 'package:drift_flutter/drift_flutter.dart';

part 'offline_database.g.dart';

/// Cached GET responses (mobile offline-first cache, #493).
///
/// One row per canonical request key. [payload] is the raw JSON string exactly
/// as the backend returned it, so a cache hit re-runs the same `fromJson` the
/// live path would.
class CachedResponses extends Table {
  TextColumn get cacheKey => text()();
  TextColumn get endpoint => text()();
  TextColumn get payload => text()();
  DateTimeColumn get fetchedAt => dateTime()();

  @override
  Set<Column> get primaryKey => {cacheKey};
}

/// Pending offline write operations (the outbox). Drained to POST /sync/batch
/// when connectivity returns. [idempotencyKey] dedupes replays server-side.
class OutboxEntries extends Table {
  IntColumn get id => integer().autoIncrement()();
  TextColumn get idempotencyKey => text().unique()();
  TextColumn get operationType => text()();
  TextColumn get entityId => text()();
  TextColumn get payload => text()();
  DateTimeColumn get queuedAt => dateTime()();
  DateTimeColumn get baseUpdatedAt => dateTime().nullable()();
  IntColumn get retryCount => integer().withDefault(const Constant(0))();
  DateTimeColumn get lastAttemptAt => dateTime().nullable()();
}

/// Small key/value store for sync bookkeeping (cache owner user id, last
/// background sync timestamp, etc.).
class SyncMeta extends Table {
  TextColumn get metaKey => text()();
  TextColumn get metaValue => text()();

  @override
  Set<Column> get primaryKey => {metaKey};
}

@DriftDatabase(tables: [CachedResponses, OutboxEntries, SyncMeta])
class OfflineDatabase extends _$OfflineDatabase {
  /// Production constructor — opens (or creates) the on-device SQLite file via
  /// drift_flutter, which bundles the native library and picks a platform-safe
  /// path. Never used on web (the factory returns a NoopOfflineStore there).
  OfflineDatabase() : super(driftDatabase(name: 'buoyai_offline'));

  /// Test constructor — inject an in-memory (or any) executor. Tests pass
  /// `NativeDatabase.memory()` from package:drift/native.dart (test-only import).
  OfflineDatabase.forTesting(super.e);

  @override
  int get schemaVersion => 1;

  /// The main isolate and the workmanager background isolate each open their
  /// own connection to the same file. WAL + a busy timeout make a concurrent
  /// write wait (up to 5s) instead of failing SQLITE_BUSY — without this, a
  /// swallowed busy error in [DriftOfflineStore.enqueue] would silently drop a
  /// user's offline edit.
  @override
  MigrationStrategy get migration => MigrationStrategy(
        beforeOpen: (details) async {
          await customStatement('PRAGMA journal_mode=WAL');
          await customStatement('PRAGMA busy_timeout=5000');
        },
      );
}
