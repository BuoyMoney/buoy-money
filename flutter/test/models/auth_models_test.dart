import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/auth_models.dart';

void main() {
  group('User', () {
    test('fromJson parses user data correctly', () {
      final json = {
        'id': '123',
        'email': 'test@example.com',
        'firstName': 'John',
        'lastName': 'Doe',
        'subscriptionTier': 1, // Standard (backend enum: 1=Standard, 2=Premium)
        'createdAt': '2024-01-15T10:30:00Z',
        'lastLoginAt': '2024-03-20T14:00:00Z',
        'mfaEnabled': true,
        'familyId': '456',
        'familyRole': 'Owner',
        'hasCompletedOnboarding': true,
      };

      final user = User.fromJson(json);

      expect(user.id, '123');
      expect(user.email, 'test@example.com');
      expect(user.firstName, 'John');
      expect(user.lastName, 'Doe');
      expect(user.fullName, 'John Doe');
      expect(user.subscriptionTier, 'Standard');
      expect(user.mfaEnabled, true);
      expect(user.familyId, '456');
      expect(user.hasCompletedOnboarding, true);
    });

    test('handles PascalCase keys from backend', () {
      final json = {
        'Id': 999,
        'Email': 'pascal@example.com',
        'FirstName': 'Jane',
        'LastName': 'Smith',
        'SubscriptionTier': 'Premium',
        'CreatedAt': '2024-01-01T00:00:00Z',
        'MfaEnabled': false,
        'HasCompletedOnboarding': false,
      };

      final user = User.fromJson(json);

      expect(user.id, '999');
      expect(user.email, 'pascal@example.com');
      expect(user.firstName, 'Jane');
      expect(user.lastName, 'Smith');
      expect(user.subscriptionTier, 'Premium');
      expect(user.mfaEnabled, false);
    });

    test('maps subscription tier int to string', () {
      // Backend enum: 1=Standard, 2=Premium, 3=PremiumInherited, 99=Admin
      expect(User.fromJson({'id': '1', 'email': '', 'firstName': '', 'lastName': '', 'subscriptionTier': 0, 'createdAt': '2024-01-01'}).subscriptionTier, 'Trial');
      expect(User.fromJson({'id': '1', 'email': '', 'firstName': '', 'lastName': '', 'subscriptionTier': 1, 'createdAt': '2024-01-01'}).subscriptionTier, 'Standard');
      expect(User.fromJson({'id': '1', 'email': '', 'firstName': '', 'lastName': '', 'subscriptionTier': 2, 'createdAt': '2024-01-01'}).subscriptionTier, 'Premium');
      expect(User.fromJson({'id': '1', 'email': '', 'firstName': '', 'lastName': '', 'subscriptionTier': 3, 'createdAt': '2024-01-01'}).subscriptionTier, 'PremiumInherited');
      expect(User.fromJson({'id': '1', 'email': '', 'firstName': '', 'lastName': '', 'subscriptionTier': 99, 'createdAt': '2024-01-01'}).subscriptionTier, 'Admin');
    });

    test('requiresPaidPlan parses from camelCase and PascalCase, defaults false', () {
      const base = {
        'id': '1',
        'email': '',
        'firstName': '',
        'lastName': '',
        'subscriptionTier': 1,
        'createdAt': '2024-01-01',
      };
      expect(User.fromJson(base).requiresPaidPlan, false);
      expect(
          User.fromJson({...base, 'requiresPaidPlan': true}).requiresPaidPlan,
          true);
      expect(
          User.fromJson({...base, 'RequiresPaidPlan': true}).requiresPaidPlan,
          true);
    });

    test('isEmailVerified parses from camelCase and PascalCase, defaults false', () {
      const base = {
        'id': '1',
        'email': '',
        'firstName': '',
        'lastName': '',
        'subscriptionTier': 1,
        'createdAt': '2024-01-01',
      };
      expect(User.fromJson(base).isEmailVerified, false);
      expect(User.fromJson({...base, 'isEmailVerified': true}).isEmailVerified,
          true);
      expect(User.fromJson({...base, 'IsEmailVerified': true}).isEmailVerified,
          true);
    });

    test('fullName combines first and last name', () {
      final user = User(
        id: '1',
        email: 'test@example.com',
        firstName: 'Alice',
        lastName: 'Wonder',
        subscriptionTier: 'Trial',
        createdAt: DateTime.now(),
      );

      expect(user.fullName, 'Alice Wonder');
    });

    test('toJson serializes correctly', () {
      final createdAt = DateTime(2024, 1, 15, 10, 30);
      final user = User(
        id: '1',
        email: 'test@example.com',
        firstName: 'John',
        lastName: 'Doe',
        subscriptionTier: 'Premium',
        createdAt: createdAt,
        mfaEnabled: true,
      );

      final json = user.toJson();

      expect(json['id'], '1');
      expect(json['email'], 'test@example.com');
      expect(json['firstName'], 'John');
      expect(json['lastName'], 'Doe');
      expect(json['subscriptionTier'], 'Premium');
      expect(json['mfaEnabled'], true);
    });
  });

  group('LoginRequest', () {
    test('toJson includes all fields', () {
      final request = LoginRequest(
        email: 'user@example.com',
        password: 'secret123',
        rememberDevice: true,
      );

      final json = request.toJson();

      expect(json['email'], 'user@example.com');
      expect(json['password'], 'secret123');
      expect(json['rememberDevice'], true);
    });
  });

  group('LoginResponse', () {
    test('fromJson parses token and user', () {
      final json = {
        'token': 'jwt.token.here',
        'user': {
          'id': '1',
          'email': 'test@example.com',
          'firstName': 'Test',
          'lastName': 'User',
          'subscriptionTier': 'Trial',
          'createdAt': '2024-01-01T00:00:00Z',
        },
      };

      final response = LoginResponse.fromJson(json);

      expect(response.token, 'jwt.token.here');
      expect(response.user.email, 'test@example.com');
    });

    // isNewUser is what tells an OAuth sign-up apart from an OAuth sign-in, so a
    // parsing slip here silently zeroes the signup funnel rather than erroring.
    test('isNewUser parses from either casing and defaults to false', () {
      Map<String, dynamic> body(Map<String, dynamic> extra) => {
            'token': 't',
            'user': {
              'id': '1',
              'email': 'test@example.com',
              'firstName': 'Test',
              'lastName': 'User',
              'subscriptionTier': 'Trial',
              'createdAt': '2024-01-01T00:00:00Z',
            },
            ...extra,
          };

      expect(LoginResponse.fromJson(body({'isNewUser': true})).isNewUser, isTrue);
      expect(LoginResponse.fromJson(body({'IsNewUser': true})).isNewUser, isTrue);
      expect(LoginResponse.fromJson(body({'isNewUser': false})).isNewUser, isFalse);
      // Absent means a returning user — never count it as a signup.
      expect(LoginResponse.fromJson(body({})).isNewUser, isFalse);
    });
  });

  group('EnhancedLoginResponse', () {
    test('parses successful login without MFA', () {
      final json = {
        'requiresMfa': false,
        'token': 'jwt.token.here',
        'user': {
          'id': '1',
          'email': 'test@example.com',
          'firstName': 'Test',
          'lastName': 'User',
          'subscriptionTier': 'Trial',
          'createdAt': '2024-01-01T00:00:00Z',
        },
      };

      final response = EnhancedLoginResponse.fromJson(json);

      expect(response.requiresMfa, false);
      expect(response.token, 'jwt.token.here');
      expect(response.user, isNotNull);
    });

    test('parses MFA required response', () {
      final json = {
        'requiresMfa': true,
        'mfaToken': 'mfa.token.here',
        'mfaReasons': ['New device detected'],
        'preferredMfaMethod': 'email',
        'availableMfaMethods': ['email', 'authenticator'],
      };

      final response = EnhancedLoginResponse.fromJson(json);

      expect(response.requiresMfa, true);
      expect(response.mfaToken, 'mfa.token.here');
      expect(response.mfaReasons, contains('New device detected'));
      expect(response.availableMfaMethods, contains('email'));
    });
  });

  group('RegisterRequest', () {
    test('toJson includes all registration fields', () {
      final request = RegisterRequest(
        email: 'new@example.com',
        password: 'Password123!',
        firstName: 'New',
        lastName: 'User',
      );

      final json = request.toJson();

      expect(json['email'], 'new@example.com');
      expect(json['password'], 'Password123!');
      expect(json['firstName'], 'New');
      expect(json['lastName'], 'User');
    });
  });

  group('GoogleLoginRequest', () {
    test('toJson defaults isRegistration to false (login intent)', () {
      final request = GoogleLoginRequest(idToken: 'id-token');

      final json = request.toJson();

      expect(json['idToken'], 'id-token');
      expect(json['isRegistration'], false);
    });

    test('toJson carries isRegistration true for the sign-up flow', () {
      final request =
          GoogleLoginRequest(idToken: 'id-token', isRegistration: true);

      final json = request.toJson();

      expect(json['idToken'], 'id-token');
      expect(json['isRegistration'], true);
    });
  });

  group('User avatar fields', () {
    Map<String, dynamic> base(Map<String, dynamic> extra) => {
          'id': 7,
          'email': 'jane@example.com',
          'firstName': 'Jane',
          'lastName': 'Doe',
          'subscriptionTier': 'Premium',
          'createdAt': '2026-01-01T00:00:00Z',
          ...extra,
        };

    test('fromJson reads the OAuth photo in camelCase', () {
      final user = User.fromJson(base({'profilePictureUrl': 'https://cdn/p.png'}));

      expect(user.profilePictureUrl, 'https://cdn/p.png');
      expect(user.effectiveAvatar, 'https://cdn/p.png');
    });

    test('fromJson reads the OAuth photo in PascalCase', () {
      final user = User.fromJson(base({'ProfilePictureUrl': 'https://cdn/p.png'}));

      expect(user.profilePictureUrl, 'https://cdn/p.png');
    });

    test('an uploaded avatar beats the OAuth photo and is cache-busted', () {
      final user = User.fromJson(base({
        'profilePictureUrl': 'https://cdn/google.png',
        'avatarUpdatedAt': '2026-02-03T04:05:06Z',
      }));

      // Uploaded picture wins, served from our own API rather than the provider.
      expect(user.effectiveAvatar, isNot(contains('cdn/google.png')));
      expect(user.effectiveAvatar, contains('/user/avatar/7'));
      expect(user.effectiveAvatar, contains('?v='));
    });

    test('no photo yields null so the UI falls back to initials', () {
      expect(User.fromJson(base({})).effectiveAvatar, isNull);
      // Apple sends no photo at all, and an empty string must not render as a
      // broken image.
      expect(User.fromJson(base({'profilePictureUrl': ''})).effectiveAvatar, isNull);
    });

    test('initials come from the name, falling back to the email', () {
      expect(User.fromJson(base({})).initials, 'JD');
      expect(
        User.fromJson(base({'firstName': '', 'lastName': ''})).initials,
        'J',
      );
    });
  });
}
