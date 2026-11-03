import 'dart:async';

import 'package:flutter/foundation.dart';

import 'app_config.dart';

/// What the server this app talks to can do, from `GET /api/system/version`.
///
/// The hosted service answers "hosted" with everything on. A self-hosted server has no billing, no
/// Plaid, no Google or Apple sign-in unless the operator set them up, and maybe no email, so the
/// app hides what would only fail. Until the answer arrives (and for any server that does not send
/// the fields) the app assumes hosted: the hosted service must never lose a screen to a missed
/// handshake.
@immutable
class ServerCapabilities {
  const ServerCapabilities({
    this.edition = 'hosted',
    this.googleSignIn = true,
    this.passkeys = true,
    this.email = true,
    this.pushRelayUrl,
    this.googleClientId,
    this.oidc = false,
    this.oidcDisplayName,
    this.passwordLogin = true,
  });

  final String edition;
  final bool googleSignIn;
  final bool passkeys;
  final bool email;

  /// Where this phone pairs for relayed push; null when the server has no relay.
  final String? pushRelayUrl;

  /// The operator's own Google web client id on a self-hosted server; null means use the build's.
  final String? googleClientId;

  /// Single sign-on with the operator's OpenID Connect provider (Pocket ID, Authentik, Keycloak).
  /// Self-host only; off unless the server says so.
  final bool oidc;

  /// What the single sign-on button names ("Sign in with Pocket ID"). Null when [oidc] is off.
  final String? oidcDisplayName;

  /// False when the operator turned password sign-in off in favour of single sign-on.
  final bool passwordLogin;

  static const hosted = ServerCapabilities();

  bool get isSelfHost => edition == 'selfhost';

  /// Plans, trials, upgrade prompts.
  bool get hasBilling => !isSelfHost;

  /// Plaid Link and the screens that manage bank connections.
  bool get hasBankConnection => !isSelfHost;

  /// The admin broadcast email screen; the server answers 404 on a self-hosted edition.
  bool get hasAdminBroadcast => !isSelfHost;

  /// Apple sign-in is not offered by a self-hosted server.
  bool get appleSignIn => !isSelfHost;

  /// True when at least one social sign-in button has anything to offer.
  bool get anySocialSignIn => googleSignIn || appleSignIn;

  factory ServerCapabilities.fromJson(Map<String, dynamic> json) {
    final features = json['features'];
    bool feature(String name) =>
        features is Map && features[name] is bool ? features[name] as bool : true;
    final relay = json['pushRelayUrl'];
    final gid = json['googleClientId'];
    final oidcName = json['oidcDisplayName'] ?? json['OidcDisplayName'];
    // Single sign-on is opt-in: a server that does not send the flag does not offer it.
    final oidcOn = features is Map && features['oidc'] == true;
    return ServerCapabilities(
      edition: (json['edition'] ?? 'hosted').toString(),
      googleSignIn: feature('googleSignIn'),
      passkeys: feature('passkeys'),
      email: feature('email'),
      pushRelayUrl: relay is String && relay.isNotEmpty ? relay : null,
      googleClientId: gid is String && gid.isNotEmpty ? gid : null,
      oidc: oidcOn,
      oidcDisplayName: oidcOn && oidcName is String && oidcName.trim().isNotEmpty
          ? oidcName.trim()
          : null,
      passwordLogin: feature('passwordLogin'),
    );
  }
}

/// The current answer, read by screens and the router. Set by the startup version check.
class ServerEdition {
  ServerEdition._();

  static final ValueNotifier<ServerCapabilities> current =
      ValueNotifier<ServerCapabilities>(ServerCapabilities.hosted);

  static Completer<void> _handshake = Completer<void>();
  static bool _handshakeStarted = false;

  /// True while a version handshake is in flight (started, not yet answered or failed).
  static bool get handshakePending => _handshakeStarted && !_handshake.isCompleted;

  /// Bumped whenever the handshake starts or ends, so screens can rebuild on that as well as on
  /// [current].
  static final ValueNotifier<int> _progress = ValueNotifier<int>(0);

  /// Fires when the server's answer arrives or the handshake starts or ends. Listen to this (not
  /// only [current]) for anything that must wait for the real answer.
  static Listenable get changes => Listenable.merge([current, _progress]);

  /// True while the server's answer is not known yet and showing the hosted default could be wrong:
  /// on a native app (the server may be self-hosted) while the handshake is in flight, and on a web
  /// page served by a self-hosted server (same-origin build) until the handshake has finished. The
  /// hosted web app never waits, so it never loses a button to a slow handshake.
  static bool get answerPending {
    if (_handshake.isCompleted) return false;
    if (AppConfig.isNonHostedServer) return true;
    return !kIsWeb && _handshakeStarted;
  }

  /// "Continue with Google" shows only for a server that offers it, and not before we know. A
  /// server the user picked must also name its own Google client: the build's client belongs to
  /// the hosted service and its tokens would be refused.
  static bool get showGoogleSignIn {
    if (answerPending || !current.value.googleSignIn) return false;
    if (AppConfig.isCustomServer && current.value.isSelfHost) {
      return current.value.googleClientId != null;
    }
    return true;
  }

  /// Passkeys as a sign-in step. On a phone talking to a server the user picked they are off:
  /// the server's passkey domain would have to match the domains this app is tied to.
  static bool get showPasskeys {
    if (!current.value.passkeys) return false;
    return !(AppConfig.isCustomServer && !kIsWeb);
  }

  /// "Continue with Apple" shows only for a server that offers it, and not before we know.
  static bool get showAppleSignIn => !answerPending && current.value.appleSignIn;

  /// "Sign in with (provider)" (single sign-on) shows only for a self-hosted server that offers it.
  static bool get showOidcSignIn =>
      !answerPending && current.value.isSelfHost && current.value.oidc;

  /// The email and password form. Hidden only when the server turned passwords off, and only once
  /// single sign-on is there to replace it, so nobody is ever left with no way in.
  static bool get showPasswordLogin =>
      current.value.passwordLogin || !showOidcSignIn;

  /// The name on the single sign-on button.
  static String get oidcDisplayName =>
      current.value.oidcDisplayName ?? 'Single sign-on';

  /// Called by the version check when it starts.
  static void markHandshakeStarted() {
    _handshakeStarted = true;
    _progress.value++;
  }

  /// Completes once the version handshake has answered or failed, so anything that depends on the
  /// server's capabilities (the Google client id) can wait for the real answer instead of the
  /// hosted default.
  static Future<void> get handshakeDone => _handshake.future;

  /// Called by the version check when it finishes, whatever the outcome.
  static void markHandshakeDone() {
    if (!_handshake.isCompleted) _handshake.complete();
    _progress.value++;
  }

  /// Takes [capabilities] as the answer for a server that was just chosen (the picker already
  /// asked it), so screens update right away without waiting for another handshake.
  static void adopt(ServerCapabilities capabilities) {
    current.value = capabilities;
    _handshakeStarted = true;
    markHandshakeDone();
  }

  @visibleForTesting
  static void resetHandshake() {
    _handshake = Completer<void>();
    _handshakeStarted = false;
    _progress.value++;
  }
}
