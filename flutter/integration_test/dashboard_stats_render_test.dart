// Desktop render check for the dashboard stat tiles — the four summary cards were far too tall,
// with the number floating at the bottom and ~100px of dead space in the middle on a 4-up grid.
// This renders the compact tiles at a desktop width so the height can be eyeballed.
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/dashboard_stats_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/widgets/dashboard/stat_card.dart';

const _income = Color(0xFF34D399);
const _expense = Color(0xFFF87171);

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  Widget grid({required bool compact, required double aspect, required int cols}) => GridView.count(
        crossAxisCount: cols,
        crossAxisSpacing: 12,
        mainAxisSpacing: 12,
        childAspectRatio: aspect,
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        children: [
          DashboardStatCard(
              label: 'Total Cash',
              value: '\$57,500.00',
              icon: Icons.account_balance_wallet,
              color: _income,
              compact: compact),
          DashboardStatCard(
              label: 'Monthly Income',
              value: '\$16,500.00',
              icon: Icons.trending_up,
              color: _income,
              change: 230.9,
              changePositiveIsGood: true,
              compact: compact),
          DashboardStatCard(
              label: 'Monthly Expenses',
              value: '\$7,518.25',
              icon: Icons.trending_down,
              color: _expense,
              change: 893.8,
              changePositiveIsGood: false,
              compact: compact),
          DashboardStatCard(
              label: 'Total Debt',
              value: '\$2,847.32',
              icon: Icons.credit_card,
              color: _expense,
              compact: compact),
        ],
      );

  testWidgets('desktop compact stat tiles', (tester) async {
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(1440, 900);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        backgroundColor: const Color(0xFF04040A),
        body: Padding(
          // Roughly the dashboard's desktop content inset (sidebar + 6% padding).
          padding: const EdgeInsets.fromLTRB(280, 40, 90, 40),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.start,
            children: [
              grid(compact: true, aspect: 2.5, cols: 4),
            ],
          ),
        ),
      ),
    ));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    await binding.takeScreenshot('dashboard-stats-desktop-compact');
  });

  testWidgets('mobile spread stat tiles (unchanged)', (tester) async {
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(412, 915);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        backgroundColor: const Color(0xFF04040A),
        body: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.start,
            children: [
              grid(compact: false, aspect: 1.25, cols: 2),
            ],
          ),
        ),
      ),
    ));
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    await binding.takeScreenshot('dashboard-stats-mobile-spread');
  });
}
