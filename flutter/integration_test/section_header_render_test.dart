// Render check for the dashboard section-header trailing alignment.
//
// The dashboard's three section headers (Budget Overview, Savings Goals, AI Insights) put a
// trailing action on the right — two "View All" text links and a refresh icon. They were drifting
// inward by different amounts because each button's own padding was left in place. This pumps the
// real DashboardSectionHeader with each trailing type, stacked at a phone width, and screenshots
// them so the right edges can be eyeballed as a single vertical line.
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/section_header_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/widgets/dashboard/section_header.dart';

const _accent = Color(0xFF7B61FF);
const _textMuted = Color(0xFF8A8A9A);

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  Widget viewAll() => TextButton(
        onPressed: () {},
        child: Text('View All',
            style: GoogleFonts.sora(fontSize: 12, color: _accent, fontWeight: FontWeight.w500)),
      );

  Widget refresh() => IconButton(
        icon: const Icon(Icons.refresh, size: 18, color: _textMuted),
        onPressed: () {},
        tooltip: 'Refresh',
      );

  // A card the same width as the header, to eyeball the right-edge alignment against.
  Widget card() => Container(
        height: 40,
        decoration: BoxDecoration(
          color: const Color(0xFF12121C),
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: Colors.white.withValues(alpha: 0.06)),
        ),
      );

  Widget section(String title, IconData icon, Widget trailing) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          DashboardSectionHeader(title: title, icon: icon, trailing: trailing),
          const SizedBox(height: 8),
          card(),
        ],
      );

  testWidgets('section header trailing actions align flush to the right edge', (tester) async {
    tester.view.devicePixelRatio = 1.0;
    tester.view.physicalSize = const Size(412, 915);
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        backgroundColor: const Color(0xFF04040A),
        body: Padding(
          padding: const EdgeInsets.all(20),
          child: Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              section('Budget Overview', Icons.pie_chart_outline, viewAll()),
              const SizedBox(height: 28),
              section('Savings Goals', Icons.savings_outlined, viewAll()),
              const SizedBox(height: 28),
              section('AI Insights', Icons.auto_awesome, refresh()),
            ],
          ),
        ),
      ),
    ));

    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
    await binding.takeScreenshot('section-headers-aligned');
  });
}
