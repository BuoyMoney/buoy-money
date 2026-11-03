import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/accounts_service.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/widgets/bank_mfa_gate_sheet.dart';

// ── Fakes ────────────────────────────────────────────────

/// Scripted enrollment backend for the sheet.
class _FakeEnrollment implements BankMfaEnrollmentClient {
  bool passkey = false;
  bool verifyAccepts = true;
  final calls = <String>[];

  @override
  Future<bool> passkeySupported() async => passkey;

  @override
  Future<String> registerPasskey({String? displayName}) async {
    calls.add('registerPasskey:$displayName');
    return 'Pixel';
  }

  @override
  Future<Map<String, dynamic>> setupTotp() async {
    calls.add('setupTotp');
    return {
      'qrCodeUrl': 'data:image/png;base64,${base64Encode([0x89, 0x50])}',
      'secret': 'ABCDEFGHIJKLMNOP',
    };
  }

  @override
  Future<void> verifyTotp(String code) async {
    calls.add('verifyTotp:$code');
    if (!verifyAccepts) throw Exception('Invalid code');
  }

  bool emailCompletes = true;

  @override
  Future<bool> enrollEmail(BuildContext context) async {
    calls.add('enrollEmail');
    return emailCompletes;
  }
}

/// Dio adapter for the service-level test of the typed MFA exception.
class _PlaidGateAdapter implements HttpClientAdapter {
  bool requiresMfa = true;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    final body = requiresMfa
        ? {'message': 'MFA required', 'requiresMfa': true}
        : {'linkToken': 'link-sandbox-123'};
    return ResponseBody.fromString(
      jsonEncode(body),
      requiresMfa ? 400 : 200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

Widget _host(_FakeEnrollment fake, void Function(bool) onResult) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(
      body: Builder(
        builder: (ctx) => Center(
          child: ElevatedButton(
            onPressed: () async => onResult(
              await showBankMfaGateSheet(ctx, displayName: 'Penny', client: fake),
            ),
            child: const Text('open'),
          ),
        ),
      ),
    ),
  );
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
  });

  group('AccountsService.createLinkToken', () {
    late _PlaidGateAdapter adapter;
    setUp(() {
      adapter = _PlaidGateAdapter();
      apiClient.dio.httpClientAdapter = adapter;
    });

    test('surfaces the MFA gate as a typed exception', () async {
      adapter.requiresMfa = true;
      expect(
        () => AccountsService().createLinkToken(),
        throwsA(isA<BankMfaRequiredException>()),
      );
    });

    test('returns the link token once the gate is satisfied', () async {
      adapter.requiresMfa = false;
      expect(await AccountsService().createLinkToken(), 'link-sandbox-123');
    });
  });

  group('showBankMfaGateSheet', () {
    late _FakeEnrollment fake;
    setUp(() => fake = _FakeEnrollment());

    testWidgets('no passkey support: authenticator is the offered path; Not now returns false',
        (tester) async {
      bool? result;
      await tester.pumpWidget(_host(fake, (r) => result = r));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      expect(find.text('One quick step first'), findsOneWidget);
      expect(find.text('Use an authenticator app'), findsOneWidget);
      expect(find.text('Use an email code'), findsOneWidget);
      expect(find.text('LEAST SECURE'), findsOneWidget);
      expect(find.text('Use a passkey'), findsNothing);

      await tester.tap(find.text('Not now'));
      await tester.pumpAndSettle();
      expect(result, isFalse);
    });

    testWidgets('passkey path: one tap registers and completes with true',
        (tester) async {
      fake.passkey = true;
      bool? result;
      await tester.pumpWidget(_host(fake, (r) => result = r));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      expect(find.text('Use a passkey'), findsOneWidget);
      expect(find.text('FASTEST'), findsOneWidget);
      await tester.tap(find.text('Use a passkey'));
      await tester.pumpAndSettle();

      expect(fake.calls, ['registerPasskey:Penny']);
      expect(result, isTrue);
    });

    testWidgets('TOTP path: wrong code shows an error, right code completes',
        (tester) async {
      bool? result;
      await tester.pumpWidget(_host(fake, (r) => result = r));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Use an authenticator app'));
      await tester.pumpAndSettle();
      expect(fake.calls, ['setupTotp']);
      expect(find.text('Set up your authenticator'), findsOneWidget);
      expect(find.text('ABCDEFGHIJKLMNOP'), findsOneWidget);

      fake.verifyAccepts = false;
      await tester.enterText(find.byType(TextField), '111111');
      await tester.pumpAndSettle();
      expect(find.textContaining('did not match'), findsOneWidget);
      expect(result, isNull);

      fake.verifyAccepts = true;
      await tester.enterText(find.byType(TextField), '222222');
      await tester.pumpAndSettle();
      expect(result, isTrue);
      expect(fake.calls, ['setupTotp', 'verifyTotp:111111', 'verifyTotp:222222']);
    });

    testWidgets('email path: verified inbox completes with true', (tester) async {
      bool? result;
      await tester.pumpWidget(_host(fake, (r) => result = r));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Use an email code'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use an email code'));
      await tester.pumpAndSettle();

      expect(fake.calls, ['enrollEmail']);
      expect(result, isTrue);
    });

    testWidgets('email path: backing out of the dialog leaves the sheet open',
        (tester) async {
      fake.emailCompletes = false;
      bool? result;
      await tester.pumpWidget(_host(fake, (r) => result = r));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();

      await tester.ensureVisible(find.text('Use an email code'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use an email code'));
      await tester.pumpAndSettle();

      expect(result, isNull);
      expect(find.text('One quick step first'), findsOneWidget);
    });

    testWidgets('Back from the TOTP screen returns to the method picker',
        (tester) async {
      await tester.pumpWidget(_host(fake, (_) {}));
      await tester.tap(find.text('open'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Use an authenticator app'));
      await tester.pumpAndSettle();
      // The Back link sits below the fold of the 600px test window; scroll it in.
      await tester.ensureVisible(find.text('Back'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Back'));
      await tester.pumpAndSettle();
      expect(find.text('One quick step first'), findsOneWidget);
    });
  });
}
