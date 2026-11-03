// No-backend boot smoke — the platform runtime net.
//
// Boots the REAL app on the host device/simulator (iOS in CI, but runs anywhere)
// and asserts it reaches the first screen without throwing. Unlike the unit
// tests, this exercises real native plugin channels (secure storage, shared
// prefs, path provider, device_info) and AOT-compiled Dart — the layer where
// "works on web, crashes on the app" bugs live (the Accounts List<Account> cast
// crash, the device_info_plus SDK break). Needs NO network/backend, so it is
// safe to run on every CI build.
//
// It does NOT log in (that needs a backend); the authed-page coverage lives in
// login_smoke_test.dart against a locally running backend.

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/main.dart' as app;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('app boots to first paint without a plugin/AOT crash',
      (WidgetTester tester) async {
    // Preload fonts so first paint is styled (matches main(); avoids flake).
    await GoogleFonts.pendingFonts([GoogleFonts.sora(), GoogleFonts.firaCode()]);

    // Boot the real app widget (unauthenticated path — no seeded token).
    await tester.pumpWidget(const app.BuoyAIApp());

    // Fixed pumps — NEVER pumpAndSettle (the perpetual background animation
    // means the tree never settles).
    const step = Duration(milliseconds: 200);
    for (var elapsed = Duration.zero;
        elapsed < const Duration(seconds: 6);
        elapsed += step) {
      await tester.pump(step);
    }

    // The app must have rendered its root without an uncaught exception during
    // boot / plugin init. takeException() surfaces anything the framework caught.
    expect(tester.takeException(), isNull,
        reason: 'app threw during boot / first paint on this platform');
    expect(find.byType(MaterialApp), findsOneWidget,
        reason: 'MaterialApp did not mount');
  }, timeout: const Timeout(Duration(minutes: 5)));
}
