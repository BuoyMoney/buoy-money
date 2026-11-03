import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/public/login_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/widgets/google_auth_button.dart';

/// The sign-in page follows the server: no "Continue with Google" on a server without it, and
/// none before the server has answered on a native app or a self-host web page.
void main() {
  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
    AppConfig.sameOriginBuildForTest = null;
  });

  // The test font is wider than the real one, so a row overflows; that is not what these tests are about.
  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 4; i++) {
      await tester.pump(const Duration(milliseconds: 50));
      while (tester.takeException() != null) {}
    }
  }

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
          home: const LoginPage(),
        ),
      ),
    );
    await settle(tester);
  }

  testWidgets('hosted: the Google button is shown', (tester) async {
    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('self-host web page: hidden before the answer and after a "no Google" answer',
      (tester) async {
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

  testWidgets('native app: hidden while the handshake is pending, shown when the server has Google',
      (tester) async {
    ServerEdition.markHandshakeStarted();
    await pumpPage(tester);
    expect(find.byType(GoogleAuthButton), findsNothing);

    ServerEdition.markHandshakeDone();
    await settle(tester);
    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('hosted: the sign-up link says "Get started free"', (tester) async {
    await pumpPage(tester);
    expect(find.text('Get started free'), findsOneWidget);
    expect(find.text('Create an account'), findsNothing);
  });

  testWidgets('self-host: the sign-up link says "Create an account", so a new server has a way in',
      (tester) async {
    ServerEdition.current.value =
        ServerCapabilities.fromJson({'edition': 'selfhost', 'features': {'googleSignIn': false}});
    await pumpPage(tester);
    expect(find.text('Create an account'), findsOneWidget);
    expect(find.text('Get started free'), findsNothing);
  });
}
