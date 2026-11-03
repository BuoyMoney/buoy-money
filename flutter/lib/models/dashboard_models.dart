import '../theme/app_theme.dart';
import '../utils/currency.dart' as currency_util;

/// Account model - matches backend Account entity
class Account {
  final int id;
  final String plaidAccountId;
  final String name;
  final String? officialName;
  final String? nickname;
  final String? mask;
  final int type;
  final String subtype;
  final double currentBalance;
  final double? availableBalance;
  final String currency;
  final bool isActive;
  final int? ownerUserId;
  final String? ownerName;
  final int? associatedUserId; // Whose budget this account feeds by default (null = family)

  Account({
    required this.id,
    required this.plaidAccountId,
    required this.name,
    this.officialName,
    this.nickname,
    this.mask,
    required this.type,
    required this.subtype,
    required this.currentBalance,
    this.availableBalance,
    this.currency = 'USD',
    this.isActive = true,
    this.ownerUserId,
    this.ownerName,
    this.associatedUserId,
  });
  
  /// Display name - nickname if set, otherwise name
  String get displayName => nickname ?? name;
  
  /// Account type name
  String get typeName => AppTheme.getAccountTypeName(type);
  
  /// Whether this is a liability (credit card or loan)
  bool get isLiability => type == 2 || type == 4;
  
  /// Whether this is an asset
  bool get isAsset => !isLiability;
  
  factory Account.fromJson(Map<String, dynamic> json) {
    return Account(
      id: _parseInt(json['id'] ?? json['Id']),
      plaidAccountId: (json['plaidAccountId'] ?? json['PlaidAccountId'] ?? '').toString(),
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      officialName: json['officialName']?.toString() ?? json['OfficialName']?.toString(),
      nickname: json['nickname']?.toString() ?? json['Nickname']?.toString(),
      mask: json['mask']?.toString() ?? json['Mask']?.toString(),
      type: _parseInt(json['type'] ?? json['Type']),
      subtype: (json['subtype'] ?? json['Subtype'] ?? '').toString(),
      currentBalance: _parseDouble(json['currentBalance'] ?? json['CurrentBalance']),
      availableBalance: json['availableBalance'] != null || json['AvailableBalance'] != null
          ? _parseDouble(json['availableBalance'] ?? json['AvailableBalance'])
          : null,
      currency: (json['currency'] ?? json['Currency'] ?? 'USD').toString(),
      isActive: _parseBool(json['isActive'] ?? json['IsActive']),
      ownerUserId: json['ownerUserId'] != null ? _parseInt(json['ownerUserId']) : null,
      ownerName: json['ownerName']?.toString(),
      associatedUserId: (json['associatedUserId'] ?? json['AssociatedUserId']) != null
          ? _parseInt(json['associatedUserId'] ?? json['AssociatedUserId'])
          : null,
    );
  }
  
  static int _parseInt(dynamic value) {
    if (value == null) return 0;
    if (value is int) return value;
    return int.tryParse(value.toString()) ?? 0;
  }
  
  static double _parseDouble(dynamic value) {
    if (value == null) return 0.0;
    if (value is double) return value;
    if (value is int) return value.toDouble();
    return double.tryParse(value.toString()) ?? 0.0;
  }
  
  static bool _parseBool(dynamic value) {
    if (value == null) return true;
    if (value is bool) return value;
    return value.toString().toLowerCase() == 'true';
  }
}

/// Dashboard transaction - simplified for display
class DashboardTransaction {
  final int id;
  final String description;
  final double amount;
  final String category;
  final DateTime date;
  final String type; // 'debit' or 'credit'
  
  DashboardTransaction({
    required this.id,
    required this.description,
    required this.amount,
    required this.category,
    required this.date,
    required this.type,
  });
  
  // Trust the credit/debit type, which the backend sets from TransactionType. The old
  // `type == 'credit' || amount > 0` let the amount override the type: a debit whose amount
  // arrives as a positive magnitude (e.g. the seed data, or any absolute-value payload) then
  // read as income and rendered green +. Fall back to the amount sign ONLY when the type is
  // genuinely unknown.
  bool get isIncome {
    if (type == 'credit') return true;
    if (type == 'debit') return false;
    return amount > 0;
  }

  bool get isExpense => !isIncome;
  
  factory DashboardTransaction.fromJson(Map<String, dynamic> json) {
    return DashboardTransaction(
      id: int.tryParse((json['id'] ?? 0).toString()) ?? 0,
      description: (json['description'] ?? json['merchantName'] ?? 'Transaction').toString(),
      amount: _parseDouble(json['amount']),
      category: (json['category'] ?? 'Uncategorized').toString(),
      date: DateTime.tryParse(json['date']?.toString() ?? '') ?? DateTime.now(),
      type: (json['type'] ?? 'debit').toString(),
    );
  }
  
  static double _parseDouble(dynamic value) {
    if (value == null) return 0.0;
    if (value is double) return value;
    if (value is int) return value.toDouble();
    return double.tryParse(value.toString()) ?? 0.0;
  }
}

/// Dashboard budget category
class DashboardBudgetCategory {
  final String name;
  final double allocated;
  final double spent;
  final String color;
  // Rollover carried in from last month (positive = saved, negative = overspent),
  // and whether rollover is enabled for this category. Drives the rollover chip.
  final double rolloverBalance;
  final bool allowRollover;

  DashboardBudgetCategory({
    required this.name,
    required this.allocated,
    required this.spent,
    required this.color,
    this.rolloverBalance = 0,
    this.allowRollover = false,
  });

  double get remaining => allocated - spent;
  double get percentUsed => allocated > 0 ? (spent / allocated * 100).clamp(0, 100) : 0;
  bool get isOverBudget => spent > allocated;
  bool get hasRollover => allowRollover && rolloverBalance != 0;

  factory DashboardBudgetCategory.fromJson(Map<String, dynamic> json) {
    return DashboardBudgetCategory(
      name: (json['name'] ?? 'Category').toString(),
      allocated: _parseDouble(json['allocated']),
      spent: _parseDouble(json['spent']),
      color: (json['color'] ?? '#6366F1').toString(),
      rolloverBalance: _parseDouble(json['rolloverBalance']),
      allowRollover: json['allowRollover'] == true,
    );
  }
  
  static double _parseDouble(dynamic value) {
    if (value == null) return 0.0;
    if (value is double) return value;
    if (value is int) return value.toDouble();
    return double.tryParse(value.toString()) ?? 0.0;
  }
}

/// Dashboard data - aggregated view
class DashboardData {
  final double totalBalance;
  final double totalDebt;
  final double creditUsed;
  final double loanBalance;
  final double monthlyIncome;
  final double monthlyExpenses;
  final int accountsCount;

  /// ISO codes of foreign-currency accounts excluded from the balance/debt
  /// totals above (different currencies can't be summed together). Empty when
  /// every account is in the primary currency.
  final List<String> foreignCurrencyCodes;

  final double? monthlyIncomeChange;
  final double? monthlyExpensesChange;
  final List<DashboardBudgetCategory> budgetCategories;
  final List<DashboardTransaction> recentTransactions;
  
  // Budget summary
  final String? budgetName;
  final double? budgetTotalIncome;
  final double? budgetTotalSpent;
  final double? budgetTotalRemaining;
  final double? budgetOverallPercentage;
  
  DashboardData({
    required this.totalBalance,
    required this.totalDebt,
    required this.creditUsed,
    required this.loanBalance,
    required this.monthlyIncome,
    required this.monthlyExpenses,
    required this.accountsCount,
    this.foreignCurrencyCodes = const [],
    this.monthlyIncomeChange,
    this.monthlyExpensesChange,
    this.budgetCategories = const [],
    this.recentTransactions = const [],
    this.budgetName,
    this.budgetTotalIncome,
    this.budgetTotalSpent,
    this.budgetTotalRemaining,
    this.budgetOverallPercentage,
  });
  
  factory DashboardData.fromJson(Map<String, dynamic> json) {
    return DashboardData(
      totalBalance: _parseDouble(json['totalBalance']),
      totalDebt: _parseDouble(json['totalDebt']),
      creditUsed: _parseDouble(json['creditUsed']),
      loanBalance: _parseDouble(json['loanBalance']),
      monthlyIncome: _parseDouble(json['monthlyIncome']),
      monthlyExpenses: _parseDouble(json['monthlyExpenses']),
      accountsCount: int.tryParse((json['accountsCount'] ?? 0).toString()) ?? 0,
      monthlyIncomeChange: json['monthlyIncomeChange'] != null 
          ? _parseDouble(json['monthlyIncomeChange']) : null,
      monthlyExpensesChange: json['monthlyExpensesChange'] != null
          ? _parseDouble(json['monthlyExpensesChange']) : null,
      budgetCategories: (json['budgetCategories'] as List?)
          ?.map((e) => DashboardBudgetCategory.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      recentTransactions: (json['recentTransactions'] as List?)
          ?.map((e) => DashboardTransaction.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      budgetName: json['budgetName']?.toString(),
      budgetTotalIncome: json['budgetTotalIncome'] != null
          ? _parseDouble(json['budgetTotalIncome']) : null,
      budgetTotalSpent: json['budgetTotalSpent'] != null
          ? _parseDouble(json['budgetTotalSpent']) : null,
      budgetTotalRemaining: json['budgetTotalRemaining'] != null
          ? _parseDouble(json['budgetTotalRemaining']) : null,
      budgetOverallPercentage: json['budgetOverallPercentage'] != null
          ? _parseDouble(json['budgetOverallPercentage']) : null,
    );
  }
  
  static double _parseDouble(dynamic value) {
    if (value == null) return 0.0;
    if (value is double) return value;
    if (value is int) return value.toDouble();
    return double.tryParse(value.toString()) ?? 0.0;
  }
  
  /// Empty dashboard for loading/error states
  factory DashboardData.empty() {
    return DashboardData(
      totalBalance: 0,
      totalDebt: 0,
      creditUsed: 0,
      loanBalance: 0,
      monthlyIncome: 0,
      monthlyExpenses: 0,
      accountsCount: 0,
    );
  }
  
  /// Create from aggregated data
  factory DashboardData.fromAggregated({
    required double totalBalance,
    required double totalIncome,
    required double totalExpenses,
    required List<Account> accounts,
    required List<dynamic> recentTransactions,
  }) {
    // Calculate credit used and loan balance from accounts. Foreign-currency
    // accounts are excluded (their amounts aren't in the totals' currency) and
    // reported via foreignCurrencyCodes instead.
    double creditUsed = 0;
    double loanBalance = 0;
    final foreignCodes = <String>{};

    for (final account in accounts) {
      if (!currency_util.isPrimaryCurrency(account.currency)) {
        foreignCodes.add(account.currency.toUpperCase());
        continue;
      }
      if (account.type == 2) {
        creditUsed += account.currentBalance.abs();
      } else if (account.type == 3 || account.type == 4) {
        loanBalance += account.currentBalance.abs();
      }
    }

    return DashboardData(
      totalBalance: totalBalance,
      totalDebt: creditUsed + loanBalance,
      creditUsed: creditUsed,
      loanBalance: loanBalance,
      monthlyIncome: totalIncome,
      monthlyExpenses: totalExpenses.abs(),
      accountsCount: accounts.length,
      foreignCurrencyCodes: foreignCodes.toList()..sort(),
      recentTransactions: recentTransactions.map((t) {
        if (t is DashboardTransaction) return t;
        // Convert Transaction to DashboardTransaction
        return DashboardTransaction(
          id: t.id as int,
          description: (t.merchantName ?? t.name ?? 'Transaction').toString(),
          amount: (t.amount as num).toDouble(),
          category: t.category.toString().split('.').last,
          date: t.date as DateTime,
          type: (t.amount as num) >= 0 ? 'credit' : 'debit',
        );
      }).toList(),
    );
  }
}
