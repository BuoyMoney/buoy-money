import 'package:buoy_ai/services/nav_history.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

/// End-to-end check of the Android back-gesture policy.
///
/// The real policy lives in `CinematicNavShell.build` — but that shell drags in AuthProvider,
/// referralService, SharedPreferences and animated background layers, which make a full pump
/// fragile and unrelated to what we're proving. So this test rebuilds the *exact same* policy
/// (the `canPop` expression + `_handleTabBack`) over the *real* [NavHistory] and a real GoRouter,
/// and drives it through `WidgetsBinding.handlePopRoute()` — the same code path Android's
/// predictive-back gesture triggers. If the shell's copy of this policy drifts, mirror it here.
///
/// What we assert:
///  - Back from a tab returns to the previously visited tab (the thing plain `context.go` lost).
///  - Back walks all the way to the start destination, then a final back exits (handlePopRoute
///    returns false → the OS backgrounds the app).
///  - A pushed sub-page pops normally (GoRouter.canPop() == true) before any tab-history handling.
void main() {
  // A stand-in shell whose back policy is copy-exact with CinematicNavShell.
  Widget shell(BuildContext context, GoRouterState state, Widget child) {
    final path = state.uri.path;
    navHistory.visit(path);

    final canPop = GoRouter.of(context).canPop() ||
        (path == NavHistory.startDestination && !navHistory.canGoBack);

    return PopScope(
      canPop: canPop,
      onPopInvokedWithResult: (didPop, _) {
        if (didPop) return;
        final prev = navHistory.goBack();
        if (prev != null) {
          context.go(prev);
        } else if (path != NavHistory.startDestination) {
          navHistory.clear();
          context.go(NavHistory.startDestination);
        }
      },
      child: Scaffold(body: child),
    );
  }

  GoRouter buildRouter({String initial = '/dashboard'}) {
    Widget page(String label) => Center(child: Text(label, key: Key('page-$label')));
    return GoRouter(
      initialLocation: initial,
      routes: [
        ShellRoute(
          builder: shell,
          routes: [
            GoRoute(path: '/dashboard', builder: (_, __) => page('/dashboard')),
            GoRoute(path: '/transactions', builder: (_, __) => page('/transactions')),
            GoRoute(path: '/budgets', builder: (_, __) => page('/budgets')),
            // A pushed sub-page under the shell (context.push) — has a real route to pop.
            GoRoute(path: '/billing', builder: (_, __) => page('/billing')),
          ],
        ),
      ],
    );
  }

  Future<void> pumpApp(WidgetTester tester, GoRouter router) async {
    await tester.pumpWidget(MaterialApp.router(routerConfig: router));
    await tester.pumpAndSettle();
  }

  // Simulates a system back gesture / predictive back. Returns false when nothing handled it,
  // i.e. the OS would background the app.
  Future<bool> systemBack() => WidgetsBinding.instance.handlePopRoute();

  setUp(navHistory.clear);

  testWidgets('back walks previous tabs, then exits at the start destination',
      (tester) async {
    final router = buildRouter();
    await pumpApp(tester, router);

    router.go('/transactions');
    await tester.pumpAndSettle();
    router.go('/budgets');
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/budgets')), findsOneWidget);

    // Back → previous tab (/transactions), handled (no exit).
    expect(await systemBack(), isTrue);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/transactions')), findsOneWidget);

    // Back → /dashboard, still handled.
    expect(await systemBack(), isTrue);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/dashboard')), findsOneWidget);

    // Back from the start destination with no history → NOT handled → OS exits.
    expect(await systemBack(), isFalse);
  });

  testWidgets('deep-linked into a non-home tab: back goes home first, then exits',
      (tester) async {
    final router = buildRouter(initial: '/transactions');
    await pumpApp(tester, router);
    expect(find.byKey(const Key('page-/transactions')), findsOneWidget);

    // Only one entry in history → back routes to the start destination (Material's
    // "return to start before exit"), and is handled.
    expect(await systemBack(), isTrue);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/dashboard')), findsOneWidget);

    // Now at start with no history → exit.
    expect(await systemBack(), isFalse);
  });

  testWidgets('a pushed sub-page pops before any tab-history handling', (tester) async {
    final router = buildRouter();
    await pumpApp(tester, router);

    router.go('/transactions');
    await tester.pumpAndSettle();
    router.push('/billing'); // real pushed route on top of the tab
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/billing')), findsOneWidget);

    // GoRouter.canPop() is true → the system pops the sub-page; we land back on the tab and
    // the tab history is untouched.
    expect(await systemBack(), isTrue);
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('page-/transactions')), findsOneWidget);
    expect(navHistory.stack, ['/dashboard', '/transactions']);
  });
}
