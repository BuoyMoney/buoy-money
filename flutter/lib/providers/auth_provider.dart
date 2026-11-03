import 'dart:async';
import 'package:dio/dio.dart';
import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import '../config/portal_mode.dart';
import '../models/auth_models.dart';
import '../offline/network_error.dart';
import '../offline/offline_store.dart';
import '../offline/sync_service.dart';
import '../services/ai_status_service.dart';
import '../services/auth_service.dart';
import '../services/api_client.dart';
import '../services/analytics_service.dart';
import '../services/biometric_service.dart';
import '../services/passkey_service.dart';
import '../services/push_notification_service.dart';
import '../services/store_billing_service.dart';
import '../utils/currency.dart';
import '../utils/navigation_depth.dart';

/// Auth state for the application
enum AuthStatus {
  initial,
  loading,
  authenticated,
  unauthenticated,
  mfaRequired,
  error,
}

/// MFA challenge data when MFA is required
class MfaChallenge {
  final String mfaToken;
  final List<String> reasons;
  final String? preferredMethod;
  final List<String> availableMethods;

  /// The email the password step used. The biometric quick-login path never
  /// fills the login form, so the MFA screen reads the account from here (the
  /// passkey ceremony needs it).
  final String? email;

  MfaChallenge({
    required this.mfaToken,
    required this.reasons,
    this.preferredMethod,
    required this.availableMethods,
    this.email,
  });
}

/// Auth Provider
///
/// Manages authentication state across the app using ChangeNotifier pattern.
class AuthProvider extends ChangeNotifier {
  User? _userField;

  // Every `_user = …` assignment routes through this setter so the global
  // primary display currency (utils/currency.dart) always tracks the signed-in
  // user — including logout (null resets it to USD so a shared device never
  // formats the next session in the previous user's currency).
  User? get _user => _userField;
  set _user(User? value) {
    _userField = value;
    setPrimaryCurrency(value?.currency);
    // The store identity follows the session: RevenueCat's app user id is our
    // numeric id, which is what its webhook maps back to this account. Fire and
    // forget; a failure is reported inside and never blocks auth.
    unawaited(storeBilling.identify(value?.id));
  }

  AuthStatus _status = AuthStatus.initial;
  String? _errorMessage;
  MfaChallenge? _mfaChallenge;
  List<PendingFamilyInvitation> _pendingFamilyInvitations = [];

  // Getters
  User? get user => _user;
  AuthStatus get status => _status;
  String? get errorMessage => _errorMessage;
  MfaChallenge? get mfaChallenge => _mfaChallenge;
  List<PendingFamilyInvitation> get pendingFamilyInvitations => _pendingFamilyInvitations;
  bool get isAuthenticated => _status == AuthStatus.authenticated;
  bool get isLoading => _status == AuthStatus.loading;

  /// True while the startup session restore ([initialize]) is in flight. Protected
  /// pages must not build in this window: they would fire data calls with no access
  /// token yet (web keeps it in memory only), and the resulting 401s are not a real
  /// expiry. [AuthRestoreGate] holds the UI back until this clears.
  bool get isRestoringSession => _restoring;
  bool _restoring = false;

  @visibleForTesting
  void debugSetRestoring(bool value) {
    _restoring = value;
    notifyListeners();
  }
  
  AuthProvider() {
    // Set up unauthorized callback
    apiClient.onUnauthorized = _handleUnauthorized;
  }
  
  /// True after an involuntary sign-out (refresh token expired/revoked) rather than
  /// a user-initiated [logout]. The router's refreshListenable bounces the user to
  /// /login on the state change; this flag lets the login page explain WHY with a
  /// toast instead of a silent, confusing kick. One-shot: read it via
  /// [consumeSessionExpired].
  bool _sessionExpired = false;

  /// Reads and clears the session-expired flag (so the toast shows exactly once).
  bool consumeSessionExpired() {
    if (!_sessionExpired) return false;
    _sessionExpired = false;
    return true;
  }

  /// Test seam: the only real trigger ([_handleUnauthorized]) is wired to a network
  /// callback, so exercise the one-shot flag directly in unit tests.
  @visibleForTesting
  void debugMarkSessionExpired() => _sessionExpired = true;

  void _handleUnauthorized() {
    // A 401 that lands during the startup restore is not an expiry: initialize()
    // is already deciding the outcome (and sets the right status itself).
    if (_restoring) return;
    _user = null;
    _status = AuthStatus.unauthenticated;
    _mfaChallenge = null;
    _errorMessage = null;
    _pendingFamilyInvitations = [];
    // Distinguish this involuntary logout from a deliberate one so the login page
    // can say "your session expired" rather than leaving the user wondering.
    _sessionExpired = true;
    analytics.reset();
    aiAvailability.reset();
    // Drop any locally cached data + queued writes so the next (possibly
    // different) user never sees this session's cache (#493).
    unawaited(wipeOfflineCache());
    notifyListeners();
  }

  /// Signs out in memory only, after the user moved the app to another server (the update
  /// screen's "Use a different server"). Tokens and the offline cache were already cleared by
  /// the switch, and the old server may not answer, so nothing is sent to it.
  void signOutForServerSwitch() {
    _user = null;
    _status = AuthStatus.unauthenticated;
    _mfaChallenge = null;
    _errorMessage = null;
    _pendingFamilyInvitations = [];
    _sessionExpired = false;
    analytics.reset();
    aiAvailability.reset();
    notifyListeners();
  }

  /// Identify user for analytics + error tracking after any successful auth.
  Future<void> _identifyUser(User u, {String method = 'unknown'}) async {
    // Ensure the local offline cache belongs to this user — wipe it if the last
    // session on this device was someone else (shared-device safety, #493).
    // Awaited so the wipe lands BEFORE the authenticated state is announced —
    // otherwise the first offline read could serve the previous user's cache.
    await reconcileCacheOwner(u.id);
    // Now that we're authenticated, flush anything queued while logged out /
    // offline — the drain is auth-gated, so this is the earliest it can run.
    unawaited(syncService.drainOutbox());
    unawaited(_markSignedInBefore());
    analytics.identify(
      u.id,
      email: u.email,
      name: u.fullName,
    );
    analytics.track(AnalyticsEvents.loginCompleted, {'method': method});
    // Sentry user context (non-PII: just userId + subscription tier)
    Sentry.configureScope((scope) {
      scope.setUser(SentryUser(id: u.id));
      scope.setTag('subscription_tier', u.subscriptionTier);
    });
  }
  
  /// Initialize auth state from stored data

  /// Whether anyone has ever completed a sign-in on THIS device.
  ///
  /// Drives where a logged-out visitor lands. A fresh install has no account, so
  /// showing it a sign-in form asks for a password that does not exist yet — which
  /// is exactly what happened to the first wave of Play testers: ten reached /login
  /// and none ever reached /register. Someone who has signed in here before and
  /// simply logged out is a different case, and still gets /login.
  static const String _hasSignedInKey = 'has_signed_in_before';
  bool _hasSignedInBefore = false;
  bool get hasSignedInBefore => _hasSignedInBefore;

  Future<void> _loadHasSignedInBefore() async {
    final prefs = await SharedPreferences.getInstance();
    _hasSignedInBefore = prefs.getBool(_hasSignedInKey) ?? false;
  }

  Future<void> _markSignedInBefore() async {
    if (_hasSignedInBefore) return;
    _hasSignedInBefore = true;
    final prefs = await SharedPreferences.getInstance();
    await prefs.setBool(_hasSignedInKey, true);
  }

  Future<bool> _hasStoredRefreshToken() async {
    try {
      final token = await apiClient.getRefreshToken();
      return token != null && token.isNotEmpty;
    } catch (_) {
      return false;
    }
  }

  Future<void> initialize() async {
    _restoring = true;
    try {
      await _initialize();
    } finally {
      _restoring = false;
      notifyListeners();
    }
  }

  Future<void> _initialize() async {
    _status = AuthStatus.loading;
    notifyListeners();

    // Read before any redirect can run — the router gates on isLoading/initial.
    await _loadHasSignedInBefore();
    
    try {
      // Check for a stored access token.
      final hasToken = await authService.hasStoredToken();
      // Tokens from an older version mean someone signed in here before, even if that version predates the
      // flag (an upgrade) or the session has since expired: never treat this device as a first launch.
      if (hasToken || await _hasStoredRefreshToken()) await _markSignedInBefore();
      if (!hasToken) {
        // None in memory (e.g. a web reload — the access token is memory-only on web).
        // Try to re-establish the session from the refresh token: the HttpOnly cookie
        // on web, or the keychain-stored refresh token on native.
        final refreshed = await apiClient.refreshSession();
        if (!refreshed) {
          _status = AuthStatus.unauthenticated;
          notifyListeners();
          return;
        }
      }
      
      // Verify token is still valid BEFORE setting authenticated.
      // This prevents the dashboard from firing API calls with an
      // expired token and avoids a brief flash of authenticated UI.
      try {
        final currentUser = await authService.getCurrentUser();
        _user = currentUser;
        _status = AuthStatus.authenticated;
        await _identifyUser(currentUser, method: 'stored_session');
        // Update stored data with fresh server data
        final token = await apiClient.getAuthToken();
        if (token != null) {
          await authService.storeAuthData(token, currentUser);
        }
        notifyListeners();
      } catch (e) {
        // Offline-first (#493): a NETWORK failure is not an invalid session.
        // Restore the session from the locally stored user so the app opens
        // into its cached data offline; the token is re-validated by the
        // normal 401→refresh path the next time a request reaches the server.
        if (e is DioException && isNetworkError(e)) {
          final stored = await authService.getStoredUser();
          if (stored != null) {
            _user = stored;
            _status = AuthStatus.authenticated;
            await _identifyUser(stored, method: 'offline_restore');
            notifyListeners();
            return;
          }
        }
        // Token genuinely invalid/expired — clear everything and go to login
        await authService.logout();
        _user = null;
        _status = AuthStatus.unauthenticated;
        notifyListeners();
      }
    } catch (e) {
      _status = AuthStatus.unauthenticated;
      _errorMessage = e.toString();
      notifyListeners();
    }
  }
  
  /// Login with email and password
  /// Returns true if login successful, false if MFA required
  Future<bool> login(String email, String password) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    _mfaChallenge = null;
    notifyListeners();
    
    try {
      final request = LoginRequest(
        email: email,
        password: password,
        // TODO: Add device fingerprint for trusted device support
      );
      
      final response = await authService.login(request);
      
      if (response.requiresMfa) {
        // MFA required
        _status = AuthStatus.mfaRequired;
        _mfaChallenge = MfaChallenge(
          mfaToken: response.mfaToken!,
          reasons: response.mfaReasons ?? [],
          preferredMethod: response.preferredMfaMethod,
          availableMethods: response.availableMfaMethods ?? [],
          email: email,
        );
        notifyListeners();
        return false;
      }

      // Login successful
      await authService.storeAuthData(response.token!, response.user!, refreshToken: response.refreshToken);
      _user = response.user;
      _pendingFamilyInvitations = response.pendingFamilyInvitations;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user!, method: 'email');
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }

  /// Clear pending invitations (after user acts on them)
  void clearPendingInvitations() {
    _pendingFamilyInvitations = [];
    notifyListeners();
  }

  /// Login with biometric authentication.
  ///
  /// The fingerprint/face check only unlocks the stored credentials on this
  /// phone; the server cannot see it, so it is not a second factor. The server
  /// decides MFA exactly as for a typed password: no prompt for accounts without
  /// MFA or on a device trusted by an earlier MFA with "remember device",
  /// otherwise an MFA challenge, which this puts on the normal MFA screen.
  Future<bool> biometricLogin() async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    _mfaChallenge = null;
    notifyListeners();
    
    try {
      // Authenticate with biometrics and get stored credentials
      final credentials = await biometricService.authenticateAndGetCredentials();
      
      if (credentials == null) {
        _status = AuthStatus.unauthenticated;
        _errorMessage = 'Biometric authentication failed or not set up';
        notifyListeners();
        return false;
      }
      
      final request = LoginRequest(
        email: credentials.email,
        password: credentials.password,
      );

      final response = await authService.login(request);

      if (response.requiresMfa) {
        // Same MFA screen as a typed-password login.
        _status = AuthStatus.mfaRequired;
        _mfaChallenge = MfaChallenge(
          mfaToken: response.mfaToken!,
          reasons: response.mfaReasons ?? [],
          preferredMethod: response.preferredMfaMethod,
          availableMethods: response.availableMfaMethods ?? [],
          email: credentials.email,
        );
        notifyListeners();
        return false;
      }
      
      // Login successful
      await authService.storeAuthData(response.token!, response.user!, refreshToken: response.refreshToken);
      _user = response.user;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user!, method: 'biometric');
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }
  
  /// Verify MFA code
  Future<bool> verifyMfa(String mfaCode, {bool rememberDevice = false}) async {
    if (_mfaChallenge == null) {
      _errorMessage = 'No MFA challenge pending';
      notifyListeners();
      return false;
    }
    
    _status = AuthStatus.loading;
    _errorMessage = null;
    notifyListeners();
    
    try {
      final request = VerifyMfaRequest(
        mfaToken: _mfaChallenge!.mfaToken,
        mfaCode: mfaCode,
        rememberDevice: rememberDevice,
      );
      
      final response = await authService.verifyMfaLogin(request);
      
      await authService.storeAuthData(response.token, response.user, refreshToken: response.refreshToken);
      _user = response.user;
      _status = AuthStatus.authenticated;
      _mfaChallenge = null;
      await _identifyUser(response.user, method: 'mfa');
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.mfaRequired; // Stay on MFA screen
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }

  /// Complete login with a passkey (WebAuthn) as the second factor.
  ///
  /// Works on web (Chrome/Safari/etc. via the `passkeys` package) as well as
  /// native. A passkey is itself a strong authentication, so a successful
  /// ceremony returns a full session directly — there's no separate code to
  /// verify. On failure we stay on the MFA screen so the user can retry or fall
  /// back to an emailed code (offered for passkey-only accounts).
  Future<bool> loginWithPasskey({required String username, bool rememberDevice = false}) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    notifyListeners();

    try {
      final raw = await passkeyService.authenticateWithPasskey(
        username: username,
        rememberDevice: rememberDevice,
      );
      final response = EnhancedLoginResponse.fromJson(raw);

      if (response.token == null || response.user == null) {
        throw Exception('Passkey sign-in did not return a session');
      }

      await authService.storeAuthData(
        response.token!,
        response.user!,
        refreshToken: response.refreshToken,
      );
      _user = response.user;
      _pendingFamilyInvitations = response.pendingFamilyInvitations;
      _status = AuthStatus.authenticated;
      _mfaChallenge = null;
      await _identifyUser(response.user!, method: 'passkey');
      notifyListeners();
      return true;
    } catch (e) {
      // If a challenge is still pending, keep the user on the MFA screen so they
      // can retry the passkey or switch to email; otherwise drop to the login form.
      _status = _mfaChallenge != null ? AuthStatus.mfaRequired : AuthStatus.unauthenticated;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }

  /// Register new user
  Future<bool> register({
    required String email,
    required String password,
    required String firstName,
    required String lastName,
    String? inviteCode,
  }) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    notifyListeners();
    
    try {
      final request = RegisterRequest(
        email: email,
        password: password,
        firstName: firstName,
        lastName: lastName,
        inviteCode: inviteCode,
        // Signing up in the Cloud portal makes a Cloud-only account (no hosted trial).
        signupContext: PortalMode.signupContextOrNull,
      );
      
      final response = await authService.register(request);

      await authService.storeAuthData(response.token, response.user, refreshToken: response.refreshToken);
      _user = response.user;
      _pendingFamilyInvitations = response.pendingFamilyInvitations;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user, method: 'register');
      analytics.track(AnalyticsEvents.signupCompleted, {'method': 'email'});
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }
  
  /// Google login / signup.
  ///
  /// Set [isRegistration] to true from the sign-up flow. When false (login) and
  /// no account exists, the backend responds 404 and this throws a
  /// [GoogleAccountNotFoundException] so the caller can route to registration.
  Future<bool> googleLogin(String idToken, {bool isRegistration = false}) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    notifyListeners();

    try {
      final request = GoogleLoginRequest(
        idToken: idToken,
        isRegistration: isRegistration,
        signupContext: PortalMode.signupContextOrNull,
      );
      final response = await authService.googleLogin(request);

      await authService.storeAuthData(response.token, response.user, refreshToken: response.refreshToken);
      _user = response.user;
      _pendingFamilyInvitations = response.pendingFamilyInvitations;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user, method: 'google');
      // OAuth sign-up and sign-in are one call, so only the server can say whether an
      // account was created. Without this a Google signup looked like a plain login.
      if (response.isNewUser) {
        analytics.track(AnalyticsEvents.signupCompleted, {'method': 'google'});
      }
      notifyListeners();
      return true;
    } on DioException catch (e) {
      // Login attempted with no existing account — surface a typed signal so the
      // login page can redirect to registration instead of showing an error.
      if (e.response?.statusCode == 404) {
        _status = AuthStatus.unauthenticated;
        _errorMessage = null;
        notifyListeners();
        throw const GoogleAccountNotFoundException();
      }
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }
  
  /// Single sign-on (self-host): finish the round trip through the operator's provider by
  /// trading the one-time code. Returns true when signed in; false with [status] set to
  /// [AuthStatus.mfaRequired] when the account still needs its MFA step (shown on the login page),
  /// or [AuthStatus.error] with [errorMessage] on failure.
  Future<bool> oidcExchange(String code, {String? codeVerifier}) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    _mfaChallenge = null;
    notifyListeners();

    try {
      final response = await authService.oidcExchange(code, codeVerifier: codeVerifier);
      if (response.requiresMfa) {
        _status = AuthStatus.mfaRequired;
        _mfaChallenge = MfaChallenge(
          mfaToken: response.mfaToken!,
          reasons: response.mfaReasons ?? [],
          preferredMethod: response.preferredMfaMethod,
          availableMethods: response.availableMfaMethods ?? [],
          email: response.user?.email,
        );
        notifyListeners();
        return false;
      }

      await authService.storeAuthData(response.token!, response.user!, refreshToken: response.refreshToken);
      _user = response.user;
      _pendingFamilyInvitations = response.pendingFamilyInvitations;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user!, method: 'oidc');
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }

  /// Apple login
  Future<bool> appleLogin(AppleLoginRequest request) async {
    _status = AuthStatus.loading;
    _errorMessage = null;
    notifyListeners();
    
    try {
      // Apple creates the account on first sign-in, from the sign-in page too, so the portal context always goes.
      final response = await authService.appleLogin(
        request.signupContext != null
            ? request
            : request.withSignupContext(PortalMode.signupContextOrNull),
      );
      
      await authService.storeAuthData(response.token, response.user, refreshToken: response.refreshToken);
      _user = response.user;
      _status = AuthStatus.authenticated;
      await _identifyUser(response.user, method: 'apple');
      if (response.isNewUser) {
        analytics.track(AnalyticsEvents.signupCompleted, {'method': 'apple'});
      }
      notifyListeners();
      return true;
    } catch (e) {
      _status = AuthStatus.error;
      _errorMessage = _extractErrorMessage(e);
      notifyListeners();
      return false;
    }
  }
  
  /// Logout
  Future<void> logout() async {
    analytics.track(AnalyticsEvents.logout);
    analytics.reset();
    Sentry.configureScope((scope) => scope.setUser(null));
    // Stop pushes following the signed-out account. Must run while the auth token is
    // still valid (the unregister endpoint is authenticated); never throws.
    await pushNotificationService.unregisterDevice();
    await authService.logout();
    // Drop cached data + queued offline writes on explicit logout (#493).
    await wipeOfflineCache();
    NavigationDepth.reset();
    aiAvailability.reset();
    _user = null;
    _status = AuthStatus.unauthenticated;
    _errorMessage = null;
    _mfaChallenge = null;
    // A deliberate logout is not an expiry — never show the "session expired" toast.
    _sessionExpired = false;
    notifyListeners();
  }
  
  /// Refresh user data from server
  Future<void> refreshUser() async {
    if (!isAuthenticated) return;
    
    try {
      final currentUser = await authService.getCurrentUser();
      _user = currentUser;
      // Update stored data
      final token = await apiClient.getAuthToken();
      if (token != null) {
        await authService.storeAuthData(token, currentUser);
      }
      notifyListeners();
    } catch (e) {
      // Silently fail - don't disrupt user
    }
  }
  
  /// Clear error
  void clearError() {
    _errorMessage = null;
    notifyListeners();
  }
  
  /// Cancel MFA and go back to login
  void cancelMfa() {
    _mfaChallenge = null;
    _status = AuthStatus.unauthenticated;
    _errorMessage = null;
    notifyListeners();
  }
  
  String _extractErrorMessage(dynamic error) {
    // Try to extract message from DioException
    if (error.toString().contains('DioException')) {
      try {
        final response = (error as dynamic).response;
        if (response?.data != null) {
          if (response.data is Map) {
            return (response.data['message'] ??
                   response.data['error'] ??
                   response.data['Message'] ??
                   'An error occurred').toString();
          }
        }
        return 'Network error. Please check your connection.';
      } catch (e) {
        return 'An error occurred';
      }
    }
    return error.toString();
  }
}
