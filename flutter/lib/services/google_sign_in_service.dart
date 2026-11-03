import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb, visibleForTesting;
import 'package:google_sign_in/google_sign_in.dart';
import '../config/app_config.dart';
import '../config/server_capabilities.dart';

/// Google Sign-In Service
///
/// Wraps the google_sign_in package (v7) which uses Google Identity Services.
/// On web: Uses renderButton() widget + authenticationEvents stream.
/// On mobile: Uses authenticate() for interactive sign-in.
class GoogleSignInService {
  static GoogleSignInService? _instance;
  String? _initializedClientId;
  bool _followingServer = false;
  Future<void> _queue = Future<void>.value();

  /// How long to wait for the server handshake before using the build's client id.
  final Duration handshakeWait;

  GoogleSignInService._() : handshakeWait = const Duration(seconds: 3);

  @visibleForTesting
  GoogleSignInService.forTesting({this.handshakeWait = const Duration(milliseconds: 50)});

  static GoogleSignInService get instance {
    _instance ??= GoogleSignInService._();
    return _instance!;
  }

  /// Initialize the Google Sign-In plugin before use. Safe to call again: it only initializes
  /// again when the effective client id has changed since the last time. Calls run one at a
  /// time, so a late server answer and a sign-in tap never initialize twice for one id.
  Future<void> initialize() {
    final next = _queue.then((_) => _initializeOnce());
    _queue = next.catchError((Object _) {});
    return next;
  }

  /// Stop following server updates (tests create several instances against one notifier).
  @visibleForTesting
  void dispose() {
    if (_followingServer) ServerEdition.current.removeListener(_onServerChanged);
    _followingServer = false;
  }

  Future<void> _initializeOnce() async {
    // A handshake that answers after the wait below (the version request may take longer than
    // the wait) must still switch the plugin to the server's client id, so follow later updates.
    if (!_followingServer) {
      _followingServer = true;
      ServerEdition.current.addListener(_onServerChanged);
    }

    // The login page runs this before the version handshake has answered. Wait for it (bounded,
    // so an unreachable server cannot hold sign-in hostage) so a self-hosted server's own client
    // id is the one the plugin starts with.
    if (ServerEdition.handshakePending) {
      await ServerEdition.handshakeDone.timeout(handshakeWait, onTimeout: () {});
    }

    // A self-hosted server names its own client id; the build's id belongs to the hosted service
    // and would produce tokens the self-hosted server rejects.
    final clientId =
        ServerEdition.current.value.googleClientId ?? AppConfig.googleWebClientId;
    if (clientId.isEmpty) {
      throw Exception(
        'Google Web Client ID not configured. '
        'Set GOOGLE_WEB_CLIENT_ID environment variable or pass via --dart-define.',
      );
    }

    if (_initializedClientId == clientId) return;

    await GoogleSignIn.instance.initialize(
      clientId: kIsWeb ? clientId : null,
      serverClientId: kIsWeb ? null : clientId,
    );
    _initializedClientId = clientId;
  }

  void _onServerChanged() {
    final id = ServerEdition.current.value.googleClientId ?? AppConfig.googleWebClientId;
    if (_initializedClientId != null && id.isNotEmpty && id != _initializedClientId) {
      unawaited(initialize());
    }
  }

  /// Stream of authentication events (sign-in / sign-out).
  /// On web, renderButton triggers events on this stream.
  Stream<GoogleSignInAuthenticationEvent> get authenticationEvents =>
      GoogleSignIn.instance.authenticationEvents;

  /// Attempt lightweight authentication (FedCM, One Tap, session restore).
  /// On web, returns null (result comes async via authenticationEvents).
  Future<GoogleSignInAccount?>? attemptLightweightAuthentication() {
    return GoogleSignIn.instance.attemptLightweightAuthentication();
  }

  /// Interactive sign-in (mobile only — throws on web).
  /// On web, use renderButton() widget instead.
  Future<GoogleSignInAccount> authenticate() async {
    // Re-check the client id at the moment of sign-in, in case the server answered late.
    await initialize();
    return GoogleSignIn.instance.authenticate();
  }

  /// Sign out the current user.
  Future<void> signOut() => GoogleSignIn.instance.signOut();

  /// Disconnect (revoke access).
  Future<void> disconnect() => GoogleSignIn.instance.disconnect();
}

// Global singleton instance
final googleSignInService = GoogleSignInService.instance;
