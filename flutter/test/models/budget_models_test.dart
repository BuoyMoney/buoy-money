import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/budget_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/services/budget_service.dart';

void main() {
  group('BudgetCategory.fromJson', () {
    Map<String, dynamic> baseJson({
      int id = 1,
      int? categoryId,
      String categoryName = 'Groceries',
    }) {
      return {
        'id': id,
        'budgetId': 42,
        if (categoryId != null) 'categoryId': categoryId,
        'categoryName': categoryName,
        'allocatedAmount': 500,
        'spentAmount': 150,
        'remainingAmount': 350,
        'percentageUsed': 30,
      };
    }

    test('reads unified categoryId for a system category', () {
      final cat = BudgetCategory.fromJson(baseJson(categoryId: 20));

      expect(cat.categoryId, 20);
      expect(cat.category, TransactionCategory.fromValue(20));
      expect(cat.categoryName, 'Groceries');
    });

    test('preserves custom category IDs (>=1000); enum degrades to uncategorized', () {
      // Custom user categories use unified IDs 1000+. The TransactionCategory
      // enum only covers 1-99 system categories, so fromValue falls back to
      // uncategorized via its orElse — but categoryId must still be preserved
      // so the UI and API round-trips work.
      final cat = BudgetCategory.fromJson(
        baseJson(categoryId: 1042, categoryName: 'Dog Supplies'),
      );

      expect(cat.categoryId, 1042);
      expect(cat.category, TransactionCategory.uncategorized);
      expect(cat.categoryName, 'Dog Supplies');
    });

    test('falls back to uncategorized (99) when categoryId is missing', () {
      final cat = BudgetCategory.fromJson(baseJson());

      expect(cat.categoryId, 99);
      expect(cat.category, TransactionCategory.uncategorized);
    });

    test('does NOT read the legacy "category" string field', () {
      // Regression guard: the backend emits only "categoryId" (int), never
      // "category" (string). If someone re-introduces the legacy key,
      // categoryId must still come from the missing-value fallback, not
      // from the stray field.
      final json = baseJson()..['category'] = 'groceries';

      final cat = BudgetCategory.fromJson(json);

      expect(cat.categoryId, 99);
    });
  });

  group('CreateBudgetCategoryRequest.toJson', () {
    test('serialises categoryId as the unified integer ID', () {
      final req = CreateBudgetCategoryRequest(
        categoryId: TransactionCategory.groceries.value,
        allocatedAmount: 250,
      );

      final json = req.toJson();

      expect(json['categoryId'], TransactionCategory.groceries.value);
      expect(json['allocatedAmount'], 250);
      expect(json['allowRollover'], false);
      expect(json['isIncome'], false);
      // The legacy string field must never be sent — the backend ignores
      // it and stores CategoryId = 0.
      expect(json.containsKey('category'), isFalse);
    });

    test('supports custom category IDs (>=1000)', () {
      final req = CreateBudgetCategoryRequest(
        categoryId: 1042,
        allocatedAmount: 75,
        isIncome: true,
        allowRollover: true,
      );

      final json = req.toJson();

      expect(json['categoryId'], 1042);
      expect(json['isIncome'], true);
      expect(json['allowRollover'], true);
    });
  });

  // ─────────────────────────────────────────────────────────────────────────
  //  BudgetPeriodListItem.fromJson
  // ─────────────────────────────────────────────────────────────────────────

  group('BudgetPeriodListItem.fromJson', () {
    Map<String, dynamic> camelCaseJson() => {
          'id': 1,
          'periodStart': '2026-01-01T00:00:00',
          'periodEnd': '2026-01-31T23:59:59',
          'totalAllocated': 700.0,
          'totalSpent': 550.0,
        };

    Map<String, dynamic> pascalCaseJson() => {
          'id': 1,
          'PeriodStart': '2026-01-01T00:00:00',
          'PeriodEnd': '2026-01-31T23:59:59',
          'TotalAllocated': 700.0,
          'TotalSpent': 550.0,
        };

    test('parses camelCase keys correctly', () {
      final item = BudgetPeriodListItem.fromJson(camelCaseJson());

      expect(item.id, 1);
      expect(item.periodStart, DateTime.parse('2026-01-01T00:00:00'));
      expect(item.totalAllocated, 700.0);
      expect(item.totalSpent, 550.0);
    });

    test('parses PascalCase keys correctly', () {
      final item = BudgetPeriodListItem.fromJson(pascalCaseJson());

      expect(item.id, 1);
      expect(item.periodStart, DateTime.parse('2026-01-01T00:00:00'));
      expect(item.totalAllocated, 700.0);
      expect(item.totalSpent, 550.0);
    });
  });

  // ─────────────────────────────────────────────────────────────────────────
  //  BudgetPeriodCategoryHistory.fromJson
  // ─────────────────────────────────────────────────────────────────────────

  group('BudgetPeriodCategoryHistory.fromJson', () {
    Map<String, dynamic> camelCaseJson() => {
          'id': 10,
          'categoryId': 20,
          'categoryName': 'Groceries',
          'allocatedAmount': 500.0,
          'spentAmount': 420.0,
          'rolloverIn': 0.0,
          'rolloverOut': 80.0,
          'isIncome': false,
          'allowRollover': true,
        };

    Map<String, dynamic> pascalCaseJson() => {
          'id': 10,
          'CategoryId': 20,
          'CategoryName': 'Groceries',
          'AllocatedAmount': 500.0,
          'SpentAmount': 420.0,
          'RolloverIn': 0.0,
          'RolloverOut': 80.0,
          'IsIncome': false,
          'AllowRollover': true,
        };

    test('parses camelCase keys correctly', () {
      final cat = BudgetPeriodCategoryHistory.fromJson(camelCaseJson());

      expect(cat.id, 10);
      expect(cat.categoryName, 'Groceries');
      expect(cat.allocatedAmount, 500.0);
      expect(cat.spentAmount, 420.0);
      expect(cat.isIncome, false);
      expect(cat.allowRollover, true);
    });

    test('parses PascalCase keys correctly', () {
      final cat = BudgetPeriodCategoryHistory.fromJson(pascalCaseJson());

      expect(cat.id, 10);
      expect(cat.categoryName, 'Groceries');
      expect(cat.allocatedAmount, 500.0);
      expect(cat.isIncome, false);
      expect(cat.allowRollover, true);
    });

    test('remainingAmount computed from allocated minus spent', () {
      final cat = BudgetPeriodCategoryHistory.fromJson(camelCaseJson());

      expect(cat.remainingAmount, 80.0);
    });

    test('percentageUsed computed correctly', () {
      final cat = BudgetPeriodCategoryHistory.fromJson(camelCaseJson());

      expect(cat.percentageUsed, closeTo(84.0, 0.1));
    });
  });

  // ─────────────────────────────────────────────────────────────────────────
  //  BudgetPeriodDetail.fromJson
  // ─────────────────────────────────────────────────────────────────────────

  group('BudgetPeriodDetail.fromJson', () {
    Map<String, dynamic> camelCaseJson() => {
          'id': 5,
          'periodStart': '2026-01-01T00:00:00',
          'periodEnd': '2026-01-31T23:59:59',
          'totalAllocated': 700.0,
          'totalSpent': 550.0,
          'totalIncome': 5000.0,
          'categories': [
            {
              'id': 10,
              'categoryId': 20,
              'categoryName': 'Groceries',
              'allocatedAmount': 500.0,
              'spentAmount': 420.0,
              'rolloverIn': 0.0,
              'rolloverOut': 80.0,
              'isIncome': false,
              'allowRollover': false,
            },
          ],
        };

    Map<String, dynamic> pascalCaseJson() => {
          'id': 5,
          'PeriodStart': '2026-01-01T00:00:00',
          'PeriodEnd': '2026-01-31T23:59:59',
          'TotalAllocated': 700.0,
          'TotalSpent': 550.0,
          'TotalIncome': 5000.0,
          'Categories': [
            {
              'id': 10,
              'CategoryId': 20,
              'CategoryName': 'Groceries',
              'AllocatedAmount': 500.0,
              'SpentAmount': 420.0,
              'RolloverIn': 0.0,
              'RolloverOut': 80.0,
              'IsIncome': false,
              'AllowRollover': false,
            },
          ],
        };

    test('parses camelCase keys with nested categories', () {
      final detail = BudgetPeriodDetail.fromJson(camelCaseJson());

      expect(detail.id, 5);
      expect(detail.totalAllocated, 700.0);
      expect(detail.totalIncome, 5000.0);
      expect(detail.categories, hasLength(1));
      expect(detail.categories.first.categoryName, 'Groceries');
    });

    test('parses PascalCase keys with nested categories', () {
      final detail = BudgetPeriodDetail.fromJson(pascalCaseJson());

      expect(detail.id, 5);
      expect(detail.totalAllocated, 700.0);
      expect(detail.totalIncome, 5000.0);
      expect(detail.categories, hasLength(1));
      expect(detail.categories.first.categoryName, 'Groceries');
    });

    test('returns empty categories list when key is absent', () {
      final json = {
        'id': 5,
        'periodStart': '2026-01-01T00:00:00',
        'periodEnd': '2026-01-31T23:59:59',
      };

      final detail = BudgetPeriodDetail.fromJson(json);

      expect(detail.categories, isEmpty);
    });
  });

  group('Budget.fromJson enum-index bounds', () {
    Map<String, dynamic> baseJson() => {
          'id': 1,
          'name': 'Test',
          'currentPeriodStart': '2026-01-01T00:00:00',
          'currentPeriodEnd': '2026-01-31T23:59:59',
          'totalIncome': 0,
          'totalExpenses': 0,
        };

    test('parses in-range enum indices normally', () {
      final json = baseJson()
        ..['period'] = 3
        ..['type'] = 1
        ..['rolloverMode'] = 2;

      final budget = Budget.fromJson(json);

      expect(budget.period, BudgetPeriod.quarterly);
      expect(budget.type, BudgetType.family);
      expect(budget.rolloverMode, ChildBudgetRolloverMode.promptUser);
    });

    test('clamps an out-of-range period index instead of throwing RangeError', () {
      // Backend enums are the source of truth and can gain a value before a
      // coordinated Flutter release; an out-of-range index must not blank the
      // budget/dashboard pages.
      final json = baseJson()..['period'] = 99;

      final budget = Budget.fromJson(json);

      expect(budget.period, BudgetPeriod.values.last);
    });

    test('clamps out-of-range type and rolloverMode indices', () {
      final json = baseJson()
        ..['type'] = 42
        ..['rolloverMode'] = 42;

      final budget = Budget.fromJson(json);

      expect(budget.type, BudgetType.values.last);
      expect(budget.rolloverMode, ChildBudgetRolloverMode.values.last);
    });

    test('clamps out-of-range countUpRolloverMode on a category', () {
      final json = {
        'id': 7,
        'budgetId': 1,
        'categoryId': 20,
        'categoryName': 'Groceries',
        'allocatedAmount': 100,
        'spentAmount': 0,
        'remainingAmount': 100,
        'percentageUsed': 0,
        'countUpRolloverMode': 99,
      };

      final cat = BudgetCategory.fromJson(json);

      expect(cat.countUpRolloverMode, CountUpRolloverMode.values.last);
    });
  });

  group('CountUpBudgetStatus.findIn', () {
    // Shapes taken from a local test copy: the budget list gives each row its
    // own id plus the shared category id; the count-up list's categoryId is
    // the row id.
    BudgetCategory row(int id, int categoryId, String name) =>
        BudgetCategory.fromJson({
          'id': id,
          'budgetId': 8,
          'categoryId': categoryId,
          'categoryName': name,
        });

    final categories = [
      row(39, 1, 'Salary'),
      row(43, 21, 'Dining Out'),
      row(50, 26, 'Travel'),
    ];

    CountUpBudgetStatus status(int categoryId, String name) =>
        CountUpBudgetStatus.fromJson({
          'categoryId': categoryId,
          'categoryName': name,
        });

    test('finds the row the count-up card describes', () {
      expect(status(43, 'Dining Out').findIn(categories)?.name, 'Dining Out');
      expect(status(50, 'Travel').findIn(categories)?.name, 'Travel');
    });

    test('does not match on the shared category id', () {
      // Row 39 holds shared category 1; a status for row 1 must not match it.
      expect(status(1, 'Other').findIn(categories), isNull);
    });

    test('returns null instead of falling back to the first category', () {
      expect(status(999, 'Gone').findIn(categories), isNull);
      expect(status(43, 'Dining Out').findIn(const []), isNull);
    });
  });
}
