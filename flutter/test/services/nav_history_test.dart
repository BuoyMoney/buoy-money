import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/nav_history.dart';

void main() {
  group('NavHistory', () {
    late NavHistory history;

    setUp(() => history = NavHistory());

    test('starts empty and cannot go back', () {
      expect(history.stack, isEmpty);
      expect(history.canGoBack, isFalse);
      expect(history.current, isNull);
      expect(history.goBack(), isNull);
    });

    test('records tab visits in order', () {
      history.visit('/dashboard');
      history.visit('/transactions');
      history.visit('/budgets');
      expect(history.stack, ['/dashboard', '/transactions', '/budgets']);
      expect(history.current, '/budgets');
      expect(history.canGoBack, isTrue);
    });

    test('ignores non-tab routes (pushed sub-pages / modals)', () {
      history.visit('/dashboard');
      history.visit('/billing'); // not a tab — a pushed sub-page
      history.visit('/transactions/123'); // not a tab
      expect(history.stack, ['/dashboard']);
    });

    test('collapses a repeat of the current tab (no self-loop)', () {
      history.visit('/dashboard');
      history.visit('/dashboard');
      history.visit('/dashboard');
      expect(history.stack, ['/dashboard']);
    });

    test('goBack pops the top and returns the new current', () {
      history.visit('/dashboard');
      history.visit('/transactions');
      history.visit('/budgets');
      expect(history.goBack(), '/transactions');
      expect(history.current, '/transactions');
      expect(history.goBack(), '/dashboard');
      expect(history.current, '/dashboard');
      // At the last entry there is nowhere left to go.
      expect(history.goBack(), isNull);
      expect(history.current, '/dashboard');
    });

    test('back-then-visit does not re-push the tab we returned to', () {
      // Mirrors the runtime loop: goBack() -> context.go(prev) -> build() -> visit(prev).
      // visit must no-op on the now-current tab or the stack never shrinks.
      history.visit('/dashboard');
      history.visit('/transactions');
      history.visit('/budgets');

      final prev = history.goBack(); // -> /transactions
      expect(prev, '/transactions');
      history.visit(prev!); // the rebuild re-visits it
      expect(history.stack, ['/dashboard', '/transactions']);
    });

    test('re-visiting an earlier tab appends (does not dedupe deep entries)', () {
      // A->B->A is a real forward navigation; back from the second A returns to B.
      history.visit('/dashboard');
      history.visit('/transactions');
      history.visit('/dashboard');
      expect(history.stack, ['/dashboard', '/transactions', '/dashboard']);
      expect(history.goBack(), '/transactions');
    });

    test('caps depth so toggling cannot grow without bound', () {
      // Alternate two tabs well past the cap.
      for (var i = 0; i < NavHistory.maxDepth * 2; i++) {
        history.visit(i.isEven ? '/dashboard' : '/transactions');
      }
      expect(history.stack.length, lessThanOrEqualTo(NavHistory.maxDepth));
    });

    test('clear empties the stack', () {
      history.visit('/dashboard');
      history.visit('/transactions');
      history.clear();
      expect(history.stack, isEmpty);
      expect(history.canGoBack, isFalse);
    });

    test('startDestination is a known tab route', () {
      expect(NavHistory.tabRoutes, contains(NavHistory.startDestination));
    });
  });
}
