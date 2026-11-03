import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/services/user_service.dart';

Map<String, dynamic> _base() => {
      'id': 1,
      'email': 'a@b.com',
      'subscriptionTier': 'Premium',
      'createdAt': '2026-01-01T00:00:00Z',
      'mfaEnabled': false,
      'emailVerified': true,
    };

void main() {
  test('camelCase aiConsentGrantedAt parses and flags consent', () {
    final p = UserProfile.fromJson({..._base(), 'aiConsentGrantedAt': '2026-08-22T10:00:00Z'});
    expect(p.aiConsentGranted, isTrue);
    expect(p.aiConsentGrantedAt, DateTime.parse('2026-08-22T10:00:00Z'));
  });

  test('PascalCase AiConsentGrantedAt parses', () {
    final p = UserProfile.fromJson({..._base(), 'AiConsentGrantedAt': '2026-08-22T10:00:00Z'});
    expect(p.aiConsentGranted, isTrue);
  });

  test('missing or null means not granted', () {
    expect(UserProfile.fromJson(_base()).aiConsentGranted, isFalse);
    expect(UserProfile.fromJson({..._base(), 'aiConsentGrantedAt': null}).aiConsentGranted, isFalse);
  });
}
