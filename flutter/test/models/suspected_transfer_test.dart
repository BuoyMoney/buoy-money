import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/models/suspected_transfer.dart';

void main() {
  group('SuspectedTransfer.fromJson', () {
    test('parses a matched pair with both legs', () {
      final t = SuspectedTransfer.fromJson({
        'confidence': 'medium',
        'reason': 'Same amount moved between accounts.',
        'sides': [
          {
            'transactionId': 1,
            'accountId': 10,
            'accountName': 'Checking',
            'amount': -2000.0,
            'date': '2026-07-01T00:00:00Z',
            'merchantName': 'Transfer',
          },
          {
            'transactionId': 2,
            'accountId': 20,
            'accountName': 'Savings',
            'amount': 2000.0,
            'date': '2026-07-01T00:00:00Z',
            'merchantName': 'Transfer',
          },
        ],
      });

      expect(t.confidence, 'medium');
      expect(t.sides, hasLength(2));
      expect(t.transactionIds, [1, 2]);
      expect(t.sides.first.accountName, 'Checking');
    });

    test('handles a single-leg suspect with missing fields gracefully', () {
      final t = SuspectedTransfer.fromJson({
        'confidence': 'low',
        'reason': 'Bank classified this as a transfer.',
        'sides': [
          {'transactionId': 7, 'amount': -750},
        ],
      });

      expect(t.sides, hasLength(1));
      expect(t.transactionIds, [7]);
      expect(t.sides.first.accountName, '');
      expect(t.sides.first.amount, -750.0);
    });
  });
}
