import 'package:buoy_ai/services/api_client.dart';
import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

/// A reported API failure (5xx or timeout) must not carry a Cloud link code from the
/// request path into the Sentry fingerprint, the http.path tag or the request context.
void main() {
  DioException failure(String method, String path, {int? status}) {
    final options = RequestOptions(path: path, method: method);
    return DioException(
      requestOptions: options,
      response: status == null
          ? null
          : Response(requestOptions: options, statusCode: status),
      type: status == null
          ? DioExceptionType.receiveTimeout
          : DioExceptionType.badResponse,
    );
  }

  for (final (method, path) in [
    ('GET', '/cloud-servers/link-sessions/BCDF-GH23'),
    ('POST', '/cloud-servers/link-sessions/BCDF-GH23/approve'),
    ('POST', '/cloud-servers/link-sessions/BCDF-GH23/deny'),
  ]) {
    test('$method $path on a 500 reports the path without the code', () {
      final scope = Scope(SentryOptions(dsn: 'https://key@example.com/1'));

      ApiClient.describeFailure(scope, failure(method, path, status: 500),
          isTransient: false, statusBucket: '5xx');

      final redacted = path.replaceFirst('BCDF-GH23', '[code]');
      expect(scope.fingerprint, ['dio', method, redacted, '5xx']);
      expect(scope.tags['http.path'], redacted);
      expect(
          scope.contexts['request'].toString(), isNot(contains('BCDF-GH23')));
      expect(scope.contexts['request'].toString(), contains(redacted));
    });
  }

  test('a timeout on a link code path is scrubbed too', () {
    final scope = Scope(SentryOptions(dsn: 'https://key@example.com/1'));

    ApiClient.describeFailure(
        scope, failure('GET', '/cloud-servers/link-sessions/BCDF-GH23'),
        isTransient: true, statusBucket: 'receiveTimeout');

    expect(scope.tags['http.path'], '/cloud-servers/link-sessions/[code]');
    expect(scope.level, SentryLevel.warning);
  });

  test('other paths are reported unchanged', () {
    final scope = Scope(SentryOptions(dsn: 'https://key@example.com/1'));

    ApiClient.describeFailure(
        scope, failure('GET', '/budget/overview', status: 503),
        isTransient: true, statusBucket: '5xx');

    expect(scope.tags['http.path'], '/budget/overview');
    expect(scope.fingerprint, ['dio', 'GET', '/budget/overview', '5xx']);
  });
}
