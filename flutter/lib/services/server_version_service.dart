import 'package:dio/dio.dart';

import '../config/app_config.dart';
import '../config/server_capabilities.dart';

/// What the version handshake with the server decided.
enum ServerCompatibility {
  /// The server and this app accept each other, or the check could not be made.
  ok,

  /// The server is older than this app needs. The owner has to update the server.
  serverNeedsUpdate,

  /// The server no longer supports this old app. The user has to update the app.
  appNeedsUpdate,
}

/// The answer of `GET /api/system/version`.
class ServerVersionInfo {
  final String version;
  final String minClientVersion;
  final String edition;

  /// Everything else the server reported (features, push relay), for [ServerCapabilities].
  final ServerCapabilities capabilities;

  const ServerVersionInfo({
    required this.version,
    required this.minClientVersion,
    required this.edition,
    this.capabilities = ServerCapabilities.hosted,
  });

  factory ServerVersionInfo.fromJson(Map<String, dynamic> json) =>
      ServerVersionInfo(
        version: (json['version'] ?? '').toString(),
        minClientVersion: (json['minClientVersion'] ?? '').toString(),
        edition: (json['edition'] ?? '').toString(),
        capabilities: ServerCapabilities.fromJson(json),
      );
}

/// Result of a check, with the numbers the screen quotes.
class VersionCheckResult {
  final ServerCompatibility compatibility;
  final String serverVersion;
  final String requiredServerVersion;
  final String requiredAppVersion;
  final String appVersion;

  const VersionCheckResult({
    required this.compatibility,
    this.serverVersion = '',
    this.requiredServerVersion = '',
    this.requiredAppVersion = '',
    this.appVersion = '',
  });

  static const ok = VersionCheckResult(compatibility: ServerCompatibility.ok);
}

/// Asks the server which version it is and which app versions it supports, before anyone signs in.
///
/// A self-hosted server can lag behind the app (the owner has not pulled the new image yet), and an
/// old app can lag behind the server. Both are caught here with a clear screen instead of a screen
/// full of broken requests.
///
/// The check never blocks on doubt. A network failure, a server that predates the endpoint (404), a
/// local 'dev' build, or a version string that is not X.Y.Z all answer [ServerCompatibility.ok]:
/// the hosted service must never lock users out because of a missed handshake.
class ServerVersionService {
  final Dio _dio;
  final String appVersion;
  final String minServerVersion;

  /// True when this service was given neither a Dio nor a base URL: it then asks whichever
  /// server the app is pointed at right now (the user can change it on the sign-in screen).
  final bool _followsAppServer;

  ServerVersionService({
    Dio? dio,
    String? baseUrl,
    String? appVersion,
    String? minServerVersion,
  })  : _dio = dio ??
            Dio(BaseOptions(
              baseUrl: baseUrl ?? AppConfig.baseUrl,
              connectTimeout: const Duration(seconds: 8),
              receiveTimeout: const Duration(seconds: 8),
            )),
        _followsAppServer = dio == null && baseUrl == null,
        appVersion = appVersion ?? AppConfig.version,
        minServerVersion = minServerVersion ?? AppConfig.minServerVersion;

  Future<VersionCheckResult> check() async {
    ServerEdition.markHandshakeStarted();
    try {
      return await _check();
    } finally {
      ServerEdition.markHandshakeDone();
    }
  }

  Future<VersionCheckResult> _check() async {
    final ServerVersionInfo info;
    final askedBaseUrl = _followsAppServer ? AppConfig.baseUrl : null;
    if (askedBaseUrl != null) _dio.options.baseUrl = askedBaseUrl;
    try {
      final response = await _dio.get<dynamic>('/system/version');
      final data = response.data;
      if (data is! Map) return VersionCheckResult.ok;
      info = ServerVersionInfo.fromJson(Map<String, dynamic>.from(data));
    } catch (_) {
      return VersionCheckResult.ok;
    }
    // The user switched servers while this was in flight: the answer belongs to the old one.
    if (askedBaseUrl != null && askedBaseUrl != AppConfig.baseUrl) return VersionCheckResult.ok;
    // Published before the compatibility verdict: even a server that needs an update tells us
    // what it is, and the screens behind the update notice should already match it.
    ServerEdition.current.value = info.capabilities;
    return evaluate(info);
  }

  /// Pure decision, separate from the network call so it can be tested.
  VersionCheckResult evaluate(ServerVersionInfo info) {
    final server = parseVersion(info.version);
    final app = parseVersion(appVersion);

    if (server != null && compareVersions(server, parseVersion(minServerVersion)) < 0) {
      return VersionCheckResult(
        compatibility: ServerCompatibility.serverNeedsUpdate,
        serverVersion: info.version,
        requiredServerVersion: minServerVersion,
        appVersion: appVersion,
      );
    }

    final minClient = parseVersion(info.minClientVersion);
    if (app != null && minClient != null && compareVersions(app, minClient) < 0) {
      return VersionCheckResult(
        compatibility: ServerCompatibility.appNeedsUpdate,
        serverVersion: info.version,
        requiredAppVersion: info.minClientVersion,
        appVersion: appVersion,
      );
    }

    return VersionCheckResult.ok;
  }

  /// "1.5.29", "v1.5.29" or "1.5.29+3" as [1, 5, 29]; null for anything else ('dev', '').
  static List<int>? parseVersion(String? value) {
    if (value == null) return null;
    final match = RegExp(r'^v?(\d{1,9})\.(\d{1,9})\.(\d{1,9})(?:[-+][0-9A-Za-z.+-]*)?$').firstMatch(value.trim());
    if (match == null) return null;
    return [for (var i = 1; i <= 3; i++) int.parse(match.group(i)!)];
  }

  /// Negative when [a] is older than [b]. A missing [b] counts as "no minimum".
  static int compareVersions(List<int> a, List<int>? b) {
    if (b == null) return 1;
    for (var i = 0; i < 3; i++) {
      if (a[i] != b[i]) return a[i].compareTo(b[i]);
    }
    return 0;
  }
}
