import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/savings_models.dart';

void main() {
  group('SavingsGoalType contract', () {
    // The type is sent to the backend as its enum index (savings_service sends
    // `type.index`). The backend SavingsGoalType enum only defines Personal=0 and
    // Family=1, so the Flutter enum must not offer any index the backend can't
    // represent — an out-of-range value would match neither budget scope and the
    // goal would silently receive no auto-allocations.
    test('has exactly the two members the backend enum defines', () {
      expect(SavingsGoalType.values, hasLength(2));
      expect(SavingsGoalType.personal.index, 0);
      expect(SavingsGoalType.family.index, 1);
    });
  });

  group('SavingsGoal.fromJson', () {
    Map<String, dynamic> baseJson() => <String, dynamic>{
          'id': 1,
          'type': 1,
          'userId': 2,
          'name': 'Vacation',
          'targetAmount': 1000,
          'currentAmount': 250,
          'allocationPercentage': 10,
          'createdAt': '2026-01-01T00:00:00Z',
        };

    test('maps the backend type index onto the collapsed enum', () {
      final goal = SavingsGoal.fromJson(baseJson());
      expect(goal.type, SavingsGoalType.family);
    });

    test('parses a goal without a monthlyContribution key (phantom field removed)', () {
      // Backend SavingsGoalDto never serializes monthlyContribution; parsing must
      // succeed without it.
      final goal = SavingsGoal.fromJson(baseJson());
      expect(goal.id, 1);
      expect(goal.targetAmount, 1000);
      expect(goal.currentAmount, 250);
    });
  });

  group('SavingsGoal account link', () {
    Map<String, dynamic> base(Map<String, dynamic> extra) => {
          'id': 1,
          'name': 'Emergency',
          'targetAmount': 5000,
          'currentAmount': 2000,
          'createdAt': '2026-01-01T00:00:00Z',
          ...extra,
        };

    test('parses reconciliation fields in either casing', () {
      final camel = SavingsGoal.fromJson(base({
        'accountId': 9,
        'accountName': 'Ally Savings',
        'accountBalance': 2180,
        'accountAllocatedToGoals': 2400,
        'accountUnallocated': -220,
      }));
      expect(camel.accountName, 'Ally Savings');
      expect(camel.accountBalance, 2180);
      expect(camel.accountUnallocated, -220);

      final pascal = SavingsGoal.fromJson(base({
        'AccountId': 9,
        'AccountName': 'Ally Savings',
        'AccountUnallocated': -220,
      }));
      expect(pascal.accountId, 9);
      expect(pascal.accountName, 'Ally Savings');
      expect(pascal.accountUnallocated, -220);
    });

    test('an unlinked goal is a normal goal, not a broken one', () {
      final goal = SavingsGoal.fromJson(base({}));
      expect(goal.accountId, isNull);
      expect(goal.isLinkedToAccount, isFalse);
      // Nothing to reconcile means nothing to warn about.
      expect(goal.isOverAllocated, isFalse);
    });

    test('only a negative remainder counts as over-allocated', () {
      // Money in the account that no goal claims is ordinary, not a problem.
      expect(
        SavingsGoal.fromJson(base({'accountId': 9, 'accountUnallocated': 500}))
            .isOverAllocated,
        isFalse,
      );
      // Goals promising more than the account holds is the case worth flagging.
      expect(
        SavingsGoal.fromJson(base({'accountId': 9, 'accountUnallocated': -220}))
            .isOverAllocated,
        isTrue,
      );
      // Exactly balanced is fine.
      expect(
        SavingsGoal.fromJson(base({'accountId': 9, 'accountUnallocated': 0}))
            .isOverAllocated,
        isFalse,
      );
    });
  });

  group('ReconcileResult', () {
    test('parses the outcome and treats a surplus as fine', () {
      final r = ReconcileResult.fromJson({
        'accountBalance': 3000,
        'allocatedToGoals': 2500,
        'unallocated': 500,
        'goals': <Map<String, dynamic>>[],
      });
      expect(r.accountBalance, 3000);
      expect(r.unallocated, 500);
      // Money no goal claims is a legitimate end state, not a failure.
      expect(r.isOverAllocated, isFalse);
    });

    test('flags only the case where goals outrun the balance', () {
      final r = ReconcileResult.fromJson({
        'accountBalance': 2180,
        'allocatedToGoals': 2400,
        'unallocated': -220,
        'goals': <Map<String, dynamic>>[],
      });
      expect(r.isOverAllocated, isTrue);
    });

    test('handles PascalCase and a missing goals list', () {
      final r = ReconcileResult.fromJson({
        'AccountBalance': 100,
        'AllocatedToGoals': 100,
        'Unallocated': 0,
      });
      expect(r.accountBalance, 100);
      expect(r.goals, isEmpty);
      expect(r.isOverAllocated, isFalse);
    });
  });
}
