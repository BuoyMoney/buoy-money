import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/auth_service.dart';

/// Unit coverage for [AuthService.verifyPassword] (settings quick-login enrol).
///
/// verifyPassword must hit POST /auth/verify-password (NOT /auth/login — which
/// mints throwaway tokens and burns the 'auth' rate limit) and surface the
/// backend's {valid:bool}. We stub the Dio adapter so no network is hit.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.body);

  final Map<String, dynamic> body;
  String? lastPath;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastPath = options.path;
    return ResponseBody.fromString(
      jsonEncode(body),
      200,
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

  setUp(() {
    // No native secure storage under flutter test — any token read returns null.
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
  });

  test('verifyPassword posts to /auth/verify-password and returns true on valid',
      () async {
    final adapter = _StubAdapter({'valid': true});
    apiClient.dio.httpClientAdapter = adapter;

    final ok = await AuthService(apiClient).verifyPassword('hunter2');

    expect(ok, isTrue);
    expect(adapter.lastPath, contains('/auth/verify-password'));
  });

  test('verifyPassword returns false when the backend reports invalid',
      () async {
    apiClient.dio.httpClientAdapter = _StubAdapter({'valid': false});

    final ok = await AuthService(apiClient).verifyPassword('wrong');

    expect(ok, isFalse);
  });

  test('verifyPassword tolerates a PascalCase Valid key', () async {
    apiClient.dio.httpClientAdapter = _StubAdapter({'Valid': true});

    final ok = await AuthService(apiClient).verifyPassword('hunter2');

    expect(ok, isTrue);
  });
}
