import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/services.dart';

/// Bridges the app-lock-enabled state to the native layer so the OS
/// app-switcher snapshot (and screenshots) never expose financial content while
/// biometric app-lock is on.
///
/// - Android: `MainActivity` sets/clears `WindowManager.LayoutParams.FLAG_SECURE`
///   on the window. FLAG_SECURE blocks screenshots AND the recents/switcher
///   thumbnail.
/// - iOS: `AppDelegate` mirrors the flag into `UserDefaults` and, when enabled,
///   overlays a blur view on `applicationWillResignActive` (removed on
///   `applicationDidBecomeActive`) so the pre-background snapshot is obscured.
///
/// No-op on web and if the platform side isn't implemented (desktop, tests).
const MethodChannel _channel = MethodChannel('com.buoyai/secure_window');

/// Push [enabled] to the native layer. Call whenever app-lock is toggled and
/// once at startup with the persisted value. Never throws.
Future<void> applyAppLockSecureWindow(bool enabled) async {
  if (kIsWeb) return;
  try {
    await _channel.invokeMethod<void>(
      'setAppLockEnabled',
      {'enabled': enabled},
    );
  } catch (_) {
    // Platform channel not implemented (web/desktop/tests) — non-fatal; the
    // Dart-side scrim still protects content, this is defence-in-depth.
  }
}
