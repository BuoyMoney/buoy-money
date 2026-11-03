import 'package:flutter/material.dart';
import 'package:flutter/scheduler.dart';

import '../models/nav_destination.dart';

/// Tracks navigation depth to determine transition direction.
///
/// Depth is DERIVED from [kNavDestinations], the same list the nav menu renders,
/// in the same order. A page further down the menu is "deeper", so navigating to
/// it dives (content rises from below) and navigating back surfaces (content
/// drops from above). Reordering the menu reorders the transitions with it.
///
/// This used to be a hand-maintained map of path to depth, which drifted: the
/// menu listed Achievements above AI Chat while the map ranked Achievements
/// *below* everything, so moving between them animated backwards.
class NavigationDepth {
  NavigationDepth._();

  /// Gap between adjacent top-level destinations. The spare room in between is
  /// where sub-routes sit, so `/settings/email-preferences` lands just below
  /// `/settings` without displacing whatever follows it in the menu.
  static const _stride = 10;

  // Admin-only entries sit last and are left out, so non-admin depth mapping is unchanged.
  static final int _maxDepth =
      (kNavDestinations.where((d) => !d.isAdmin).length - 1) * _stride;

  static int _previousDepth = 0;

  /// Direction of the most recent navigation action.
  /// Exiting pages read this to determine their exit direction.
  static Offset _lastDirection = const Offset(0, 1);
  static Offset get lastDirection => _lastDirection;

  /// Broadcasts depth as a fraction (0.0 = surface, 1.0 = deepest).
  /// The background gradient listens to this for the deep-sea darkening effect.
  static final depthNotifier = ValueNotifier<double>(0.0);

  /// Depth index for a path, or null when the path is neither a top-level
  /// destination nor nested under one (e.g. `/billing`, `/family`, `/ai-quiz`).
  ///
  /// Callers decide what to do with an unplaceable route; see
  /// [getSlideDirection], which treats it as one level deeper than wherever the
  /// user currently is, because such pages are always reached from another page.
  static int? depthOf(String path) {
    final exact = kNavDestinations.indexWhere((d) => d.path == path);
    if (exact >= 0) return exact * _stride;

    // Nested under a destination: deeper than its parent by one level per extra
    // path segment. Longest matching parent wins so `/settings/x` prefers
    // `/settings` over any shorter prefix.
    var parentIndex = -1;
    var parentLength = 0;
    for (var i = 0; i < kNavDestinations.length; i++) {
      final parent = kNavDestinations[i].path;
      if (path.startsWith('$parent/') && parent.length > parentLength) {
        parentIndex = i;
        parentLength = parent.length;
      }
    }
    if (parentIndex >= 0) {
      final extraSegments = '/'.allMatches(path.substring(parentLength)).length;
      // Clamped so a deeply nested child can never leapfrog the next top-level
      // destination, which would invert the direction of leaving it.
      return parentIndex * _stride + extraSegments.clamp(1, _stride - 1);
    }

    return null;
  }

  /// Computes transition direction.
  /// Returns an Offset for the SlideTransition begin position:
  /// - Diving deeper: Offset(0, 1)  → new page enters from below
  /// - Surfacing:     Offset(0, -1) → new page enters from above
  static Offset getSlideDirection(String newPath) {
    // A route with no place in the menu is a detail page reached from wherever
    // you were, so it is always one level deeper. Recording that as the new
    // previous depth means leaving it correctly surfaces back to the menu page.
    final newDepth = depthOf(newPath) ?? _previousDepth + 1;

    final direction = newDepth >= _previousDepth
        ? const Offset(0, 1) // diving — content rises from below
        : const Offset(0, -1); // surfacing — content drops from above
    _previousDepth = newDepth;
    _lastDirection = direction;
    // Defer notifier update to avoid setState-during-build when
    // the transition builder triggers a ValueListenableBuilder rebuild.
    final depthFraction = (newDepth / _maxDepth).clamp(0.0, 1.0);
    SchedulerBinding.instance.addPostFrameCallback((_) {
      depthNotifier.value = depthFraction;
    });
    return direction;
  }

  /// Resets navigation depth state. Call on logout to prevent
  /// stale depth from affecting the first transition after re-login.
  static void reset() {
    _previousDepth = 0;
    _lastDirection = const Offset(0, 1);
    depthNotifier.value = 0.0;
  }
}
