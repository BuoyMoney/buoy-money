import 'dart:convert';
import 'dart:math';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:url_launcher/url_launcher.dart';

import '../config/app_config.dart';

/// Opens a page in the browser; returns false when it could not.
typedef OidcLauncher = Future<bool> Function(Uri uri);

/// Single sign-on with a self-hosted server's OpenID Connect provider (Pocket ID, Authentik,
/// Keycloak).
///
/// The server runs the provider round trip. The app only starts it and finishes it:
/// - web: the page navigates to `/api/auth/oidc/start?platform=web`; the server sends the browser
///   back to `/app/auth/oidc?code=...`.
/// - phone: the system browser opens the same start address with `platform=mobile`; the server
///   sends it back to the `com.buoyai.buoymoney://oidc?code=...` deep link. The app adds its own
///   PKCE pair, so a code caught by another app is useless without the verifier kept here.
///
/// Either way the app then trades the one-time code at `/api/auth/oidc/exchange`.
class OidcSignInService {
  OidcSignInService({FlutterSecureStorage? storage, OidcLauncher? launcher})
      : _storage = storage ?? const FlutterSecureStorage(),
        _launcher = launcher;

  /// The deep-link scheme the server sends a phone back to. Registered in AndroidManifest.xml
  /// and Info.plist.
  static const mobileScheme = 'com.buoyai.buoymoney';

  static const _verifierKey = 'oidc_code_verifier';

  final FlutterSecureStorage _storage;
  final OidcLauncher? _launcher;

  /// Fallback when secure storage is unavailable.
  String? _memoryVerifier;

  /// The server address that starts a sign-in.
  Uri startUri({required bool web, String? invite, String? codeChallenge}) {
    final base = Uri.parse('${AppConfig.baseUrl}/auth/oidc/start');
    return base.replace(queryParameters: {
      'platform': web ? 'web' : 'mobile',
      if (invite != null && invite.trim().isNotEmpty) 'invite': invite.trim(),
      if (codeChallenge != null) 'code_challenge': codeChallenge,
    });
  }

  /// Leaves for the provider. Returns false when the browser could not be opened.
  Future<bool> begin({String? invite}) async {
    if (kIsWeb) {
      return _launch(startUri(web: true, invite: invite), web: true);
    }
    final verifier = newVerifier();
    await _saveVerifier(verifier);
    return _launch(
      startUri(
          web: false, invite: invite, codeChallenge: challengeFor(verifier)),
      web: false,
    );
  }

  /// The verifier saved by [begin], removed so it is used once. Null on the web.
  Future<String?> takeVerifier() async {
    if (kIsWeb) return null;
    String? value;
    try {
      value = await _storage.read(key: _verifierKey);
      await _storage.delete(key: _verifierKey);
    } catch (_) {
      // Secure storage unavailable: fall back to memory.
    }
    value ??= _memoryVerifier;
    _memoryVerifier = null;
    return value;
  }

  Future<void> _saveVerifier(String verifier) async {
    _memoryVerifier = verifier;
    try {
      await _storage.write(key: _verifierKey, value: verifier);
    } catch (_) {
      // Memory copy above still works while the app stays alive.
    }
  }

  Future<bool> _launch(Uri uri, {required bool web}) async {
    try {
      final custom = _launcher;
      if (custom != null) return await custom(uri);
      if (web) return await launchUrl(uri, webOnlyWindowName: '_self');
      return await launchUrl(uri, mode: LaunchMode.externalApplication);
    } catch (_) {
      return false;
    }
  }

  /// A fresh PKCE verifier: 32 random bytes, base64url without padding (43 characters).
  static String newVerifier() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return _base64Url(bytes);
  }

  /// The S256 challenge for [verifier].
  static String challengeFor(String verifier) =>
      _base64Url(sha256.convert(ascii.encode(verifier)).bytes);

  static String _base64Url(List<int> bytes) =>
      base64Url.encode(bytes).replaceAll('=', '');

  /// True for the phone app's single sign-on deep link.
  static bool isCallbackLink(Uri uri) =>
      uri.scheme == mobileScheme && uri.host == 'oidc';
}

/// Shared instance.
final oidcSignInService = OidcSignInService();
