import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/offline/offline_database.dart';
import 'package:buoy_ai/offline/offline_status.dart';
import 'package:buoy_ai/offline/offline_store.dart';
import 'package:buoy_ai/offline/sync_service.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/notification_service.dart';

/// Adapter that always fails with a transport error (device offline).
class _OfflineAdapter implements HttpClientAdapter {
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    throw DioException(
      requestOptions: options,
      type: DioExceptionType.connectionError,
      error: 'SocketException: Failed host lookup',
    );
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late OfflineDatabase db;
  late DriftOfflineStore store;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );

    db = OfflineDatabase.forTesting(NativeDatabase.memory());
    store = DriftOfflineStore(db);
    offlineStore = store;
    offlineStatus = OfflineStatus();
    syncService = SyncService();
    apiClient.dio.httpClientAdapter = _OfflineAdapter();
  });

  tearDown(() async {
    offlineStore = const NoopOfflineStore();
    await db.close();
  });

  test('markAsRead on a network error queues the op and does not throw',
      () async {
    await expectLater(notificationService.markAsRead(42), completes);

    final pending = await store.pendingOps();
    expect(pending.length, 1);
    expect(pending.single.operationType, SyncService.opNotificationMarkRead);
    expect(pending.single.entityId, '42');
    expect(offlineStatus.pendingOutboxCount, 1);
  });

  test('archive on a network error queues an archive op', () async {
    await expectLater(notificationService.archive(7), completes);

    final pending = await store.pendingOps();
    expect(pending.single.operationType, SyncService.opNotificationArchive);
    expect(pending.single.entityId, '7');
  });

  test(
      'markAllAsRead on a network error patches the cache to read and enqueues '
      'a mark-read op per previously-unread id (no throw)', () async {
    // Seed a cached /notifications list: one unread (id 10), one already-read
    // (id 11), plus a summary payload carrying an unread badge count.
    await store.writeCache(
      '/notifications?page=1&pageSize=20',
      '/notifications',
      jsonEncode([
        {'id': 10, 'isRead': false, 'title': 'A'},
        {'id': 11, 'isRead': true, 'title': 'B'},
      ]),
    );
    await store.writeCache(
      '/notifications/summary',
      '/notifications/summary',
      jsonEncode({
        'unreadCount': 1,
        'totalCount': 2,
        'recent': [
          {'id': 10, 'isRead': false, 'title': 'A'},
        ],
      }),
    );

    await expectLater(notificationService.markAllAsRead(), completes);

    // Every cached notification is now read, and the summary badge is zeroed.
    final entries = await store.readCacheByPrefix('/notifications');
    for (final entry in entries) {
      final decoded = jsonDecode(entry.payload);
      void assertRead(dynamic node) {
        if (node is Map) {
          if (node.containsKey('isRead')) expect(node['isRead'], isTrue);
          if (node.containsKey('unreadCount')) expect(node['unreadCount'], 0);
          node.values.forEach(assertRead);
        } else if (node is List) {
          node.forEach(assertRead);
        }
      }

      assertRead(decoded);
    }

    // Only the previously-unread id (10) is enqueued; already-read (11) is not.
    final pending = await store.pendingOps();
    expect(pending.length, 1);
    expect(pending.single.operationType, SyncService.opNotificationMarkRead);
    expect(pending.single.entityId, '10');
  });
}
