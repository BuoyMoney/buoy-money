import 'api_client.dart';

// ── Models ──────────────────────────────────────────────

class WrappedCategory {
  final int categoryId;
  final String name;
  final double amount;
  final double share;

  WrappedCategory({
    required this.categoryId,
    required this.name,
    required this.amount,
    required this.share,
  });

  factory WrappedCategory.fromJson(Map<String, dynamic> json) => WrappedCategory(
        categoryId: ((json['categoryId'] ?? json['CategoryId']) as num?)?.toInt() ?? 0,
        name: (json['name'] ?? json['Name'] ?? '').toString(),
        amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
        share: ((json['share'] ?? json['Share'] ?? 0) as num).toDouble(),
      );
}

class WrappedMerchant {
  final String name;
  final int visits;
  final double amount;

  WrappedMerchant({required this.name, required this.visits, required this.amount});

  factory WrappedMerchant.fromJson(Map<String, dynamic> json) => WrappedMerchant(
        name: (json['name'] ?? json['Name'] ?? '').toString(),
        visits: ((json['visits'] ?? json['Visits']) as num?)?.toInt() ?? 0,
        amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      );
}

class WrappedBadge {
  final String key;
  final String title;
  final String icon;
  final int points;

  WrappedBadge(
      {required this.key, required this.title, required this.icon, required this.points});

  factory WrappedBadge.fromJson(Map<String, dynamic> json) => WrappedBadge(
        key: (json['key'] ?? json['Key'] ?? '').toString(),
        title: (json['title'] ?? json['Title'] ?? '').toString(),
        icon: (json['icon'] ?? json['Icon'] ?? '🏆').toString(),
        points: ((json['points'] ?? json['Points']) as num?)?.toInt() ?? 0,
      );
}

class WrappedBiggestDay {
  final DateTime date;
  final double amount;

  WrappedBiggestDay({required this.date, required this.amount});

  factory WrappedBiggestDay.fromJson(Map<String, dynamic> json) => WrappedBiggestDay(
        date: DateTime.tryParse((json['date'] ?? json['Date'] ?? '').toString()) ??
            DateTime.now(),
        amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      );
}

class WrappedBonusCard {
  final String factKey;
  final String kicker;
  final String headline;
  final String value;
  final String detail;
  final String source; // rotation | ai | percentile

  WrappedBonusCard({
    required this.factKey,
    required this.kicker,
    required this.headline,
    required this.value,
    required this.detail,
    required this.source,
  });

  factory WrappedBonusCard.fromJson(Map<String, dynamic> json) =>
      WrappedBonusCard(
        factKey: (json['factKey'] ?? json['FactKey'] ?? '').toString(),
        kicker: (json['kicker'] ?? json['Kicker'] ?? '').toString(),
        headline: (json['headline'] ?? json['Headline'] ?? '').toString(),
        value: (json['value'] ?? json['Value'] ?? '').toString(),
        detail: (json['detail'] ?? json['Detail'] ?? '').toString(),
        source: (json['source'] ?? json['Source'] ?? 'rotation').toString(),
      );
}

class MonthlyWrapped {
  final int year;
  final int month;
  final double totalIncome;
  final double totalSpent;
  final double? previousMonthSpent;
  final WrappedCategory? topCategory;
  final WrappedMerchant? topMerchant;
  final int zeroSpendDays;
  final int longestStreak;
  final List<WrappedBadge> badgesEarned;
  final double savedToGoals;
  final WrappedBiggestDay? biggestSpendDay;
  final int transactionCount;
  final List<WrappedBonusCard> bonusCards;

  MonthlyWrapped({
    required this.year,
    required this.month,
    required this.totalIncome,
    required this.totalSpent,
    this.previousMonthSpent,
    this.topCategory,
    this.topMerchant,
    required this.zeroSpendDays,
    required this.longestStreak,
    required this.badgesEarned,
    required this.savedToGoals,
    this.biggestSpendDay,
    required this.transactionCount,
    this.bonusCards = const [],
  });

  factory MonthlyWrapped.fromJson(Map<String, dynamic> json) {
    Map<String, dynamic>? sub(String a, String b) =>
        (json[a] ?? json[b]) as Map<String, dynamic>?;
    return MonthlyWrapped(
      year: ((json['year'] ?? json['Year']) as num?)?.toInt() ?? 0,
      month: ((json['month'] ?? json['Month']) as num?)?.toInt() ?? 0,
      totalIncome: ((json['totalIncome'] ?? json['TotalIncome'] ?? 0) as num).toDouble(),
      totalSpent: ((json['totalSpent'] ?? json['TotalSpent'] ?? 0) as num).toDouble(),
      previousMonthSpent:
          ((json['previousMonthSpent'] ?? json['PreviousMonthSpent']) as num?)?.toDouble(),
      topCategory: sub('topCategory', 'TopCategory') != null
          ? WrappedCategory.fromJson(sub('topCategory', 'TopCategory')!)
          : null,
      topMerchant: sub('topMerchant', 'TopMerchant') != null
          ? WrappedMerchant.fromJson(sub('topMerchant', 'TopMerchant')!)
          : null,
      zeroSpendDays: ((json['zeroSpendDays'] ?? json['ZeroSpendDays']) as num?)?.toInt() ?? 0,
      longestStreak: ((json['longestStreak'] ?? json['LongestStreak']) as num?)?.toInt() ?? 0,
      badgesEarned: ((json['badgesEarned'] ?? json['BadgesEarned'] ?? []) as List<dynamic>)
          .map((e) => WrappedBadge.fromJson(e as Map<String, dynamic>))
          .toList(),
      savedToGoals: ((json['savedToGoals'] ?? json['SavedToGoals'] ?? 0) as num).toDouble(),
      biggestSpendDay: sub('biggestSpendDay', 'BiggestSpendDay') != null
          ? WrappedBiggestDay.fromJson(sub('biggestSpendDay', 'BiggestSpendDay')!)
          : null,
      transactionCount:
          ((json['transactionCount'] ?? json['TransactionCount']) as num?)?.toInt() ?? 0,
      bonusCards: ((json['bonusCards'] ?? json['BonusCards'] ?? []) as List<dynamic>)
          .map((e) => WrappedBonusCard.fromJson(e as Map<String, dynamic>))
          .toList(),
    );
  }
}

class YearlyWrapped {
  final int year;
  final double totalIncome;
  final double totalSpent;
  final List<double> monthlySpend;
  final List<WrappedCategory> topCategories;
  final WrappedMerchant? topMerchant;
  final int zeroSpendDays;
  final int longestStreak;
  final int badgesEarnedCount;
  final WrappedBadge? bestBadge;
  final double savedToGoals;
  final WrappedBiggestDay? biggestSpendDay;
  final int transactionCount;
  final List<WrappedBonusCard> bonusCards;

  YearlyWrapped({
    required this.year,
    required this.totalIncome,
    required this.totalSpent,
    required this.monthlySpend,
    required this.topCategories,
    this.topMerchant,
    required this.zeroSpendDays,
    required this.longestStreak,
    required this.badgesEarnedCount,
    this.bestBadge,
    required this.savedToGoals,
    this.biggestSpendDay,
    required this.transactionCount,
    this.bonusCards = const [],
  });

  factory YearlyWrapped.fromJson(Map<String, dynamic> json) {
    Map<String, dynamic>? sub(String a, String b) =>
        (json[a] ?? json[b]) as Map<String, dynamic>?;
    return YearlyWrapped(
      year: ((json['year'] ?? json['Year']) as num?)?.toInt() ?? 0,
      totalIncome: ((json['totalIncome'] ?? json['TotalIncome'] ?? 0) as num).toDouble(),
      totalSpent: ((json['totalSpent'] ?? json['TotalSpent'] ?? 0) as num).toDouble(),
      monthlySpend: ((json['monthlySpend'] ?? json['MonthlySpend'] ?? []) as List<dynamic>)
          .map((e) => (e as num).toDouble())
          .toList(),
      topCategories:
          ((json['topCategories'] ?? json['TopCategories'] ?? []) as List<dynamic>)
              .map((e) => WrappedCategory.fromJson(e as Map<String, dynamic>))
              .toList(),
      topMerchant: sub('topMerchant', 'TopMerchant') != null
          ? WrappedMerchant.fromJson(sub('topMerchant', 'TopMerchant')!)
          : null,
      zeroSpendDays: ((json['zeroSpendDays'] ?? json['ZeroSpendDays']) as num?)?.toInt() ?? 0,
      longestStreak: ((json['longestStreak'] ?? json['LongestStreak']) as num?)?.toInt() ?? 0,
      badgesEarnedCount:
          ((json['badgesEarnedCount'] ?? json['BadgesEarnedCount']) as num?)?.toInt() ?? 0,
      bestBadge: sub('bestBadge', 'BestBadge') != null
          ? WrappedBadge.fromJson(sub('bestBadge', 'BestBadge')!)
          : null,
      savedToGoals: ((json['savedToGoals'] ?? json['SavedToGoals'] ?? 0) as num).toDouble(),
      biggestSpendDay: sub('biggestSpendDay', 'BiggestSpendDay') != null
          ? WrappedBiggestDay.fromJson(sub('biggestSpendDay', 'BiggestSpendDay')!)
          : null,
      transactionCount:
          ((json['transactionCount'] ?? json['TransactionCount']) as num?)?.toInt() ?? 0,
      bonusCards: ((json['bonusCards'] ?? json['BonusCards'] ?? []) as List<dynamic>)
          .map((e) => WrappedBonusCard.fromJson(e as Map<String, dynamic>))
          .toList(),
    );
  }
}

// ── Service ─────────────────────────────────────────────

class WrappedService {
  Future<MonthlyWrapped> getMonthly(int year, int month) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/wrapped/monthly',
      queryParameters: {'year': year, 'month': month},
    );
    return MonthlyWrapped.fromJson(response);
  }

  Future<YearlyWrapped> getYearly(int year) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/wrapped/yearly',
      queryParameters: {'year': year},
    );
    return YearlyWrapped.fromJson(response);
  }
}

// Intentionally non-final so tests can inject a fake (no DI container).
var wrappedService = WrappedService();
