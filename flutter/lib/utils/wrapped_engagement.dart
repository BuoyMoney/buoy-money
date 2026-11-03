import 'dart:convert';

import 'package:shared_preferences/shared_preferences.dart';

/// Local, on-device record of how long the user lingered on each Wrapped card
/// type across monthly stories. The year-end story leads with what THIS user
/// actually cares about — no backend analytics plumbing required (aggregate
/// learning happens separately via opt-in PostHog events).
class WrappedEngagement {
  WrappedEngagement._();

  static const _key = 'wrapped_dwell_ms';
  static Map<String, int> _dwell = {};
  static bool _loaded = false;

  static Future<void> _ensureLoaded() async {
    if (_loaded) return;
    try {
      final prefs = await SharedPreferences.getInstance();
      final raw = prefs.getString(_key);
      if (raw != null) {
        _dwell = (jsonDecode(raw) as Map<String, dynamic>)
            .map((k, v) => MapEntry(k, (v as num).toInt()));
      }
    } catch (_) {
      _dwell = {};
    }
    _loaded = true;
  }

  /// Add [ms] of dwell to [cardType]. Fire-and-forget safe.
  static Future<void> record(String cardType, int ms) async {
    if (ms < 400) return; // a flick-through says nothing
    await _ensureLoaded();
    _dwell[cardType] = (_dwell[cardType] ?? 0) + ms;
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setString(_key, jsonEncode(_dwell));
    } catch (_) {}
  }

  /// Total dwell for a card type (0 when never seen).
  static Future<int> scoreFor(String cardType) async {
    await _ensureLoaded();
    return _dwell[cardType] ?? 0;
  }

  /// Snapshot of all scores, for ordering a story in one read.
  static Future<Map<String, int>> scores() async {
    await _ensureLoaded();
    return Map.of(_dwell);
  }

  /// Test hook.
  static void resetForTest() {
    _dwell = {};
    _loaded = true;
  }
}
