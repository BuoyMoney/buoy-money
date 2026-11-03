/// Records the sequence of top-level tabs the user has visited, so the Android system back
/// gesture can return to the *previous page* (then to the start destination, then exit).
///
/// GoRouter's `context.go(...)` — which the bottom nav and every drill-in use — *replaces* the
/// location and keeps no back stack, so there's nothing for the OS back gesture to pop and Android
/// runs its exit animation instead. This is the history that `go` throws away. Pushed sub-pages
/// (`context.push('/billing')` …) and modal sheets are NOT tracked here — those pop through
/// GoRouter/Navigator on their own; this only fills the tab-switching gap.
class NavHistory {
  /// The app's start destination — back from here (with no history) exits.
  static const String startDestination = '/dashboard';

  /// The known top-level tab routes (the bottom-nav / sidebar destinations). Only these are
  /// recorded; anything else is a pushed sub-page or modal that pops itself.
  static const Set<String> tabRoutes = {
    '/dashboard',
    '/transactions',
    '/budgets',
    '/savings',
    '/accounts',
    '/subscriptions',
    '/achievements',
    '/insights',
    '/ai-chat',
    '/notifications',
    '/profile',
    '/settings',
  };

  /// Cap so pathological A→B→A→B toggling can't grow the stack without bound.
  static const int maxDepth = 20;

  final List<String> _stack = [];

  List<String> get stack => List.unmodifiable(_stack);

  /// Whether there's a previous tab to return to.
  bool get canGoBack => _stack.length > 1;

  String? get current => _stack.isEmpty ? null : _stack.last;

  /// Record a visit to [location]. No-op for non-tab routes and for a repeat of the current tab —
  /// so navigating back (which re-visits the now-top entry) doesn't re-push it and cause a loop.
  void visit(String location) {
    if (!tabRoutes.contains(location)) return;
    if (_stack.isNotEmpty && _stack.last == location) return;
    _stack.add(location);
    if (_stack.length > maxDepth) _stack.removeAt(0);
  }

  /// Pop the current tab and return the previous one to navigate to, or null if there is none.
  String? goBack() {
    if (_stack.length < 2) return null;
    _stack.removeLast();
    return _stack.last;
  }

  void clear() => _stack.clear();
}

/// App-wide instance (matches the project's global-service pattern; swap in tests by constructing
/// [NavHistory] directly).
final navHistory = NavHistory();
