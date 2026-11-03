import 'dart:async';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/offline/connectivity_service.dart';
import 'package:buoy_ai/offline/offline_database.dart';
import 'package:buoy_ai/offline/offline_status.dart';
import 'package:buoy_ai/offline/offline_store.dart';
import 'package:buoy_ai/offline/sync_service.dart';
import 'package:buoy_ai/services/api_client.dart';

/// Programmable Dio adapter for the POST /sync/batch call inside drainOutbox.
class _BatchAdapter implements HttpClientAdapter {
  final List<String> queuedBodies = [];
  bool throwNext = false;
  int? statusNext;
  int requests = 0;
  Completer<void>? gate;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests++;
    if (gate != null) await gate!.future;
    if (throwNext) {
      throw DioException(
        requestOptions: options,
        type: DioExceptionType.connectionError,
        error: 'SocketException: Failed host lookup',
      );
    }
    final status = statusNext ?? 200;
    statusNext = null;
    final body = queuedBodies.isNotEmpty
        ? queuedBodies.removeAt(0)
        : (status == 200 ? '{"results":[]}' : '{"message":"unauthorized"}');
    return ResponseBody.fromString(
      body,
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late OfflineDatabase db;
  late DriftOfflineStore store;
  late _BatchAdapter adapter;

  SyncOp op(String key, String type, String entity) => SyncOp(
        idempotencyKey: key,
        operationType: type,
        entityId: entity,
        payload: '{}',
        queuedAt: DateTime.now().toUtc(),
      );

  // The drain is auth-gated (#493 live-test fix): it only runs with a stored
  // token. Reads of 'authToken' return one; everything else (incl. the refresh
  // token) stays null so a 401 can't trigger a real network refresh attempt.
  var storedAuthToken = 'test-token';
  void installStorageMock() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async {
        if (call.method == 'read') {
          final key = (call.arguments as Map)['key'];
          if (key == 'authToken') {
            return storedAuthToken.isEmpty ? null : storedAuthToken;
          }
        }
        return null;
      },
    );
  }

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    storedAuthToken = 'test-token';
    installStorageMock();

    db = OfflineDatabase.forTesting(NativeDatabase.memory());
    store = DriftOfflineStore(db);
    offlineStore = store;
    offlineStatus = OfflineStatus();
    connectivityService = ConnectivityService();
    syncService = SyncService();

    adapter = _BatchAdapter();
    apiClient.dio.httpClientAdapter = adapter;
  });

  tearDown(() async {
    offlineStore = const NoopOfflineStore();
    await db.close();
  });

  test('maps applied/duplicate/conflict/failed and invalidates on conflict',
      () async {
    await store.enqueue(op('k-applied', SyncService.opNotificationMarkRead, '1'));
    await store.enqueue(op('k-dup', SyncService.opNotificationArchive, '2'));
    await store.enqueue(
        op('k-conflict', SyncService.opBudgetCategoryUpdate, '3'));
    await store.enqueue(
        op('k-failed', SyncService.opTransactionNotesUpdate, '4'));

    // Cache entries the conflict should invalidate (/budget) and one it shouldn't.
    await store.writeCache('/budget', '/budget', '[]');
    await store.writeCache('/transactions', '/transactions', '{}');

    adapter.queuedBodies.add('''
      {"results":[
        {"idempotencyKey":"k-applied","status":"applied","error":null},
        {"idempotencyKey":"k-dup","status":"duplicate","error":null},
        {"idempotencyKey":"k-conflict","status":"conflict","error":null},
        {"idempotencyKey":"k-failed","status":"failed","error":"not found"}
      ]}''');

    await syncService.drainOutbox();

    expect(await store.pendingOps(), isEmpty,
        reason: 'all four terminal statuses remove the op');
    expect(await store.readCache('/budget'), isNull,
        reason: 'conflict invalidates the /budget prefix');
    expect(await store.readCache('/transactions'), isNotNull,
        reason: 'unrelated cache prefixes untouched');
    expect(offlineStatus.pendingOutboxCount, 0);
  });

  test('transport failure keeps ops and bumps retry', () async {
    await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));
    adapter.throwNext = true;

    await syncService.drainOutbox();

    final pending = await store.pendingOps();
    expect(pending.length, 1, reason: 'op is kept on transport failure');
    expect(pending.single.retryCount, 1);
    expect(pending.single.lastAttemptAt, isNotNull);
  });

  test('backoff gate suppresses a not-yet-due op (no batch call made)',
      () async {
    final id = await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));
    // 3 retries → backoff 8 minutes; lastAttemptAt is "now", so it is not due.
    await store.bumpRetry([id]);
    await store.bumpRetry([id]);
    await store.bumpRetry([id]);

    await syncService.drainOutbox();

    expect(adapter.requests, 0, reason: 'nothing eligible → no POST');
    final pending = await store.pendingOps();
    expect(pending.single.retryCount, 3, reason: 'op untouched behind gate');
  });

  test('op is dropped after 20 retries with no batch call', () async {
    final id = await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));
    for (var i = 0; i < 20; i++) {
      await store.bumpRetry([id]);
    }

    await syncService.drainOutbox();

    expect(await store.pendingOps(), isEmpty,
        reason: 'retryCount >= 20 is permanently dropped');
    expect(adapter.requests, 0,
        reason: 'the dropped op was the only one — nothing to send');
  });

  test('concurrency guard: a second drain while one is in flight is a no-op',
      () async {
    await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));
    adapter.gate = Completer<void>();
    adapter.queuedBodies.add(
        '{"results":[{"idempotencyKey":"k1","status":"applied","error":null}]}');

    // First drain starts and blocks inside the adapter on the gate.
    final first = syncService.drainOutbox();
    await Future<void>.delayed(const Duration(milliseconds: 20));

    // Second drain should bail immediately (guard), issuing no new request.
    await syncService.drainOutbox();
    expect(adapter.requests, 1, reason: 'guard blocks the concurrent drain');

    // Release the first; it completes normally.
    adapter.gate!.complete();
    await first;
    expect(await store.pendingOps(), isEmpty);
  });

  test('drain is auth-gated: no stored token → no batch call, ops untouched',
      () async {
    storedAuthToken = '';
    await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));

    await syncService.drainOutbox();

    expect(adapter.requests, 0, reason: 'logged out — must not POST');
    final pending = await store.pendingOps();
    expect(pending.length, 1);
    expect(pending.single.retryCount, 0,
        reason: 'waiting for login must not burn the retry budget');
  });

  test('drain skips while believed offline (no retry bump)', () async {
    connectivityService.reportNetworkFailure();
    connectivityService.reportNetworkFailure();
    await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));

    await syncService.drainOutbox();

    expect(adapter.requests, 0);
    expect((await store.pendingOps()).single.retryCount, 0);
  });

  test('batch 401 keeps ops without burning retry budget', () async {
    await store.enqueue(op('k1', SyncService.opNotificationMarkRead, '1'));
    adapter.statusNext = 401;

    await syncService.drainOutbox();

    final pending = await store.pendingOps();
    expect(pending.length, 1, reason: 'op kept for the post-login drain');
    expect(pending.single.retryCount, 0,
        reason: '401 is an auth state, not a transport failure');
  });
}
