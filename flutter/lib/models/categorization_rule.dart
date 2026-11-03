/// A categorization rule as the Categorization Rules page shows it: what it matches, the
/// category it assigns, whether it is active, and how often it has fired. Mirrors the backend
/// CategorizationRuleDto; reads both camelCase and PascalCase keys.
class CategorizationRule {
  final int id;
  final String name;
  final bool isActive;
  final bool sharedWithFamily;
  final int ownerUserId;
  final String? merchantExact;
  final String? merchantContains;
  final String? descriptionContains;
  final String? assignCategoryName;
  final int timesApplied;

  const CategorizationRule({
    required this.id,
    required this.name,
    required this.isActive,
    required this.sharedWithFamily,
    this.ownerUserId = 0,
    this.merchantExact,
    this.merchantContains,
    this.descriptionContains,
    this.assignCategoryName,
    this.timesApplied = 0,
  });

  /// The merchant or text the rule matches, for display.
  String get matchText {
    for (final v in [merchantExact, merchantContains, descriptionContains]) {
      if (v != null && v.trim().isNotEmpty) return v.trim();
    }
    return name;
  }

  CategorizationRule copyWith({bool? isActive}) => CategorizationRule(
        id: id,
        name: name,
        isActive: isActive ?? this.isActive,
        sharedWithFamily: sharedWithFamily,
        ownerUserId: ownerUserId,
        merchantExact: merchantExact,
        merchantContains: merchantContains,
        descriptionContains: descriptionContains,
        assignCategoryName: assignCategoryName,
        timesApplied: timesApplied,
      );

  factory CategorizationRule.fromJson(Map<String, dynamic> json) {
    T? read<T>(String camel) {
      final pascal = camel[0].toUpperCase() + camel.substring(1);
      return (json[camel] ?? json[pascal]) as T?;
    }

    return CategorizationRule(
      id: (read<num>('id') ?? 0).toInt(),
      name: read<String>('name') ?? '',
      isActive: read<bool>('isActive') ?? true,
      sharedWithFamily: read<num>('familyId') != null,
      ownerUserId: (read<num>('ownerUserId') ?? 0).toInt(),
      merchantExact: read<String>('merchantExact'),
      merchantContains: read<String>('merchantContains'),
      descriptionContains: read<String>('descriptionContains'),
      assignCategoryName: read<String>('assignCategoryName'),
      timesApplied: (read<num>('timesApplied') ?? 0).toInt(),
    );
  }
}
