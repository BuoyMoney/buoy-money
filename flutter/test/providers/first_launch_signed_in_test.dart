import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// An upgrade is never a first launch: tokens left by an older version (even an expired session, or a version from
/// before the "signed in before" flag existed) count as having signed in on this device.
void main() {
  testWidgets('a refresh token from an older version counts as signed in before', (tester) async {
    SharedPreferences.setMockInitialValues({});
    FlutterSecureStorage.setMockInitialValues({'refreshToken': 'old-refresh-token'});
    final auth = AuthProvider();

    // The refresh is turned down (the test HTTP client answers 400), so the session ends signed out.
    await tester.runAsync(auth.initialize);

    expect(auth.isAuthenticated, isFalse);
    expect(auth.hasSignedInBefore, isTrue);
    final prefs = await SharedPreferences.getInstance();
    expect(prefs.getBool('has_signed_in_before'), isTrue);
  });

  testWidgets('a fresh install with no tokens has not signed in before', (tester) async {
    SharedPreferences.setMockInitialValues({});
    FlutterSecureStorage.setMockInitialValues({});
    final auth = AuthProvider();

    await tester.runAsync(auth.initialize);

    expect(auth.isAuthenticated, isFalse);
    expect(auth.hasSignedInBefore, isFalse);
  });
}
