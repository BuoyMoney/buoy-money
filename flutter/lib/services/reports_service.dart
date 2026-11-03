import 'api_client.dart';

/// One slice of the spending-by-category report.
class SpendingSlice {
  final String category;
  final double amount;
  final double percentage;

  SpendingSlice(
      {required this.category, required this.amount, required this.percentage});

  factory SpendingSlice.fromJson(Map<String, dynamic> json) => SpendingSlice(
        category: (json['category'] ?? json['Category'] ?? '').toString(),
        amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
        percentage:
            ((json['percentage'] ?? json['Percentage'] ?? 0) as num).toDouble(),
      );
}

class ReportsService {
  /// Spending by category for a date range (defaults server-side to 30 days).
  Future<List<SpendingSlice>> getSpendingByCategory(
      {DateTime? start, DateTime? end}) async {
    final response = await apiClient.get<List<dynamic>>(
      '/reports/spending-by-category',
      queryParameters: {
        if (start != null) 'startDate': start.toIso8601String(),
        if (end != null) 'endDate': end.toIso8601String(),
      },
    );
    return response
        .map((e) => SpendingSlice.fromJson(e as Map<String, dynamic>))
        .toList();
  }
}

// Intentionally non-final so tests can inject a fake (no DI container).
var reportsService = ReportsService();
