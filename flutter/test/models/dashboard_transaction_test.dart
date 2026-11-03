import 'package:buoy_ai/models/dashboard_models.dart';
import 'package:flutter_test/flutter_test.dart';

/// Recent-transaction rows colour by income vs expense. The classification must trust the
/// credit/debit type, not the amount's sign — the backend can send an expense as a positive
/// magnitude (the seed data does, and any absolute-value payload would), and the old
/// `type == 'credit' || amount > 0` then coloured every such row green as if it were income.
DashboardTransaction _tx({required double amount, required String type}) => DashboardTransaction(
      id: 1,
      description: 'x',
      amount: amount,
      category: 'c',
      date: DateTime(2026, 1, 1),
      type: type,
    );

void main() {
  test('a debit is an expense even when its amount is positive', () {
    final t = _tx(amount: 245.67, type: 'debit'); // Whole Foods, seed-style unsigned
    expect(t.isExpense, isTrue);
    expect(t.isIncome, isFalse);
  });

  test('a debit is an expense when its amount is negative (signed prod data)', () {
    final t = _tx(amount: -245.67, type: 'debit');
    expect(t.isExpense, isTrue);
  });

  test('a credit is income', () {
    expect(_tx(amount: 5000, type: 'credit').isIncome, isTrue);
  });

  test('unknown type falls back to the amount sign', () {
    expect(_tx(amount: 10, type: '').isIncome, isTrue);
    expect(_tx(amount: -10, type: '').isIncome, isFalse);
  });

  test('isIncome and isExpense are always opposite', () {
    for (final t in [
      _tx(amount: 245.67, type: 'debit'),
      _tx(amount: -245.67, type: 'debit'),
      _tx(amount: 5000, type: 'credit'),
      _tx(amount: 0, type: 'debit'),
    ]) {
      expect(t.isIncome, isNot(t.isExpense));
    }
  });
}
