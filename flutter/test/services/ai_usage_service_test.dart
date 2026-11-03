import 'package:buoy_ai/services/ai_usage_service.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('formatMicros', () {
    test('rounds to the cent with integer math', () {
      expect(formatMicros(1500000), r'$1.50');
      expect(formatMicros(0), r'$0.00');
      expect(formatMicros(10000), r'$0.01');
      expect(formatMicros(14999), r'$0.01');
      expect(formatMicros(15000), r'$0.02');
      expect(formatMicros(20000000), r'$20.00');
      expect(formatMicros(123456789), r'$123.46');
    });

    test('a real but tiny amount is not shown as zero', () {
      expect(formatMicros(1), r'<$0.01');
      expect(formatMicros(4999), r'<$0.01');
    });

    test('negative or zero reads as zero dollars', () {
      expect(formatMicros(-5), r'$0.00');
    });
  });

  group('parseDollarsToMicros', () {
    test('plain amounts become whole-cent micro-dollars', () {
      expect(parseDollarsToMicros('5'), 5000000);
      expect(parseDollarsToMicros('5.5'), 5500000);
      expect(parseDollarsToMicros('5.50'), 5500000);
      expect(parseDollarsToMicros(r'$12.05'), 12050000);
      expect(parseDollarsToMicros('  7 '), 7000000);
      expect(parseDollarsToMicros('0.01'), 10000);
    });

    test('anything that is not a plain amount is refused', () {
      for (final bad in ['', 'abc', '-3', '1.234', '1,5', '5.', '.5', r'$$5', '1e3', '99999999']) {
        expect(parseDollarsToMicros(bad), isNull, reason: bad);
      }
    });
  });

  group('AiUsageSummary.fromJson', () {
    test('reads the server answer', () {
      final s = AiUsageSummary.fromJson({
        'source': 'cloud',
        'unlimited': false,
        'periodStart': '2026-10-01T00:00:00Z',
        'periodEnd': '2026-11-01T00:00:00Z',
        'allowanceMicros': 1500000,
        'usedMicros': 1200000,
        'percent': 80,
        'state': 'warning',
        'blocked': false,
        'projectedMicros': 1800000,
        'payg': {'available': true, 'enabled': true, 'capMicros': 5000000, 'usedMicros': 250000, 'maxCapMicros': 20000000},
        'byFeature': [
          {'feature': 'categorization', 'calls': 40, 'costMicros': 900000},
          {'feature': 'insights', 'calls': 3, 'costMicros': 300000},
        ],
      });

      expect(s.source, 'cloud');
      expect(s.allowanceMicros, 1500000);
      expect(s.usedMicros, 1200000);
      expect(s.isWarning, isTrue);
      expect(s.isOver, isFalse);
      expect(s.payg.available, isTrue);
      expect(s.payg.enabled, isTrue);
      expect(s.payg.capMicros, 5000000);
      expect(s.payg.maxCapMicros, 20000000);
      expect(s.byFeature.map((f) => f.feature), ['categorization', 'insights']);
      expect(s.byFeature.first.calls, 40);
      expect(s.periodEnd.toUtc(), DateTime.utc(2026, 11, 1));
    });

    test('missing pieces fall back to safe defaults rather than crashing', () {
      final s = AiUsageSummary.fromJson({});
      expect(s.source, 'local');
      expect(s.unlimited, isFalse);
      expect(s.usedMicros, 0);
      expect(s.state, 'ok');
      expect(s.payg.available, isFalse);
      expect(s.byFeature, isEmpty);
    });

    test('whole numbers that arrive as doubles are still whole micro-dollars', () {
      final s = AiUsageSummary.fromJson({'allowanceMicros': 1500000.0, 'usedMicros': 12.0});
      expect(s.allowanceMicros, 1500000);
      expect(s.usedMicros, 12);
    });
  });
}
