import 'api_client.dart';

// ── Models ──────────────────────────────────────────────

class GamificationDashboard {
  final int totalPoints;
  final String level;
  final int pointsToNextLevel;

  /// Fraction (0.0–1.0) through the current level, computed by the backend.
  final double levelProgress;
  final int achievementCount;
  final List<UserStreak> activeStreaks;
  final List<UserAchievement> recentAchievements;
  final List<UserChallenge> activeChallenges;
  final List<String> newAchievementIds;

  GamificationDashboard({
    required this.totalPoints,
    required this.level,
    required this.pointsToNextLevel,
    required this.levelProgress,
    required this.achievementCount,
    required this.activeStreaks,
    required this.recentAchievements,
    required this.activeChallenges,
    required this.newAchievementIds,
  });

  bool get isMaxLevel => level == 'Legend';

  factory GamificationDashboard.fromJson(Map<String, dynamic> json) {
    return GamificationDashboard(
      totalPoints:
          ((json['totalPoints'] ?? json['TotalPoints']) as num?)?.toInt() ?? 0,
      level: (json['level'] ?? json['Level'] ?? 'Novice').toString(),
      pointsToNextLevel:
          ((json['pointsToNextLevel'] ?? json['PointsToNextLevel']) as num?)
                  ?.toInt() ??
              0,
      levelProgress:
          ((json['levelProgress'] ?? json['LevelProgress'] ?? 0.0) as num)
              .toDouble(),
      achievementCount:
          ((json['achievementCount'] ?? json['AchievementCount']) as num?)
                  ?.toInt() ??
              0,
      activeStreaks: _parseList(
          json['activeStreaks'] ?? json['ActiveStreaks'], UserStreak.fromJson),
      recentAchievements: _parseList(
          json['recentAchievements'] ?? json['RecentAchievements'],
          UserAchievement.fromJson),
      activeChallenges: _parseList(
          json['activeChallenges'] ?? json['ActiveChallenges'],
          UserChallenge.fromJson),
      newAchievementIds: List<String>.from(
          (json['newAchievementIds'] ?? json['NewAchievementIds'] ?? [])
              as List<dynamic>),
    );
  }
}

class UserStreak {
  final int id;
  final String streakType;
  final String displayName;
  final String icon;
  final int currentCount;
  final int bestCount;
  final DateTime lastIncrementDate;
  final DateTime streakStartDate;
  final bool isAtRisk;

  /// Banked freezes that auto-cover a missed day so one slip doesn't reset the streak.
  final int streakFreezes;

  UserStreak({
    required this.id,
    required this.streakType,
    required this.displayName,
    required this.icon,
    required this.currentCount,
    required this.bestCount,
    required this.lastIncrementDate,
    required this.streakStartDate,
    required this.isAtRisk,
    required this.streakFreezes,
  });

  factory UserStreak.fromJson(Map<String, dynamic> json) {
    return UserStreak(
      id: ((json['id'] ?? json['Id']) as num?)?.toInt() ?? 0,
      streakType: (json['streakType'] ?? json['StreakType'] ?? '').toString(),
      displayName:
          (json['displayName'] ?? json['DisplayName'] ?? '').toString(),
      icon: (json['icon'] ?? json['Icon'] ?? '🔥').toString(),
      currentCount:
          ((json['currentCount'] ?? json['CurrentCount']) as num?)?.toInt() ?? 0,
      bestCount:
          ((json['bestCount'] ?? json['BestCount']) as num?)?.toInt() ?? 0,
      lastIncrementDate: DateTime.tryParse(
              (json['lastIncrementDate'] ?? json['LastIncrementDate'] ?? '')
                  .toString()) ??
          DateTime.now(),
      streakStartDate: DateTime.tryParse(
              (json['streakStartDate'] ?? json['StreakStartDate'] ?? '')
                  .toString()) ??
          DateTime.now(),
      isAtRisk: ((json['isAtRisk'] ?? json['IsAtRisk']) as bool?) ?? false,
      streakFreezes:
          ((json['streakFreezes'] ?? json['StreakFreezes']) as num?)?.toInt() ??
              0,
    );
  }
}

class UserAchievement {
  final int id;
  final String achievementKey;
  final String title;
  final String description;
  final String icon;
  final String category;
  final int points;
  final DateTime earnedAt;
  final bool isNew;

  UserAchievement({
    required this.id,
    required this.achievementKey,
    required this.title,
    required this.description,
    required this.icon,
    required this.category,
    required this.points,
    required this.earnedAt,
    required this.isNew,
  });

  factory UserAchievement.fromJson(Map<String, dynamic> json) {
    return UserAchievement(
      id: ((json['id'] ?? json['Id']) as num?)?.toInt() ?? 0,
      achievementKey:
          (json['achievementKey'] ?? json['AchievementKey'] ?? '').toString(),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      description:
          (json['description'] ?? json['Description'] ?? '').toString(),
      icon: (json['icon'] ?? json['Icon'] ?? '🏆').toString(),
      category: (json['category'] ?? json['Category'] ?? '').toString(),
      points: ((json['points'] ?? json['Points']) as num?)?.toInt() ?? 0,
      earnedAt: DateTime.tryParse(
              (json['earnedAt'] ?? json['EarnedAt'] ?? '').toString()) ??
          DateTime.now(),
      isNew: ((json['isNew'] ?? json['IsNew']) as bool?) ?? false,
    );
  }
}

class UserChallenge {
  final int id;
  final String challengeType;
  final String title;
  final String description;
  final double targetValue;
  final double currentValue;
  final int rewardPoints;
  final DateTime startDate;
  final DateTime endDate;
  final String status;

  UserChallenge({
    required this.id,
    required this.challengeType,
    required this.title,
    required this.description,
    required this.targetValue,
    required this.currentValue,
    required this.rewardPoints,
    required this.startDate,
    required this.endDate,
    required this.status,
  });

  double get progressPercentage =>
      targetValue > 0 ? (currentValue / targetValue).clamp(0.0, 1.0) : 0.0;

  int get daysRemaining {
    final now = DateTime.now();
    if (endDate.isBefore(now)) return 0;
    return endDate.difference(now).inDays;
  }

  bool get isCompleted => status.toLowerCase() == 'completed';

  /// Expired means the end time has actually passed — NOT daysRemaining == 0,
  /// which is true for every same-day daily challenge all day long (that bug
  /// rendered active dailies as dismissible-expired).
  bool get isExpired => endDate.isBefore(DateTime.now()) && !isCompleted;

  factory UserChallenge.fromJson(Map<String, dynamic> json) {
    return UserChallenge(
      id: ((json['id'] ?? json['Id']) as num?)?.toInt() ?? 0,
      challengeType:
          (json['challengeType'] ?? json['ChallengeType'] ?? '').toString(),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      description:
          (json['description'] ?? json['Description'] ?? '').toString(),
      targetValue:
          ((json['targetValue'] ?? json['TargetValue'] ?? 0) as num).toDouble(),
      currentValue:
          ((json['currentValue'] ?? json['CurrentValue'] ?? 0) as num)
              .toDouble(),
      rewardPoints:
          ((json['rewardPoints'] ?? json['RewardPoints']) as num?)?.toInt() ?? 0,
      startDate: DateTime.tryParse(
              (json['startDate'] ?? json['StartDate'] ?? '').toString()) ??
          DateTime.now(),
      endDate: DateTime.tryParse(
              (json['endDate'] ?? json['EndDate'] ?? '').toString()) ??
          DateTime.now(),
      status: (json['status'] ?? json['Status'] ?? 'active').toString(),
    );
  }
}

/// One entry in the full achievement catalog (every definition merged with the
/// user's earned state). Locked "good" achievements arrive as masked silhouettes
/// ([title]/[description]/[icon] are null); cautionary ("oops") achievements are
/// always fully described.
class AchievementCatalogItem {
  final String key;
  final String? title;
  final String? description;
  final String? icon;
  final String category;
  final int points;

  /// Bronze/Silver/Gold/Platinum for "good" achievements; null for cautionary ones.
  final String? tier;

  /// Share of active users who have earned this (0–100).
  final double percentEarned;
  final bool isNegative;
  final bool isEarned;
  final DateTime? earnedAt;

  /// Times earned; only repeatable (oops) badges ever exceed 1.
  final int timesEarned;

  AchievementCatalogItem({
    required this.key,
    required this.title,
    required this.description,
    required this.icon,
    required this.category,
    required this.points,
    required this.tier,
    required this.percentEarned,
    required this.isNegative,
    required this.isEarned,
    required this.earnedAt,
    this.timesEarned = 0,
  });

  /// A locked "good" achievement — render as a silhouette (details hidden).
  bool get isSecret => !isEarned && !isNegative;

  factory AchievementCatalogItem.fromJson(Map<String, dynamic> json) {
    final bool earned =
        ((json['isEarned'] ?? json['IsEarned']) as bool?) ?? false;
    final rawEarnedAt = json['earnedAt'] ?? json['EarnedAt'];
    return AchievementCatalogItem(
      key: (json['key'] ?? json['Key'] ?? '').toString(),
      title: (json['title'] ?? json['Title']) as String?,
      description: (json['description'] ?? json['Description']) as String?,
      icon: (json['icon'] ?? json['Icon']) as String?,
      category: (json['category'] ?? json['Category'] ?? '').toString(),
      points: ((json['points'] ?? json['Points']) as num?)?.toInt() ?? 0,
      tier: (json['tier'] ?? json['Tier']) as String?,
      percentEarned:
          ((json['percentEarned'] ?? json['PercentEarned'] ?? 0) as num)
              .toDouble(),
      isNegative:
          ((json['isNegative'] ?? json['IsNegative']) as bool?) ?? false,
      isEarned: earned,
      earnedAt: rawEarnedAt != null
          ? DateTime.tryParse(rawEarnedAt.toString())
          : null,
      timesEarned:
          ((json['timesEarned'] ?? json['TimesEarned']) as num?)?.toInt() ??
              (earned ? 1 : 0),
    );
  }
}

// ── Service ─────────────────────────────────────────────

class GamificationService {
  Future<GamificationDashboard> getDashboard() async {
    final response =
        await apiClient.get<Map<String, dynamic>>('/gamification/dashboard');
    return GamificationDashboard.fromJson(response);
  }

  Future<List<UserAchievement>> getAchievements() async {
    final response =
        await apiClient.get<List<dynamic>>('/gamification/achievements');
    return response
        .map((e) => UserAchievement.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<void> markAchievementsSeen(List<String> keys) async {
    await apiClient.post('/gamification/achievements/seen', data: keys);
  }

  /// Advance the active challenge of [challengeType] (best-effort; ignores failures so a
  /// challenge nudge never disrupts the user's real action).
  Future<void> incrementChallengeProgress(String challengeType) async {
    try {
      await apiClient.post('/gamification/challenges/$challengeType/progress');
    } catch (_) {
      // Non-critical — challenges are a nudge, not a blocking flow.
    }
  }

  /// Full catalog (earned + locked). Locked "good" achievements come back masked;
  /// cautionary achievements are always described.
  Future<List<AchievementCatalogItem>> getAchievementCatalog() async {
    final response =
        await apiClient.get<List<dynamic>>('/gamification/catalog');
    return response
        .map((e) => AchievementCatalogItem.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<UserStreak>> getStreaks() async {
    final response =
        await apiClient.get<List<dynamic>>('/gamification/streaks');
    return response
        .map((e) => UserStreak.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<UserChallenge>> getChallenges() async {
    final response =
        await apiClient.get<List<dynamic>>('/gamification/challenges');
    return response
        .map((e) => UserChallenge.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<UserChallenge?> generateChallenge() async {
    try {
      final response = await apiClient
          .post<Map<String, dynamic>>('/gamification/challenges/generate');
      return UserChallenge.fromJson(response);
    } catch (_) {
      return null;
    }
  }

  Future<void> dismissChallenge(int id) async {
    await apiClient.delete('/gamification/challenges/$id');
  }

  Future<GamificationDashboard> checkAchievements() async {
    final response = await apiClient
        .post<Map<String, dynamic>>('/gamification/check-achievements');
    return GamificationDashboard.fromJson(response);
  }

  Future<List<UserStreak>> updateStreaks() async {
    final response = await apiClient
        .post<List<dynamic>>('/gamification/update-streaks');
    return response
        .map((e) => UserStreak.fromJson(e as Map<String, dynamic>))
        .toList();
  }
}

// Intentionally non-final so tests can inject a fake (no DI container).
var gamificationService = GamificationService();

// ── Helpers ─────────────────────────────────────────────

List<T> _parseList<T>(
    dynamic data, T Function(Map<String, dynamic>) fromJson) {
  if (data is List) {
    return data
        .map((e) => fromJson(e as Map<String, dynamic>))
        .toList();
  }
  return [];
}
