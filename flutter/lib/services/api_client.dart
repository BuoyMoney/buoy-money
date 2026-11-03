import 'dart:async';
import 'dart:convert';

import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart' show kIsWeb, visibleForTesting;
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:sentry_dio/sentry_dio.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../config/app_config.dart';
import '../offline/connectivity_service.dart';
import '../offline/network_error.dart';
import '../offline/offline_status.dart';
import '../offline/offline_store.dart';
import '../utils/link_url_scrubber.dart';
import 'web_credentials.dart' if (dart.library.html) 'web_credentials_web.dart';

/// Platform-aware storage for auth material.
///
/// Mobile: everything lives in the OS keychain/keystore (encrypted, isolated from
/// web-style XSS).
///
/// Web (F1-tok hardening): the long-lived refresh token is NOT stored here at all —
/// it is an HttpOnly cookie the JavaScript can't read. The short-lived access token
/// and operation token are kept in memory only (never localStorage), so an XSS
/// payload can't exfiltrate a persistent credential; they are re-derived from the
/// refresh cookie via `/auth/refresh` on reload.
class _PlatformStorage {
  // iOS keychain accessibility: `first_unlock_this_device` — readable after the first unlock
  // following boot (so background token refresh / offline sync works while the screen is
  // locked), but device-bound and NOT included in iCloud Keychain backups, so auth tokens
  // can't be restored onto another device. (Default is `unlocked`, which is unreadable while
  // locked and iCloud-backup eligible.)
  final FlutterSecureStorage _secure = const FlutterSecureStorage(
    iOptions: IOSOptions(
      accessibility: KeychainAccessibility.first_unlock_this_device,
    ),
  );
  Future<void>? _migrationFuture;

  /// Keys held only in memory on web (never in JS-readable storage).
  static const Set<String> _webMemoryKeys = {'authToken', 'operationToken'};

  /// Keys never stored client-side on web (delivered via HttpOnly cookie).
  static const Set<String> _webCookieKeys = {'refreshToken'};

  static final Map<String, String> _webMemory = {};

  /// One-time migration from SharedPreferences → FlutterSecureStorage (legacy mobile).
  /// On web it also purges any previously-persisted access/operation token so no stale
  /// credential lingers in localStorage under the new in-memory model.
  Future<void> _migrateFromSharedPrefs() {
    return _migrationFuture ??= _doMigration();
  }

  Future<void> _doMigration() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      for (final key in ['authToken', 'userData', 'operationToken']) {
        if (kIsWeb && _webMemoryKeys.contains(key)) {
          // Never persist these on web — clean up any prior copies.
          await prefs.remove(key);
          await _secure.delete(key: key);
          continue;
        }
        final value = prefs.getString(key);
        if (value != null) {
          final existing = await _secure.read(key: key);
          if (existing == null) {
            await _secure.write(key: key, value: value);
          }
          await prefs.remove(key);
        }
      }
    } catch (_) {
      // Best-effort: don't break app if migration fails
    }
  }

  Future<void> write({required String key, required String value}) async {
    if (kIsWeb) {
      if (_webCookieKeys.contains(key)) return; // cookie-managed; never store client-side
      if (_webMemoryKeys.contains(key)) {
        _webMemory[key] = value;
        return;
      }
    }
    await _secure.write(key: key, value: value);
  }

  Future<String?> read({required String key}) async {
    if (kIsWeb) {
      if (_webCookieKeys.contains(key)) return null;
      if (_webMemoryKeys.contains(key)) return _webMemory[key];
    }
    await _migrateFromSharedPrefs();
    return await _secure.read(key: key);
  }

  Future<void> delete({required String key}) async {
    if (kIsWeb && (_webMemoryKeys.contains(key) || _webCookieKeys.contains(key))) {
      _webMemory.remove(key);
      return;
    }
    await _secure.delete(key: key);
  }

  Future<void> deleteAll() async {
    _webMemory.clear();
    // Clear all auth-related keys managed by this client.
    for (final key in ['authToken', 'userData', 'operationToken', 'refreshToken']) {
      await _secure.delete(key: key);
    }
  }
}

/// API Client
///
/// Features:
/// - JWT access-token auto-injection
/// - Silent refresh-token rotation on 401 (retries the original request)
/// - Operation token for step-up MFA
/// - Auto-logout when refresh fails
/// - Error handling
class ApiClient {
  late final Dio _dio;

  /// Bare Dio (no auth interceptor) used for the refresh call itself, so refreshing
  /// can't recurse through the 401 handler.
  late final Dio _refreshDio;

  final _PlatformStorage _storage = _PlatformStorage();

  /// Coalesces concurrent refreshes so a burst of 401s triggers a single rotation.
  Future<bool?>? _refreshInFlight;

  // Callback for when user needs to be logged out
  void Function()? onUnauthorized;

  /// Set true by the workmanager background isolate (#493). When set, a failed
  /// token refresh must NOT wipe the stored session: secure storage is shared
  /// with the foreground app, so a transient /auth/refresh failure during a
  /// background sync would otherwise silently log the user out.
  bool backgroundIsolateMode = false;

  /// Expose dio instance for services that need raw access
  Dio get dio => _dio;

  /// Expose base URL for services that need it
  String get baseUrl => AppConfig.baseUrl;

  /// Get auth token (for direct HTTP requests)
  Future<String?> getToken() => getAuthToken();

  /// Points both Dio instances at [AppConfig.baseUrl] again, after the user picked another
  /// server on the sign-in screen (or the saved choice was loaded).
  void applyBaseUrl() {
    _dio.options.baseUrl = AppConfig.baseUrl;
    _refreshDio.options.baseUrl = AppConfig.baseUrl;
  }

  ApiClient() {
    _dio = Dio(BaseOptions(
      baseUrl: AppConfig.baseUrl,
      connectTimeout: Duration(milliseconds: AppConfig.defaultTimeout),
      receiveTimeout: Duration(milliseconds: AppConfig.defaultTimeout),
      headers: {
        'Content-Type': 'application/json',
      },
    ));

    _refreshDio = Dio(BaseOptions(
      baseUrl: AppConfig.baseUrl,
      connectTimeout: Duration(milliseconds: AppConfig.defaultTimeout),
      receiveTimeout: Duration(milliseconds: AppConfig.defaultTimeout),
      headers: {'Content-Type': 'application/json'},
    ));

    // On web, send the HttpOnly refresh cookie with requests (no-op on mobile).
    configureWebCredentials(_dio);
    configureWebCredentials(_refreshDio);

    _setupInterceptors();

    // Sentry Dio integration: breadcrumbs + spans only. Failed requests are
    // reported by _reportServerOrNetworkError (retry-aware, fingerprinted);
    // sentry_dio's own capture would report every retried 504 at error
    // level (FLUTTER-1B, #1057).
    _dio.addSentry(captureFailedRequests: false);
  }

  void _setupInterceptors() {
    // Request interceptor - add auth token
    _dio.interceptors.add(InterceptorsWrapper(
      onRequest: (options, handler) async {
        // Add JWT access token if available
        final token = await _storage.read(key: 'authToken');
        if (token != null) {
          options.headers['Authorization'] = 'Bearer $token';
        }

        // Add operation token for step-up MFA if available
        final operationToken = await _storage.read(key: 'operationToken');
        if (operationToken != null) {
          options.headers['X-Operation-Token'] = operationToken;
        }

        // Add client identification headers for analytics and debugging
        options.headers['X-Client-Type'] = AppConfig.clientType;
        options.headers['X-App-Version'] = AppConfig.appVersion;

        // Send UTC offset so backend resets budget periods at local midnight, not UTC midnight
        options.headers['X-Timezone-Offset'] =
            DateTime.now().timeZoneOffset.inMinutes.toString();

        return handler.next(options);
      },
      onResponse: (response, handler) {
        return handler.next(response);
      },
      onError: (error, handler) async {
        final status = error.response?.statusCode;

        // Handle 403 MfaRequired - let calling code handle this.
        // Guard the body shape first: most 403s (Forbid() authorization denials) come
        // back with an EMPTY body, which Dio's JSON transformer decodes to a String (''),
        // not a Map. Indexing `''['error']` would throw a TypeError *inside* the interceptor,
        // replacing the original DioException and bypassing every caller's `on DioException`
        // handling (and the retry/report paths below). Only index when it's actually a Map.
        final errorData = error.response?.data;
        if (status == 403 && errorData is Map && errorData['error'] == 'MfaRequired') {
          return handler.next(error);
        }

        // Handle 401 Unauthorized - try a silent refresh + retry before logging out.
        // A 401 here is an *authentication* signal (expired/invalid access token) — the JWT
        // middleware rejects the request before the controller runs, so the handler never
        // executed and replaying it can't double-apply a side effect. Pure authorization
        // denials are 403 (see ErrorHandlingMiddleware), not 401, so they never reach here.
        if (status == 401 && _shouldAttemptRefresh(error.requestOptions)) {
          final tokenBefore = error.requestOptions.headers['Authorization'] as String?;
          final refreshed = await _refreshSession();
          if (refreshed == null) {
            // Refresh could not REACH the server (offline). The session may be
            // perfectly valid — never wipe it here; the request just fails and
            // the offline cache layer takes over (#493).
            return handler.next(error);
          }
          if (refreshed) {
            // Only replay a non-idempotent (write) request when the refresh actually produced
            // a NEW access token — i.e. the original 401 was plausibly an expired token, not
            // some other 401 the same token would just hit again. Idempotent GETs are always
            // safe to replay. This prevents a spurious refresh-loop from replaying a write.
            final method = error.requestOptions.method.toUpperCase();
            final isIdempotent =
                method == 'GET' || method == 'HEAD' || method == 'OPTIONS';
            final newToken = await _storage.read(key: 'authToken');
            final gotNewToken =
                newToken != null && 'Bearer $newToken' != tokenBefore;
            if (isIdempotent || gotNewToken) {
              try {
                final retried = await _retry(error.requestOptions);
                return handler.resolve(retried);
              } catch (_) {
                // fall through to logout
              }
            }
          }
          // Refresh failed (or the retry still 401'd) — clear session and log out.
          // Except from the background isolate: its refresh can fail transiently
          // and wiping the shared refresh token would log the foreground app out.
          if (!backgroundIsolateMode) {
            await _storage.delete(key: 'authToken');
            await _storage.delete(key: 'userData');
            await _storage.delete(key: 'refreshToken');
            onUnauthorized?.call();
          }
        }

        // Transient server hiccups — retry idempotent GETs a few times with backoff so a brief
        // blip (a 429 under load, a 502/503/504 gateway timeout, or a dropped connection) degrades
        // into a slightly slower load instead of a thrown DioException (and a noisy Sentry event).
        // Only GETs are retried — replaying a POST/PUT/PATCH/DELETE could double-apply a write.
        // The __transientAttempt counter (incremented on each _dio.fetch re-entry) caps it at 3.
        final attempts = (error.requestOptions.extra['__transientAttempt'] as int?) ?? 0;
        // Requests can opt out with extra['noTransientRetry'] (the data export: a 429 or a
        // timeout there means "slow down", and replaying it would burn the strict export limit).
        final optedOut = error.requestOptions.extra['noTransientRetry'] == true;
        if (!optedOut &&
            _isTransientError(error) &&
            error.requestOptions.method.toUpperCase() == 'GET' &&
            attempts < 3) {
          error.requestOptions.extra['__transientAttempt'] = attempts + 1;
          await Future<void>.delayed(_transientRetryDelay(error, attempts + 1));
          try {
            final token = await _storage.read(key: 'authToken');
            if (token != null) {
              error.requestOptions.headers['Authorization'] = 'Bearer $token';
            }
            final retried = await _dio.fetch(error.requestOptions);
            return handler.resolve(retried);
          } on DioException catch (retryError) {
            return handler.next(retryError);
          }
        }

        // Centralized observability: report genuine failures — server 5xx and
        // network/timeout errors that survived the retries above — to Sentry
        // with request context, so a broken endpoint surfaces on its own without
        // every page needing bespoke try/catch instrumentation. Expected 4xx
        // (validation / permission / not-found) and the handled 401 are skipped
        // to keep the signal high.
        _reportServerOrNetworkError(error);
        return handler.next(error);
      },
    ));
  }

  /// Send a DioException to Sentry only when it represents a real backend or
  /// connectivity failure (HTTP 5xx, or a network/timeout error). Tagged with
  /// method/path/status/dio-type and a stable fingerprint so a recurring failure
  /// is grouped into one issue instead of fragmenting per status code.
  ///
  /// Noise control (transient 504/offline flood was ~90% of Flutter Sentry
  /// events): a transient gateway/rate-limit status (502/503/504/429) is skipped
  /// while it is still eligible for the GET retry above, and only reported once
  /// the retries are exhausted — then downgraded to warning. A user-offline
  /// connectionError (SocketException / host-lookup failure) is dropped entirely:
  /// it is the user's network, not a backend fault.
  void _reportServerOrNetworkError(DioException error) {
    final status = error.response?.statusCode;
    final isServerError = status != null && status >= 500;
    final isNetworkError = error.type == DioExceptionType.connectionError ||
        error.type == DioExceptionType.connectionTimeout ||
        error.type == DioExceptionType.receiveTimeout ||
        error.type == DioExceptionType.sendTimeout;
    if (!isServerError && !isNetworkError) return;

    final options = error.requestOptions;

    // Offline: a connectionError is the client's own network dropping (DNS
    // lookup / SocketException), not something ops can fix. Don't report.
    if (error.type == DioExceptionType.connectionError) return;

    // Transient (429/502/503/504, or a timeout): these are retried for GETs.
    // Skip the report while retries remain; report only once exhausted, and as
    // a warning so it doesn't page like a hard error.
    final isTransient = _isTransientError(error);
    if (isTransient) {
      final attempts = (options.extra['__transientAttempt'] as int?) ?? 0;
      final retriesRemain =
          options.method.toUpperCase() == 'GET' && attempts < 3;
      if (retriesRemain) return;
    }

    // Group recurrences of the same endpoint failure into one Sentry issue.
    // Bucket the status into its class (5xx) or the dio error type so a mix of
    // 500/503 on one path doesn't fragment into distinct issues.
    final statusBucket = status != null
        ? '${status ~/ 100}xx'
        : error.type.name;

    Sentry.captureException(
      error,
      stackTrace: error.stackTrace,
      withScope: (scope) => describeFailure(scope, error,
          isTransient: isTransient, statusBucket: statusBucket),
    );
  }

  /// Fills the Sentry scope for a reported API failure. The path goes through
  /// [LinkUrlScrubber] first: a Cloud link code rides in the path
  /// (`/cloud-servers/link-sessions/BCDF-GH23/approve`) and must not reach the
  /// fingerprint, the `http.path` tag or the request context.
  @visibleForTesting
  static void describeFailure(Scope scope, DioException error,
      {required bool isTransient, required String statusBucket}) {
    final options = error.requestOptions;
    final status = error.response?.statusCode;
    final path = LinkUrlScrubber.scrub(options.path) ?? options.path;
    scope.level = isTransient ? SentryLevel.warning : SentryLevel.error;
    scope.fingerprint = ['dio', options.method, path, statusBucket];
    scope.setTag('http.method', options.method);
    scope.setTag('http.path', path);
    scope.setTag('http.status', status?.toString() ?? 'none');
    scope.setTag('dio.type', error.type.name);
    scope.setContexts('request', {
      'method': options.method,
      'path': path,
      'status_code': status,
      'dio_type': error.type.name,
    });
  }

  /// A transient failure worth retrying: rate-limit (429), gateway/unavailable (502/503/504),
  /// or a connection/receive timeout / dropped connection.
  static bool _isTransientError(DioException error) {
    final status = error.response?.statusCode;
    if (status == 429 || status == 502 || status == 503 || status == 504) {
      return true;
    }
    return error.type == DioExceptionType.connectionTimeout ||
        error.type == DioExceptionType.receiveTimeout ||
        error.type == DioExceptionType.connectionError;
  }

  /// Backoff before a transient retry. Honors a sane `Retry-After` on 429; otherwise
  /// exponential-ish: 400ms, 800ms, 1600ms.
  static Duration _transientRetryDelay(DioException error, int attempt) {
    if (error.response?.statusCode == 429) {
      final retryAfter = error.response?.headers.value('retry-after');
      final secs = retryAfter != null ? int.tryParse(retryAfter.trim()) : null;
      if (secs != null && secs >= 0 && secs <= 10) {
        return Duration(seconds: secs);
      }
    }
    return Duration(milliseconds: 400 * (1 << (attempt - 1)));
  }

  /// A request is eligible for refresh-retry unless it is itself an auth endpoint or
  /// has already been retried once (prevents infinite loops).
  bool _shouldAttemptRefresh(RequestOptions options) {
    if (options.extra['__retried'] == true) return false;
    final path = options.path;
    return !path.contains('/auth/refresh') &&
        !path.contains('/auth/login') &&
        !path.contains('/auth/logout');
  }

  Future<Response<dynamic>> _retry(RequestOptions options) async {
    final token = await _storage.read(key: 'authToken');
    if (token != null) {
      options.headers['Authorization'] = 'Bearer $token';
    }
    options.extra['__retried'] = true;
    return _dio.fetch(options);
  }

  /// Rotates the refresh token and stores the new access token. Returns whether a
  /// valid session was obtained. Safe to call concurrently — one rotation is shared.
  Future<bool> refreshSession() async => await _refreshSession() == true;

  Future<bool?> _refreshSession() {
    final inflight = _refreshInFlight;
    if (inflight != null) return inflight;
    final future = _doRefresh();
    _refreshInFlight = future;
    future.whenComplete(() => _refreshInFlight = null);
    return future;
  }

  /// Three-state refresh result (#493 offline-first):
  ///   true  — refreshed, new token stored
  ///   false — the server REJECTED the refresh (invalid/expired refresh token);
  ///           the session is genuinely dead
  ///   null  — transport failure (offline / server unreachable); the session is
  ///           UNKNOWN and must NOT be wiped — the user may just be offline
  Future<bool?> _doRefresh() async {
    try {
      final body = <String, dynamic>{};
      if (!kIsWeb) {
        final rt = await getRefreshToken();
        if (rt == null || rt.isEmpty) return false; // native with no refresh token
        body['refreshToken'] = rt;
      }
      final resp = await _refreshDio.post('/auth/refresh', data: body);
      final data = resp.data as Map<String, dynamic>;
      final newToken = (data['token'] ?? data['Token']) as String?;
      if (newToken == null || newToken.isEmpty) return false;
      await setAuthToken(newToken);
      final newRefresh = (data['refreshToken'] ?? data['RefreshToken']) as String?;
      if (!kIsWeb && newRefresh != null && newRefresh.isNotEmpty) {
        await setRefreshToken(newRefresh);
      }
      return true;
    } on DioException catch (e) {
      if (isNetworkError(e)) return null; // offline — session unknown, keep it
      return false;
    } catch (_) {
      return false;
    }
  }

  /// Result for a 204 No Content response. There is no body to parse, so return a
  /// type-appropriate empty value. `null as T` (the old behaviour) throws a TypeError
  /// on AOT/mobile whenever a caller declared a non-nullable T (e.g. `put<Map<...>>`
  /// against a 204 route), crashing the action. Null is only returned when T actually
  /// permits it; a non-nullable Map/List gets an empty instance instead.
  static T _noContent<T>() {
    if (null is T) return null as T;
    if (<String, dynamic>{} is T) return <String, dynamic>{} as T;
    if (<dynamic>[] is T) return <dynamic>[] as T;
    // Genuinely unrepresentable (a non-nullable scalar against a 204 route). This is a
    // caller/contract bug; surface it explicitly rather than as an opaque cast error.
    throw StateError(
      'Endpoint returned 204 No Content but caller requested non-nullable type $T. '
      'Use a nullable or void type for no-content routes.',
    );
  }

  /// GET request - returns data directly.
  ///
  /// Offline-first (mobile, #493): on a successful GET of a cacheable endpoint
  /// the raw body is written to the local cache (fire-and-forget). If the GET
  /// fails with a transport error and a cached copy exists, the cached body is
  /// decoded and returned instead of throwing — the caller can't tell it came
  /// from cache, but [offlineStatus] flags it for the offline banner. Web uses a
  /// NoopOfflineStore, so this is a no-op there and behaviour is unchanged.
  Future<T> get<T>(
    String path, {
    Map<String, dynamic>? queryParameters,
    T Function(dynamic)? fromJson,
    CancelToken? cancelToken,
  }) async {
    // Known-offline fast path: don't stall behind connect timeouts + the
    // interceptor's transient retries (~3s+) when we already know the network
    // is down — serve the cache immediately and revalidate in the background
    // so a recovery flips the connectivity state back.
    if (!connectivityService.isOnline && _isCacheableGet(path)) {
      final cached = await _readCache<T>(path, queryParameters, fromJson);
      if (cached != null) {
        _revalidate(path, queryParameters);
        return cached.value;
      }
    }
    try {
      final response = await _dio.get(path,
          queryParameters: queryParameters, cancelToken: cancelToken);
      connectivityService.reportNetworkSuccess();
      if (response.statusCode == 204) return _noContent<T>();
      final body = response.data;
      if (_isCacheableGet(path)) {
        _writeCache(path, queryParameters, body);
        offlineStatus.reportFreshData();
      }
      if (fromJson != null) {
        return fromJson(body);
      }
      return body as T;
    } on DioException catch (e) {
      // Fall back to the local cache for cacheable GETs on transport failures
      // AND on backend 5xx — "a backend hiccup doesn't break the app" (#493).
      final transport = isNetworkError(e);
      final serverError = (e.response?.statusCode ?? 0) >= 500;
      if (transport) connectivityService.reportNetworkFailure();
      if ((transport || serverError) && _isCacheableGet(path)) {
        final cached = await _readCache<T>(path, queryParameters, fromJson);
        if (cached != null) return cached.value;
      }
      rethrow;
    }
  }

  /// Fire-and-forget network attempt behind the known-offline fast path: a
  /// success refreshes the cache and reports recovery so the offline state
  /// clears; a failure just re-confirms we're offline.
  void _revalidate(String path, Map<String, dynamic>? queryParameters) {
    unawaited(() async {
      try {
        final response =
            await _dio.get(path, queryParameters: queryParameters);
        connectivityService.reportNetworkSuccess();
        if (response.statusCode != 204) {
          _writeCache(path, queryParameters, response.data);
        }
      } on DioException catch (e) {
        if (isNetworkError(e)) connectivityService.reportNetworkFailure();
      } catch (_) {}
    }());
  }

  /// GET endpoints whose responses are safe to cache for offline reads.
  static const List<String> _cacheablePrefixes = [
    '/transactions',
    '/budget',
    '/savings',
    '/plaid/accounts',
    '/bank/accounts',
    '/notifications',
    '/dashboard',
    '/categories',
    '/user/settings',
    '/user/profile',
    '/gamification',
    '/subscriptions',
    '/insights',
  ];

  bool _isCacheableGet(String path) =>
      _cacheablePrefixes.any((p) => path.startsWith(p));

  /// Canonical cache key: path plus query params sorted by key so two logically
  /// identical requests map to one entry.
  String _cacheKey(String path, Map<String, dynamic>? query) {
    if (query == null || query.isEmpty) return path;
    final keys = query.keys.toList()..sort();
    final parts = keys.map((k) => '$k=${query[k]}');
    return '$path?${parts.join('&')}';
  }

  /// Fire-and-forget cache write. Never lets a cache failure touch the caller.
  void _writeCache(String path, Map<String, dynamic>? query, dynamic body) {
    try {
      final key = _cacheKey(path, query);
      unawaited(
        offlineStore
            .writeCache(key, path, jsonEncode(body))
            .catchError((_) {}),
      );
    } catch (_) {
      // Encoding a non-JSON body, etc. — ignore; live path already succeeded.
    }
  }

  Future<_CacheHit<T>?> _readCache<T>(
    String path,
    Map<String, dynamic>? query,
    T Function(dynamic)? fromJson,
  ) async {
    try {
      final entry = await offlineStore.readCache(_cacheKey(path, query));
      if (entry == null) return null;
      final decoded = jsonDecode(entry.payload);
      offlineStatus.markServedFromCache(entry.fetchedAt);
      final value = fromJson != null ? fromJson(decoded) : decoded as T;
      return _CacheHit<T>(value);
    } catch (_) {
      return null;
    }
  }

  /// POST request - returns data directly
  Future<T> post<T>(
    String path, {
    dynamic data,
    T Function(dynamic)? fromJson,
    CancelToken? cancelToken,
  }) async {
    final response = await _dio.post(path, data: data, cancelToken: cancelToken);
    if (response.statusCode == 204) return _noContent<T>();
    if (fromJson != null) {
      return fromJson(response.data);
    }
    return response.data as T;
  }

  /// PUT request - returns data directly
  Future<T> put<T>(
    String path, {
    dynamic data,
    T Function(dynamic)? fromJson,
  }) async {
    final response = await _dio.put(path, data: data);
    if (response.statusCode == 204) return _noContent<T>();
    if (fromJson != null) {
      return fromJson(response.data);
    }
    return response.data as T;
  }

  /// PATCH request - returns data directly
  Future<T> patch<T>(
    String path, {
    dynamic data,
    T Function(dynamic)? fromJson,
  }) async {
    final response = await _dio.patch(path, data: data);
    if (response.statusCode == 204) return _noContent<T>();
    if (fromJson != null) {
      return fromJson(response.data);
    }
    return response.data as T;
  }

  /// DELETE request - returns data directly
  Future<T> delete<T>(
    String path, {
    dynamic data,
    T Function(dynamic)? fromJson,
  }) async {
    final response = await _dio.delete(path, data: data);
    if (response.statusCode == 204) return _noContent<T>();
    if (fromJson != null) {
      return fromJson(response.data);
    }
    return response.data as T;
  }

  /// POST with extended timeout (for AI operations)
  Future<T> postWithExtendedTimeout<T>(
    String path, {
    dynamic data,
    Map<String, dynamic>? queryParameters,
    int? timeoutMs,
    T Function(dynamic)? fromJson,
  }) async {
    final response = await _dio.post(
      path,
      data: data,
      queryParameters: queryParameters,
      options: Options(
        receiveTimeout: Duration(
          milliseconds: timeoutMs ?? AppConfig.extendedTimeout,
        ),
      ),
    );
    if (fromJson != null) {
      return fromJson(response.data);
    }
    return response.data as T;
  }

  // Token management helpers
  Future<void> setAuthToken(String token) async {
    await _storage.write(key: 'authToken', value: token);
  }

  Future<String?> getAuthToken() async {
    return await _storage.read(key: 'authToken');
  }

  Future<void> clearAuthToken() async {
    await _storage.delete(key: 'authToken');
  }

  /// Refresh token (native clients only; web keeps it in an HttpOnly cookie).
  Future<void> setRefreshToken(String token) async {
    await _storage.write(key: 'refreshToken', value: token);
  }

  Future<String?> getRefreshToken() async {
    return await _storage.read(key: 'refreshToken');
  }

  Future<void> clearRefreshToken() async {
    await _storage.delete(key: 'refreshToken');
  }

  Future<void> setUserData(String userData) async {
    await _storage.write(key: 'userData', value: userData);
  }

  Future<String?> getUserData() async {
    return await _storage.read(key: 'userData');
  }

  Future<void> clearUserData() async {
    await _storage.delete(key: 'userData');
  }

  Future<void> setOperationToken(String token) async {
    await _storage.write(key: 'operationToken', value: token);
  }

  Future<void> clearOperationToken() async {
    await _storage.delete(key: 'operationToken');
  }

  Future<void> clearAll() async {
    await _storage.deleteAll();
  }
}

/// Wraps a cache-read result so a genuine hit whose value is `null` (nullable T)
/// is distinguishable from a cache miss (the whole wrapper is null).
class _CacheHit<T> {
  final T value;
  const _CacheHit(this.value);
}

// Global singleton instance
final apiClient = ApiClient();
