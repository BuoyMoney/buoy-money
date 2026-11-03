import 'dart:async';
import 'package:dio/dio.dart';
import '../config/app_config.dart';
import '../offline/sync_service.dart';
import '../utils/currency.dart';
import '../utils/formatters.dart';
import 'api_client.dart';

/// User profile model — mirrors backend UserProfileDto
class UserProfile {
  final int id;
  final String email;
  final String? firstName;
  final String? lastName;
  final String? currency;

  /// First day of the budgeting week (0=Sunday..6=Saturday). Null = legacy Sunday.
  final int? weekStartDay;

  final String? profilePictureUrl;
  final bool isGoogleUser;
  final bool isAppleUser;
  /// True when the account has a local BuoyAI password. False for OAuth-only
  /// (Google/Apple) accounts — those can't use password-replay features like
  /// biometric "quick login".
  final bool hasPassword;
  final String subscriptionTier;
  final DateTime? trialEndsAt;
  final DateTime createdAt;
  final bool mfaEnabled;
  final bool emailVerified;
  final String? avatarUrl;
  /// Non-null when the user has uploaded a custom avatar; doubles as a cache-bust
  /// version for the `/user/avatar/{id}` image URL.
  final DateTime? avatarUpdatedAt;
  final int? familyId;
  final String? familyRole;
  /// When the user granted permission to send their data to third-party AI
  /// (Google Gemini). Null = not granted. App Store 5.1.2(i).
  final DateTime? aiConsentGrantedAt;
  final DateTime? aiConsentWithdrawnAt;
  bool get aiConsentGranted => aiConsentGrantedAt != null;
  bool get aiConsentWithdrawn => aiConsentWithdrawnAt != null;

  UserProfile({
    required this.id,
    required this.email,
    this.firstName,
    this.lastName,
    this.currency,
    this.weekStartDay,
    this.profilePictureUrl,
    this.isGoogleUser = false,
    this.isAppleUser = false,
    this.hasPassword = true,
    required this.subscriptionTier,
    this.trialEndsAt,
    required this.createdAt,
    required this.mfaEnabled,
    required this.emailVerified,
    this.avatarUrl,
    this.avatarUpdatedAt,
    this.familyId,
    this.familyRole,
    this.aiConsentGrantedAt,
    this.aiConsentWithdrawnAt,
  });

  String get displayName {
    // Trim + treat empty strings as absent — Apple/social sign-ins send "" (not null),
    // which the old `?? email` fallback silently skipped.
    final f = firstName?.trim() ?? '';
    final l = lastName?.trim() ?? '';
    if (f.isNotEmpty && l.isNotEmpty) return '$f $l';
    if (f.isNotEmpty) return f;
    return email.split('@').first;
  }

  String get initials {
    // Robust against empty/whitespace and single-character names — the old code
    // did `firstName![0]` / `substring(0, 2)`, which throws a RangeError on "" or
    // a one-letter name.
    final f = firstName?.trim() ?? '';
    final l = lastName?.trim() ?? '';
    if (f.isNotEmpty && l.isNotEmpty) return '${f[0]}${l[0]}'.toUpperCase();
    if (f.isNotEmpty) return f.substring(0, f.length >= 2 ? 2 : 1).toUpperCase();
    final e = email.trim();
    if (e.isNotEmpty) return e.substring(0, e.length >= 2 ? 2 : 1).toUpperCase();
    return '?';
  }

  bool get isPremium => subscriptionTier == 'Premium' || subscriptionTier == 'PremiumInherited' || isTrial;
  bool get isTrial => subscriptionTier == 'Trial';
  bool get isAdmin => subscriptionTier == 'Admin';

  /// Effective avatar URL: an uploaded custom picture wins (served from our API,
  /// cache-busted by its updated timestamp), then a Google profile pic, else null
  /// (the UI falls back to initials).
  String? get effectiveAvatar {
    if (avatarUpdatedAt != null) {
      return '${AppConfig.baseUrl}/user/avatar/$id?v=${avatarUpdatedAt!.millisecondsSinceEpoch}';
    }
    return profilePictureUrl ?? avatarUrl;
  }

  static String _mapSubscriptionTier(dynamic value) =>
      mapSubscriptionTier(value, fallback: 'Free');

  factory UserProfile.fromJson(Map<String, dynamic> json) {
    return UserProfile(
      id: int.tryParse((json['id'] ?? json['Id'])?.toString() ?? '') ?? 0,
      email: (json['email'] ?? json['Email'] ?? '').toString(),
      firstName: (json['firstName'] ?? json['FirstName'])?.toString(),
      lastName: (json['lastName'] ?? json['LastName'])?.toString(),
      currency: (json['currency'] ?? json['Currency'])?.toString(),
      weekStartDay: int.tryParse(
          (json['weekStartDay'] ?? json['WeekStartDay'])?.toString() ?? ''),
      profilePictureUrl: (json['profilePictureUrl'] ?? json['ProfilePictureUrl'])?.toString(),
      isGoogleUser: (json['isGoogleUser'] ?? json['IsGoogleUser']) as bool? ?? false,
      isAppleUser: (json['isAppleUser'] ?? json['IsAppleUser']) as bool? ?? false,
      // Default true when the backend omits it (older responses) so email/password
      // accounts aren't wrongly stripped of quick login; OAuth-only accounts send false.
      hasPassword: (json['hasPassword'] ?? json['HasPassword']) as bool? ?? true,
      subscriptionTier: _mapSubscriptionTier(json['subscriptionTier'] ?? json['SubscriptionTier']),
      trialEndsAt: json['trialEndsAt'] != null || json['TrialEndsAt'] != null
          ? DateTime.tryParse((json['trialEndsAt'] ?? json['TrialEndsAt']).toString())
          : null,
      createdAt: json['createdAt'] != null || json['CreatedAt'] != null
          ? DateTime.parse((json['createdAt'] ?? json['CreatedAt']).toString())
          : DateTime.now(),
      mfaEnabled: (json['mfaEnabled'] ?? json['MfaEnabled']) as bool? ?? false,
      emailVerified: (json['emailVerified'] ?? json['EmailVerified']) as bool? ?? false,
      avatarUrl: (json['avatarUrl'] ?? json['AvatarUrl'])?.toString(),
      avatarUpdatedAt: (json['avatarUpdatedAt'] ?? json['AvatarUpdatedAt']) != null
          ? DateTime.tryParse((json['avatarUpdatedAt'] ?? json['AvatarUpdatedAt']).toString())
          : null,
      familyId: int.tryParse((json['familyId'] ?? json['FamilyId'])?.toString() ?? ''),
      familyRole: (json['familyRole'] ?? json['FamilyRole'])?.toString(),
      aiConsentGrantedAt: DateTime.tryParse(
          (json['aiConsentGrantedAt'] ?? json['AiConsentGrantedAt'] ?? '').toString()),
      aiConsentWithdrawnAt: DateTime.tryParse(
          (json['aiConsentWithdrawnAt'] ?? json['AiConsentWithdrawnAt'] ?? '').toString()),
    );
  }
}

/// Update profile request
class UpdateProfileRequest {
  final String? firstName;
  final String? lastName;

  /// ISO-4217 primary display currency. Sending it marks the choice as explicit
  /// server-side, so a later bank link never overrides it.
  final String? currency;

  /// First day of the budgeting week (0=Sunday..6=Saturday). Setting it re-bases
  /// active weekly/bi-weekly budget periods server-side.
  final int? weekStartDay;

  UpdateProfileRequest({this.firstName, this.lastName, this.currency, this.weekStartDay});

  Map<String, dynamic> toJson() {
    final json = <String, dynamic>{};
    if (firstName != null) json['firstName'] = firstName;
    if (lastName != null) json['lastName'] = lastName;
    if (currency != null) json['currency'] = currency;
    if (weekStartDay != null) json['weekStartDay'] = weekStartDay;
    return json;
  }
}

/// Service for user profile API calls
class UserService {
  /// Grant or withdraw permission to share data with third-party AI (Gemini).
  /// Returns the server's grant timestamp (null when withdrawn). The server
  /// enforces this on every model-calling endpoint; the client mirrors it via
  /// AuthProvider.user.aiConsentGranted.
  Future<DateTime?> setAiConsent(bool granted) async {
    final res = await apiClient.put<Map<String, dynamic>>(
      '/user/ai-consent',
      data: {'granted': granted},
    );
    final raw = res['aiConsentGrantedAt'] ?? res['AiConsentGrantedAt'];
    return raw == null ? null : DateTime.tryParse(raw.toString());
  }

  final Dio _dio = apiClient.dio;

  /// Get current user profile.
  /// Reads go through [apiClient.get] so responses land in the offline cache (#493).
  Future<UserProfile> getProfile() async {
    final data = await apiClient.get<Map<String, dynamic>>('/user/profile');
    final profile = UserProfile.fromJson(data);
    // Keep the global display currency in sync with the freshest server value
    // (AuthProvider sets it at login; this covers profile refreshes).
    setPrimaryCurrency(profile.currency);
    return profile;
  }

  /// Update user profile
  Future<UserProfile> updateProfile(UpdateProfileRequest request) async {
    final response = await _dio.put<Map<String, dynamic>>(
      '/user/profile',
      data: request.toJson(),
    );
    final profile = UserProfile.fromJson(response.data!);
    setPrimaryCurrency(profile.currency);
    return profile;
  }

  /// Upload/replace the profile picture. [bytes] should already be resized by the
  /// picker (maxWidth/maxHeight); the backend caps the raw size.
  Future<UserProfile> uploadAvatar(
    List<int> bytes,
    String filename,
    String contentType,
  ) async {
    final form = FormData.fromMap({
      'file': MultipartFile.fromBytes(
        bytes,
        filename: filename,
        contentType: DioMediaType.parse(contentType),
      ),
    });
    final response =
        await _dio.post<Map<String, dynamic>>('/user/avatar', data: form);
    return UserProfile.fromJson(response.data!);
  }

  /// Remove the uploaded profile picture (revert to initials).
  Future<UserProfile> deleteAvatar() async {
    final response = await _dio.delete<Map<String, dynamic>>('/user/avatar');
    return UserProfile.fromJson(response.data!);
  }

  /// Delete account (password is null for Google-authenticated users)
  Future<void> deleteAccount(String? password) async {
    await _dio.delete(
      '/user/account',
      data: password != null ? {'password': password} : null,
    );
  }

  // ── Settings endpoints ────────────────────────────────

  /// Get budget settings
  Future<BudgetSettings> getBudgetSettings() async {
    try {
      final data = await apiClient.get<Map<String, dynamic>>('/user/settings/budget');
      return BudgetSettings.fromJson(data);
    } catch (_) {
      return BudgetSettings();
    }
  }

  /// Update budget settings. Offline-first (#493): on a network failure the op is
  /// queued and the corresponding GET cache is primed with the new value so the
  /// settings screen reflects the change immediately.
  Future<void> updateBudgetSettings(BudgetSettings settings) async {
    try {
      await _dio.put('/user/settings/budget', data: settings.toJson());
      unawaited(syncService.drainOutbox());
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opUserSettingsBudgetUpdate,
        entityId: '',
        payload: settings.toJson(),
      );
      await syncService.primeCache('/user/settings/budget', settings.toJson());
    }
  }

  /// Get AI settings
  Future<AiSettings> getAiSettings() async {
    try {
      final data = await apiClient.get<Map<String, dynamic>>('/user/settings/ai');
      return AiSettings.fromJson(data);
    } catch (_) {
      return AiSettings();
    }
  }

  /// Update AI settings. Offline-first: queues + primes the cache on a network
  /// failure (see [updateBudgetSettings]).
  Future<void> updateAiSettings(AiSettings settings) async {
    try {
      await _dio.put('/user/settings/ai', data: settings.toJson());
      unawaited(syncService.drainOutbox());
    } on DioException catch (e) {
      if (!SyncService.isNetworkError(e)) rethrow;
      await syncService.enqueue(
        operationType: SyncService.opUserSettingsAiUpdate,
        entityId: '',
        payload: settings.toJson(),
      );
      await syncService.primeCache('/user/settings/ai', settings.toJson());
    }
  }

  /// Get subscription prompt settings
  Future<SubscriptionPromptSettings> getSubscriptionPromptSettings() async {
    try {
      final response = await _dio.get<Map<String, dynamic>>('/user/settings/subscription-prompt');
      return SubscriptionPromptSettings.fromJson(response.data ?? {});
    } catch (_) {
      return SubscriptionPromptSettings();
    }
  }

  /// Update subscription prompt settings
  Future<void> updateSubscriptionPromptSettings(SubscriptionPromptSettings settings) async {
    await _dio.put('/user/settings/subscription-prompt', data: settings.toJson());
  }
}

// ── Settings DTOs ─────────────────────────────────────────

class BudgetSettings {
  final bool excludePendingFromBudget;
  final bool reattributeOnAccountAssociationChange;

  BudgetSettings({
    this.excludePendingFromBudget = false,
    this.reattributeOnAccountAssociationChange = false,
  });

  factory BudgetSettings.fromJson(Map<String, dynamic> json) {
    return BudgetSettings(
      excludePendingFromBudget: json['excludePendingFromBudget'] as bool? ?? false,
      reattributeOnAccountAssociationChange:
          json['reattributeOnAccountAssociationChange'] as bool? ?? false,
    );
  }

  Map<String, dynamic> toJson() => {
        'excludePendingFromBudget': excludePendingFromBudget,
        'reattributeOnAccountAssociationChange':
            reattributeOnAccountAssociationChange,
      };
}

class AiSettings {
  final bool aiMemoryEnabled;
  final String aiInteractionMode;

  /// AI Control Spectrum (#412): Manual, Coach, Assistant, or Manager.
  final String aiAutonomyLevel;

  AiSettings({
    this.aiMemoryEnabled = true,
    this.aiInteractionMode = 'Quick',
    this.aiAutonomyLevel = 'Coach',
  });

  factory AiSettings.fromJson(Map<String, dynamic> json) {
    return AiSettings(
      aiMemoryEnabled: json['aiMemoryEnabled'] as bool? ?? true,
      aiInteractionMode: json['aiInteractionMode'] as String? ?? 'Quick',
      aiAutonomyLevel: json['aiAutonomyLevel'] as String? ?? 'Coach',
    );
  }

  Map<String, dynamic> toJson() => {
    'aiMemoryEnabled': aiMemoryEnabled,
    'aiInteractionMode': aiInteractionMode,
    'aiAutonomyLevel': aiAutonomyLevel,
  };

  AiSettings copyWith({
    bool? aiMemoryEnabled,
    String? aiInteractionMode,
    String? aiAutonomyLevel,
  }) =>
      AiSettings(
        aiMemoryEnabled: aiMemoryEnabled ?? this.aiMemoryEnabled,
        aiInteractionMode: aiInteractionMode ?? this.aiInteractionMode,
        aiAutonomyLevel: aiAutonomyLevel ?? this.aiAutonomyLevel,
      );
}

class SubscriptionPromptSettings {
  final List<String> promptCategories;

  SubscriptionPromptSettings({this.promptCategories = const []});

  factory SubscriptionPromptSettings.fromJson(Map<String, dynamic> json) {
    final cats = json['promptCategories'];
    return SubscriptionPromptSettings(
      promptCategories: cats is List ? cats.cast<String>() : [],
    );
  }

  Map<String, dynamic> toJson() => {'promptCategories': promptCategories};
}

// Singleton instance
final userService = UserService();
