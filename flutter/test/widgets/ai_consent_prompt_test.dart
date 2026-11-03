import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/widgets/ai_consent_sheet.dart';

class _FakeAuthProvider extends AuthProvider {
  final User _user;
  _FakeAuthProvider(this._user);

  @override
  User? get user => _user;

  @override
  AuthStatus get status => AuthStatus.authenticated;

  @override
  bool get isAuthenticated => true;
}

User _user({DateTime? granted, DateTime? withdrawn}) => User(
      id: '7',
      email: 'u@example.com',
      firstName: 'Test',
      lastName: 'User',
      subscriptionTier: 'Standard',
      createdAt: DateTime(2026, 1, 1),
      aiConsentGrantedAt: granted,
      aiConsentWithdrawnAt: withdrawn,
    );

/// Hosts a button that runs the app-level ask, like the dashboard does after landing.
Widget _host(User user) => ChangeNotifierProvider<AuthProvider>.value(
      value: _FakeAuthProvider(user),
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (ctx) => ElevatedButton(
              onPressed: () => maybeShowAiConsentPrompt(ctx),
              child: const Text('land'),
            ),
          ),
        ),
      ),
    );

Future<void> _land(WidgetTester tester) async {
  await tester.tap(find.text('land'));
  await tester.pumpAndSettle();
}

void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));

  testWidgets('asks an undecided user once after landing, on every plan', (tester) async {
    await tester.pumpWidget(_host(_user()));
    await _land(tester);
    expect(find.text('Turn on AI features?'), findsOneWidget);
  });

  testWidgets('does not ask again within 14 days after "Not now"', (tester) async {
    await tester.pumpWidget(_host(_user()));
    await _land(tester);
    await tester.ensureVisible(find.text('Not now'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Not now'));
    await tester.pumpAndSettle();

    await _land(tester);
    expect(find.text('Turn on AI features?'), findsNothing);
  });

  testWidgets('asks again once 14 days have passed', (tester) async {
    final old = DateTime.now().subtract(const Duration(days: 15)).millisecondsSinceEpoch;
    SharedPreferences.setMockInitialValues({'ai_consent_prompt_last_asked:7': old});
    await tester.pumpWidget(_host(_user()));
    await _land(tester);
    expect(find.text('Turn on AI features?'), findsOneWidget);
  });

  testWidgets('never re-asks someone who turned AI off in Settings', (tester) async {
    await tester.pumpWidget(_host(_user(withdrawn: DateTime(2026, 9, 1))));
    await _land(tester);
    expect(find.text('Turn on AI features?'), findsNothing);
  });

  testWidgets('does not ask someone who already agreed', (tester) async {
    await tester.pumpWidget(_host(_user(granted: DateTime(2026, 9, 26))));
    await _land(tester);
    expect(find.text('Turn on AI features?'), findsNothing);
  });
}
