import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/portal_mode.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/register_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/registration_service.dart';
import 'package:buoy_ai/widgets/google_auth_button.dart';

/// Sign-up screen against the three kinds of server: hosted, a fresh self-hosted one, and a
/// self-hosted one whose sign-up lock is closed.
class _FakeRegistration extends RegistrationService {
  _FakeRegistration(this.status);
  final RegistrationStatus? status;

  @override
  Future<RegistrationStatus?> getStatus() async => status;
}

void main() {
  late RegistrationService realService;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realService = registrationService;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    registrationService = realService;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
    AppConfig.sameOriginBuildForTest = null;
    AppConfig.customServerOriginForTest = null;
  });

  Future<void> pumpPage(WidgetTester tester) async {
    tester.view.physicalSize = const Size(900, 3000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ChangeNotifierProvider<AuthProvider>(
        create: (_) => AuthProvider(),
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const RegisterPage(),
        ),
      ),
    );
    // The cinematic background never settles, so pump a few frames instead.
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  testWidgets('hosted: the Google button is there and there is no invite code', (tester) async {
    registrationService = _FakeRegistration(null);

    await pumpPage(tester);

    expect(find.byType(GoogleAuthButton), findsOneWidget);
    expect(find.text('Invite code'), findsNothing);
    expect(find.byKey(const Key('firstAccountNotice')), findsNothing);
  });

  testWidgets('self-host without Google: no social buttons at all', (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: false, open: true, requiresInvite: false));
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});

    await pumpPage(tester);

    expect(find.byType(GoogleAuthButton), findsNothing);
    expect(find.text('Create Account'), findsOneWidget);
  });

  testWidgets('self-host with Google set up: the Google button stays', (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: false, open: true, requiresInvite: false));
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': true}});

    await pumpPage(tester);

    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('self-host, first account: says this person becomes the admin', (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: true, open: false, requiresInvite: false));
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});

    await pumpPage(tester);

    expect(find.byKey(const Key('firstAccountNotice')), findsOneWidget);
    expect(find.text('Invite code'), findsNothing);
  });

  testWidgets('self-host, sign-up closed: asks for an invite code', (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: false, open: false, requiresInvite: true));
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});

    await pumpPage(tester);

    expect(find.text('Invite code'), findsOneWidget);
    expect(find.byKey(const Key('firstAccountNotice')), findsNothing);
  });

  testWidgets('the answer arriving after the page opens still updates it', (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: false, open: false, requiresInvite: true));

    await pumpPage(tester);
    expect(find.text('Invite code'), findsNothing);

    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }

    expect(find.text('Invite code'), findsOneWidget);
    expect(find.byType(GoogleAuthButton), findsNothing);
  });

  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
    }
  }

  testWidgets('native app: no Google button while the handshake is pending, then it follows the answer',
      (tester) async {
    registrationService = _FakeRegistration(null);
    ServerEdition.markHandshakeStarted();

    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);

    // The server says no Google: the button must stay away after the handshake ends.
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});
    ServerEdition.markHandshakeDone();
    await settle(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);
  });

  testWidgets('native app on a server that has Google: the button appears once the handshake ends',
      (tester) async {
    registrationService = _FakeRegistration(null);
    ServerEdition.markHandshakeStarted();

    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);

    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.markHandshakeDone();
    await settle(tester);
    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('self-host web build: hidden from the first frame, even before the handshake starts',
      (tester) async {
    registrationService = _FakeRegistration(null);
    AppConfig.sameOriginBuildForTest = true;

    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);

    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});
    ServerEdition.markHandshakeStarted();
    ServerEdition.markHandshakeDone();
    await settle(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);
  });

  testWidgets('hosted web build never waits: the button is there with no handshake at all',
      (tester) async {
    registrationService = _FakeRegistration(null);
    AppConfig.sameOriginBuildForTest = false;

    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('hosted: the server control says BuoyAI Cloud', (tester) async {
    registrationService = _FakeRegistration(null);

    await pumpPage(tester);

    expect(find.text('Server: BuoyAI Cloud'), findsOneWidget);
  });

  testWidgets('a server the user picked without its own Google client: host shown, no Google',
      (tester) async {
    registrationService = _FakeRegistration(
        const RegistrationStatus(firstAccount: false, open: true, requiresInvite: false));
    AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
    ServerEdition.adopt(ServerCapabilities.fromJson(
        {'edition': 'selfhost', 'features': {'googleSignIn': true}}));

    await pumpPage(tester);

    expect(find.text('Server: buoy.example.ts.net'), findsOneWidget);
    expect(find.byType(GoogleAuthButton), findsNothing);
  });

  testWidgets('Cloud portal: sign-up says it makes a Cloud account with no hosted trial', (tester) async {
    registrationService = _FakeRegistration(null);
    PortalMode.debugOverride = true;
    addTearDown(() => PortalMode.debugOverride = null);

    await pumpPage(tester);

    expect(find.byKey(const ValueKey('register-portal-note')), findsOneWidget);
    expect(find.textContaining('BuoyAI Cloud account'), findsOneWidget);
  });

  testWidgets('hosted app: no Cloud portal note', (tester) async {
    registrationService = _FakeRegistration(null);

    await pumpPage(tester);

    expect(find.byKey(const ValueKey('register-portal-note')), findsNothing);
  });
}
