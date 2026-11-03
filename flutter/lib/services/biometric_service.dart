import '../config/app_brand.dart';
import 'package:local_auth/local_auth.dart';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter_secure_storage/flutter_secure_storage.dart';

import '../utils/secure_window.dart';

/// Biometric Service - handles fingerprint/Face ID authentication
/// 
/// This service allows users to:
/// 1. Check if biometric auth is available on the device
/// 2. Enable biometric login after successful email/password login
/// 3. Authenticate using biometrics to retrieve stored credentials
/// 
/// Security model:
/// - Credentials are stored encrypted in secure storage
/// - Biometric verification is required to access them
/// - The biometric check is local only and is NOT a second factor: the server
///   still applies MFA (skipped only for accounts without MFA or on a device
///   trusted by an earlier completed MFA)
class BiometricService {
  final LocalAuthentication _localAuth = LocalAuthentication();
  // Device-bound keychain (not iCloud-backed-up), readable after first unlock. The stored
  // credentials must never sync to another device via iCloud Keychain. See _PlatformStorage.
  final FlutterSecureStorage _secureStorage = const FlutterSecureStorage(
    iOptions: IOSOptions(
      accessibility: KeychainAccessibility.first_unlock_this_device,
    ),
  );
  
  // Storage keys
  static const String _biometricEnabledKey = 'biometric_enabled';
  static const String _biometricEmailKey = 'biometric_email';
  static const String _biometricPasswordKey = 'biometric_password';
  static const String _biometricUserIdKey = 'biometric_user_id';
  // App-unlock (local gate over an existing session) is a separate concern from
  // quick-login (stored credentials): unlock works for ALL auth types and never
  // stores a password. Keyed independently so the two toggles don't entangle.
  static const String _appLockEnabledKey = 'app_lock_enabled';
  // Set once the user says "Not now" to the post-login quick-login offer, so we
  // don't nag on every subsequent sign-in. Cleared when they opt in from Settings.
  static const String _offerDeclinedKey = 'biometric_offer_declined';

  /// How long the app may sit backgrounded before a resume re-locks it. Short
  /// enough to protect a set-down phone, long enough not to punish app-switching.
  static const Duration appLockResumeThreshold = Duration(minutes: 2);

  /// Check if device supports biometric authentication
  Future<bool> isDeviceSupported() async {
    try {
      return await _localAuth.isDeviceSupported();
    } catch (e) {
      return false;
    }
  }
  
  /// Check if biometrics can be used (device supported + enrolled)
  Future<bool> canCheckBiometrics() async {
    try {
      final isSupported = await _localAuth.isDeviceSupported();
      if (!isSupported) return false;
      
      final canCheck = await _localAuth.canCheckBiometrics;
      return canCheck;
    } catch (e) {
      return false;
    }
  }
  
  /// Get available biometric types (fingerprint, face, iris)
  Future<List<BiometricType>> getAvailableBiometrics() async {
    try {
      return await _localAuth.getAvailableBiometrics();
    } catch (e) {
      return [];
    }
  }
  
  /// Check if biometric login is enabled for this app
  Future<bool> isBiometricLoginEnabled() async {
    try {
      final enabled = await _secureStorage.read(key: _biometricEnabledKey);
      if (enabled != 'true') return false;
      
      // Also verify credentials are stored
      final email = await _secureStorage.read(key: _biometricEmailKey);
      final password = await _secureStorage.read(key: _biometricPasswordKey);
      return email != null && password != null;
    } catch (e) {
      return false;
    }
  }
  
  /// Enable biometric login - stores credentials securely
  /// Call this after successful email/password login
  Future<bool> enableBiometricLogin({
    required String email,
    required String password,
    required String userId,
  }) async {
    try {
      // Never store the raw password on web: web "secure storage" is localStorage,
      // which is XSS-readable (F1-tok). Web uses passkeys/WebAuthn for strong auth
      // instead of a stored password.
      if (kIsWeb) return false;

      // First verify biometric is available
      final canUse = await canCheckBiometrics();
      if (!canUse) return false;
      
      // Prompt user to confirm with biometric
      final authenticated = await authenticate(
        reason: 'Verify your identity to enable biometric login',
      );
      
      if (!authenticated) return false;
      
      // Store credentials securely
      await _secureStorage.write(key: _biometricEmailKey, value: email);
      await _secureStorage.write(key: _biometricPasswordKey, value: password);
      await _secureStorage.write(key: _biometricUserIdKey, value: userId);
      await _secureStorage.write(key: _biometricEnabledKey, value: 'true');
      
      return true;
    } catch (e) {
      return false;
    }
  }
  
  /// Disable biometric login - clears stored credentials
  Future<void> disableBiometricLogin() async {
    try {
      await _secureStorage.delete(key: _biometricEnabledKey);
      await _secureStorage.delete(key: _biometricEmailKey);
      await _secureStorage.delete(key: _biometricPasswordKey);
      await _secureStorage.delete(key: _biometricUserIdKey);
    } catch (e) {
      // Ignore errors during cleanup
    }
  }
  
  /// Authenticate with biometrics and retrieve stored credentials
  /// Returns null if authentication fails or credentials not found
  Future<BiometricCredentials?> authenticateAndGetCredentials() async {
    try {
      // Check if biometric login is enabled
      final isEnabled = await isBiometricLoginEnabled();
      if (!isEnabled) return null;
      
      // Prompt for biometric authentication
      final authenticated = await authenticate(
        reason: 'Login to ${AppBrand.name}',
      );
      
      if (!authenticated) return null;
      
      // Retrieve stored credentials
      final email = await _secureStorage.read(key: _biometricEmailKey);
      final password = await _secureStorage.read(key: _biometricPasswordKey);
      final userId = await _secureStorage.read(key: _biometricUserIdKey);
      
      if (email == null || password == null) return null;
      
      return BiometricCredentials(
        email: email,
        password: password,
        userId: userId,
      );
    } catch (e) {
      return null;
    }
  }
  
  /// Perform biometric authentication
  Future<bool> authenticate({required String reason}) async {
    try {
      // local_auth 3.x removed the exported AuthenticationOptions class and
      // flattened its fields into named parameters on authenticate().
      // stickyAuth was renamed to persistAcrossBackgrounding.
      return await _localAuth.authenticate(
        localizedReason: reason,
        biometricOnly: true,
        persistAcrossBackgrounding: true,
      );
    } catch (e) {
      return false;
    }
  }
  
  /// Get a user-friendly name for the available biometric type
  Future<String> getBiometricTypeName() async {
    final biometrics = await getAvailableBiometrics();
    
    if (biometrics.contains(BiometricType.face)) {
      return 'Face ID';
    } else if (biometrics.contains(BiometricType.fingerprint)) {
      return 'Fingerprint';
    } else if (biometrics.contains(BiometricType.iris)) {
      return 'Iris';
    } else if (biometrics.contains(BiometricType.strong)) {
      return 'Biometric';
    } else if (biometrics.contains(BiometricType.weak)) {
      return 'Biometric';
    }
    
    return 'Biometric';
  }
  
  // ── App-unlock gate ──────────────────────────────────────────────────────
  // A LOCAL gate over an already-authenticated session (Chase/Robinhood style).
  // It does not store credentials and works for every auth type (email, Google,
  // Apple, passkey) — it just requires a biometric to reveal the app on open.

  /// Whether the biometric app-unlock gate is enabled on this device.
  Future<bool> isAppLockEnabled() async {
    try {
      if (kIsWeb) return false;
      return (await _secureStorage.read(key: _appLockEnabledKey)) == 'true';
    } catch (e) {
      return false;
    }
  }

  /// Enable/disable the app-unlock gate. Enabling first requires a successful
  /// biometric check so a user can't lock themselves behind a factor they can't
  /// actually satisfy. Returns the resulting enabled-state (false if enabling was
  /// cancelled/failed). Never persists on web (no local_auth implementation).
  Future<bool> setAppLockEnabled(bool enabled) async {
    try {
      if (kIsWeb) return false;
      if (enabled) {
        final ok = await authenticate(reason: 'Enable biometric app unlock');
        if (!ok) return false;
        await _secureStorage.write(key: _appLockEnabledKey, value: 'true');
        // Turn on native snapshot protection (Android FLAG_SECURE / iOS blur).
        await applyAppLockSecureWindow(true);
        return true;
      }
      await _secureStorage.delete(key: _appLockEnabledKey);
      await applyAppLockSecureWindow(false);
      return false;
    } catch (e) {
      return false;
    }
  }

  // ── Quick-login enrollment offer state ───────────────────────────────────

  /// Whether the user previously dismissed the post-login quick-login offer.
  Future<bool> isBiometricOfferDeclined() async {
    try {
      return (await _secureStorage.read(key: _offerDeclinedKey)) == 'true';
    } catch (e) {
      return false;
    }
  }

  /// Persist the user's "Not now" choice so the offer stops appearing.
  Future<void> setBiometricOfferDeclined(bool declined) async {
    try {
      if (declined) {
        await _secureStorage.write(key: _offerDeclinedKey, value: 'true');
      } else {
        await _secureStorage.delete(key: _offerDeclinedKey);
      }
    } catch (e) {
      // Non-fatal — worst case the offer shows again next login.
    }
  }

  /// Get the stored email (for display on login screen)
  Future<String?> getStoredEmail() async {
    try {
      return await _secureStorage.read(key: _biometricEmailKey);
    } catch (e) {
      return null;
    }
  }
}

/// Credentials retrieved after successful biometric authentication
class BiometricCredentials {
  final String email;
  final String password;
  final String? userId;

  BiometricCredentials({
    required this.email,
    required this.password,
    this.userId,
  });
}

// Global singleton instance. Mutable so widget tests can swap in a fake, as with
// transactionsService/accountsService.
BiometricService biometricService = BiometricService();
