import 'api_client.dart';

/// AI Insight type
enum InsightType {
  tip,
  alert,
  opportunity,
  trend,
  achievement,
}

/// AI Insight model
class AIInsight {
  final String id;
  final InsightType type;
  final String title;
  final String description;
  final String? actionLabel;
  final String? actionRoute;
  final DateTime createdAt;
  final bool isDismissed;
  // Backend serializes priority as a string ('low' | 'medium' | 'high') — see
  // the sibling AIInsight in ai_service.dart. Typed as int? this cast threw on
  // AOT (mobile), and the swallow-all catch turned the whole Insights page into
  // an empty state on every native load while web (dart2js) silently worked.
  final String? priority;

  AIInsight({
    required this.id,
    required this.type,
    required this.title,
    required this.description,
    this.actionLabel,
    this.actionRoute,
    required this.createdAt,
    this.isDismissed = false,
    this.priority,
  });

  factory AIInsight.fromJson(Map<String, dynamic> json) {
    return AIInsight(
      id: (json['id'] ?? json['Id'] ?? '').toString(),
      type: _parseInsightType(json['type'] ?? json['Type']),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      description: (json['description'] ?? json['Description'] ?? '').toString(),
      actionLabel: (json['actionLabel'] ?? json['ActionLabel'])?.toString(),
      actionRoute: (json['actionRoute'] ?? json['ActionRoute'])?.toString(),
      createdAt: DateTime.tryParse((json['createdAt'] ?? json['CreatedAt'] ?? '').toString()) ?? DateTime.now(),
      isDismissed: (json['isDismissed'] ?? json['IsDismissed']) as bool? ?? false,
      priority: (json['priority'] ?? json['Priority'])?.toString(),
    );
  }

  static InsightType _parseInsightType(dynamic type) {
    if (type is int) {
      return InsightType.values[type.clamp(0, InsightType.values.length - 1)];
    }
    if (type is String) {
      return InsightType.values.firstWhere(
        (t) => t.name.toLowerCase() == type.toLowerCase(),
        orElse: () => InsightType.tip,
      );
    }
    return InsightType.tip;
  }
}

/// Category spending breakdown
class CategorySpending {
  final String categoryName;
  final int categoryId;
  final double amount;
  final int transactionCount;
  final double percentageOfTotal;

  CategorySpending({
    required this.categoryName,
    required this.categoryId,
    required this.amount,
    required this.transactionCount,
    required this.percentageOfTotal,
  });

  factory CategorySpending.fromJson(Map<String, dynamic> json) {
    return CategorySpending(
      categoryName: (json['categoryName'] ?? json['CategoryName'] ?? 'Unknown').toString(),
      categoryId: ((json['categoryId'] ?? json['CategoryId']) as num?)?.toInt() ?? 0,
      // Backend CategoryStatisticsDto serializes these as totalAmount / percentage.
      amount: ((json['totalAmount'] ?? json['TotalAmount'] ?? json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      transactionCount: ((json['transactionCount'] ?? json['TransactionCount']) as num?)?.toInt() ?? 0,
      percentageOfTotal: ((json['percentage'] ?? json['Percentage'] ?? json['percentageOfTotal'] ?? json['PercentageOfTotal'] ?? 0) as num).toDouble(),
    );
  }
}

/// Spending trend data point
class SpendingTrendPoint {
  final DateTime date;
  final double amount;
  final String label;

  SpendingTrendPoint({
    required this.date,
    required this.amount,
    required this.label,
  });

  factory SpendingTrendPoint.fromJson(Map<String, dynamic> json) {
    return SpendingTrendPoint(
      date: DateTime.tryParse((json['date'] ?? json['Date'] ?? '').toString()) ?? DateTime.now(),
      amount: ((json['amount'] ?? json['Amount'] ?? 0) as num).toDouble(),
      label: (json['label'] ?? json['Label'] ?? '').toString(),
    );
  }
}

/// Spending analysis response
class SpendingAnalysis {
  final double totalSpending;
  final double totalIncome;
  final double netAmount;
  final List<CategorySpending> categoryBreakdown;
  final List<SpendingTrendPoint> dailyTrend;
  final double averageDailySpending;
  final String periodLabel;

  SpendingAnalysis({
    required this.totalSpending,
    required this.totalIncome,
    required this.netAmount,
    required this.categoryBreakdown,
    required this.dailyTrend,
    required this.averageDailySpending,
    required this.periodLabel,
  });

  factory SpendingAnalysis.fromJson(Map<String, dynamic> json) {
    return SpendingAnalysis(
      totalSpending: ((json['totalSpending'] ?? json['TotalSpending'] ?? json['totalExpenses'] ?? 0) as num).toDouble(),
      totalIncome: ((json['totalIncome'] ?? json['TotalIncome'] ?? 0) as num).toDouble(),
      netAmount: ((json['netAmount'] ?? json['NetAmount'] ?? 0) as num).toDouble(),
      categoryBreakdown: _parseCategoryBreakdown(json),
      // Parenthesize the ?? before the cast — `as` binds tighter than `??`, so
      // without these parens only `DailyTrend` was cast and the camelCase path
      // yielded a `List<dynamic>` that throws assigning to `List<SpendingTrendPoint>`
      // on AOT (mobile). Same precedence trap as accounts_service.PlaidConnection.
      dailyTrend: ((json['dailyTrend'] ?? json['DailyTrend']) as List<dynamic>?)
          ?.map((e) => SpendingTrendPoint.fromJson(e as Map<String, dynamic>))
          .toList() ??
          <SpendingTrendPoint>[],
      averageDailySpending: ((json['averageDailySpending'] ?? json['AverageDailySpending'] ?? 0) as num).toDouble(),
      periodLabel: (json['periodLabel'] ?? json['PeriodLabel'] ?? 'This Month').toString(),
    );
  }

  static List<CategorySpending> _parseCategoryBreakdown(Map<String, dynamic> json) {
    // Handle both array format and map format
    final breakdown = json['categoryBreakdown'] ?? json['CategoryBreakdown'] ?? json['categoryTotals'];
    
    if (breakdown is List) {
      return breakdown
          .map((e) => CategorySpending.fromJson(e as Map<String, dynamic>))
          .toList();
    }
    
    if (breakdown is Map) {
      // Convert map format to list
      final totalSpending = ((json['totalSpending'] ?? json['totalExpenses'] ?? 1) as num).toDouble();
      return breakdown.entries.map((e) {
        final amount = (e.value as num).toDouble();
        return CategorySpending(
          categoryName: e.key as String,
          categoryId: 0,
          amount: amount,
          transactionCount: 0,
          percentageOfTotal: totalSpending > 0 ? (amount / totalSpending) * 100 : 0,
        );
      }).toList()
        ..sort((a, b) => b.amount.compareTo(a.amount)); // Sort by amount descending
    }
    
    return [];
  }

  factory SpendingAnalysis.empty() {
    return SpendingAnalysis(
      totalSpending: 0,
      totalIncome: 0,
      netAmount: 0,
      categoryBreakdown: [],
      dailyTrend: [],
      averageDailySpending: 0,
      periodLabel: 'No Data',
    );
  }
}

/// Insights Service
/// 
/// Handles AI insights and spending analysis API calls.
class InsightsService {
  /// Get AI insights for a context
  Future<List<AIInsight>> getInsights({String context = 'dashboard', int maxInsights = 5}) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights',
        data: {
          'context': context,
          'maxInsights': maxInsights,
        },
      );
      
      final insights = response['insights'] as List<dynamic>?;
      if (insights != null) {
        return insights
            .map((e) => AIInsight.fromJson(e as Map<String, dynamic>))
            .toList();
      }
      return [];
    } catch (e) {
      // AI insights may not be available (non-premium, etc.)
      return [];
    }
  }

  /// Refresh AI insights (force regeneration)
  Future<List<AIInsight>> refreshInsights({String context = 'dashboard'}) async {
    try {
      final response = await apiClient.post<Map<String, dynamic>>(
        '/ai/insights/refresh/$context',
      );
      
      final insights = response['insights'] as List<dynamic>?;
      if (insights != null) {
        return insights
            .map((e) => AIInsight.fromJson(e as Map<String, dynamic>))
            .toList();
      }
      return [];
    } catch (e) {
      return [];
    }
  }

  /// Dismiss an insight
  Future<bool> dismissInsight(String insightId) async {
    try {
      await apiClient.post('/ai/insights/$insightId/dismiss');
      return true;
    } catch (e) {
      return false;
    }
  }

  /// Get spending analysis for a date range
  Future<SpendingAnalysis> getSpendingAnalysis({
    DateTime? startDate,
    DateTime? endDate,
  }) async {
    try {
      final params = <String, dynamic>{};
      if (startDate != null) {
        params['startDate'] = startDate.toIso8601String().split('T')[0];
      }
      if (endDate != null) {
        params['endDate'] = endDate.toIso8601String().split('T')[0];
      }
      
      final response = await apiClient.get<Map<String, dynamic>>(
        '/transactions/summary',
        queryParameters: params.isNotEmpty ? params : null,
      );
      
      return SpendingAnalysis.fromJson(response);
    } catch (e) {
      return SpendingAnalysis.empty();
    }
  }

  /// Get category statistics
  Future<List<CategorySpending>> getCategoryBreakdown({
    DateTime? startDate,
    DateTime? endDate,
  }) async {
    try {
      final params = <String, dynamic>{};
      if (startDate != null) {
        params['startDate'] = startDate.toIso8601String().split('T')[0];
      }
      if (endDate != null) {
        params['endDate'] = endDate.toIso8601String().split('T')[0];
      }
      
      final response = await apiClient.get<List<dynamic>>(
        '/transactions/categories',
        queryParameters: params.isNotEmpty ? params : null,
      );
      
      return response
          .map((e) => CategorySpending.fromJson(e as Map<String, dynamic>))
          .toList();
    } catch (e) {
      return [];
    }
  }
}

/// Singleton instance
// Intentionally non-final so tests can inject a fake (no DI container).
var insightsService = InsightsService();
