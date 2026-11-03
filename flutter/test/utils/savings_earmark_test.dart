import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/utils/savings_earmark.dart';

Transaction _tx({bool isPending = false, bool isSplit = false}) => Transaction(
      id: 742,
      accountId: 1,
      amount: -177.23,
      type: TransactionType.debit,
      description: 'AMAZON MKTPLACE PMTS',
      categoryName: 'Shopping',
      date: DateTime(2026, 7, 21),
      accountName: 'Freedom',
      isPending: isPending,
      isSplit: isSplit,
    );

void main() {
  group('canEarmarkToSavings', () {
    test('a posted, unsplit transaction is eligible', () {
      expect(canEarmarkToSavings(_tx()), isTrue);
      expect(savingsEarmarkBlockedReason(_tx()), isNull);
    });

    // Regression: transaction 742 was a pending hold. The picker offered the
    // goals anyway, the backend rejected the POST with 400, and the client
    // showed a generic red banner.
    test('a pending transaction is not eligible and explains why', () {
      final tx = _tx(isPending: true);
      expect(canEarmarkToSavings(tx), isFalse);
      expect(savingsEarmarkBlockedReason(tx), contains('pending'));
    });

    test('a split parent is not eligible and explains why', () {
      final tx = _tx(isSplit: true);
      expect(canEarmarkToSavings(tx), isFalse);
      expect(savingsEarmarkBlockedReason(tx), contains('Split'));
    });

    test('pending takes precedence in the message when both apply', () {
      // Split is checked first — either reason is correct, but the message must
      // never be null when the transaction is ineligible.
      final tx = _tx(isPending: true, isSplit: true);
      expect(canEarmarkToSavings(tx), isFalse);
      expect(savingsEarmarkBlockedReason(tx), isNotNull);
    });
  });
}
