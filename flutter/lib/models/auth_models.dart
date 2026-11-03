import '../config/app_config.dart';
import '../utils/formatters.dart';

/// User model - matches backend User entity
class User {
  final String id;
  final String email;
  final String firstName;
  final String lastName;
  final String subscriptionTier;
  final DateTime createdAt;
  final DateTime? lastLoginAt;
  final bool mfaEnabled;
  final String? familyId;
  final String? familyRole;
  final bool hasCompletedOnboarding;

  /// Primary display currency (ISO-4217, backend Users.Currency). Null when an
  /// older backend response omits it; treated as USD.
  final String? currency;

  /// Profile photo from the OAuth provider. Google supplies this; Apple never
  /// does (its ID token carries only email/email_verified), so Apple accounts
  /// fall back to initials by design.
  final String? profilePictureUrl;

  /// Non-null when the user uploaded a custom avatar, served from our own API.
  /// Doubles as a cache-busting version stamp.
  final DateTime? avatarUpdatedAt;

  /// True when the account must choose and pay for a plan before using the app
  /// (a returning user whose email already used its free trial). Drives the paywall.
  final bool requiresPaidPlan;

  /// True once the user's email has been verified. Required before protected
  /// operations such as connecting a bank account.
  final bool isEmailVerified;

  /// When the user granted permission to send their data to third-party AI
  /// (Google Gemini). Null = not granted. App Store 5.1.2(i): every AI surface
  /// checks this (and the server enforces it) before calling the model.
  final DateTime? aiConsentGrantedAt;
  /// Explicit opt-out (Settings > AI Assistant off). Blocks AI on every platform.
  final DateTime? aiConsentWithdrawnAt;
  bool get aiConsentGranted => aiConsentGrantedAt != null;
  bool get aiConsentWithdrawn => aiConsentWithdrawnAt != null;

  User({
    required this.id,
    required this.email,
    required this.firstName,
    required this.lastName,
    required this.subscriptionTier,
    required this.createdAt,
    this.lastLoginAt,
    this.mfaEnabled = false,
    this.familyId,
    this.familyRole,
    this.hasCompletedOnboarding = false,
    this.currency,
    this.requiresPaidPlan = false,
    this.isEmailVerified = false,
    this.profilePictureUrl,
    this.avatarUpdatedAt,
    this.aiConsentGrantedAt,
    this.aiConsentWithdrawnAt,
  });

  String get fullName => '$firstName $lastName';

  /// Effective avatar URL, or null to fall back to initials. Same precedence as
  /// `UserProfile.effectiveAvatar`: an uploaded custom picture wins (served from
  /// our API, cache-busted by its timestamp), then the OAuth provider's photo.
  String? get effectiveAvatar {
    if (avatarUpdatedAt != null) {
      return '${AppConfig.baseUrl}/user/avatar/$id?v=${avatarUpdatedAt!.millisecondsSinceEpoch}';
    }
    final url = profilePictureUrl;
    return (url == null || url.isEmpty) ? null : url;
  }

  /// Initials for the fallback, e.g. "JB". Falls back to the email's first
  /// character when no name is set.
  String get initials {
    final f = firstName.trim();
    final l = lastName.trim();
    if (f.isEmpty && l.isEmpty) {
      return email.isNotEmpty ? email[0].toUpperCase() : '?';
    }
    final buffer = StringBuffer();
    if (f.isNotEmpty) buffer.write(f[0]);
    if (l.isNotEmpty) buffer.write(l[0]);
    return buffer.toString().toUpperCase();
  }
  
  /// Helper to safely convert any value to String
  static String _toString(dynamic value, [String defaultValue = '']) {
    if (value == null) return defaultValue;
    return value.toString();
  }
  
  /// Helper to safely parse DateTime
  static DateTime _parseDateTime(dynamic value) {
    if (value == null) return DateTime.now();
    if (value is DateTime) return value;
    return DateTime.tryParse(value.toString()) ?? DateTime.now();
  }
  
  /// Helper to safely parse nullable DateTime
  static DateTime? _parseNullableDateTime(dynamic value) {
    if (value == null) return null;
    if (value is DateTime) return value;
    return DateTime.tryParse(value.toString());
  }
  
  /// Helper to safely parse bool
  static bool _parseBool(dynamic value, [bool defaultValue = false]) {
    if (value == null) return defaultValue;
    if (value is bool) return value;
    if (value is int) return value != 0;
    if (value is String) return value.toLowerCase() == 'true';
    return defaultValue;
  }
  
  /// Map subscription tier int to string
  static String _mapSubscriptionTier(dynamic value) =>
      mapSubscriptionTier(value, fallback: 'Trial');
  
  factory User.fromJson(Map<String, dynamic> json) {
    return User(
      // Handle both camelCase and PascalCase from backend, with type coercion
      id: _toString(json['id'] ?? json['Id']),
      email: _toString(json['email'] ?? json['Email']),
      firstName: _toString(json['firstName'] ?? json['FirstName']),
      lastName: _toString(json['lastName'] ?? json['LastName']),
      subscriptionTier: _mapSubscriptionTier(json['subscriptionTier'] ?? json['SubscriptionTier']),
      createdAt: _parseDateTime(json['createdAt'] ?? json['CreatedAt']),
      lastLoginAt: _parseNullableDateTime(json['lastLoginAt'] ?? json['LastLoginAt']),
      mfaEnabled: _parseBool(json['mfaEnabled'] ?? json['MfaEnabled']),
      familyId: json['familyId']?.toString() ?? json['FamilyId']?.toString(),
      familyRole: json['familyRole']?.toString() ?? json['FamilyRole']?.toString(),
      hasCompletedOnboarding: _parseBool(json['hasCompletedOnboarding'] ?? json['HasCompletedOnboarding']),
      currency: (json['currency'] ?? json['Currency'])?.toString(),
      requiresPaidPlan: _parseBool(json['requiresPaidPlan'] ?? json['RequiresPaidPlan']),
      isEmailVerified: _parseBool(json['isEmailVerified'] ?? json['IsEmailVerified']),
      profilePictureUrl:
          (json['profilePictureUrl'] ?? json['ProfilePictureUrl'])?.toString(),
      avatarUpdatedAt: _parseNullableDateTime(
          json['avatarUpdatedAt'] ?? json['AvatarUpdatedAt']),
      aiConsentGrantedAt: _parseNullableDateTime(
          json['aiConsentGrantedAt'] ?? json['AiConsentGrantedAt']),
      aiConsentWithdrawnAt: _parseNullableDateTime(
          json['aiConsentWithdrawnAt'] ?? json['AiConsentWithdrawnAt']),
    );
  }
  
  Map<String, dynamic> toJson() {
    return {
      'id': id,
      'email': email,
      'firstName': firstName,
      'lastName': lastName,
      'subscriptionTier': subscriptionTier,
      'createdAt': createdAt.toIso8601String(),
      'lastLoginAt': lastLoginAt?.toIso8601String(),
      'mfaEnabled': mfaEnabled,
      'familyId': familyId,
      'familyRole': familyRole,
      'hasCompletedOnboarding': hasCompletedOnboarding,
      'currency': currency,
      'requiresPaidPlan': requiresPaidPlan,
      'isEmailVerified': isEmailVerified,
      'aiConsentGrantedAt': aiConsentGrantedAt?.toIso8601String(),
      'aiConsentWithdrawnAt': aiConsentWithdrawnAt?.toIso8601String(),
      'profilePictureUrl': profilePictureUrl,
      'avatarUpdatedAt': avatarUpdatedAt?.toIso8601String(),
    };
  }
}

/// Login request
class LoginRequest {
  final String email;
  final String password;
  final String? deviceFingerprint;
  final bool rememberDevice;

  LoginRequest({
    required this.email,
    required this.password,
    this.deviceFingerprint,
    this.rememberDevice = false,
  });

  Map<String, dynamic> toJson() {
    return {
      'email': email,
      'password': password,
      if (deviceFingerprint != null) 'deviceFingerprint': deviceFingerprint,
      'rememberDevice': rememberDevice,
    };
  }
}

/// A pending family invitation returned with the auth response
class PendingFamilyInvitation {
  final String invitationToken;
  final String familyName;
  final String invitedByName;
  final String invitedRole;
  final DateTime expiresAt;

  PendingFamilyInvitation({
    required this.invitationToken,
    required this.familyName,
    required this.invitedByName,
    required this.invitedRole,
    required this.expiresAt,
  });

  factory PendingFamilyInvitation.fromJson(Map<String, dynamic> json) {
    return PendingFamilyInvitation(
      invitationToken: (json['invitationToken'] ?? json['InvitationToken'] ?? '').toString(),
      familyName: (json['familyName'] ?? json['FamilyName'] ?? 'Family').toString(),
      invitedByName: (json['invitedByName'] ?? json['InvitedByName'] ?? '').toString(),
      invitedRole: (json['invitedRole'] ?? json['InvitedRole'] ?? 'Member').toString(),
      expiresAt: DateTime.tryParse(
        (json['expiresAt'] ?? json['ExpiresAt'] ?? '').toString(),
      ) ?? DateTime.now().add(const Duration(days: 7)),
    );
  }
}

/// Standard login response (no MFA required)
class LoginResponse {
  final String token;
  final User user;
  final String expiresAt;

  /// Rotating refresh token (native clients only; web receives an HttpOnly cookie).
  final String? refreshToken;
  final List<PendingFamilyInvitation> pendingFamilyInvitations;

  /// True only when this request created the account. OAuth sign-up and sign-in are the
  /// same call, so this is the only way the client can tell a new user from a returning
  /// one — it is what makes an OAuth signup countable in the activation funnel.
  final bool isNewUser;

  LoginResponse({
    required this.token,
    required this.user,
    required this.expiresAt,
    this.refreshToken,
    this.pendingFamilyInvitations = const [],
    this.isNewUser = false,
  });

  factory LoginResponse.fromJson(Map<String, dynamic> json) {
    final invitationsJson = json['pendingFamilyInvitations']
        ?? json['PendingFamilyInvitations']
        ?? [];
    return LoginResponse(
      token: (json['token'] ?? json['Token'] ?? '').toString(),
      user: User.fromJson((json['user'] ?? json['User'] ?? <String, dynamic>{}) as Map<String, dynamic>),
      expiresAt: (json['expiresAt'] ?? json['ExpiresAt'] ?? '').toString(),
      refreshToken: (json['refreshToken'] ?? json['RefreshToken'])?.toString(),
      isNewUser: (json['isNewUser'] ?? json['IsNewUser']) == true,
      pendingFamilyInvitations: (invitationsJson as List)
          .map((j) => PendingFamilyInvitation.fromJson(j as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// Enhanced login response - may include MFA challenge
class EnhancedLoginResponse {
  final bool requiresMfa;
  final String? mfaToken;
  final List<String>? mfaReasons;
  final String? preferredMfaMethod;
  final List<String>? availableMfaMethods;

  // If MFA not required, these are populated
  final String? token;
  final User? user;
  final String? expiresAt;
  final String? refreshToken;
  final List<PendingFamilyInvitation> pendingFamilyInvitations;

  EnhancedLoginResponse({
    required this.requiresMfa,
    this.mfaToken,
    this.mfaReasons,
    this.preferredMfaMethod,
    this.availableMfaMethods,
    this.token,
    this.user,
    this.expiresAt,
    this.refreshToken,
    this.pendingFamilyInvitations = const [],
  });

  factory EnhancedLoginResponse.fromJson(Map<String, dynamic> json) {
    final requiresMfa = json['requiresMfa'] ?? json['RequiresMfa'] ?? false;
    final invitationsJson = json['pendingFamilyInvitations']
        ?? json['PendingFamilyInvitations']
        ?? [];

    return EnhancedLoginResponse(
      requiresMfa: requiresMfa == true,
      mfaToken: (json['mfaToken'] ?? json['MfaToken'])?.toString(),
      mfaReasons: ((json['mfaReasons'] ?? json['MfaReasons']) as List?)?.cast<String>(),
      preferredMfaMethod: (json['preferredMfaMethod'] ?? json['PreferredMfaMethod'])?.toString(),
      availableMfaMethods: ((json['availableMfaMethods'] ?? json['AvailableMfaMethods']) as List?)?.cast<String>(),
      token: (json['token'] ?? json['Token'])?.toString(),
      user: json['user'] != null || json['User'] != null
          ? User.fromJson((json['user'] ?? json['User']) as Map<String, dynamic>)
          : null,
      expiresAt: (json['expiresAt'] ?? json['ExpiresAt'])?.toString(),
      refreshToken: (json['refreshToken'] ?? json['RefreshToken'])?.toString(),
      pendingFamilyInvitations: (invitationsJson as List)
          .map((j) => PendingFamilyInvitation.fromJson(j as Map<String, dynamic>))
          .toList(),
    );
  }
}

/// MFA verification request
class VerifyMfaRequest {
  final String mfaToken;
  final String mfaCode;
  final String? deviceFingerprint;
  final bool rememberDevice;
  
  VerifyMfaRequest({
    required this.mfaToken,
    required this.mfaCode,
    this.deviceFingerprint,
    this.rememberDevice = false,
  });
  
  Map<String, dynamic> toJson() {
    return {
      'mfaToken': mfaToken,
      'mfaCode': mfaCode,
      if (deviceFingerprint != null) 'deviceFingerprint': deviceFingerprint,
      'rememberDevice': rememberDevice,
    };
  }
}

/// Register request
class RegisterRequest {
  final String email;
  final String password;
  final String firstName;
  final String lastName;

  /// Self-hosted servers only: the one-time code an admin gave this person.
  final String? inviteCode;

  /// `cloud` when the sign-up started in the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only,
  /// with no hosted trial. Null for an ordinary sign-up. Ignored by the server for an existing account.
  final String? signupContext;

  RegisterRequest({
    required this.email,
    required this.password,
    required this.firstName,
    required this.lastName,
    this.inviteCode,
    this.signupContext,
  });
  
  Map<String, dynamic> toJson() {
    return {
      'email': email,
      'password': password,
      'firstName': firstName,
      'lastName': lastName,
      if (inviteCode != null && inviteCode!.isNotEmpty) 'inviteCode': inviteCode,
      if (signupContext != null) 'signupContext': signupContext,
    };
  }
}

/// Thrown when a Google sign-in from the login flow finds no matching account.
/// The login page catches this to route the user to registration.
class GoogleAccountNotFoundException implements Exception {
  const GoogleAccountNotFoundException();
}

/// Google login request
class GoogleLoginRequest {
  final String idToken;

  /// True when the request originates from the sign-up flow. When false (login)
  /// and no account exists, the backend returns 404 so the client can route to
  /// registration instead of silently creating an account.
  final bool isRegistration;

  /// `cloud` when the sign-up started in the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only,
  /// with no hosted trial. Null for an ordinary sign-up. Ignored by the server for an existing account.
  final String? signupContext;

  GoogleLoginRequest({
    required this.idToken,
    this.isRegistration = false,
    this.signupContext,
  });

  Map<String, dynamic> toJson() {
    return {
      'idToken': idToken,
      'isRegistration': isRegistration,
      if (signupContext != null) 'signupContext': signupContext,
    };
  }
}

/// Apple login request
class AppleLoginRequest {
  final String identityToken;
  final String authorizationCode;
  final String nonce;
  final String? email;
  final String? firstName;
  final String? lastName;
  final String userIdentifier;

  /// `cloud` when the sign-up started in the Cloud portal (buoy-ai.com/cloud/): the new account is Cloud-only,
  /// with no hosted trial. Null for an ordinary sign-up. Ignored by the server for an existing account.
  final String? signupContext;

  AppleLoginRequest({
    required this.identityToken,
    required this.authorizationCode,
    required this.nonce,
    this.email,
    this.firstName,
    this.lastName,
    required this.userIdentifier,
    this.signupContext,
  });

  /// The same request with [context] as its sign-up context.
  AppleLoginRequest withSignupContext(String? context) => AppleLoginRequest(
        identityToken: identityToken,
        authorizationCode: authorizationCode,
        nonce: nonce,
        email: email,
        firstName: firstName,
        lastName: lastName,
        userIdentifier: userIdentifier,
        signupContext: context,
      );
  
  Map<String, dynamic> toJson() {
    return {
      'identityToken': identityToken,
      'authorizationCode': authorizationCode,
      'nonce': nonce,
      if (email != null) 'email': email,
      if (firstName != null) 'firstName': firstName,
      if (lastName != null) 'lastName': lastName,
      'userIdentifier': userIdentifier,
      if (signupContext != null) 'signupContext': signupContext,
    };
  }
}
