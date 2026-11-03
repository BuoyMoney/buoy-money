import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/services/mfa_service.dart';

void main() {
  group('MfaStatus email-window fields', () {
    test('parses PascalCase status with a computed grace end', () {
      final end = DateTime.now().add(const Duration(days: 12, hours: 3));
      final s = MfaStatus.fromJson({
        'HasMfaEnabled': true,
        'CurrentMfaType': 'email',
        'HasPasskeyRegistered': false,
        'CanAccessPlaidFeatures': true,
        'MfaGracePeriodEndsAt': end.toIso8601String(),
      });
      expect(s.canAccessPlaid, isTrue);
      expect(s.isOnEmailWindow, isTrue);
      expect(s.daysLeftInWindow, 12);
    });

    test('parses camelCase and treats strong methods as not on the window', () {
      final s = MfaStatus.fromJson({
        'hasMfaEnabled': true,
        'currentMfaType': 'totp',
        'hasPasskeyRegistered': false,
        'canAccessPlaidFeatures': true,
        'mfaGracePeriodEndsAt': null,
      });
      expect(s.canAccessPlaid, isTrue);
      expect(s.isOnEmailWindow, isFalse);
      expect(s.daysLeftInWindow, isNull);
    });

    test('lapsed email window: cannot access Plaid, days clamp to zero', () {
      final s = MfaStatus.fromJson({
        'hasMfaEnabled': true,
        'currentMfaType': 'email',
        'canAccessPlaidFeatures': false,
        'mfaGracePeriodEndsAt':
            DateTime.now().subtract(const Duration(days: 4)).toIso8601String(),
      });
      expect(s.canAccessPlaid, isFalse);
      expect(s.isOnEmailWindow, isFalse);
      expect(s.daysLeftInWindow, 0);
    });

    test('email user who also registered a passkey is not nudged', () {
      final s = MfaStatus.fromJson({
        'hasMfaEnabled': true,
        'currentMfaType': 'email',
        'hasPasskeyRegistered': true,
        'canAccessPlaidFeatures': true,
      });
      expect(s.isOnEmailWindow, isFalse);
    });
  });
}
