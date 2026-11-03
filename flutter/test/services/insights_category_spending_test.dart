import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/insights_service.dart';

void main() {
  group('CategorySpending.fromJson', () {
    // GET /transactions/categories returns CategoryStatisticsDto whose money fields
    // serialize as `totalAmount` and `percentage`. The parser previously read
    // `amount` / `percentageOfTotal`, which don't exist in the payload, so every
    // category rendered $0 and 0%. Map from the real keys.
    test('reads totalAmount and percentage from the backend payload', () {
      final json = <String, dynamic>{
        'categoryId': 5,
        'categoryName': 'Groceries',
        'totalAmount': 123.45,
        'transactionCount': 7,
        'percentage': 42.5,
      };

      final spending = CategorySpending.fromJson(json);

      expect(spending.categoryName, 'Groceries');
      expect(spending.categoryId, 5);
      expect(spending.amount, 123.45);
      expect(spending.transactionCount, 7);
      expect(spending.percentageOfTotal, 42.5);
    });

    test('reads PascalCase TotalAmount / Percentage too', () {
      final json = <String, dynamic>{
        'CategoryName': 'Dining',
        'CategoryId': 6,
        'TotalAmount': 50,
        'TransactionCount': 2,
        'Percentage': 10,
      };

      final spending = CategorySpending.fromJson(json);

      expect(spending.amount, 50);
      expect(spending.percentageOfTotal, 10);
    });

    test('falls back to 0 when money keys are absent', () {
      final spending = CategorySpending.fromJson(<String, dynamic>{
        'categoryName': 'Unknown',
        'categoryId': 0,
        'transactionCount': 0,
      });

      expect(spending.amount, 0);
      expect(spending.percentageOfTotal, 0);
    });
  });
}
