import 'package:dio/dio.dart';

import '../offline/network_error.dart';

/// Pulls the human-readable message the backend sent with a failed request.
///
/// Controllers return their validation failures as `{ "message": "..." }` (e.g. the
/// savings earmark endpoint explaining that a transaction is still pending). Catch
/// blocks that discarded the error and showed a fixed string turned those precise,
/// actionable messages into an unexplained red banner — the user had no way to know
/// *why* the action failed or what to do about it.
///
/// Falls back to [fallback] when the failure carries no usable server message
/// (network drop, non-JSON body, or a non-Dio error).
String apiErrorMessage(Object? error, {required String fallback}) {
  if (error is! DioException) return fallback;

  // Connection/timeout failures have no server message worth showing.
  if (isNetworkError(error)) {
    return 'No connection. Check your network and try again.';
  }

  final data = error.response?.data;
  if (data is Map) {
    for (final key in const ['message', 'Message', 'error', 'Error', 'title']) {
      final value = data[key];
      if (value is String && value.trim().isNotEmpty) return value.trim();
    }
  } else if (data is String && data.trim().isNotEmpty && data.length <= 300) {
    // Some endpoints return a bare string body; ignore anything long enough to
    // be an HTML error page.
    return data.trim();
  }

  return fallback;
}
