import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:provider/provider.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/bank_sources.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/accounts_page.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/api_client.dart';

class _Routes implements HttpClientAdapter {
  final Map<String, ({int status, Object? body})> routes;
  final calls = <String>[];
  _Routes(this.routes);

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    final key = '${options.method} ${options.path}';
    calls.add(key);
    final route = routes[key];
    return ResponseBody.fromString(
      route == null ? '' : jsonEncode(route.body),
      route?.status ?? 404,
      headers: {Headers.contentTypeHeader: [Headers.jsonContentType]},
    );
  }

  @override
  void close({bool force = false}) {}
}

const _plaidConnection = {
  'id': 4,
  'institutionName': 'Credit Union',
  'provider': 'Plaid',
  'isActive': true,
  'lastSyncAt': '2026-10-13T12:00:00Z',
  'accounts': [
    {'id': 10, 'name': 'Checking', 'type': 0, 'subtype': 'checking', 'currentBalance': 12.5},
  ],
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
    // The page listens to Plaid Link's event stream; there is no native side in a test.
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.flutter.io/plaid_flutter/events'),
      (call) async => null,
    );
    ServerEdition.current.value = ServerCapabilities.hosted;
  });

  Future<void> pumpPage(WidgetTester tester, Map<String, ({int status, Object? body})> routes) async {
    apiClient.dio.httpClientAdapter = _Routes({
      'GET /bank/connections': (status: 200, body: [_plaidConnection]),
      'GET /bank/providers': (status: 200, body: ['Plaid']),
      'GET /twofactor/status': (status: 200, body: {}),
      ...routes,
    });
    tester.view.physicalSize = const Size(1280, 1600);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      ChangeNotifierProvider<AuthProvider>(
        create: (_) => AuthProvider(),
        child: MaterialApp(
          key: UniqueKey(), // a fresh navigator and page for each scenario
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(body: AccountsPage()),
        ),
      ),
    );
    // Dio answers on the real event loop, so let it run between frames until the page has its data.
    for (var i = 0; i < 20 && find.text('Loading accounts…').evaluate().isNotEmpty; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pump(const Duration(milliseconds: 100));
    }
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
    await tester.pump(const Duration(seconds: 1));
  }

  // One test, three scenarios: ApiClient keeps a storage-migration future for the life of the process, and one
  // created under a finished test's fake clock never completes in the next test.
  testWidgets('the Accounts page for a family-less user, hosted, and self-host', (tester) async {
    // 1. A user who is not in a family: the server answers 400 on /family/members. That is no failure.
    await pumpPage(tester, {'GET /family/members': (status: 400, body: {'message': 'You are not in a family'})});
    expect(tester.takeException(), isNull);
    expect(find.text('Credit Union'), findsOneWidget);
    expect(find.text('Failed to load accounts'), findsNothing);

    // 2. Hosted with only Plaid: Connect Another Bank opens the Plaid sheet directly, as it always did.
    await tester.tap(find.text('Connect Another Bank'));
    await tester.pump(const Duration(seconds: 1));
    expect(tester.takeException(), isNull);
    expect(find.text('Connect Your Bank'), findsOneWidget);
    expect(find.byType(AddBankChooserSheet), findsNothing);

    // 3. Self-host: the page loads from the neutral routes alone and Add a bank opens the chooser.
    ServerEdition.current.value = const ServerCapabilities(edition: 'selfhost');
    addTearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);
    await pumpPage(tester, {
      'GET /family/members': (status: 400, body: {'message': 'You are not in a family'}),
      'GET /bank/providers': (status: 200, body: ['SimpleFin']),
    });
    expect(find.text('Failed to load accounts'), findsNothing);
    expect((apiClient.dio.httpClientAdapter as _Routes).calls.where((c) => c.contains('/plaid/')), isEmpty);

    // Sync has nothing to call on a server without Plaid: it refreshes the list and says so, no error.
    await tester.tap(find.text('Sync'));
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 100)));
    await tester.pump(const Duration(milliseconds: 500));
    expect(find.text('Up to date. Bank feeds sync on their own schedule.'), findsOneWidget);
    expect((apiClient.dio.httpClientAdapter as _Routes).calls.where((c) => c.contains('/plaid/')), isEmpty);
    await tester.pump(const Duration(seconds: 6));

    await tester.tap(find.text('Add a bank'));
    await tester.pump(const Duration(seconds: 1));

    expect(find.byType(AddBankChooserSheet), findsOneWidget);
    expect(find.text('SimpleFIN'), findsOneWidget);
    expect(find.text('Import a file'), findsOneWidget);
    expect(find.text('Connect with Plaid'), findsNothing);

    // 4. A linked self-host (Bridge offered) whose connect fails while the providers call also fails: a transient
    // outage is not "link first". The link prompt must not appear, because the refresh could not confirm anything.
    await pumpPage(tester, {
      'GET /family/members': (status: 400, body: {'message': 'You are not in a family'}),
      'GET /bank/providers': (status: 200, body: ['Bridge']),
      'POST /aggregators/Bridge/connect/start': (status: 500, body: {'message': 'boom'}),
    });
    await tester.tap(find.text('Add a bank'));
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(AddBankChooserSheet), findsOneWidget);
    final routes = (apiClient.dio.httpClientAdapter as _Routes).routes;
    routes['GET /bank/providers'] = (status: 500, body: {'message': 'down'}); // the server is now unreachable for the providers refresh too
    // The sheet lays out below the test viewport, so pick the option the way a tap would.
    tester.widget<AddBankChooserSheet>(find.byType(AddBankChooserSheet)).onSelected(BankSource.cloudFeed);
    for (var i = 0; i < 10; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pump(const Duration(milliseconds: 200));
    }
    expect((apiClient.dio.httpClientAdapter as _Routes).calls, contains('POST /aggregators/Bridge/connect/start'));
    expect(find.text('Link with BuoyAI Cloud first'), findsNothing);
    await tester.pump(const Duration(seconds: 6)); // let the error toast go

    // The failed refresh kept the confirmed list: the next Add a bank still offers connecting, not linking.
    await tester.tap(find.text('Add a bank'));
    await tester.pump(const Duration(seconds: 1));
    expect(find.byType(AddBankChooserSheet), findsOneWidget);
    expect(tester.widget<AddBankChooserSheet>(find.byType(AddBankChooserSheet)).cloudNeedsLink, isFalse);
    Navigator.of(tester.element(find.byType(AddBankChooserSheet))).pop();
    await tester.pump(const Duration(seconds: 1));

    // 5. Linked a moment ago: the page still holds the list from before the link. Choosing the Cloud feed asks
    // again and connects, instead of sending the person back to "link first".
    await pumpPage(tester, {
      'GET /family/members': (status: 400, body: {'message': 'You are not in a family'}),
      'GET /bank/providers': (status: 200, body: ['SimpleFin']),
      'POST /aggregators/Bridge/connect/start': (status: 500, body: {'message': 'boom'}),
    });
    await tester.tap(find.text('Add a bank'));
    await tester.pump(const Duration(seconds: 1));
    expect(tester.widget<AddBankChooserSheet>(find.byType(AddBankChooserSheet)).cloudNeedsLink, isTrue);
    (apiClient.dio.httpClientAdapter as _Routes).routes['GET /bank/providers'] = (status: 200, body: ['Bridge']);
    tester.widget<AddBankChooserSheet>(find.byType(AddBankChooserSheet)).onSelected(BankSource.cloudFeed);
    for (var i = 0; i < 10; i++) {
      await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 50)));
      await tester.pump(const Duration(milliseconds: 200));
    }
    expect((apiClient.dio.httpClientAdapter as _Routes).calls, contains('POST /aggregators/Bridge/connect/start'));
    await tester.pump(const Duration(seconds: 6));

    // 6. The link prompt scrolls on a short (landscape phone) screen instead of overflowing.
    tester.view.physicalSize = const Size(700, 220);
    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: CloudLinkPromptSheet(onLink: () {}, onClose: () {})),
    ));
    expect(tester.takeException(), isNull);
    expect(find.byType(SingleChildScrollView), findsWidgets);
  });
}
