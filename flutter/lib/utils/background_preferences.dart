import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Global notifier for background bubble density (0–200).
/// Settings page writes to it; DeepWaterCurrentPainter reads it.
class BackgroundPreferences {
  BackgroundPreferences._();

  static const _key = 'bubble_density';
  static const int defaultDensity = 200;

  /// Current particle count. Listened to by DeepWaterCurrentPainter.
  static final particleCount = ValueNotifier<int>(defaultDensity);

  /// Load saved preference on app start.
  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    particleCount.value = prefs.getInt(_key) ?? defaultDensity;
  }

  /// Persist and broadcast a new density.
  static Future<void> setDensity(int count) async {
    particleCount.value = count.clamp(0, defaultDensity);
    final prefs = await SharedPreferences.getInstance();
    await prefs.setInt(_key, particleCount.value);
  }
}
