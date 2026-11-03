// Budget models for BuoyAI Flutter app
// Mirrors backend DTOs from BuoyAI.Core/DTOs/Budget/

import 'transaction_models.dart';

// ═══════════════════════════════════════════
//  ENUMS
// ═══════════════════════════════════════════

enum BudgetPeriod {
  weekly,    // 0
  biWeekly,  // 1
  monthly,   // 2
  quarterly, // 3
  yearly,    // 4
}

extension BudgetPeriodExtension on BudgetPeriod {
  String get displayName {
    switch (this) {
      case BudgetPeriod.weekly: return 'Weekly';
      case BudgetPeriod.biWeekly: return 'Bi-Weekly';
      case BudgetPeriod.monthly: return 'Monthly';
      case BudgetPeriod.quarterly: return 'Quarterly';
      case BudgetPeriod.yearly: return 'Yearly';
    }
  }
}

enum BudgetType {
  personal,  // 0
  family,    // 1
  allowance, // 2
}

enum ChildBudgetRolloverMode {
  rolloverToSelf,     // 0
  allocateToSavings,  // 1
  promptUser,         // 2
}

enum CountUpRolloverMode {
  rolloverToSelf,       // 0
  transferToSavings,    // 1
  resetToZero,          // 2
}

enum BudgetCategoryStatus { onTrack, nearLimit, overBudget }
enum BudgetStatus { onTrack, nearLimit, overBudget }

// ═══════════════════════════════════════════
//  BUDGET CATEGORY DTO
// ═══════════════════════════════════════════

class BudgetCategory {
  final int id;
  final int budgetId;
  final int categoryId;
  final TransactionCategory category;
  final String categoryName;
  final double allocatedAmount;
  final double spentAmount;
  final double remainingAmount;
  final double percentageUsed;
  final bool isIncome;
  final double? defaultAllocatedAmount;
  final int? groupId;
  final String? groupName;
  final int sortOrder;
  // Rollover
  final bool allowRollover;
  final double rolloverBalance;
  final double? maxRolloverMultiplier;
  final int? rolloverOverflowSavingsGoalId;
  final String? rolloverOverflowSavingsGoalName;
  // Count-up
  final bool isCountUpEnabled;
  final CountUpRolloverMode? countUpRolloverMode;
  final int? savingsGoalId;
  final String? savingsGoalName;
  // Allowance
  final bool isAllowance;
  final int? linkedBudgetId;
  final int? assignedUserId;
  final String? assignedUserName;
  // Misc
  final bool isVirtual;
  final int transactionCount;

  double get effectiveAmount => allocatedAmount + rolloverBalance;
  String get name => categoryName;

  BudgetCategoryStatus get status {
    if (percentageUsed >= 100) return BudgetCategoryStatus.overBudget;
    if (percentageUsed >= 80) return BudgetCategoryStatus.nearLimit;
    return BudgetCategoryStatus.onTrack;
  }

  BudgetCategory({
    required this.id,
    required this.budgetId,
    required this.categoryId,
    required this.category,
    required this.categoryName,
    required this.allocatedAmount,
    required this.spentAmount,
    required this.remainingAmount,
    required this.percentageUsed,
    this.isIncome = false,
    this.defaultAllocatedAmount,
    this.groupId,
    this.groupName,
    this.sortOrder = 0,
    this.allowRollover = false,
    this.rolloverBalance = 0,
    this.maxRolloverMultiplier,
    this.rolloverOverflowSavingsGoalId,
    this.rolloverOverflowSavingsGoalName,
    this.isCountUpEnabled = false,
    this.countUpRolloverMode,
    this.savingsGoalId,
    this.savingsGoalName,
    this.isAllowance = false,
    this.linkedBudgetId,
    this.assignedUserId,
    this.assignedUserName,
    this.isVirtual = false,
    this.transactionCount = 0,
  });

  factory BudgetCategory.fromJson(Map<String, dynamic> json) {
    return BudgetCategory(
      id: json['id'] as int,
      budgetId: json['budgetId'] as int? ?? 0,
      categoryId: json['categoryId'] as int? ?? 99,
      category: TransactionCategory.fromValue(json['categoryId'] as int? ?? 99),
      categoryName: json['categoryName'] as String? ?? 'Unknown',
      allocatedAmount: (json['allocatedAmount'] as num?)?.toDouble() ?? 0,
      spentAmount: (json['spentAmount'] as num?)?.toDouble() ?? 0,
      remainingAmount: (json['remainingAmount'] as num?)?.toDouble() ?? 0,
      percentageUsed: (json['percentageUsed'] as num?)?.toDouble() ?? 0,
      isIncome: json['isIncome'] as bool? ?? false,
      defaultAllocatedAmount: (json['defaultAllocatedAmount'] as num?)?.toDouble(),
      groupId: json['groupId'] as int?,
      groupName: json['groupName'] as String?,
      sortOrder: json['sortOrder'] as int? ?? 0,
      allowRollover: json['allowRollover'] as bool? ?? false,
      rolloverBalance: (json['rolloverBalance'] as num?)?.toDouble() ?? 0,
      maxRolloverMultiplier: (json['maxRolloverMultiplier'] as num?)?.toDouble(),
      rolloverOverflowSavingsGoalId: json['rolloverOverflowSavingsGoalId'] as int?,
      rolloverOverflowSavingsGoalName: json['rolloverOverflowSavingsGoalName'] as String?,
      isCountUpEnabled: json['isCountUpEnabled'] as bool? ?? false,
      countUpRolloverMode: json['countUpRolloverMode'] != null
          ? CountUpRolloverMode.values[(json['countUpRolloverMode'] as int)
              .clamp(0, CountUpRolloverMode.values.length - 1)]
          : null,
      savingsGoalId: json['savingsGoalId'] as int?,
      savingsGoalName: json['savingsGoalName'] as String?,
      isAllowance: json['isAllowance'] as bool? ?? false,
      linkedBudgetId: json['linkedBudgetId'] as int?,
      assignedUserId: json['assignedUserId'] as int?,
      assignedUserName: json['assignedUserName'] as String?,
      isVirtual: json['isVirtual'] as bool? ?? false,
      transactionCount: json['transactionCount'] as int? ?? 0,
    );
  }
}

// ═══════════════════════════════════════════
//  BUDGET SAVINGS TRANSFER
// ═══════════════════════════════════════════

/// A transaction earmarked to a savings goal, surfaced on the budget response.
/// [isDeposit] is true when income was deposited into the goal, false when an
/// expense was withdrawn from it.
class BudgetSavingsTransfer {
  final int transactionId;
  final String description;
  final double amount;
  final bool isDeposit;
  final int savingsGoalId;
  final String savingsGoalName;
  final DateTime date;

  BudgetSavingsTransfer({
    required this.transactionId,
    required this.description,
    required this.amount,
    required this.isDeposit,
    required this.savingsGoalId,
    required this.savingsGoalName,
    required this.date,
  });

  factory BudgetSavingsTransfer.fromJson(Map<String, dynamic> json) {
    return BudgetSavingsTransfer(
      transactionId:
          (json['transactionId'] ?? json['TransactionId']) as int? ?? 0,
      description:
          (json['description'] ?? json['Description']) as String? ?? '',
      amount: ((json['amount'] ?? json['Amount']) as num?)?.toDouble() ?? 0,
      isDeposit: (json['isDeposit'] ?? json['IsDeposit']) as bool? ?? false,
      savingsGoalId:
          (json['savingsGoalId'] ?? json['SavingsGoalId']) as int? ?? 0,
      savingsGoalName:
          (json['savingsGoalName'] ?? json['SavingsGoalName']) as String? ?? '',
      date: DateTime.parse((json['date'] ?? json['Date']) as String),
    );
  }
}

// ═══════════════════════════════════════════
//  BUDGET DTO
// ═══════════════════════════════════════════

class Budget {
  final int id;
  final String name;
  final BudgetPeriod period;
  final DateTime startDate;
  final DateTime endDate;
  final DateTime lastReset;
  final bool autoReset;
  final int daysInCurrentPeriod;
  final int daysElapsed;
  final double totalIncome;
  final double totalExpenses;
  final bool isActive;
  final List<BudgetCategory> categories;
  final int userId;
  final int? familyId;
  final int? parentBudgetId;
  final double? allocatedAmount;
  final BudgetType type;
  final bool isPrimary;
  // RFC #793 — a personal budget funded by a family allocation. True for both a
  // linked Type=Personal child and a legacy Type=Allowance budget, so the UI treats
  // them identically without branching on [type]. False for standalone personal budgets.
  final bool isFamilyFunded;
  // The family allocation backing this budget when [isFamilyFunded]; null otherwise.
  final double? familyAllocationAmount;
  final ChildBudgetRolloverMode rolloverMode;
  final int? defaultSavingsGoalId;
  final String? defaultSavingsGoalName;
  final String? ownerName;
  final List<Budget> childBudgets;
  final List<BudgetShare> shares;
  final BudgetPeriodSummary? lastPeriodSummary;
  final List<BudgetSavingsTransfer> savingsTransfers;
  final double totalSavingsDeposits;
  final double totalSavingsWithdrawals;

  bool get isSharedWithFamily => type == BudgetType.family;

  // Totals reflect BUDGETED categories only. Virtual categories (unbudgeted
  // spending the backend surfaces for visibility) are excluded so "spent" and
  // "% used" stay relative to what was actually budgeted — otherwise spending in
  // categories budgeted on a different budget inflates this budget's totals.
  double get totalAllocated => categories
      .where((c) => !c.isIncome && !c.isVirtual)
      .fold(0.0, (sum, c) => sum + c.allocatedAmount);

  double get totalSpent => categories
      .where((c) => !c.isIncome && !c.isVirtual)
      .fold(0.0, (sum, c) => sum + c.spentAmount);

  double get totalRemaining => totalAllocated - totalSpent;

  /// Zero-based budgeting line: income allocations minus expense allocations.
  /// Positive = dollars without a job yet; zero = fully assigned ("every dollar
  /// has a job"); negative = over-assigned. Only meaningful when the budget has
  /// income set (totalIncome > 0).
  double get readyToAssign => totalIncome - totalAllocated;

  double get percentageUsed =>
      totalAllocated > 0 ? (totalSpent / totalAllocated) * 100 : 0;

  List<BudgetCategory> get expenseCategories =>
      categories.where((c) => !c.isIncome).toList();

  List<BudgetCategory> get incomeCategories =>
      categories.where((c) => c.isIncome).toList();

  int get daysRemaining {
    final now = DateTime.now();
    if (now.isAfter(endDate)) return 0;
    return endDate.difference(now).inDays;
  }

  BudgetStatus get status {
    if (percentageUsed >= 100) return BudgetStatus.overBudget;
    if (percentageUsed >= 80) return BudgetStatus.nearLimit;
    return BudgetStatus.onTrack;
  }

  Budget({
    required this.id,
    required this.name,
    required this.period,
    required this.startDate,
    required this.endDate,
    required this.lastReset,
    required this.autoReset,
    this.daysInCurrentPeriod = 30,
    this.daysElapsed = 0,
    required this.totalIncome,
    required this.totalExpenses,
    required this.isActive,
    required this.categories,
    required this.userId,
    this.familyId,
    this.parentBudgetId,
    this.allocatedAmount,
    required this.type,
    required this.isPrimary,
    this.isFamilyFunded = false,
    this.familyAllocationAmount,
    this.rolloverMode = ChildBudgetRolloverMode.rolloverToSelf,
    this.defaultSavingsGoalId,
    this.defaultSavingsGoalName,
    this.ownerName,
    this.childBudgets = const [],
    this.shares = const [],
    this.lastPeriodSummary,
    this.savingsTransfers = const [],
    this.totalSavingsDeposits = 0,
    this.totalSavingsWithdrawals = 0,
  });

  factory Budget.fromJson(Map<String, dynamic> json) {
    return Budget(
      id: json['id'] as int,
      name: json['name'] as String? ?? 'Budget',
      // Clamp every enum index: the backend enums are the source of truth and
      // can gain a value before a coordinated Flutter release. An out-of-range
      // index would throw RangeError and blank the budget + dashboard pages.
      period: BudgetPeriod.values[(json['period'] as int? ?? 2)
          .clamp(0, BudgetPeriod.values.length - 1)],
      startDate: DateTime.parse(json['currentPeriodStart'] as String? ?? json['startDate'] as String),
      endDate: DateTime.parse(json['currentPeriodEnd'] as String? ?? json['endDate'] as String),
      lastReset: DateTime.tryParse(json['lastReset'] as String? ?? '') ?? DateTime.now(),
      autoReset: json['autoReset'] as bool? ?? true,
      daysInCurrentPeriod: json['daysInCurrentPeriod'] as int? ?? 30,
      daysElapsed: json['daysElapsed'] as int? ?? 0,
      totalIncome: (json['totalIncome'] as num?)?.toDouble() ?? 0,
      totalExpenses: (json['totalExpenses'] as num?)?.toDouble() ?? 0,
      isActive: json['isActive'] as bool? ?? true,
      categories: (json['categories'] as List<dynamic>?)
          ?.map((e) => BudgetCategory.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      userId: json['userId'] as int? ?? 0,
      familyId: json['familyId'] as int?,
      parentBudgetId: json['parentBudgetId'] as int?,
      allocatedAmount: (json['allocatedAmount'] as num?)?.toDouble(),
      type: BudgetType.values[(json['type'] as int? ?? 0)
          .clamp(0, BudgetType.values.length - 1)],
      isPrimary: json['isPrimary'] as bool? ?? false,
      isFamilyFunded:
          (json['isFamilyFunded'] ?? json['IsFamilyFunded']) as bool? ?? false,
      familyAllocationAmount:
          ((json['familyAllocationAmount'] ?? json['FamilyAllocationAmount'])
                  as num?)
              ?.toDouble(),
      rolloverMode: ChildBudgetRolloverMode.values[(json['rolloverMode'] as int? ?? 0)
          .clamp(0, ChildBudgetRolloverMode.values.length - 1)],
      defaultSavingsGoalId: json['defaultSavingsGoalId'] as int?,
      defaultSavingsGoalName: json['defaultSavingsGoalName'] as String?,
      ownerName: json['ownerName'] as String?,
      childBudgets: (json['childBudgets'] as List<dynamic>?)
          ?.map((e) => Budget.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      shares: (json['shares'] as List<dynamic>?)
          ?.map((e) => BudgetShare.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      lastPeriodSummary: json['lastPeriodSummary'] != null
          ? BudgetPeriodSummary.fromJson(json['lastPeriodSummary'] as Map<String, dynamic>)
          : null,
      savingsTransfers:
          ((json['savingsTransfers'] ?? json['SavingsTransfers'])
                      as List<dynamic>?)
                  ?.map((e) =>
                      BudgetSavingsTransfer.fromJson(e as Map<String, dynamic>))
                  .toList() ??
              [],
      totalSavingsDeposits:
          ((json['totalSavingsDeposits'] ?? json['TotalSavingsDeposits'])
                  as num?)
              ?.toDouble() ??
              0,
      totalSavingsWithdrawals:
          ((json['totalSavingsWithdrawals'] ?? json['TotalSavingsWithdrawals'])
                  as num?)
              ?.toDouble() ??
              0,
    );
  }
}

// ═══════════════════════════════════════════
//  BUDGET SHARE
// ═══════════════════════════════════════════

class BudgetShare {
  final int id;
  final int budgetId;
  final int sharedWithUserId;
  final String sharedWithUserName;
  final bool canEdit;
  final bool canViewTransactions;

  BudgetShare({
    required this.id,
    required this.budgetId,
    required this.sharedWithUserId,
    required this.sharedWithUserName,
    this.canEdit = false,
    this.canViewTransactions = true,
  });

  factory BudgetShare.fromJson(Map<String, dynamic> json) {
    return BudgetShare(
      id: json['id'] as int,
      budgetId: json['budgetId'] as int,
      sharedWithUserId: json['sharedWithUserId'] as int,
      sharedWithUserName: json['sharedWithUserName'] as String? ?? '',
      canEdit: json['canEdit'] as bool? ?? false,
      canViewTransactions: json['canViewTransactions'] as bool? ?? true,
    );
  }
}

// ═══════════════════════════════════════════
//  PERIOD SUMMARY
// ═══════════════════════════════════════════

class BudgetPeriodSummary {
  final DateTime periodStart;
  final DateTime periodEnd;
  final double totalSavings;
  final double totalRollover;
  final List<BudgetCategorySummary> categorySummaries;
  final String encouragementMessage;

  BudgetPeriodSummary({
    required this.periodStart,
    required this.periodEnd,
    required this.totalSavings,
    required this.totalRollover,
    this.categorySummaries = const [],
    this.encouragementMessage = '',
  });

  factory BudgetPeriodSummary.fromJson(Map<String, dynamic> json) {
    return BudgetPeriodSummary(
      periodStart: DateTime.parse(json['periodStart'] as String),
      periodEnd: DateTime.parse(json['periodEnd'] as String),
      totalSavings: (json['totalSavings'] as num?)?.toDouble() ?? 0,
      totalRollover: (json['totalRollover'] as num?)?.toDouble() ?? 0,
      categorySummaries: (json['categorySummaries'] as List<dynamic>?)
          ?.map((e) => BudgetCategorySummary.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      encouragementMessage: json['encouragementMessage'] as String? ?? '',
    );
  }
}

class BudgetCategorySummary {
  final String categoryName;
  final double allocated;
  final double spent;
  final double saved;
  final double rolledOver;
  final bool wasUnderBudget;
  final bool isIncome;

  BudgetCategorySummary({
    required this.categoryName,
    required this.allocated,
    required this.spent,
    required this.saved,
    required this.rolledOver,
    required this.wasUnderBudget,
    required this.isIncome,
  });

  factory BudgetCategorySummary.fromJson(Map<String, dynamic> json) {
    return BudgetCategorySummary(
      categoryName: json['categoryName'] as String? ?? '',
      allocated: (json['allocated'] as num?)?.toDouble() ?? 0,
      spent: (json['spent'] as num?)?.toDouble() ?? 0,
      saved: (json['saved'] as num?)?.toDouble() ?? 0,
      rolledOver: (json['rolledOver'] as num?)?.toDouble() ?? 0,
      wasUnderBudget: json['wasUnderBudget'] as bool? ?? false,
      isIncome: json['isIncome'] as bool? ?? false,
    );
  }
}

// ═══════════════════════════════════════════
//  CATEGORY GROUPS
// ═══════════════════════════════════════════

class CategoryGroup {
  final int id;
  final String name;
  final String? icon;
  final int sortOrder;
  final bool isSystem;
  final List<BudgetCategory> categories;

  CategoryGroup({
    required this.id,
    required this.name,
    this.icon,
    this.sortOrder = 0,
    this.isSystem = false,
    this.categories = const [],
  });

  factory CategoryGroup.fromJson(Map<String, dynamic> json) {
    return CategoryGroup(
      id: json['id'] as int,
      name: json['name'] as String? ?? '',
      icon: json['icon'] as String?,
      sortOrder: json['sortOrder'] as int? ?? 0,
      isSystem: json['isSystem'] as bool? ?? false,
      categories: (json['categories'] as List<dynamic>?)
          ?.map((e) => BudgetCategory.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
    );
  }
}

class GroupedBudgetOverview {
  final int budgetId;
  final String budgetName;
  final double totalAllocated;
  final double totalSpent;
  final double totalRemaining;
  final List<CategoryGroup> groups;

  GroupedBudgetOverview({
    required this.budgetId,
    required this.budgetName,
    required this.totalAllocated,
    required this.totalSpent,
    required this.totalRemaining,
    this.groups = const [],
  });

  factory GroupedBudgetOverview.fromJson(Map<String, dynamic> json) {
    return GroupedBudgetOverview(
      budgetId: json['budgetId'] as int,
      budgetName: json['budgetName'] as String? ?? '',
      totalAllocated: (json['totalAllocated'] as num?)?.toDouble() ?? 0,
      totalSpent: (json['totalSpent'] as num?)?.toDouble() ?? 0,
      totalRemaining: (json['totalRemaining'] as num?)?.toDouble() ?? 0,
      groups: (json['groups'] as List<dynamic>?)
          ?.map((e) => CategoryGroup.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
    );
  }
}

// ═══════════════════════════════════════════
//  COUNT-UP BUDGETING
// ═══════════════════════════════════════════

class CountUpBudgetStatus {
  /// The budget-category ROW id ([BudgetCategory.id]), not the shared category
  /// id that [BudgetCategory.categoryId] holds. The server fills it from
  /// BudgetCategory.Id (CountUpBudgetService).
  final int categoryId;
  final String categoryName;
  final double allocatedAmount;
  final double actualSpent;
  final double dailyAllowance;
  final int daysElapsed;
  final int totalDays;
  final double earnedToDate;
  final double availableBalance;
  final double spendingRate;
  final bool isOverEarned;
  final double projectedSavings;
  final int consecutiveDaysUnderBudget;
  final int zeroSpendingDays;
  final List<CountUpAchievement> achievements;

  CountUpBudgetStatus({
    required this.categoryId,
    required this.categoryName,
    required this.allocatedAmount,
    required this.actualSpent,
    required this.dailyAllowance,
    required this.daysElapsed,
    required this.totalDays,
    required this.earnedToDate,
    required this.availableBalance,
    required this.spendingRate,
    required this.isOverEarned,
    required this.projectedSavings,
    this.consecutiveDaysUnderBudget = 0,
    this.zeroSpendingDays = 0,
    this.achievements = const [],
  });

  factory CountUpBudgetStatus.fromJson(Map<String, dynamic> json) {
    return CountUpBudgetStatus(
      categoryId: json['categoryId'] as int,
      categoryName: json['categoryName'] as String? ?? '',
      allocatedAmount: (json['allocatedAmount'] as num?)?.toDouble() ?? 0,
      actualSpent: (json['actualSpent'] as num?)?.toDouble() ?? 0,
      dailyAllowance: (json['dailyAllowance'] as num?)?.toDouble() ?? 0,
      daysElapsed: json['daysElapsed'] as int? ?? 0,
      totalDays: json['totalDays'] as int? ?? 30,
      earnedToDate: (json['earnedToDate'] as num?)?.toDouble() ?? 0,
      availableBalance: (json['availableBalance'] as num?)?.toDouble() ?? 0,
      spendingRate: (json['spendingRate'] as num?)?.toDouble() ?? 0,
      isOverEarned: json['isOverEarned'] as bool? ?? false,
      projectedSavings: (json['projectedSavings'] as num?)?.toDouble() ?? 0,
      consecutiveDaysUnderBudget: json['consecutiveDaysUnderBudget'] as int? ?? 0,
      zeroSpendingDays: json['zeroSpendingDays'] as int? ?? 0,
      achievements: (json['achievements'] as List<dynamic>?)
          ?.map((e) => CountUpAchievement.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
    );
  }

  /// The budget row this status describes, or null if it isn't in [categories].
  /// Matches on the row id: comparing against [BudgetCategory.categoryId] (the
  /// shared category id) picked the wrong row, so edit opened another category.
  BudgetCategory? findIn(Iterable<BudgetCategory> categories) {
    for (final c in categories) {
      if (c.id == categoryId) return c;
    }
    return null;
  }
}

class CountUpAchievement {
  final String title;
  final String description;
  final String icon;
  final DateTime earnedDate;

  CountUpAchievement({
    required this.title,
    required this.description,
    required this.icon,
    required this.earnedDate,
  });

  factory CountUpAchievement.fromJson(Map<String, dynamic> json) {
    return CountUpAchievement(
      title: json['title'] as String? ?? '',
      description: json['description'] as String? ?? '',
      icon: json['icon'] as String? ?? '',
      earnedDate: DateTime.tryParse(json['earnedDate'] as String? ?? '') ?? DateTime.now(),
    );
  }
}

// ═══════════════════════════════════════════
//  TRANSFER DTOs
// ═══════════════════════════════════════════

class TransferFundsResponse {
  final int transferId;
  final BudgetCategory sourceCategory;
  final BudgetCategory targetCategory;
  final double amount;
  final DateTime transferredAt;

  TransferFundsResponse({
    required this.transferId,
    required this.sourceCategory,
    required this.targetCategory,
    required this.amount,
    required this.transferredAt,
  });

  factory TransferFundsResponse.fromJson(Map<String, dynamic> json) {
    return TransferFundsResponse(
      transferId: json['transferId'] as int,
      sourceCategory: BudgetCategory.fromJson(json['sourceCategory'] as Map<String, dynamic>),
      targetCategory: BudgetCategory.fromJson(json['targetCategory'] as Map<String, dynamic>),
      amount: (json['amount'] as num).toDouble(),
      transferredAt: DateTime.parse(json['transferredAt'] as String),
    );
  }
}

class TransferToSavingsResponse {
  final int transferId;
  final BudgetCategory sourceCategory;
  final int savingsGoalId;
  final String savingsGoalName;
  final double amount;
  final double newGoalCurrentAmount;
  final DateTime transferredAt;

  TransferToSavingsResponse({
    required this.transferId,
    required this.sourceCategory,
    required this.savingsGoalId,
    required this.savingsGoalName,
    required this.amount,
    required this.newGoalCurrentAmount,
    required this.transferredAt,
  });

  factory TransferToSavingsResponse.fromJson(Map<String, dynamic> json) {
    return TransferToSavingsResponse(
      transferId: json['transferId'] as int,
      sourceCategory: BudgetCategory.fromJson(json['sourceCategory'] as Map<String, dynamic>),
      savingsGoalId: json['savingsGoalId'] as int,
      savingsGoalName: json['savingsGoalName'] as String? ?? '',
      amount: (json['amount'] as num).toDouble(),
      newGoalCurrentAmount: (json['newGoalCurrentAmount'] as num).toDouble(),
      transferredAt: DateTime.parse(json['transferredAt'] as String),
    );
  }
}

class BudgetTransfer {
  final int id;
  final int? sourceCategoryId;
  final String? sourceCategoryName;
  final int? sourceSavingsGoalId;
  final String? sourceSavingsGoalName;
  final int? targetCategoryId;
  final String? targetCategoryName;
  final int? targetSavingsGoalId;
  final String? targetSavingsGoalName;
  final double amount;
  final String? note;
  final String? reason;
  final String createdByUserName;
  final DateTime createdAt;

  BudgetTransfer({
    required this.id,
    this.sourceCategoryId,
    this.sourceCategoryName,
    this.sourceSavingsGoalId,
    this.sourceSavingsGoalName,
    this.targetCategoryId,
    this.targetCategoryName,
    this.targetSavingsGoalId,
    this.targetSavingsGoalName,
    required this.amount,
    this.note,
    this.reason,
    required this.createdByUserName,
    required this.createdAt,
  });

  factory BudgetTransfer.fromJson(Map<String, dynamic> json) {
    return BudgetTransfer(
      id: json['id'] as int,
      sourceCategoryId: json['sourceCategoryId'] as int?,
      sourceCategoryName: json['sourceCategoryName'] as String?,
      sourceSavingsGoalId: json['sourceSavingsGoalId'] as int?,
      sourceSavingsGoalName: json['sourceSavingsGoalName'] as String?,
      targetCategoryId: json['targetCategoryId'] as int?,
      targetCategoryName: json['targetCategoryName'] as String?,
      targetSavingsGoalId: json['targetSavingsGoalId'] as int?,
      targetSavingsGoalName: json['targetSavingsGoalName'] as String?,
      amount: (json['amount'] as num).toDouble(),
      note: json['note'] as String?,
      reason: json['reason'] as String?,
      createdByUserName: json['createdByUserName'] as String? ?? '',
      createdAt: DateTime.parse(json['createdAt'] as String),
    );
  }
}

class ResetRolloversResponse {
  final int categoriesReset;
  final double totalRolloverCleared;
  final List<CategoryRolloverReset> resetCategories;

  ResetRolloversResponse({
    required this.categoriesReset,
    required this.totalRolloverCleared,
    this.resetCategories = const [],
  });

  factory ResetRolloversResponse.fromJson(Map<String, dynamic> json) {
    return ResetRolloversResponse(
      categoriesReset: json['categoriesReset'] as int? ?? 0,
      totalRolloverCleared: (json['totalRolloverCleared'] as num?)?.toDouble() ?? 0,
      resetCategories: (json['resetCategories'] as List<dynamic>?)
          ?.map((e) => CategoryRolloverReset.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
    );
  }
}

class CategoryRolloverReset {
  final int categoryId;
  final String categoryName;
  final double previousRollover;

  CategoryRolloverReset({
    required this.categoryId,
    required this.categoryName,
    required this.previousRollover,
  });

  factory CategoryRolloverReset.fromJson(Map<String, dynamic> json) {
    return CategoryRolloverReset(
      categoryId: json['categoryId'] as int,
      categoryName: json['categoryName'] as String? ?? '',
      previousRollover: (json['previousRollover'] as num?)?.toDouble() ?? 0,
    );
  }
}

// ═══════════════════════════════════════════
//  FAMILY BUDGET DTOs
// ═══════════════════════════════════════════

class FamilyBudgetOverview {
  final int familyId;
  final String familyName;
  final List<FamilyMemberBudgetSummary> members;
  final double totalFamilyAllocated;
  final double totalFamilySpent;

  FamilyBudgetOverview({
    required this.familyId,
    required this.familyName,
    this.members = const [],
    required this.totalFamilyAllocated,
    required this.totalFamilySpent,
  });

  factory FamilyBudgetOverview.fromJson(Map<String, dynamic> json) {
    return FamilyBudgetOverview(
      familyId: json['familyId'] as int,
      familyName: json['familyName'] as String? ?? '',
      members: (json['members'] as List<dynamic>?)
          ?.map((e) => FamilyMemberBudgetSummary.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      totalFamilyAllocated: (json['totalFamilyAllocated'] as num?)?.toDouble() ?? 0,
      totalFamilySpent: (json['totalFamilySpent'] as num?)?.toDouble() ?? 0,
    );
  }
}

class FamilyMemberBudgetSummary {
  final int userId;
  final String userName;
  final List<ChildBudgetSummary> budgets;
  final double totalAllocated;
  final double totalSpent;
  final double totalRemaining;

  FamilyMemberBudgetSummary({
    required this.userId,
    required this.userName,
    this.budgets = const [],
    required this.totalAllocated,
    required this.totalSpent,
    required this.totalRemaining,
  });

  factory FamilyMemberBudgetSummary.fromJson(Map<String, dynamic> json) {
    return FamilyMemberBudgetSummary(
      userId: json['userId'] as int,
      userName: json['userName'] as String? ?? '',
      budgets: (json['budgets'] as List<dynamic>?)
          ?.map((e) => ChildBudgetSummary.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      totalAllocated: (json['totalAllocated'] as num?)?.toDouble() ?? 0,
      totalSpent: (json['totalSpent'] as num?)?.toDouble() ?? 0,
      totalRemaining: (json['totalRemaining'] as num?)?.toDouble() ?? 0,
    );
  }
}

class ChildBudgetSummary {
  final int id;
  final String name;
  final double allocatedAmount;
  final double spentAmount;
  final double remainingAmount;
  final double percentUsed;
  final ChildBudgetRolloverMode rolloverMode;
  final bool isSharedWithFamily;

  ChildBudgetSummary({
    required this.id,
    required this.name,
    required this.allocatedAmount,
    required this.spentAmount,
    required this.remainingAmount,
    required this.percentUsed,
    this.rolloverMode = ChildBudgetRolloverMode.rolloverToSelf,
    this.isSharedWithFamily = false,
  });

  factory ChildBudgetSummary.fromJson(Map<String, dynamic> json) {
    return ChildBudgetSummary(
      id: json['id'] as int,
      name: json['name'] as String? ?? '',
      allocatedAmount: (json['allocatedAmount'] as num?)?.toDouble() ?? 0,
      spentAmount: (json['spentAmount'] as num?)?.toDouble() ?? 0,
      remainingAmount: (json['remainingAmount'] as num?)?.toDouble() ?? 0,
      percentUsed: (json['percentUsed'] as num?)?.toDouble() ?? 0,
      rolloverMode: ChildBudgetRolloverMode.values[(json['rolloverMode'] as int? ?? 0)
          .clamp(0, ChildBudgetRolloverMode.values.length - 1)],
      isSharedWithFamily: json['isSharedWithFamily'] as bool? ?? false,
    );
  }
}

// ═══════════════════════════════════════════
//  PERIOD HISTORY (multi-month navigation)
// ═══════════════════════════════════════════

class BudgetPeriodListItem {
  final int id;
  final DateTime periodStart;
  final DateTime periodEnd;
  final double totalAllocated;
  final double totalSpent;

  BudgetPeriodListItem({
    required this.id,
    required this.periodStart,
    required this.periodEnd,
    required this.totalAllocated,
    required this.totalSpent,
  });

  factory BudgetPeriodListItem.fromJson(Map<String, dynamic> json) {
    return BudgetPeriodListItem(
      id: json['id'] as int,
      periodStart: DateTime.parse(
          (json['periodStart'] ?? json['PeriodStart']) as String),
      periodEnd: DateTime.parse(
          (json['periodEnd'] ?? json['PeriodEnd']) as String),
      totalAllocated:
          ((json['totalAllocated'] ?? json['TotalAllocated']) as num?)
                  ?.toDouble() ??
              0,
      totalSpent:
          ((json['totalSpent'] ?? json['TotalSpent']) as num?)?.toDouble() ??
              0,
    );
  }
}

class BudgetPeriodCategoryHistory {
  final int id;
  final int categoryId;
  final String categoryName;
  final double allocatedAmount;
  final double spentAmount;
  final double rolloverIn;
  final double rolloverOut;
  final bool isIncome;
  final bool allowRollover;

  double get remainingAmount => allocatedAmount - spentAmount;
  double get percentageUsed =>
      allocatedAmount > 0 ? (spentAmount / allocatedAmount) * 100 : 0;

  BudgetPeriodCategoryHistory({
    required this.id,
    required this.categoryId,
    required this.categoryName,
    required this.allocatedAmount,
    required this.spentAmount,
    required this.rolloverIn,
    required this.rolloverOut,
    required this.isIncome,
    required this.allowRollover,
  });

  BudgetPeriodCategoryHistory copyWith({double? allocatedAmount}) {
    return BudgetPeriodCategoryHistory(
      id: id,
      categoryId: categoryId,
      categoryName: categoryName,
      allocatedAmount: allocatedAmount ?? this.allocatedAmount,
      spentAmount: spentAmount,
      rolloverIn: rolloverIn,
      rolloverOut: rolloverOut,
      isIncome: isIncome,
      allowRollover: allowRollover,
    );
  }

  factory BudgetPeriodCategoryHistory.fromJson(Map<String, dynamic> json) {
    return BudgetPeriodCategoryHistory(
      id: json['id'] as int,
      categoryId:
          (json['categoryId'] ?? json['CategoryId']) as int? ?? 0,
      categoryName:
          (json['categoryName'] ?? json['CategoryName']) as String? ?? '',
      allocatedAmount:
          ((json['allocatedAmount'] ?? json['AllocatedAmount']) as num?)
                  ?.toDouble() ??
              0,
      spentAmount:
          ((json['spentAmount'] ?? json['SpentAmount']) as num?)?.toDouble() ??
              0,
      rolloverIn:
          ((json['rolloverIn'] ?? json['RolloverIn']) as num?)?.toDouble() ?? 0,
      rolloverOut:
          ((json['rolloverOut'] ?? json['RolloverOut']) as num?)?.toDouble() ??
              0,
      isIncome:
          (json['isIncome'] ?? json['IsIncome']) as bool? ?? false,
      allowRollover:
          (json['allowRollover'] ?? json['AllowRollover']) as bool? ?? false,
    );
  }
}

class BudgetPeriodDetail {
  final int id;
  final DateTime periodStart;
  final DateTime periodEnd;
  final double totalAllocated;
  final double totalSpent;
  final double totalIncome;
  final List<BudgetPeriodCategoryHistory> categories;

  BudgetPeriodDetail({
    required this.id,
    required this.periodStart,
    required this.periodEnd,
    required this.totalAllocated,
    required this.totalSpent,
    required this.totalIncome,
    required this.categories,
  });

  factory BudgetPeriodDetail.fromJson(Map<String, dynamic> json) {
    return BudgetPeriodDetail(
      id: json['id'] as int,
      periodStart: DateTime.parse(
          (json['periodStart'] ?? json['PeriodStart']) as String),
      periodEnd: DateTime.parse(
          (json['periodEnd'] ?? json['PeriodEnd']) as String),
      totalAllocated:
          ((json['totalAllocated'] ?? json['TotalAllocated']) as num?)
                  ?.toDouble() ??
              0,
      totalSpent:
          ((json['totalSpent'] ?? json['TotalSpent']) as num?)?.toDouble() ?? 0,
      totalIncome:
          ((json['totalIncome'] ?? json['TotalIncome']) as num?)?.toDouble() ??
              0,
      categories:
          ((json['categories'] ?? json['Categories']) as List<dynamic>?)
                  ?.map((e) => BudgetPeriodCategoryHistory.fromJson(
                      e as Map<String, dynamic>))
                  .toList() ??
              [],
    );
  }
}

// ═══════════════════════════════════════════
//  BUDGET OVERVIEW (Dashboard)
// ═══════════════════════════════════════════

class BudgetOverview {
  final Budget? activeBudget;
  final double totalAllocated;
  final double totalSpent;
  final double totalRemaining;
  final double percentageUsed;
  final List<BudgetCategory> topCategories;

  BudgetOverview({
    this.activeBudget,
    required this.totalAllocated,
    required this.totalSpent,
    required this.totalRemaining,
    required this.percentageUsed,
    this.topCategories = const [],
  });

  factory BudgetOverview.empty() {
    return BudgetOverview(
      activeBudget: null,
      totalAllocated: 0,
      totalSpent: 0,
      totalRemaining: 0,
      percentageUsed: 0,
      topCategories: [],
    );
  }
}
