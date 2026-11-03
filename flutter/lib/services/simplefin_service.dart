import 'package:dio/dio.dart';

import 'api_client.dart';

/// One bank connection from `GET /api/aggregators/connections` (any provider).
class BankConnectionSummary {
  final int id;
  final String provider;
  final String institutionName;
  final bool isActive;

  /// The connection is waiting on the user (paste a new token, or sign in at the bridge).
  final bool requiresUserAction;

  /// Active, LoginRequired, Error, Disconnected and so on.
  final String status;
  final DateTime? lastSyncAt;

  /// Why it needs attention, in words the server already cleaned. Null when it does not.
  final String? message;
  final int accountCount;

  const BankConnectionSummary({
    required this.id,
    required this.provider,
    required this.institutionName,
    required this.isActive,
    required this.requiresUserAction,
    required this.status,
    required this.lastSyncAt,
    required this.message,
    required this.accountCount,
  });

  bool get isSimpleFin => provider == SimpleFinService.providerName;

  /// A connection shown as healthy: active, nothing waiting on the user.
  bool get isHealthy => isActive && !requiresUserAction && status == 'Active';

  // The backend writes camelCase; accept PascalCase too (project rule for all models).
  factory BankConnectionSummary.fromJson(Map<String, dynamic> json) {
    dynamic get(String key) =>
        json[key] ?? json[key[0].toUpperCase() + key.substring(1)];
    return BankConnectionSummary(
      id: (get('id') as num?)?.toInt() ?? 0,
      provider: (get('provider') ?? '').toString(),
      institutionName: (get('institutionName') ?? '').toString(),
      isActive: (get('isActive') as bool?) ?? true,
      requiresUserAction: (get('requiresUserAction') as bool?) ?? false,
      status: (get('status') ?? 'Active').toString(),
      lastSyncAt: DateTime.tryParse((get('lastSyncAt') ?? '').toString()),
      message: get('message')?.toString(),
      accountCount: (get('accountCount') as num?)?.toInt() ?? 0,
    );
  }
}

/// What the server said when a pasted token was submitted.
class SimpleFinConnectResult {
  final bool success;

  /// Why it failed, or a note about a connection that was saved but did not finish its first sync.
  final String? message;

  const SimpleFinConnectResult({required this.success, this.message});

  factory SimpleFinConnectResult.fromJson(Map<String, dynamic> json) {
    dynamic get(String key) =>
        json[key] ?? json[key[0].toUpperCase() + key.substring(1)];
    return SimpleFinConnectResult(
      success: (get('success') as bool?) ?? false,
      message: get('message')?.toString(),
    );
  }
}

/// Talks to the provider-neutral `/api/aggregators` routes for SimpleFIN: the user makes a setup
/// token at the SimpleFIN bridge and pastes it in.
class SimpleFinService {
  static const providerName = 'SimpleFin';

  /// Whether this server offers SimpleFIN (the setting is on). Any failure counts as "no", so a
  /// problem here only hides the option.
  Future<bool> isAvailable() async {
    try {
      final providers = await apiClient.get<List<dynamic>>('/aggregators');
      return providers.map((e) => e.toString()).contains(providerName);
    } catch (_) {
      return false;
    }
  }

  /// Where the user makes a setup token.
  Future<String> bridgeUrl() async {
    final data = await apiClient.post<Map<String, dynamic>>(
      '/aggregators/$providerName/connect/start',
      data: <String, dynamic>{},
    );
    return (data['value'] ?? data['Value'] ?? '').toString();
  }

  /// Sends the pasted token. A setup token works once, so this must never be retried behind the
  /// user's back (a retry would only be refused and hide the first attempt's real result), and it
  /// uses the raw Dio instance so nothing here is cached or queued for later.
  Future<SimpleFinConnectResult> connect(String setupToken) async {
    final response = await apiClient.dio.post<Map<String, dynamic>>(
      '/aggregators/$providerName/connect/complete',
      data: {'artifact': setupToken.trim()},
      options: Options(
        receiveTimeout: const Duration(minutes: 2),
        extra: const {'noTransientRetry': true},
      ),
    );
    return SimpleFinConnectResult.fromJson(response.data!);
  }

  Future<List<BankConnectionSummary>> connections() async {
    final data = await apiClient.get<List<dynamic>>('/aggregators/connections');
    return data
        .map((e) => BankConnectionSummary.fromJson(e as Map<String, dynamic>))
        .where((c) => c.isSimpleFin)
        .toList();
  }

  Future<void> remove(int connectionId) async {
    await apiClient.delete<dynamic>('/aggregators/connections/$connectionId');
  }
}

/// Global singleton. Not `final` only so tests can inject a fake (there is no DI container).
SimpleFinService simpleFinService = SimpleFinService();
