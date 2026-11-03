import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/email_preferences_page.dart';
import 'package:buoy_ai/services/email_preferences_service.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

class _FakeEmailPrefs extends EmailPreferencesService {
  _FakeEmailPrefs({this.fail = false});

  final bool fail;
  final updates = <(String, bool)>[];

  @override
  Future<EmailPreferences> getPreferences() async {
    if (fail) throw Exception('500');
    return EmailPreferences();
  }

  @override
  Future<EmailPreferences> updateSinglePreference(
      String key, bool value) async {
    updates.add((key, value));
    return EmailPreferences(
        featureAnnouncements: key == 'featureAnnouncements' ? value : false);
  }
}

Widget _wrap(EmailPreferencesService service) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: EmailPreferencesPage(service: service),
    );

void main() {
  testWidgets('lists the preference groups with their switches',
      (tester) async {
    await tester.pumpWidget(_wrap(_FakeEmailPrefs()));
    await tester.pumpAndSettle();

    expect(find.text('Email Preferences'), findsOneWidget);
    expect(find.text('Account Activity'), findsOneWidget);
    expect(find.text('Connection Issues'), findsOneWidget);
    expect(find.byType(Switch), findsWidgets);
  });

  testWidgets('tapping a row saves the flipped value', (tester) async {
    final service = _FakeEmailPrefs();
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.scrollUntilVisible(find.text('Feature Announcements'), 300,
        scrollable: find.byType(Scrollable).first);
    await tester.ensureVisible(find.text('Feature Announcements'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Feature Announcements'));
    await tester.pumpAndSettle();

    expect(service.updates, [('featureAnnouncements', true)]);
  });

  testWidgets('each switch is announced together with its preference name',
      (tester) async {
    final handle = tester.ensureSemantics();
    await tester.pumpWidget(_wrap(_FakeEmailPrefs()));
    await tester.pumpAndSettle();

    await tester.scrollUntilVisible(find.text('Feature Announcements'), 300,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    final row = find.ancestor(
        of: find.text('Feature Announcements'),
        matching: find.byType(MergeSemantics));
    expect(row, findsOneWidget);
    final semantics = tester.getSemantics(row);
    expect(semantics.label, contains('Feature Announcements'));
    expect(
        semantics.getSemanticsData().toString(), contains('hasToggledState'));
    handle.dispose();
  });

  testWidgets('a failed load offers a retry', (tester) async {
    await tester.pumpWidget(_wrap(_FakeEmailPrefs(fail: true)));
    await tester.pumpAndSettle();

    expect(find.text('Failed to load preferences'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
  });
}
