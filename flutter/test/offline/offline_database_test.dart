import 'package:drift/native.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/offline/offline_database.dart';
import 'package:buoy_ai/offline/offline_store.dart';

/// DriftOfflineStore exercised against an in-memory SQLite database. `drift/native`
/// is imported only here (never in lib/) so the web build stays dart:io-free.
void main() {
  late OfflineDatabase db;
  late DriftOfflineStore store;

  setUp(() {
    db = OfflineDatabase.forTesting(NativeDatabase.memory());
    store = DriftOfflineStore(db);
  });

  tearDown(() async {
    await db.close();
  });

  SyncOp op(String key, {String type = 'notification_mark_read', String entity = '1'}) =>
      SyncOp(
        idempotencyKey: key,
        operationType: type,
        entityId: entity,
        payload: '{}',
        queuedAt: DateTime.now().toUtc(),
      );

  group('cache', () {
    test('write then read round-trips payload + fetchedAt', () async {
      await store.writeCache('/transactions?page=1', '/transactions', '{"a":1}');
      final hit = await store.readCache('/transactions?page=1');
      expect(hit, isNotNull);
      expect(hit!.payload, '{"a":1}');
      expect(hit.endpoint, '/transactions');
      expect(hit.fetchedAt, isA<DateTime>());
    });

    test('read miss returns null', () async {
      expect(await store.readCache('/nope'), isNull);
    });

    test('writeCache upserts (same key overwrites)', () async {
      await store.writeCache('/k', '/k', 'first');
      await store.writeCache('/k', '/k', 'second');
      final hit = await store.readCache('/k');
      expect(hit!.payload, 'second');
    });

    test('readCacheByPrefix returns only matching entries', () async {
      await store.writeCache('/budget', '/budget', 'b');
      await store.writeCache('/budget/active', '/budget/active', 'ba');
      await store.writeCache('/transactions', '/transactions', 't');
      final rows = await store.readCacheByPrefix('/budget');
      expect(rows.map((e) => e.cacheKey).toSet(),
          {'/budget', '/budget/active'});
    });

    test('invalidatePrefix deletes matching entries only', () async {
      await store.writeCache('/budget', '/budget', 'b');
      await store.writeCache('/budget/active', '/budget/active', 'ba');
      await store.writeCache('/transactions', '/transactions', 't');
      await store.invalidatePrefix('/budget');
      expect(await store.readCache('/budget'), isNull);
      expect(await store.readCache('/budget/active'), isNull);
      expect(await store.readCache('/transactions'), isNotNull);
    });
  });

  group('outbox', () {
    test('enqueue + pendingOps preserves queuedAt order', () async {
      // Insert out of chronological order to prove ordering is by queuedAt.
      await db.into(db.outboxEntries).insert(
        _companion('k-late', DateTime.utc(2026, 1, 2)),
      );
      await db.into(db.outboxEntries).insert(
        _companion('k-early', DateTime.utc(2026, 1, 1)),
      );
      final pending = await store.pendingOps();
      expect(pending.map((o) => o.idempotencyKey), ['k-early', 'k-late']);
    });

    test('removeOps deletes the given rows', () async {
      final id1 = await store.enqueue(op('a'));
      final id2 = await store.enqueue(op('b'));
      await store.removeOps([id1]);
      final pending = await store.pendingOps();
      expect(pending.map((o) => o.id), [id2]);
    });

    test('bumpRetry increments retryCount + stamps lastAttemptAt', () async {
      final id = await store.enqueue(op('a'));
      await store.bumpRetry([id]);
      await store.bumpRetry([id]);
      final pending = await store.pendingOps();
      expect(pending.single.retryCount, 2);
      expect(pending.single.lastAttemptAt, isNotNull);
    });

    test('idempotencyKey is unique', () async {
      final id1 = await store.enqueue(op('dupe'));
      expect(id1, greaterThan(0));
      // Second insert with same key is swallowed (returns -1), not thrown.
      final id2 = await store.enqueue(op('dupe'));
      expect(id2, -1);
      expect((await store.pendingOps()).length, 1);
    });
  });

  group('meta + wipe', () {
    test('setMeta/getMeta round-trips and upserts', () async {
      expect(await store.getMeta('k'), isNull);
      await store.setMeta('k', 'v1');
      expect(await store.getMeta('k'), 'v1');
      await store.setMeta('k', 'v2');
      expect(await store.getMeta('k'), 'v2');
    });

    test('wipe clears cache + outbox + meta', () async {
      await store.writeCache('/k', '/k', 'v');
      await store.enqueue(op('a'));
      await store.setMeta('m', 'v');
      await store.wipe();
      expect(await store.readCache('/k'), isNull);
      expect(await store.pendingOps(), isEmpty);
      expect(await store.getMeta('m'), isNull);
    });
  });
}

OutboxEntriesCompanion _companion(String key, DateTime queuedAt) =>
    OutboxEntriesCompanion.insert(
      idempotencyKey: key,
      operationType: 'notification_mark_read',
      entityId: '1',
      payload: '{}',
      queuedAt: queuedAt,
    );
