import 'api_client.dart';

/// Category model matching backend CategoryDto
class Category {
  final int id;
  final String name;
  final String color;
  final String? icon;
  final bool isIncome;
  final bool isSystem;
  final int sortOrder;
  final String? description;
  final String scope; // "system", "personal", "family"
  final int? familyId;
  final bool canEdit;

  Category({
    required this.id,
    required this.name,
    required this.color,
    this.icon,
    required this.isIncome,
    required this.isSystem,
    required this.sortOrder,
    this.description,
    required this.scope,
    this.familyId,
    required this.canEdit,
  });

  factory Category.fromJson(Map<String, dynamic> json) {
    return Category(
      id: json['id'] as int,
      name: json['name'] as String? ?? '',
      color: json['color'] as String? ?? '#6B7280',
      icon: json['icon'] as String?,
      isIncome: json['isIncome'] as bool? ?? false,
      isSystem: json['isSystem'] as bool? ?? true,
      sortOrder: json['sortOrder'] as int? ?? 0,
      description: json['description'] as String?,
      scope: json['scope'] as String? ?? 'system',
      familyId: json['familyId'] as int?,
      canEdit: json['canEdit'] as bool? ?? false,
    );
  }
}

/// Response from GET /categories
class CategoriesResponse {
  final List<Category> systemCategories;
  final List<Category> personalCategories;
  final List<Category> familyCategories;
  final List<Category> allCategories;

  CategoriesResponse({
    required this.systemCategories,
    required this.personalCategories,
    required this.familyCategories,
    required this.allCategories,
  });

  factory CategoriesResponse.fromJson(Map<String, dynamic> json) {
    return CategoriesResponse(
      systemCategories: _parseList(json['systemCategories']),
      personalCategories: _parseList(json['personalCategories']),
      familyCategories: _parseList(json['familyCategories']),
      allCategories: _parseList(json['allCategories']),
    );
  }

  static List<Category> _parseList(dynamic list) {
    return (list as List<dynamic>?)
        ?.map((e) => Category.fromJson(e as Map<String, dynamic>))
        .toList() ?? [];
  }
}

/// Categories Service
class CategoriesService {
  CategoriesResponse? _cache;

  /// Get all categories (cached after first call)
  Future<CategoriesResponse> getAll({bool forceRefresh = false}) async {
    if (_cache != null && !forceRefresh) return _cache!;
    final response = await apiClient.get<Map<String, dynamic>>('/categories');
    _cache = CategoriesResponse.fromJson(response);
    return _cache!;
  }

  /// Create a new category
  Future<Category> create({
    required String name,
    required String color,
    String? icon,
    bool isIncome = false,
    bool isFamily = false,
  }) async {
    final response = await apiClient.post<Map<String, dynamic>>(
      '/categories',
      data: {
        'name': name,
        'color': color,
        if (icon != null) 'icon': icon,
        'isIncome': isIncome,
        'isFamily': isFamily,
      },
    );
    _cache = null; // invalidate cache
    return Category.fromJson(response);
  }

  /// Delete a category
  Future<void> delete(int id) async {
    await apiClient.delete<dynamic>('/categories/$id');
    _cache = null;
  }

  void clearCache() => _cache = null;
}

/// Singleton
// Mutable so widget tests can swap in a fake, as with transactionsService/accountsService.
CategoriesService categoriesService = CategoriesService();
