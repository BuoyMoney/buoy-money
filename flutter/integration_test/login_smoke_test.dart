// Scripted UI smoke — the renderer-proof login path + authed navigation.
//
// This is the "scripted keeper" layer: it exercises what the API e2e (Layer 1)
// and the overflow sweep cannot — a REAL user typing into the login form and the
// authenticated shell rendering across pages. Runs the real app against a locally
// running backend via `flutter drive` (see scripts/dev/ui-smoke.sh).
//
// It deliberately drives the WIDGET TREE (tester.enterText / tester.tap), which
// works on Flutter web where DOM/synthetic-key text entry does not. The
// claim→budget *logic* is covered by the API-level e2e tests; this proves the UI wiring renders and
// the auth path works end to end.
//
//   --dart-define=API_BASE_URL=http://localhost:5100/api   # local API (app reads this too)
//   --dart-define=LOGIN_EMAIL=family-owner@buoy-ai.com
//   --dart-define=LOGIN_PASSWORD=BuoyDevLocal2025!            # BUOYAI_TEST_PASSWORD

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/main.dart' as app;
import 'package:buoy_ai/widgets/cinematic/cinematic_nav_shell.dart';

const _loginEmail =
    String.fromEnvironment('LOGIN_EMAIL', defaultValue: 'family-owner@buoy-ai.com');
const _loginPassword =
    String.fromEnvironment('LOGIN_PASSWORD', defaultValue: 'BuoyDevLocal2025!');

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('login via the real form → dashboard → navigate authed shell',
      (WidgetTester tester) async {
    // Preload fonts so first paint is styled (matches main(); avoids flake).
    await GoogleFonts.pendingFonts([GoogleFonts.sora(), GoogleFonts.firaCode()]);

    // Boot WITHOUT a seeded token — we want the real unauthenticated login path.
    await tester.pumpWidget(const app.BuoyAIApp());
    await _pumpFor(tester, const Duration(seconds: 4));

    final router = _appRouter(tester);

    String location() =>
        router.routerDelegate.currentConfiguration.uri.toString();

    // Go to the login screen.
    router.go('/login');
    await _pumpFor(tester, const Duration(seconds: 2));
    expect(find.text('Sign In'), findsOneWidget,
        reason: 'login form did not render');

    // Type real credentials into the form (widget-tree text entry).
    final fields = find.byType(TextFormField);
    expect(fields, findsNWidgets(2), reason: 'expected email + password fields');
    await tester.enterText(fields.at(0), _loginEmail);
    await tester.enterText(fields.at(1), _loginPassword);
    await _pumpFor(tester, const Duration(milliseconds: 300));

    // Submit and wait for the auth round trip + redirect.
    await tester.tap(find.text('Sign In'));
    await _pumpFor(tester, const Duration(seconds: 6));

    // Assert we authenticated and landed in the protected shell.
    expect(location(), contains('/dashboard'),
        reason: 'login did not redirect to /dashboard (got "${location()}"). '
            'Check the seeded password (BUOYAI_TEST_PASSWORD) and that MFA is off.');
    expect(find.byType(CinematicNavShell), findsOneWidget,
        reason: 'authenticated nav shell did not render');

    // Smoke the authed shell across EVERY primary page — each must render under
    // the shell without throwing. /accounts, /subscriptions and /achievements are
    // included deliberately: /accounts was the page whose fromJson cast crashed
    // only on the mobile app (#686), and a smoke that skipped it missed the bug.
    // Fixed pumps; never pumpAndSettle (bg animation).
    for (final route in const [
      '/transactions',
      '/budgets',
      '/savings',
      '/accounts',
      '/subscriptions',
      '/achievements',
      '/dashboard',
    ]) {
      router.go(route);
      await _pumpFor(tester, const Duration(seconds: 3));
      expect(find.byType(CinematicNavShell), findsOneWidget,
          reason: 'nav shell missing on $route');
      expect(location(), contains(route), reason: 'did not navigate to $route');
      expect(tester.takeException(), isNull,
          reason: '$route threw while rendering (a page-load crash)');
    }
  }, timeout: const Timeout(Duration(minutes: 10)));
}

/// Fixed-duration pumps — never pumpAndSettle (the app's perpetual background
/// animation means the tree never settles; pumpAndSettle would hang).
Future<void> _pumpFor(WidgetTester tester, Duration total) async {
  const step = Duration(milliseconds: 200);
  var elapsed = Duration.zero;
  while (elapsed < total) {
    await tester.pump(step);
    elapsed += step;
  }
}

/// Resolve the app's GoRouter regardless of how MaterialApp.router is wired.
/// main.dart passes routerDelegate/backButtonDispatcher instead of routerConfig
/// (the go_router back-button guard), so routerConfig may be null — fall back to
/// the inherited GoRouter below the root Router element.
GoRouter _appRouter(WidgetTester tester) {
  final materialApp = tester.widget<MaterialApp>(find.byType(MaterialApp));
  final direct = materialApp.routerConfig;
  if (direct is GoRouter) return direct;
  return GoRouter.of(tester.element(find.byType(Navigator).first));
}
