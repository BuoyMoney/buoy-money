import 'dart:convert';
import 'dart:typed_data';

import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/services/server_version_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

/// Answers every request with a fixed status and body, or fails like a dead network.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter({this.status = 200, this.body = '', this.fail = false});

  final int status;
  final String body;
  final bool fail;
  String? requestedPath;

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    requestedPath = options.path;
    if (fail) throw DioException.connectionError(requestOptions: options, reason: 'offline');
    return ResponseBody.fromString(body, status, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

ServerVersionService _service(_StubAdapter adapter, {String app = '1.6.0', String minServer = '1.5.29'}) {
  final dio = Dio(BaseOptions(baseUrl: 'http://server/api'))..httpClientAdapter = adapter;
  return ServerVersionService(dio: dio, appVersion: app, minServerVersion: minServer);
}

String _json(String version, {String minClient = '1.0.0', String edition = 'selfhost'}) =>
    jsonEncode({'version': version, 'minClientVersion': minClient, 'edition': edition});

void main() {
  group('version parsing and comparison', () {
    test('reads X.Y.Z with an optional v prefix and build suffix', () {
      expect(ServerVersionService.parseVersion('1.5.29'), [1, 5, 29]);
      expect(ServerVersionService.parseVersion('v2.0.1'), [2, 0, 1]);
      expect(ServerVersionService.parseVersion('1.5.29+3'), [1, 5, 29]);
      expect(ServerVersionService.parseVersion('1.5.29-rc1'), [1, 5, 29]);
    });

    test('returns null for anything that is not a release number', () {
      expect(ServerVersionService.parseVersion('dev'), isNull);
      expect(ServerVersionService.parseVersion('unknown'), isNull);
      expect(ServerVersionService.parseVersion(''), isNull);
      expect(ServerVersionService.parseVersion('1.2.3garbage'), isNull);
      expect(ServerVersionService.parseVersion('99999999999999999999.0.0'), isNull);
      expect(ServerVersionService.parseVersion(null), isNull);
    });

    test('compares numerically, not as text', () {
      expect(ServerVersionService.compareVersions([1, 10, 0], [1, 9, 0]), greaterThan(0));
      expect(ServerVersionService.compareVersions([1, 5, 29], [1, 5, 30]), lessThan(0));
      expect(ServerVersionService.compareVersions([2, 0, 0], [1, 99, 99]), greaterThan(0));
      expect(ServerVersionService.compareVersions([1, 5, 29], [1, 5, 29]), 0);
      expect(ServerVersionService.compareVersions([1, 0, 0], null), greaterThan(0));
    });
  });

  group('check', () {
    test('asks the system version endpoint', () async {
      final adapter = _StubAdapter(body: _json('1.6.0'));
      await _service(adapter).check();
      expect(adapter.requestedPath, '/system/version');
    });

    test('a current server and app are fine', () async {
      final result = await _service(_StubAdapter(body: _json('1.6.0'))).check();
      expect(result.compatibility, ServerCompatibility.ok);
    });

    test('a server older than the app needs asks the owner to update the server', () async {
      final result = await _service(_StubAdapter(body: _json('1.5.10'))).check();
      expect(result.compatibility, ServerCompatibility.serverNeedsUpdate);
      expect(result.serverVersion, '1.5.10');
      expect(result.requiredServerVersion, '1.5.29');
    });

    test('a server that has moved past this app asks the user to update the app', () async {
      final result = await _service(_StubAdapter(body: _json('2.0.0', minClient: '1.9.0'))).check();
      expect(result.compatibility, ServerCompatibility.appNeedsUpdate);
      expect(result.requiredAppVersion, '1.9.0');
      expect(result.appVersion, '1.6.0');
    });

    test('exactly the minimum on either side is accepted', () async {
      final result = await _service(_StubAdapter(body: _json('1.5.29', minClient: '1.6.0'))).check();
      expect(result.compatibility, ServerCompatibility.ok);
    });

    test('a local dev build is never blocked by the client side', () async {
      final result = await _service(_StubAdapter(body: _json('2.0.0', minClient: '1.9.0')), app: 'dev').check();
      expect(result.compatibility, ServerCompatibility.ok);
    });

    test('a server that predates the endpoint (404) does not lock anyone out', () async {
      final result = await _service(_StubAdapter(status: 404, body: '{}')).check();
      expect(result.compatibility, ServerCompatibility.ok);
    });

    test('no network, a server error or a garbled body do not lock anyone out', () async {
      expect((await _service(_StubAdapter(fail: true)).check()).compatibility, ServerCompatibility.ok);
      expect((await _service(_StubAdapter(status: 500, body: 'oops')).check()).compatibility, ServerCompatibility.ok);
      expect((await _service(_StubAdapter(body: '[1,2]')).check()).compatibility, ServerCompatibility.ok);
      expect((await _service(_StubAdapter(body: _json('unknown'))).check()).compatibility, ServerCompatibility.ok);
    });
  });

  group('what the server can do', () {
    tearDown(() => ServerEdition.current.value = ServerCapabilities.hosted);

    test('a self-hosted answer hides hosted-only screens', () async {
      final body = jsonEncode({
        'version': '1.5.29',
        'minClientVersion': '1.0.0',
        'edition': 'selfhost',
        'pushRelayUrl': 'https://relay.example',
        'features': {'googleSignIn': false, 'passkeys': true, 'email': false},
      });

      await _service(_StubAdapter(body: body)).check();

      final caps = ServerEdition.current.value;
      expect(caps.isSelfHost, isTrue);
      expect(caps.hasBilling, isFalse);
      expect(caps.hasBankConnection, isFalse);
      expect(caps.googleSignIn, isFalse);
      expect(caps.email, isFalse);
      expect(caps.pushRelayUrl, 'https://relay.example');
    });

    test('the server needing an update still reports what it is', () async {
      final body = jsonEncode({'version': '1.0.0', 'minClientVersion': '1.0.0', 'edition': 'selfhost'});

      final result = await _service(_StubAdapter(body: body)).check();

      expect(result.compatibility, ServerCompatibility.serverNeedsUpdate);
      expect(ServerEdition.current.value.isSelfHost, isTrue);
    });

    test('hosted, an old hosted server and a failed check all keep every screen', () async {
      await _service(_StubAdapter(body: _json('1.5.29', edition: 'hosted'))).check();
      expect(ServerEdition.current.value.hasBilling, isTrue);
      expect(ServerEdition.current.value.googleSignIn, isTrue);

      ServerEdition.current.value = ServerCapabilities.hosted;
      await _service(_StubAdapter(status: 404, body: '{}')).check();
      await _service(_StubAdapter(fail: true)).check();
      expect(ServerEdition.current.value.hasBilling, isTrue);
      expect(ServerEdition.current.value.hasBankConnection, isTrue);
    });
  });
}
