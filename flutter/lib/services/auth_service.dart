import 'dart:convert';
import '../models/auth_models.dart';
import 'api_client.dart';

/// Auth Service
///
/// Handles all authentication operations:
/// - Login (with MFA support)
/// - MFA verification
/// - Google OAuth login
/// - Registration
/// - Current user fetching
/// - Logout
class AuthService {
  final ApiClient _apiClient;
  
  AuthService(this._apiClient);
  
  /// Login with email and password
  /// Returns EnhancedLoginResponse which may require MFA
  Future<EnhancedLoginResponse> login(LoginRequest request) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/auth/login',
        data: request.toJson(),
      );
      
      return EnhancedLoginResponse.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Verify the current user's password WITHOUT minting any tokens.
  ///
  /// Used to re-confirm identity for a sensitive in-app action (e.g. enabling
  /// biometric quick-login from Settings) without going through [login] — which
  /// would mint throwaway access/refresh tokens and burn the 'auth' rate limit.
  /// Requires an authenticated session; the backend reads the caller from the
  /// bearer token and only checks the supplied password. Returns whether it
  /// matched; any transport/HTTP error propagates to the caller.
  Future<bool> verifyPassword(String password) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/auth/verify-password',
      data: {'password': password},
    );
    // Backend may send camelCase or PascalCase.
    return (response['valid'] ?? response['Valid']) as bool? ?? false;
  }

  /// Verify MFA code after login challenge
  Future<LoginResponse> verifyMfaLogin(VerifyMfaRequest request) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/auth/verify-mfa',
        data: request.toJson(),
      );
      
      return LoginResponse.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Login with Google OAuth token
  Future<LoginResponse> googleLogin(GoogleLoginRequest request) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/auth/google-login',
        data: request.toJson(),
      );
      
      return LoginResponse.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Single sign-on (self-host): trade the one-time code from the redirect back for the normal
  /// sign-in answer. Like a password sign-in, the answer may be an MFA challenge.
  Future<EnhancedLoginResponse> oidcExchange(String code, {String? codeVerifier}) async {
    final response = await _apiClient.post<Map<String, dynamic>>(
      '/auth/oidc/exchange',
      data: {
        'code': code,
        if (codeVerifier != null) 'codeVerifier': codeVerifier,
      },
    );
    return EnhancedLoginResponse.fromJson(response);
  }

  /// Login with Apple Sign-In credentials
  Future<LoginResponse> appleLogin(AppleLoginRequest request) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/auth/apple-login',
        data: request.toJson(),
      );
      
      return LoginResponse.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Register a new user
  Future<LoginResponse> register(RegisterRequest request) async {
    try {
      final response = await _apiClient.post<Map<String, dynamic>>(
        '/auth/register',
        data: request.toJson(),
      );
      
      return LoginResponse.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Get current authenticated user
  Future<User> getCurrentUser() async {
    try {
      final response = await _apiClient.get<Map<String, dynamic>>('/user/profile');
      return User.fromJson(response);
    } catch (e) {
      rethrow;
    }
  }
  
  /// Logout - revokes the refresh token server-side (best-effort), then clears local state.
  Future<void> logout() async {
    try {
      final refreshToken = await _apiClient.getRefreshToken();
      // Web sends the HttpOnly cookie automatically; native sends the token in the body.
      await _apiClient.post('/auth/logout', data: refreshToken != null ? {'refreshToken': refreshToken} : {});
    } catch (_) {
      // Best-effort revoke — always clear locally regardless of the network result.
    }
    await _apiClient.clearAll();
  }

  /// Store auth data after successful login. On native clients the rotating refresh
  /// token is persisted to the keychain; on web it arrives as an HttpOnly cookie and
  /// [refreshToken] is null (so nothing is stored client-side).
  Future<void> storeAuthData(String token, User user, {String? refreshToken}) async {
    await _apiClient.setAuthToken(token);
    await _apiClient.setUserData(jsonEncode(user.toJson()));
    if (refreshToken != null && refreshToken.isNotEmpty) {
      await _apiClient.setRefreshToken(refreshToken);
    }
  }
  
  /// Get stored user data (for app startup)
  Future<User?> getStoredUser() async {
    try {
      final userData = await _apiClient.getUserData();
      if (userData != null) {
        return User.fromJson(jsonDecode(userData) as Map<String, dynamic>);
      }
    } catch (e) {
      // Invalid stored data
    }
    return null;
  }
  
  /// Check if user has stored token
  Future<bool> hasStoredToken() async {
    final token = await _apiClient.getAuthToken();
    return token != null && token.isNotEmpty;
  }
  
  /// Send MFA code via email during login
  /// This is used when user selects email as their MFA method
  Future<void> sendLoginMfaCode(String mfaToken) async {
    try {
      await _apiClient.post(
        '/auth/send-login-mfa-code',
        data: {'mfaToken': mfaToken},
      );
    } catch (e) {
      rethrow;
    }
  }
}

// Global singleton instance
final authService = AuthService(apiClient);
