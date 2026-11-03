import 'dart:convert';
import 'dart:typed_data';

import 'package:buoy_ai/services/relay_push_service.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';

// The shared test vector from docs/self_host/push_relay.md. The backend sealed this envelope with
// .NET; opening it here proves the two implementations agree.
const _d = 'jO/CBsTnlvkQFg+2FrflxefFmt1StsH/fAWhZ6GN9eM=';
const _x = 'GCnwadxrqiERteM7e3+CIt602oFM8t4hWF/OcGBR0W8=';
const _y = 'hJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=';
const _pub =
    'BBgp8Gnca6ohEbXjO3t/giLetNqBTPLeIVhfznBgUdFvhJ1LQJZc/dl/btGG1UEY4W96ze1IfbeXOPeDE3SiVNo=';
const _envelope =
    'AQRCEM4olUC53EDO-9e3wrTw4sMuuCZMeM0j-p0RXw41eh_MuRX_NBH8zIOEVeoOjH4uuTjJD3vLDKGDslInyha6HEE-rV_OcdMj0OmAyfJiVQUs2pOWZXa0SM_XSgmFhC5kmQTX6kcJIPe1E_3vRKj32wQOECl5f4XOkYFlUZYDMVV4fq9gFzmfeQgRFt0gzomQbCzuU5QYyz9RZCjJvP3CDlj2-ioOUeLu-qZDOvjxrHHp5NZ5i38y7P3_M7FmJCbRj_uVfEf5wIvozHeryo18FV73GvCZdKoMiGPrAA';

RelayKeyPair _vectorKeys() => RelayKeyPair(
      d: base64.decode(_d),
      x: base64.decode(_x),
      y: base64.decode(_y),
    );

class _MemoryStore implements RelayKeyStore {
  final Map<String, String> values = {};
  @override
  Future<String?> read(String key) async => values[key];
  @override
  Future<void> write(String key, String value) async => values[key] = value;
  @override
  Future<void> delete(String key) async => values.remove(key);
}

class _RelayAdapter implements HttpClientAdapter {
  _RelayAdapter({this.body = '{"pairingToken":"pair-123"}'});
  final String body;
  final List<RequestOptions> requests = [];

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream,
      Future<void>? cancelFuture) async {
    requests.add(options);
    return ResponseBody.fromString(body, 200, headers: {
      Headers.contentTypeHeader: [Headers.jsonContentType],
    });
  }

  @override
  void close({bool force = false}) {}
}

RelayPushService _service(_MemoryStore store, _RelayAdapter adapter) => RelayPushService(
      store: store,
      dioFor: (url) => Dio(BaseOptions(baseUrl: url))..httpClientAdapter = adapter,
    );

void main() {
  group('opening an envelope', () {
    test('opens the envelope the server sealed (cross-language test vector)', () async {
      final message = await RelayPushCrypto.open(_vectorKeys(), _envelope);

      expect(message, isNotNull);
      expect(message!.title, 'Dining is over budget');
      expect(message.body, 'You spent 412 of 400');
      expect(message.route, '/budgets');
      expect(message.data['type'], 'budget_alert');
      expect(message.timestamp, 1760000000);
    });

    test('the public key matches the one the server was given', () {
      expect(_vectorKeys().publicKeyBase64, _pub);
    });

    test('another phone cannot open it', () async {
      final other = await RelayPushCrypto.generateKeyPair();

      expect(await RelayPushCrypto.open(other, _envelope), isNull);
    });

    test('a changed envelope is dropped, not shown', () async {
      final bytes = base64Url.decode(base64Url.normalize(_envelope));
      bytes[bytes.length - 20] ^= 0x01;
      final tampered = base64Url.encode(bytes).replaceAll('=', '');

      expect(await RelayPushCrypto.open(_vectorKeys(), tampered), isNull);
    });

    test('rubbish and unknown versions are dropped quietly', () async {
      final keys = _vectorKeys();
      expect(await RelayPushCrypto.open(keys, ''), isNull);
      expect(await RelayPushCrypto.open(keys, 'not an envelope'), isNull);
      final bytes = base64Url.decode(base64Url.normalize(_envelope));
      bytes[0] = 2;
      expect(await RelayPushCrypto.open(keys, base64Url.encode(bytes)), isNull);
    });
  });

  group('pairing', () {
    test('keys are made once and kept', () async {
      final store = _MemoryStore();
      final first = await _service(store, _RelayAdapter()).keys();
      final again = await _service(store, _RelayAdapter()).keys();

      expect(again.publicKeyBase64, first.publicKeyBase64);
      expect(base64.decode(first.publicKeyBase64).length, 65);
      expect(base64.decode(first.publicKeyBase64).first, 4);
    });

    test('pairs with the relay and remembers the pairing token', () async {
      final store = _MemoryStore();
      final adapter = _RelayAdapter();
      final service = _service(store, adapter);

      final token = await service.pair(
          relayUrl: 'https://relay.test', pushToken: 'fcm-abc', platform: 'android');

      expect(token, 'pair-123');
      expect(await service.pairingToken(), 'pair-123');
      final request = adapter.requests.single;
      expect(request.uri.toString(), 'https://relay.test/v1/pair');
      expect(request.data, {'platform': 'android', 'pushToken': 'fcm-abc'});
    });

    test('a refresh sends the existing pairing token so the relay updates it', () async {
      final store = _MemoryStore();
      final adapter = _RelayAdapter();
      final service = _service(store, adapter);
      await service.pair(relayUrl: 'https://relay.test', pushToken: 'fcm-1', platform: 'ios');

      await service.pair(relayUrl: 'https://relay.test', pushToken: 'fcm-2', platform: 'ios');

      expect(adapter.requests.last.data['pairingToken'], 'pair-123');
      expect(adapter.requests.last.data['pushToken'], 'fcm-2');
    });

    test('a relay answer without a token is an error, not a silent success', () async {
      final service = _service(_MemoryStore(), _RelayAdapter(body: '{}'));

      expect(
        () => service.pair(relayUrl: 'https://relay.test', pushToken: 't', platform: 'android'),
        throwsA(isA<StateError>()),
      );
    });

    test('the registration body for the server carries the pairing token and public key', () async {
      final service = _service(_MemoryStore(), _RelayAdapter());

      final body = await service.registrationBody('pair-123');

      expect(body['platform'], 'relay');
      expect(body['token'], 'pair-123');
      expect(body['publicKey'], (await service.keys()).publicKeyBase64);
    });

    test('logging out forgets the pairing but keeps the phone key', () async {
      final store = _MemoryStore();
      final service = _service(store, _RelayAdapter());
      final key = (await service.keys()).publicKeyBase64;
      await service.pair(relayUrl: 'https://relay.test', pushToken: 't', platform: 'android');

      await service.clearPairing();

      expect(await service.pairingToken(), isNull);
      expect((await _service(store, _RelayAdapter()).keys()).publicKeyBase64, key);
    });
  });
}
