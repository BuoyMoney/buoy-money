import 'package:buoy_ai/widgets/dashboard/stat_card.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

/// The desktop dashboard stat tiles are compact — content-height, no dead space. The failure mode
/// while building this was the opposite: too short, so the content overflowed the fixed-aspect
/// grid cell (a yellow-and-black overflow stripe). These tests pin the compact tile into a cell of
/// the real desktop dimensions and assert it fits, and that the change % renders inline with the
/// value rather than pinned to the bottom.

Widget _cell(Widget child, {required double width, required double height}) => MaterialApp(
      home: Scaffold(
        body: Center(
          child: SizedBox(width: width, height: height, child: child),
        ),
      ),
    );

void main() {
  setUp(() => GoogleFonts.config.allowRuntimeFetching = false);

  testWidgets('compact tile fits a desktop grid cell without overflowing', (tester) async {
    // A 4-up desktop grid at ~1030px content width and aspect 2.5 → ~258x103 per cell.
    await tester.pumpWidget(_cell(
      const DashboardStatCard(
        label: 'Monthly Income',
        value: '\$16,500.00',
        icon: Icons.trending_up,
        color: Color(0xFF34D399),
        change: 230.9,
        changePositiveIsGood: true,
        compact: true,
      ),
      width: 258,
      height: 103,
    ));
    await tester.pump();

    // No RenderFlex overflow was recorded while laying out.
    expect(tester.takeException(), isNull);
  });

  testWidgets('compact tile shows the change % beside the value, not below it', (tester) async {
    await tester.pumpWidget(_cell(
      const DashboardStatCard(
        label: 'Monthly Income',
        value: '\$16,500.00',
        icon: Icons.trending_up,
        color: Color(0xFF34D399),
        change: 230.9,
        compact: true,
      ),
      width: 258,
      height: 103,
    ));
    await tester.pump();

    final valueRect = tester.getRect(find.text('\$16,500.00'));
    final changeRect = tester.getRect(find.text('230.9%'));

    // Inline: the change sits to the right of the value and shares its vertical band.
    expect(changeRect.left, greaterThan(valueRect.right - 1));
    expect((changeRect.center.dy - valueRect.center.dy).abs(), lessThan(valueRect.height));
  });

  testWidgets('a very long value scales down instead of overflowing', (tester) async {
    await tester.pumpWidget(_cell(
      const DashboardStatCard(
        label: 'Total Cash',
        value: '\$1,234,567,890.00',
        icon: Icons.account_balance_wallet,
        color: Color(0xFF34D399),
        compact: true,
      ),
      width: 258,
      height: 103,
    ));
    await tester.pump();

    expect(tester.takeException(), isNull);
  });

  testWidgets('mobile (non-compact) tile keeps the top-label / bottom-value spread', (tester) async {
    // The phone layout the user likes: a 2-up grid at aspect 1.25 → taller cell.
    await tester.pumpWidget(_cell(
      const DashboardStatCard(
        label: 'Monthly Income',
        value: '\$16,500.00',
        icon: Icons.trending_up,
        color: Color(0xFF34D399),
        change: 230.9,
        compact: false,
      ),
      width: 190,
      height: 152,
    ));
    await tester.pump();

    expect(tester.takeException(), isNull);

    // Value sits in the lower portion of the tile (spread), unlike the compact top-stacked layout.
    final tileRect = tester.getRect(find.byType(DashboardStatCard));
    final valueRect = tester.getRect(find.text('\$16,500.00'));
    expect(valueRect.center.dy, greaterThan(tileRect.center.dy),
        reason: 'mobile spread pins the value to the lower half');
  });
}
