import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Global notifier for the live count-up ticker toggle (display-only).
/// Settings page writes to it; the budgets count-up view reads it.
class TickerPreferences {
  TickerPreferences._();

  static const _key = 'live_countup_ticker';
  static const bool defaultEnabled = false;

  /// Whether count-up balances tick upward in real time.
  static final liveCountUpTicker = ValueNotifier<bool>(defaultEnabled);

  /// Load saved preference on app start.
  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    liveCountUpTicker.value = prefs.getBool(_key) ?? defaultEnabled;
  }

  /// Persist and broadcast the toggle.
  static Future<void> setLiveCountUpTicker(bool enabled) async {
    liveCountUpTicker.value = enabled;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_key, enabled);
  }
}
