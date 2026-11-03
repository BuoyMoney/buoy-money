import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/cinematic/glass_settings.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';

void main() {
  testWidgets('back pops the router stack when the page was pushed from a top-level route', (tester) async {
    tester.view.physicalSize = const Size(900, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final router = GoRouter(initialLocation: '/approve', routes: [
      GoRoute(
        path: '/approve',
        builder: (context, _) => Scaffold(
          body: TextButton(
            key: const ValueKey('to-cloud'),
            onPressed: () => context.push('/settings/cloud'),
            child: const Text('Approval page'),
          ),
        ),
      ),
      // The destination sits in a nested navigator, as the Cloud page does inside the shell.
      ShellRoute(
        builder: (context, state, child) => Scaffold(body: child),
        routes: [
          GoRoute(
            path: '/settings/cloud',
            builder: (context, _) => const GlassSubPage(title: 'Cloud', children: [SizedBox()]),
          ),
          GoRoute(path: '/settings', builder: (context, _) => const Text('Settings page')),
        ],
      ),
    ]);
    await tester.pumpWidget(MaterialApp.router(
      routerConfig: router,
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
    ));
    await tester.tap(find.byKey(const ValueKey('to-cloud')));
    await tester.pumpAndSettle();
    expect(find.text('Cloud'), findsOneWidget);

    await tester.tap(find.byKey(const ValueKey('glass-subpage-back')));
    await tester.pumpAndSettle();
    expect(find.text('Approval page'), findsOneWidget);
    expect(find.text('Settings page'), findsNothing);
  });
}
