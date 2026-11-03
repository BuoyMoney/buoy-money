import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/transaction_models.dart';

void main() {
  group('TransactionType.fromJson', () {
    test('maps backend integers (0=Debit, 1=Credit, 2=Transfer)', () {
      expect(TransactionType.fromJson(0), TransactionType.debit);
      expect(TransactionType.fromJson(1), TransactionType.credit);
      expect(TransactionType.fromJson(2), TransactionType.transfer);
    });

    test('accepts string names and numeric strings', () {
      expect(TransactionType.fromJson('credit'), TransactionType.credit);
      expect(TransactionType.fromJson('Transfer'), TransactionType.transfer);
      expect(TransactionType.fromJson('income'), TransactionType.credit);
      expect(TransactionType.fromJson('1'), TransactionType.credit);
    });

    test('falls back to debit for null / out-of-range / garbage', () {
      // Previously `TransactionType.values[json['type']]` would throw a
      // RangeError on an out-of-range index and crash the transactions list.
      expect(TransactionType.fromJson(null), TransactionType.debit);
      expect(TransactionType.fromJson(99), TransactionType.debit);
      expect(TransactionType.fromJson('nonsense'), TransactionType.debit);
    });
  });

  group('Transaction.fromJson defensive scalars', () {
    test('tolerates missing/null required scalars without throwing', () {
      final t = Transaction.fromJson({
        // id, accountId, amount and type all absent
        'date': '2026-07-11T00:00:00.000Z',
      });
      expect(t.id, 0);
      expect(t.accountId, 0);
      expect(t.amount, 0);
      expect(t.type, TransactionType.debit);
      expect(t.categoryName, 'Uncategorized');
    });

    test('parses a well-formed payload', () {
      final t = Transaction.fromJson({
        'id': 42,
        'accountId': 7,
        'amount': 12.5,
        'type': 1,
        'description': 'Paycheck',
        'categoryName': 'Salary',
        'date': '2026-07-11T00:00:00.000Z',
        'accountName': 'Checking',
      });
      expect(t.id, 42);
      expect(t.accountId, 7);
      expect(t.amount, 12.5);
      expect(t.type, TransactionType.credit);
      expect(t.isIncome, isTrue);
    });

    test('does not throw on a missing/null/invalid date (falls back to now)', () {
      // A single malformed date must degrade to one row, not crash the whole
      // TransactionResponse.fromJson map().
      final before = DateTime.now().subtract(const Duration(seconds: 5));
      for (final bad in <dynamic>[null, '', 'not-a-date']) {
        final t = Transaction.fromJson({'id': 1, 'date': bad});
        expect(t.date.isAfter(before), isTrue);
      }
      // Absent 'date' key entirely.
      final t = Transaction.fromJson({'id': 1});
      expect(t.date.isAfter(before), isTrue);
    });

    test('accepts a PascalCase Date key', () {
      final t = Transaction.fromJson({
        'id': 1,
        'Date': '2026-07-11T00:00:00.000Z',
      });
      expect(t.date.year, 2026);
      expect(t.date.month, 7);
      expect(t.date.day, 11);
    });
  });
}
