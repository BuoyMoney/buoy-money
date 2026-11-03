import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/savings_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/services/categories_service.dart';
import 'package:buoy_ai/services/savings_service.dart';
import 'package:buoy_ai/services/transactions_service.dart';
import 'package:buoy_ai/widgets/transaction_modals.dart';

/// Widget coverage for the edit-category sheet's save path.
///
/// The AI auto-applies its guess to `categoryId` while leaving `isUserCategorized` false and
/// `needsReview` true, so a low-confidence transaction already *displays* a category. Picking
/// that same category here is the user confirming the guess — it has to be persisted, or the
/// review queue keeps asking about a transaction the user already decided.
///
/// Drives the real sheet (tap the category, tap Save) with the service globals faked, per the
/// pattern in create_transaction_sheet_test.dart.

Category _cat(int id, String name) => Category(
      id: id,
      name: name,
      color: '#3B82F6',
      isIncome: false,
      isSystem: true,
      sortOrder: 0,
      scope: 'system',
      canEdit: false,
    );

class _FakeCategoriesService extends CategoriesService {
  @override
  Future<CategoriesResponse> getAll({bool forceRefresh = false}) async {
    final cats = [_cat(21, 'Dining Out'), _cat(22, 'Groceries')];
    return CategoriesResponse(
      systemCategories: cats,
      personalCategories: const [],
      familyCategories: const [],
      allCategories: cats,
    );
  }
}

class _FakeSavingsService extends SavingsService {
  @override
  Future<List<SavingsGoal>> getGoals() async => const [];
}

class _FakeTransactionsService extends TransactionsService {
  int updateCalls = 0;
  int? lastCategoryId;

  @override
  Future<Transaction> updateTransactionCategory(
    int transactionId, {
    required int? categoryId,
    int? personalCategoryId,
    String? semanticTags,
  }) async {
    updateCalls++;
    lastCategoryId = categoryId;
    return _tx(id: transactionId, categoryId: categoryId, isUserCategorized: true);
  }
}

Transaction _tx({
  required int id,
  int? categoryId,
  bool isUserCategorized = false,
  bool needsReview = false,
}) =>
    Transaction(
      id: id,
      accountId: 1,
      amount: -25,
      type: TransactionType.debit,
      description: 'Coffee',
      categoryId: categoryId,
      categoryName: 'Dining Out',
      date: DateTime(2026, 1, 1),
      accountName: 'Checking',
      isUserCategorized: isUserCategorized,
      needsReview: needsReview,
      aiSuggestedCategoryId: categoryId,
    );

void main() {
  late CategoriesService realCategories;
  late SavingsService realSavings;
  late TransactionsService realTransactions;
  late _FakeTransactionsService fakeTx;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realCategories = categoriesService;
    realSavings = savingsService;
    realTransactions = transactionsService;

    categoriesService = _FakeCategoriesService();
    savingsService = _FakeSavingsService();
    fakeTx = _FakeTransactionsService();
    transactionsService = fakeTx;
  });

  tearDown(() {
    categoriesService = realCategories;
    savingsService = realSavings;
    transactionsService = realTransactions;
  });

  Future<void> openSheet(WidgetTester tester, Transaction tx) async {
    tester.view.physicalSize = const Size(1200, 2600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);

    await tester.pumpWidget(MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(
        body: Builder(
          builder: (ctx) => Center(
            child: ElevatedButton(
              onPressed: () => showEditCategorySheet(ctx, tx),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    ));

    await tester.tap(find.text('open'));
    await tester.pump(); // push the modal route
    await tester.pump(const Duration(milliseconds: 500)); // anim + fake loads
  }

  testWidgets('confirming the AI guess on a needs-review transaction saves it',
      (tester) async {
    // categoryId is already 21 because the AI auto-applied it — the user never confirmed it.
    await openSheet(
      tester,
      _tx(id: 1, categoryId: 21, isUserCategorized: false, needsReview: true),
    );

    // Re-pick the category the card already shows. This is the user agreeing with the AI.
    await tester.tap(find.text('Dining Out').last);
    await tester.pump();
    await tester.tap(find.text('Save Category'));
    await tester.pump();

    expect(fakeTx.updateCalls, 1,
        reason: 'confirming the AI guess must persist, or the review queue asks again');
    expect(fakeTx.lastCategoryId, 21);
  });

  testWidgets('re-picking the same category on an already-confirmed transaction is a no-op',
      (tester) async {
    await openSheet(
      tester,
      _tx(id: 2, categoryId: 21, isUserCategorized: true, needsReview: false),
    );

    await tester.tap(find.text('Dining Out').last);
    await tester.pump();
    await tester.tap(find.text('Save Category'));
    await tester.pump();

    expect(fakeTx.updateCalls, 0,
        reason: 'nothing changed and the user had already confirmed it');
  });

  testWidgets('changing the category still saves', (tester) async {
    await openSheet(
      tester,
      _tx(id: 3, categoryId: 21, isUserCategorized: true, needsReview: false),
    );

    await tester.tap(find.text('Groceries').last);
    await tester.pump();
    await tester.tap(find.text('Save Category'));
    await tester.pump();

    expect(fakeTx.updateCalls, 1);
    expect(fakeTx.lastCategoryId, 22);
  });
}
