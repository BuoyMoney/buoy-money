import 'dart:convert';
import 'dart:math';
import 'dart:async';
import 'package:device_info_plus/device_info_plus.dart';
import 'package:flutter/foundation.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'package:crypto/crypto.dart';
import 'package:sign_in_with_apple/sign_in_with_apple.dart';
import '../config/app_config.dart';

/// Apple Sign-In Service
///
/// Wraps the sign_in_with_apple package to provide OAuth tokens for backend authentication.
///
/// Configuration:
/// - iOS/macOS: native flow. Requires "Sign in with Apple" capability in Xcode.
///   Audience (`aud`) of the token is the bundle ID `com.buoyai.buoyAi`.
/// - Web: Services-ID flow via Apple's JS SDK (popup). Gated behind
///   [AppConfig.isAppleWebConfigured] so the button only appears once a real
///   Services ID is baked into the build. Audience is the Services ID.
/// - Android: not supported (button stays hidden).
///
/// Apple Sign-In provides:
/// - Identity token (JWT) for backend verification
/// - Authorization code for server-to-server verification
/// - User info (email, name) - only on FIRST sign-in!
class AppleSignInService {
  static AppleSignInService? _instance;
  
  AppleSignInService._();
  
  static AppleSignInService get instance {
    _instance ??= AppleSignInService._();
    return _instance!;
  }
  
  /// Check if Apple Sign-In is available on this device.
  /// - Web: available only when a Services ID is configured
  ///   ([AppConfig.isAppleWebConfigured]) — keeps the button hidden until the
  ///   Apple portal setup is done, so we never ship a button that errors.
  /// - Native "Sign in with Apple" is offered on iOS 13+ / macOS 10.15+ only.
  /// Uses `kIsWeb`/`defaultTargetPlatform` (not `dart:io`) so this file is safe
  /// to import from the shared web+mobile login page (see .claude/rules/flutter.md).
  Future<bool> isAvailable() async {
    if (kIsWeb) return AppConfig.isAppleWebConfigured;
    if (defaultTargetPlatform != TargetPlatform.iOS &&
        defaultTargetPlatform != TargetPlatform.macOS) {
      return false;
    }
    return await SignInWithApple.isAvailable();
  }
  
  /// Generate a random nonce for security
  /// The nonce is included in the identity token and verified by the backend
  String _generateNonce([int length = 32]) {
    const charset = '0123456789ABCDEFGHIJKLMNOPQRSTUVXYZabcdefghijklmnopqrstuvwxyz-._';
    final random = Random.secure();
    return List.generate(length, (_) => charset[random.nextInt(charset.length)]).join();
  }
  
  /// Create SHA256 hash of the nonce
  String _sha256ofString(String input) {
    final bytes = utf8.encode(input);
    final digest = sha256.convert(bytes);
    return digest.toString();
  }
  
  /// Sign in with Apple and return the credentials.
  /// Routes to the web (Services ID) or native (bundle ID) flow automatically.
  /// Returns null if sign-in was cancelled.
  Future<AppleSignInResult?> signIn() async {
    final rawNonce = _generateNonce();

    if (kIsWeb) {
      return _signInWeb(rawNonce);
    }

    try {
      // Native (iOS/macOS): Apple pins the token's `nonce` claim to the SHA-256
      // of what we pass, so send the hash to Apple and the raw value to the
      // backend (which compares raw-or-SHA256).
      final nonce = _sha256ofString(rawNonce);

      final credential = await SignInWithApple.getAppleIDCredential(
        scopes: [
          AppleIDAuthorizationScopes.email,
          AppleIDAuthorizationScopes.fullName,
        ],
        nonce: nonce,
      );

      // Check we got the identity token
      if (credential.identityToken == null) {
        throw Exception('Failed to get identity token from Apple Sign-In');
      }

      return AppleSignInResult(
        identityToken: credential.identityToken!,
        authorizationCode: credential.authorizationCode,
        rawNonce: rawNonce, // Send raw nonce to backend for verification
        email: credential.email,
        givenName: credential.givenName,
        familyName: credential.familyName,
        userIdentifier: credential.userIdentifier ?? '',
      );
    } on SignInWithAppleAuthorizationException catch (e) {
      final mapped = mapAppleSignInError(e);
      if (mapped == null) return null; // user cancelled: not an error
      unawaited(reportAppleSignInFailure(e));
      throw mapped;
    } catch (e) {
      if (e is AppleSignInUnavailableException) rethrow;
      unawaited(reportAppleSignInFailure(e));
      throw AppleSignInUnavailableException(_codeName(e));
    }
  }

  /// Web ("Services ID") flow via Apple's JS SDK popup.
  ///
  /// Requires [AppConfig.appleServicesId] (the OAuth client_id for web) and a
  /// [AppConfig.appleWebRedirectUri] that exactly matches a registered Return
  /// URL on the verified domain. The `AppleID` global from Apple's SDK
  /// (loaded in web/index.html) must be present.
  ///
  /// The RAW nonce is passed here (not pre-hashed): Apple echoes it into the
  /// id_token's `nonce` claim, and the backend accepts either the raw value or
  /// its SHA-256, so verification holds regardless of Apple's hashing.
  Future<AppleSignInResult?> _signInWeb(String rawNonce) async {
    try {
      final credential = await SignInWithApple.getAppleIDCredential(
        scopes: [
          AppleIDAuthorizationScopes.email,
          AppleIDAuthorizationScopes.fullName,
        ],
        nonce: rawNonce,
        webAuthenticationOptions: WebAuthenticationOptions(
          clientId: AppConfig.appleServicesId,
          redirectUri: Uri.parse(AppConfig.appleWebRedirectUri),
        ),
      );

      if (credential.identityToken == null) {
        throw Exception('Failed to get identity token from Apple Sign-In (web)');
      }

      return AppleSignInResult(
        identityToken: credential.identityToken!,
        authorizationCode: credential.authorizationCode,
        rawNonce: rawNonce,
        email: credential.email,
        givenName: credential.givenName,
        familyName: credential.familyName,
        // The web SDK does not surface a stable userIdentifier; the backend
        // derives identity from the token's `sub` claim instead.
        userIdentifier: credential.userIdentifier ?? '',
      );
    } on SignInWithAppleCredentialsException catch (e) {
      // The web SDK funnels JS errors (including the user closing/cancelling the
      // popup) through this exception. Treat cancellation as a no-op.
      final msg = e.message.toLowerCase();
      if (msg.contains('popup_closed') ||
          msg.contains('closed') ||
          msg.contains('cancel')) {
        return null;
      }
      unawaited(reportAppleSignInFailure(e));
      throw AppleSignInUnavailableException('web_credentials');
    } catch (e) {
      // Anything else (e.g. a null identity token) gets the same friendly
      // wrapping and Sentry report as the native path.
      if (e is AppleSignInUnavailableException) rethrow;
      unawaited(reportAppleSignInFailure(e));
      throw wrapAppleWebFailure(e);
    }
  }
}

/// Thrown by [AppleSignInService.signIn] for any non-cancel failure. Carries
/// only a short code, never raw platform text, so UI can show a friendly
/// localized message without leaking exception strings.
class AppleSignInUnavailableException implements Exception {
  final String code;
  AppleSignInUnavailableException(this.code);
  @override
  String toString() => 'AppleSignInUnavailableException($code)';
}

/// Maps an unexpected web-flow failure to the friendly exception.
@visibleForTesting
AppleSignInUnavailableException wrapAppleWebFailure(Object e) =>
    e is AppleSignInUnavailableException
        ? e
        : AppleSignInUnavailableException('web_failure');

String _codeName(Object e) {
  if (e is SignInWithAppleAuthorizationException) return e.code.name;
  return e.runtimeType.toString();
}

/// Maps an Apple sign-in error. Returns null when the user cancelled (stay
/// silent); otherwise an [AppleSignInUnavailableException] for the UI to show
/// as a friendly message. Error 1000 (`unknown`) is typically a device state
/// issue (no Apple ID / iCloud, restrictions), not an app bug.
AppleSignInUnavailableException? mapAppleSignInError(Object e) {
  if (e is SignInWithAppleAuthorizationException) {
    if (e.code == AuthorizationErrorCode.canceled) return null;
    return AppleSignInUnavailableException(e.code.name);
  }
  return AppleSignInUnavailableException(_codeName(e));
}

/// Reports the real failure to Sentry with non-personal device context.
/// Never throws.
Future<void> reportAppleSignInFailure(Object e, [StackTrace? st]) async {
  try {
    final tags = <String, String>{
      'apple_signin_code': _codeName(e),
      'platform': defaultTargetPlatform.name,
    };
    final ctx = <String, dynamic>{};
    if (!kIsWeb && defaultTargetPlatform == TargetPlatform.iOS) {
      try {
        final info = await DeviceInfoPlugin().iosInfo;
        tags['ios_version'] = info.systemVersion;
        tags['device_model'] = info.model; // "iPad" or "iPhone"
        ctx['hardware'] = info.utsname.machine; // e.g. iPad13,1 (not personal)
      } catch (_) {}
      final views = PlatformDispatcher.instance.views;
      if (views.isNotEmpty) {
        final v = views.first;
        final shortest = (v.physicalSize.shortestSide / v.devicePixelRatio);
        tags['ipad_layout'] = (shortest >= 600).toString();
        ctx['shortest_side_dp'] = shortest.round();
      }
    }
    if (e is SignInWithAppleAuthorizationException) {
      // message may be platform text; no personal data in it
      ctx['message'] = e.message;
    }
    await Sentry.captureException(
      e,
      stackTrace: st,
      withScope: (scope) {
        tags.forEach(scope.setTag);
        scope.setContexts('apple_signin', ctx);
      },
    );
  } catch (_) {}
}

/// Result from a successful Apple Sign-In
class AppleSignInResult {
  /// The identity token (JWT) to send to the backend for verification
  final String identityToken;
  
  /// Authorization code for server-to-server verification
  final String authorizationCode;
  
  /// Raw nonce used for token verification
  final String rawNonce;
  
  /// User's email (only provided on FIRST sign-in, then null)
  final String? email;
  
  /// User's given/first name (only provided on FIRST sign-in)
  final String? givenName;
  
  /// User's family/last name (only provided on FIRST sign-in)
  final String? familyName;
  
  /// Apple's unique user identifier (stable across sign-ins)
  final String userIdentifier;
  
  AppleSignInResult({
    required this.identityToken,
    required this.authorizationCode,
    required this.rawNonce,
    this.email,
    this.givenName,
    this.familyName,
    required this.userIdentifier,
  });
  
  /// Get full name if available
  String? get fullName {
    if (givenName == null && familyName == null) return null;
    return '${givenName ?? ''} ${familyName ?? ''}'.trim();
  }
}

// Global singleton instance
final appleSignInService = AppleSignInService.instance;
