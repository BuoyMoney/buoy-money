import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/utils/budget_period_label.dart';

void main() {
  DateTime d(int y, int m, int day) => DateTime(y, m, day);

  group('budgetPeriodLabel', () {
    test('full calendar month → "Mon Year"', () {
      expect(budgetPeriodLabel(d(2026, 6, 1), d(2026, 6, 30)), 'Jun 2026');
      expect(budgetPeriodLabel(d(2026, 2, 1), d(2026, 2, 28)), 'Feb 2026');
      expect(budgetPeriodLabel(d(2026, 1, 1), d(2026, 1, 31)), 'Jan 2026');
    });

    test('full calendar quarter → "Qn Year"', () {
      expect(budgetPeriodLabel(d(2026, 1, 1), d(2026, 3, 31)), 'Q1 2026');
      expect(budgetPeriodLabel(d(2026, 4, 1), d(2026, 6, 30)), 'Q2 2026');
      expect(budgetPeriodLabel(d(2026, 7, 1), d(2026, 9, 30)), 'Q3 2026');
      expect(budgetPeriodLabel(d(2026, 10, 1), d(2026, 12, 31)), 'Q4 2026');
    });

    test('full calendar year → "Year"', () {
      expect(budgetPeriodLabel(d(2026, 1, 1), d(2026, 12, 31)), '2026');
    });

    test('sub-month range within one month → "Mon d–d, Year"', () {
      expect(budgetPeriodLabel(d(2026, 5, 1), d(2026, 5, 14)), 'May 1–14, 2026');
      expect(
          budgetPeriodLabel(d(2026, 5, 15), d(2026, 5, 28)), 'May 15–28, 2026');
    });

    test('range crossing months → "Mon d – Mon d, Year"', () {
      expect(budgetPeriodLabel(d(2026, 5, 29), d(2026, 6, 11)),
          'May 29 – Jun 11, 2026');
    });

    test('range crossing years → "Mon d, Year – Mon d, Year"', () {
      expect(budgetPeriodLabel(d(2026, 12, 28), d(2027, 1, 3)),
          'Dec 28, 2026 – Jan 3, 2027');
    });

    test('the three former "May 2026" fragments now read distinctly', () {
      final labels = {
        budgetPeriodLabel(d(2026, 5, 1), d(2026, 5, 14)),
        budgetPeriodLabel(d(2026, 5, 15), d(2026, 5, 28)),
        budgetPeriodLabel(d(2026, 5, 29), d(2026, 6, 11)),
      };
      expect(labels.length, 3); // all distinct, no more triple "May 2026"
    });
  });
}
