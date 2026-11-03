import 'dart:io';

import 'package:flutter_test/flutter_test.dart';

/// The iPhone app's capabilities come from ios/Runner/Runner.entitlements, but
/// only when the Xcode build setting CODE_SIGN_ENTITLEMENTS points at it. The
/// file was in the repo from the start and nothing pointed at it, so every iOS
/// build shipped without Sign in with Apple ("AuthorizationErrorCode.unknown",
/// App Review 2.1(a) rejection, 2026-10-06), push notifications, or the
/// associated domains that passkeys and links back into the app need. The
/// TestFlight job also checks the signed app; this catches it on every PR.
void main() {
  final pbxproj =
      File('ios/Runner.xcodeproj/project.pbxproj').readAsStringSync();
  final entitlements =
      File('ios/Runner/Runner.entitlements').readAsStringSync();

  test('every Runner build configuration signs with Runner.entitlements', () {
    final runnerConfigs = RegExp(r'buildSettings = \{(.*?)\};', dotAll: true)
        .allMatches(pbxproj)
        .map((m) => m.group(1)!)
        .where((b) =>
            b.contains('PRODUCT_BUNDLE_IDENTIFIER = com.buoyai.buoyAi;'))
        .toList();

    expect(runnerConfigs, hasLength(3), reason: 'Debug, Release and Profile');
    for (final config in runnerConfigs) {
      expect(config,
          contains('CODE_SIGN_ENTITLEMENTS = Runner/Runner.entitlements;'));
    }
  });

  test('Runner.entitlements asks for the capabilities the app uses', () {
    for (final key in const [
      'com.apple.developer.applesignin',
      'com.apple.developer.associated-domains',
      'aps-environment',
    ]) {
      expect(entitlements, contains('<key>$key</key>'));
    }
    expect(entitlements, contains('webcredentials:buoy-ai.com'));
  });
}
