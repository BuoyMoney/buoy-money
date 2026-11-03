import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/models/budget_models.dart';
import 'package:buoy_ai/models/family_models.dart';
import 'package:buoy_ai/services/budget_service.dart';
import 'package:buoy_ai/widgets/budget_modals.dart';

/// Widget coverage for the RFC #793 "Make Personal Budget" spawn sheet (F1).
///
/// The sheet turns a family expense category into a member's family-funded
/// personal budget. The global `budgetService` is swapped for a fake so nothing
/// touches the network; the fake records the spawn arguments.
class _FakeBudgetService extends BudgetService {
  int calls = 0;
  int? lastCategoryId;
  int? lastAssignedUserId;
  double? lastAllocatedAmount;
  String? lastBudgetName;

  @override
  Future<Budget> spawnPersonalBudget(
    int categoryId, {
    int? assignedUserId,
    double? allocatedAmount,
    String? budgetName,
  }) async {
    calls++;
    lastCategoryId = categoryId;
    lastAssignedUserId = assignedUserId;
    lastAllocatedAmount = allocatedAmount;
    lastBudgetName = budgetName;
    return Budget.fromJson(<String, dynamic>{
      'id': 42,
      'name': budgetName ?? 'Budget',
      'currentPeriodStart': '2026-01-01T00:00:00Z',
      'currentPeriodEnd': '2026-02-01T00:00:00Z',
      'type': 0,
      'isFamilyFunded': true,
    });
  }
}

BudgetCategory _category({double amount = 200}) => BudgetCategory.fromJson({
      'id': 7,
      'categoryId': 20,
      'categoryName': 'Groceries',
      'allocatedAmount': amount,
    });

FamilyMember _member(int id, String first,
        {bool isCurrentUser = false}) =>
    FamilyMember(
      userId: id,
      email: '$first@example.com',
      firstName: first,
      role: FamilyMemberRole.owner,
      joinedAt: DateTime(2026, 1, 1),
      isCurrentUser: isCurrentUser,
    );

void main() {
  late BudgetService realBudget;
  late _FakeBudgetService fakeBudget;

  setUp(() {
    GoogleFonts.config.allowRuntimeFetching = false;
    realBudget = budgetService;
    fakeBudget = _FakeBudgetService();
    budgetService = fakeBudget;
  });

  tearDown(() {
    budgetService = realBudget;
  });

  Future<void> openSheet(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1200, 2600);
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
                onPressed: () => showSpawnPersonalBudgetSheet(
                  ctx,
                  category: _category(),
                  familyMembers: [
                    _member(1, 'Sam', isCurrentUser: true),
                    _member(2, 'Alex'),
                  ],
                  currentUserId: 1,
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

  testWidgets('renders with the amount and name prefilled from the category',
      (tester) async {
    await openSheet(tester);

    expect(find.text('Make a Personal Budget'), findsOneWidget);
    // Name defaults to "{caller first name}'s Budget".
    expect(find.text("Sam's Budget"), findsOneWidget);
    // Amount defaults to the category's current allocation.
    expect(find.text('200'), findsOneWidget);
    expect(find.text('Create Personal Budget'), findsOneWidget);
  });

  testWidgets('submits with the defaults (caller + category amount + name)',
      (tester) async {
    await openSheet(tester);

    await tester.tap(find.text('Create Personal Budget'));
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 400));

    expect(fakeBudget.calls, 1);
    expect(fakeBudget.lastCategoryId, 7);
    expect(fakeBudget.lastAssignedUserId, 1);
    expect(fakeBudget.lastAllocatedAmount, 200);
    expect(fakeBudget.lastBudgetName, "Sam's Budget");
  });
}
