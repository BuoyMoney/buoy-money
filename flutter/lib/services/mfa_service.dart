import 'api_client.dart';

// ── DTOs ──────────────────────────────────────────────────

class MfaPreferences {
  final String primaryMethod;
  final String? fallbackMethod;
  final bool notifyOnNewDevice;
  final bool notifyOnSuspiciousLogin;
  final List<String> availableMethods;

  MfaPreferences({
    required this.primaryMethod,
    this.fallbackMethod,
    required this.notifyOnNewDevice,
    required this.notifyOnSuspiciousLogin,
    required this.availableMethods,
  });

  factory MfaPreferences.fromJson(Map<String, dynamic> json) {
    T? val<T>(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as T?;
    return MfaPreferences(
      primaryMethod: val<String>('primaryMethod', 'PrimaryMethod') ?? 'Email',
      fallbackMethod: val<String>('fallbackMethod', 'FallbackMethod'),
      notifyOnNewDevice: val<bool>('notifyOnNewDevice', 'NotifyOnNewDevice') ?? true,
      notifyOnSuspiciousLogin: val<bool>('notifyOnSuspiciousLogin', 'NotifyOnSuspiciousLogin') ?? true,
      availableMethods: (val<List<dynamic>>('availableMethods', 'AvailableMethods'))
              ?.map((e) => e.toString())
              .toList() ??
          [],
    );
  }
}

class UpdateMfaPreferences {
  final String primaryMethod;
  final String? fallbackMethod;
  final bool notifyOnNewDevice;
  final bool notifyOnSuspiciousLogin;

  UpdateMfaPreferences({
    required this.primaryMethod,
    this.fallbackMethod,
    required this.notifyOnNewDevice,
    required this.notifyOnSuspiciousLogin,
  });

  Map<String, dynamic> toJson() => {
        'primaryMethod': primaryMethod,
        'fallbackMethod': fallbackMethod,
        'notifyOnNewDevice': notifyOnNewDevice,
        'notifyOnSuspiciousLogin': notifyOnSuspiciousLogin,
      };
}

class StepUpMfaResponse {
  final String operationToken;
  final DateTime expiresAt;
  final String operation;

  StepUpMfaResponse({
    required this.operationToken,
    required this.expiresAt,
    required this.operation,
  });

  factory StepUpMfaResponse.fromJson(Map<String, dynamic> json) {
    T? val<T>(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as T?;
    return StepUpMfaResponse(
      operationToken: val<String>('operationToken', 'OperationToken') ?? '',
      expiresAt: DateTime.parse(val<String>('expiresAt', 'ExpiresAt') ?? ''),
      operation: val<String>('operation', 'Operation') ?? '',
    );
  }
}

class MfaStatus {
  final bool isMfaEnabled;
  final String? currentMfaType;
  final bool hasPasskeyRegistered;
  final List<String> enabledMethods;
  /// Server verdict for the Plaid gate (TOTP/passkey always; email inside its window).
  final bool canAccessPlaid;
  /// When email-code MFA stops counting for Plaid (null for strong methods / no MFA).
  final DateTime? gracePeriodEndsAt;

  MfaStatus({
    required this.isMfaEnabled,
    this.currentMfaType,
    this.hasPasskeyRegistered = false,
    required this.enabledMethods,
    this.canAccessPlaid = false,
    this.gracePeriodEndsAt,
  });

  /// True for a user relying on the bounded email window: they can link banks
  /// today but should upgrade before [gracePeriodEndsAt].
  bool get isOnEmailWindow =>
      canAccessPlaid &&
      (currentMfaType ?? '').toLowerCase() == 'email' &&
      !hasPasskeyRegistered;

  int? get daysLeftInWindow {
    final end = gracePeriodEndsAt;
    if (end == null) return null;
    final d = end.difference(DateTime.now()).inDays;
    return d < 0 ? 0 : d;
  }

  factory MfaStatus.fromJson(Map<String, dynamic> json) {
    T? val<T>(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as T?;

    final enabled = val<bool>('hasMfaEnabled', 'HasMfaEnabled')
        ?? val<bool>('isMfaEnabled', 'IsMfaEnabled')
        ?? false;
    final mfaType = val<String>('currentMfaType', 'CurrentMfaType');
    final hasPasskey = val<bool>('hasPasskeyRegistered', 'HasPasskeyRegistered') ?? false;

    // Derive enabled methods from backend fields
    final methods = <String>[];
    if (mfaType != null && mfaType.isNotEmpty && mfaType.toLowerCase() != 'none') {
      methods.add(mfaType.toLowerCase());
    }
    if (hasPasskey && !methods.contains('passkey')) {
      methods.add('passkey');
    }

    // Also support the legacy enabledMethods array if present
    final legacyMethods = val<List<dynamic>>('enabledMethods', 'EnabledMethods');
    if (legacyMethods != null) {
      for (final m in legacyMethods) {
        final method = m.toString().toLowerCase();
        if (!methods.contains(method)) methods.add(method);
      }
    }

    return MfaStatus(
      isMfaEnabled: enabled,
      currentMfaType: mfaType,
      hasPasskeyRegistered: hasPasskey,
      enabledMethods: methods,
      canAccessPlaid: val<bool>('canAccessPlaidFeatures', 'CanAccessPlaidFeatures') ?? false,
      gracePeriodEndsAt: DateTime.tryParse(
          (val<String>('mfaGracePeriodEndsAt', 'MfaGracePeriodEndsAt') ?? '')),
    );
  }
}

// ── Service ───────────────────────────────────────────────

class MfaService {
  final ApiClient _apiClient;

  MfaService(this._apiClient);

  // ── Preferences ─────────────────────────────────────────

  Future<MfaPreferences> getPreferences() async {
    final json = await _apiClient.get<Map<String, dynamic>>('/mfapreferences');
    return MfaPreferences.fromJson(json);
  }

  Future<void> updatePreferences(UpdateMfaPreferences prefs) async {
    await _apiClient.put('/mfapreferences', data: prefs.toJson());
  }

  Future<List<String>> getAvailableMethods() async {
    final json =
        await _apiClient.get<Map<String, dynamic>>('/mfapreferences/available-methods');
    return (json['methods'] as List<dynamic>).map((e) => e as String).toList();
  }

  // ── Step-up MFA ─────────────────────────────────────────

  Future<void> requestStepUp(String operation) async {
    await _apiClient.post(
      '/auth/step-up-mfa/request',
      data: {'operation': operation},
    );
  }

  Future<StepUpMfaResponse> verifyStepUp({
    required String mfaCode,
    required String operation,
  }) async {
    final json = await _apiClient.post<Map<String, dynamic>>(
      '/auth/step-up-mfa/verify',
      data: {'mfaCode': mfaCode, 'operation': operation},
    );
    return StepUpMfaResponse.fromJson(json);
  }

  Future<bool> checkStepUpRequired(String operation) async {
    final json = await _apiClient.get<Map<String, dynamic>>(
      '/auth/step-up-mfa/check',
      queryParameters: {'operation': operation},
    );
    return json['requiresMfa'] as bool? ?? false;
  }

  Future<Map<String, dynamic>> beginStepUpPasskey(String operation) async {
    return await _apiClient.post<Map<String, dynamic>>(
      '/auth/step-up-mfa/passkey/begin',
      data: {'operation': operation},
    );
  }

  Future<StepUpMfaResponse> verifyStepUpPasskey({
    required String assertionResponse,
    required String operation,
  }) async {
    final json = await _apiClient.post<Map<String, dynamic>>(
      '/auth/step-up-mfa/passkey/verify',
      data: {'assertionResponse': assertionResponse, 'operation': operation},
    );
    return StepUpMfaResponse.fromJson(json);
  }

  // ── MFA Status ──────────────────────────────────────────

  Future<MfaStatus> getStatus() async {
    final json =
        await _apiClient.get<Map<String, dynamic>>('/twofactor/status');
    return MfaStatus.fromJson(json);
  }
}

// Global singleton
final mfaService = MfaService(apiClient);
