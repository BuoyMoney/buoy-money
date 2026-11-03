import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/budget_models.dart';
import 'package:buoy_ai/models/savings_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/services/budget_service.dart';
import 'package:buoy_ai/services/categories_service.dart';
import 'package:buoy_ai/services/savings_service.dart';
import 'package:buoy_ai/services/transactions_service.dart';
import 'package:buoy_ai/widgets/transaction_modals.dart';

/// Widget coverage for the RFC #793 personal sub-category picker (F4) in the
/// edit-category sheet. When the member has a family-funded personal budget its
/// expense categories become private sub-categories; picking one sends
/// personalCategoryId to updateTransactionCategory.
class _FakeCategoriesService extends CategoriesService {
  @override
  Future<CategoriesResponse> getAll({bool forceRefresh = false}) async {
    return CategoriesResponse(
      systemCategories: const [],
      personalCategories: const [],
      familyCategories: const [],
      allCategories: [
        Category.fromJson({'id': 20, 'name': 'Groceries', 'isIncome': false}),
      ],
    );
  }
}

class _FakeSavingsService extends SavingsService {
  @override
  Future<List<SavingsGoal>> getGoals() async => const [];
}

class _FakeBudgetService extends BudgetService {
  @override
  Future<List<Budget>> getBudgets() async => [
        Budget.fromJson({
          'id': 42,
          'name': "Sam's Budget",
          'currentPeriodStart': '2026-01-01T00:00:00Z',
          'currentPeriodEnd': '2026-02-01T00:00:00Z',
          'type': 0,
          'isFamilyFunded': true,
          'familyAllocationAmount': 200,
          'categories': [
            {
              'id': 91,
              'categoryId': 1001,
              'categoryName': 'Snacks',
              'allocatedAmount': 50,
              'isIncome': false,
            },
          ],
        }),
      ];
}

class _FakeTransactionsService extends TransactionsService {
  int calls = 0;
  int? lastPersonalCategoryId;

  @override
  Future<Transaction> updateTransactionCategory(
    int transactionId, {
    required int? categoryId,
    int? personalCategoryId,
    String? semanticTags,
  }) async {
    calls++;
    lastPersonalCategoryId = personalCategoryId;
    return _tx(personalCategoryId: personalCategoryId);
  }
}

Transaction _tx({int? personalCategoryId}) => Transaction(
      id: 500,
      accountId: 7,
      amount: -12.5,
      type: TransactionType.debit,
      description: 'Corner Store',
      categoryId: 20,
      categoryName: 'Groceries',
      isUserCategorized: true,
      date: DateTime(2026, 1, 5),
      accountName: 'Checking',
      personalCategoryId: personalCategoryId,
    );

void main() {
  late CategoriesService realCategories;
  late SavingsService realSavings;
  late BudgetService realBudget;
  late TransactionsService realTx;
  late _FakeTransactionsService fakeTx;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realCategories = categoriesService;
    realSavings = savingsService;
    realBudget = budgetService;
    realTx = transactionsService;

    categoriesService = _FakeCategoriesService();
    savingsService = _FakeSavingsService();
    budgetService = _FakeBudgetService();
    fakeTx = _FakeTransactionsService();
    transactionsService = fakeTx;
  });

  tearDown(() {
    categoriesService = realCategories;
    savingsService = realSavings;
    budgetService = realBudget;
    transactionsService = realTx;
  });

  Future<void> openSheet(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1200, 3200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(
      MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: Scaffold(
          body: Builder(
            builder: (ctx) => Center(
              child: ElevatedButton(
                onPressed: () => showEditCategorySheet(ctx, _tx()),
                child: const Text('open'),
              ),
            ),
          ),
        ),
      ),
    );

    await tester.tap(find.text('open'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 500));
  }

  testWidgets('renders the personal sub-category picker from the funded budget',
      (tester) async {
    await openSheet(tester);

    expect(find.text('Personal sub-category (private)'), findsOneWidget);
    expect(find.text('Snacks'), findsOneWidget);
    expect(find.text('None'), findsOneWidget);
  });

  testWidgets('picking a sub-category sends personalCategoryId on save',
      (tester) async {
    await openSheet(tester);

    await tester.tap(find.text('Snacks'));
    await tester.pump();

    await tester.ensureVisible(find.text('Save Category'));
    await tester.tap(find.text('Save Category'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(fakeTx.calls, 1);
    // The picked personal sub-category maps to its categoryId (1001).
    expect(fakeTx.lastPersonalCategoryId, 1001);
  });
}
