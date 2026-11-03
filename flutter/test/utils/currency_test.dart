import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/dashboard_models.dart';
import 'package:buoy_ai/models/transaction_models.dart';
import 'package:buoy_ai/utils/currency.dart';
import 'package:buoy_ai/utils/formatters.dart';

Account _account({
  required int id,
  required int type,
  required double balance,
  String currency = 'USD',
}) {
  return Account(
    id: id,
    plaidAccountId: 'plaid-$id',
    name: 'Account $id',
    type: type,
    subtype: 'checking',
    currentBalance: balance,
    currency: currency,
  );
}

void main() {
  // The primary currency is process-global; never leak one test's setting.
  tearDown(() => setPrimaryCurrency(null));

  group('setPrimaryCurrency', () {
    test('formatters and partitioning follow the user currency', () {
      setPrimaryCurrency('CAD');
      expect(primaryCurrency, 'CAD');
      expect(isPrimaryCurrency('cad'), isTrue);
      expect(isPrimaryCurrency('USD'), isFalse);
      expect(formatCurrency(1234.5), 'CA\$1,234.50');
      expect(formatCompactCurrency(1500), 'CA\$1.5K');
      expect(formatCurrencyLive(12.3456), 'CA\$12.3456');
    });

    test('null/empty resets to USD (logout, older backends)', () {
      setPrimaryCurrency('EUR');
      setPrimaryCurrency(null);
      expect(primaryCurrency, 'USD');
      expect(formatCurrency(1.0), '\$1.00');
      // The live-ticker cache must rebuild after the currency changes back.
      expect(formatCurrencyLive(1.0), '\$1.0000');
    });
  });

  group('parseFlexibleAmount', () {
    test('accepts both decimal conventions', () {
      expect(parseFlexibleAmount('1,234.56'), 1234.56);
      expect(parseFlexibleAmount('1.234,56'), 1234.56);
      expect(parseFlexibleAmount('12,50'), 12.50);
      expect(parseFlexibleAmount('12.50'), 12.50);
      expect(parseFlexibleAmount('12,500'), 12500);
      expect(parseFlexibleAmount('1.234.567,89'), 1234567.89);
    });

    test('tolerates symbols, spaces, and negatives', () {
      expect(parseFlexibleAmount(r'$1,234.56'), 1234.56);
      expect(parseFlexibleAmount('€ 12,50'), 12.50);
      expect(parseFlexibleAmount('-42'), -42);
      expect(parseFlexibleAmount('  7  '), 7);
    });

    test('rejects garbage and empties', () {
      expect(parseFlexibleAmount(''), isNull);
      expect(parseFlexibleAmount('abc'), isNull);
      expect(parseFlexibleAmount(null), isNull);
    });
  });

  group('isPrimaryCurrency', () {
    test('USD, null, and empty count as primary', () {
      expect(isPrimaryCurrency('USD'), isTrue);
      expect(isPrimaryCurrency('usd'), isTrue);
      expect(isPrimaryCurrency(null), isTrue);
      expect(isPrimaryCurrency(''), isTrue);
    });

    test('foreign codes are not primary', () {
      expect(isPrimaryCurrency('CAD'), isFalse);
      expect(isPrimaryCurrency('eur'), isFalse);
    });
  });

  group('splitByCurrency', () {
    test('sums primary and buckets each foreign code separately', () {
      final accounts = [
        _account(id: 1, type: 0, balance: 100.0),
        _account(id: 2, type: 0, balance: 50.0, currency: 'usd'),
        _account(id: 3, type: 0, balance: 200.0, currency: 'CAD'),
        _account(id: 4, type: 0, balance: 25.0, currency: 'cad'),
        _account(id: 5, type: 0, balance: 10.0, currency: 'EUR'),
      ];
      final split = splitByCurrency(
        accounts,
        currencyOf: (a) => a.currency,
        amountOf: (a) => a.currentBalance,
      );
      expect(split.primaryTotal, 150.0);
      expect(split.otherTotals, {'CAD': 225.0, 'EUR': 10.0});
      expect(split.hasOtherCurrencies, isTrue);
      expect(split.otherCodes, ['CAD', 'EUR']);
    });

    test('all-primary input has no foreign buckets', () {
      final split = splitByCurrency(
        [_account(id: 1, type: 0, balance: 5.0)],
        currencyOf: (a) => a.currency,
        amountOf: (a) => a.currentBalance,
      );
      expect(split.primaryTotal, 5.0);
      expect(split.hasOtherCurrencies, isFalse);
    });
  });

  group('formatCurrencyCode', () {
    test('USD and missing codes render like formatCurrency', () {
      expect(formatCurrencyCode(1234.5, 'USD'), formatCurrency(1234.5));
      expect(formatCurrencyCode(1234.5, null), formatCurrency(1234.5));
      expect(formatCurrencyCode(1234.5, ''), formatCurrency(1234.5));
    });

    test('foreign codes use their own symbol, not the dollar sign', () {
      expect(formatCurrencyCode(1234.5, 'CAD'), 'CA\$1,234.50');
      expect(formatCurrencyCode(88.0, 'EUR'), '€88.00');
      // Unmapped codes surface the code itself rather than lying with $.
      expect(formatCurrencyCode(12.0, 'SEK'), 'SEK 12.00');
      expect(formatCurrencyCode(1.0, 'XTS'), isNot(contains('\$')));
    });
  });

  group('DashboardData.fromAggregated currency partitioning', () {
    test('foreign-currency liabilities stay out of debt totals and are reported', () {
      final data = DashboardData.fromAggregated(
        totalBalance: 100.0,
        totalIncome: 0,
        totalExpenses: 0,
        accounts: [
          _account(id: 1, type: 2, balance: -40.0),
          _account(id: 2, type: 2, balance: -1000.0, currency: 'CAD'),
          _account(id: 3, type: 4, balance: -60.0),
          _account(id: 4, type: 0, balance: 500.0, currency: 'EUR'),
        ],
        recentTransactions: const [],
      );
      expect(data.creditUsed, 40.0);
      expect(data.loanBalance, 60.0);
      expect(data.totalDebt, 100.0);
      expect(data.foreignCurrencyCodes, ['CAD', 'EUR']);
      expect(data.accountsCount, 4);
    });
  });

  group('Transaction.fromJson isoCurrencyCode', () {
    test('parses when present and stays null when absent', () {
      final withCode = Transaction.fromJson(const {
        'id': 1,
        'amount': -12.5,
        'type': 'debit',
        'isoCurrencyCode': 'CAD',
      });
      expect(withCode.isoCurrencyCode, 'CAD');

      final withoutCode = Transaction.fromJson(const {
        'id': 2,
        'amount': -12.5,
        'type': 'debit',
      });
      expect(withoutCode.isoCurrencyCode, isNull);
    });
  });

  group('Account.fromJson currency', () {
    test('parses camelCase and PascalCase, defaults USD', () {
      expect(Account.fromJson(const {'id': 1, 'currency': 'CAD'}).currency, 'CAD');
      expect(Account.fromJson(const {'id': 1, 'Currency': 'EUR'}).currency, 'EUR');
      expect(Account.fromJson(const {'id': 1}).currency, 'USD');
    });
  });
}
