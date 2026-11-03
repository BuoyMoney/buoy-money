// Render check for the shared app toast (lib/widgets/app_toast.dart) — pumps it
// inside a stand-in for CinematicNavShell and captures a screenshot (saved to
// build/viz-screenshots/ by the driver). This verifies the actual rendered
// pixels — that the toast clears the bottom nav and the Android gesture inset
// rather than sitting flush against the screen edge.
//
// It pins the geometry directly, at an exact viewport and gesture inset, which
// is the point: reaching these toasts in the live app means logging in and
// driving several multi-step flows (subscription suggestions, transfers,
// billing) just to observe a bar that shows for 4s. Driving the live app IS
// possible, it's just a poor fit for
// asserting exact insets.
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/toast_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/widgets/app_toast.dart';

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  // Mirrors CinematicNavShell: the bottom nav is a Positioned(bottom: 0)
  // overlay on the root Scaffold, and the page's MediaQuery has its bottom
  // padding stripped. The toast is shown from the *page's* context but renders
  // in the root Scaffold, which is what put it under the nav bar before.
  Widget shell({required Widget page}) => MaterialApp(
        debugShowCheckedModeBanner: false,
        home: Scaffold(
          backgroundColor: const Color(0xFF04040A),
          body: Builder(
            builder: (context) => Stack(
              children: [
                MediaQuery.removePadding(
                  context: context,
                  removeBottom: true,
                  child: page,
                ),
                // Stand-in for the glass bottom nav (64dp + gesture inset).
                Positioned(
                  left: 0,
                  right: 0,
                  bottom: 0,
                  child: Container(
                    height: 64 + MediaQuery.of(context).padding.bottom,
                    decoration: BoxDecoration(
                      color: const Color(0xFF04040A).withValues(alpha: 0.6),
                      border: Border(
                        top: BorderSide(
                            color: Colors.white.withValues(alpha: 0.06),
                            width: 1),
                      ),
                    ),
                    child: const Center(
                      child: Text('▢   ▢   ▢   ▢   ▢',
                          style: TextStyle(color: Color(0xFF8A8A9A))),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      );

  Future<void> shoot(
    WidgetTester tester,
    String name, {
    required AppToastType type,
    required String message,
    String? actionLabel,
  }) async {
    // A gesture-nav Android phone — the case this fix is about. Without this
    // the harness renders at desktop width, where the shell shows a sidebar
    // instead of the bottom nav and the toast geometry is a different branch.
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(412, 915);
    tester.view.padding = const FakeViewPadding(bottom: 48);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(shell(
      page: Builder(
        builder: (context) => Scaffold(
          backgroundColor: Colors.transparent,
          body: Center(
            child: ElevatedButton(
              onPressed: () => showAppToast(
                context,
                message,
                type: type,
                actionLabel: actionLabel,
                onAction: actionLabel == null ? null : () {},
              ),
              child: const Text('show toast'),
            ),
          ),
        ),
      ),
    ));

    await tester.tap(find.text('show toast'));
    // Fixed pumps — let the entrance animation and fonts settle.
    for (var i = 0; i < 12; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }
    await binding.takeScreenshot(name);
  }

  testWidgets('render success toast with UNDO', (tester) async {
    await shoot(
      tester,
      'toast-success-undo',
      type: AppToastType.success,
      message: 'Added "Apple" as subscription',
      actionLabel: 'UNDO',
    );
  });

  testWidgets('render error toast', (tester) async {
    await shoot(
      tester,
      'toast-error',
      type: AppToastType.error,
      message: 'Couldn\'t update subscription. Please try again.',
    );
  });

  testWidgets('render neutral toast', (tester) async {
    await shoot(
      tester,
      'toast-neutral',
      type: AppToastType.neutral,
      message: '"Apple" snoozed for 30 days',
    );
  });
}
