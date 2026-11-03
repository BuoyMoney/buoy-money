import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/server_selection_service.dart';
import 'package:buoy_ai/services/server_version_service.dart';
import 'package:buoy_ai/widgets/server_version_gate.dart';

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

class _SpyAuth extends AuthProvider {
  int switchSignOuts = 0;

  @override
  void signOutForServerSwitch() {
    switchSignOuts++;
    super.signOutForServerSwitch();
  }
}

const _serverOld = VersionCheckResult(
  compatibility: ServerCompatibility.serverNeedsUpdate,
  serverVersion: '1.5.10',
  requiredServerVersion: '1.5.29',
  appVersion: '1.6.0',
);

/// "Use a different server" on the update screen: a phone pointed at an old self-hosted server
/// is not stuck there.
void main() {
  late List<String> calls;

  setUp(() {
    calls = [];
    SharedPreferences.setMockInitialValues({});
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    AppConfig.customServerOriginForTest = null;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  ServerSelectionService selection() => ServerSelectionService(
        dioFor: (origin) => Dio(),
        clearServerState: () async => calls.add('clear'),
        applyBaseUrl: () => calls.add('apply'),
      );

  Future<void> pumpGate(WidgetTester tester, _ScriptedService service, AuthProvider auth,
      {required bool custom}) async {
    await tester.pumpWidget(ChangeNotifierProvider<AuthProvider>.value(
      value: auth,
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: ServerVersionGate(
          service: service,
          selectionService: selection(),
          isCustomServer: () => custom && AppConfig.isCustomServer,
          child: const Text('the app'),
        ),
      ),
    ));
    await tester.pumpAndSettle();
  }

  testWidgets('custom server: switching back to Cloud signs out and brings the app back',
      (tester) async {
    AppConfig.customServerOriginForTest = 'https://old.example.ts.net';
    final auth = _SpyAuth();
    final service = _ScriptedService([_serverOld, VersionCheckResult.ok]);
    await pumpGate(tester, service, auth, custom: true);

    expect(find.text('Your server needs an update'), findsOneWidget);
    await tester.tap(find.byKey(const Key('versionGateChangeServer')));
    await tester.pumpAndSettle();

    // The picker opens on "My own server"; pick Cloud and connect.
    await tester.tap(find.byKey(const Key('serverOptionCloud')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('serverPickerConnect')));
    await tester.pumpAndSettle();

    expect(calls, ['clear', 'apply']);
    expect(AppConfig.isCustomServer, isFalse);
    expect(auth.switchSignOuts, 1);
    expect(auth.isAuthenticated, isFalse);
    expect(service.calls, 2, reason: 'the gate checks again after the switch');
    expect(find.text('the app'), findsOneWidget);
    expect(find.byKey(const Key('versionGateTitle')), findsNothing);
  });

  testWidgets('cancelling the picker leaves everything as it was', (tester) async {
    AppConfig.customServerOriginForTest = 'https://old.example.ts.net';
    final auth = _SpyAuth();
    final service = _ScriptedService([_serverOld, VersionCheckResult.ok]);
    await pumpGate(tester, service, auth, custom: true);

    await tester.tap(find.byKey(const Key('versionGateChangeServer')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(calls, isEmpty);
    expect(auth.switchSignOuts, 0);
    expect(service.calls, 1);
    expect(find.text('Your server needs an update'), findsOneWidget);
  });

  testWidgets('build default server: no "Use a different server" button', (tester) async {
    await pumpGate(tester, _ScriptedService([_serverOld]), _SpyAuth(), custom: false);
    expect(find.text('Your server needs an update'), findsOneWidget);
    expect(find.byKey(const Key('versionGateChangeServer')), findsNothing);
  });
}
