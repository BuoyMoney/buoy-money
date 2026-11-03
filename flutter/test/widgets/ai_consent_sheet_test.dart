import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/ai_consent_sheet.dart';

Widget _host(AiConsentGrant grant, void Function(bool) onResult) {
  return MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(
      body: Builder(
        builder: (ctx) => Center(
          child: ElevatedButton(
            onPressed: () async =>
                onResult(await showAiConsentSheet(ctx, grant: grant)),
            child: const Text('open'),
          ),
        ),
      ),
    ),
  );
}

void main() {
  testWidgets('names the recipient, what is sent, and where to turn it off',
      (tester) async {
    await tester.pumpWidget(_host((_) async => null, (_) {}));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    expect(find.text('Turn on AI features?'), findsOneWidget);
    expect(find.textContaining('Gemini'), findsWidgets);
    expect(find.textContaining('transactions'), findsWidgets);
    expect(find.textContaining('Settings > AI Assistant'), findsOneWidget);
    // Apple 5.1.1(i)/5.1.2(i): every category that reaches Gemini, the background use, and
    // what never leaves.
    expect(find.textContaining('account holder names'), findsOneWidget);
    expect(find.textContaining('memos'), findsOneWidget);
    expect(find.textContaining('after each bank sync'), findsOneWidget);
    expect(find.textContaining('what you type or attach in chat'), findsOneWidget);
    expect(find.textContaining('Never sent'), findsOneWidget);
    expect(find.text('Privacy Policy'), findsOneWidget);
  });

  testWidgets('accept calls grant(true) and returns true', (tester) async {
    final calls = <bool>[];
    bool? result;
    await tester.pumpWidget(_host((g) async {
      calls.add(g);
      return DateTime(2026, 8, 22);
    }, (r) => result = r));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // The sheet lists every data category now, so it scrolls; bring the button into view.
    await tester.ensureVisible(find.text('Turn on AI features'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Turn on AI features'));
    await tester.pumpAndSettle();

    expect(calls, [true]);
    expect(result, isTrue);
  });

  testWidgets('Not now returns false without calling grant', (tester) async {
    final calls = <bool>[];
    bool? result;
    await tester.pumpWidget(_host((g) async {
      calls.add(g);
      return null;
    }, (r) => result = r));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // The sheet lists every data category now, so it scrolls; bring the button into view.
    await tester.ensureVisible(find.text('Not now'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Not now'));
    await tester.pumpAndSettle();

    expect(calls, isEmpty);
    expect(result, isFalse);
  });

  testWidgets('a failed grant keeps the sheet open', (tester) async {
    bool? result;
    await tester.pumpWidget(_host((_) async => throw Exception('offline'),
        (r) => result = r));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    // The sheet lists every data category now, so it scrolls; bring the button into view.
    await tester.ensureVisible(find.text('Turn on AI features'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Turn on AI features'));
    await tester.pumpAndSettle();

    expect(result, isNull);
    expect(find.text('Turn on AI features?'), findsOneWidget);
  });
}
