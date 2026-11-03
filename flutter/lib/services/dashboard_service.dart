import 'dart:async';
import 'package:flutter/foundation.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'package:dio/dio.dart';
import '../offline/network_error.dart' as neterr;
import '../models/dashboard_models.dart';
import '../models/transaction_models.dart';
import '../utils/currency.dart';
import 'accounts_service.dart' show fetchAccountsJson;
import 'api_client.dart';

/// Dashboard Service - fetches and aggregates dashboard data from multiple endpoints
class DashboardService {
  final ApiClient _apiClient;
  
  DashboardService(this._apiClient);
  
  /// Get aggregated dashboard data from multiple endpoints
  Future<DashboardData> getDashboardData() async {
    try {
      // Fetch data from multiple endpoints in parallel
      final results = await Future.wait([
        _getAccounts(),
        _getRecentTransactions(),
        _getCurrentPeriodSummary(),
        _getPreviousPeriodSummary(),
        _getActiveBudget(),
        _getFamilyBudgets(),
      ], eagerError: false);
      
      final accounts = results[0] as List<Account>;
      final transactions = results[1] as List<Transaction>;
      final currentSummary = results[2] as Map<String, dynamic>?;
      final previousSummary = results[3] as Map<String, dynamic>?;
      final activeBudget = results[4] as Map<String, dynamic>?;
      final familyBudgets = results[5] as List<Map<String, dynamic>>;
      
      // Calculate balances from accounts
      // Account types: 0=depository, 1=savings, 2=credit, 3=investment, 4=loan/other
      // Total Balance = checking + savings only (exclude investments for budgeting focus)
      double totalBalance = 0;
      double creditUsed = 0;
      double loanBalance = 0;
      final foreignCodes = <String>{};

      for (final account in accounts) {
        // Foreign-currency accounts can't be added into these single-currency
        // totals; note their codes so the UI can say they're excluded.
        if (!isPrimaryCurrency(account.currency)) {
          foreignCodes.add(account.currency.toUpperCase());
          continue;
        }
        if (account.type == 0 || account.type == 1) {
          // Checking, Savings - spendable assets
          totalBalance += account.currentBalance;
        } else if (account.type == 2) {
          // Credit card - liability
          creditUsed += account.currentBalance.abs();
        } else if (account.type == 4) {
          // Loan/other - liability
          loanBalance += account.currentBalance.abs();
        }
      }
      
      // Total Debt = credit card debt + loan balances
      final totalDebt = creditUsed + loanBalance;
      
      // Get income/expenses from summary
      double totalIncome = 0;
      double totalExpenses = 0;
      
      if (currentSummary != null) {
        totalIncome = _parseDouble(currentSummary['totalIncome']);
        totalExpenses = _parseDouble(currentSummary['totalExpenses']).abs();
      }
      
      // Calculate month-over-month changes
      double? incomeChange;
      double? expensesChange;
      
      if (previousSummary != null) {
        final prevIncome = _parseDouble(previousSummary['totalIncome']);
        final prevExpenses = _parseDouble(previousSummary['totalExpenses']).abs();

        incomeChange = _meaningfulChange(totalIncome, prevIncome);
        expensesChange = _meaningfulChange(totalExpenses, prevExpenses);
      }
      
      // Process budget: prefer family budget, fall back to personal
      final primaryFamilyBudget = familyBudgets.where((b) => 
        (b['isPrimary'] == true || b['IsPrimary'] == true) && 
        (b['isActive'] == true || b['IsActive'] == true)
      ).firstOrNull ?? familyBudgets.where((b) => 
        b['isActive'] == true || b['IsActive'] == true
      ).firstOrNull;
      
      final budget = primaryFamilyBudget ?? activeBudget;
      
      // Process budget categories
      List<DashboardBudgetCategory> budgetCategories = [];
      String? budgetName;
      double? budgetTotalIncome;
      double? budgetTotalSpent;
      double? budgetTotalRemaining;
      double? budgetOverallPercentage;
      
      if (budget != null) {
        budgetName = (budget['name'] ?? budget['Name'])?.toString();
        budgetTotalIncome = _parseDouble(budget['totalIncome'] ?? budget['TotalIncome']);
        
        final categories = (budget['categories'] ?? budget['Categories']) as List<dynamic>? ?? [];
        
        // Budgeted expense categories only — exclude income and virtual
        // (unbudgeted) categories so spent/allocated stay budget-relative.
        final expenseCategories = categories.where((cat) {
          final isIncome = cat['isIncome'] ?? cat['IsIncome'] ?? false;
          final isVirtual = cat['isVirtual'] ?? cat['IsVirtual'] ?? false;
          return isIncome != true && isVirtual != true;
        }).toList();
        
        double totalAllocated = 0;
        double totalSpent = 0;
        
        for (var cat in expenseCategories) {
          totalAllocated += _parseDouble(cat['allocatedAmount'] ?? cat['AllocatedAmount']);
          totalSpent += _parseDouble(cat['spentAmount'] ?? cat['SpentAmount']).abs();
        }
        
        budgetTotalSpent = totalSpent;
        // Remaining is budget-relative (allocated - spent), to stay consistent with
        // "% used" (spent / allocated) and the budget page. Using income here made the
        // bar read e.g. "99% used" while showing a large income-based "remaining".
        budgetTotalRemaining = totalAllocated - totalSpent;
        budgetOverallPercentage = totalAllocated > 0 ? (totalSpent / totalAllocated) * 100 : 0;
        
        // Top SPENDING categories, most-spent first — this is a "where's my money going"
        // glance. Build it from expenseCategories (income + virtual already excluded), sorted
        // by spend, NOT from the raw `categories.take(4)`, which showed the first four the
        // backend happened to return — so income categories (Salary, Other Income) led the list.
        final topSpending = [...expenseCategories]
          ..sort((a, b) => _parseDouble(b['spentAmount'] ?? b['SpentAmount'])
              .abs()
              .compareTo(_parseDouble(a['spentAmount'] ?? a['SpentAmount']).abs()));
        final categoryColors = ['#3B82F6', '#10B981', '#8B5CF6', '#F59E0B', '#EF4444', '#14B8A6', '#EC4899'];
        budgetCategories = topSpending.take(4).toList().asMap().entries.map((entry) {
          final cat = entry.value;
          final index = entry.key;
          return DashboardBudgetCategory(
            name: (cat['categoryName'] ?? cat['CategoryName'] ?? 'Category').toString(),
            allocated: _parseDouble(cat['allocatedAmount'] ?? cat['AllocatedAmount']),
            spent: _parseDouble(cat['spentAmount'] ?? cat['SpentAmount']).abs(),
            color: categoryColors[index % categoryColors.length],
            rolloverBalance:
                _parseDouble(cat['rolloverBalance'] ?? cat['RolloverBalance']),
            allowRollover:
                (cat['allowRollover'] ?? cat['AllowRollover']) == true,
          );
        }).toList();
      }
      
      // Format recent transactions
      final recentTransactions = transactions.take(25).map((t) => DashboardTransaction(
        id: t.id,
        description: t.displayTitle,
        amount: t.amount,
        category: t.categoryName,
        date: t.date,
        type: t.type == TransactionType.credit ? 'credit' : 'debit',
      )).toList();
      
      return DashboardData(
        totalBalance: totalBalance,
        totalDebt: totalDebt,
        creditUsed: creditUsed,
        loanBalance: loanBalance,
        monthlyIncome: totalIncome,
        monthlyExpenses: totalExpenses,
        accountsCount: accounts.length,
        foreignCurrencyCodes: foreignCodes.toList()..sort(),
        monthlyIncomeChange: incomeChange,
        monthlyExpensesChange: expensesChange,
        budgetCategories: budgetCategories,
        recentTransactions: recentTransactions,
        budgetName: budgetName,
        budgetTotalIncome: budgetTotalIncome,
        budgetTotalSpent: budgetTotalSpent,
        budgetTotalRemaining: budgetTotalRemaining,
        budgetOverallPercentage: budgetOverallPercentage,
      );
    } catch (e) {
      rethrow;
    }
  }
  
  /// Report a swallowed dashboard-load failure to Sentry, tagged with the
  /// endpoint. The dashboard degrades gracefully (returns an empty default) so a
  /// parse/contract failure or 4xx here — which the central api_client
  /// interceptor never reports (it only fires on 5xx/network) — would otherwise
  /// leave ops blind to a $0/empty money display. Mirrors the Sentry capture on
  /// accounts_page. Best-effort; never let reporting break the load.
  void _reportSwallowed(String endpoint, Object e, StackTrace st) {
    // The user being offline (DNS/SocketException, timeouts) is their network,
    // not ours; the central interceptor drops those and so must this path, or a
    // phone opening the app in an elevator pages ops (Sentry 2026-09-08).
    if (e is DioException && neterr.isNetworkError(e)) return;
    unawaited(Sentry.captureException(
      e,
      stackTrace: st,
      withScope: (scope) {
        scope.setTag('dashboard.endpoint', endpoint);
      },
    ));
  }

  /// Get accounts list
  Future<List<Account>> _getAccounts() async {
    try {
      final response = await fetchAccountsJson(_apiClient);
      return response.map((e) => Account.fromJson(e as Map<String, dynamic>)).toList();
    } catch (e, st) {
      debugPrint('Error fetching accounts: $e');
      _reportSwallowed('/plaid/accounts', e, st);
      return [];
    }
  }

  /// Get recent transactions
  Future<List<Transaction>> _getRecentTransactions() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>('/transactions?pageSize=50');
      final transactions = response['transactions'] as List<dynamic>? ?? [];
      return transactions.map((e) => Transaction.fromJson(e as Map<String, dynamic>)).toList();
    } catch (e, st) {
      debugPrint('Error fetching transactions: $e');
      _reportSwallowed('/transactions', e, st);
      return [];
    }
  }

  /// Get current period summary (last 30 days)
  Future<Map<String, dynamic>?> _getCurrentPeriodSummary() async {
    try {
      final now = DateTime.now();
      final startDate = now.subtract(const Duration(days: 30));
      final response = await _apiClient.get<Map<String, dynamic>>(
        '/transactions/summary?startDate=${_formatDate(startDate)}&endDate=${_formatDate(now)}'
      );
      return response;
    } catch (e, st) {
      debugPrint('Error fetching current summary: $e');
      _reportSwallowed('/transactions/summary', e, st);
      return null;
    }
  }
  
  /// Get previous period summary (31-60 days ago)
  Future<Map<String, dynamic>?> _getPreviousPeriodSummary() async {
    try {
      final endDate = DateTime.now().subtract(const Duration(days: 30));
      final startDate = DateTime.now().subtract(const Duration(days: 60));
      final response = await _apiClient.get<Map<String, dynamic>>(
        '/transactions/summary?startDate=${_formatDate(startDate)}&endDate=${_formatDate(endDate)}'
      );
      return response;
    } catch (e) {
      debugPrint('Error fetching previous summary: $e');
      return null;
    }
  }
  
  /// Get active personal budget
  Future<Map<String, dynamic>?> _getActiveBudget() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>('/budget/active');
      return response;
    } catch (e) {
      // 404 means no active budget - this is normal
      debugPrint('No active budget: $e');
      return null;
    }
  }
  
  /// Get family budgets
  Future<List<Map<String, dynamic>>> _getFamilyBudgets() async {
    try {
      final response = await _apiClient.get<List<dynamic>>('/budget/family');
      return response.map((e) => e as Map<String, dynamic>).toList();
    } catch (e) {
      // 404 means user not in family - this is normal
      debugPrint('No family budgets: $e');
      return [];
    }
  }
  
  /// Format date as YYYY-MM-DD
  String _formatDate(DateTime date) {
    return '${date.year}-${date.month.toString().padLeft(2, '0')}-${date.day.toString().padLeft(2, '0')}';
  }
  
  /// Parse double safely
  double _parseDouble(dynamic value) {
    if (value == null) return 0.0;
    if (value is double) return value;
    if (value is int) return value.toDouble();
    return double.tryParse(value.toString()) ?? 0.0;
  }

  /// Period-over-period % change, or null when the number would be noise rather than signal.
  ///
  /// Two guards, because a raw `(now - prev) / prev * 100` is worse than showing nothing:
  ///  - the prior window needs a real base ([_changeBaseFloor]); a $30 prior month against a
  ///    $16k current one is a 50,000% "increase" that means only "last window was ~empty".
  ///  - swings beyond [_changeCap] are almost always a ramping/new account, not a real trend, and
  ///    a "+894%" chip erodes trust more than it informs. Suppress rather than display those.
  static const double _changeBaseFloor = 100.0;
  static const double _changeCap = 150.0;
  double? _meaningfulChange(double current, double previous) {
    if (previous < _changeBaseFloor) return null;
    final change = ((current - previous) / previous) * 100;
    if (change.abs() > _changeCap) return null;
    return change;
  }
  
  /// Public method to get accounts
  Future<List<Account>> getAccounts() async {
    return _getAccounts();
  }
}

// Global singleton instance
final dashboardService = DashboardService(apiClient);
