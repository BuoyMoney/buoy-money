import 'api_client.dart';

// ── Models ──────────────────────────────────────────────

class ChartDataPoint {
  final String name;
  final double value;
  final String? color;
  final String? label;

  ChartDataPoint({
    required this.name,
    required this.value,
    this.color,
    this.label,
  });

  factory ChartDataPoint.fromJson(Map<String, dynamic> json) {
    return ChartDataPoint(
      name: (json['name'] ?? json['Name'] ?? '').toString(),
      value: ((json['value'] ?? json['Value'] ?? 0) as num).toDouble(),
      color: (json['color'] ?? json['Color'])?.toString(),
      label: (json['label'] ?? json['Label'])?.toString(),
    );
  }
}

class ChartConfig {
  final String? xAxisLabel;
  final String? yAxisLabel;
  final bool showLegend;
  final bool animate;
  final String? valuePrefix;
  final String? valueSuffix;

  ChartConfig({
    this.xAxisLabel,
    this.yAxisLabel,
    this.showLegend = true,
    this.animate = true,
    this.valuePrefix,
    this.valueSuffix,
  });

  factory ChartConfig.fromJson(Map<String, dynamic> json) {
    return ChartConfig(
      xAxisLabel: (json['xAxisLabel'] ?? json['XAxisLabel'])?.toString(),
      yAxisLabel: (json['yAxisLabel'] ?? json['YAxisLabel'])?.toString(),
      showLegend: (json['showLegend'] ?? json['ShowLegend']) as bool? ?? true,
      animate: (json['animate'] ?? json['Animate']) as bool? ?? true,
      valuePrefix: (json['valuePrefix'] ?? json['ValuePrefix'])?.toString(),
      valueSuffix: (json['valueSuffix'] ?? json['ValueSuffix'])?.toString(),
    );
  }
}

class UserVisualization {
  final int id;
  final String chartType;
  final String title;
  final String message;
  final String? motivationalMessage;
  final List<ChartDataPoint> chartData;
  final ChartConfig? chartConfig;
  final String category;
  final String priority;
  final double confidence;
  final bool isStarred;
  final bool isDismissed;
  final int viewCount;
  final DateTime? lastViewedAt;
  final DateTime generatedAt;

  UserVisualization({
    required this.id,
    required this.chartType,
    required this.title,
    required this.message,
    this.motivationalMessage,
    required this.chartData,
    this.chartConfig,
    required this.category,
    required this.priority,
    required this.confidence,
    required this.isStarred,
    required this.isDismissed,
    required this.viewCount,
    this.lastViewedAt,
    required this.generatedAt,
  });

  factory UserVisualization.fromJson(Map<String, dynamic> json) {
    return UserVisualization(
      id: int.tryParse((json['id'] ?? json['Id'] ?? 0).toString()) ?? 0,
      chartType: (json['chartType'] ?? json['ChartType'] ?? 'bar').toString(),
      title: (json['title'] ?? json['Title'] ?? '').toString(),
      message: (json['message'] ?? json['Message'] ?? '').toString(),
      motivationalMessage:
          (json['motivationalMessage'] ?? json['MotivationalMessage'])
              ?.toString(),
      chartData: _parseChartData(json['chartData'] ?? json['ChartData']),
      chartConfig: json['chartConfig'] != null || json['ChartConfig'] != null
          ? ChartConfig.fromJson((json['chartConfig'] ?? json['ChartConfig'])
              as Map<String, dynamic>)
          : null,
      category: (json['category'] ?? json['Category'] ?? 'general').toString(),
      priority: (json['priority'] ?? json['Priority'] ?? 'medium').toString(),
      confidence:
          ((json['confidence'] ?? json['Confidence'] ?? 0) as num).toDouble(),
      isStarred: (json['isStarred'] ?? json['IsStarred']) as bool? ?? false,
      isDismissed:
          (json['isDismissed'] ?? json['IsDismissed']) as bool? ?? false,
      viewCount:
          int.tryParse((json['viewCount'] ?? json['ViewCount'] ?? 0).toString()) ??
              0,
      lastViewedAt: json['lastViewedAt'] != null || json['LastViewedAt'] != null
          ? DateTime.tryParse(
              (json['lastViewedAt'] ?? json['LastViewedAt']).toString())
          : null,
      generatedAt: DateTime.tryParse(
              (json['generatedAt'] ?? json['GeneratedAt'] ?? '').toString()) ??
          DateTime.now(),
    );
  }
}

class VisualizationListResponse {
  final List<UserVisualization> visualizations;
  final int totalCount;
  final int page;
  final int pageSize;
  final int totalPages;

  VisualizationListResponse({
    required this.visualizations,
    required this.totalCount,
    required this.page,
    required this.pageSize,
    required this.totalPages,
  });

  factory VisualizationListResponse.fromJson(Map<String, dynamic> json) {
    final vizList = json['visualizations'] ?? json['Visualizations'] ?? [];
    return VisualizationListResponse(
      visualizations: (vizList as List)
          .map((e) => UserVisualization.fromJson(e as Map<String, dynamic>))
          .toList(),
      totalCount:
          int.tryParse((json['totalCount'] ?? json['TotalCount'] ?? 0).toString()) ??
              0,
      page: int.tryParse((json['page'] ?? json['Page'] ?? 1).toString()) ?? 1,
      pageSize:
          int.tryParse((json['pageSize'] ?? json['PageSize'] ?? 10).toString()) ??
              10,
      totalPages:
          int.tryParse((json['totalPages'] ?? json['TotalPages'] ?? 0).toString()) ??
              0,
    );
  }
}

class VisualizationCategory {
  final String value;
  final String label;

  VisualizationCategory({required this.value, required this.label});

  factory VisualizationCategory.fromJson(Map<String, dynamic> json) {
    return VisualizationCategory(
      value: (json['value'] ?? json['Value'] ?? '').toString(),
      label: (json['label'] ?? json['Label'] ?? '').toString(),
    );
  }
}

// ── Service ─────────────────────────────────────────────

class VisualizationService {
  Future<UserVisualization> generateVisualization({
    String? categoryHint,
    String? focusArea,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/visualizations/generate',
      data: {
        if (categoryHint != null) 'categoryHint': categoryHint,
        if (focusArea != null) 'focusArea': focusArea,
      },
    );
    return UserVisualization.fromJson(response);
  }

  Future<VisualizationListResponse> getVisualizations({
    int page = 1,
    int pageSize = 10,
    String? category,
    bool includeDismissed = false,
  }) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/visualizations',
      queryParameters: {
        'page': page,
        'pageSize': pageSize,
        if (category != null) 'category': category,
        'includeDismissed': includeDismissed,
      },
    );
    return VisualizationListResponse.fromJson(response);
  }

  Future<VisualizationListResponse> getStarredVisualizations({
    int page = 1,
    int pageSize = 10,
  }) async {
    final response = await apiClient.get<Map<String, dynamic>>(
      '/visualizations/starred',
      queryParameters: {'page': page, 'pageSize': pageSize},
    );
    return VisualizationListResponse.fromJson(response);
  }

  Future<UserVisualization> getVisualization(int id) async {
    final response =
        await apiClient.get<Map<String, dynamic>>('/visualizations/$id');
    return UserVisualization.fromJson(response);
  }

  Future<bool> toggleStar(int id) async {
    final response =
        await apiClient.post<Map<String, dynamic>>('/visualizations/$id/star');
    return (response['isStarred'] ?? response['IsStarred']) as bool? ?? false;
  }

  Future<void> dismissVisualization(int id) async {
    await apiClient.post('/visualizations/$id/dismiss');
  }

  Future<List<VisualizationCategory>> getCategories() async {
    final response =
        await apiClient.get<List<dynamic>>('/visualizations/categories');
    return response
        .map((e) => VisualizationCategory.fromJson(e as Map<String, dynamic>))
        .toList();
  }
}

// Intentionally non-final so tests can inject a fake (no DI container).
var visualizationService = VisualizationService();

// ── Helpers ─────────────────────────────────────────────

List<ChartDataPoint> _parseChartData(dynamic data) {
  if (data is List) {
    return data
        .map((e) => ChartDataPoint.fromJson(e as Map<String, dynamic>))
        .toList();
  }
  return [];
}
