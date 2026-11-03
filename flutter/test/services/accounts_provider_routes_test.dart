import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/services/accounts_service.dart';
import 'package:buoy_ai/services/api_client.dart';

/// Answers each path from a table and records what was asked. A path not in the table is a missing
/// route: an empty-bodied 404, as an older server gives.
class _RouteAdapter implements HttpClientAdapter {
  final Map<String, ({int status, Object? body})> routes = {};
  final calls = <String>[];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls.add('${options.method} ${options.path}');
    final route = routes['${options.method} ${options.path}'];
    final body = route == null ? '' : jsonEncode(route.body);
    return ResponseBody.fromString(
      body,
      route?.status ?? 404,
      headers: {Headers.contentTypeHeader: [Headers.jsonContentType]},
    );
  }

  @override
  void close({bool force = false}) {}
}

const _connection = {
  'id': 4,
  'institutionName': 'Credit Union',
  'provider': 'SimpleFin',
  'accounts': [
    {'id': 10, 'name': 'Checking', 'type': 0},
  ],
};

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  late _RouteAdapter adapter;

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
      (call) async => null,
    );
    adapter = _RouteAdapter();
    apiClient.dio.httpClientAdapter = adapter;
    ServerEdition.current.value = ServerCapabilities.hosted;
  });

  tearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);

  group('a current server', () {
    test('loads connections and accounts from the provider-neutral routes, on self-host too', () async {
      ServerEdition.current.value = const ServerCapabilities(edition: 'selfhost');
      adapter.routes['GET /bank/connections'] = (status: 200, body: [_connection]);
      adapter.routes['GET /bank/accounts'] = (status: 200, body: [{'id': 10, 'name': 'Checking', 'type': 0}]);

      final connections = await AccountsService().getConnections();
      final accounts = await AccountsService().getAccounts();

      expect(connections.single.isSimpleFin, isTrue);
      expect(connections.single.accounts.single.name, 'Checking');
      expect(accounts.single.id, 10);
      expect(adapter.calls, ['GET /bank/connections', 'GET /bank/accounts']);
    });

    test('nickname, association and import progress use the neutral routes', () async {
      adapter.routes['PUT /bank/accounts/10/nickname'] = (status: 200, body: {'accountId': 10, 'nickname': 'Joint'});
      adapter.routes['PUT /bank/accounts/10/association'] = (status: 200, body: {'accountId': 10});
      adapter.routes['GET /bank/connections/4/import-progress'] = (status: 200, body: {'connectionId': 4, 'status': 'Completed', 'progress': 100});

      final nick = await AccountsService().updateAccountNickname(10, 'Joint');
      await AccountsService().setAccountAssociation(10, null);
      final progress = await AccountsService().getImportProgress(4);

      expect(nick['nickname'], 'Joint');
      expect(progress.connectionId, 4);
      expect(adapter.calls.every((c) => c.contains('/bank/')), isTrue);
    });

    test('a server error on the new accounts route falls back to the old route (offline cache from before the upgrade)', () async {
      adapter.routes['GET /bank/accounts'] = (status: 500, body: {'message': 'boom'});
      adapter.routes['GET /plaid/accounts'] = (status: 200, body: [{'id': 10, 'name': 'Checking', 'type': 0}]);

      final accounts = await AccountsService().getAccounts();

      expect(accounts.single.id, 10);
      expect(adapter.calls.last, 'GET /plaid/accounts');
    });

    test('a server error with nothing on the old route either still throws the original error', () async {
      adapter.routes['GET /bank/accounts'] = (status: 500, body: {'message': 'boom'});
      adapter.routes['GET /plaid/accounts'] = (status: 500, body: {'message': 'boom'});

      await expectLater(AccountsService().getAccounts(), throwsA(isA<DioException>()));
    });

    test('a real 404 (account not found) is not retried on the Plaid route', () async {
      adapter.routes['PUT /bank/accounts/99/nickname'] = (status: 404, body: {'message': 'Account not found'});

      await expectLater(
        AccountsService().updateAccountNickname(99, 'x'),
        throwsA(isA<DioException>().having((e) => e.response?.statusCode, 'status', 404)),
      );
      expect(adapter.calls, ['PUT /bank/accounts/99/nickname']);
    });

    test('lists the providers the server offers', () async {
      adapter.routes['GET /bank/providers'] = (status: 200, body: ['SimpleFin', 'Bridge']);

      expect(await AccountsService().getBankProviders(), ['SimpleFin', 'Bridge']);
    });
  });

  group('an older server', () {
    test('hosted falls back to the Plaid routes', () async {
      adapter.routes['GET /plaid/connections'] = (status: 200, body: [_connection]);
      adapter.routes['GET /plaid/accounts'] = (status: 200, body: [{'id': 10, 'name': 'Checking', 'type': 0}]);
      adapter.routes['PUT /plaid/accounts/10/nickname'] = (status: 200, body: {'accountId': 10});

      expect((await AccountsService().getConnections()).length, 1);
      expect((await AccountsService().getAccounts()).length, 1);
      await AccountsService().updateAccountNickname(10, 'x');

      expect(adapter.calls, [
        'GET /bank/connections',
        'GET /plaid/connections',
        'GET /bank/accounts',
        'GET /plaid/accounts',
        'PUT /bank/accounts/10/nickname',
        'PUT /plaid/accounts/10/nickname',
      ]);
    });

    test('self-host with no neutral route shows no connections instead of calling Plaid routes it does not have', () async {
      ServerEdition.current.value = const ServerCapabilities(edition: 'selfhost');

      expect(await AccountsService().getConnections(), isEmpty);
      expect(adapter.calls, ['GET /bank/connections']);
    });

    test('providers fall back to the plain list, which never includes the Cloud feed', () async {
      adapter.routes['GET /aggregators'] = (status: 200, body: ['Plaid', 'SimpleFin', 'Bridge']);

      expect(await AccountsService().getBankProviders(), ['Plaid', 'SimpleFin']);
    });
  });

  group('removing a connection', () {
    test('Plaid keeps its own route (step-up check, revoke at Plaid)', () async {
      adapter.routes['DELETE /plaid/connections/4'] = (status: 200, body: {'message': 'ok'});

      await AccountsService().removeConnection(4);

      expect(adapter.calls, ['DELETE /plaid/connections/4']);
    });

    test('other providers use the neutral route, which self-host has', () async {
      adapter.routes['DELETE /aggregators/connections/4'] = (status: 200, body: {'message': 'ok'});

      await AccountsService().removeConnection(4, provider: 'SimpleFin');

      expect(adapter.calls, ['DELETE /aggregators/connections/4']);
    });
  });
}
