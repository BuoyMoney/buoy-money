import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// The app is English only (owner decision, 2026-10-12). A phone set to another language,
/// Spanish included, must still get the English strings rather than a missing-locale failure.
void main() {
  test('English is the only supported locale', () {
    expect(AppLocalizations.supportedLocales, [const Locale('en')]);
  });

  testWidgets('an unsupported device locale (es-MX) renders English', (tester) async {
    await tester.pumpWidget(MaterialApp(
      locale: const Locale('es', 'MX'),
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Builder(builder: (context) => Text(AppLocalizations.of(context).rulesCancel)),
    ));
    await tester.pumpAndSettle();
    expect(find.text('Cancel'), findsOneWidget);
    expect(Localizations.localeOf(tester.element(find.text('Cancel'))).languageCode, 'en');
  });
}
