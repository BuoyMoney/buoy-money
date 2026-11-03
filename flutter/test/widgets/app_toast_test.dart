import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/app_toast.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  /// Stands in for CinematicNavShell: a root Scaffold with the bottom nav as a
  /// `Positioned(bottom: 0)` overlay, and the page wrapped in
  /// `MediaQuery.removePadding(removeBottom: true)` — the arrangement that
  /// makes the toast render in the *shell's* Scaffold while the page's own
  /// MediaQuery reports no bottom inset.
  Widget shell({required Widget page}) => MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          backgroundColor: Colors.transparent,
          body: Builder(
            builder: (context) => Stack(
              children: [
                MediaQuery.removePadding(
                  context: context,
                  removeBottom: true,
                  child: page,
                ),
                const Positioned(
                  left: 0,
                  right: 0,
                  bottom: 0,
                  child: SizedBox(height: 64),
                ),
              ],
            ),
          ),
        ),
      );

  Widget trigger(void Function(BuildContext) onTap) => Builder(
        builder: (context) => Scaffold(
          backgroundColor: Colors.transparent,
          body: Center(
            child: ElevatedButton(
              onPressed: () => onTap(context),
              child: const Text('go'),
            ),
          ),
        ),
      );

  SnackBar findSnackBar(WidgetTester tester) =>
      tester.widget<SnackBar>(find.byType(SnackBar));

  testWidgets('is swipe-dismissable sideways, not downward', (tester) async {
    await tester.pumpWidget(shell(
      page: trigger((c) => showAppToast(c, 'hi', type: AppToastType.success)),
    ));
    await tester.tap(find.text('go'));
    await tester.pump();

    // The SDK default is DismissDirection.down, which Android's notification
    // shade claims — leaving the toast impossible to swipe away.
    expect(findSnackBar(tester).dismissDirection, DismissDirection.horizontal);
  });

  testWidgets('success toast is green with readable dark text', (tester) async {
    await tester.pumpWidget(shell(
      page: trigger((c) => showAppToast(c, 'Added "Apple" as subscription',
          type: AppToastType.success)),
    ));
    await tester.tap(find.text('go'));
    await tester.pump();

    final bar = findSnackBar(tester);
    expect(bar.backgroundColor, const Color(0xFF34D399));
    expect(bar.behavior, SnackBarBehavior.floating);

    final text = tester.widget<Text>(find.text('Added "Apple" as subscription'));
    expect(text.style?.color, const Color(0xFF04040A));
  });

  testWidgets('clears the mobile nav bar and the gesture inset', (tester) async {
    // A gesture-nav Android device: 48dp of bottom padding.
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(400, 900);
    tester.view.padding = const FakeViewPadding(bottom: 48);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(shell(page: trigger((c) => showAppToast(c, 'hi'))));
    await tester.tap(find.text('go'));
    await tester.pump();

    // 64 (nav) + 48 (gesture inset) + 12 (gap). The page's own MediaQuery
    // reports padding.bottom == 0 here, so this only passes because the helper
    // reads the inset from the view rather than the ambient MediaQuery.
    expect(
      findSnackBar(tester).margin,
      const EdgeInsets.only(left: 16, right: 16, bottom: 124),
    );
  });

  testWidgets('page MediaQuery really does report a zeroed bottom inset',
      (tester) async {
    // Guards the assumption behind the helper: if the shell ever stops
    // stripping the padding, the View-based read is still correct, but this
    // documents why it is needed.
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(400, 900);
    tester.view.padding = const FakeViewPadding(bottom: 48);
    addTearDown(tester.view.reset);

    late double pagePaddingBottom;
    await tester.pumpWidget(shell(
      page: Builder(builder: (context) {
        pagePaddingBottom = MediaQuery.of(context).padding.bottom;
        return const SizedBox();
      }),
    ));

    expect(pagePaddingBottom, 0);
  });

  testWidgets('public routes clear the gesture bar but not a nav bar',
      (tester) async {
    // Pages outside CinematicNavShell (login, register, invitation accept)
    // have no bottom nav — but still must clear the gesture inset.
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(400, 900);
    tester.view.padding = const FakeViewPadding(bottom: 48);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(shell(
      page: trigger((c) => showAppToast(c, 'hi', clearsBottomNav: false)),
    ));
    await tester.tap(find.text('go'));
    await tester.pump();

    expect(
      findSnackBar(tester).margin,
      const EdgeInsets.only(left: 16, right: 16, bottom: 48 + 16),
    );
  });

  testWidgets('desktop sits at the bottom edge (nav is a sidebar)',
      (tester) async {
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(1400, 900);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(shell(page: trigger((c) => showAppToast(c, 'hi'))));
    await tester.tap(find.text('go'));
    await tester.pump();

    expect(
      findSnackBar(tester).margin,
      const EdgeInsets.only(left: 16, right: 16, bottom: 16),
    );
  });

  testWidgets('action fires and replaces rather than queues', (tester) async {
    var undone = 0;
    await tester.pumpWidget(shell(
      page: trigger((c) => showAppToast(
            c,
            'Added "Apple" as subscription',
            type: AppToastType.success,
            actionLabel: 'UNDO',
            onAction: () => undone++,
          )),
    ));

    await tester.tap(find.text('go'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('go')); // second toast replaces the first
    await tester.pumpAndSettle();
    expect(find.byType(SnackBar), findsOneWidget);

    await tester.tap(find.text('UNDO'));
    await tester.pump();
    expect(undone, 1);
  });
}
