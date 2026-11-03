import 'package:dio/dio.dart';

import '../config/app_config.dart';
import '../config/server_capabilities.dart';
import '../offline/offline_store.dart';
import 'api_client.dart';
import 'biometric_service.dart';
import 'server_version_service.dart';

/// Why a server address was turned down by [ServerSelectionService.probe].
enum ServerProbeError {
  /// The text is not a web address.
  invalidAddress,

  /// Nothing answered (wrong address, server down, no network, blocked http).
  unreachable,

  /// Something answered, but not a Buoy Money server.
  notBuoyServer,

  /// The server is older than this app needs.
  serverTooOld,

  /// The server needs a newer app.
  appTooOld,
}

/// The outcome of asking a server whether this app can use it.
class ServerProbeResult {
  const ServerProbeResult._({
    required this.origin,
    this.error,
    this.capabilities,
    this.version,
  });

  const ServerProbeResult.ok(String origin, ServerCapabilities capabilities, VersionCheckResult version)
      : this._(origin: origin, capabilities: capabilities, version: version);

  const ServerProbeResult.failed(String origin, ServerProbeError error, {VersionCheckResult? version})
      : this._(origin: origin, error: error, version: version);

  /// The normalized address that was asked.
  final String origin;
  final ServerProbeError? error;

  /// What the server can do; set on success.
  final ServerCapabilities? capabilities;

  /// Version numbers, for the "too old" messages.
  final VersionCheckResult? version;

  bool get ok => error == null;
}

/// Lets the user point the app at their own server, or back at BuoyAI Cloud.
///
/// Switching is only offered while signed out. Anything tied to the old server (tokens, the
/// offline cache, saved biometric sign-in) is cleared, so nothing from one server is sent to or
/// shown for another.
class ServerSelectionService {
  ServerSelectionService({
    Dio Function(String origin)? dioFor,
    Future<void> Function()? clearServerState,
    void Function()? applyBaseUrl,
    this.appVersion,
    this.minServerVersion,
  })  : _dioFor = dioFor ?? _defaultDio,
        _clearServerState = clearServerState ?? _clearEverything,
        _applyBaseUrl = applyBaseUrl ?? (() => apiClient.applyBaseUrl());

  final Dio Function(String origin) _dioFor;
  final Future<void> Function() _clearServerState;
  final void Function() _applyBaseUrl;

  /// Overrides for tests; the app uses the build's numbers.
  final String? appVersion;
  final String? minServerVersion;

  static Dio _defaultDio(String origin) => Dio(BaseOptions(
        baseUrl: '$origin/api',
        connectTimeout: const Duration(seconds: 6),
        receiveTimeout: const Duration(seconds: 6),
        sendTimeout: const Duration(seconds: 6),
      ));

  static Future<void> _clearEverything() async {
    await apiClient.clearAll();
    await wipeOfflineCache();
    // Saved quick sign-in holds a password for the old server; never send it to a new one.
    try {
      await biometricService.disableBiometricLogin();
    } catch (_) {}
  }

  /// Asks `GET <origin>/api/system/version` and decides whether this app can use the server.
  /// [input] is what the user typed; it is normalized first.
  Future<ServerProbeResult> probe(String input) async {
    final origin = AppConfig.normalizeServerOrigin(input);
    if (origin == null) {
      return ServerProbeResult.failed(input.trim(), ServerProbeError.invalidAddress);
    }

    final ServerVersionInfo info;
    try {
      final response = await _dioFor(origin).get<dynamic>('/system/version');
      final data = response.data;
      if (data is! Map || data['version'] is! String || data['edition'] is! String) {
        return ServerProbeResult.failed(origin, ServerProbeError.notBuoyServer);
      }
      info = ServerVersionInfo.fromJson(Map<String, dynamic>.from(data));
    } on DioException catch (e) {
      // An answer that is not the version endpoint (404 page, proxy error) means the address
      // reaches something, just not a Buoy Money server.
      final reached = e.response != null || e.type == DioExceptionType.badResponse;
      return ServerProbeResult.failed(
          origin, reached ? ServerProbeError.notBuoyServer : ServerProbeError.unreachable);
    } catch (_) {
      return ServerProbeResult.failed(origin, ServerProbeError.notBuoyServer);
    }

    final verdict = ServerVersionService(
      dio: Dio(),
      appVersion: appVersion,
      minServerVersion: minServerVersion,
    ).evaluate(info);
    return switch (verdict.compatibility) {
      ServerCompatibility.serverNeedsUpdate =>
        ServerProbeResult.failed(origin, ServerProbeError.serverTooOld, version: verdict),
      ServerCompatibility.appNeedsUpdate =>
        ServerProbeResult.failed(origin, ServerProbeError.appTooOld, version: verdict),
      ServerCompatibility.ok => ServerProbeResult.ok(origin, info.capabilities, verdict),
    };
  }

  /// Makes [origin] the server (null, or the build's own address, means BuoyAI Cloud) and
  /// publishes [capabilities] so the screens follow at once. Clears everything tied to the old
  /// server first. Call only while signed out.
  Future<void> useServer(String? origin,
      {ServerCapabilities capabilities = ServerCapabilities.hosted}) async {
    var target = origin == null ? null : AppConfig.normalizeServerOrigin(origin);
    if (target != null && '$target/api' == AppConfig.defaultBaseUrl) target = null;

    await _clearServerState();
    await AppConfig.saveCustomServer(target);
    _applyBaseUrl();
    ServerEdition.adopt(target == null ? ServerCapabilities.hosted : capabilities);
  }
}

/// Global instance; tests swap in their own.
ServerSelectionService serverSelectionService = ServerSelectionService();
