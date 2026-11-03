import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/dashboard/section_header.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

/// The dashboard section headers put a trailing action (a "View All" link or a refresh icon) on
/// the right. They must all sit flush with the section's right edge — which equals the card's
/// right edge below them. Two "View All" links plus a refresh icon were each drifting inward by a
/// different amount because every button carries its own intrinsic padding, and past fixes only
/// stripped one button at a time. This measures the *actual rendered* right edge of each trailing
/// glyph and asserts it reaches the header's right edge, so the drift can't come back.

TextButton _viewAll() => TextButton(
      onPressed: () {},
      child: Text('View All',
          style: GoogleFonts.sora(fontSize: 12, color: const Color(0xFF7B61FF))),
    );

IconButton _refresh() => IconButton(
      icon: const Icon(Icons.refresh, size: 18),
      onPressed: () {},
    );

// Mirrors how the dashboard actually mounts headers: a stretch Column (see
// _buildAIInsightsSection / _buildRecentTransactionsSection). Width matters — the drift this
// guards against is INVISIBLE at narrow widths and grows with available space, so test wide.
Future<void> _pump(WidgetTester tester, List<Widget> headers,
    {double width = 760}) async {
  GoogleFonts.config.allowRuntimeFetching = false;
  await tester.pumpWidget(MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(
      body: Padding(
        padding: const EdgeInsets.all(20),
        child: SizedBox(
          width: width,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            mainAxisSize: MainAxisSize.min,
            children: headers,
          ),
        ),
      ),
    ),
  ));
  await tester.pump();
}

void main() {
  testWidgets('a single trailing action reaches the header right edge', (tester) async {
    await _pump(tester, [
      const DashboardSectionHeader(
        key: Key('h'),
        title: 'Budget Overview',
        icon: Icons.pie_chart_outline,
      ).copyTrailing(_viewAll()),
    ]);

    final headerRight = tester.getRect(find.byKey(const Key('h'))).right;
    final textRight = tester.getRect(find.text('View All')).right;

    // Flush: the glyph's right edge is at the header's right edge (allow a hair for antialiasing).
    expect((headerRight - textRight).abs(), lessThan(1.0),
        reason: 'trailing action must sit flush to the section right edge, not inset by button padding');
  });

  testWidgets('mixed trailing types all share the same right edge', (tester) async {
    await _pump(tester, [
      const DashboardSectionHeader(title: 'Budget Overview', icon: Icons.pie_chart_outline)
          .copyTrailing(_viewAll()),
      const SizedBox(height: 24),
      const DashboardSectionHeader(title: 'Savings Goals', icon: Icons.savings_outlined)
          .copyTrailing(_viewAll()),
      const SizedBox(height: 24),
      const DashboardSectionHeader(title: 'AI Insights', icon: Icons.auto_awesome)
          .copyTrailing(_refresh()),
    ]);

    final rights = <double>[
      tester.getRect(find.text('View All').first).right,
      tester.getRect(find.text('View All').last).right,
      tester.getRect(find.byIcon(Icons.refresh)).right,
    ];

    // Every trailing glyph lands on the same vertical line — no staircase.
    final spread = rights.reduce((a, b) => a > b ? a : b) - rights.reduce((a, b) => a < b ? a : b);
    expect(spread, lessThan(1.0),
        reason: 'View All, View All and the refresh icon must align; measured rights: $rights');
  });

  // Regression: the AI Insights refresh icon drifted ~180px inward on-device while the narrow
  // (360px) test above stayed flush. Cause was a Flexible title + a Spacer both taking flex:1 —
  // the short title under-used its half and the leftover was dumped past the trailing action.
  // The drift scaled with width, so it only showed at real widths. Assert flush when WIDE.
  testWidgets('IconButton trailing stays flush at a wide (phone) width', (tester) async {
    await _pump(
      tester,
      [
        const DashboardSectionHeader(key: Key('h'), title: 'AI Insights', icon: Icons.auto_awesome)
            .copyTrailing(_refresh()),
      ],
      width: 760,
    );

    final headerRight = tester.getRect(find.byKey(const Key('h'))).right;
    final iconRight = tester.getRect(find.byIcon(Icons.refresh)).right;
    expect((headerRight - iconRight).abs(), lessThan(1.0),
        reason: 'refresh icon must be flush to the right edge at wide widths, not dragged inward '
            'by a title/Spacer double-flex (measured drift ${headerRight - iconRight})');
  });

  testWidgets('a count badge does not push the trailing action off the right edge', (tester) async {
    await _pump(
      tester,
      [
        const DashboardSectionHeader(
          key: Key('h'),
          title: 'Recent Transactions',
          icon: Icons.receipt_long_outlined,
          count: 25,
        ).copyTrailing(_viewAll()),
      ],
      width: 760,
    );

    final headerRight = tester.getRect(find.byKey(const Key('h'))).right;
    final textRight = tester.getRect(find.text('View All')).right;
    expect((headerRight - textRight).abs(), lessThan(1.0),
        reason: 'trailing action stays flush even with a count badge present');
  });
}

/// Small test helper: rebuild a header with a trailing widget (keeps the test call sites terse
/// while the widget's own constructor stays const-friendly).
extension on DashboardSectionHeader {
  DashboardSectionHeader copyTrailing(Widget trailing) => DashboardSectionHeader(
        key: key,
        title: title,
        icon: icon,
        count: count,
        trailing: trailing,
      );
}
