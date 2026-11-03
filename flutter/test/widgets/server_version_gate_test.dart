import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/server_version_service.dart';
import 'package:buoy_ai/widgets/server_version_gate.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

/// Returns scripted answers, one per call.
class _ScriptedService extends ServerVersionService {
  _ScriptedService(this.answers) : super(appVersion: '1.6.0', minServerVersion: '1.5.29');

  final List<VersionCheckResult> answers;
  int calls = 0;

  @override
  Future<VersionCheckResult> check() async {
    final i = calls < answers.length ? calls : answers.length - 1;
    calls++;
    return answers[i];
  }
}

Widget _wrap(Widget child) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: child,
    );

const _serverOld = VersionCheckResult(
  compatibility: ServerCompatibility.serverNeedsUpdate,
  serverVersion: '1.5.10',
  requiredServerVersion: '1.5.29',
  appVersion: '1.6.0',
);

const _appOld = VersionCheckResult(
  compatibility: ServerCompatibility.appNeedsUpdate,
  serverVersion: '2.0.0',
  requiredAppVersion: '1.9.0',
  appVersion: '1.6.0',
);

void main() {
  testWidgets('shows the app straight away and keeps it when the versions agree', (tester) async {
    final service = _ScriptedService([VersionCheckResult.ok]);
    await tester.pumpWidget(_wrap(ServerVersionGate(service: service, child: const Text('the app'))));
    expect(find.text('the app'), findsOneWidget, reason: 'the check must not hold the app back');

    await tester.pumpAndSettle();

    expect(find.text('the app'), findsOneWidget);
    expect(find.byKey(const Key('versionGateTitle')), findsNothing);
    expect(service.calls, 1);
  });

  testWidgets('replaces the app with "your server needs an update" and names both versions', (tester) async {
    await tester.pumpWidget(_wrap(ServerVersionGate(service: _ScriptedService([_serverOld]), child: const Text('the app'))));
    await tester.pumpAndSettle();

    expect(find.text('the app'), findsNothing);
    expect(find.text('Your server needs an update'), findsOneWidget);
    final body = tester.widget<Text>(find.byKey(const Key('versionGateBody'))).data!;
    expect(body, contains('1.5.29'));
    expect(body, contains('1.5.10'));
    expect(find.textContaining('docker compose pull'), findsOneWidget);
  });

  testWidgets('shows "this app needs an update" when the server dropped support for this app', (tester) async {
    await tester.pumpWidget(_wrap(ServerVersionGate(service: _ScriptedService([_appOld]), child: const Text('the app'))));
    await tester.pumpAndSettle();

    expect(find.text('This app needs an update'), findsOneWidget);
    final body = tester.widget<Text>(find.byKey(const Key('versionGateBody'))).data!;
    expect(body, contains('1.9.0'));
    expect(find.textContaining('docker compose pull'), findsNothing, reason: 'the owner cannot fix an old app by updating the server');
  });

  testWidgets('Check again returns to the app once the server has been updated', (tester) async {
    final service = _ScriptedService([_serverOld, VersionCheckResult.ok]);
    await tester.pumpWidget(_wrap(ServerVersionGate(service: service, child: const Text('the app'))));
    await tester.pumpAndSettle();
    expect(find.text('Your server needs an update'), findsOneWidget);

    await tester.tap(find.byKey(const Key('versionGateRetry')));
    await tester.pumpAndSettle();

    expect(find.text('the app'), findsOneWidget);
    expect(service.calls, 2);
  });
}
