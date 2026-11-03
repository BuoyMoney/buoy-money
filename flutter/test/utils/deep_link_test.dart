import 'package:buoy_ai/utils/deep_link.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  group('normalizeInAppRoute', () {
    test('strips the /app web base-href prefix (the reported bug)', () {
      // A notification stored with the web deep-link prefix must still route in-app.
      expect(normalizeInAppRoute('/app/achievements'), '/achievements');
    });

    test('leaves already-correct in-app routes unchanged', () {
      expect(normalizeInAppRoute('/achievements'), '/achievements');
      expect(normalizeInAppRoute('/billing'), '/billing');
      expect(normalizeInAppRoute('/update-connection/1'), '/update-connection/1');
    });

    test('maps bare /app to the root', () {
      expect(normalizeInAppRoute('/app'), '/');
    });

    test('does not strip /app when it is part of a real segment', () {
      // '/application' must not become '/lication'.
      expect(normalizeInAppRoute('/application'), '/application');
    });

    test('rejects external links (left to the OS, not GoRouter)', () {
      expect(normalizeInAppRoute('https://buoy-ai.com/app/achievements'), isNull);
      expect(normalizeInAppRoute('http://example.com'), isNull);
    });

    test('rejects non-route and empty/null values', () {
      expect(normalizeInAppRoute('achievements'), isNull);
      expect(normalizeInAppRoute(''), isNull);
      expect(normalizeInAppRoute('   '), isNull);
      expect(normalizeInAppRoute(null), isNull);
    });

    test('trims surrounding whitespace', () {
      expect(normalizeInAppRoute('  /app/achievements '), '/achievements');
    });
  });
}
