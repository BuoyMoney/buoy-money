import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/utils/api_error.dart';

DioException _badResponse(dynamic body, {int status = 400}) {
  final options = RequestOptions(path: '/transactions/742/savings-withdrawal');
  return DioException(
    requestOptions: options,
    type: DioExceptionType.badResponse,
    response: Response(
      requestOptions: options,
      statusCode: status,
      data: body,
    ),
  );
}

void main() {
  group('apiErrorMessage', () {
    // Regression: the backend explained exactly why the earmark failed and the
    // client threw the message away in favour of a fixed string.
    test('returns the backend message for a 400', () {
      const serverMessage =
          'This transaction is still pending. Once it posts you can earmark it '
          'to a savings goal.';
      expect(
        apiErrorMessage(_badResponse({'message': serverMessage}),
            fallback: 'Failed to earmark to savings goal'),
        serverMessage,
      );
    });

    test('accepts PascalCase and error keys', () {
      expect(apiErrorMessage(_badResponse({'Message': 'nope'}), fallback: 'f'),
          'nope');
      expect(apiErrorMessage(_badResponse({'error': 'nope'}), fallback: 'f'),
          'nope');
    });

    test('falls back when the body carries no usable message', () {
      expect(apiErrorMessage(_badResponse({'code': 42}), fallback: 'f'), 'f');
      expect(apiErrorMessage(_badResponse({'message': '   '}), fallback: 'f'), 'f');
      expect(apiErrorMessage(_badResponse(null), fallback: 'f'), 'f');
    });

    test('ignores a long non-JSON body (an HTML error page)', () {
      expect(
        apiErrorMessage(_badResponse('<html>${'x' * 400}</html>'), fallback: 'f'),
        'f',
      );
    });

    test('uses a bare string body when it is short enough', () {
      expect(apiErrorMessage(_badResponse('Savings goal not found'),
          fallback: 'f'),
          'Savings goal not found');
    });

    test('reports connectivity failures as an offline message', () {
      final e = DioException(
        requestOptions: RequestOptions(path: '/x'),
        type: DioExceptionType.connectionError,
      );
      expect(apiErrorMessage(e, fallback: 'f'), contains('No connection'));
    });

    test('falls back for a non-Dio error', () {
      expect(apiErrorMessage(StateError('boom'), fallback: 'f'), 'f');
      expect(apiErrorMessage(null, fallback: 'f'), 'f');
    });
  });
}
