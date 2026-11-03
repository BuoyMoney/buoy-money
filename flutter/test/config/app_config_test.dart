import 'package:flutter_test/flutter_test.dart';
import 'package:buoy_ai/config/app_config.dart';

void main() {
  group('AppConfig — Sign in with Apple (Web)', () {
    test('Services ID defaults to empty so the web button ships hidden', () {
      // A non-empty default would light up the web Apple button in every build
      // before the Apple portal / domain verification is done. Guard against
      // that regression: the value must come from --dart-define at build time.
      expect(AppConfig.appleServicesId, isEmpty);
    });

    test('web Apple flow reports unconfigured without a Services ID', () {
      // In the (non-web) test VM this is always false; the assertion documents
      // and locks the "hidden until configured" contract.
      expect(AppConfig.isAppleWebConfigured, isFalse);
    });

    test('redirect URI defaults to the registered production Return URL', () {
      expect(
        AppConfig.appleWebRedirectUri,
        'https://buoy-ai.com/api/auth/apple/callback',
      );
    });
  });
}
