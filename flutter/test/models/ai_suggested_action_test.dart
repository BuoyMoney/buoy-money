import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/services/ai_service.dart';

void main() {
  group('SuggestedAction.fromJson', () {
    test('parses a navigate action', () {
      final action = SuggestedAction.fromJson({
        'type': 'navigate',
        'label': 'Review these transactions',
        'route': '/transactions',
      });

      expect(action.isNavigate, isTrue);
      expect(action.isAgentHandoff, isFalse);
      expect(action.route, '/transactions');
      expect(action.label, 'Review these transactions');
    });

    test('parses an agent_handoff action', () {
      final action = SuggestedAction.fromJson({
        'type': 'agent_handoff',
        'label': 'Fix categories',
        'instruction': 'Recategorize the 3 Robinhood debits as Transfer',
        'summary': 'Marks 3 as transfers',
      });

      expect(action.isAgentHandoff, isTrue);
      expect(action.instruction, 'Recategorize the 3 Robinhood debits as Transfer');
      expect(action.summary, 'Marks 3 as transfers');
    });
  });

  group('AIInsight.fromJson', () {
    test('reads a nested suggestedAction', () {
      final insight = AIInsight.fromJson({
        'id': 'dashboard_insight_1',
        'type': 'spending',
        'title': 'Check this',
        'message': 'A large charge is unreviewed.',
        'confidence': 0.8,
        'priority': 'high',
        'suggestedAction': {
          'type': 'navigate',
          'label': 'Review',
          'route': '/transactions',
        },
      });

      expect(insight.suggestedAction, isNotNull);
      expect(insight.suggestedAction!.route, '/transactions');
    });

    test('is null when no suggestedAction present', () {
      final insight = AIInsight.fromJson({
        'id': 'x',
        'type': 'general',
        'title': 't',
        'message': 'm',
        'confidence': 0.5,
        'priority': 'low',
      });

      expect(insight.suggestedAction, isNull);
    });
  });

  group('InsightActionResult.fromJson', () {
    test('parses success with changes and undo token', () {
      final result = InsightActionResult.fromJson({
        'success': true,
        'summary': 'Recategorized 1 transaction.',
        'changes': [
          {'description': 'Category of "Store" back to Groceries'},
        ],
        'undoToken': 'abc.def',
      });

      expect(result.success, isTrue);
      expect(result.canUndo, isTrue);
      expect(result.changes, hasLength(1));
      expect(result.changes.first, contains('Groceries'));
    });

    test('canUndo is false without a token', () {
      final result = InsightActionResult.fromJson({
        'success': true,
        'summary': 'Nothing to change.',
      });

      expect(result.canUndo, isFalse);
      expect(result.changes, isEmpty);
    });
  });
}
