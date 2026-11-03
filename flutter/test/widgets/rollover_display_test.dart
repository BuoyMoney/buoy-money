import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/widgets/cinematic/rollover_display.dart';

void main() {
  Widget wrap(Widget child) => MaterialApp(
        home: Scaffold(
          body: Center(child: SizedBox(width: 300, child: child)),
        ),
      );

  group('RolloverChip', () {
    testWidgets('positive rollover reads "saved"', (tester) async {
      await tester.pumpWidget(wrap(const RolloverChip(rolloverBalance: 40)));
      expect(find.textContaining('saved'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('negative rollover reads "over"', (tester) async {
      await tester.pumpWidget(wrap(const RolloverChip(rolloverBalance: -45)));
      expect(find.textContaining('over'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });

    testWidgets('overspend below the budget does not unlock the achievement',
        (tester) async {
      await tester.pumpWidget(
          wrap(const RolloverChip(rolloverBalance: -45, allocated: 200)));
      expect(find.textContaining('over'), findsOneWidget);
      expect(find.text('Time Traveler'), findsNothing);
      expect(tester.takeException(), isNull);
    });

    testWidgets(
        'overspend that eats the whole budget unlocks the "Time Traveler" achievement',
        (tester) async {
      await tester.pumpWidget(
          wrap(const RolloverChip(rolloverBalance: -350, allocated: 300)));
      expect(find.textContaining('over'), findsOneWidget);
      expect(find.textContaining('next month'), findsOneWidget);
      expect(find.text('Time Traveler'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('RolloverSpendBar', () {
    testWidgets('renders for saved / overspent / over-budget / big-overspend',
        (tester) async {
      final cases = <(double, double, double)>[
        (200, 95, 40), // saved (bonus tail)
        (200, 150, -45), // carried overspend (amber lead-in)
        (200, 150, 0), // no rollover
        (200, 400, -250), // wildly over budget
        (200, 0, 120), // untouched with a large bonus
      ];
      for (final c in cases) {
        await tester.pumpWidget(wrap(RolloverSpendBar(
            allocated: c.$1, spent: c.$2, rolloverBalance: c.$3)));
        expect(tester.takeException(), isNull);
      }
    });
  });
}
