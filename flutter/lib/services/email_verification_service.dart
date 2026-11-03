import 'api_client.dart';

/// Client for the email-verification endpoints. A 6-digit code is emailed to the
/// user (this also confirms our email-delivered MFA fallback works), and entering
/// it marks the address verified — a prerequisite for protected operations.
class EmailVerificationService {
  final ApiClient _apiClient;

  EmailVerificationService(this._apiClient);

  /// Whether the current user's email is already verified.
  Future<bool> getStatus() async {
    final json =
        await _apiClient.get<Map<String, dynamic>>('/emailverification/status');
    return (json['isEmailVerified'] ?? json['IsEmailVerified']) as bool? ?? false;
  }

  /// Sends a fresh verification code to the user's email. Returns true when the
  /// address was already verified (e.g. via Google sign-in), in which case the
  /// backend short-circuits and sends NO code — callers should not claim one was.
  Future<bool> sendCode() async {
    final json =
        await _apiClient.post<Map<String, dynamic>>('/emailverification/send');
    return (json['isEmailVerified'] ?? json['IsEmailVerified']) as bool? ?? false;
  }

  /// Confirms the emailed code. Throws on an invalid/expired code.
  Future<void> confirmCode(String code) async {
    await _apiClient.post(
      '/emailverification/confirm',
      data: {'code': code},
    );
  }
}

// Global singleton
final emailVerificationService = EmailVerificationService(apiClient);
