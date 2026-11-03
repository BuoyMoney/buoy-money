import 'dart:async';
import 'package:dio/dio.dart';
import '../models/budget_models.dart';
import '../offline/sync_service.dart';
import 'api_client.dart';

// Re-export models commonly needed alongside the service
export '../models/budget_models.dart'
    show BudgetPeriod, BudgetType, CountUpRolloverMode, ChildBudgetRolloverMode;

// ═══════════════════════════════════════════
//  REQUEST DTOs
// ═══════════════════════════════════════════

class CreateBudgetCategoryRequest {
  final int categoryId;
  final double allocatedAmount;
  final bool allowRollover;
  final double? maxRolloverMultiplier;
  final int? rolloverOverflowSavingsGoalId;
  final bool isIncome;
  final bool isCountUpEnabled;
  final bool isAllowance;
  final int? assignedUserId;

  CreateBudgetCategoryRequest({
    required this.categoryId,
    required this.allocatedAmount,
    this.allowRollover = false,
    this.maxRolloverMultiplier,
    this.rolloverOverflowSavingsGoalId,
    this.isIncome = false,
    this.isCountUpEnabled = false,
    this.isAllowance = false,
    this.assignedUserId,
  });

  Map<String, dynamic> toJson() => {
    'categoryId': categoryId,
    'allocatedAmount': allocatedAmount,
    'allowRollover': allowRollover,
    if (maxRolloverMultiplier != null) 'maxRolloverMultiplier': maxRolloverMultiplier,
    if (rolloverOverflowSavingsGoalId != null) 'rolloverOverflowSavingsGoalId': rolloverOverflowSavingsGoalId,
    'isIncome': isIncome,
    'isCountUpEnabled': isCountUpEnabled,
    if (isAllowance) 'isAllowance': isAllowance,
    if (assignedUserId != null) 'assignedUserId': assignedUserId,
  };
}

class CreateBudgetRequest {
  final String name;
  final BudgetPeriod period;
  final bool autoReset;
  final double totalIncome;
  final List<CreateBudgetCategoryRequest> categories;
  final BudgetType type;
  final int? parentBudgetId;
  final double? allocatedAmount;
  final int? assignedUserId;
  // RFC #793 — for a Type=Personal budget created inside a family, link/create the
  // funding family category. Send ONE of these (Owner/BudgetAdmin only, server-enforced).
  final int? fundFromCategoryId;
  final String? newFamilyCategoryName;

  CreateBudgetRequest({
    required this.name,
    required this.period,
    this.autoReset = true,
    required this.totalIncome,
    required this.categories,
    this.type = BudgetType.personal,
    this.parentBudgetId,
    this.allocatedAmount,
    this.assignedUserId,
    this.fundFromCategoryId,
    this.newFamilyCategoryName,
  });

  Map<String, dynamic> toJson() => {
    'name': name,
    'period': period.index,
    'autoReset': autoReset,
    'totalIncome': totalIncome,
    'categories': categories.map((c) => c.toJson()).toList(),
    'type': type.index,
    if (parentBudgetId != null) 'parentBudgetId': parentBudgetId,
    if (allocatedAmount != null) 'allocatedAmount': allocatedAmount,
    if (assignedUserId != null) 'assignedUserId': assignedUserId,
    if (fundFromCategoryId != null) 'fundFromCategoryId': fundFromCategoryId,
    if (newFamilyCategoryName != null) 'newFamilyCategoryName': newFamilyCategoryName,
  };
}

class UpdateBudgetCategoryRequest {
  final double allocatedAmount;
  final bool allowRollover;
  final double? maxRolloverMultiplier;
  final int? rolloverOverflowSavingsGoalId;
  final CountUpRolloverMode? countUpRolloverMode;
  final int? savingsGoalId;
  final bool applyToFuturePeriods;

  UpdateBudgetCategoryRequest({
    required this.allocatedAmount,
    required this.allowRollover,
    this.maxRolloverMultiplier,
    this.rolloverOverflowSavingsGoalId,
    this.countUpRolloverMode,
    this.savingsGoalId,
    this.applyToFuturePeriods = false,
  });

  Map<String, dynamic> toJson() => {
    'allocatedAmount': allocatedAmount,
    'allowRollover': allowRollover,
    'maxRolloverMultiplier': maxRolloverMultiplier,
    'rolloverOverflowSavingsGoalId': rolloverOverflowSavingsGoalId,
    if (countUpRolloverMode != null) 'countUpRolloverMode': countUpRolloverMode!.index,
    'savingsGoalId': savingsGoalId,
    'applyToFuturePeriods': applyToFuturePeriods,
  };
}

class InlineUpdateCategoryRequest {
  final double? allocatedAmount;
  final bool? allowRollover;
  final bool? isCountUpEnabled;
  final int? sortOrder;

  InlineUpdateCategoryRequest({
    this.allocatedAmount,
    this.allowRollover,
    this.isCountUpEnabled,
    this.sortOrder,
  });

  Map<String, dynamic> toJson() {
    final m = <String, dynamic>{};
    if (allocatedAmount != null) m['allocatedAmount'] = allocatedAmount;
    if (allowRollover != null) m['allowRollover'] = allowRollover;
    if (isCountUpEnabled != null) m['isCountUpEnabled'] = isCountUpEnabled;
    if (sortOrder != null) m['sortOrder'] = sortOrder;
    return m;
  }
}

// ═══════════════════════════════════════════
//  BUDGET SERVICE
// ═══════════════════════════════════════════

class BudgetService {
  // ── Core CRUD ──────────────────────────

  Future<List<Budget>> getBudgets() async {
    final response = await apiClient.get<List<dynamic>>('/budget');
    return response
        .map((e) => Budget.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<Budget?> getActiveBudget() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>('/budget/active');
      return Budget.fromJson(response);
    } catch (_) {
      return null;
    }
  }

  Future<Budget> getBudget(int budgetId) async {
    final response = await apiClient.get<Map<String, dynamic>>('/budget/$budgetId');
    return Budget.fromJson(response);
  }

  Future<List<Budget>> getAccessibleBudgets() async {
    final response = await apiClient.get<List<dynamic>>('/budget/accessible');
    return response
        .map((e) => Budget.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<Budget?> getPrimaryBudget() async {
    try {
      final response = await apiClient.get<Map<String, dynamic>>('/budget/primary');
      return Budget.fromJson(response);
    } catch (_) {
      return null;
    }
  }

  Future<void> setPrimaryBudget(int budgetId) async {
    await apiClient.put('/budget/$budgetId/set-primary');
  }

  Future<Budget> createBudget(CreateBudgetRequest request) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget',
      data: request.toJson(),
    );
    return Budget.fromJson(response);
  }

  Future<Budget> createAllowanceBudget(
    int parentBudgetId,
    CreateBudgetRequest request,
  ) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/$parentBudgetId/allowance',
      data: request.toJson(),
    );
    return Budget.fromJson(response);
  }

  /// Spawn a personal budget from an existing family expense category (RFC #793).
  ///
  /// Re-purposes the family category as the funding link and creates a child
  /// personal budget for [assignedUserId] (defaults to the caller server-side).
  /// [allocatedAmount] defaults to the category's current allocation; [budgetName]
  /// defaults to "{member first name}'s Budget". Owner/BudgetAdmin only (403 otherwise).
  Future<Budget> spawnPersonalBudget(
    int categoryId, {
    int? assignedUserId,
    double? allocatedAmount,
    String? budgetName,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/category/$categoryId/spawn-personal',
      data: {
        if (assignedUserId != null) 'assignedUserId': assignedUserId,
        if (allocatedAmount != null) 'allocatedAmount': allocatedAmount,
        if (budgetName != null && budgetName.trim().isNotEmpty)
          'budgetName': budgetName.trim(),
      },
    );
    return Budget.fromJson(response);
  }

  Future<Budget> updateBudget(int budgetId, CreateBudgetRequest request) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/budget/$budgetId',
      data: request.toJson(),
    );
    return Budget.fromJson(response);
  }

  Future<void> deleteBudget(int budgetId) async {
    await apiClient.delete('/budget/$budgetId');
  }

  // ── Category ───────────────────────────

  /// Update a budget category's allocation/rollover settings.
  ///
  /// Offline-first (#493): on a network failure the op is queued and the cached
  /// `/budget*` payloads are patched with the new values; the patched category is
  /// re-parsed and returned. If nothing is cached the original error is rethrown
  /// (the op is still queued and syncs on reconnect).
  Future<BudgetCategory> updateBudgetCategory(
    int categoryId,
    UpdateBudgetCategoryRequest request,
  ) async {
    try {
      final response = await apiClient.put<Map<String, dynamic>>(
        '/budget/category/$categoryId',
        data: request.toJson(),
      );
      unawaited(syncService.drainOutbox());
      return BudgetCategory.fromJson(response);
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opBudgetCategoryUpdate,
        entityId: '$categoryId',
        payload: request.toJson(),
      );
      final changes = <String, dynamic>{
        'allocatedAmount': request.allocatedAmount,
        'allowRollover': request.allowRollover,
        if (request.maxRolloverMultiplier != null)
          'maxRolloverMultiplier': request.maxRolloverMultiplier,
        if (request.rolloverOverflowSavingsGoalId != null)
          'rolloverOverflowSavingsGoalId': request.rolloverOverflowSavingsGoalId,
        if (request.savingsGoalId != null) 'savingsGoalId': request.savingsGoalId,
      };
      final patched = await syncService.patchBudgetCategory(categoryId, changes);
      if (patched == null) rethrow;
      return BudgetCategory.fromJson(patched);
    }
  }

  Future<BudgetCategory> updateCategoryInline(
    int categoryId,
    InlineUpdateCategoryRequest request,
  ) async {
    final response = await apiClient.patch<Map<String, dynamic>>(
      '/budget/categories/$categoryId',
      data: request.toJson(),
    );
    return BudgetCategory.fromJson(response);
  }

  Future<BudgetCategory> moveCategoryToGroup(
    int categoryId, {
    int? groupId,
    int? sortOrder,
  }) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/budget/categories/$categoryId/move',
      data: {
        'categoryId': categoryId,
        if (groupId != null) 'groupId': groupId,
        if (sortOrder != null) 'sortOrder': sortOrder,
      },
    );
    return BudgetCategory.fromJson(response);
  }

  // ── Grouped Overview ───────────────────

  Future<GroupedBudgetOverview> getGroupedOverview(int budgetId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/budget/$budgetId/grouped',
    );
    return GroupedBudgetOverview.fromJson(response);
  }

  Future<List<CategoryGroup>> getCategoryGroups(int budgetId) async {
    final response = await apiClient.get<List<dynamic>>(
      '/budget/$budgetId/groups',
    );
    return response
        .map((e) => CategoryGroup.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<CategoryGroup> createCategoryGroup(
    int budgetId, {
    required String name,
    String? icon,
    int? sortOrder,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/$budgetId/groups',
      data: {
        'name': name,
        if (icon != null) 'icon': icon,
        if (sortOrder != null) 'sortOrder': sortOrder,
      },
    );
    return CategoryGroup.fromJson(response);
  }

  Future<CategoryGroup> updateCategoryGroup(
    int groupId, {
    String? name,
    String? icon,
    int? sortOrder,
  }) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/budget/groups/$groupId',
      data: {
        if (name != null) 'name': name,
        if (icon != null) 'icon': icon,
        if (sortOrder != null) 'sortOrder': sortOrder,
      },
    );
    return CategoryGroup.fromJson(response);
  }

  Future<void> deleteCategoryGroup(int groupId) async {
    await apiClient.delete('/budget/groups/$groupId');
  }

  Future<void> reorderCategoryGroups(int budgetId, List<int> groupIds) async {
    await apiClient.put(
      '/budget/$budgetId/groups/reorder',
      data: {'groupIds': groupIds},
    );
  }

  // ── Count-Up Budgeting ─────────────────

  Future<List<CountUpBudgetStatus>> getCountUpStatus(int budgetId) async {
    final response = await apiClient.get<List<dynamic>>(
      '/budget/$budgetId/countup',
    );
    return response
        .map((e) => CountUpBudgetStatus.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<void> enableCountUp(int categoryId, bool enable) async {
    await apiClient.post(
      '/budget/category/$categoryId/enable-countup',
      data: {'categoryId': categoryId, 'enable': enable},
    );
  }

  Future<void> updateCountUpSettings(
    int categoryId, {
    double? customDailyAllowance,
  }) async {
    await apiClient.put(
      '/budget/category/$categoryId/countup-settings',
      data: {
        'categoryId': categoryId,
        if (customDailyAllowance != null) 'customDailyAllowance': customDailyAllowance,
      },
    );
  }

  Future<CountUpBudgetStatus> getCountUpCategoryStatus(int categoryId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/budget/category/$categoryId/countup',
    );
    return CountUpBudgetStatus.fromJson(response);
  }

  // ── Transfers ──────────────────────────

  Future<TransferFundsResponse> transferFunds({
    required int targetCategoryId,
    required int sourceCategoryId,
    required double amount,
    String? note,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/categories/$targetCategoryId/transfer',
      data: {
        'sourceCategoryId': sourceCategoryId,
        'amount': amount,
        if (note != null) 'note': note,
      },
    );
    return TransferFundsResponse.fromJson(response);
  }

  Future<TransferToSavingsResponse> transferToSavings({
    required int categoryId,
    required int savingsGoalId,
    required double amount,
    String? note,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/categories/$categoryId/transfer-to-savings',
      data: {
        'savingsGoalId': savingsGoalId,
        'amount': amount,
        if (note != null) 'note': note,
      },
    );
    return TransferToSavingsResponse.fromJson(response);
  }

  Future<List<BudgetTransfer>> getTransferHistory(int budgetId) async {
    final response = await apiClient.get<List<dynamic>>(
      '/budget/$budgetId/transfers',
    );
    return response
        .map((e) => BudgetTransfer.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  // ── Rollovers ──────────────────────────

  Future<ResetRolloversResponse> resetCategoryRollover(int categoryId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/categories/$categoryId/reset-rollover',
    );
    return ResetRolloversResponse.fromJson(response);
  }

  Future<ResetRolloversResponse> resetAllRollovers(int budgetId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/$budgetId/reset-all-rollovers',
    );
    return ResetRolloversResponse.fromJson(response);
  }

  // ── Family Budgets ─────────────────────

  Future<List<Budget>> getFamilyBudgets() async {
    final response = await apiClient.get<List<dynamic>>('/budget/family');
    return response
        .map((e) => Budget.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<FamilyBudgetOverview> getFamilyBudgetOverview() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/budget/family/overview',
    );
    return FamilyBudgetOverview.fromJson(response);
  }

  // ── Sharing ────────────────────────────

  Future<List<BudgetShare>> getBudgetShares(int budgetId) async {
    final response = await apiClient.get<List<dynamic>>(
      '/budget/$budgetId/shares',
    );
    return response
        .map((e) => BudgetShare.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<BudgetShare> shareBudget(
    int budgetId, {
    required int shareWithUserId,
    bool canEdit = false,
    bool canViewTransactions = true,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/budget/$budgetId/shares',
      data: {
        'shareWithUserId': shareWithUserId,
        'canEdit': canEdit,
        'canViewTransactions': canViewTransactions,
      },
    );
    return BudgetShare.fromJson(response);
  }

  Future<void> removeBudgetShare(int budgetId, int userId) async {
    await apiClient.delete('/budget/$budgetId/shares/$userId');
  }

  // ── Period History ─────────────────────

  Future<List<BudgetPeriodListItem>> getBudgetPeriods(int budgetId) async {
    final response = await apiClient.get<List<dynamic>>(
      '/budget/$budgetId/periods',
    );
    return response
        .map((e) => BudgetPeriodListItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<BudgetPeriodDetail> getBudgetPeriodDetail(
      int budgetId, int snapshotId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/budget/$budgetId/periods/$snapshotId',
    );
    return BudgetPeriodDetail.fromJson(response);
  }

  Future<BudgetPeriodCategoryHistory> updatePeriodCategoryAmount(
    int budgetId,
    int snapshotId,
    int categorySnapshotId,
    double allocatedAmount,
  ) async {
    final response = await apiClient.patch<Map<String, dynamic>>(
      '/budget/$budgetId/periods/$snapshotId/categories/$categorySnapshotId',
      data: {'allocatedAmount': allocatedAmount},
    );
    return BudgetPeriodCategoryHistory.fromJson(response);
  }

  // ── Dashboard Overview ─────────────────

  Future<BudgetOverview> getBudgetOverview() async {
    try {
      final activeBudget = await getActiveBudget();
      if (activeBudget == null) return BudgetOverview.empty();

      final sortedCategories = [...activeBudget.expenseCategories]
        ..sort((a, b) => b.spentAmount.compareTo(a.spentAmount));
      final topCategories = sortedCategories.take(5).toList();

      return BudgetOverview(
        activeBudget: activeBudget,
        totalAllocated: activeBudget.totalAllocated,
        totalSpent: activeBudget.totalSpent,
        totalRemaining: activeBudget.totalRemaining,
        percentageUsed: activeBudget.percentageUsed,
        topCategories: topCategories,
      );
    } catch (_) {
      return BudgetOverview.empty();
    }
  }
}

/// Singleton instance. Mutable so widget tests can swap in a fake (matching the
/// pattern used by transactionsService / accountsService / categoriesService).
BudgetService budgetService = BudgetService();
