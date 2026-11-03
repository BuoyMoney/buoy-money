import 'dart:async';
import 'package:dio/dio.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import '../models/covering_transfer_models.dart';
import '../offline/network_error.dart' as neterr;
import '../models/transaction_models.dart';
import '../models/suspected_transfer.dart';
import '../models/cash_flow.dart';
import '../offline/sync_service.dart';
import 'api_client.dart';
import 'gamification_service.dart';

/// Transactions Service
/// 
/// Handles all transaction-related API calls.
class TransactionsService {
  /// Get transactions with optional filters
  Future<TransactionResponse> getTransactions([TransactionFilter? filter]) async {
    final params = filter?.toQueryParams() ?? {'page': 1, 'pageSize': 25};
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions',
      queryParameters: params,
    );
    return TransactionResponse.fromJson(response);
  }

  /// Actual cash flow (money in / out / net) for a date range — transfers and card payments
  /// excluded, refunds netted. Defaults server-side to the current month when dates are omitted.
  Future<CashFlow> getCashFlow({String? startDate, String? endDate}) async {
    final params = <String, dynamic>{};
    if (startDate != null) params['startDate'] = startDate;
    if (endDate != null) params['endDate'] = endDate;
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions/cash-flow',
      queryParameters: params,
    );
    return CashFlow.fromJson(response);
  }

  /// Get a single transaction by ID
  Future<Transaction> getTransaction(int id) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions/$id',
    );
    return Transaction.fromJson(response);
  }

  /// Get transaction summary for a date range
  Future<TransactionSummary> getTransactionSummary({
    String? startDate,
    String? endDate,
  }) async {
    final params = <String, dynamic>{};
    if (startDate != null) params['startDate'] = startDate;
    if (endDate != null) params['endDate'] = endDate;
    
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions/summary',
      queryParameters: params.isNotEmpty ? params : null,
    );
    return TransactionSummary.fromJson(response);
  }

  /// Update transaction category (unified: system 1-99, custom 1000+)
  Future<Transaction> updateTransactionCategory(
    int transactionId, {
    required int? categoryId,
    int? personalCategoryId,
    String? semanticTags,
  }) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/transactions/$transactionId/category',
      data: {
        'categoryId': categoryId,
        if (personalCategoryId != null) 'personalCategoryId': personalCategoryId,
        if (semanticTags != null) 'semanticTags': semanticTags,
      },
    );
    // Nudge the "categorize N transactions" daily challenge (best-effort).
    unawaited(
        gamificationService.incrementChallengeProgress('categorize_transactions'));
    return Transaction.fromJson(response);
  }

  /// Rename a transaction. Pass null/empty to clear it and fall back to the bank's own name.
  Future<Transaction> updateTransactionNickname(
    int transactionId,
    String? nickname,
  ) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/transactions/$transactionId/nickname',
      data: {'nickname': nickname},
    );
    return Transaction.fromJson(response);
  }

  /// Update transaction user notes.
  ///
  /// Offline-first (#493): on a network failure the op is queued and the cached
  /// transaction lists are patched with the new notes; the patched item is
  /// re-parsed and returned so the UI updates optimistically. If nothing is
  /// cached (we can't fabricate a Transaction) the original error is rethrown —
  /// note the op is still queued, so the change syncs once connectivity returns.
  Future<Transaction> updateTransactionNotes(
    int transactionId,
    String? notes,
  ) async {
    try {
      final response = await apiClient.put<Map<String, dynamic>>(
        '/transactions/$transactionId/notes',
        data: {'notes': notes},
      );
      unawaited(syncService.drainOutbox());
      return Transaction.fromJson(response);
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opTransactionNotesUpdate,
        entityId: '$transactionId',
        payload: {'notes': notes},
      );
      final patched =
          await syncService.patchTransactionNotes(transactionId, notes);
      if (patched == null) rethrow;
      return Transaction.fromJson(patched);
    }
  }

  /// Update transaction date
  Future<Transaction> updateTransactionDate(
    int transactionId,
    DateTime date,
  ) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/transactions/$transactionId/date',
      data: {'date': date.toIso8601String()},
    );
    return Transaction.fromJson(response);
  }

  /// Split a transaction into multiple parts
  Future<Transaction> splitTransaction(
    int transactionId,
    List<TransactionSplitItem> splits,
  ) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/transactions/$transactionId/split',
      data: {
        'splits': splits.map((s) => s.toJson()).toList(),
      },
    );
    return Transaction.fromJson(response);
  }

  /// Unsplit a previously split transaction
  Future<Transaction> unsplitTransaction(int transactionId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/transactions/$transactionId/unsplit',
    );
    return Transaction.fromJson(response);
  }

  /// Delete a transaction
  Future<void> deleteTransaction(int transactionId) async {
    await apiClient.delete<dynamic>('/transactions/$transactionId');
  }

  /// Create a manual transaction (user-entered, not from Plaid/import).
  /// [amount] is signed: positive = income, negative = expense.
  /// [accountId] is optional — omit it (null) to record a cash transaction with no linked bank
  /// account; the backend files it under the user's synthetic "Cash" account.
  Future<Transaction> createTransaction({
    int? accountId,
    required double amount,
    required String description,
    String? merchantName,
    DateTime? date,
    int? categoryId,
    String? userNotes,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/transactions',
      data: {
        if (accountId != null) 'accountId': accountId,
        'amount': amount,
        'description': description,
        if (merchantName != null && merchantName.isNotEmpty) 'merchantName': merchantName,
        if (date != null) 'date': date.toIso8601String(),
        if (categoryId != null) 'categoryId': categoryId,
        if (userNotes != null && userNotes.isNotEmpty) 'userNotes': userNotes,
      },
    );
    return Transaction.fromJson(response);
  }

  /// Mark a transaction as a savings goal withdrawal
  Future<Transaction> markSavingsWithdrawal(int transactionId, int savingsGoalId) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/transactions/$transactionId/savings-withdrawal',
      data: {'savingsGoalId': savingsGoalId},
    );
    return Transaction.fromJson(response);
  }

  /// Remove savings goal withdrawal link from a transaction
  Future<Transaction> removeSavingsWithdrawal(int transactionId) async {
    final response = await apiClient.delete<Map<String, dynamic>>(
      '/transactions/$transactionId/savings-withdrawal',
    );
    return Transaction.fromJson(response);
  }

  /// For a purchase paid from a goal that lives in a real account: the in-flight
  /// draws on that goal and the recent money-out rows that could have moved it.
  Future<CoveringTransferCandidates> getCoveringTransferCandidates(
      int transactionId) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions/$transactionId/covering-transfer/candidates',
    );
    return CoveringTransferCandidates.fromJson(response);
  }

  /// "This transfer covered that purchase." [includeOtherPending] settles every
  /// other in-flight draw on the same goal with the same transfer.
  Future<Transaction> linkCoveringTransfer(
    int transactionId,
    int transferTransactionId, {
    bool includeOtherPending = false,
  }) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/transactions/$transactionId/covering-transfer',
      data: {
        'transferTransactionId': transferTransactionId,
        'includeOtherPending': includeOtherPending,
      },
    );
    return Transaction.fromJson(response);
  }

  /// Put a draw back in flight.
  Future<Transaction> unlinkCoveringTransfer(int transactionId) async {
    final response = await apiClient.delete<Map<String, dynamic>>(
      '/transactions/$transactionId/covering-transfer',
    );
    return Transaction.fromJson(response);
  }

  /// Transactions that look like internal transfers but are counted as spending.
  Future<List<SuspectedTransfer>> getSuspectedTransfers() async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/transactions/suspected-transfers',
    );
    final raw = response['suspectedTransfers'] as List<dynamic>? ?? const [];
    return raw
        .map((s) => SuspectedTransfer.fromJson(s as Map<String, dynamic>))
        .toList();
  }

  /// Confirm suspected legs as an actual transfer (recategorizes to Transfer).
  Future<bool> confirmSuspectedTransfer(List<int> transactionIds) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/transactions/suspected-transfers/confirm',
        data: {'transactionIds': transactionIds},
      );
      return response['success'] == true;
    } catch (e, st) {
      // A 4xx (validation/permission/409 concurrency) or a response-parse throw is
      // NOT reported by the central api_client interceptor (it only captures
      // 5xx/network), so a systematically failing recategorization write — which
      // silently keeps mislabeled transfers inflating the user's spending — would
      // be invisible to ops. Surface it here; still return false for the UI.
      if (e is! DioException || !neterr.isNetworkError(e)) {
        unawaited(Sentry.captureException(e, stackTrace: st, withScope: (scope) {
          scope.setTag('transactions.write', 'confirm-suspected-transfer');
        }));
      }
      return false;
    }
  }

  /// Dismiss a suspected transfer (marks the legs reviewed so they aren't suggested again).
  Future<bool> dismissSuspectedTransfer(List<int> transactionIds) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/transactions/suspected-transfers/dismiss',
        data: {'transactionIds': transactionIds},
      );
      return response['success'] == true;
    } catch (e, st) {
      // Same central-interceptor gap as confirm above: a 4xx/parse failure on this
      // money-affecting write is otherwise unreported. Surface it, still return false.
      if (e is! DioException || !neterr.isNetworkError(e)) {
        unawaited(Sentry.captureException(e, stackTrace: st, withScope: (scope) {
          scope.setTag('transactions.write', 'dismiss-suspected-transfer');
        }));
      }
      return false;
    }
  }
}

/// Split item for creating a split
class TransactionSplitItem {
  final double amount;
  final int? categoryId;

  /// Saved as the part's notes (receipt splits list their items here).
  final String? note;

  TransactionSplitItem({required this.amount, this.categoryId, this.note});

  Map<String, dynamic> toJson() => {
    'amount': amount,
    if (categoryId != null) 'categoryId': categoryId,
    if (note != null && note!.isNotEmpty) 'note': note,
  };
}

/// Singleton instance.
///
/// Not `final` only so widget/unit tests can inject a fake (there is no DI container);
/// production code never reassigns it. See test/widgets/create_transaction_sheet_test.dart.
TransactionsService transactionsService = TransactionsService();
