import 'dart:convert';

import 'package:buoy_ai/config/portal_mode.dart';
import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// A sign-up started in the Cloud portal (buoy-ai.com/cloud/) must tell the server so (`signupContext: "cloud"`),
/// which makes the account Cloud-only: no hosted trial. Every way of creating an account carries it (email,
/// Google, Apple), and nothing outside the portal ever does.
class _CapturingAdapter implements HttpClientAdapter {
  final bodies = <String, Map<String, dynamic>>{};

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final data = options.data;
    bodies[options.path] = data is Map<String, dynamic>
        ? data
        : (data is String ? jsonDecode(data) as Map<String, dynamic> : {});
    return ResponseBody.fromString(
      jsonEncode({
        'token': 'jwt',
        'refreshToken': 'refresh',
        'isNewUser': true,
        'user': {
          'id': 7,
          'email': 'new@example.com',
          'firstName': 'New',
          'lastName': 'Person',
          'subscriptionTier': 'Free',
          'createdAt': '2026-10-31T12:00:00Z',
        },
      }),
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
  late _CapturingAdapter adapter;
  late HttpClientAdapter realAdapter;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
    realAdapter = apiClient.dio.httpClientAdapter;
    adapter = _CapturingAdapter();
    apiClient.dio.httpClientAdapter = adapter;
  });

  tearDown(() {
    apiClient.dio.httpClientAdapter = realAdapter;
    PortalMode.debugOverride = null;
  });

  Map<String, dynamic> sent(String endsWith) =>
      adapter.bodies.entries.singleWhere((e) => e.key.endsWith(endsWith)).value;

  Future<void> signUpEveryWay() async {
    final auth = AuthProvider();
    expect(
      await auth.register(
          email: 'new@example.com',
          password: 'Zx7!kQ2#mWp9',
          firstName: 'New',
          lastName: 'Person'),
      isTrue,
    );
    expect(await auth.googleLogin('google-id-token', isRegistration: true),
        isTrue);
    expect(
      await auth.appleLogin(AppleLoginRequest(
        identityToken: 'apple-token',
        authorizationCode: 'code',
        nonce: 'nonce',
        userIdentifier: 'apple-user',
      )),
      isTrue,
    );
  }

  test('in the Cloud portal every sign-up says signupContext cloud', () async {
    PortalMode.debugOverride = true;

    await signUpEveryWay();

    expect(sent('/auth/register')['signupContext'], 'cloud');
    expect(sent('/auth/google-login')['signupContext'], 'cloud');
    expect(sent('/auth/apple-login')['signupContext'], 'cloud');
  });

  test('outside the portal no sign-up carries a context', () async {
    PortalMode.debugOverride = false;

    await signUpEveryWay();

    expect(sent('/auth/register').containsKey('signupContext'), isFalse);
    expect(sent('/auth/google-login').containsKey('signupContext'), isFalse);
    expect(sent('/auth/apple-login').containsKey('signupContext'), isFalse);
  });

  test('the request models write the context only when there is one', () {
    expect(
      RegisterRequest(
              email: 'a@b.c',
              password: 'p',
              firstName: 'A',
              lastName: 'B',
              signupContext: 'cloud')
          .toJson()['signupContext'],
      'cloud',
    );
    expect(
        GoogleLoginRequest(idToken: 't').toJson().containsKey('signupContext'),
        isFalse);
    final apple = AppleLoginRequest(
        identityToken: 't',
        authorizationCode: 'c',
        nonce: 'n',
        userIdentifier: 'u');
    expect(apple.toJson().containsKey('signupContext'), isFalse);
    final cloudApple = apple.withSignupContext('cloud');
    expect(cloudApple.toJson()['signupContext'], 'cloud');
    expect(cloudApple.toJson()['identityToken'], 't');
  });
}
