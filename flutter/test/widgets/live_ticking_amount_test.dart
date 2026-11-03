import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/utils/formatters.dart';
import 'package:buoy_ai/widgets/cinematic/live_ticking_amount.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('interpolatedEarnedToDate', () {
    // A 30-day period whose day 1 is the fetch date. $3/day allowance.
    final fetchedAt = DateTime(2026, 8, 1, 9, 30); // fetched 9:30am, day 1

    double earned(DateTime now, {int daysElapsed = 1}) =>
        interpolatedEarnedToDate(
          dailyAllowance: 3.0,
          daysElapsed: daysElapsed,
          totalDays: 30,
          fetchedAt: fetchedAt,
          now: now,
        );

    test('day 1 starts from zero at local midnight', () {
      expect(earned(DateTime(2026, 8, 1, 0, 0)), 0);
    });

    test('day 1 midday is half the daily allowance', () {
      expect(earned(DateTime(2026, 8, 1, 12, 0)), closeTo(1.5, 1e-9));
    });

    test('day 1 converges on the full allowance approaching midnight', () {
      final justBeforeMidnight = DateTime(2026, 8, 1, 23, 59, 59);
      expect(earned(justBeforeMidnight), closeTo(3.0, 0.001));
    });

    test('rolls over at local midnight without a refetch', () {
      // Status still says daysElapsed=1 (fetched Aug 1), but the clock has
      // crossed into Aug 2: the day index is re-derived from the device date.
      expect(earned(DateTime(2026, 8, 2, 0, 0)), closeTo(3.0, 1e-9));
      expect(earned(DateTime(2026, 8, 2, 12, 0)), closeTo(4.5, 1e-9));
    });

    test('mid-period day uses full credit for prior days', () {
      // daysElapsed=10 at fetch, viewed the same day at 06:00 (25% of day 10).
      final now = DateTime(2026, 8, 1, 6, 0);
      expect(earned(now, daysElapsed: 10), closeTo(3.0 * (9 + 0.25), 1e-9));
    });

    test('clamps at the period total once the period is over', () {
      expect(earned(DateTime(2026, 9, 15, 12, 0), daysElapsed: 30), 90.0);
    });

    test('returns zero before the period starts', () {
      expect(
        interpolatedEarnedToDate(
          dailyAllowance: 3.0,
          daysElapsed: 0,
          totalDays: 30,
          fetchedAt: fetchedAt,
          now: DateTime(2026, 8, 1, 12, 0),
        ),
        0,
      );
    });

    test('never returns a negative value', () {
      // daysElapsed=1 but the clock is a day BEHIND the fetch date (clock
      // skew): day index drops below 1 and the result floors at zero.
      expect(earned(DateTime(2026, 7, 31, 12, 0)), 0);
    });
  });

  group('formatCurrencyLive', () {
    test('renders four decimals', () {
      expect(formatCurrencyLive(12.34567), '\$12.3457');
      expect(formatCurrencyLive(0), '\$0.0000');
    });
  });

  group('LiveTickingAmount', () {
  /// The odometer renders one Text per character — reassemble the string.
  String rolled(WidgetTester tester) => tester
      .widgetList<Text>(find.descendant(
          of: find.byType(LiveTickingAmount), matching: find.byType(Text)))
      .map((t) => t.data ?? '')
      .join();

    testWidgets('ticks the rendered string as the clock advances',
        (tester) async {
      var now = DateTime(2026, 8, 1, 12, 0);
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: LiveTickingAmount(
          // $8.64/day pace = exactly $0.0001 per second.
          compute: (t) =>
              t.difference(DateTime(2026, 8, 1)).inMilliseconds / 86400000 * 8.64,
          styleBuilder: (_) => const TextStyle(),
          clock: () => now,
        ),
      ));
      expect(rolled(tester), '\$4.3200');

      now = now.add(const Duration(seconds: 10));
      await tester.pump(const Duration(milliseconds: 100));
      // Let the 220ms odometer roll finish — mid-transition the outgoing and
      // incoming digit glyphs briefly coexist.
      await tester.pump(const Duration(milliseconds: 300));
      expect(rolled(tester), '\$4.3210');

      // Widget holds a periodic timer; dispose it cleanly.
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('does not rebuild when the formatted value is unchanged',
        (tester) async {
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: LiveTickingAmount(
          compute: (_) => 1.5,
          styleBuilder: (_) => const TextStyle(),
          clock: () => DateTime(2026, 8, 1, 12, 0),
        ),
      ));
      await tester.pump(const Duration(seconds: 1));
      expect(rolled(tester), '\$1.5000');
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('renders a static two-decimal amount under reduced motion',
        (tester) async {
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: MediaQuery(
          data: const MediaQueryData(disableAnimations: true),
          child: LiveTickingAmount(
            compute: (_) => 12.3456,
            styleBuilder: (_) => const TextStyle(),
            clock: () => DateTime(2026, 8, 1, 12, 0),
          ),
        ),
      ));
      expect(find.text('\$12.35'), findsOneWidget);
      await tester.pumpWidget(const SizedBox());
    });
  });
}
