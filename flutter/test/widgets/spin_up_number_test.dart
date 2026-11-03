import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/widgets/spin_up_number.dart';

Widget _host(Widget child) => MaterialApp(home: Scaffold(body: child));

void main() {
  group('SpinUpNumber', () {
    testWidgets('spins from zero to the real value when active',
        (tester) async {
      await tester.pumpWidget(_host(SpinUpNumber(
        value: 250,
        active: true,
        format: (v) => v.round().toString(),
      )));

      // Before the delay elapses it holds at zero.
      expect(find.text('0'), findsOneWidget);

      await tester.pump(const Duration(milliseconds: 200)); // past delay
      await tester.pump(const Duration(milliseconds: 400)); // mid-flight
      final mid = int.parse(
          (tester.widget<Text>(find.byType(Text))).data!);
      expect(mid, greaterThan(0));
      expect(mid, lessThan(250));

      await tester.pumpAndSettle();
      expect(find.text('250'), findsOneWidget);
    });

    testWidgets('holds at zero while inactive, plays when activated',
        (tester) async {
      Widget build(bool active) => _host(SpinUpNumber(
            value: 99,
            active: active,
            format: (v) => v.round().toString(),
          ));

      await tester.pumpWidget(build(false));
      await tester.pump(const Duration(seconds: 2));
      expect(find.text('0'), findsOneWidget);

      await tester.pumpWidget(build(true));
      await tester.pumpAndSettle();
      expect(find.text('99'), findsOneWidget);
    });

    testWidgets('uses the format callback (currency style)', (tester) async {
      await tester.pumpWidget(_host(SpinUpNumber(
        value: 1234.5,
        active: true,
        format: (v) => '\$${v.toStringAsFixed(2)}',
      )));
      await tester.pumpAndSettle();
      expect(find.text('\$1234.50'), findsOneWidget);
    });
  });
}
