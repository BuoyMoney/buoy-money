import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/biometric_service.dart';

/// A fingerprint unlock that always succeeds and hands back stored credentials.
class _UnlockedBiometrics extends BiometricService {
  @override
  Future<BiometricCredentials?> authenticateAndGetCredentials() async =>
      BiometricCredentials(email: 'mfa@example.com', password: 'stored-password');
}

/// Answers POST /auth/login with [loginBody] and records what the app sent.
class _LoginAdapter implements HttpClientAdapter {
  _LoginAdapter(this.loginBody);

  final Map<String, dynamic> loginBody;
  final sentBodies = <Map<String, dynamic>>[];

  @override
  Future<ResponseBody> fetch(
      RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (options.method == 'POST' && options.path.endsWith('/auth/login')) {
      final data = options.data;
      sentBodies.add(data is String
          ? jsonDecode(data) as Map<String, dynamic>
          : Map<String, dynamic>.from(data as Map));
      return ResponseBody.fromString(jsonEncode(loginBody), 200,
          headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});
    }
    return ResponseBody.fromString('', 404);
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late BiometricService originalBiometrics;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
    originalBiometrics = biometricService;
    biometricService = _UnlockedBiometrics();
  });

  tearDown(() => biometricService = originalBiometrics);

  test('a biometric unlock answered with an MFA challenge lands on the MFA step', () async {
    final adapter = _LoginAdapter({
      'requiresMfa': true,
      'mfaToken': 'mfa-pending-token',
      'preferredMfaMethod': 'Totp',
      'availableMfaMethods': ['Totp'],
      'mfaReasons': ['New or untrusted device'],
    });
    apiClient.dio.httpClientAdapter = adapter;
    final auth = AuthProvider();

    final success = await auth.biometricLogin();

    expect(success, isFalse);
    expect(auth.status, AuthStatus.mfaRequired);
    expect(auth.errorMessage, isNull, reason: 'a challenge is not a failure');
    final challenge = auth.mfaChallenge!;
    expect(challenge.mfaToken, 'mfa-pending-token');
    expect(challenge.availableMethods, ['Totp']);
    expect(challenge.email, 'mfa@example.com',
        reason: 'the MFA screen needs the account; the form was never filled');
  });

  test('the login request no longer claims a biometric second factor', () async {
    final adapter = _LoginAdapter({
      'requiresMfa': true,
      'mfaToken': 'mfa-pending-token',
      'availableMfaMethods': ['Totp'],
    });
    apiClient.dio.httpClientAdapter = adapter;

    await AuthProvider().biometricLogin();

    expect(adapter.sentBodies, hasLength(1));
    expect(adapter.sentBodies.single['email'], 'mfa@example.com');
    expect(adapter.sentBodies.single.containsKey('biometricVerified'), isFalse);
  });
}
