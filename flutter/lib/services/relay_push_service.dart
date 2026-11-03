import 'dart:convert';
import 'dart:math';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:pointycastle/export.dart';

/// A notification the server encrypted to this phone (see docs/self_host/push_relay.md).
class RelayMessage {
  const RelayMessage({
    required this.title,
    required this.body,
    required this.data,
    required this.timestamp,
  });

  final String title;
  final String body;
  final Map<String, String> data;
  final int timestamp;

  String? get route => data['route'];
}

/// The phone's push key pair. The private half never leaves the device.
class RelayKeyPair {
  const RelayKeyPair({required this.d, required this.x, required this.y});

  final List<int> d;
  final List<int> x;
  final List<int> y;

  /// Base64 of the 65-byte uncompressed point `0x04 | X | Y`; what the server stores.
  String get publicKeyBase64 => base64.encode([4, ..._pad(x), ..._pad(y)]);

  static List<int> _pad(List<int> v) =>
      v.length >= 32 ? v : [...List<int>.filled(32 - v.length, 0), ...v];

  Map<String, String> toJson() =>
      {'d': base64.encode(d), 'x': base64.encode(x), 'y': base64.encode(y)};

  factory RelayKeyPair.fromJson(Map<String, dynamic> json) => RelayKeyPair(
        d: base64.decode(json['d'] as String),
        x: base64.decode(json['x'] as String),
        y: base64.decode(json['y'] as String),
      );
}

/// Opens the push envelopes a self-hosted server sends through the relay.
///
/// Format: 0x01 | ephemeral P-256 public key (65) | nonce (12) | ciphertext | tag (16), base64url.
/// Key: ECDH x coordinate, then HKDF-SHA256 (salt = ephemeral public key, info `buoymoney-push-v1`).
/// Cipher: AES-256-GCM with the version byte as associated data.
///
/// Pure Dart (pointycastle), so it behaves the same on web, Android, iOS and in tests.
class RelayPushCrypto {
  static final _domain = ECDomainParameters('prime256v1');
  static final _info = Uint8List.fromList(utf8.encode('buoymoney-push-v1'));

  static Future<RelayKeyPair> generateKeyPair() async {
    final seed = Random.secure();
    final random = FortunaRandom()
      ..seed(KeyParameter(Uint8List.fromList(List<int>.generate(32, (_) => seed.nextInt(256)))));
    final generator = ECKeyGenerator()
      ..init(ParametersWithRandom(ECKeyGeneratorParameters(_domain), random));
    final pair = generator.generateKeyPair();
    final priv = pair.privateKey;
    final pub = pair.publicKey;
    return RelayKeyPair(
      d: _bytes(priv.d!),
      x: _bytes(pub.Q!.x!.toBigInteger()!),
      y: _bytes(pub.Q!.y!.toBigInteger()!),
    );
  }

  /// The message, or null when this envelope is not for this phone, was altered, or is a version
  /// this app does not know. A push that cannot be opened is dropped quietly.
  static Future<RelayMessage?> open(RelayKeyPair keys, String envelope) async {
    try {
      final bytes = _fromBase64Url(envelope);
      if (bytes.length < 1 + 65 + 12 + 16 || bytes[0] != 1) return null;
      final ephemeralPublic = Uint8List.fromList(bytes.sublist(1, 66));
      final nonce = Uint8List.fromList(bytes.sublist(66, 78));
      final cipherAndTag = Uint8List.fromList(bytes.sublist(78));
      if (ephemeralPublic[0] != 4) return null;

      final remote = ECPublicKey(
        _domain.curve.createPoint(_big(ephemeralPublic.sublist(1, 33)), _big(ephemeralPublic.sublist(33, 65))),
        _domain,
      );
      final agreement = ECDHBasicAgreement()..init(ECPrivateKey(_big(keys.d), _domain));
      final secret = _bytes(agreement.calculateAgreement(remote), length: 32);

      final hkdf = HKDFKeyDerivator(SHA256Digest())
        ..init(HkdfParameters(secret, 32, ephemeralPublic, _info));
      final key = Uint8List(32);
      hkdf.deriveKey(null, 0, key, 0);

      final gcm = GCMBlockCipher(AESEngine())
        ..init(false, AEADParameters(KeyParameter(key), 128, nonce, Uint8List.fromList(const [1])));
      final plain = gcm.process(cipherAndTag);

      final json = jsonDecode(utf8.decode(plain)) as Map<String, dynamic>;
      final data = <String, String>{};
      final raw = json['data'];
      if (raw is Map) {
        raw.forEach((k, v) => data[k.toString()] = v.toString());
      }
      return RelayMessage(
        title: (json['title'] ?? '').toString(),
        body: (json['body'] ?? '').toString(),
        data: data,
        timestamp: (json['ts'] as num?)?.toInt() ?? 0,
      );
    } catch (_) {
      return null;
    }
  }

  static BigInt _big(List<int> bytes) {
    var result = BigInt.zero;
    for (final b in bytes) {
      result = (result << 8) | BigInt.from(b);
    }
    return result;
  }

  static Uint8List _bytes(BigInt value, {int length = 32}) {
    final out = Uint8List(length);
    var v = value;
    for (var i = length - 1; i >= 0; i--) {
      out[i] = (v & BigInt.from(0xff)).toInt();
      v = v >> 8;
    }
    return out;
  }

  static List<int> _fromBase64Url(String value) {
    var s = value.trim().replaceAll('-', '+').replaceAll('_', '/');
    s = s.padRight(s.length + (4 - s.length % 4) % 4, '=');
    return base64.decode(s);
  }
}

/// Where the key pair and pairing credential are kept between runs.
abstract class RelayKeyStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

class SecureRelayKeyStore implements RelayKeyStore {
  const SecureRelayKeyStore();
  static const _storage = FlutterSecureStorage(
    iOptions: IOSOptions(accessibility: KeychainAccessibility.first_unlock_this_device),
  );

  @override
  Future<String?> read(String key) => _storage.read(key: key);
  @override
  Future<void> write(String key, String value) => _storage.write(key: key, value: value);
  @override
  Future<void> delete(String key) => _storage.delete(key: key);
}

/// Pairs this phone with the push relay and registers it with the user's own server.
///
/// 1. [pair] sends the Firebase push token to the relay and gets a pairing token back.
/// 2. The caller then registers `{platform: relay, token: pairingToken, publicKey}` with the
///    user's server (`POST /notifications/devices`); [registrationBody] builds that body.
class RelayPushService {
  RelayPushService({RelayKeyStore? store, Dio Function(String relayUrl)? dioFor})
      : _store = store ?? const SecureRelayKeyStore(),
        _dioFor = dioFor ??
            ((url) => Dio(BaseOptions(
                  baseUrl: url,
                  connectTimeout: const Duration(seconds: 10),
                  receiveTimeout: const Duration(seconds: 10),
                )));

  static const _keysKey = 'relay_push_keys_v1';
  static const _pairingKey = 'relay_push_pairing_v1';

  final RelayKeyStore _store;
  final Dio Function(String relayUrl) _dioFor;
  RelayKeyPair? _keys;

  /// This phone's key pair, made on first use and kept in secure storage.
  Future<RelayKeyPair> keys() async {
    if (_keys != null) return _keys!;
    final saved = await _store.read(_keysKey);
    if (saved != null) {
      try {
        return _keys = RelayKeyPair.fromJson(jsonDecode(saved) as Map<String, dynamic>);
      } catch (_) {
        // Unreadable: make a new pair. Old envelopes are lost, which is fine for a push.
      }
    }
    final fresh = await RelayPushCrypto.generateKeyPair();
    await _store.write(_keysKey, jsonEncode(fresh.toJson()));
    return _keys = fresh;
  }

  Future<String?> pairingToken() => _store.read(_pairingKey);

  /// Asks the relay to pair (or refresh) this phone and remembers the pairing token.
  Future<String> pair({
    required String relayUrl,
    required String pushToken,
    required String platform,
  }) async {
    final existing = await pairingToken();
    final response = await _dioFor(relayUrl).post<dynamic>('/v1/pair', data: {
      'platform': platform,
      'pushToken': pushToken,
      if (existing != null) 'pairingToken': existing,
    });
    final data = response.data;
    final token = data is Map ? data['pairingToken'] : null;
    if (token is! String || token.isEmpty) {
      throw StateError('The push relay did not return a pairing token.');
    }
    await _store.write(_pairingKey, token);
    return token;
  }

  /// The body for `POST /notifications/devices` on the user's server.
  Future<Map<String, dynamic>> registrationBody(String pairingToken) async => {
        'token': pairingToken,
        'platform': 'relay',
        'publicKey': (await keys()).publicKeyBase64,
      };

  /// The message inside a push the relay forwarded (`data['e']`), or null if it is not for us.
  Future<RelayMessage?> open(String envelope) async =>
      RelayPushCrypto.open(await keys(), envelope);

  /// Forgets the pairing (logout). The key pair stays: it identifies the phone, not the account.
  Future<void> clearPairing() => _store.delete(_pairingKey);
}

/// Singleton used by [PushNotificationService].
final relayPushService = RelayPushService();
