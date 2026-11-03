// Render check for the #388 AI visualization widgets — pumps each with sample data
// and captures a screenshot (saved to build/viz-screenshots/ by the driver). This
// verifies the actual rendered pixels without needing login or seeded data, which
// keeps it cheap and deterministic. (Driving the live app through login IS
// possible, it's just unnecessary to
// render a widget from fixed sample data.)
//
// Run: flutter drive --driver test_driver/integration_test.dart \
//        --target integration_test/viz_render_test.dart -d web-server \
//        --browser-name chrome --driver-port 4444

import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'package:buoy_ai/services/visualization_service.dart';
import 'package:buoy_ai/widgets/ai/visualization_chart.dart';
import 'package:buoy_ai/widgets/ai/ai_rich_message.dart';

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  UserVisualization sampleViz(String type) => UserVisualization(
        id: 1,
        chartType: type,
        title: '${type[0].toUpperCase()}${type.substring(1)} chart',
        message: 'Sample $type visualization',
        motivationalMessage: null,
        chartData: [
          ChartDataPoint(name: 'Rent', value: 1800),
          ChartDataPoint(name: 'Food', value: 720),
          ChartDataPoint(name: 'Transit', value: 240),
          ChartDataPoint(name: 'Fun', value: 410),
          ChartDataPoint(name: 'Save', value: 600),
        ],
        chartConfig: ChartConfig(valuePrefix: r'$'),
        category: 'spending',
        priority: 'medium',
        confidence: 0.9,
        isStarred: false,
        isDismissed: false,
        viewCount: 0,
        lastViewedAt: null,
        generatedAt: DateTime(2026, 6, 29),
      );

  Future<void> shoot(WidgetTester tester, String name, Widget child) async {
    await tester.pumpWidget(MaterialApp(
      debugShowCheckedModeBanner: false,
      home: Scaffold(
        backgroundColor: const Color(0xFF04040A),
        body: Padding(padding: const EdgeInsets.all(16), child: child),
      ),
    ));
    // Fixed pumps — let fonts + chart animation settle (no pumpAndSettle).
    for (var i = 0; i < 14; i++) {
      await tester.pump(const Duration(milliseconds: 120));
    }
    await binding.takeScreenshot(name);
  }

  testWidgets('render bar chart (#388 Phase 1)', (tester) async {
    await shoot(tester, 'viz-bar', VisualizationChart(viz: sampleViz('bar')));
  });

  testWidgets('render pie chart (#388 Phase 1)', (tester) async {
    await shoot(tester, 'viz-pie', VisualizationChart(viz: sampleViz('pie')));
  });

  testWidgets('render line chart (#388 Phase 1)', (tester) async {
    await shoot(tester, 'viz-line', VisualizationChart(viz: sampleViz('line')));
  });

  testWidgets('render model-authored SVG in message (#388 Phase 2)', (tester) async {
    const svg = '''
Here's how your spending splits this month:

```svg
<svg viewBox="0 0 360 200" xmlns="http://www.w3.org/2000/svg">
  <rect x="0" y="0" width="360" height="200" fill="#0A0A14"/>
  <rect x="20" y="60" width="60" height="120" fill="#7B61FF"/>
  <rect x="100" y="100" width="60" height="80" fill="#4ECDC4"/>
  <rect x="180" y="40" width="60" height="140" fill="#FF6B6B"/>
  <rect x="260" y="120" width="60" height="60" fill="#FFD93D"/>
  <text x="20" y="195" fill="#F0EFF4" font-size="12">Rent</text>
  <text x="100" y="195" fill="#F0EFF4" font-size="12">Food</text>
  <text x="180" y="195" fill="#F0EFF4" font-size="12">Bills</text>
  <text x="260" y="195" fill="#F0EFF4" font-size="12">Fun</text>
</svg>
```

The biggest slice is **Bills**.''';
    await shoot(
      tester,
      'viz-svg-message',
      AiRichMessage(
        content: svg,
        styleSheet: MarkdownStyleSheet(
          p: const TextStyle(color: Color(0xFFF0EFF4)),
        ),
      ),
    );
  });
}
