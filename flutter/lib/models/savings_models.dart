// Savings models for BuoyAI Flutter app

/// Savings goal type enum.
///
/// Mirrors the backend `SavingsGoalType` (BuoyAI.Core/Enums/FinancialEnums.cs),
/// which defines only Personal=0 and Family=1. These are sent to the API as the
/// enum index (savings_service.createGoal/updateGoal), so this enum must stay in
/// lock-step with the backend — adding a member the backend can't represent would
/// make the goal match neither budget scope and silently receive no auto-allocations.
enum SavingsGoalType {
  personal,   // 0 - Personal savings (funded by personal budget leftover)
  family,     // 1 - Family savings (funded by family budget leftover)
}

extension SavingsGoalTypeExtension on SavingsGoalType {
  String get displayName {
    switch (this) {
      case SavingsGoalType.personal: return 'Personal Savings';
      case SavingsGoalType.family: return 'Family Savings';
    }
  }
}

/// Savings goal
class SavingsGoal {
  final int id;
  final SavingsGoalType type;
  final int userId;
  final int? familyId;
  final String ownerName;
  final bool isOwner;
  final bool canContribute;
  final String name;
  final String? description;
  final double targetAmount;
  final double currentAmount;
  final double allocationPercentage;
  final DateTime? targetDate;
  final String iconName;
  final String colorTheme;
  final bool isActive;
  final bool isCompleted;
  final bool countWithdrawalsTowardBudget;
  final double progressPercentage;
  final double remainingAmount;
  final int daysRemaining;
  final double dailyRequiredSavings;
  final bool isOnTrack;
  final String progressStatus; // 'on_track', 'behind', 'completed'
  final DateTime createdAt;

  // ── Optional account link (reconciliation only) ──
  /// Real account this goal's money is meant to live in. Null = unlinked, a
  /// first-class choice (cash, a checking account, no account opened yet).
  final int? accountId;
  final String? accountName;
  final double? accountBalance;

  /// Total promised to goals on that account by EVERY goal pointing at it, not
  /// just this one — several goals can share an account.
  final double? accountAllocatedToGoals;

  /// Balance minus everything allocated. Positive = money no goal has claimed;
  /// negative = the goals promise more than the account holds.
  final double? accountUnallocated;

  /// Earmarked purchases whose money has not physically left the linked
  /// account yet. Explains that much of [accountUnallocated]: the bank still
  /// holds it, but it is spoken for.
  final double? pendingTransferAmount;

  bool get isLinkedToAccount => accountId != null;

  bool get hasMoneyInFlight =>
      pendingTransferAmount != null && pendingTransferAmount! > 0;

  /// True only when the goals on this account promise MORE than it holds. A
  /// positive remainder is ordinary unallocated money, not a problem to flag.
  bool get isOverAllocated =>
      accountUnallocated != null && accountUnallocated! < 0;

  /// Alias for progressPercentage (for UI)
  double get progress => progressPercentage;

  SavingsGoal({
    required this.id,
    required this.type,
    required this.userId,
    this.familyId,
    required this.ownerName,
    required this.isOwner,
    this.canContribute = false,
    required this.name,
    this.description,
    required this.targetAmount,
    required this.currentAmount,
    required this.allocationPercentage,
    this.targetDate,
    required this.iconName,
    required this.colorTheme,
    required this.isActive,
    required this.isCompleted,
    this.countWithdrawalsTowardBudget = false,
    required this.progressPercentage,
    required this.remainingAmount,
    required this.daysRemaining,
    required this.dailyRequiredSavings,
    required this.isOnTrack,
    required this.progressStatus,
    required this.createdAt,
    this.accountId,
    this.accountName,
    this.accountBalance,
    this.accountAllocatedToGoals,
    this.accountUnallocated,
    this.pendingTransferAmount,
  });

  factory SavingsGoal.fromJson(Map<String, dynamic> json) {
    return SavingsGoal(
      id: json['id'] as int,
      type: SavingsGoalType.values[
        (json['type'] as int? ?? 0).clamp(0, SavingsGoalType.values.length - 1)
      ],
      userId: json['userId'] as int? ?? 0,
      familyId: json['familyId'] as int?,
      ownerName: json['ownerName'] as String? ?? '',
      isOwner: json['isOwner'] as bool? ?? false,
      canContribute: json['canContribute'] as bool? ?? false,
      name: json['name'] as String? ?? '',
      description: json['description'] as String?,
      targetAmount: (json['targetAmount'] as num?)?.toDouble() ?? 0,
      currentAmount: (json['currentAmount'] as num?)?.toDouble() ?? 0,
      allocationPercentage: (json['allocationPercentage'] as num?)?.toDouble() ?? 0,
      targetDate: json['targetDate'] != null 
          ? DateTime.parse(json['targetDate'] as String)
          : null,
      iconName: json['iconName'] as String? ?? 'savings',
      colorTheme: json['colorTheme'] as String? ?? 'blue',
      isActive: json['isActive'] as bool? ?? true,
      isCompleted: json['isCompleted'] as bool? ?? false,
      countWithdrawalsTowardBudget:
          json['countWithdrawalsTowardBudget'] as bool? ??
          json['CountWithdrawalsTowardBudget'] as bool? ??
          false,
      progressPercentage: (json['progressPercentage'] as num?)?.toDouble() ?? 0,
      remainingAmount: (json['remainingAmount'] as num?)?.toDouble() ?? 0,
      daysRemaining: json['daysRemaining'] as int? ?? 0,
      dailyRequiredSavings: (json['dailyRequiredSavings'] as num?)?.toDouble() ?? 0,
      isOnTrack: json['isOnTrack'] as bool? ?? false,
      progressStatus: json['progressStatus'] as String? ?? 'on_track',
      accountId: (json['accountId'] ?? json['AccountId']) as int?,
      accountName: (json['accountName'] ?? json['AccountName']) as String?,
      accountBalance:
          ((json['accountBalance'] ?? json['AccountBalance']) as num?)?.toDouble(),
      accountAllocatedToGoals:
          ((json['accountAllocatedToGoals'] ?? json['AccountAllocatedToGoals'])
                  as num?)
              ?.toDouble(),
      accountUnallocated:
          ((json['accountUnallocated'] ?? json['AccountUnallocated']) as num?)
              ?.toDouble(),
      pendingTransferAmount:
          ((json['pendingTransferAmount'] ?? json['PendingTransferAmount'])
                  as num?)
              ?.toDouble(),
      createdAt: DateTime.parse(json['createdAt'] as String? ?? DateTime.now().toIso8601String()),
    );
  }
}

/// Savings achievement
class SavingsAchievement {
  final int id;
  final String achievementType;
  final String title;
  final String description;
  final String badgeIcon;
  final String badgeColor;
  final DateTime earnedDate;
  final bool isNotified;

  SavingsAchievement({
    required this.id,
    required this.achievementType,
    required this.title,
    required this.description,
    required this.badgeIcon,
    required this.badgeColor,
    required this.earnedDate,
    required this.isNotified,
  });

  factory SavingsAchievement.fromJson(Map<String, dynamic> json) {
    return SavingsAchievement(
      id: json['id'] as int,
      achievementType: json['achievementType'] as String? ?? '',
      title: json['title'] as String? ?? '',
      description: json['description'] as String? ?? '',
      badgeIcon: json['badgeIcon'] as String? ?? 'star',
      badgeColor: json['badgeColor'] as String? ?? 'gold',
      earnedDate: DateTime.parse(json['earnedDate'] as String? ?? DateTime.now().toIso8601String()),
      isNotified: json['isNotified'] as bool? ?? false,
    );
  }
}

/// Savings overview
class SavingsOverview {
  final double totalSaved;
  final double monthlyGrowth;
  final double currentPeriodLeftover;
  final double familyLeftover;
  final double personalLeftover;
  final bool hasFamilyBudget;
  final double projectedMonthlyGrowth;
  final double totalAllocatedPerPeriod;
  final double unallocatedAmount;
  final int activeGoalsCount;
  final int completedGoalsCount;
  final List<SavingsGoal> goals;
  final List<SavingsAchievement> recentAchievements;

  SavingsOverview({
    required this.totalSaved,
    required this.monthlyGrowth,
    required this.currentPeriodLeftover,
    required this.familyLeftover,
    required this.personalLeftover,
    required this.hasFamilyBudget,
    required this.projectedMonthlyGrowth,
    required this.totalAllocatedPerPeriod,
    required this.unallocatedAmount,
    required this.activeGoalsCount,
    required this.completedGoalsCount,
    required this.goals,
    required this.recentAchievements,
  });

  factory SavingsOverview.fromJson(Map<String, dynamic> json) {
    return SavingsOverview(
      totalSaved: (json['totalSaved'] as num?)?.toDouble() ?? 0,
      monthlyGrowth: (json['monthlyGrowth'] as num?)?.toDouble() ?? 0,
      currentPeriodLeftover: (json['currentPeriodLeftover'] as num?)?.toDouble() ?? 0,
      familyLeftover: (json['familyLeftover'] as num?)?.toDouble() ?? 0,
      personalLeftover: (json['personalLeftover'] as num?)?.toDouble() ?? 0,
      hasFamilyBudget: json['hasFamilyBudget'] as bool? ?? false,
      projectedMonthlyGrowth: (json['projectedMonthlyGrowth'] as num?)?.toDouble() ?? 0,
      totalAllocatedPerPeriod: (json['totalAllocatedPerPeriod'] as num?)?.toDouble() ?? 0,
      unallocatedAmount: (json['unallocatedAmount'] as num?)?.toDouble() ?? 0,
      activeGoalsCount: json['activeGoalsCount'] as int? ?? 0,
      completedGoalsCount: json['completedGoalsCount'] as int? ?? 0,
      goals: (json['goals'] as List<dynamic>?)
          ?.map((e) => SavingsGoal.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
      recentAchievements: (json['recentAchievements'] as List<dynamic>?)
          ?.map((e) => SavingsAchievement.fromJson(e as Map<String, dynamic>))
          .toList() ?? [],
    );
  }
  
  factory SavingsOverview.empty() {
    return SavingsOverview(
      totalSaved: 0,
      monthlyGrowth: 0,
      currentPeriodLeftover: 0,
      familyLeftover: 0,
      personalLeftover: 0,
      hasFamilyBudget: false,
      projectedMonthlyGrowth: 0,
      totalAllocatedPerPeriod: 0,
      unallocatedAmount: 0,
      activeGoalsCount: 0,
      completedGoalsCount: 0,
      goals: [],
      recentAchievements: [],
    );
  }
}

/// Outcome of reconciling the goals held in one account.
class ReconcileResult {
  final double accountBalance;
  final double allocatedToGoals;

  /// Positive = money no goal claims, which is a fine place to stop.
  /// Negative = goals still promise more than the account holds.
  final double unallocated;

  final List<SavingsGoal> goals;

  const ReconcileResult({
    required this.accountBalance,
    required this.allocatedToGoals,
    required this.unallocated,
    required this.goals,
  });

  bool get isOverAllocated => unallocated < 0;

  factory ReconcileResult.fromJson(Map<String, dynamic> json) {
    final goalsJson = (json['goals'] ?? json['Goals'] ?? const []) as List;
    return ReconcileResult(
      accountBalance:
          ((json['accountBalance'] ?? json['AccountBalance']) as num?)?.toDouble() ?? 0,
      allocatedToGoals:
          ((json['allocatedToGoals'] ?? json['AllocatedToGoals']) as num?)?.toDouble() ?? 0,
      unallocated:
          ((json['unallocated'] ?? json['Unallocated']) as num?)?.toDouble() ?? 0,
      goals: goalsJson
          .map((g) => SavingsGoal.fromJson(g as Map<String, dynamic>))
          .toList(),
    );
  }
}
