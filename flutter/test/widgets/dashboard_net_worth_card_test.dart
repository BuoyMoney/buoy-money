import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/utils/formatters.dart';
import 'package:buoy_ai/widgets/dashboard/stat_card.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

const _expense = Color(0xFFF87171);
const _textGhost = Color(0xFFF0EFF4);

Future<void> _pump(WidgetTester tester, double netWorth,
    {List<String> excludedCurrencyCodes = const []}) async {
  GoogleFonts.config.allowRuntimeFetching = false;
  await tester.pumpWidget(MaterialApp(
    // The card reads its subtitle from AppLocalizations (gen_l10n, #941).
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(
      body: Center(
        child: SizedBox(
          width: 380,
          child: DashboardNetWorthCard(
            netWorth: netWorth,
            excludedCurrencyCodes: excludedCurrencyCodes,
          ),
        ),
      ),
    ),
  ));
  await tester.pump();
}

Text _valueText(WidgetTester tester) => tester.widget<Text>(
      find.descendant(of: find.byType(FittedBox), matching: find.byType(Text)),
    );

void main() {
  testWidgets('renders the label, subtitle, and positive value (neutral color)',
      (tester) async {
    await _pump(tester, 4200.50);

    expect(find.text('Net Worth'), findsOneWidget);
    expect(find.text('Total cash minus what you owe'), findsOneWidget);
    // Positive: no leading minus, formatted like the rest of the app.
    expect(find.text(formatCurrency(4200.50)), findsOneWidget);
    expect(_valueText(tester).style!.color, _textGhost);
  });

  testWidgets('negative net worth is flagged red with a leading minus', (tester) async {
    await _pump(tester, -1500.0);

    expect(find.text('-${formatCurrency(1500.0)}'), findsOneWidget);
    expect(_valueText(tester).style!.color, _expense);
  });

  testWidgets('does NOT show any income/spending or change chip (that is the cash-flow card)',
      (tester) async {
    await _pump(tester, 1000.0);

    // No flow language leaks into the position hero.
    expect(find.textContaining('Income'), findsNothing);
    expect(find.textContaining('Spending'), findsNothing);
    expect(find.textContaining('%'), findsNothing);
  });

  testWidgets('names excluded foreign currencies in the subtitle (#941)',
      (tester) async {
    await _pump(tester, 1000.0, excludedCurrencyCodes: ['CAD', 'EUR']);

    expect(
      find.text('Total cash minus what you owe · excludes CAD, EUR accounts'),
      findsOneWidget,
    );
  });
}
