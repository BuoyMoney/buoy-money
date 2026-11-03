import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:buoy_ai/services/dashboard_service.dart';

/// An ApiClient whose every GET throws, standing in for a backend that changed a
/// response shape (the `List<dynamic>` -> `Account.fromJson` cast crash class) or
/// returned a 4xx — neither of which the central interceptor reports.
class _ThrowingApiClient extends ApiClient {
  @override
  Future<T> get<T>(
    String path, {
    Map<String, dynamic>? queryParameters,
    T Function(dynamic)? fromJson,
    CancelToken? cancelToken,
  }) async {
    throw DioException(
      requestOptions: RequestOptions(path: path),
      type: DioExceptionType.badResponse,
      response: Response(
        requestOptions: RequestOptions(path: path),
        statusCode: 400,
      ),
    );
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  group('DashboardService.getDashboardData graceful degrade', () {
    // Finding #1: a parse/4xx failure on the money endpoints must NOT crash the
    // dashboard — it degrades to a zeroed DashboardData (and now also fires a
    // Sentry signal, which is a safe no-op when Sentry is uninitialized here).
    test('returns a zeroed DashboardData instead of throwing when every '
        'endpoint fails', () async {
      final service = DashboardService(_ThrowingApiClient());

      final data = await service.getDashboardData();

      expect(data.totalBalance, 0);
      expect(data.totalDebt, 0);
      expect(data.accountsCount, 0);
      expect(data.recentTransactions, isEmpty);
      expect(data.budgetCategories, isEmpty);
    });
  });
}
