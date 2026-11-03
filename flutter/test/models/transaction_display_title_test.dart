import 'package:buoy_ai/models/transaction_models.dart';
import 'package:flutter_test/flutter_test.dart';

/// The title shown for a transaction.
///
/// The UI used to do `merchantName ?? description`, but the backend defaults both bank fields
/// to `string.Empty`, never null — so `??` never fired and a blank merchant rendered a blank
/// title even when the description had good text. Blank is the condition to test, not null.
Transaction _tx({
  String description = '',
  String? merchantName,
  String? nickname,
}) =>
    Transaction(
      id: 1,
      accountId: 1,
      amount: -25,
      type: TransactionType.debit,
      description: description,
      merchantName: merchantName,
      nickname: nickname,
      categoryName: 'Uncategorized',
      date: DateTime(2026, 1, 1),
      accountName: 'Checking',
    );

void main() {
  group('displayTitle', () {
    test('prefers the user rename over the bank fields', () {
      final tx = _tx(nickname: 'Roof repair', merchantName: "Sam's Club", description: 'SAMS CLUB #4791');

      expect(tx.displayTitle, 'Roof repair');
    });

    test('uses the merchant when there is no rename', () {
      expect(_tx(merchantName: "Sam's Club", description: 'SAMS CLUB #4791').displayTitle, "Sam's Club");
    });

    test('falls back to description when merchant is an EMPTY STRING', () {
      // The exact regression: `merchantName ?? description` returns '' here and renders blank.
      expect(_tx(merchantName: '', description: 'CHECK #1042').displayTitle, 'CHECK #1042');
    });

    test('falls back to description when merchant is null', () {
      expect(_tx(merchantName: null, description: 'CHECK #1042').displayTitle, 'CHECK #1042');
    });

    test('shows a placeholder rather than an empty title when the bank sent nothing', () {
      expect(_tx(merchantName: '', description: '').displayTitle, 'Unnamed transaction');
    });

    test('ignores a whitespace-only rename', () {
      expect(_tx(nickname: '   ', merchantName: "Sam's Club").displayTitle, "Sam's Club");
    });
  });

  group('bankName / hasName', () {
    test('bankName ignores the rename so the dialog can offer the original', () {
      final tx = _tx(nickname: 'Roof repair', merchantName: "Sam's Club");

      expect(tx.bankName, "Sam's Club");
      expect(tx.hasBankName, isTrue);
    });

    test('hasBankName is false when the bank sent nothing', () {
      final tx = _tx(merchantName: '', description: '');

      expect(tx.hasBankName, isFalse);
      expect(tx.hasName, isFalse);
    });

    test('hasName is true once the user renames a nameless transaction', () {
      final tx = _tx(merchantName: '', description: '', nickname: 'Roof repair');

      expect(tx.hasName, isTrue);
      expect(tx.hasBankName, isFalse);
    });
  });

  test('fromJson reads nickname', () {
    final tx = Transaction.fromJson(<String, dynamic>{
      'id': 1,
      'accountId': 1,
      'amount': -25,
      'type': 0,
      'description': 'CHECK #1042',
      'merchantName': '',
      'nickname': 'Roof repair',
      'categoryName': 'Uncategorized',
      'date': '2026-01-01T00:00:00Z',
      'accountName': 'Checking',
    });

    expect(tx.nickname, 'Roof repair');
    expect(tx.displayTitle, 'Roof repair');
  });
}
