import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/biometric_service.dart';

/// Unit coverage for [BiometricService] (#biometric).
///
/// local_auth and flutter_secure_storage are native plugins with no host
/// implementation under `flutter test`, so we stub both method channels:
///  - `plugins.flutter.io/local_auth` — configurable device-support / enrolled
///    biometrics / authenticate result (and a throw path).
///  - `plugins.it_nomads.com/flutter_secure_storage` — a real in-memory map so
///    enable/disable round-trips actually persist and read back.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;

  // ── local_auth stub state ──
  List<String> availableBiometrics = <String>['fingerprint'];
  bool deviceSupported = true;
  bool authenticateResult = true;
  bool authenticateThrows = false;

  // ── secure storage stub state ──
  final Map<String, String> store = <String, String>{};

  setUp(() {
    availableBiometrics = <String>['fingerprint'];
    deviceSupported = true;
    authenticateResult = true;
    authenticateThrows = false;
    store.clear();

    messenger.setMockMethodCallHandler(
      const MethodChannel('plugins.flutter.io/local_auth'),
      (call) async {
        switch (call.method) {
          case 'isDeviceSupported':
            return deviceSupported;
          case 'getAvailableBiometrics':
            return availableBiometrics;
          case 'authenticate':
            if (authenticateThrows) {
              throw PlatformException(code: 'NotAvailable');
            }
            return authenticateResult;
          case 'stopAuthentication':
            return true;
        }
        return null;
      },
    );

    messenger.setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async {
        final args = (call.arguments as Map?) ?? const {};
        final key = args['key'] as String?;
        switch (call.method) {
          case 'read':
            return store[key];
          case 'write':
            store[key!] = args['value'] as String;
            return null;
          case 'delete':
            store.remove(key);
            return null;
          case 'deleteAll':
            store.clear();
            return null;
          case 'containsKey':
            return store.containsKey(key);
          case 'readAll':
            return Map<String, String>.from(store);
        }
        return null;
      },
    );
  });

  tearDown(() {
    messenger.setMockMethodCallHandler(
        const MethodChannel('plugins.flutter.io/local_auth'), null);
    messenger.setMockMethodCallHandler(
        const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
        null);
  });

  group('authenticate', () {
    test('returns true when the platform authorizes', () async {
      authenticateResult = true;
      expect(await BiometricService().authenticate(reason: 'x'), isTrue);
    });

    test('returns false when the platform denies', () async {
      authenticateResult = false;
      expect(await BiometricService().authenticate(reason: 'x'), isFalse);
    });

    test('returns false (never throws) on a platform exception', () async {
      authenticateThrows = true;
      expect(await BiometricService().authenticate(reason: 'x'), isFalse);
    });
  });

  group('quick-login enable/disable round-trip', () {
    test('enable persists credentials and reads back enabled', () async {
      final svc = BiometricService();
      final ok = await svc.enableBiometricLogin(
        email: 'user@example.com',
        password: 'hunter2',
        userId: '42',
      );
      expect(ok, isTrue);
      expect(await svc.isBiometricLoginEnabled(), isTrue);
      expect(await svc.getStoredEmail(), 'user@example.com');
    });

    test('enable fails (stores nothing) when the biometric check is declined',
        () async {
      authenticateResult = false;
      final svc = BiometricService();
      final ok = await svc.enableBiometricLogin(
        email: 'user@example.com',
        password: 'hunter2',
        userId: '42',
      );
      expect(ok, isFalse);
      expect(await svc.isBiometricLoginEnabled(), isFalse);
      expect(store, isEmpty);
    });

    test('disable clears the stored credentials', () async {
      final svc = BiometricService();
      await svc.enableBiometricLogin(
          email: 'user@example.com', password: 'hunter2', userId: '42');
      await svc.disableBiometricLogin();
      expect(await svc.isBiometricLoginEnabled(), isFalse);
      expect(await svc.getStoredEmail(), isNull);
    });
  });

  group('authenticateAndGetCredentials', () {
    test('returns credentials only on a successful biometric check', () async {
      final svc = BiometricService();
      await svc.enableBiometricLogin(
          email: 'user@example.com', password: 'hunter2', userId: '42');

      // Biometric denied → no credentials leak.
      authenticateResult = false;
      expect(await svc.authenticateAndGetCredentials(), isNull);

      // Biometric granted → credentials returned.
      authenticateResult = true;
      final creds = await svc.authenticateAndGetCredentials();
      expect(creds, isNotNull);
      expect(creds!.email, 'user@example.com');
      expect(creds.password, 'hunter2');
      expect(creds.userId, '42');
    });

    test('returns null when quick-login was never enabled', () async {
      final svc = BiometricService();
      expect(await svc.authenticateAndGetCredentials(), isNull);
    });
  });

  group('app-lock gate', () {
    test('enable requires a successful biometric check', () async {
      final svc = BiometricService();

      // Declined → not enabled.
      authenticateResult = false;
      expect(await svc.setAppLockEnabled(true), isFalse);
      expect(await svc.isAppLockEnabled(), isFalse);

      // Granted → enabled and persisted.
      authenticateResult = true;
      expect(await svc.setAppLockEnabled(true), isTrue);
      expect(await svc.isAppLockEnabled(), isTrue);
    });

    test('disable clears the flag without prompting', () async {
      final svc = BiometricService();
      authenticateResult = true;
      await svc.setAppLockEnabled(true);
      expect(await svc.setAppLockEnabled(false), isFalse);
      expect(await svc.isAppLockEnabled(), isFalse);
    });
  });

  group('offer-declined flag', () {
    test('round-trips through secure storage', () async {
      final svc = BiometricService();
      expect(await svc.isBiometricOfferDeclined(), isFalse);
      await svc.setBiometricOfferDeclined(true);
      expect(await svc.isBiometricOfferDeclined(), isTrue);
      await svc.setBiometricOfferDeclined(false);
      expect(await svc.isBiometricOfferDeclined(), isFalse);
    });
  });
}
