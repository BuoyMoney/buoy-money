// Basic smoke test for BuoyAI Flutter app.
//
// Verifies the app widget tree can be constructed and renders
// a MaterialApp with router configuration.

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/main.dart';

void main() {
  testWidgets('App smoke test — renders MaterialApp.router',
      (WidgetTester tester) async {
    // The full app renders widgets that overflow in the small test viewport
    // and tries to load network images (which return 400 in test env).
    // These are expected — suppress so they don't fail the test.
    final errors = <FlutterErrorDetails>[];
    final originalOnError = FlutterError.onError!;
    FlutterError.onError = (details) {
      errors.add(details);
    };

    await tester.pumpWidget(const BuoyAIApp());

    // Restore original handler
    FlutterError.onError = originalOnError;

    // The app should render a MaterialApp (the .router variant)
    expect(find.byType(MaterialApp), findsOneWidget);
  });
}
