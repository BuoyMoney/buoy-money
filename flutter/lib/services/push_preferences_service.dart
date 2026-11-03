import 'api_client.dart';

/// Push notification preferences (#190) — mirrors the backend's
/// `GET/PUT /notifications/push-preferences` shape. Quiet-hours values are
/// minutes-from-midnight in the user's local time; a start after the end means the
/// window wraps past midnight (the 22:00 → 07:00 default).
class PushPreferences {
  final bool pushEnabled;
  final bool connectionIssues;
  final bool budgetAlerts;
  final bool billReminders;
  final bool subscriptionDetection;
  final bool transactionReview;
  final bool quietHoursEnabled;
  final int quietHoursStartMinutes;
  final int quietHoursEndMinutes;
  final int registeredDeviceCount;

  const PushPreferences({
    this.pushEnabled = true,
    this.connectionIssues = true,
    this.budgetAlerts = true,
    this.billReminders = true,
    this.subscriptionDetection = true,
    this.transactionReview = true,
    this.quietHoursEnabled = false,
    this.quietHoursStartMinutes = 22 * 60,
    this.quietHoursEndMinutes = 7 * 60,
    this.registeredDeviceCount = 0,
  });

  factory PushPreferences.fromJson(Map<String, dynamic> json) {
    // Backend serializes camelCase, but cover PascalCase too (transition rule).
    T pick<T>(String key, T fallback) =>
        (json[key] ?? json[key[0].toUpperCase() + key.substring(1)]) as T? ??
        fallback;

    return PushPreferences(
      pushEnabled: pick('pushEnabled', true),
      connectionIssues: pick('connectionIssues', true),
      budgetAlerts: pick('budgetAlerts', true),
      billReminders: pick('billReminders', true),
      subscriptionDetection: pick('subscriptionDetection', true),
      transactionReview: pick('transactionReview', true),
      quietHoursEnabled: pick('quietHoursEnabled', false),
      quietHoursStartMinutes: pick('quietHoursStartMinutes', 22 * 60),
      quietHoursEndMinutes: pick('quietHoursEndMinutes', 7 * 60),
      registeredDeviceCount: pick('registeredDeviceCount', 0),
    );
  }

  Map<String, dynamic> toJson() => {
        'pushEnabled': pushEnabled,
        'connectionIssues': connectionIssues,
        'budgetAlerts': budgetAlerts,
        'billReminders': billReminders,
        'subscriptionDetection': subscriptionDetection,
        'transactionReview': transactionReview,
        'quietHoursEnabled': quietHoursEnabled,
        'quietHoursStartMinutes': quietHoursStartMinutes,
        'quietHoursEndMinutes': quietHoursEndMinutes,
        // Sent so quiet hours track the device's clock (DST self-heals on next save).
        'timeZoneOffsetMinutes': DateTime.now().timeZoneOffset.inMinutes,
      };

  PushPreferences copyWith({
    bool? pushEnabled,
    bool? connectionIssues,
    bool? budgetAlerts,
    bool? billReminders,
    bool? subscriptionDetection,
    bool? transactionReview,
    bool? quietHoursEnabled,
    int? quietHoursStartMinutes,
    int? quietHoursEndMinutes,
  }) {
    return PushPreferences(
      pushEnabled: pushEnabled ?? this.pushEnabled,
      connectionIssues: connectionIssues ?? this.connectionIssues,
      budgetAlerts: budgetAlerts ?? this.budgetAlerts,
      billReminders: billReminders ?? this.billReminders,
      subscriptionDetection: subscriptionDetection ?? this.subscriptionDetection,
      transactionReview: transactionReview ?? this.transactionReview,
      quietHoursEnabled: quietHoursEnabled ?? this.quietHoursEnabled,
      quietHoursStartMinutes:
          quietHoursStartMinutes ?? this.quietHoursStartMinutes,
      quietHoursEndMinutes: quietHoursEndMinutes ?? this.quietHoursEndMinutes,
      registeredDeviceCount: registeredDeviceCount,
    );
  }
}

class PushPreferencesService {
  Future<PushPreferences> getPreferences() async {
    try {
      final response = await apiClient
          .get<Map<String, dynamic>>('/notifications/push-preferences');
      return PushPreferences.fromJson(response);
    } catch (_) {
      return const PushPreferences();
    }
  }

  Future<PushPreferences> updatePreferences(PushPreferences prefs) async {
    final response = await apiClient.put<Map<String, dynamic>>(
      '/notifications/push-preferences',
      data: prefs.toJson(),
    );
    return PushPreferences.fromJson(response);
  }
}

/// Singleton instance
final pushPreferencesService = PushPreferencesService();
