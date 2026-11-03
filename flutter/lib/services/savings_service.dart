import 'dart:async';
import 'package:dio/dio.dart';
import '../models/savings_models.dart';
import '../offline/sync_service.dart';
import 'api_client.dart';

/// Savings Service — all savings-related API calls.
///
/// Matches backend endpoints:
///   SavingsController  → /api/savings/*
///   ManualAllocationRequest → POST /savings/goals/{id}/allocate
class SavingsService {
  // ── Overview ────────────────────────────────────────────

  Future<SavingsOverview> getOverview() async {
    final response =
        await apiClient.get<Map<String, dynamic>>('/savings/overview');
    return SavingsOverview.fromJson(response);
  }

  // ── Goals CRUD ──────────────────────────────────────────

  Future<List<SavingsGoal>> getGoals() async {
    final response = await apiClient.get<List<dynamic>>('/savings/goals');
    return response
        .map((e) => SavingsGoal.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<SavingsGoal> getGoal(int goalId) async {
    final response =
        await apiClient.get<Map<String, dynamic>>('/savings/goals/$goalId');
    return SavingsGoal.fromJson(response);
  }

  /// Create goal — fields match [CreateSavingsGoalRequest].
  Future<SavingsGoal> createGoal({
    required String name,
    required double targetAmount,
    required double allocationPercentage,
    SavingsGoalType type = SavingsGoalType.personal,
    double initialAmount = 0,
    String? description,
    DateTime? targetDate,
    String iconName = 'target',
    String colorTheme = 'primary',
    bool countWithdrawalsTowardBudget = false,
    /// Optional account to reconcile against; null leaves the goal unlinked.
    int? accountId,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/savings/goals',
      data: {
        'type': type.index,
        'name': name,
        'targetAmount': targetAmount,
        'allocationPercentage': allocationPercentage,
        'iconName': iconName,
        'colorTheme': colorTheme,
        'countWithdrawalsTowardBudget': countWithdrawalsTowardBudget,
        if (initialAmount > 0) 'initialAmount': initialAmount,
        if (description != null && description.isNotEmpty)
          'description': description,
        if (targetDate != null) 'targetDate': targetDate.toIso8601String(),
        if (accountId != null) 'accountId': accountId,
      },
    );
    return SavingsGoal.fromJson(response);
  }

  /// Update goal — fields match [UpdateSavingsGoalRequest].
  ///
  /// Offline-first (#493): on a network failure the op is queued and the cached
  /// `/savings/goals` payload is patched with the new fields; the patched goal is
  /// re-parsed and returned. If nothing is cached the original error is rethrown
  /// (the op is still queued and syncs on reconnect).
  /// Sets a goal's balance to the balance of the account holding it.
  ///
  /// Only valid when the goal is alone in that account — the server rejects it
  /// otherwise, because claiming the whole balance would take a sibling goal's money.
  Future<SavingsGoal> syncGoalToAccount(int goalId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/savings/goals/$goalId/sync-to-account',
    );
    return SavingsGoal.fromJson(response);
  }

  /// Redistributes balances across the goals sharing one account, in one transaction.
  ///
  /// [amountsByGoalId] are absolute target balances, not deltas, so the server commits
  /// exactly the distribution the user saw and approved.
  Future<ReconcileResult> reconcileAccount(
    int accountId,
    Map<int, double> amountsByGoalId,
  ) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/savings/accounts/$accountId/reconcile',
      data: {
        'adjustments': amountsByGoalId.entries
            .map((e) => {'goalId': e.key, 'newAmount': e.value})
            .toList(),
      },
    );
    return ReconcileResult.fromJson(response);
  }

  Future<SavingsGoal> updateGoal(
    int goalId, {
    required String name,
    required double targetAmount,
    required double allocationPercentage,
    String? description,
    DateTime? targetDate,
    SavingsGoalType? type,
    String? iconName,
    String? colorTheme,
    bool? isActive,
    bool? countWithdrawalsTowardBudget,
    /// New account to reconcile against. Omitted leaves the existing link alone —
    /// use [clearAccountLink] to deliberately unlink, since a null here cannot mean
    /// "remove" without silently unlinking on every unrelated edit.
    int? accountId,
    bool clearAccountLink = false,
  }) async {
    final body = <String, dynamic>{
      'name': name,
      'targetAmount': targetAmount,
      'allocationPercentage': allocationPercentage,
      if (description != null) 'description': description,
      if (targetDate != null) 'targetDate': targetDate.toIso8601String(),
      if (type != null) 'type': type.index,
      if (iconName != null) 'iconName': iconName,
      if (colorTheme != null) 'colorTheme': colorTheme,
      if (isActive != null) 'isActive': isActive,
      if (countWithdrawalsTowardBudget != null)
        'countWithdrawalsTowardBudget': countWithdrawalsTowardBudget,
      if (clearAccountLink) 'clearAccountLink': true
      else if (accountId != null) 'accountId': accountId,
    };
    try {
      final response = await apiClient.put<Map<String, dynamic>>(
        '/savings/goals/$goalId',
        data: body,
      );
      unawaited(syncService.drainOutbox());
      return SavingsGoal.fromJson(response);
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opSavingsGoalUpdate,
        entityId: '$goalId',
        payload: body,
      );
      final patched = await syncService.patchSavingsGoal(goalId, body);
      if (patched == null) rethrow;
      return SavingsGoal.fromJson(patched);
    }
  }

  Future<void> deleteGoal(int goalId) async {
    await apiClient.delete('/savings/goals/$goalId');
  }

  /// Reset the goal's current balance back to zero. The goal definition
  /// and allocation history are preserved; only [currentAmount] is
  /// zeroed and [isCompleted] cleared.
  Future<SavingsGoal> resetGoal(int goalId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/savings/goals/$goalId/reset',
    );
    return SavingsGoal.fromJson(response);
  }

  // ── Allocations ─────────────────────────────────────────

  /// Add funds — matches [ManualAllocationRequest].
  Future<SavingsGoal> allocateToGoal(
    int goalId, {
    required double amount,
    String source = 'manual_addition',
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/savings/goals/$goalId/allocate',
      data: {
        'savingsGoalId': goalId,
        'amount': amount,
        'source': source,
      },
    );
    return SavingsGoal.fromJson(response);
  }

  Future<double> getLeftoverBudget(int budgetId) async {
    final response = await apiClient
        .get<Map<String, dynamic>>('/savings/leftover/$budgetId');
    return (response['leftoverAmount'] as num?)?.toDouble() ?? 0;
  }

  // ── Achievements ────────────────────────────────────────

  Future<List<SavingsAchievement>> getAchievements() async {
    final response =
        await apiClient.get<List<dynamic>>('/savings/achievements');
    return response
        .map((e) => SavingsAchievement.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<SavingsAchievement>> checkForNewAchievements() async {
    final response =
        await apiClient.post<List<dynamic>>('/savings/achievements/check');
    return response
        .map((e) => SavingsAchievement.fromJson(e as Map<String, dynamic>))
        .toList();
  }
}

/// Singleton instance
// Mutable so widget tests can swap in a fake, as with transactionsService/accountsService.
SavingsService savingsService = SavingsService();
