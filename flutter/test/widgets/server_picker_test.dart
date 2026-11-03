import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/public/login_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/server_selection_service.dart';
import 'package:buoy_ai/widgets/google_auth_button.dart';
import 'package:buoy_ai/widgets/server_picker.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter({this.body = '', this.fail = false});

  final String body;
  final bool fail;

  @override
  Future<ResponseBody> fetch(
      RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (fail) throw DioException.connectionError(requestOptions: options, reason: 'offline');
    return ResponseBody.fromString(body, 200, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

/// The "Server: ..." control on the sign-in page and the chooser behind it.
void main() {
  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    SharedPreferences.setMockInitialValues({});
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  tearDown(() {
    AppConfig.customServerOriginForTest = null;
    AppConfig.sameOriginBuildForTest = null;
    AppConfig.webBuildForTest = null;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
  });

  // The test font is wider than the real one, so a row overflows; that is not what these tests
  // are about.
  Future<void> settle(WidgetTester tester) async {
    for (var i = 0; i < 6; i++) {
      await tester.pump(const Duration(milliseconds: 50));
      while (tester.takeException() != null) {}
    }
  }

  Future<void> pumpLogin(WidgetTester tester) async {
    tester.view.physicalSize = const Size(900, 3000);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ChangeNotifierProvider<AuthProvider>(
        create: (_) => AuthProvider(),
        child: MaterialApp(
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const LoginPage(),
        ),
      ),
    );
    await settle(tester);
  }

  testWidgets('hosted default: shows "Server: BuoyAI Cloud" and the Google button', (tester) async {
    await pumpLogin(tester);

    expect(find.byKey(const Key('serverPickerControl')), findsOneWidget);
    expect(find.text('Server: BuoyAI Cloud'), findsOneWidget);
    expect(find.byType(GoogleAuthButton), findsOneWidget);
    expect(ServerEdition.showPasskeys, isTrue);
  });

  testWidgets('custom self-hosted server: shows its host and hides Google and Apple',
      (tester) async {
    AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
    // Google "on" but no client id of its own: the build's client would be refused.
    ServerEdition.adopt(ServerCapabilities.fromJson({
      'edition': 'selfhost',
      'features': {'googleSignIn': true},
    }));

    await pumpLogin(tester);

    expect(find.text('Server: buoy.example.ts.net'), findsOneWidget);
    expect(find.byType(GoogleAuthButton), findsNothing);
    expect(ServerEdition.showAppleSignIn, isFalse);
    // Passkeys are off on a phone talking to a server the user picked.
    expect(ServerEdition.showPasskeys, isFalse);
  });

  testWidgets('custom self-hosted server with its own Google client keeps Google', (tester) async {
    AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
    ServerEdition.adopt(ServerCapabilities.fromJson({
      'edition': 'selfhost',
      'features': {'googleSignIn': true},
      'googleClientId': 'operator.apps.googleusercontent.com',
    }));

    await pumpLogin(tester);

    expect(find.byType(GoogleAuthButton), findsOneWidget);
  });

  testWidgets('any web build (hosted too): no server control, and a saved server is ignored',
      (tester) async {
    AppConfig.webBuildForTest = true;
    AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
    await pumpLogin(tester);
    expect(find.byKey(const Key('serverPickerControl')), findsNothing);
    expect(AppConfig.isCustomServer, isFalse);
    expect(AppConfig.baseUrl, AppConfig.defaultBaseUrl);
  });

  testWidgets('self-host web build: no server control at all', (tester) async {
    AppConfig.sameOriginBuildForTest = true;
    await pumpLogin(tester);
    expect(find.byKey(const Key('serverPickerControl')), findsNothing);
  });

  group('chooser', () {
    late List<String> calls;

    ServerSelectionService service(_StubAdapter adapter) => ServerSelectionService(
          dioFor: (origin) =>
              Dio(BaseOptions(baseUrl: '$origin/api'))..httpClientAdapter = adapter,
          clearServerState: () async => calls.add('clear'),
          applyBaseUrl: () => calls.add('apply'),
          appVersion: '1.6.0',
          minServerVersion: '1.5.0',
        );

    Future<void> pumpControl(WidgetTester tester, ServerSelectionService svc,
        {VoidCallback? onChanged}) async {
      await tester.pumpWidget(MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
            body: Center(child: ServerPickerControl(service: svc, onChanged: onChanged))),
      ));
    }

    Future<void> openOwn(WidgetTester tester) async {
      await tester.tap(find.byKey(const Key('serverPickerChange')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('serverOptionOwn')));
      await tester.pumpAndSettle();
    }

    setUp(() => calls = []);

    testWidgets('an unreachable server shows an error and changes nothing', (tester) async {
      await pumpControl(tester, service(_StubAdapter(fail: true)));
      await openOwn(tester);
      await tester.enterText(find.byKey(const Key('serverAddressField')), 'buoy.example.ts.net');
      await tester.tap(find.byKey(const Key('serverPickerConnect')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('serverPickerError')), findsOneWidget);
      expect(find.textContaining('Could not reach that server'), findsOneWidget);
      expect(calls, isEmpty);
      expect(AppConfig.isCustomServer, isFalse);
    });

    testWidgets('http shows the unencrypted warning', (tester) async {
      await pumpControl(tester, service(_StubAdapter(fail: true)));
      await openOwn(tester);

      await tester.enterText(
          find.byKey(const Key('serverAddressField')), 'http://192.168.1.20:8080');
      await tester.pump();
      expect(find.byKey(const Key('serverHttpWarning')), findsOneWidget);

      await tester.enterText(
          find.byKey(const Key('serverAddressField')), 'https://buoy.example.ts.net');
      await tester.pump();
      expect(find.byKey(const Key('serverHttpWarning')), findsNothing);
    });

    testWidgets('a good server is saved, capabilities published, label updated', (tester) async {
      var changed = 0;
      final body = jsonEncode({
        'version': '1.5.35',
        'minClientVersion': '1.0.0',
        'edition': 'selfhost',
        'features': {'googleSignIn': false},
      });
      await pumpControl(tester, service(_StubAdapter(body: body)), onChanged: () => changed++);
      await openOwn(tester);
      await tester.enterText(
          find.byKey(const Key('serverAddressField')), 'buoy.example.ts.net/api');
      await tester.tap(find.byKey(const Key('serverPickerConnect')));
      await tester.pumpAndSettle();

      expect(calls, ['clear', 'apply']);
      expect(changed, 1);
      expect(AppConfig.customServerOrigin, 'https://buoy.example.ts.net');
      expect(ServerEdition.current.value.isSelfHost, isTrue);
      expect(ServerEdition.current.value.hasBilling, isFalse);
      expect(ServerEdition.current.value.hasBankConnection, isFalse);
      expect(find.text('Server: buoy.example.ts.net'), findsOneWidget);
    });
  });
}
