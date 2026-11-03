import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:google_fonts/google_fonts.dart';

import 'package:buoy_ai/l10n/generated/app_localizations.dart';
import 'package:buoy_ai/services/ai_service.dart';
import 'package:buoy_ai/widgets/ai/tool_action_cards.dart';

/// The AI chat's Confirm card hid the change behind "Show Details" (raw keys like
/// categoryId and newAmount), "Actions taken" said Complete before the user had
/// confirmed anything, and the bubble kept asking "Would you like me to proceed"
/// after it was answered. Seen while planning a budget-change film, 2026-10-05.

// What update_budget_amount returns before the user confirms (BudgetWriteTools.cs).
final _budgetPreview = jsonEncode({
  'RequiresConfirmation': true,
  'ActionType': 'update_budget_amount',
  'Preview': {
    'CategoryName': 'Dining Out',
    'CurrentAmount': 450.0,
    'NewAmount': 300.0,
    'Change': -150.0,
    'ChangePercent': -33.3,
    'ApplyToFuture': false,
    'BudgetName': 'Main',
  },
  'Description': "Change 'Dining Out' from \$450.00 to \$300.00/month",
});

ToolCallResult _pending(String result,
        {String tool = 'update_budget_amount'}) =>
    ToolCallResult(
      toolName: tool,
      arguments: {'categoryId': 5, 'newAmount': 300, 'confirmed': false},
      result: result,
      requiresConfirmation: true,
    );

Future<void> _pump(WidgetTester tester, Widget child) {
  return tester.pumpWidget(MaterialApp(
    localizationsDelegates: AppLocalizations.localizationsDelegates,
    supportedLocales: AppLocalizations.supportedLocales,
    home: Scaffold(body: SingleChildScrollView(child: child)),
  ));
}

void main() {
  setUp(() {
    // GoogleFonts would try to fetch over the network under test; keep it offline.
    GoogleFonts.config.allowRuntimeFetching = false;
  });

  group('Confirm card', () {
    testWidgets('shows the change and the amounts before and after',
        (tester) async {
      await _pump(
        tester,
        ToolConfirmationCard(
          toolResult: _pending(_budgetPreview),
          isLoading: false,
          onConfirm: () {},
          onCancel: () {},
        ),
      );

      expect(find.text("Change 'Dining Out' from \$450.00 to \$300.00/month"),
          findsOneWidget);
      expect(find.text('\$450.00'), findsOneWidget);
      expect(find.text('\$300.00'), findsOneWidget);
      expect(find.textContaining('The AI would like to'), findsNothing);
      // Raw argument keys stay behind Show Details.
      expect(find.text('categoryId'), findsNothing);
    });

    testWidgets('Show Details lists the arguments, without "confirmed"',
        (tester) async {
      await _pump(
        tester,
        ToolConfirmationCard(
          toolResult: _pending(_budgetPreview),
          isLoading: false,
          onConfirm: () {},
          onCancel: () {},
        ),
      );

      await tester.tap(find.text('Show Details'));
      await tester.pump();

      expect(find.text('categoryId'), findsOneWidget);
      expect(find.text('confirmed'), findsNothing);
    });

    testWidgets('money added to a goal shows the total before and after',
        (tester) async {
      final preview = jsonEncode({
        'RequiresConfirmation': true,
        'Preview': {
          'GoalName': 'Japan Trip',
          'CurrentAmount': 1200,
          'AddingAmount': 300,
          'NewTotal': 1500,
        },
        'Description': "Add \$300.00 to 'Japan Trip'",
      });
      await _pump(
        tester,
        ToolConfirmationCard(
          toolResult: _pending(preview, tool: 'add_to_savings_goal'),
          isLoading: false,
          onConfirm: () {},
          onCancel: () {},
        ),
      );

      expect(find.text('\$1,200.00'), findsOneWidget);
      expect(find.text('\$1,500.00'), findsOneWidget);
    });

    testWidgets('a delete shows its warning', (tester) async {
      final preview = jsonEncode({
        'RequiresConfirmation': true,
        'Warning': 'This action CANNOT be undone!',
        'Preview': {'TransactionId': 9, 'Amount': -42.5},
        'Description':
            'Delete transaction: Corner Cafe (\$42.50) from Oct 02, 2026',
      });
      await _pump(
        tester,
        ToolConfirmationCard(
          toolResult: _pending(preview, tool: 'delete_transaction'),
          isLoading: false,
          onConfirm: () {},
          onCancel: () {},
        ),
      );

      expect(find.text('This action CANNOT be undone!'), findsOneWidget);
      // A single amount is not a before and after.
      expect(find.byIcon(Icons.arrow_forward), findsNothing);
    });

    testWidgets('without a description it still names the action',
        (tester) async {
      await _pump(
        tester,
        ToolConfirmationCard(
          toolResult: _pending('not json'),
          isLoading: false,
          onConfirm: () {},
          onCancel: () {},
        ),
      );

      expect(find.text('The AI would like to: Update Budget Amount'),
          findsOneWidget);
    });
  });

  group('Actions taken', () {
    ToolCallResult ran(String tool, String result) => ToolCallResult(
          toolName: tool,
          arguments: const {},
          result: result,
          requiresConfirmation: false,
        );

    testWidgets('a preview waiting for Confirm is not listed as Complete',
        (tester) async {
      await _pump(
        tester,
        ToolActionSummary(toolResults: [
          ran('get_budgets', '[]'),
          ran('update_budget_amount', _budgetPreview),
        ]),
      );

      expect(find.text('Retrieved Budgets'), findsOneWidget);
      expect(find.text('Update Budget Amount'), findsNothing);
      expect(find.text('Complete'), findsNothing);
    });

    testWidgets('a confirmed write shows one Complete row', (tester) async {
      await _pump(
        tester,
        ToolActionSummary(toolResults: [
          ran('update_budget_amount', _budgetPreview),
          ran('update_budget_amount',
              jsonEncode({'Success': true, 'Message': 'Updated!'})),
        ]),
      );

      expect(find.text('Update Budget Amount'), findsOneWidget);
      expect(find.text('Complete'), findsOneWidget);
    });

    testWidgets('only a preview means nothing to show', (tester) async {
      await _pump(
        tester,
        ToolActionSummary(
            toolResults: [ran('update_budget_amount', _budgetPreview)]),
      );

      expect(find.text('ACTIONS TAKEN'), findsNothing);
    });

    testWidgets('a read tool without get_ is a chip, not a Complete write',
        (tester) async {
      await _pump(
        tester,
        ToolActionSummary(toolResults: [ran('search_help', '[]')]),
      );

      expect(find.text('Complete'), findsNothing);
    });
  });

  group('After the user answers', () {
    const asked = "📋 **Confirmation Required**\n\n"
        "Change 'Dining Out' from \$450.00 to \$300.00/month\n\n"
        'Would you like me to proceed with this action?';

    test('Confirm replaces the question with the result', () {
      final outcome = ToolOutcome.fromResult(jsonEncode({
        'Success': true,
        'Message': "Updated 'Dining Out' to \$300.00/month!",
      }));

      expect(outcome.success, isTrue);
      expect(
        settleConfirmationText(asked, '✅ ${outcome.message}'),
        "Change 'Dining Out' from \$450.00 to \$300.00/month\n\n"
        "✅ Updated 'Dining Out' to \$300.00/month!",
      );
    });

    test('a tool that refused is reported as a failure, not success', () {
      final outcome = ToolOutcome.fromResult(jsonEncode({
        'Error': "Budget category not found or you don't have access to it."
      }));

      expect(outcome.success, isFalse);
      expect(outcome.message, contains('not found'));
    });

    test('Cancel replaces the question too', () {
      expect(
        settleConfirmationText(asked, '❌ Action cancelled.'),
        "Change 'Dining Out' from \$450.00 to \$300.00/month\n\n"
        '❌ Action cancelled.',
      );
    });

    test('a result that is not JSON still counts as done', () {
      final outcome = ToolOutcome.fromResult('ok');
      expect(outcome.success, isTrue);
      expect(outcome.message, 'Done.');
    });
  });
}
