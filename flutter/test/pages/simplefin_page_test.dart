import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/pages/simplefin_page.dart';
import 'package:buoy_ai/services/simplefin_service.dart';

class _FakeService extends SimpleFinService {
  _FakeService({List<BankConnectionSummary>? connections, this.result, this.connectError})
      : _connections = connections ?? [];

  List<BankConnectionSummary> _connections;
  final SimpleFinConnectResult? result;
  final Object? connectError;
  final tokens = <String>[];
  final removed = <int>[];

  @override
  Future<List<BankConnectionSummary>> connections() async => _connections;

  @override
  Future<String> bridgeUrl() async => 'https://bridge.simplefin.org/simplefin/create';

  @override
  Future<SimpleFinConnectResult> connect(String setupToken) async {
    tokens.add(setupToken);
    if (connectError != null) throw connectError!;
    return result ?? const SimpleFinConnectResult(success: true);
  }

  @override
  Future<void> remove(int connectionId) async {
    removed.add(connectionId);
    _connections = _connections.where((c) => c.id != connectionId).toList();
  }
}

BankConnectionSummary _connection({
  int id = 1,
  String name = 'My Bank',
  bool isActive = true,
  bool requiresUserAction = false,
  String status = 'Active',
  String? message,
  DateTime? lastSyncAt,
  int accounts = 2,
}) =>
    BankConnectionSummary(
      id: id,
      provider: 'SimpleFin',
      institutionName: name,
      isActive: isActive,
      requiresUserAction: requiresUserAction,
      status: status,
      lastSyncAt: lastSyncAt,
      message: message,
      accountCount: accounts,
    );

Widget _wrap(SimpleFinService service) => MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: SimpleFinPage(service: service),
    );

void main() {
  testWidgets('shows the two steps and an empty list when nothing is connected', (tester) async {
    await tester.pumpWidget(_wrap(_FakeService()));
    await tester.pumpAndSettle();

    expect(find.textContaining('1. Open SimpleFIN'), findsOneWidget);
    expect(find.textContaining('2. Paste the token'), findsOneWidget);
    expect(find.text('Nothing connected yet.'), findsOneWidget);
  });

  testWidgets('pasting a token connects, clears the box and reloads the list', (tester) async {
    final service = _FakeService(result: const SimpleFinConnectResult(success: true));
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const ValueKey('simplefin-token')), '  aGVsbG8=  ');
    await tester.tap(find.byKey(const ValueKey('simplefin-connect')));
    await tester.pumpAndSettle();

    expect(service.tokens, ['aGVsbG8=']);
    expect(tester.widget<TextField>(find.byKey(const ValueKey('simplefin-token'))).controller!.text, isEmpty);
    expect(find.byKey(const ValueKey('simplefin-error')), findsNothing);
  });

  testWidgets('does nothing for an empty token', (tester) async {
    final service = _FakeService();
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('simplefin-connect')));
    await tester.pumpAndSettle();

    expect(service.tokens, isEmpty);
  });

  testWidgets('a refused token shows the server message and keeps the text', (tester) async {
    final service = _FakeService(
      result: const SimpleFinConnectResult(success: false, message: 'SimpleFIN did not accept that setup token.'),
    );
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const ValueKey('simplefin-token')), 'abc');
    await tester.tap(find.byKey(const ValueKey('simplefin-connect')));
    await tester.pumpAndSettle();

    expect(find.text('SimpleFIN did not accept that setup token.'), findsOneWidget);
    expect(tester.widget<TextField>(find.byKey(const ValueKey('simplefin-token'))).controller!.text, 'abc');
  });

  testWidgets('a request that fails shows the server message, else a plain one', (tester) async {
    final options = RequestOptions(path: '/aggregators/SimpleFin/connect/complete');
    final service = _FakeService(
      connectError: DioException(
        requestOptions: options,
        response: Response(requestOptions: options, statusCode: 403, data: {'message': 'Only owners can do that.'}),
      ),
    );
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.enterText(find.byKey(const ValueKey('simplefin-token')), 'abc');
    await tester.tap(find.byKey(const ValueKey('simplefin-connect')));
    await tester.pumpAndSettle();

    expect(find.text('Only owners can do that.'), findsOneWidget);
  });

  testWidgets('a healthy connection says it is working and offers disconnect', (tester) async {
    final service = _FakeService(connections: [
      _connection(lastSyncAt: DateTime(2026, 10, 9), accounts: 3),
    ]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.text('My Bank'), findsOneWidget);
    expect(find.text('Working'), findsOneWidget);
    expect(find.textContaining('3 accounts'), findsOneWidget);
    expect(find.byKey(const ValueKey('simplefin-disconnect-1')), findsOneWidget);
    expect(find.textContaining('Make a new setup token'), findsNothing);
  });

  testWidgets('a connection that needs attention says why and what to do', (tester) async {
    final service = _FakeService(connections: [
      _connection(
        status: 'LoginRequired',
        requiresUserAction: true,
        message: 'SimpleFIN no longer accepts this connection.',
      ),
    ]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.text('Needs attention'), findsOneWidget);
    expect(find.text('SimpleFIN no longer accepts this connection.'), findsOneWidget);
    expect(find.textContaining('Make a new setup token and paste it above'), findsOneWidget);
  });

  testWidgets('an active connection the bridge flagged points to the bridge, not a new token', (tester) async {
    final service = _FakeService(connections: [
      _connection(
        status: 'Active',
        requiresUserAction: true,
        message: 'Sign in at your bank again.',
      ),
    ]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.textContaining('Sign in at your SimpleFIN bridge'), findsOneWidget);
    expect(find.textContaining('Make a new setup token'), findsNothing);
  });

  testWidgets('a disconnected connection has no disconnect button', (tester) async {
    final service = _FakeService(connections: [
      _connection(isActive: false, status: 'Disconnected'),
    ]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    expect(find.text('Disconnected'), findsOneWidget);
    expect(find.byKey(const ValueKey('simplefin-disconnect-1')), findsNothing);
  });

  testWidgets('disconnect asks first, then removes the connection', (tester) async {
    final service = _FakeService(connections: [_connection()]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.byKey(const ValueKey('simplefin-disconnect-1')));

    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('simplefin-disconnect-1')));
    await tester.pumpAndSettle();
    expect(find.text('Disconnect this connection?'), findsOneWidget);
    expect(service.removed, isEmpty);

    await tester.tap(find.byKey(const ValueKey('simplefin-confirm-disconnect')));
    await tester.pumpAndSettle();

    expect(service.removed, [1]);
    expect(find.text('My Bank'), findsNothing);
  });

  testWidgets('cancelling the disconnect dialog removes nothing', (tester) async {
    final service = _FakeService(connections: [_connection()]);
    await tester.pumpWidget(_wrap(service));
    await tester.pumpAndSettle();

    await tester.ensureVisible(find.byKey(const ValueKey('simplefin-disconnect-1')));

    await tester.pumpAndSettle();

    await tester.tap(find.byKey(const ValueKey('simplefin-disconnect-1')));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();

    expect(service.removed, isEmpty);
    expect(find.text('My Bank'), findsOneWidget);
  });
}
