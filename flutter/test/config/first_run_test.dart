import 'package:buoy_ai/config/app_config.dart';
import 'package:buoy_ai/config/first_run.dart';
import 'package:buoy_ai/router/signed_out_landing.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Who sees "How would you like to use Buoy Money?" at first launch, and where everyone else lands.
void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    // A fresh install of the native production app.
    AppConfig.environment = 'production';
    AppConfig.webBuildForTest = false;
    AppConfig.sameOriginBuildForTest = false;
    AppConfig.fixedServerForTest = false;
    AppConfig.customServerOriginForTest = null;
    FirstRun.doneForTest = false;
  });

  tearDown(() {
    AppConfig.environment = 'development';
    AppConfig.webBuildForTest = null;
    AppConfig.sameOriginBuildForTest = null;
    AppConfig.fixedServerForTest = null;
    AppConfig.customServerOriginForTest = null;
    FirstRun.doneForTest = false;
  });

  group('first-launch question', () {
    test('shows on a fresh install of the native production app', () {
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isTrue);
      expect(signedOutLanding(hasSignedInBefore: false), firstRunPath);
    });

    test('never shows to someone who signed in on this device before (an upgrade)', () {
      expect(FirstRun.shouldShow(hasSignedInBefore: true), isFalse);
      expect(signedOutLanding(hasSignedInBefore: true), '/login');
    });

    test('never shows on the web', () {
      AppConfig.webBuildForTest = true;
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
      expect(signedOutLanding(hasSignedInBefore: false), '/register');
    });

    test('never shows on the web page a self-hosted server serves', () {
      AppConfig.sameOriginBuildForTest = true;
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
    });

    test('never shows on dev or staging builds', () {
      AppConfig.environment = 'development';
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
      AppConfig.environment = 'staging';
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
      expect(signedOutLanding(hasSignedInBefore: false), '/register');
    });

    test('never shows on a build pinned to one server', () {
      AppConfig.fixedServerForTest = true;
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
    });

    test('never shows once a server of your own was picked; that person signs in to it', () {
      AppConfig.customServerOriginForTest = 'https://buoy.example.ts.net';
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
      expect(signedOutLanding(hasSignedInBefore: false), '/login');
    });

    test('is asked once: after an answer it is gone, even after a restart', () async {
      await FirstRun.markDone();
      expect(FirstRun.shouldShow(hasSignedInBefore: false), isFalse);
      expect(signedOutLanding(hasSignedInBefore: false), '/register');

      FirstRun.doneForTest = false;
      await FirstRun.load();
      expect(FirstRun.done, isTrue);
    });

    test('a fresh install reads no saved answer', () async {
      await FirstRun.load();
      expect(FirstRun.done, isFalse);
    });

    test('a referral link always goes to sign-up', () {
      expect(signedOutLanding(hasSignedInBefore: false, hasReferral: true), '/register');
      expect(signedOutLanding(hasSignedInBefore: true, hasReferral: true), '/register');
    });
  });
}
