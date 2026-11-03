import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'app_config.dart';

/// The first-launch question "How would you like to use Buoy Money?" (hosted, or a server of your own).
///
/// It is asked once, by a fresh install of the phone or desktop app on the production build, before anyone has
/// signed in on the device. Everyone else goes straight to sign-in or sign-up as before:
/// - anyone who has signed in on this device, or still has tokens from an older version (an upgrade);
/// - anyone who already picked a server of their own;
/// - the web (a page talks to the server it came from), dev and staging builds, and builds pinned to one API.
class FirstRun {
  FirstRun._();

  /// SharedPreferences key set once the question was answered.
  static const prefKey = 'first_run_choice_done';

  static bool _done = false;

  /// True once the question was answered on this device.
  static bool get done => _done;

  /// The build can ask at all: a production phone or desktop app whose server is not fixed at build time.
  static bool get eligibleBuild =>
      AppConfig.canChooseServer && AppConfig.isProduction && !AppConfig.hasFixedServer;

  /// Whether a signed-out visitor at the app root sees the question instead of sign-up.
  static bool shouldShow({required bool hasSignedInBefore}) =>
      eligibleBuild && !_done && !hasSignedInBefore && !AppConfig.isCustomServer;

  /// Reads the saved answer. Call once in main() before the router runs. Never throws.
  static Future<void> load() async {
    try {
      final prefs = await SharedPreferences.getInstance();
      _done = prefs.getBool(prefKey) ?? false;
    } catch (_) {
      _done = false;
    }
  }

  /// Remembers that the question was answered, so it is never asked again on this device.
  static Future<void> markDone() async {
    _done = true;
    try {
      final prefs = await SharedPreferences.getInstance();
      await prefs.setBool(prefKey, true);
    } catch (_) {}
  }

  @visibleForTesting
  static set doneForTest(bool value) => _done = value;
}
