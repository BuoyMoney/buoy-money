import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/server_capabilities.dart';
import 'package:buoy_ai/services/server_selection_service.dart';

/// Answers with a fixed status and body, or fails like a dead network.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter({this.status = 200, this.body = '', this.fail = false, this.contentType});

  final int status;
  final String body;
  final bool fail;
  final String? contentType;
  Uri? requested;

  @override
  Future<ResponseBody> fetch(
      RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    requested = options.uri;
    if (fail) throw DioException.connectionError(requestOptions: options, reason: 'offline');
    return ResponseBody.fromString(body, status, headers: {
      Headers.contentTypeHeader: [contentType ?? Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

String _version(String version,
        {String edition = 'selfhost', String minClient = '1.0.0', Map<String, dynamic>? extra}) =>
    jsonEncode({'version': version, 'minClientVersion': minClient, 'edition': edition, ...?extra});

void main() {
  late List<String> calls;

  ServerSelectionService service(_StubAdapter adapter,
      {String app = '1.6.0', String minServer = '1.5.29'}) {
    return ServerSelectionService(
      dioFor: (origin) => Dio(BaseOptions(baseUrl: '$origin/api'))..httpClientAdapter = adapter,
      clearServerState: () async => calls.add('clear'),
      applyBaseUrl: () => calls.add('apply ${AppConfig.baseUrl}'),
      appVersion: app,
      minServerVersion: minServer,
    );
  }

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

  group('probe', () {
    test('a current self-hosted server passes, with its capabilities', () async {
      final adapter = _StubAdapter(
          body: _version('1.5.35', extra: {
        'features': {'googleSignIn': false}
      }));
      final result = await service(adapter).probe('buoy.example.ts.net/app/');

      expect(result.ok, isTrue);
      expect(result.origin, 'https://buoy.example.ts.net');
      expect(adapter.requested.toString(), 'https://buoy.example.ts.net/api/system/version');
      expect(result.capabilities!.isSelfHost, isTrue);
      expect(result.capabilities!.googleSignIn, isFalse);
    });

    test('a bad address is refused without a request', () async {
      final adapter = _StubAdapter(body: _version('1.5.35'));
      final result = await service(adapter).probe('not a url');
      expect(result.error, ServerProbeError.invalidAddress);
      expect(adapter.requested, isNull);
    });

    test('no answer: unreachable', () async {
      final result = await service(_StubAdapter(fail: true)).probe('https://buoy.example.ts.net');
      expect(result.error, ServerProbeError.unreachable);
    });

    test('a 404 page: not a Buoy Money server', () async {
      final result =
          await service(_StubAdapter(status: 404, body: 'Not found', contentType: 'text/html'))
              .probe('https://example.com');
      expect(result.error, ServerProbeError.notBuoyServer);
    });

    test('a web page that answers 200: not a Buoy Money server', () async {
      final result = await service(_StubAdapter(body: '<html></html>', contentType: 'text/html'))
          .probe('https://example.com');
      expect(result.error, ServerProbeError.notBuoyServer);
    });

    test('JSON without a version: not a Buoy Money server', () async {
      final result =
          await service(_StubAdapter(body: jsonEncode({'status': 'ok'}))).probe('https://example.com');
      expect(result.error, ServerProbeError.notBuoyServer);
    });

    test('a server older than the app needs: server too old, with the numbers', () async {
      final result =
          await service(_StubAdapter(body: _version('1.4.0'))).probe('https://buoy.example.ts.net');
      expect(result.error, ServerProbeError.serverTooOld);
      expect(result.version!.serverVersion, '1.4.0');
      expect(result.version!.requiredServerVersion, '1.5.29');
    });

    test('a server that needs a newer app: app too old', () async {
      final result = await service(_StubAdapter(body: _version('1.6.0', minClient: '2.0.0')))
          .probe('https://buoy.example.ts.net');
      expect(result.error, ServerProbeError.appTooOld);
      expect(result.version!.requiredAppVersion, '2.0.0');
    });
  });

  group('useServer', () {
    test('switching clears old state, saves, repoints the client, publishes capabilities',
        () async {
      final caps = ServerCapabilities.fromJson({'edition': 'selfhost'});
      await service(_StubAdapter()).useServer('https://buoy.example.ts.net', capabilities: caps);

      expect(calls, ['clear', 'apply https://buoy.example.ts.net/api']);
      expect(AppConfig.customServerOrigin, 'https://buoy.example.ts.net');
      expect(ServerEdition.current.value.isSelfHost, isTrue);
      expect(ServerEdition.answerPending, isFalse);
      final prefs = await SharedPreferences.getInstance();
      expect(prefs.getString(AppConfig.customServerPrefKey), 'https://buoy.example.ts.net');
    });

    test('going back to Cloud forgets the server and restores the hosted capabilities', () async {
      AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
      ServerEdition.current.value = ServerCapabilities.fromJson({'edition': 'selfhost'});

      await service(_StubAdapter()).useServer(null);

      expect(calls, ['clear', 'apply ${AppConfig.defaultBaseUrl}']);
      expect(AppConfig.isCustomServer, isFalse);
      expect(identical(ServerEdition.current.value, ServerCapabilities.hosted), isTrue);
    });

    test('typing the build default address counts as Cloud', () async {
      final origin = AppConfig.defaultBaseUrl.replaceFirst(RegExp(r'/api$'), '');
      await service(_StubAdapter())
          .useServer(origin, capabilities: ServerCapabilities.fromJson({'edition': 'selfhost'}));
      expect(AppConfig.isCustomServer, isFalse);
      expect(ServerEdition.current.value.isSelfHost, isFalse);
    });
  });
}
