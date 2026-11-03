// Renderer-proof overflow sweep — the automated version of the manual device×page
// audit (issue #539). Boots the REAL app against a locally running backend, seeds an
// authenticated session via the dev debug-token endpoint, then walks every
// protected route at every device width, collecting RenderFlex/RenderBox overflow
// via FlutterError.onError. Emits a device×page table and (optionally) fails CI.
//
// WHY this and not a widget test: a plain `flutter test` widget test runs under
// fake_async and DEADLOCKS on the app's perpetual background gradient animation —
// it cannot render the authenticated app (it hung for hours). The
// integration_test binding uses a real clock, so fixed-duration pumps advance for
// real. The two hard rules that keep this from hanging:
//   1. LIVE binding (IntegrationTestWidgetsFlutterBinding) — real vsync/clock.
//   2. NEVER pumpAndSettle — the bg animation never settles. Use fixed pump()s.
//
// Run it via `flutter drive` (needs chromedriver) — see scripts/dev/overflow-sweep.sh.
//
//   --dart-define=API_BASE_URL=http://localhost:5100/api  # local API base (incl. /api); the app reads this too
//   --dart-define=SWEEP_USER=family-owner@buoy-ai.com  # seeded user to authenticate as
//   --dart-define=OVERFLOW_STRICT=true                 # fail the test if any overflow found

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/main.dart' as app;

// ---------------------------------------------------------------------------
// Config (overridable via --dart-define; defaults target the default local backend on port 5000).
// ---------------------------------------------------------------------------
// Includes the `/api` suffix, matching AppConfig.baseUrl (the app reads the same define).
const _apiBaseUrl =
    String.fromEnvironment('API_BASE_URL', defaultValue: 'http://localhost:5000/api');
const _sweepUser =
    String.fromEnvironment('SWEEP_USER', defaultValue: 'family-owner@buoy-ai.com');
const _strict =
    bool.fromEnvironment('OVERFLOW_STRICT', defaultValue: false);

/// Device widths to sweep, logical (CSS) pixels = MediaQuery.size. Covers: smallest phone, Pixel, 7"/10" tablet,
/// iPad Pro 12.9" (crosses the 900 wide breakpoint), laptop.
const List<(String, Size)> _devices = [
  ('iPhone SE 375', Size(375, 667)),
  ('Pixel 7 412', Size(412, 915)),
  ('7in tablet 600', Size(600, 960)),
  ('10in tablet 800', Size(800, 1280)),
  ('iPad Pro 1024', Size(1024, 1366)),
  ('Laptop 1440', Size(1440, 900)),
];

/// Protected routes under the nav shell. `/insights` redirects to `/dashboard`,
/// so it's intentionally omitted. Secondary deep routes (trusted-devices,
/// email-preferences, ai-quiz, dev-timeline) are excluded to keep the sweep tight.
const List<String> _routes = [
  '/dashboard',
  '/transactions',
  '/budgets',
  '/savings',
  '/accounts',
  '/subscriptions',
  '/ai-chat',
  '/notifications',
  '/profile',
  '/settings',
  '/billing',
  '/family',
];

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'overflow sweep: every protected route × every device width',
    (WidgetTester tester) async {
      // --- 1. Seed an authenticated session ------------------------------
      // Mint a real JWT from the dev debug-token endpoint and drop it where the
      // app reads it on boot (AuthProvider.initialize → secure storage). On web,
      // FlutterSecureStorage is encrypted localStorage.
      final dio = Dio(BaseOptions(baseUrl: _apiBaseUrl));
      final resp = await dio.get('/debug/token',
          queryParameters: {'email': _sweepUser});
      final token = (resp.data['token'] ?? resp.data['Token']) as String;
      const storage = FlutterSecureStorage();
      await storage.write(key: 'authToken', value: token);

      // --- 2. Boot the real app ------------------------------------------
      // Preload the exact fonts main() preloads BEFORE first paint. This is the
      // production first-paint state — and it eliminates the false-positive class
      // where a wider fallback font transiently overflows a row until the real
      // Google font lands. Skip main()'s analytics/Sentry side effects otherwise.
      await GoogleFonts.pendingFonts([
        GoogleFonts.sora(),
        GoogleFonts.sora(fontWeight: FontWeight.w500),
        GoogleFonts.sora(fontWeight: FontWeight.w600),
        GoogleFonts.sora(fontWeight: FontWeight.w700),
        GoogleFonts.firaCode(),
        GoogleFonts.firaCode(fontWeight: FontWeight.w600),
        GoogleFonts.firaCode(fontWeight: FontWeight.bold),
        GoogleFonts.instrumentSerif(),
        GoogleFonts.instrumentSerif(fontWeight: FontWeight.bold),
      ]);
      await tester.pumpWidget(const app.BuoyAIApp());

      // Let AuthProvider validate the token against the live API and settle the
      // first authenticated frame. Fixed pumps — pumpAndSettle would hang.
      await _pumpFor(tester, const Duration(seconds: 6));

      // Grab the app's GoRouter (works for both routerConfig and
      // routerDelegate wirings — see _appRouter).
      final router = _appRouter(tester);

      // --- 3. Collect overflow without aborting the sweep ----------------
      final overflows = <String>[]; // "route @ device\tmessage"
      final otherErrors = <String>[];
      String current = 'boot';
      var collecting = false; // only record during the steady-state probe window
      final priorOnError = FlutterError.onError;
      FlutterError.onError = (FlutterErrorDetails details) {
        if (!collecting) return; // ignore transient load/transition/font frames
        final text = details.exceptionAsString();
        if (text.toLowerCase().contains('overflow')) {
          overflows.add('$current\t${text.split('\n').first.trim()}');
        } else {
          // Record but DON'T forward to the binding (forwarding fails the whole
          // test and aborts the sweep). We surface these separately at the end.
          otherErrors.add('$current\t${text.split('\n').first.trim()}');
        }
      };

      // --- 4. Sweep: device (outer) × route (inner) ----------------------
      // IMPORTANT: binding.setSurfaceSize() is a NO-OP on web integration_test —
      // the app keeps rendering at the real browser window width. We drive the
      // viewport via tester.view.physicalSize / devicePixelRatio instead, which
      // TestFlutterView overrides for real. dpr=1 so physical == logical.
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final diag = <String>[];
      try {
        for (final (deviceName, size) in _devices) {
          tester.view.physicalSize = size; // logical == size (dpr 1)
          await _pumpFor(tester, const Duration(milliseconds: 600));
          // DIAGNOSTIC: confirm the app actually sees the requested logical width.
          final ctx = tester.element(find.byType(Navigator).last);
          final mqW = MediaQuery.of(ctx).size.width;
          diag.add('requested ${size.width.toInt()} -> MediaQuery ${mqW.toStringAsFixed(1)} '
              '(view.physical ${tester.view.physicalSize.width.toInt()}, dpr ${tester.view.devicePixelRatio})');
          for (final route in _routes) {
            current = '$route @ $deviceName';
            collecting = false;
            router.go(route);
            // Skip the cross-page transition (~500ms opacity/slide — no width
            // change, so no horizontal overflow, but both pages briefly co-exist).
            await _pumpFor(tester, const Duration(milliseconds: 700));
            // Collect across the page's own build + data load + layout. Overflow
            // fires during layout, so we must be collecting WHILE the page lays
            // out — a narrow post-hoc probe misses it (RenderFlex reports overflow
            // during its own layout pass, not on demand). Fonts are already
            // preloaded, so what we catch here is real layout overflow, not a
            // transient wider-fallback-font artifact.
            collecting = true;
            await _pumpFor(tester, const Duration(milliseconds: 2800));
            collecting = false;
          }
        }
      } finally {
        FlutterError.onError = priorOnError;
        tester.view.reset(); // restore the real browser surface
      }

      // --- 4b. Positive control ------------------------------------------
      // A green guard is worthless unless it can go red. Pump a deliberately
      // overflowing Row and assert the collector catches it. If this fails, a
      // "0 overflows" result above is meaningless (the guard is blind) — fail loud.
      var selfCheckCaught = false;
      final scOnError = FlutterError.onError;
      FlutterError.onError = (FlutterErrorDetails details) {
        if (details.exceptionAsString().toLowerCase().contains('overflow')) {
          selfCheckCaught = true;
        }
      };
      await tester.pumpWidget(const MaterialApp(
        home: Scaffold(
          body: Row(children: [
            SizedBox(width: 9000, height: 20),
            SizedBox(width: 9000, height: 20),
          ]),
        ),
      ));
      await _pumpFor(tester, const Duration(milliseconds: 300));
      FlutterError.onError = scOnError;

      // --- 5. Report -----------------------------------------------------
      final table = _renderTable(overflows);
      // Printed lines surface as `flutter:` in the drive output.
      debugPrint('\n===== SIZE DIAGNOSTIC =====');
      for (final d in diag) {
        debugPrint(d);
      }
      debugPrint('\n===== OVERFLOW SWEEP RESULTS =====\n$table');
      if (otherErrors.isNotEmpty) {
        debugPrint('\n----- non-overflow errors seen during sweep -----');
        for (final e in otherErrors.toSet()) {
          debugPrint(e.replaceAll('\t', '  ::  '));
        }
      }

      // Structured payload → build/integration_response_data.json (driver writes it).
      binding.reportData = <String, dynamic>{
        'overflowCount': overflows.length,
        'overflows': overflows,
        'otherErrors': otherErrors.toSet().toList(),
        'table': table,
        'devices': _devices.map((d) => d.$1).toList(),
        'routes': _routes,
        'sizeDiagnostic': diag,
        'selfCheckPassed': selfCheckCaught,
      };

      // The guard must be able to go red, else a clean sweep proves nothing.
      expect(selfCheckCaught, isTrue,
          reason: 'Overflow self-check FAILED — the collector did not catch a '
              'deliberate overflow, so the sweep results are not trustworthy.');

      if (_strict) {
        expect(overflows, isEmpty,
            reason: 'Overflow(s) detected:\n${overflows.join('\n')}');
      }
    },
    timeout: const Timeout(Duration(minutes: 45)),
  );
}

/// Pumps fixed-duration frames for [total] without ever waiting for the tree to
/// settle (the perpetual bg animation means it never does).
Future<void> _pumpFor(WidgetTester tester, Duration total) async {
  const step = Duration(milliseconds: 200);
  var elapsed = Duration.zero;
  while (elapsed < total) {
    await tester.pump(step);
    elapsed += step;
  }
}

/// Renders a device×route matrix: `.` = clean, `X` = overflow.
String _renderTable(List<String> overflows) {
  // Set of "route @ device" cells that overflowed.
  final hit = <String>{for (final o in overflows) o.split('\t').first};
  final buf = StringBuffer();
  // Header
  buf.writeln('route'.padRight(16) +
      _devices.map((d) => d.$1.split(' ').last.padLeft(6)).join());
  for (final route in _routes) {
    final row = StringBuffer(route.padRight(16));
    for (final (deviceName, _) in _devices) {
      final cell = '$route @ $deviceName';
      row.write((hit.contains(cell) ? 'X' : '.').padLeft(6));
    }
    buf.writeln(row);
  }
  final total = hit.length;
  buf.writeln('\n$total cell(s) with overflow'
      '${total == 0 ? '  ✅' : ''}.');
  if (overflows.isNotEmpty) {
    buf.writeln('\nDetails:');
    for (final o in overflows.toSet()) {
      buf.writeln('  ${o.replaceAll('\t', '  ::  ')}');
    }
  }
  return buf.toString();
}

/// Resolve the app's GoRouter regardless of how MaterialApp.router is wired
/// (routerConfig vs routerDelegate + backButtonDispatcher — see main.dart).
GoRouter _appRouter(WidgetTester tester) {
  final materialApp = tester.widget<MaterialApp>(find.byType(MaterialApp));
  final direct = materialApp.routerConfig;
  if (direct is GoRouter) return direct;
  return GoRouter.of(tester.element(find.byType(Navigator).first));
}
