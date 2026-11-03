import 'package:buoy_ai/widgets/ai_sparkle.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  Widget wrap(Widget child) => MaterialApp(
        home: Scaffold(
          body: DefaultTextStyle(
            style: const TextStyle(color: Color(0xFF000000)),
            child: child,
          ),
        ),
      );

  group('AiHighlightedText', () {
    testWidgets('renders plain text when no matches', (tester) async {
      await tester.pumpWidget(wrap(AiHighlightedText(
        text: 'Hello there friend',
        baseStyle: const TextStyle(fontSize: 12),
      )));

      expect(find.textContaining('Hello there friend'), findsOneWidget);
    });

    testWidgets('non-premium mode renders without ShaderMask', (tester) async {
      await tester.pumpWidget(wrap(AiHighlightedText(
        text: 'You spent \$123.45 today',
        baseStyle: const TextStyle(fontSize: 12),
      )));

      // Non-premium path uses TextSpans only — no ShaderMask widgets.
      expect(find.byType(ShaderMask), findsNothing);
      expect(find.byType(Text), findsOneWidget);
    });

    testWidgets('premium flag triggers ShaderMask for highlighted spans',
        (tester) async {
      await tester.pumpWidget(wrap(AiHighlightedText(
        text: 'You saved \$500 on subscriptions (15%)',
        baseStyle: const TextStyle(fontSize: 12),
        forcePremium: true,
      )));

      // Four matches: "saved", "$500", "subscriptions", "15%" → each
      // wrapped in a ShaderMask for the gradient treatment.
      expect(find.byType(ShaderMask), findsNWidgets(4));
    });

    testWidgets('multiple non-overlapping matches each wrap once',
        (tester) async {
      await tester.pumpWidget(wrap(AiHighlightedText(
        text: 'savings up 20%',
        baseStyle: const TextStyle(fontSize: 12),
        forcePremium: true,
      )));

      // "savings" (keyword) + "20%" (numeric) → 2 ShaderMasks, one per match.
      expect(find.byType(ShaderMask), findsNWidgets(2));
    });
  });

  group('AiBadge', () {
    testWidgets('renders gradient icon + label when forcePremium',
        (tester) async {
      await tester.pumpWidget(wrap(
        const AiBadge(confidence: 0.9, forcePremium: true),
      ));

      // Icon + confidence label both masked.
      expect(find.byType(ShaderMask), findsNWidgets(2));
    });

    testWidgets('renders without confidence label when null', (tester) async {
      await tester.pumpWidget(wrap(
        const AiBadge(forcePremium: true),
      ));

      // Only the icon is masked; no confidence text to mask.
      expect(find.byType(ShaderMask), findsOneWidget);
    });
  });
}
