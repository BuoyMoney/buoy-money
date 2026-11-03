import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/widgets/always_categorize_offer.dart';

void main() {
  Future<bool?> Function() pumpOffer(WidgetTester tester) {
    bool? result;
    var opened = false;
    return () async {
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Builder(
          builder: (context) {
            if (!opened) {
              opened = true;
              WidgetsBinding.instance.addPostFrameCallback((_) async {
                result = await showAlwaysCategorizeOffer(
                  context,
                  merchant: 'Dillons',
                  category: 'Groceries',
                );
              });
            }
            return const Scaffold();
          },
        ),
      ));
      await tester.pumpAndSettle();
      return result;
    };
  }

  testWidgets('names the merchant and category in the question', (tester) async {
    await pumpOffer(tester)();

    expect(find.text('Always file Dillons as Groceries?'), findsOneWidget);
    expect(find.text('Always'), findsOneWidget);
    expect(find.text('Just this one'), findsOneWidget);
  });

  testWidgets('"Always" resolves true', (tester) async {
    bool? result;
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () async {
              result = await showAlwaysCategorizeOffer(context,
                  merchant: 'Dillons', category: 'Groceries');
            },
            child: const Text('open'),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Always'));
    await tester.pumpAndSettle();

    expect(result, isTrue);
    expect(find.text('Always file Dillons as Groceries?'), findsNothing);
  });

  testWidgets('"Just this one" resolves false', (tester) async {
    bool? result;
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Builder(
        builder: (context) => Scaffold(
          body: TextButton(
            onPressed: () async {
              result = await showAlwaysCategorizeOffer(context,
                  merchant: 'Dillons', category: 'Groceries');
            },
            child: const Text('open'),
          ),
        ),
      ),
    ));
    await tester.tap(find.text('open'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Just this one'));
    await tester.pumpAndSettle();

    expect(result, isFalse);
  });
}
