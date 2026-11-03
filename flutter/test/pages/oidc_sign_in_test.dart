import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/public/login_page.dart';
import 'package:buoy_ai/pages/public/oidc_callback_page.dart';
import 'package:buoy_ai/pages/register_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/oidc_sign_in_service.dart';
import 'package:buoy_ai/services/registration_service.dart';

/// Single sign-on (OpenID Connect) on a self-hosted server: the button follows the server, the
/// password form hides only when the server turned passwords off, and the return page trades the
/// one-time code and moves on.
class _FakeRegistration extends RegistrationService {
  @override
  Future<RegistrationStatus?> getStatus() async =>
      const RegistrationStatus(firstAccount: false, open: true, requiresInvite: false);
}

ServerCapabilities _selfHost({bool oidc = true, bool passwordLogin = true, String? name = 'Pocket ID'}) =>
    ServerCapabilities.fromJson({
      'edition': 'selfhost',
      'oidcDisplayName': name,
      'features': {'googleSignIn': false, 'oidc': oidc, 'passwordLogin': passwordLogin},
    });

void main() {
  late RegistrationService realRegistration;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realRegistration = registrationService;
    registrationService = _FakeRegistration();
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    registrationService = realRegistration;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
    AppConfig.sameOriginBuildForTest = null;
  });

  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
      while (tester.takeException() != null) {}
    }
  }

  Future<void> pumpHome(WidgetTester tester, Widget home) async {
    tester.view.physicalSize = const Size(900, 3000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ChangeNotifierProvider<AuthProvider>(
        create: (_) => AuthProvider(),
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: home,
        ),
      ),
    );
    await settle(tester);
  }

  group('ServerCapabilities', () {
    test('hosted and older servers: no single sign-on, passwords on', () {
      final caps = ServerCapabilities.fromJson({'edition': 'hosted', 'features': {}});
      expect(caps.oidc, isFalse);
      expect(caps.oidcDisplayName, isNull);
      expect(caps.passwordLogin, isTrue);
    });

    test('reads oidc, its name and passwordLogin', () {
      final caps = _selfHost(passwordLogin: false);
      expect(caps.oidc, isTrue);
      expect(caps.oidcDisplayName, 'Pocket ID');
      expect(caps.passwordLogin, isFalse);
    });

    test('reads the PascalCase display name too', () {
      final caps = ServerCapabilities.fromJson({
        'edition': 'selfhost',
        'OidcDisplayName': 'Authentik',
        'features': {'oidc': true},
      });
      expect(caps.oidcDisplayName, 'Authentik');
    });
  });

  group('login page', () {
    testWidgets('hosted: no single sign-on button', (tester) async {
      await pumpHome(tester, const LoginPage());
      expect(find.byKey(const Key('oidc-sign-in-button')), findsNothing);
      expect(find.text('Password'), findsOneWidget);
    });

    testWidgets('self-host without single sign-on: no button', (tester) async {
      ServerEdition.current.value = _selfHost(oidc: false);
      await pumpHome(tester, const LoginPage());
      expect(find.byKey(const Key('oidc-sign-in-button')), findsNothing);
    });

    testWidgets('self-host with single sign-on: "Sign in with Pocket ID" next to the form',
        (tester) async {
      ServerEdition.current.value = _selfHost();
      await pumpHome(tester, const LoginPage());
      expect(find.byKey(const Key('oidc-sign-in-button')), findsOneWidget);
      expect(find.text('Sign in with Pocket ID'), findsOneWidget);
      expect(find.text('Password'), findsOneWidget);
    });

    testWidgets('passwords turned off: only the single sign-on button', (tester) async {
      ServerEdition.current.value = _selfHost(passwordLogin: false);
      await pumpHome(tester, const LoginPage());
      expect(find.byKey(const Key('oidc-sign-in-button')), findsOneWidget);
      expect(find.text('Password'), findsNothing);
    });

    testWidgets('no display name: falls back to "Single sign-on"', (tester) async {
      ServerEdition.current.value = _selfHost(name: null);
      await pumpHome(tester, const LoginPage());
      expect(find.text('Sign in with Single sign-on'), findsOneWidget);
    });

    testWidgets('the answer arriving after the page opens shows the button', (tester) async {
      await pumpHome(tester, const LoginPage());
      expect(find.byKey(const Key('oidc-sign-in-button')), findsNothing);

      ServerEdition.current.value = _selfHost();
      await settle(tester);
      expect(find.byKey(const Key('oidc-sign-in-button')), findsOneWidget);
    });
  });

  group('register page', () {
    testWidgets('self-host with single sign-on: "Sign up with Pocket ID"', (tester) async {
      ServerEdition.current.value = _selfHost();
      await pumpHome(tester, const RegisterPage());
      expect(find.text('Sign up with Pocket ID'), findsOneWidget);
      expect(find.text('Create Account'), findsOneWidget);
    });

    testWidgets('passwords turned off: no password sign-up form', (tester) async {
      ServerEdition.current.value = _selfHost(passwordLogin: false);
      await pumpHome(tester, const RegisterPage());
      expect(find.text('Sign up with Pocket ID'), findsOneWidget);
      expect(find.text('Create Account'), findsNothing);
    });
  });

  group('return page', () {
    Future<GoRouter> pumpCallback(
      WidgetTester tester, {
      required String location,
      required OidcExchange exchange,
      Future<String?> Function()? takeVerifier,
    }) async {
      final router = GoRouter(
        initialLocation: location,
        routes: [
          GoRoute(
            path: '/auth/oidc',
            builder: (context, state) => OidcCallbackPage(
              code: state.uri.queryParameters['code'],
              error: state.uri.queryParameters['error'],
              next: state.uri.queryParameters['next'],
              exchange: exchange,
              takeVerifier: takeVerifier ?? () async => null,
            ),
          ),
          GoRoute(path: '/dashboard', builder: (_, __) => const Text('dashboard page')),
          GoRoute(path: '/budgets', builder: (_, __) => const Text('budgets page')),
          GoRoute(path: '/login', builder: (_, __) => const Text('login page')),
        ],
      );
      addTearDown(router.dispose);
      await tester.pumpWidget(
        ChangeNotifierProvider<AuthProvider>(
          create: (_) => AuthProvider(),
          child: MaterialApp.router(
            localizationsDelegates: AppLocalizations.localizationsDelegates,
            supportedLocales: AppLocalizations.supportedLocales,
            routerConfig: router,
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
      await tester.pumpAndSettle();
      return router;
    }

    testWidgets('trades the code with the saved verifier, then opens the dashboard',
        (tester) async {
      String? sentCode;
      String? sentVerifier;
      await pumpCallback(
        tester,
        location: '/auth/oidc?code=one-time',
        takeVerifier: () async => 'the-verifier',
        exchange: (code, verifier) async {
          sentCode = code;
          sentVerifier = verifier;
          return true;
        },
      );

      expect(sentCode, 'one-time');
      expect(sentVerifier, 'the-verifier');
      expect(find.text('dashboard page'), findsOneWidget);
    });

    testWidgets('goes to the in-app path the server passed back', (tester) async {
      await pumpCallback(
        tester,
        location: '/auth/oidc?code=one-time&next=%2Fbudgets',
        exchange: (_, __) async => true,
      );
      expect(find.text('budgets page'), findsOneWidget);
    });

    testWidgets('ignores a next path that leaves the app', (tester) async {
      await pumpCallback(
        tester,
        location: '/auth/oidc?code=one-time&next=%2F%2Fevil.test',
        exchange: (_, __) async => true,
      );
      expect(find.text('dashboard page'), findsOneWidget);
    });

    testWidgets('a refusal from the server shows its message and does not call exchange',
        (tester) async {
      var called = false;
      await pumpCallback(
        tester,
        location: '/auth/oidc?error=no_account',
        exchange: (_, __) async => called = true,
      );
      expect(called, isFalse);
      expect(find.byKey(const Key('oidc-error')), findsOneWidget);
      expect(find.textContaining('No account uses this email'), findsOneWidget);
      expect(find.text('Back to sign in'), findsOneWidget);
    });

    testWidgets('a failed exchange shows an error with a way back', (tester) async {
      await pumpCallback(
        tester,
        location: '/auth/oidc?code=stale',
        exchange: (_, __) async => false,
      );
      expect(find.byKey(const Key('oidc-error')), findsOneWidget);
      await tester.tap(find.text('Back to sign in'));
      await tester.pumpAndSettle();
      expect(find.text('login page'), findsOneWidget);
    });
  });

  group('OidcSignInService', () {
    test('mobile start address carries the platform, the invite and the S256 challenge', () {
      final service = OidcSignInService();
      final verifier = OidcSignInService.newVerifier();
      final uri = service.startUri(
        web: false,
        invite: ' ABCDE ',
        codeChallenge: OidcSignInService.challengeFor(verifier),
      );
      expect(uri.path, endsWith('/api/auth/oidc/start'));
      expect(uri.queryParameters['platform'], 'mobile');
      expect(uri.queryParameters['invite'], 'ABCDE');
      expect(uri.queryParameters['code_challenge'], hasLength(43));
      expect(verifier, hasLength(43));
    });

    test('S256 challenge matches RFC 7636 appendix B', () {
      expect(
        OidcSignInService.challengeFor('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk'),
        'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM',
      );
    });

    test('recognises only its own deep link', () {
      expect(OidcSignInService.isCallbackLink(Uri.parse('com.buoyai.buoymoney://oidc?code=x')), isTrue);
      expect(OidcSignInService.isCallbackLink(Uri.parse('com.buoyai.buoymoney://other')), isFalse);
      expect(OidcSignInService.isCallbackLink(Uri.parse('https://buoy-ai.com/app/auth/oidc')), isFalse);
    });
  });
}
