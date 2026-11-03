/// Web stub for background sync.
///
/// Selected via conditional import (`if (dart.library.html)`) so the web build
/// never imports `package:workmanager` (which has no web implementation). Web is
/// network-only, so there is nothing to schedule and no preference to honour.
const String kBackgroundSyncPrefKey = 'background_sync_enabled';

Future<void> registerBackgroundSync() async {}

Future<bool> isBackgroundSyncEnabled() async => false;

Future<void> setBackgroundSyncEnabled(bool enabled) async {}
