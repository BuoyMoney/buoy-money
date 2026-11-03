import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/widgets.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:workmanager/workmanager.dart';

import '../config/app_config.dart';
import '../services/api_client.dart';
import 'offline_store.dart';
import 'sync_service.dart';

/// Unique task identifier. Kept identical across Android (uniqueName/taskName)
/// and iOS (BGTaskScheduler identifier registered in Info.plist + AppDelegate)
/// so the same string threads through both platforms.
const String kBuoyaiSyncTask = 'com.buoyai.buoyAi.sync';

/// A small, fixed set of cache keys to warm on each background run. Kept short
/// to respect iOS's ~30s background budget. These GETs flow through ApiClient's
/// normal cache-write path, so they populate the same keys the UI reads.
Future<void> _warmCaches() async {
  Future<void> warm(String path, [Map<String, dynamic>? query]) async {
    try {
      await apiClient.get<dynamic>(path, queryParameters: query);
    } catch (_) {
      // A single failed refresh must not fail the whole task.
    }
  }

  await warm('/transactions', {'page': 1, 'pageSize': 25});
  await warm('/budget/active');
  await warm('/notifications/unread-count');
  await warm('/bank/accounts');
}

/// Workmanager entry point. Runs in a fresh background isolate with no app state,
/// so it re-initialises the offline store and lets ApiClient read the auth token
/// straight from flutter_secure_storage (its request interceptor already does
/// this — same 'authToken' key used everywhere). Everything is wrapped so the
/// task never crashes (always returns true).
@pragma('vm:entry-point')
void callbackDispatcher() {
  Workmanager().executeTask((task, inputData) async {
    try {
      WidgetsFlutterBinding.ensureInitialized();
      // A failed token refresh in this isolate must never wipe the shared
      // session (it would silently log the foreground app out).
      apiClient.backgroundIsolateMode = true;
      // Talk to the server the user picked, not the build's default.
      await AppConfig.loadCustomServer();
      apiClient.applyBaseUrl();
      await initOfflineStore();

      // 1) Flush any queued offline writes.
      await syncService.drainOutbox();

      // 2) Warm a small set of caches for a fast next open.
      await _warmCaches();

      // 3) Record when we last ran.
      await offlineStore.setMeta(
        'lastBackgroundSync',
        DateTime.now().toUtc().toIso8601String(),
      );
    } catch (_) {
      // Never surface a crash from a background task.
    }
    return Future.value(true);
  });
}

/// User preference gating background sync (Settings → Privacy & Data). ON by
/// default; the privacy policy's "control when financial data is updated"
/// promise is backed by this switch. Mobile-only (no-op on web).
const String kBackgroundSyncPrefKey = 'background_sync_enabled';

Future<bool> isBackgroundSyncEnabled() async {
  if (kIsWeb) return false;
  try {
    final prefs = await SharedPreferences.getInstance();
    return prefs.getBool(kBackgroundSyncPrefKey) ?? true;
  } catch (_) {
    return true;
  }
}

/// Persist the preference and apply it immediately: ON (re)schedules the
/// periodic task, OFF cancels it. Never throws.
Future<void> setBackgroundSyncEnabled(bool enabled) async {
  if (kIsWeb) return;
  try {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(kBackgroundSyncPrefKey, enabled);
    if (enabled) {
      await registerBackgroundSync();
    } else {
      await Workmanager().cancelByUniqueName(kBuoyaiSyncTask);
    }
  } catch (_) {
    // Best-effort; worst case the old schedule persists until next app start.
  }
}

/// Register the periodic background sync (honours the user preference —
/// disabled means no registration). Android schedules a ~30-minute WorkManager
/// job; on iOS the actual cadence is at the OS's discretion (the
/// BGTaskScheduler identifier is registered natively in AppDelegate/Info.plist).
/// Never throws — a scheduling failure just means no background sync.
Future<void> registerBackgroundSync() async {
  try {
    if (!await isBackgroundSyncEnabled()) return;
    await Workmanager().initialize(callbackDispatcher);
    await Workmanager().registerPeriodicTask(
      kBuoyaiSyncTask,
      kBuoyaiSyncTask,
      frequency: const Duration(minutes: 30),
      constraints: Constraints(
        networkType: NetworkType.connected,
        requiresBatteryNotLow: true,
      ),
      existingWorkPolicy: ExistingPeriodicWorkPolicy.keep,
    );
  } catch (_) {
    // Best-effort; offline still works via foreground reconnect drains.
  }
}
