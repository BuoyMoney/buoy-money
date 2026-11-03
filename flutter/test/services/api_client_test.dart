import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:buoy_ai/services/api_client.dart';

/// A Dio HttpClientAdapter that returns a single canned response for every request,
/// so we can exercise the ApiClient interceptor + verb helpers without a real backend.
class _CannedAdapter implements HttpClientAdapter {
  _CannedAdapter(this.statusCode, this.body);

  final int statusCode;
  final String body;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
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

ApiClient _clientReturning(int statusCode, String body) {
  final client = ApiClient();
  client.dio.httpClientAdapter = _CannedAdapter(statusCode, body);
  return client;
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    // ApiClient touches flutter_secure_storage + shared_preferences on every request
    // (to read the auth token). Stub both so unit tests don't hit missing plugins.
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
  });

  group('403 body-shape guard (finding #1)', () {
    test('403 with an EMPTY body surfaces as a DioException, not a TypeError',
        () async {
      // A Forbid() authorization denial returns 403 with no body; Dio decodes that
      // to an empty String. The interceptor must not index `''['error']`.
      final client = _clientReturning(403, '');
      DioException? caught;
      try {
        await client.get<dynamic>('/family/members/1/role');
      } on DioException catch (e) {
        caught = e;
      }
      expect(caught, isNotNull,
          reason: 'an empty-body 403 must propagate as a DioException');
      expect(caught!.response?.statusCode, 403);
    });

    test('403 MfaRequired JSON body still propagates for callers to handle',
        () async {
      final client = _clientReturning(403, '{"error":"MfaRequired"}');
      DioException? caught;
      try {
        await client.get<dynamic>('/some/protected');
      } on DioException catch (e) {
        caught = e;
      }
      expect(caught, isNotNull);
      expect(caught!.response?.statusCode, 403);
    });
  });

  group('204 No Content handling (finding #3)', () {
    test('non-nullable Map caller gets an empty map, not a null-cast crash',
        () async {
      final client = _clientReturning(204, '');
      final result = await client.put<Map<String, dynamic>>('/family');
      expect(result, isA<Map<String, dynamic>>());
      expect(result, isEmpty);
    });

    test('nullable caller still gets null', () async {
      final client = _clientReturning(204, '');
      final result = await client.get<Map<String, dynamic>?>('/whatever');
      expect(result, isNull);
    });

    test('void caller does not throw', () async {
      final client = _clientReturning(204, '');
      await expectLater(client.delete<void>('/family'), completes);
    });
  });
}
