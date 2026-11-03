import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/models/auth_models.dart';

Map<String, dynamic> _base() => {
      'id': 1,
      'email': 'a@b.com',
      'firstName': 'A',
      'lastName': 'B',
      'subscriptionTier': 'Premium',
      'createdAt': '2026-01-01T00:00:00Z',
    };

void main() {
  group('User.aiConsentGranted', () {
    test('camelCase grant timestamp parses', () {
      final u = User.fromJson({..._base(), 'aiConsentGrantedAt': '2026-08-22T10:00:00Z'});
      expect(u.aiConsentGranted, isTrue);
    });

    test('PascalCase grant timestamp parses', () {
      final u = User.fromJson({..._base(), 'AiConsentGrantedAt': '2026-08-22T10:00:00Z'});
      expect(u.aiConsentGranted, isTrue);
    });

    test('absent or null is not granted', () {
      expect(User.fromJson(_base()).aiConsentGranted, isFalse);
      expect(User.fromJson({..._base(), 'aiConsentGrantedAt': null}).aiConsentGranted, isFalse);
    });

    test('withdrawn flag parses both casings and survives toJson', () {
      final u = User.fromJson({..._base(), 'AiConsentWithdrawnAt': '2026-08-22T10:00:00Z'});
      expect(u.aiConsentWithdrawn, isTrue);
      expect(User.fromJson(u.toJson()).aiConsentWithdrawn, isTrue);
    });

    test('round-trips through toJson so a cached session keeps the grant', () {
      final u = User.fromJson({..._base(), 'aiConsentGrantedAt': '2026-08-22T10:00:00Z'});
      final again = User.fromJson(u.toJson());
      expect(again.aiConsentGranted, isTrue);
    });
  });
}
