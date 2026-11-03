import 'package:flutter/foundation.dart';

/// App-wide, observable offline/sync status that the [OfflineBanner] renders.
///
/// ApiClient updates the cache fields when it serves (or stops serving) stale
/// data; SyncService updates [pendingOutboxCount] as the outbox fills/drains.
class OfflineStatus extends ChangeNotifier {
  bool _showingCachedData = false;
  DateTime? _oldestServedCacheTime;
  int _pendingOutboxCount = 0;

  /// True while at least one screen is being fed from the local cache because a
  /// live request failed.
  bool get showingCachedData => _showingCachedData;

  /// The oldest `fetchedAt` among currently-served cache hits (drives the
  /// "showing data from …" relative timestamp). Null when nothing stale served.
  DateTime? get oldestServedCacheTime => _oldestServedCacheTime;

  /// Number of queued offline writes waiting to sync.
  int get pendingOutboxCount => _pendingOutboxCount;

  /// A cache hit was served (a live GET failed and we fell back). Tracks the
  /// oldest served timestamp so the banner shows the staleness of the worst one.
  void markServedFromCache(DateTime fetchedAt) {
    var changed = false;
    if (!_showingCachedData) {
      _showingCachedData = true;
      changed = true;
    }
    if (_oldestServedCacheTime == null ||
        fetchedAt.isBefore(_oldestServedCacheTime!)) {
      _oldestServedCacheTime = fetchedAt;
      changed = true;
    }
    if (changed) notifyListeners();
  }

  /// A live response came back fresh — we're no longer serving stale data.
  void reportFreshData() {
    if (_showingCachedData || _oldestServedCacheTime != null) {
      _showingCachedData = false;
      _oldestServedCacheTime = null;
      notifyListeners();
    }
  }

  void setPendingCount(int count) {
    if (count != _pendingOutboxCount) {
      _pendingOutboxCount = count;
      notifyListeners();
    }
  }
}

/// Global singleton. Non-final so tests can swap in a fresh instance.
OfflineStatus offlineStatus = OfflineStatus();
