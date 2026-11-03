import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/services/api_client.dart';
import 'package:flutter_test/flutter_test.dart';

/// The "session expired" signal (#zombie-session): after an involuntary logout the
/// router bounces the user to /login, and this one-shot flag lets the login page
/// explain why. It must fire exactly once and never survive a rebuild.
void main() {
  test('consumeSessionExpired returns false by default', () {
    expect(AuthProvider().consumeSessionExpired(), isFalse);
  });

  test('fires exactly once after an expiry, then stays false', () {
    final auth = AuthProvider();
    auth.debugMarkSessionExpired();

    expect(auth.consumeSessionExpired(), isTrue, reason: 'first read reports the expiry');
    expect(auth.consumeSessionExpired(), isFalse, reason: 'one-shot: never re-fires on rebuild');
  });

  test('a 401 during the startup restore is not a session expiry', () {
    final auth = AuthProvider();
    auth.debugSetRestoring(true);

    apiClient.onUnauthorized!(); // what ApiClient fires when a refresh fails

    expect(auth.consumeSessionExpired(), isFalse,
        reason: 'initialize() owns the outcome while restoring; no false toast');
    auth.debugSetRestoring(false);
  });

  test('a 401 after the restore still reports an expiry', () {
    final auth = AuthProvider();
    apiClient.onUnauthorized!();
    expect(auth.consumeSessionExpired(), isTrue);
  });
}
