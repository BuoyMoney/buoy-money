import '../utils/formatters.dart';
import 'api_client.dart';

/// One feature's share of the period's AI spend. Money is whole micro-dollars (1 USD = 1,000,000), as integers.
class AiFeatureUsage {
  final String feature;
  final int calls;
  final int costMicros;

  const AiFeatureUsage({required this.feature, required this.calls, required this.costMicros});

  factory AiFeatureUsage.fromJson(Map<String, dynamic> json) => AiFeatureUsage(
        feature: (json['feature'] ?? '').toString(),
        calls: (json['calls'] as num?)?.toInt() ?? 0,
        costMicros: (json['costMicros'] as num?)?.toInt() ?? 0,
      );
}

/// Pay-as-you-go: spend beyond the allowance, only up to a cap the customer sets.
class AiPaygStatus {
  final bool available;
  final bool enabled;
  final int capMicros;
  final int usedMicros;
  final int maxCapMicros;

  const AiPaygStatus({
    this.available = false,
    this.enabled = false,
    this.capMicros = 0,
    this.usedMicros = 0,
    this.maxCapMicros = 0,
  });

  factory AiPaygStatus.fromJson(Map<String, dynamic> json) => AiPaygStatus(
        available: json['available'] == true,
        enabled: json['enabled'] == true,
        capMicros: (json['capMicros'] as num?)?.toInt() ?? 0,
        usedMicros: (json['usedMicros'] as num?)?.toInt() ?? 0,
        maxCapMicros: (json['maxCapMicros'] as num?)?.toInt() ?? 0,
      );
}

/// The answer of `GET /api/aiusage`.
///
/// [source] is `cloud` when the allowance lives in BuoyAI Cloud (one allowance for the whole server) and `local`
/// when it is this server's own tracking. [unlimited] means nothing caps AI here, so the figures are statistics.
class AiUsageSummary {
  final String source;
  final bool unlimited;
  final DateTime periodStart;
  final DateTime periodEnd;
  final int allowanceMicros;
  final int usedMicros;
  final int percent;

  /// `ok`, `warning` (80 percent and up) or `over` (the allowance is used up).
  final String state;
  final bool blocked;
  final AiPaygStatus payg;
  final List<AiFeatureUsage> byFeature;
  final int projectedMicros;

  const AiUsageSummary({
    required this.source,
    required this.unlimited,
    required this.periodStart,
    required this.periodEnd,
    required this.allowanceMicros,
    required this.usedMicros,
    required this.percent,
    required this.state,
    required this.blocked,
    required this.payg,
    required this.byFeature,
    required this.projectedMicros,
  });

  bool get isOver => state == 'over';
  bool get isWarning => state == 'warning';

  factory AiUsageSummary.fromJson(Map<String, dynamic> json) {
    final features = <AiFeatureUsage>[];
    final raw = json['byFeature'];
    if (raw is List) {
      for (final item in raw) {
        if (item is Map) features.add(AiFeatureUsage.fromJson(Map<String, dynamic>.from(item)));
      }
    }
    final payg = json['payg'];
    return AiUsageSummary(
      source: (json['source'] ?? 'local').toString(),
      unlimited: json['unlimited'] == true,
      periodStart: DateTime.tryParse((json['periodStart'] ?? '').toString())?.toLocal() ?? DateTime.now(),
      periodEnd: DateTime.tryParse((json['periodEnd'] ?? '').toString())?.toLocal() ?? DateTime.now(),
      allowanceMicros: (json['allowanceMicros'] as num?)?.toInt() ?? 0,
      usedMicros: (json['usedMicros'] as num?)?.toInt() ?? 0,
      percent: (json['percent'] as num?)?.toInt() ?? 0,
      state: (json['state'] ?? 'ok').toString(),
      blocked: json['blocked'] == true,
      payg: payg is Map ? AiPaygStatus.fromJson(Map<String, dynamic>.from(payg)) : const AiPaygStatus(),
      byFeature: features,
      projectedMicros: (json['projectedMicros'] as num?)?.toInt() ?? 0,
    );
  }
}

/// US dollars from whole micro-dollars, rounded to the cent with integer math ("$1.50"), written with the app's
/// unambiguous USD symbol whatever the user's own currency is: the allowance is billed in dollars. A tiny nonzero amount
/// reads "<$0.01" rather than a misleading "$0.00".
String formatMicros(int micros) {
  if (micros <= 0) return formatCurrencyCode(0, 'USD');
  final cents = (micros + 5000) ~/ 10000;
  if (cents == 0) return '<${formatCurrencyCode(0.01, 'USD')}';
  return formatCurrencyCode(cents / 100, 'USD');
}

/// Cents typed by a person ("5", "5.5", "5.50", "$5") as micro-dollars, or null if it is not a plain amount.
/// Whole cents only, so no floating point ever reaches the money.
int? parseDollarsToMicros(String text) {
  final cleaned = text.trim().replaceFirst(RegExp(r'^[$]'), '');
  final match = RegExp(r'^(\d{1,7})(?:\.(\d{1,2}))?$').firstMatch(cleaned);
  if (match == null) return null;
  final dollars = int.parse(match.group(1)!);
  final centsText = (match.group(2) ?? '').padRight(2, '0');
  final cents = dollars * 100 + int.parse(centsText);
  return cents * 10000;
}

class AiUsageService {
  Future<AiUsageSummary> fetch() async {
    final json = await apiClient.get<Map<String, dynamic>>('/aiusage');
    return AiUsageSummary.fromJson(json);
  }

  /// Turns pay-as-you-go on with a cap in micro-dollars, or off. Administrator only.
  Future<AiUsageSummary> setPayg({required bool enabled, required int capMicros}) async {
    final json = await apiClient.put<Map<String, dynamic>>(
      '/aiusage/payg',
      data: {'enabled': enabled, 'capMicros': capMicros},
    );
    return AiUsageSummary.fromJson(json);
  }
}

final aiUsageService = AiUsageService();
