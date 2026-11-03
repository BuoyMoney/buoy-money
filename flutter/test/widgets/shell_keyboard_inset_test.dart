import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/cinematic/cinematic_nav_shell.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Regression test for the Android "keyboard-height void" (v1.5.14/15).
///
/// The shell's `Scaffold` resizes for the keyboard and strips
/// `viewInsets.bottom` from its body's MediaQuery — but the shell then wrapped
/// its child in `MediaQuery.removePadding(context: context, …)`, whose data
/// comes from the shell's build context ABOVE the Scaffold. That re-injected
/// the raw keyboard inset under the already-shrunk body, so a page's nested
/// `Scaffold` resized a second time and viewInsets-padded sheets padded on top
/// of the shell's resize — each reserving one extra keyboard height of blank
/// space above the IME. Confirmed by on-device measurement (emulator, IME
/// open): child constraints already keyboard-shrunk while its MediaQuery
/// still reported the full inset.
///
/// These tests rebuild the shell's exact structure — outer resizing Scaffold,
/// child MediaQuery rebuilt from the context ABOVE the Scaffold via
/// [stripShellOwnedInsets] (pumping the real `CinematicNavShell` drags in
/// AuthProvider/SharedPreferences/animated layers) — and assert the keyboard
/// is consumed exactly once. If the transform stops removing viewInsets, the
/// markers land a second keyboard height up and these fail.
void main() {
  const screenHeight = 800.0;
  const keyboard = 300.0;

  /// Outer tree shaped like CinematicNavShell's mobile branch: a MediaQuery
  /// reporting an open keyboard, a resizing Scaffold, and the child's
  /// MediaQuery rebuilt from the SHELL's context (above the Scaffold) — the
  /// exact pattern that used to leak the keyboard inset back in.
  Widget shellLike(WidgetTester tester, Widget child) {
    tester.view.physicalSize = const Size(400, screenHeight);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    return MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: MediaQuery(
        data: const MediaQueryData(
          size: Size(400, screenHeight),
          viewInsets: EdgeInsets.only(bottom: keyboard),
        ),
        child: Builder(
          builder: (shellContext) => Scaffold(
            body: MediaQuery(
              data: stripShellOwnedInsets(MediaQuery.of(shellContext),
                  isDesktop: false),
              child: child,
            ),
          ),
        ),
      ),
    );
  }

  test('stripShellOwnedInsets removes the keyboard inset and padding', () {
    const data = MediaQueryData(
      size: Size(400, screenHeight),
      padding: EdgeInsets.only(top: 40, bottom: 20),
      viewInsets: EdgeInsets.only(bottom: keyboard),
    );

    final mobile = stripShellOwnedInsets(data, isDesktop: false);
    expect(mobile.viewInsets.bottom, 0);
    expect(mobile.padding.top, 0);
    expect(mobile.padding.bottom, 0);

    final desktop = stripShellOwnedInsets(data, isDesktop: true);
    expect(desktop.viewInsets.bottom, 0);
    expect(desktop.padding.top, 0);
    // Desktop keeps the bottom padding (no bottom nav overlay consumes it).
    expect(desktop.padding.bottom, 20);
  });

  testWidgets(
      'nested page Scaffold does not resize a second time (accounts void)',
      (tester) async {
    const marker = Key('bottom-marker');
    await tester.pumpWidget(shellLike(
      tester,
      Scaffold(
        backgroundColor: Colors.transparent,
        body: Align(
          alignment: Alignment.bottomCenter,
          child: SizedBox(key: marker, height: 10, width: 10),
        ),
      ),
    ));

    // The outer (shell) Scaffold already shrank the body by the keyboard.
    // The marker must sit directly on the keyboard's top edge — one keyboard
    // height up, not two.
    final bottom = tester.getBottomLeft(find.byKey(marker)).dy;
    expect(bottom, screenHeight - keyboard);
  });

  testWidgets(
      'viewInsets-padded sheet content gains no extra height (create-tx void)',
      (tester) async {
    const marker = Key('sheet-content');
    await tester.pumpWidget(shellLike(
      tester,
      Align(
        alignment: Alignment.bottomCenter,
        // The common sheet pattern: pad the sheet by viewInsets.bottom.
        // Inside the shell that inset is already consumed, so it reads 0.
        child: Builder(
          builder: (context) => Padding(
            padding: EdgeInsets.only(
                bottom: MediaQuery.of(context).viewInsets.bottom),
            child: const SizedBox(key: marker, height: 10, width: 10),
          ),
        ),
      ),
    ));

    final bottom = tester.getBottomLeft(find.byKey(marker)).dy;
    expect(bottom, screenHeight - keyboard);
  });
}
