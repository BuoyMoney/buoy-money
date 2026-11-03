import 'package:flutter/foundation.dart' show kIsWeb, defaultTargetPlatform, TargetPlatform, visibleForTesting;
import 'package:shared_preferences/shared_preferences.dart';

import 'app_brand.dart';

/// Application configuration
/// 
/// Environment is determined by build-time constant or can be set for testing.
/// - Development: Connects to localhost (web) or 10.0.2.2 (Android emulator)
/// - Staging: Connects to staging.buoy-ai.com
/// - Production: Connects to buoy-ai.com
class AppConfig {
  // Environment constants
  static const String _buildEnvironment = String.fromEnvironment(
    'ENVIRONMENT',
    defaultValue: 'development',
  );
  
  // Allow runtime override for testing
  static String? _runtimeEnvironment;
  
  static String get environment => _runtimeEnvironment ?? _buildEnvironment;
  
  static set environment(String value) => _runtimeEnvironment = value;
  
  // Base URLs for each environment
  static const String _devBaseUrlEmulator = 'http://10.0.2.2:5000/api';
  static const String _devBaseUrlWeb = 'http://localhost:5000/api';
  static const String _stagingBaseUrl = 'https://staging.buoy-ai.com/api';
  static const String _prodBaseUrl = 'https://buoy-ai.com/api';

  // Override: point this build at a specific API, e.g. a second local backend
  // on another port: --dart-define=API_BASE_URL=http://localhost:5100/api.
  // Empty default = fall through to the environment-derived URL below.
  static const String _apiBaseUrlOverride = String.fromEnvironment(
    'API_BASE_URL',
    defaultValue: '',
  );

  // Get the appropriate base URL based on environment
  static String get baseUrl {
    final custom = customServerOrigin;
    if (custom != null) return '$custom/api';
    return defaultBaseUrl;
  }

  /// The API this build talks to when the user has not picked a server of their own.
  static String get defaultBaseUrl {
    // Self-host web build: the API is served by the same process that served this page, under
    // /api on whatever address the browser used (a LAN IP, a Tailscale name, a domain), so the
    // build bakes in API_BASE_URL=same-origin instead of a hostname.
    if (_apiBaseUrlOverride == sameOrigin) return _sameOriginBaseUrl();
    if (_apiBaseUrlOverride.isNotEmpty) return _apiBaseUrlOverride;
    return switch (environment) {
      'production' => _prodBaseUrl,
      'staging' => _stagingBaseUrl,
      _ => kIsWeb ? _devBaseUrlWeb : _devBaseUrlEmulator,
    };
  }
  
  /// True for the web build a self-hosted server serves itself (API_BASE_URL=same-origin): the
  /// server behind it is not the hosted service, so its capabilities must be asked before the
  /// screens assume them.
  static bool get isSameOriginBuild => sameOriginBuildForTest ?? _apiBaseUrlOverride == sameOrigin;

  /// Lets a test act as the self-host web build, which is a compile-time define in the real app.
  @visibleForTesting
  static bool? sameOriginBuildForTest;

  /// Value of API_BASE_URL meaning "the origin this web page was loaded from".
  static const String sameOrigin = 'same-origin';

  // ---------------------------------------------------------------------------------------------
  // Custom server (chosen at runtime on the sign-in screen, like Immich or Bitwarden)
  // ---------------------------------------------------------------------------------------------

  /// SharedPreferences key holding the chosen server, e.g. `https://buoy.example.ts.net`.
  static const String customServerPrefKey = 'custom_server_origin';

  static String? _customServerOrigin;

  /// The self-hosted server the user picked (scheme, host, port, optional path; no trailing
  /// slash and no `/api`), or null for the build's own server. Always null on the web: the picker
  /// is for the phone apps (a web page talks to the server it came from).
  static String? get customServerOrigin => canChooseServer ? _customServerOrigin : null;

  /// True when the user pointed this app at a server of their own.
  static bool get isCustomServer => customServerOrigin != null;

  /// True when the server behind this app is not the hosted service (or might not be): the
  /// self-host web build, or a server the user picked. Its capabilities must be asked before the
  /// screens assume them.
  static bool get isNonHostedServer => isSameOriginBuild || isCustomServer;

  /// The server picker is offered in the phone and desktop apps only, never on the web.
  static bool get canChooseServer => !_isWeb && !isSameOriginBuild;

  static bool get _isWeb => webBuildForTest ?? kIsWeb;

  /// Lets a test act as a web build for the server picker (kIsWeb is a compile-time constant).
  @visibleForTesting
  static bool? webBuildForTest;

  /// True for a build pinned to one API with --dart-define=API_BASE_URL (a local slot, a film build, the
  /// self-host web build). Such a build has its server decided already, so it never asks where to connect.
  static bool get hasFixedServer => fixedServerForTest ?? _apiBaseUrlOverride.isNotEmpty;

  /// Lets a test act as a build pinned to one API (a compile-time define in the real app).
  @visibleForTesting
  static bool? fixedServerForTest;

  /// Host of the build's own server, for the "BuoyAI Cloud (buoy-ai.com)" label.
  static String get defaultServerHost => Uri.tryParse(defaultBaseUrl)?.host ?? '';

  /// Reads the saved server. Call once in main() before the first API request. Never throws.
  static Future<void> loadCustomServer() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      final saved = prefs.getString(customServerPrefKey);
      _customServerOrigin = saved == null ? null : normalizeServerOrigin(saved);
    } catch (_) {
      _customServerOrigin = null;
    }
  }

  /// Saves [origin] as the server to use (null goes back to the build's own server). Callers
  /// must also point the API client at it and clear anything tied to the old server.
  static Future<void> saveCustomServer(String? origin) async {
    final normalized = origin == null ? null : normalizeServerOrigin(origin);
    _customServerOrigin = normalized;
    final prefs = await SharedPreferences.getInstance();
    if (normalized == null) {
      await prefs.remove(customServerPrefKey);
    } else {
      await prefs.setString(customServerPrefKey, normalized);
    }
  }

  /// Sets the chosen server in memory only.
  @visibleForTesting
  static set customServerOriginForTest(String? origin) => _customServerOrigin = origin;

  /// Turns what a person types into a server origin, or null when it cannot be one.
  ///
  /// `buoy.example.ts.net` becomes `https://buoy.example.ts.net`; a trailing slash, `/api` or
  /// `/app` is dropped (people paste the app or API address as often as the root). Only http and
  /// https are accepted. A sub-path (`https://example.com/buoy`) is kept.
  static String? normalizeServerOrigin(String input) {
    var text = input.trim();
    if (text.isEmpty || text.contains(' ')) return null;
    if (!text.contains('://')) text = 'https://$text';
    final uri = Uri.tryParse(text);
    if (uri == null) return null;
    final scheme = uri.scheme.toLowerCase();
    if (scheme != 'http' && scheme != 'https') return null;
    if (uri.host.isEmpty || uri.userInfo.isNotEmpty) return null;

    final segments = uri.pathSegments.where((s) => s.isNotEmpty).toList();
    while (segments.isNotEmpty &&
        (segments.last.toLowerCase() == 'api' || segments.last.toLowerCase() == 'app')) {
      segments.removeLast();
    }
    final host = uri.host.toLowerCase();
    final hostPart = host.contains(':') ? '[$host]' : host;
    final port = uri.hasPort ? ':${uri.port}' : '';
    final path = segments.isEmpty ? '' : '/${segments.join('/')}';
    return '$scheme://$hostPart$port$path';
  }

  /// True for an http origin, where passwords would travel unencrypted.
  static bool isInsecureOrigin(String origin) => origin.toLowerCase().startsWith('http://');

  static String _sameOriginBaseUrl() => '${Uri.base.origin}/api';

  /// Release version of this build (--dart-define=APP_VERSION=X.Y.Z); 'dev' for local builds.
  static const String version = String.fromEnvironment(
    'APP_VERSION',
    defaultValue: 'dev',
  );

  /// Oldest server this build can talk to. Raise it when the app starts needing a newer API;
  /// a self-hosted server older than this shows the "server needs an update" screen.
  /// Override with --dart-define=MIN_SERVER_VERSION=X.Y.Z.
  static const String minServerVersion = String.fromEnvironment(
    'MIN_SERVER_VERSION',
    defaultValue: '1.0.0',
  );

  // Environment checks
  static bool get isProduction => environment == 'production';
  static bool get isStaging => environment == 'staging';
  static bool get isDevelopment => environment == 'development';
  
  // API timeout in milliseconds
  static const int defaultTimeout = 30000; // 30 seconds
  static const int extendedTimeout = 45000; // 45 seconds for AI operations
  
  // App info
  static const String appName = AppBrand.name;
  static const String appVersion = '1.0.0';
  
  // Google OAuth Web Client ID
  // On web: used as clientId for Google Identity Services
  // On mobile: used as serverClientId to request backend-verifiable ID tokens
  // Override via: --dart-define=GOOGLE_WEB_CLIENT_ID=xxx
  static const String googleWebClientId = String.fromEnvironment(
    'GOOGLE_WEB_CLIENT_ID',
    defaultValue: '841207882659-kink79fsnp2rvtt44cmur4ss7cuuoibm.apps.googleusercontent.com',
  );

  // Sign in with Apple — Web ("Services ID" flow)
  // Web uses an Apple "Services ID" as the OAuth client_id (distinct from the
  // native iOS bundle ID `com.buoyai.buoyAi`). Empty by default so the web Apple
  // button stays HIDDEN until a real Services ID is baked into the build — no
  // broken button ships before the Apple portal + domain verification are done.
  // Override via: --dart-define=APPLE_SERVICES_ID=com.buoyai.web
  static const String appleServicesId = String.fromEnvironment(
    'APPLE_SERVICES_ID',
    defaultValue: '',
  );

  // The Return URL registered against the Services ID above. Must EXACTLY match
  // one of the "Return URLs" configured on the Apple Services ID and live under
  // the domain verified via apple-developer-domain-association.txt. Apple uses
  // popup (postMessage) mode, so NO live handler is required at this path — it
  // is only an identifier Apple validates against the registered Return URLs.
  // Matches the URL registered in the Apple portal for this Services ID.
  // Override via: --dart-define=APPLE_WEB_REDIRECT_URI=https://buoy-ai.com/api/auth/apple/callback
  static const String appleWebRedirectUri = String.fromEnvironment(
    'APPLE_WEB_REDIRECT_URI',
    defaultValue: 'https://buoy-ai.com/api/auth/apple/callback',
  );

  // RevenueCat PUBLIC SDK keys (safe to ship; they only identify the project).
  // Empty means "no in-app purchases on this platform": the billing page keeps
  // the web link-out. iOS ships with the Apple key; Android stays empty until
  // Play Billing is turned on. Override via --dart-define=REVENUECAT_APPLE_KEY=appl_…
  static const String revenueCatAppleKey = String.fromEnvironment(
    'REVENUECAT_APPLE_KEY',
    defaultValue: '',
  );
  static const String revenueCatGoogleKey = String.fromEnvironment(
    'REVENUECAT_GOOGLE_KEY',
    defaultValue: '',
  );

  /// True when the web "Sign in with Apple" (Services ID) flow is configured.
  /// Gates the web Apple button so it only appears once a real Services ID is
  /// injected at build time. Always false on non-web platforms (they use the
  /// native bundle-ID flow instead).
  static bool get isAppleWebConfigured =>
      kIsWeb && appleServicesId.isNotEmpty;

  // Client identification for API requests
  static String get clientType {
    if (kIsWeb) return 'web';
    return switch (defaultTargetPlatform) {
      TargetPlatform.android => 'mobile-android',
      TargetPlatform.iOS => 'mobile-ios',
      TargetPlatform.macOS => 'desktop-macos',
      TargetPlatform.windows => 'desktop-windows',
      TargetPlatform.linux => 'desktop-linux',
      _ => 'unknown',
    };
  }
}
