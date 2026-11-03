import 'dart:async';

import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:flutter/foundation.dart';

/// Tracks whether the app can actually reach the backend.
///
/// Two signals are combined, because neither alone is reliable:
///  1. The OS interface state from connectivity_plus — but "wifi" only means an
///     interface exists, not that the backend is reachable (captive portals,
///     dropped VPN, backend down all report "wifi").
///  2. Real request outcomes fed in by ApiClient via [reportNetworkFailure] /
///     [reportNetworkSuccess] — the ground truth.
///
/// [isOnline] = an interface exists AND we haven't seen a run of failed
/// requests. A single fresh success clears the failure streak immediately.
class ConnectivityService extends ChangeNotifier {
  /// Consecutive request failures at/after which we consider ourselves offline
  /// even though an interface is up.
  static const int _failThreshold = 2;

  bool _hasInterface = true;
  int _consecutiveFailures = 0;
  bool _lastEmitted = true;

  final StreamController<bool> _controller = StreamController<bool>.broadcast();
  StreamSubscription<List<ConnectivityResult>>? _sub;

  /// Emits on every online↔offline transition (true = came online).
  Stream<bool> get onlineStream => _controller.stream;

  bool get isOnline => _hasInterface && _consecutiveFailures < _failThreshold;

  /// Begin listening to OS connectivity changes. Safe to call on web
  /// (connectivity_plus has a web implementation); idempotent.
  Future<void> init() async {
    if (_sub != null) return;
    try {
      final initial = await Connectivity().checkConnectivity();
      _applyInterface(initial);
      _sub = Connectivity()
          .onConnectivityChanged
          .listen(_applyInterface, onError: (_) {});
    } catch (_) {
      // Platform channel unavailable (e.g. pure Dart test host) — assume online
      // and rely purely on request outcomes.
    }
    _lastEmitted = isOnline;
  }

  void _applyInterface(List<ConnectivityResult> results) {
    final has = results.any((r) => r != ConnectivityResult.none);
    _hasInterface = has;
    // A regained interface is worth re-probing the backend — clear the streak so
    // one successful request can flip us online.
    if (has) _consecutiveFailures = 0;
    _emitIfChanged();
  }

  /// Called by ApiClient when a request fails with a transport-type error.
  void reportNetworkFailure() {
    if (_consecutiveFailures < 1000) _consecutiveFailures++;
    _emitIfChanged();
  }

  /// Called by ApiClient when any request succeeds.
  void reportNetworkSuccess() {
    var changed = false;
    if (_consecutiveFailures != 0) {
      _consecutiveFailures = 0;
      changed = true;
    }
    if (!_hasInterface) {
      _hasInterface = true;
      changed = true;
    }
    if (changed) _emitIfChanged();
  }

  void _emitIfChanged() {
    final now = isOnline;
    if (now != _lastEmitted) {
      _lastEmitted = now;
      if (!_controller.isClosed) _controller.add(now);
      notifyListeners();
    }
  }

  @override
  void dispose() {
    _sub?.cancel();
    _controller.close();
    super.dispose();
  }
}

/// Global singleton. Non-final so tests can swap in a fresh instance.
ConnectivityService connectivityService = ConnectivityService();
