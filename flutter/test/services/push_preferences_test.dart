import 'package:buoy_ai/services/push_preferences_service.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('PushPreferences.fromJson', () {
    test('parses camelCase keys (backend default)', () {
      final prefs = PushPreferences.fromJson({
        'pushEnabled': false,
        'connectionIssues': false,
        'budgetAlerts': true,
        'billReminders': false,
        'subscriptionDetection': true,
        'transactionReview': false,
        'quietHoursEnabled': true,
        'quietHoursStartMinutes': 1260,
        'quietHoursEndMinutes': 360,
        'registeredDeviceCount': 2,
      });

      expect(prefs.pushEnabled, false);
      expect(prefs.connectionIssues, false);
      expect(prefs.billReminders, false);
      expect(prefs.quietHoursEnabled, true);
      expect(prefs.quietHoursStartMinutes, 1260);
      expect(prefs.quietHoursEndMinutes, 360);
      expect(prefs.registeredDeviceCount, 2);
    });

    test('parses PascalCase keys (transition rule)', () {
      final prefs = PushPreferences.fromJson({
        'PushEnabled': false,
        'BillReminders': false,
        'QuietHoursStartMinutes': 600,
      });

      expect(prefs.pushEnabled, false);
      expect(prefs.billReminders, false);
      expect(prefs.quietHoursStartMinutes, 600);
      // Unspecified keys fall back to defaults.
      expect(prefs.budgetAlerts, true);
    });

    test('empty json yields defaults (everything on, no quiet hours)', () {
      final prefs = PushPreferences.fromJson({});

      expect(prefs.pushEnabled, true);
      expect(prefs.quietHoursEnabled, false);
      expect(prefs.quietHoursStartMinutes, 22 * 60);
      expect(prefs.quietHoursEndMinutes, 7 * 60);
      expect(prefs.registeredDeviceCount, 0);
    });
  });

  test('toJson round-trips through fromJson and carries the device tz offset',
      () {
    const original = PushPreferences(
      pushEnabled: false,
      budgetAlerts: false,
      quietHoursEnabled: true,
      quietHoursStartMinutes: 1300,
      quietHoursEndMinutes: 400,
    );

    final json = original.toJson();
    expect(json['timeZoneOffsetMinutes'],
        DateTime.now().timeZoneOffset.inMinutes);

    final restored = PushPreferences.fromJson(json);
    expect(restored.pushEnabled, false);
    expect(restored.budgetAlerts, false);
    expect(restored.quietHoursEnabled, true);
    expect(restored.quietHoursStartMinutes, 1300);
    expect(restored.quietHoursEndMinutes, 400);
  });

  test('copyWith changes only the named field and preserves device count', () {
    const original = PushPreferences(registeredDeviceCount: 3);

    final updated = original.copyWith(billReminders: false);

    expect(updated.billReminders, false);
    expect(updated.pushEnabled, true);
    expect(updated.registeredDeviceCount, 3);
  });
}
