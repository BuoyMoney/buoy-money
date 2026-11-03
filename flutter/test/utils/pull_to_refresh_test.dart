import 'package:buoy_ai/utils/pull_to_refresh.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// The manual refresh/sync button is desktop-only: mobile relies on pull-to-refresh (a touch
/// gesture a mouse can't perform). This checks the breakpoint that decides which layout shows it.
Future<bool> _showsAt(WidgetTester tester, double width) async {
  late bool result;
  await tester.pumpWidget(MediaQuery(
    data: MediaQueryData(size: Size(width, 900)),
    child: Builder(builder: (context) {
      result = showsManualRefresh(context);
      return const SizedBox();
    }),
  ));
  return result;
}

void main() {
  testWidgets('shows the button on a desktop-width layout', (tester) async {
    expect(await _showsAt(tester, 1200), isTrue);
  });

  testWidgets('hides the button on a phone-width layout (pull-to-refresh instead)', (tester) async {
    expect(await _showsAt(tester, 390), isFalse);
    expect(await _showsAt(tester, 430), isFalse);
  });

  testWidgets('hides on a tablet-portrait width below the desktop breakpoint', (tester) async {
    // 800 is the breakpoint; a narrower tablet is still the touch/mobile layout.
    expect(await _showsAt(tester, 768), isFalse);
  });
}
