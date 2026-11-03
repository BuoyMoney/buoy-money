import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/budget_models.dart';
import 'package:buoy_ai/services/categories_service.dart';
import 'package:buoy_ai/widgets/budget_modals.dart';

/// Widget coverage for the RFC #793 "Fund from your family" picker (F2) inside
/// the Create Budget sheet. A Personal budget created in a family can be backed
/// by an existing family category or a brand-new one. The categories service is
/// faked so the sheet loads offline.
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

Budget _familyBudget() => Budget.fromJson({
      'id': 5,
      'name': 'Family',
      'currentPeriodStart': '2026-01-01T00:00:00Z',
      'currentPeriodEnd': '2026-02-01T00:00:00Z',
      'type': 1,
      'categories': [
        {
          'id': 11,
          'categoryId': 21,
          'categoryName': 'Fun Money',
          'allocatedAmount': 150,
          'isIncome': false,
        },
      ],
    });

void main() {
  late CategoriesService realCategories;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realCategories = categoriesService;
    categoriesService = _FakeCategoriesService();
  });

  tearDown(() {
    categoriesService = realCategories;
  });

  Future<void> openSheet(
    WidgetTester tester, {
    required bool canManageFamily,
  }) async {
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
                onPressed: () => showCreateBudgetSheet(
                  ctx,
                  hasFamily: true,
                  familyBudget: _familyBudget(),
                  canManageFamily: canManageFamily,
                ),
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

  testWidgets('admin sees the fund-from selector and can switch existing vs new',
      (tester) async {
    await openSheet(tester, canManageFamily: true);

    expect(find.text('Fund from your family'), findsOneWidget);
    expect(find.text('No family funding'), findsOneWidget);
    expect(find.text('Existing category'), findsOneWidget);
    expect(find.text('+ New family category'), findsOneWidget);

    // Existing → shows the family-category dropdown.
    await tester.tap(find.text('Existing category'));
    await tester.pump();
    expect(find.text('Choose a family category'), findsOneWidget);
    expect(find.text('Family allocation'), findsOneWidget);

    // New → shows the new-category name field instead.
    await tester.tap(find.text('+ New family category'));
    await tester.pump();
    expect(find.text('New family category name'), findsOneWidget);
    expect(find.text('Family allocation'), findsOneWidget);
  });

  testWidgets('a non-admin member sees the disabled funding note only',
      (tester) async {
    await openSheet(tester, canManageFamily: false);

    expect(find.text('Fund from your family'), findsOneWidget);
    expect(
      find.textContaining('Only a family owner or budget admin'),
      findsOneWidget,
    );
    // The mode chips are hidden for a plain member.
    expect(find.text('Existing category'), findsNothing);
    expect(find.text('+ New family category'), findsNothing);
  });
}
