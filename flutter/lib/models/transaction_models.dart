// Transaction models for BuoyAI Flutter app

import 'covering_transfer_models.dart';

/// Transaction type enum
enum TransactionType {
  debit,   // 0
  credit,  // 1
  expense, // alias for debit
  income,  // alias for credit
  transfer, // 2
  ;

  /// Deserialize the backend `type` field defensively. The API sends an integer
  /// (`Debit = 0`, `Credit = 1`; `2` reserved for transfers), but we also accept
  /// a string name. Anything unexpected or out-of-range falls back to [debit]
  /// instead of throwing a RangeError — a malformed payload must never crash the
  /// transactions list. Mirrors the `fromValue`/orElse pattern in
  /// subscription_models.dart.
  static TransactionType fromJson(dynamic value) {
    if (value == null) return TransactionType.debit;
    if (value is int) return _fromInt(value);
    final s = value.toString().trim().toLowerCase();
    final asInt = int.tryParse(s);
    if (asInt != null) return _fromInt(asInt);
    switch (s) {
      case 'debit':
      case 'expense':
        return TransactionType.debit;
      case 'credit':
      case 'income':
        return TransactionType.credit;
      case 'transfer':
        return TransactionType.transfer;
      default:
        return TransactionType.debit;
    }
  }

  static TransactionType _fromInt(int value) {
    switch (value) {
      case 0:
        return TransactionType.debit;
      case 1:
        return TransactionType.credit;
      case 2:
        return TransactionType.transfer;
      default:
        return TransactionType.debit;
    }
  }
}

/// Transaction category enum - matches backend TransactionCategory
enum TransactionCategory {
  // Income
  salary(1),
  bonus(2),
  investment(3),
  otherIncome(4),

  // Fixed Expenses
  rent(10),
  utilities(11),
  insurance(12),
  subscriptions(13),
  loanPayments(14),

  // Variable Expenses
  groceries(20),
  diningOut(21),
  transportation(22),
  entertainment(23),
  shopping(24),
  healthcare(25),
  travel(26),
  personalCare(27),
  education(28),
  gifts(29),

  // Transfers & Payments
  creditPayment(30),
  transfer(31),

  // Uncategorized
  uncategorized(99);

  final int value;
  const TransactionCategory(this.value);

  static TransactionCategory fromValue(int value) {
    return TransactionCategory.values.firstWhere(
      (e) => e.value == value,
      orElse: () => TransactionCategory.uncategorized,
    );
  }
  
  /// Human-readable name
  String get displayName {
    switch (this) {
      case TransactionCategory.salary: return 'Salary';
      case TransactionCategory.bonus: return 'Bonus';
      case TransactionCategory.investment: return 'Investment';
      case TransactionCategory.otherIncome: return 'Other Income';
      case TransactionCategory.rent: return 'Rent';
      case TransactionCategory.utilities: return 'Utilities';
      case TransactionCategory.insurance: return 'Insurance';
      case TransactionCategory.subscriptions: return 'Subscriptions';
      case TransactionCategory.loanPayments: return 'Loan Payments';
      case TransactionCategory.groceries: return 'Groceries';
      case TransactionCategory.diningOut: return 'Dining Out';
      case TransactionCategory.transportation: return 'Transportation';
      case TransactionCategory.entertainment: return 'Entertainment';
      case TransactionCategory.shopping: return 'Shopping';
      case TransactionCategory.healthcare: return 'Healthcare';
      case TransactionCategory.travel: return 'Travel';
      case TransactionCategory.personalCare: return 'Personal Care';
      case TransactionCategory.education: return 'Education';
      case TransactionCategory.gifts: return 'Gifts';
      case TransactionCategory.creditPayment: return 'Credit Payment';
      case TransactionCategory.transfer: return 'Transfer';
      case TransactionCategory.uncategorized: return 'Uncategorized';
    }
  }
  
  /// Whether this is an income category
  bool get isIncome {
    return value >= 1 && value <= 4;
  }
}

/// Transaction DTO
class Transaction {
  final int id;
  final int accountId;
  final String? plaidTransactionId;
  final double amount;
  final TransactionType type;

  /// ISO-4217 currency of [amount], as reported by the bank. Null means the
  /// bank didn't report one (treat as the account's currency). Amounts with
  /// differing codes must never be summed together.
  final String? isoCurrencyCode;

  final String description;
  final String? merchantName;

  /// The user's own name for this transaction, if they renamed it. Null when they haven't.
  final String? nickname;

  final int? categoryId;          // 1-99 system, 1000+ custom
  final String categoryName;
  final String? categoryColor;
  final bool isUserCategorized;
  final DateTime date;
  final String accountName;
  final bool isRecurring;
  final int? budgetId;
  final int? claimedByUserId;
  final String? claimedByUserName;
  final int? personalCategoryId;
  final String? personalCategoryName;
  final String? personalCategoryColor;
  final String? plaidCategoryDetailed;
  final String? merchantLogoUrl;
  final String? merchantWebsite;
  final String? paymentChannel;
  final String? locationCity;
  final String? locationRegion;
  final bool isPending;
  final int? aiSuggestedCategoryId;
  final String? aiSuggestedCategoryName;
  final double? aiConfidence;
  final bool needsReview;
  final String? skipUntil;
  final String? categorySource;
  final String? semanticTags;
  final String? userNotes;
  final bool isSplit;
  final int? parentTransactionId;
  final List<TransactionSplit>? splits;
  final int? savingsGoalId;
  final String? savingsGoalName;

  /// The real account the earmarked goal lives in, when linked. Non-null on an
  /// expense means this is a *draw* that a transfer can settle.
  final int? savingsGoalAccountId;

  /// The transfer that settled this draw, or null while it is still in flight.
  final int? coveringTransferTransactionId;

  /// Summary of that transfer; only populated on single-transaction reads.
  final CoveringTransfer? coveringTransfer;

  Transaction({
    required this.id,
    required this.accountId,
    this.plaidTransactionId,
    required this.amount,
    required this.type,
    this.isoCurrencyCode,
    required this.description,
    this.merchantName,
    this.nickname,
    this.categoryId,
    required this.categoryName,
    this.categoryColor,
    this.isUserCategorized = false,
    required this.date,
    required this.accountName,
    this.isRecurring = false,
    this.budgetId,
    this.claimedByUserId,
    this.claimedByUserName,
    this.personalCategoryId,
    this.personalCategoryName,
    this.personalCategoryColor,
    this.plaidCategoryDetailed,
    this.merchantLogoUrl,
    this.merchantWebsite,
    this.paymentChannel,
    this.locationCity,
    this.locationRegion,
    this.isPending = false,
    this.aiSuggestedCategoryId,
    this.aiSuggestedCategoryName,
    this.aiConfidence,
    this.needsReview = false,
    this.skipUntil,
    this.categorySource,
    this.semanticTags,
    this.userNotes,
    this.isSplit = false,
    this.parentTransactionId,
    this.splits,
    this.savingsGoalId,
    this.savingsGoalName,
    this.savingsGoalAccountId,
    this.coveringTransferTransactionId,
    this.coveringTransfer,
  });

  factory Transaction.fromJson(Map<String, dynamic> json) {
    return Transaction(
      id: (json['id'] as num?)?.toInt() ?? 0,
      accountId: (json['accountId'] as num?)?.toInt() ?? 0,
      plaidTransactionId: json['plaidTransactionId'] as String?,
      amount: (json['amount'] as num?)?.toDouble() ?? 0,
      type: TransactionType.fromJson(json['type']),
      isoCurrencyCode: json['isoCurrencyCode'] as String?,
      description: json['description'] as String? ?? '',
      merchantName: json['merchantName'] as String?,
      nickname: json['nickname'] as String?,
      categoryId: json['categoryId'] as int?,
      categoryName: json['categoryName'] as String? ?? 'Uncategorized',
      categoryColor: json['categoryColor'] as String?,
      isUserCategorized: json['isUserCategorized'] as bool? ?? false,
      // Defensive date parse: a single malformed/missing/PascalCase `date`
      // must never crash the whole transactions-list parse (matches
      // TransactionType.fromJson's stated contract and TransactionSummary's
      // own fallback-to-now handling).
      date: DateTime.tryParse((json['date'] ?? json['Date'])?.toString() ?? '') ??
          DateTime.now(),
      accountName: json['accountName'] as String? ?? '',
      isRecurring: json['isRecurring'] as bool? ?? false,
      budgetId: json['budgetId'] as int?,
      claimedByUserId: json['claimedByUserId'] as int?,
      claimedByUserName: json['claimedByUserName'] as String?,
      personalCategoryId: json['personalCategoryId'] as int?,
      personalCategoryName: json['personalCategoryName'] as String?,
      personalCategoryColor: json['personalCategoryColor'] as String?,
      plaidCategoryDetailed: json['plaidCategoryDetailed'] as String?,
      merchantLogoUrl: json['merchantLogoUrl'] as String?,
      merchantWebsite: json['merchantWebsite'] as String?,
      paymentChannel: json['paymentChannel'] as String?,
      locationCity: json['locationCity'] as String?,
      locationRegion: json['locationRegion'] as String?,
      isPending: json['isPending'] as bool? ?? false,
      aiSuggestedCategoryId: json['aiSuggestedCategoryId'] as int?,
      aiSuggestedCategoryName: json['aiSuggestedCategoryName'] as String?,
      aiConfidence: (json['aiConfidence'] as num?)?.toDouble(),
      needsReview: json['needsReview'] as bool? ?? false,
      skipUntil: json['skipUntil'] as String?,
      categorySource: json['categorySource'] as String?,
      semanticTags: (json['semanticTags'] ?? json['SemanticTags']) as String?,
      userNotes: json['userNotes'] as String?,
      isSplit: json['isSplit'] as bool? ?? false,
      parentTransactionId: json['parentTransactionId'] as int?,
      splits: (json['splits'] as List<dynamic>?)
          ?.map((e) => TransactionSplit.fromJson(e as Map<String, dynamic>))
          .toList(),
      savingsGoalId: (json['savingsGoalId'] ?? json['SavingsGoalId']) as int?,
      savingsGoalName:
          (json['savingsGoalName'] ?? json['SavingsGoalName']) as String?,
      savingsGoalAccountId:
          (json['savingsGoalAccountId'] ?? json['SavingsGoalAccountId']) as int?,
      coveringTransferTransactionId: (json['coveringTransferTransactionId'] ??
          json['CoveringTransferTransactionId']) as int?,
      coveringTransfer: (json['coveringTransfer'] ?? json['CoveringTransfer'])
              is Map<String, dynamic>
          ? CoveringTransfer.fromJson(
              (json['coveringTransfer'] ?? json['CoveringTransfer'])
                  as Map<String, dynamic>)
          : null,
    );
  }

  /// An expense paid from a goal that lives in a real account: the envelope
  /// already dropped, and a transfer out of that account will (or did) move the
  /// money. Income earmarks deposit instead and have nothing to settle.
  bool get isDrawFromLinkedGoal =>
      savingsGoalId != null && savingsGoalAccountId != null && !isIncome;

  bool get isCoveredByTransfer => coveringTransferTransactionId != null;

  /// What to render as this transaction's title.
  ///
  /// Use this instead of `merchantName ?? description`. Both bank fields arrive as EMPTY
  /// STRINGS rather than null (the backend defaults them to `string.Empty`), so `??` never
  /// fires and the title renders blank — including when `description` had perfectly good text.
  /// Blank is the condition to test, not null.
  String get displayTitle {
    final n = nickname?.trim() ?? '';
    if (n.isNotEmpty) return n;
    final m = merchantName?.trim() ?? '';
    if (m.isNotEmpty) return m;
    final d = description.trim();
    if (d.isNotEmpty) return d;
    return 'Unnamed transaction';
  }

  /// Whether the transaction has any real name — the bank's or the user's. False means
  /// [displayTitle] is the "Unnamed transaction" placeholder, not actual data.
  bool get hasName => (nickname?.trim().isNotEmpty ?? false) || hasBankName;

  /// What the bank called this, ignoring any rename. Empty when the bank sent nothing —
  /// which is common: Plaid supplies no merchant for checks or account transfers.
  String get bankName {
    final m = merchantName?.trim() ?? '';
    if (m.isNotEmpty) return m;
    return description.trim();
  }

  /// Whether the bank gave this transaction any name of its own.
  bool get hasBankName => bankName.isNotEmpty;

  /// Whether this is an income transaction (based on type, not amount sign)
  bool get isIncome => type == TransactionType.credit || type == TransactionType.income;

  /// Whether this transaction is earmarked to a savings goal
  bool get isEarmarkedToGoal => savingsGoalId != null;

  /// Whether AI has suggested a category that the user hasn't confirmed
  bool get isAICategorized => aiSuggestedCategoryId != null && !isUserCategorized;

  /// Legacy: get TransactionCategory enum from categoryId (for old pages)
  TransactionCategory get category =>
      TransactionCategory.fromValue(categoryId ?? 99);
  
  /// Display amount (positive for income, negative for expenses)
  double get displayAmount => isIncome ? amount.abs() : -amount.abs();
  
  /// Alias for description (used in UI)
  String get name => description;
  
  /// Alias for isPending (used in UI)
  bool get pending => isPending;
  
  /// Combined location string
  String? get location {
    if (locationCity != null && locationRegion != null) {
      return '$locationCity, $locationRegion';
    }
    return locationCity ?? locationRegion;
  }
}

/// Transaction split DTO
class TransactionSplit {
  final int id;
  final int transactionId;
  final double amount;
  final int? categoryId;
  final String categoryName;
  final String? categoryColor;

  /// The part's own notes; a receipt split lists the part's items here.
  final String? notes;

  TransactionSplit({
    required this.id,
    required this.transactionId,
    required this.amount,
    this.categoryId,
    required this.categoryName,
    this.categoryColor,
    this.notes,
  });

  factory TransactionSplit.fromJson(Map<String, dynamic> json) {
    return TransactionSplit(
      id: json['id'] as int,
      transactionId: json['transactionId'] as int,
      amount: (json['amount'] as num).toDouble(),
      categoryId: json['categoryId'] as int?,
      categoryName: json['categoryName'] as String? ?? '',
      categoryColor: json['categoryColor'] as String?,
      notes: (json['notes'] ?? json['Notes']) as String?,
    );
  }
}

/// Pagination metadata
class PaginationMetadata {
  final int currentPage;
  final int pageSize;
  final int totalPages;
  final int totalItems;
  final bool hasNextPage;
  final bool hasPreviousPage;

  PaginationMetadata({
    required this.currentPage,
    required this.pageSize,
    required this.totalPages,
    required this.totalItems,
    required this.hasNextPage,
    required this.hasPreviousPage,
  });

  factory PaginationMetadata.fromJson(Map<String, dynamic> json) {
    return PaginationMetadata(
      currentPage: json['currentPage'] as int? ?? 1,
      pageSize: json['pageSize'] as int? ?? 20,
      totalPages: json['totalPages'] as int? ?? 1,
      totalItems: json['totalItems'] as int? ?? 0,
      hasNextPage: json['hasNextPage'] as bool? ?? false,
      hasPreviousPage: json['hasPreviousPage'] as bool? ?? false,
    );
  }

  PaginationMetadata copyWith({
    int? currentPage,
    int? pageSize,
    int? totalPages,
    int? totalItems,
    bool? hasNextPage,
    bool? hasPreviousPage,
  }) {
    return PaginationMetadata(
      currentPage: currentPage ?? this.currentPage,
      pageSize: pageSize ?? this.pageSize,
      totalPages: totalPages ?? this.totalPages,
      totalItems: totalItems ?? this.totalItems,
      hasNextPage: hasNextPage ?? this.hasNextPage,
      hasPreviousPage: hasPreviousPage ?? this.hasPreviousPage,
    );
  }
}

/// Transaction summary
class TransactionSummary {
  final double totalIncome;
  final double totalExpenses;
  final double netAmount;
  final int transactionCount;
  final DateTime periodStart;
  final DateTime periodEnd;
  final Map<String, double> categoryTotals;
  final Map<String, int> categoryCounts;

  TransactionSummary({
    required this.totalIncome,
    required this.totalExpenses,
    required this.netAmount,
    required this.transactionCount,
    required this.periodStart,
    required this.periodEnd,
    required this.categoryTotals,
    required this.categoryCounts,
  });

  factory TransactionSummary.fromJson(Map<String, dynamic> json) {
    return TransactionSummary(
      totalIncome: (json['totalIncome'] as num?)?.toDouble() ?? 0,
      totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
      netAmount: (json['netAmount'] as num?)?.toDouble() ?? 0,
      transactionCount: json['transactionCount'] as int? ?? 0,
      periodStart: DateTime.parse(json['periodStart'] as String? ?? DateTime.now().toIso8601String()),
      periodEnd: DateTime.parse(json['periodEnd'] as String? ?? DateTime.now().toIso8601String()),
      categoryTotals: (json['categoryTotals'] as Map<String, dynamic>?)
          ?.map((k, v) => MapEntry(k, (v as num).toDouble())) ?? {},
      categoryCounts: (json['categoryCounts'] as Map<String, dynamic>?)
          ?.map((k, v) => MapEntry(k, v as int)) ?? {},
    );
  }
  
  factory TransactionSummary.empty() {
    return TransactionSummary(
      totalIncome: 0,
      totalExpenses: 0,
      netAmount: 0,
      transactionCount: 0,
      periodStart: DateTime.now(),
      periodEnd: DateTime.now(),
      categoryTotals: {},
      categoryCounts: {},
    );
  }
}

/// Transaction response with pagination
class TransactionResponse {
  final List<Transaction> transactions;
  final PaginationMetadata pagination;
  final TransactionSummary summary;

  TransactionResponse({
    required this.transactions,
    required this.pagination,
    required this.summary,
  });

  factory TransactionResponse.fromJson(Map<String, dynamic> json) {
    return TransactionResponse(
      transactions: (json['transactions'] as List<dynamic>?)
          ?.map((e) => Transaction.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      pagination: PaginationMetadata.fromJson(
          json['pagination'] as Map<String, dynamic>? ?? {}),
      summary: TransactionSummary.fromJson(
          json['summary'] as Map<String, dynamic>? ?? {}),
    );
  }
}

/// Transaction filter request
class TransactionFilter {
  final DateTime? startDate;
  final DateTime? endDate;
  final int? categoryId;
  final int? accountId;
  final String? search;
  final double? minAmount;
  final double? maxAmount;
  final int page;
  final int pageSize;

  // Legacy fields - only used by old transactions_page.dart, not sent to API
  final TransactionType? type;
  final TransactionCategory? category;
  final TransactionSortField? sortField;
  final SortDirection? sortDirection;

  TransactionFilter({
    this.startDate,
    this.endDate,
    this.categoryId,
    this.accountId,
    this.search,
    this.minAmount,
    this.maxAmount,
    this.page = 1,
    this.pageSize = 25,
    this.type,
    this.category,
    this.sortField,
    this.sortDirection,
  });

  /// Whether any filters are currently active
  bool get hasActiveFilters {
    return startDate != null ||
        endDate != null ||
        categoryId != null ||
        accountId != null ||
        (search != null && search!.isNotEmpty) ||
        minAmount != null ||
        maxAmount != null;
  }

  Map<String, dynamic> toQueryParams() {
    final params = <String, dynamic>{};
    if (startDate != null) params['startDate'] = startDate!.toIso8601String().split('T')[0];
    if (endDate != null) params['endDate'] = endDate!.toIso8601String().split('T')[0];
    if (categoryId != null) params['categoryId'] = categoryId;
    if (accountId != null) params['accountId'] = accountId;
    if (search != null && search!.isNotEmpty) params['searchTerm'] = search;
    if (minAmount != null) params['minAmount'] = minAmount;
    if (maxAmount != null) params['maxAmount'] = maxAmount;
    params['page'] = page;
    params['pageSize'] = pageSize;
    return params;
  }
  
  TransactionFilter copyWith({
    DateTime? startDate,
    DateTime? endDate,
    int? categoryId,
    int? accountId,
    String? search,
    double? minAmount,
    double? maxAmount,
    int? page,
    int? pageSize,
    bool clearCategoryId = false,
    bool clearAccountId = false,
    bool clearMinAmount = false,
    bool clearMaxAmount = false,
    bool clearStartDate = false,
    bool clearEndDate = false,
    // Legacy params for old page compat
    TransactionType? type,
    TransactionCategory? category,
    TransactionSortField? sortField,
    SortDirection? sortDirection,
    bool clearType = false,
    bool clearCategory = false,
  }) {
    return TransactionFilter(
      startDate: clearStartDate ? null : (startDate ?? this.startDate),
      endDate: clearEndDate ? null : (endDate ?? this.endDate),
      categoryId: clearCategoryId ? null : (categoryId ?? this.categoryId),
      accountId: clearAccountId ? null : (accountId ?? this.accountId),
      search: search ?? this.search,
      minAmount: clearMinAmount ? null : (minAmount ?? this.minAmount),
      maxAmount: clearMaxAmount ? null : (maxAmount ?? this.maxAmount),
      page: page ?? this.page,
      pageSize: pageSize ?? this.pageSize,
      type: clearType ? null : (type ?? this.type),
      category: clearCategory ? null : (category ?? this.category),
      sortField: sortField ?? this.sortField,
      sortDirection: sortDirection ?? this.sortDirection,
    );
  }
}

// Legacy enums - only used by old transactions_page.dart for compilation.
// Backend sorts by date desc; client-side sorting is unnecessary.
enum SortDirection { asc, desc, ascending, descending }

enum TransactionSortField { date, amount, name, merchant }
