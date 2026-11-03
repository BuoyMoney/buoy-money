import 'package:dio/dio.dart';

/// Whether a [DioException] represents a transport/connectivity failure (the
/// device couldn't reach the backend) rather than a server response.
///
/// Deliberately does NOT reference any `dart:io` type (`SocketException` etc.) —
/// that import would break the web build. Instead it classifies by Dio's own
/// [DioExceptionType] plus a string sniff of `error` for the `unknown` case,
/// which is where a wrapped socket/host-lookup failure usually lands.
///
/// Shared by ApiClient (serve-from-cache on a failed GET) and SyncService
/// (queue-instead-of-fail on a failed write), so both agree on what "offline"
/// means.
bool isNetworkError(DioException e) {
  switch (e.type) {
    case DioExceptionType.connectionError:
    case DioExceptionType.connectionTimeout:
    case DioExceptionType.receiveTimeout:
    case DioExceptionType.sendTimeout:
      return true;
    case DioExceptionType.unknown:
      final s = e.error?.toString().toLowerCase() ?? '';
      return s.contains('socket') ||
          s.contains('failed host lookup') ||
          s.contains('connection') ||
          s.contains('network is unreachable') ||
          s.contains('no address associated');
    default:
      return false;
  }
}
