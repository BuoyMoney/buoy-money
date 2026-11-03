import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/biometric_service.dart';
import 'package:buoy_ai/widgets/app_lock_gate.dart';

/// Widget coverage for [AppLockGate] (#biometric).
///
/// The gate reads app-lock state + drives the biometric prompt through the
/// global [biometricService], and watches an [AuthProvider] above it in the tree.
/// We swap a fake service (global swap, as with accountsService) and provide a
/// fake provider whose auth status we control directly.
class _FakeBiometricService extends BiometricService {
  bool appLock;
  bool canCheck;
  bool authResult;
  int authCalls = 0;

  _FakeBiometricService({
    this.appLock = false,
    this.canCheck = true,
    this.authResult = false,
  });

  @override
  Future<bool> isAppLockEnabled() async => appLock;

  @override
  Future<bool> canCheckBiometrics() async => canCheck;

  @override
  Future<bool> authenticate({required String reason}) async {
    authCalls++;
    return authResult;
  }
}

class _FakeAuthProvider extends AuthProvider {
  final AuthStatus _status;
  _FakeAuthProvider(this._status);

  @override
  AuthStatus get status => _status;

  @override
  bool get isAuthenticated => _status == AuthStatus.authenticated;
}

void main() {
  late BiometricService realService;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realService = biometricService;
  });

  tearDown(() {
    biometricService = realService;
  });

  Widget wrap(AuthProvider auth) {
    return ChangeNotifierProvider<AuthProvider>.value(
      value: auth,
      child: const MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: AppLockGate(child: Text('APP CONTENT')),
      ),
    );
  }

  testWidgets('renders child (no lock) when app-lock is disabled',
      (tester) async {
    biometricService = _FakeBiometricService(appLock: false);
    await tester.pumpWidget(wrap(_FakeAuthProvider(AuthStatus.authenticated)));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('APP CONTENT'), findsOneWidget);
    expect(find.text('Locked'), findsNothing);
  });

  testWidgets('locks on cold start and auto-prompts; a failed prompt shows retry',
      (tester) async {
    final fake = _FakeBiometricService(
        appLock: true, canCheck: true, authResult: false);
    biometricService = fake;

    await tester.pumpWidget(wrap(_FakeAuthProvider(AuthStatus.authenticated)));
    await tester.pump(); // cold-start post-frame callback
    await tester.pump(const Duration(milliseconds: 50)); // isAppLockEnabled + canCheck → lock
    await tester.pump(const Duration(milliseconds: 350)); // auto-prompt timer + authenticate

    expect(find.text('Locked'), findsOneWidget);
    expect(find.text('APP CONTENT'), findsNothing);
    expect(fake.authCalls, 1, reason: 'auto-prompt should fire once');
    expect(find.text('Authentication failed — try again'), findsOneWidget);
    expect(find.text('Unlock'), findsOneWidget);
  });

  testWidgets('tapping Unlock with a successful prompt reveals the child',
      (tester) async {
    final fake = _FakeBiometricService(
        appLock: true, canCheck: true, authResult: false);
    biometricService = fake;

    await tester.pumpWidget(wrap(_FakeAuthProvider(AuthStatus.authenticated)));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 350));
    expect(find.text('Unlock'), findsOneWidget);

    // Next attempt succeeds.
    fake.authResult = true;
    await tester.tap(find.text('Unlock'));
    await tester.pump(); // _runAuth → authenticate
    await tester.pump(const Duration(milliseconds: 50));

    expect(find.text('Locked'), findsNothing);
    expect(find.text('APP CONTENT'), findsOneWidget);
    expect(fake.authCalls, 2);
  });

  testWidgets(
      'initiallyLocked renders the scrim on the first frame (no content flash)',
      (tester) async {
    final fake = _FakeBiometricService(
        appLock: true, canCheck: true, authResult: true);
    biometricService = fake;

    await tester.pumpWidget(
      ChangeNotifierProvider<AuthProvider>.value(
        value: _FakeAuthProvider(AuthStatus.authenticated),
        child: const MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: AppLockGate(initiallyLocked: true, child: Text('APP CONTENT')),
        ),
      ),
    );

    // The very first build already shows the scrim (locked synchronously in
    // initState) — the child content is never rendered for even one frame.
    expect(find.text('Locked'), findsOneWidget);
    expect(find.text('APP CONTENT'), findsNothing);

    // Drain the cold-start auto-prompt chain so no timers remain pending.
    await tester.pump(const Duration(milliseconds: 50));
    await tester.pump(const Duration(milliseconds: 350));
  });

  testWidgets('never locks while unauthenticated', (tester) async {
    biometricService = _FakeBiometricService(appLock: true, canCheck: true);
    await tester
        .pumpWidget(wrap(_FakeAuthProvider(AuthStatus.unauthenticated)));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(find.text('Locked'), findsNothing);
    expect(find.text('APP CONTENT'), findsOneWidget);
  });
}
