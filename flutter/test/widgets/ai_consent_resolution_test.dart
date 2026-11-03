import 'package:flutter_test/flutter_test.dart';

import 'package:buoy_ai/widgets/ai_consent_sheet.dart';

void main() {
  group('resolveAiConsent (explicit opt-in on every platform)', () {
    test('undecided must ask', () {
      expect(resolveAiConsent(granted: false, withdrawn: false), isFalse);
    });
    test('explicit grant allows', () {
      expect(resolveAiConsent(granted: true, withdrawn: false), isTrue);
    });
    test('explicit withdrawal blocks', () {
      expect(resolveAiConsent(granted: false, withdrawn: true), isFalse);
    });
  });
}
