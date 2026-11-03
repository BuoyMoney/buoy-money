import 'package:audioplayers/audioplayers.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Global notifier for the celebration sound toggle (on by default —
/// the chime is soft by design, and the OS mute switch still wins).
class SoundPreferences {
  SoundPreferences._();

  static const _key = 'celebration_sound';
  static const bool defaultEnabled = true;

  static final celebrationSound = ValueNotifier<bool>(defaultEnabled);

  /// Load saved preference on app start.
  static Future<void> init() async {
    final prefs = await SharedPreferences.getInstance();
    celebrationSound.value = prefs.getBool(_key) ?? defaultEnabled;
  }

  /// Persist and broadcast the toggle.
  static Future<void> setCelebrationSound(bool enabled) async {
    celebrationSound.value = enabled;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_key, enabled);
  }
}

/// Celebration feedback: the achievement chime + a medium haptic. One entry
/// point so every celebration (badge unlock, challenge complete) sounds and
/// feels the same. All failure modes are swallowed — feedback is a bonus,
/// never an error path (e.g. MissingPluginException in widget tests).
class CelebrationFx {
  CelebrationFx._();

  static final _player = AudioPlayer();

  static Future<void> play() async {
    try {
      HapticFeedback.mediumImpact();
    } catch (_) {}
    // No audio on web — unsolicited sound on a website is jarring and nobody
    // expects it there. Apps are a different story.
    if (kIsWeb) return;
    if (!SoundPreferences.celebrationSound.value) return;
    try {
      await _player.stop(); // restart cleanly if a celebration chains fast
      await _player.play(AssetSource('sounds/achievement_chime.mp3'),
          volume: 0.8);
    } catch (_) {}
  }

  /// Haptic-only celebration beat: for follow-up moments where the chime
  /// would get repetitive (each chained badge after the first, the Wrapped
  /// finale). Inert on web.
  static void buzz() {
    if (kIsWeb) return;
    try {
      HapticFeedback.mediumImpact();
    } catch (_) {}
  }

  /// The subtlest built-in tactile tick (what OS keyboards use). For ambient
  /// milestone moments — ticker whole-dollar crossings and the like. Silent
  /// and inert on web/desktop, where the channel is a no-op.
  static void milestoneTick() {
    if (kIsWeb) return;
    try {
      HapticFeedback.selectionClick();
    } catch (_) {}
  }
}
