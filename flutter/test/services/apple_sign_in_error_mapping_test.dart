import 'package:flutter_test/flutter_test.dart';
import 'package:sign_in_with_apple/sign_in_with_apple.dart';
import 'package:buoy_ai/l10n/generated/app_localizations_en.dart';
import 'package:buoy_ai/services/apple_sign_in_service.dart';

void main() {
  webFailureTests();
  const raw =
      'The operation couldn\'t be completed. (com.apple.AuthenticationServices.AuthorizationError error 1000.)';

  test('canceled maps to null (silent)', () {
    final e = SignInWithAppleAuthorizationException(
        code: AuthorizationErrorCode.canceled, message: 'cancelled');
    expect(mapAppleSignInError(e), isNull);
  });

  for (final code in [
    AuthorizationErrorCode.unknown,
    AuthorizationErrorCode.failed,
    AuthorizationErrorCode.notHandled,
    AuthorizationErrorCode.invalidResponse,
    AuthorizationErrorCode.notInteractive,
  ]) {
    test('${code.name} maps to friendly unavailable exception', () {
      final e = SignInWithAppleAuthorizationException(code: code, message: raw);
      final mapped = mapAppleSignInError(e);
      expect(mapped, isNotNull);
      expect(mapped!.code, code.name);
      expect(mapped.toString(), isNot(contains('1000')));
    });
  }

  test('friendly text has no raw exception text', () {
    final text = AppLocalizationsEn().appleSignInUnavailable;
    expect(text, contains("isn't available on this device"));
    expect(text, contains('iCloud'));
    expect(text, isNot(contains('SignInWithApple')));
    expect(text, isNot(contains('error 1000')));
  });
}

void webFailureTests() {
  group('wrapAppleWebFailure', () {
    test('plain Exception (null identity token) becomes friendly exception', () {
      final w = wrapAppleWebFailure(Exception('Failed to get identity token'));
      expect(w.code, 'web_failure');
      expect(w.toString(), isNot(contains('identity token')));
    });
    test('already-friendly exception passes through', () {
      final e = AppleSignInUnavailableException('x');
      expect(identical(wrapAppleWebFailure(e), e), isTrue);
    });
  });
}
