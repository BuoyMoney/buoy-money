import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/nav_destination.dart';
import 'package:buoy_ai/utils/navigation_depth.dart';

/// Transition direction is derived from [kNavDestinations] rather than a
/// hand-maintained table, because the hand-maintained one drifted: the menu
/// listed Achievements above AI Chat while the table ranked Achievements last,
/// so moving between them animated backwards.
void main() {
  // getSlideDirection defers its depth-notifier write to a post-frame callback,
  // which needs a binding.
  TestWidgetsFlutterBinding.ensureInitialized();

  const dive = Offset(0, 1); // deeper — new page rises from below
  const surface = Offset(0, -1); // shallower — new page drops from above

  setUp(NavigationDepth.reset);

  String pathOf(String key) =>
      kNavDestinations.firstWhere((d) => d.key == key).path;

  group('depth follows menu order', () {
    test('every destination is strictly deeper than the one above it', () {
      int? previous;
      for (final d in kNavDestinations) {
        final depth = NavigationDepth.depthOf(d.path);
        expect(depth, isNotNull, reason: '${d.path} has no depth');
        if (previous != null) {
          expect(depth!, greaterThan(previous),
              reason: '${d.path} must be deeper than the entry above it');
        }
        previous = depth;
      }
    });

    test('achievements -> ai-chat dives, matching the menu', () {
      // The reported bug: AI Chat sits BELOW Achievements in the menu, so moving
      // to it must dive. The old table gave achievements depth 15 and ai-chat 7,
      // which surfaced instead.
      expect(kNavDestinations.indexWhere((d) => d.key == 'ai-chat'),
          greaterThan(kNavDestinations.indexWhere((d) => d.key == 'achievements')),
          reason: 'this test encodes the menu order it is checking against');

      NavigationDepth.getSlideDirection(pathOf('achievements'));
      expect(NavigationDepth.getSlideDirection(pathOf('ai-chat')), dive);
    });

    test('ai-chat -> achievements surfaces, the exact reverse', () {
      NavigationDepth.getSlideDirection(pathOf('ai-chat'));
      expect(NavigationDepth.getSlideDirection(pathOf('achievements')), surface);
    });

    test('dashboard -> settings dives, settings -> dashboard surfaces', () {
      NavigationDepth.getSlideDirection(pathOf('dashboard'));
      expect(NavigationDepth.getSlideDirection(pathOf('settings')), dive);
      expect(NavigationDepth.getSlideDirection(pathOf('dashboard')), surface);
    });
  });

  group('sub-routes', () {
    test('a child sits deeper than its parent but above the next destination', () {
      final settings = NavigationDepth.depthOf('/settings')!;
      final child = NavigationDepth.depthOf('/settings/email-preferences')!;

      expect(child, greaterThan(settings));

      // Must not leapfrog whatever follows Settings in the menu, or leaving the
      // child would animate the wrong way.
      final settingsIndex =
          kNavDestinations.indexWhere((d) => d.path == '/settings');
      if (settingsIndex + 1 < kNavDestinations.length) {
        final next =
            NavigationDepth.depthOf(kNavDestinations[settingsIndex + 1].path)!;
        expect(child, lessThan(next));
      }
    });

    test('entering a child dives and leaving it surfaces', () {
      NavigationDepth.getSlideDirection('/profile');
      expect(NavigationDepth.getSlideDirection('/profile/trusted-devices'), dive);
      expect(NavigationDepth.getSlideDirection('/profile'), surface);
    });

    test('longest matching parent wins', () {
      expect(NavigationDepth.depthOf('/profile/trusted-devices'),
          greaterThan(NavigationDepth.depthOf('/profile')!));
    });
  });

  group('routes with no menu entry', () {
    test('are unplaceable on their own', () {
      // /billing and /family are reachable from other pages but are not menu items.
      expect(NavigationDepth.depthOf('/billing'), isNull);
      expect(NavigationDepth.depthOf('/family'), isNull);
    });

    test('always dive in, and surface on the way back', () {
      NavigationDepth.getSlideDirection(pathOf('settings'));
      expect(NavigationDepth.getSlideDirection('/billing'), dive);
      // Returning to a real menu page must surface, which only works because the
      // detail page recorded its own (deeper) depth on the way in.
      expect(NavigationDepth.getSlideDirection(pathOf('settings')), surface);
    });

    test('diving in from the deepest menu page still dives', () {
      NavigationDepth.getSlideDirection(kNavDestinations.last.path);
      expect(NavigationDepth.getSlideDirection('/ai-quiz'), dive);
    });
  });

  test('reset clears depth so the next login starts at the surface', () {
    NavigationDepth.getSlideDirection(pathOf('settings'));
    NavigationDepth.reset();

    expect(NavigationDepth.lastDirection, dive);
    expect(NavigationDepth.depthNotifier.value, 0.0);
    // From a clean slate, dashboard is the surface: going anywhere else dives.
    expect(NavigationDepth.getSlideDirection(pathOf('transactions')), dive);
  });
}
