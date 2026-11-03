import 'package:buoy_ai/models/covering_transfer_models.dart';
import 'package:buoy_ai/models/savings_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:flutter_test/flutter_test.dart';

/// Savings draw settlement (backend 0039). A purchase paid from a goal that lives
/// in a real account is a draw: in flight until a transfer out of that account
/// covers it. These pin the JSON contract and the two getters the sheet keys on.
void main() {
  Map<String, dynamic> purchase({
    int? goalAccountId = 5,
    int? covering,
    bool income = false,
  }) =>
      <String, dynamic>{
        'id': 1385,
        'accountId': 10,
        'amount': income ? 380.18 : -380.18,
        'type': income ? 1 : 0,
        'description': 'Big O Tires',
        'categoryName': 'Auto',
        'date': '2026-09-06T00:00:00Z',
        'accountName': 'CREDIT CARD',
        'savingsGoalId': 4,
        'savingsGoalName': 'Emergency Fund',
        'savingsGoalAccountId': goalAccountId,
        'coveringTransferTransactionId': covering,
        if (covering != null)
          'coveringTransfer': {
            'transactionId': covering,
            'date': '2026-09-12T00:00:00Z',
            'amount': -881.73,
            'accountName': 'EMERGENCY',
            'title': 'Transfer to checking',
          },
      };

  group('Transaction draw getters', () {
    test('an expense earmarked to a linked goal is a draw in flight', () {
      final tx = Transaction.fromJson(purchase());
      expect(tx.isDrawFromLinkedGoal, isTrue);
      expect(tx.isCoveredByTransfer, isFalse);
      expect(tx.coveringTransfer, isNull);
    });

    test('a covered draw carries the transfer summary', () {
      final tx = Transaction.fromJson(purchase(covering: 1500));
      expect(tx.isCoveredByTransfer, isTrue);
      expect(tx.coveringTransfer!.transactionId, 1500);
      expect(tx.coveringTransfer!.accountName, 'EMERGENCY');
      expect(tx.coveringTransfer!.amount, -881.73);
    });

    test('an unlinked goal has nothing to settle', () {
      final tx = Transaction.fromJson(purchase(goalAccountId: null));
      expect(tx.isDrawFromLinkedGoal, isFalse);
    });

    test('income earmarks deposit, they are never draws', () {
      final tx = Transaction.fromJson(purchase(income: true));
      expect(tx.isDrawFromLinkedGoal, isFalse);
    });

    test('accepts PascalCase keys from older payload shapes', () {
      final tx = Transaction.fromJson({
        ...purchase(),
        'savingsGoalAccountId': null,
        'SavingsGoalAccountId': 5,
        'CoveringTransferTransactionId': 9,
      });
      expect(tx.savingsGoalAccountId, 5);
      expect(tx.coveringTransferTransactionId, 9);
    });
  });

  group('CoveringTransferCandidates.fromJson', () {
    test('parses draws, candidates and the match flags', () {
      final data = CoveringTransferCandidates.fromJson({
        'savingsGoalId': 4,
        'savingsGoalName': 'Emergency Fund',
        'accountId': 5,
        'accountName': 'EMERGENCY',
        'pendingDraws': [
          {
            'transactionId': 1385,
            'date': '2026-09-06T00:00:00Z',
            'amount': 380.18,
            'title': 'Big O Tires'
          },
          {
            'transactionId': 1394,
            'date': '2026-09-06T00:00:00Z',
            'amount': 501.55,
            'title': 'Big O Tires'
          },
        ],
        'pendingTotal': 881.73,
        'candidates': [
          {
            'transactionId': 1500,
            'date': '2026-09-12T00:00:00Z',
            'amount': 881.73,
            'title': 'Transfer to checking',
            'categoryName': 'Transfer',
            'matchesThis': false,
            'matchesPendingTotal': true,
          },
          {
            'transactionId': 1501,
            'date': '2026-09-11T00:00:00Z',
            'amount': 20,
            'title': 'Fee'
          },
        ],
      });
      expect(data.pendingDraws, hasLength(2));
      expect(data.pendingTotal, 881.73);
      expect(data.candidates.first.isExactMatch, isTrue);
      expect(data.candidates.last.isExactMatch, isFalse);
      expect(data.candidates.last.categoryName, isNull);
    });

    test('tolerates an empty payload', () {
      final data = CoveringTransferCandidates.fromJson({});
      expect(data.candidates, isEmpty);
      expect(data.pendingDraws, isEmpty);
      expect(data.accountName, '');
    });
  });

  group('SavingsGoal.pendingTransferAmount', () {
    test('surfaces in-flight money and the getter', () {
      final goal = SavingsGoal.fromJson({
        'id': 4,
        'name': 'Emergency Fund',
        'targetAmount': 15000,
        'currentAmount': 247.01,
        'allocationPercentage': 0,
        'createdAt': '2026-01-01T00:00:00Z',
        'accountId': 5,
        'accountBalance': 1128.74,
        'accountUnallocated': 881.73,
        'pendingTransferAmount': 881.73,
      });
      expect(goal.hasMoneyInFlight, isTrue);
      expect(goal.pendingTransferAmount, 881.73);
    });

    test('is absent for an unlinked goal', () {
      final goal = SavingsGoal.fromJson({
        'id': 5,
        'name': 'Vacation',
        'targetAmount': 3500,
        'currentAmount': 300,
        'allocationPercentage': 0,
        'createdAt': '2026-01-01T00:00:00Z',
      });
      expect(goal.hasMoneyInFlight, isFalse);
    });
  });
}
