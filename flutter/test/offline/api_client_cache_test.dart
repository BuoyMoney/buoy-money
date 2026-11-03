import 'package:dio/dio.dart';
import 'package:drift/native.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/offline/connectivity_service.dart';
import 'package:buoy_ai/offline/offline_database.dart';
import 'package:buoy_ai/offline/offline_status.dart';
import 'package:buoy_ai/offline/offline_store.dart';
import 'package:buoy_ai/services/api_client.dart';

/// Dio adapter that returns a canned body, or (when [fail] is set) throws a
/// transport-type DioException to simulate the device going offline.
class _SwitchAdapter implements HttpClientAdapter {
  _SwitchAdapter(this.statusCode, this.body);
  final int statusCode;
  final String body;
  bool fail = false;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    if (fail) {
      throw DioException(
        requestOptions: options,
        type: DioExceptionType.connectionError,
        error: 'SocketException: Failed host lookup: api',
      );
    }
    return ResponseBody.fromString(
      body,
      statusCode,
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
  late _SwitchAdapter adapter;
  late ApiClient client;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );

    // Fresh offline globals per test.
    db = OfflineDatabase.forTesting(NativeDatabase.memory());
    offlineStore = DriftOfflineStore(db);
    offlineStatus = OfflineStatus();
    connectivityService = ConnectivityService();

    adapter = _SwitchAdapter(200, '{"foo":"bar"}');
    client = ApiClient();
    client.dio.httpClientAdapter = adapter;
  });

  tearDown(() async {
    offlineStore = const NoopOfflineStore();
    await db.close();
  });

  test('successful cacheable GET populates the cache', () async {
    final res = await client.get<Map<String, dynamic>>('/dashboard');
    expect(res['foo'], 'bar');

    final cached = await offlineStore.readCache('/dashboard');
    expect(cached, isNotNull);
    expect(cached!.payload, contains('bar'));
  });

  test('failed cacheable GET returns cached payload + flags offlineStatus',
      () async {
    // Warm the cache with a live success first.
    await client.get<Map<String, dynamic>>('/dashboard');
    expect(offlineStatus.showingCachedData, isFalse);

    // Now the network drops.
    adapter.fail = true;
    final res = await client.get<Map<String, dynamic>>('/dashboard');
    expect(res['foo'], 'bar', reason: 'served from cache');
    expect(offlineStatus.showingCachedData, isTrue);
    expect(offlineStatus.oldestServedCacheTime, isNotNull);
  });

  test('non-cacheable path is not stored', () async {
    await client.get<Map<String, dynamic>>('/family/1/role');
    expect(await offlineStore.readCache('/family/1/role'), isNull);
  });

  test('cacheable GET with no cached copy rethrows the network error',
      () async {
    adapter.fail = true;
    await expectLater(
      client.get<Map<String, dynamic>>('/insights'),
      throwsA(isA<DioException>()),
    );
  });

  test('cache key folds query params in canonical (sorted) order', () async {
    await client.get<Map<String, dynamic>>(
      '/transactions',
      queryParameters: {'pageSize': 25, 'page': 1},
    );
    // Sorted: page before pageSize.
    expect(
        await offlineStore.readCache('/transactions?page=1&pageSize=25'),
        isNotNull);
  });
}
