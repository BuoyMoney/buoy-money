import 'package:buoy_ai/services/ai_service.dart';
import 'package:flutter_test/flutter_test.dart';

/// The server only runs an AI write when the user taps Confirm, and binds that
/// tap to the preview with a one-time token. The app has to carry it through.
void main() {
  test('a write preview keeps its confirmation token', () {
    final result = ToolCallResult.fromJson({
      'toolName': 'update_budget_amount',
      'arguments': {'categoryId': 5, 'newAmount': 400},
      'result': '{"RequiresConfirmation":true}',
      'requiresConfirmation': true,
      'confirmationToken': '6f1c2a0e-1b7d-4c55-9f0e-2d4b8a7c3e11',
    });

    expect(result.requiresConfirmation, isTrue);
    expect(result.confirmationToken, '6f1c2a0e-1b7d-4c55-9f0e-2d4b8a7c3e11');
  });

  test('a preview from an older server has no token', () {
    final result = ToolCallResult.fromJson({
      'toolName': 'update_budget_amount',
      'arguments': {'categoryId': 5},
      'requiresConfirmation': true,
    });

    expect(result.confirmationToken, isNull);
  });
}
