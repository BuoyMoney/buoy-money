import '../config/app_brand.dart';
import 'package:flutter/foundation.dart';
import 'package:passkeys/authenticator.dart';
import 'package:passkeys_platform_interface/types/types.dart';
import 'api_client.dart';

// ── DTOs ──────────────────────────────────────────────────

class UserPasskey {
  final int id;
  final String name;
  final String? deviceType;
  final String createdAt;
  final String? lastUsedAt;
  final int signCount;
  final bool isActive;

  UserPasskey({
    required this.id,
    required this.name,
    this.deviceType,
    required this.createdAt,
    this.lastUsedAt,
    required this.signCount,
    required this.isActive,
  });

  factory UserPasskey.fromJson(Map<String, dynamic> json) {
    T? val<T>(String camel, String pascal) =>
        (json[camel] ?? json[pascal]) as T?;
    return UserPasskey(
      id: val<int>('id', 'Id') ?? (val<num>('id', 'Id')?.toInt() ?? 0),
      name: val<String>('name', 'Name') ?? 'Passkey',
      deviceType: val<String>('deviceType', 'DeviceType'),
      createdAt: val<String>('createdAt', 'CreatedAt') ?? '',
      lastUsedAt: val<String>('lastUsedAt', 'LastUsedAt'),
      signCount: val<int>('signCount', 'SignCount') ??
          (val<num>('signCount', 'SignCount')?.toInt() ?? 0),
      isActive: val<bool>('isActive', 'IsActive') ?? true,
    );
  }
}

// ── Service ───────────────────────────────────────────────

class PasskeyService {
  final ApiClient _apiClient;
  final PasskeyAuthenticator _authenticator;

  PasskeyService(this._apiClient)
      : _authenticator = PasskeyAuthenticator(
          debugMode: kDebugMode,
        );

  /// Check if passkeys are supported on this platform.
  Future<bool> isSupported() async {
    try {
      final availability = _authenticator.getAvailability();
      if (kIsWeb) {
        final webAvail = await availability.web();
        return webAvail.hasPasskeySupport;
      }
      switch (defaultTargetPlatform) {
        case TargetPlatform.android:
          final androidAvail = await availability.android();
          return androidAvail.hasPasskeySupport;
        case TargetPlatform.iOS:
          final iosAvail = await availability.iOS();
          return iosAvail.hasPasskeySupport;
        case TargetPlatform.windows:
          final winAvail = await availability.windows();
          return winAvail.hasPasskeySupport;
        default:
          return false;
      }
    } catch (_) {
      return false;
    }
  }

  // ── Registration ─────────────────────────────────────────

  /// Register a new passkey for the authenticated user.
  /// Returns the device name for display.
  Future<String> registerPasskey({String? displayName}) async {
    final deviceName = _getDeviceType();

    // 1. Begin registration with backend
    final options = await _apiClient.post<Map<String, dynamic>>(
      '/twofactor/passkey/register/begin',
      data: {
        'displayName': displayName ?? '${AppBrand.name} User',
        'deviceName': deviceName,
      },
    );

    // 2. Parse the credentialCreationOptions from the server
    final creationOptionsJson = options['credentialCreationOptions'] as String;
    final registerRequest =
        RegisterRequestType.fromJsonString(creationOptionsJson);

    // 3. Use the platform authenticator (Face ID, Touch ID, Windows Hello, etc.)
    final registerResponse = await _authenticator.register(registerRequest);

    // 4. Complete registration with backend
    await _apiClient.post(
      '/twofactor/passkey/register/complete',
      data: {
        'credentialResponse': registerResponse.toJsonString(),
        'deviceName': deviceName,
      },
    );

    return deviceName;
  }

  // ── Authentication ───────────────────────────────────────

  /// Authenticate with a passkey. Returns the login response with JWT token.
  Future<Map<String, dynamic>> authenticateWithPasskey({
    String? username,
    bool rememberDevice = false,
  }) async {
    // 1. Begin authentication with backend
    final options = await _apiClient.post<Map<String, dynamic>>(
      '/twofactor/passkey/authenticate/begin',
      data: {'username': username},
    );

    // 2. Parse the assertionOptions from the server
    final assertionOptionsJson = options['assertionOptions'] as String;
    final authRequest =
        AuthenticateRequestType.fromJsonString(assertionOptionsJson);

    // 3. Use the platform authenticator
    final authResponse = await _authenticator.authenticate(authRequest);

    // 4. Complete authentication with backend
    final loginResponse = await _apiClient.post<Map<String, dynamic>>(
      '/twofactor/passkey/authenticate/complete',
      data: {
        'assertionResponse': authResponse.toJsonString(),
        'rememberDevice': rememberDevice,
      },
    );

    return loginResponse;
  }

  // ── Passkey Management ───────────────────────────────────

  /// Get all registered passkeys for the current user.
  Future<List<UserPasskey>> getUserPasskeys() async {
    final list = await _apiClient.get<List<dynamic>>('/twofactor/passkey/list');
    return list
        .map((e) => UserPasskey.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  /// Remove a passkey by ID. May require step-up MFA.
  Future<void> removePasskey(int passkeyId) async {
    await _apiClient.delete('/twofactor/passkey/$passkeyId');
  }

  /// Update a passkey's name.
  Future<void> updatePasskey(int passkeyId, {required String name}) async {
    await _apiClient.put(
      '/twofactor/passkey/$passkeyId',
      data: {'name': name},
    );
  }

  // ── Helpers ──────────────────────────────────────────────

  String _getDeviceType() {
    if (kIsWeb) {
      return 'Web Browser';
    }
    switch (defaultTargetPlatform) {
      case TargetPlatform.iOS:
        return 'iPhone';
      case TargetPlatform.android:
        return 'Android';
      case TargetPlatform.macOS:
        return 'Mac';
      case TargetPlatform.windows:
        return 'Windows PC';
      case TargetPlatform.linux:
        return 'Linux PC';
      default:
        return 'Unknown Device';
    }
  }
}

// Global singleton
final passkeyService = PasskeyService(apiClient);
