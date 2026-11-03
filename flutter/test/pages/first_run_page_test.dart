import 'dart:convert';
import 'dart:typed_data';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/first_run.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/public/first_run_page.dart';
import 'package:buoy_ai/services/registration_service.dart';
import 'package:buoy_ai/services/server_selection_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:shared_preferences/shared_preferences.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter({this.body = '', this.fail = false});

  final String body;
  final bool fail;
  final asked = <String>[];

  @override
  Future<ResponseBody> fetch(
      RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    asked.add(options.uri.toString());
    if (fail) throw DioException.connectionError(requestOptions: options, reason: 'offline');
    return ResponseBody.fromString(body, 200, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

final _selfHostVersion = jsonEncode({
  'version': '1.5.36',
  'minClientVersion': '1.0.0',
  'edition': 'selfhost',
  'features': {'googleSignIn': false},
});

/// "How would you like to use Buoy Money?" on a fresh install of the phone app.
void main() {
  late List<String> calls;
  late int hosted;
  late int own;
  late bool? ownFirstAccount;
  RegistrationStatus? status;
  bool statusThrows = false;

  ServerSelectionService service(_StubAdapter adapter) => ServerSelectionService(
        dioFor: (origin) => Dio(BaseOptions(baseUrl: '$origin/api'))..httpClientAdapter = adapter,
        clearServerState: () async => calls.add('clear'),
        applyBaseUrl: () => calls.add('apply'),
        appVersion: '1.6.0',
        minServerVersion: '1.5.0',
      );

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    SharedPreferences.setMockInitialValues({});
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
    FirstRun.doneForTest = false;
    calls = [];
    hosted = 0;
    own = 0;
    ownFirstAccount = null;
    status = null;
    statusThrows = false;
  });

  tearDown(() {
    AppConfig.customServerOriginForTest = null;
    ServerEdition.current.value = ServerCapabilities.hosted;
    ServerEdition.resetHandshake();
    FirstRun.doneForTest = false;
  });

  Future<void> pump(WidgetTester tester, _StubAdapter adapter) async {
    tester.view.physicalSize = const Size(420, 900);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: FirstRunPage(
        service: service(adapter),
        animateBackground: false,
        onHosted: () => hosted++,
        onOwnServer: (firstAccount) {
          own++;
          ownFirstAccount = firstAccount;
        },
        registrationLoader: () async {
          if (statusThrows) throw Exception('boom');
          return status;
        },
      ),
    ));
    await tester.pumpAndSettle();
  }

  Future<void> openOwn(WidgetTester tester) async {
    await tester.tap(find.byKey(const ValueKey('first-run-own')));
    await tester.pumpAndSettle();
  }

  testWidgets('asks the question with the two choices', (tester) async {
    await pump(tester, _StubAdapter());

    expect(find.text('How would you like to use Buoy Money?'), findsOneWidget);
    expect(find.text('Hosted by Buoy Money'), findsOneWidget);
    expect(find.text('We run it for you. Sign in from any device.'), findsOneWidget);
    expect(find.text('On my own server'), findsOneWidget);
    expect(find.text('Your budget stays on a computer you control.'), findsOneWidget);
    expect(find.byKey(const ValueKey('first-run-address')), findsNothing);
  });

  testWidgets('the headline uses the app serif over the app background', (tester) async {
    await pump(tester, _StubAdapter());

    final headline = tester.widget<Text>(find.text('How would you like to use Buoy Money?'));
    expect(headline.style?.fontFamily, contains('InstrumentSerif'));
    final scaffold = tester.widget<Scaffold>(find.byType(Scaffold));
    expect(scaffold.backgroundColor, const Color(0xFF04040A));
  });

  testWidgets('hosted goes to sign-up, keeps the hosted server and is not asked again', (tester) async {
    await pump(tester, _StubAdapter());

    await tester.tap(find.byKey(const ValueKey('first-run-hosted')));
    await tester.pumpAndSettle();

    expect(hosted, 1);
    expect(own, 0);
    expect(calls, isEmpty, reason: 'nothing to switch: the build already talks to the hosted service');
    expect(AppConfig.isCustomServer, isFalse);
    expect(FirstRun.done, isTrue);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getBool(FirstRun.prefKey), isTrue);
  });

  testWidgets('own server: a good address is checked, switched to, then sign-in', (tester) async {
    final adapter = _StubAdapter(body: _selfHostVersion);
    await pump(tester, adapter);
    await openOwn(tester);

    expect(find.text('Connect to your server'), findsOneWidget);
    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'buoy.example.ts.net/app');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(adapter.asked, ['https://buoy.example.ts.net/api/system/version']);
    expect(calls, ['clear', 'apply']);
    expect(AppConfig.customServerOrigin, 'https://buoy.example.ts.net');
    expect(ServerEdition.current.value.isSelfHost, isTrue);
    expect(own, 1);
    expect(hosted, 0);
    expect(FirstRun.done, isTrue);
    expect(ownFirstAccount, isFalse, reason: 'no sign-up status, so treat it as an existing server: sign-in');
  });

  testWidgets('own server: a brand-new server (no account yet) lands on sign-up', (tester) async {
    status = const RegistrationStatus(firstAccount: true, open: false, requiresInvite: false);
    await pump(tester, _StubAdapter(body: _selfHostVersion));
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'buoy.example.ts.net');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(own, 1);
    expect(ownFirstAccount, isTrue);
  });

  testWidgets('own server: a server that already has accounts lands on sign-in', (tester) async {
    status = const RegistrationStatus(firstAccount: false, open: false, requiresInvite: true);
    await pump(tester, _StubAdapter(body: _selfHostVersion));
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'buoy.example.ts.net');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(own, 1);
    expect(ownFirstAccount, isFalse);
  });

  testWidgets('own server: a failing status lookup falls back to sign-in', (tester) async {
    statusThrows = true;
    await pump(tester, _StubAdapter(body: _selfHostVersion));
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'buoy.example.ts.net');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(own, 1);
    expect(ownFirstAccount, isFalse);
  });

  testWidgets('own server: an unreachable address says so and changes nothing', (tester) async {
    await pump(tester, _StubAdapter(fail: true));
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'buoy.example.ts.net');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('first-run-error')), findsOneWidget);
    expect(find.textContaining('Could not reach that server'), findsOneWidget);
    expect(calls, isEmpty);
    expect(AppConfig.isCustomServer, isFalse);
    expect(own, 0);
    expect(FirstRun.done, isFalse, reason: 'still unanswered, so it is asked again next launch');
  });

  testWidgets('own server: something that is not a Buoy Money server is turned down', (tester) async {
    await pump(tester, _StubAdapter(body: jsonEncode({'hello': 'world'})));
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'example.com');
    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(find.text('That address answered, but it is not a Buoy Money server.'), findsOneWidget);
    expect(calls, isEmpty);
  });

  testWidgets('own server: an empty address asks for one', (tester) async {
    await pump(tester, _StubAdapter());
    await openOwn(tester);

    await tester.tap(find.byKey(const ValueKey('first-run-connect')));
    await tester.pumpAndSettle();

    expect(find.textContaining('Enter a server address'), findsOneWidget);
  });

  testWidgets('own server: http warns that the password travels unencrypted', (tester) async {
    await pump(tester, _StubAdapter());
    await openOwn(tester);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'http://192.168.1.20:8080');
    await tester.pump();
    expect(find.byKey(const ValueKey('first-run-http-warning')), findsOneWidget);

    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'https://buoy.example.ts.net');
    await tester.pump();
    expect(find.byKey(const ValueKey('first-run-http-warning')), findsNothing);
  });

  testWidgets('fits a small phone with no overflow, on both steps', (tester) async {
    await pump(tester, _StubAdapter());
    tester.view.physicalSize = const Size(320, 568);
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);

    // The page scrolls on a short screen.
    await tester.ensureVisible(find.byKey(const ValueKey('first-run-own')));
    await openOwn(tester);
    await tester.enterText(find.byKey(const ValueKey('first-run-address')), 'http://192.168.1.20:8080');
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets('back returns to the two choices', (tester) async {
    await pump(tester, _StubAdapter());
    await openOwn(tester);

    await tester.tap(find.byKey(const ValueKey('first-run-back')));
    await tester.pumpAndSettle();

    expect(find.byKey(const ValueKey('first-run-hosted')), findsOneWidget);
    expect(find.byKey(const ValueKey('first-run-address')), findsNothing);
  });
}
