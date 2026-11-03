import 'dart:async';
import 'dart:ui';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import '../../config/app_config.dart';
import '../../config/server_capabilities.dart';
import 'package:go_router/go_router.dart';
import '../../router/post_auth_redirect.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:google_sign_in/google_sign_in.dart';
import 'package:provider/provider.dart';
import '../../l10n/generated/app_localizations.dart';
import 'package:sign_in_with_apple/sign_in_with_apple.dart';
import '../../widgets/google_auth_button.dart';
import '../../models/auth_models.dart';
import '../../providers/auth_provider.dart';
import '../../services/auth_service.dart';
import '../../services/biometric_service.dart';
import '../../services/google_sign_in_service.dart';
import '../../services/apple_sign_in_service.dart';
import '../../widgets/app_toast.dart';
import '../../widgets/google_sign_in_button.dart';
import '../../widgets/oidc_sign_in_button.dart';
import '../../services/oidc_sign_in_service.dart';
import '../../widgets/server_picker.dart';
import '../../widgets/cinematic/noise_overlay.dart';
import '../../widgets/cinematic/backgrounds/interactive_background_layer.dart';
import '../../services/passkey_service.dart';
import '../../utils/web_navigation.dart';

class LoginPage extends StatefulWidget {
  const LoginPage({super.key});

  @override
  State<LoginPage> createState() => _LoginPageState();
}

class _LoginPageState extends State<LoginPage> with SingleTickerProviderStateMixin {
  final _formKey = GlobalKey<FormState>();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();
  final _mfaCodeController = TextEditingController();
  final _scrollController = ScrollController();
  final _emailFocusNode = FocusNode();
  final _passwordFocusNode = FocusNode();
  final ValueNotifier<Offset> _globalMousePos = ValueNotifier(Offset.zero);

  bool _obscurePassword = true;
  bool _rememberDevice = false;
  String? _selectedMfaMethod;
  bool _passkeySupported = false;
  bool _sendingEmailCode = false;
  bool _googleSignInLoading = false;
  bool _oidcLoading = false;
  bool _googleInitialized = false;
  bool _appleSignInLoading = false;
  bool _appleAvailable = false;
  // Biometric quick-login (mobile only): true when stored credentials exist, so
  // the fingerprint button shows and cold start auto-prompts.
  bool _biometricLoginEnabled = false;
  // 'Fingerprint' / 'Face ID' / 'Biometric' — loaded once for adaptive labels.
  String _biometricTypeName = 'Biometric';
  StreamSubscription<GoogleSignInAuthenticationEvent>? _authEventSub;

  late AnimationController _fadeController;
  late Animation<double> _fadeAnimation;

  @override
  void initState() {
    super.initState();
    _fadeController = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 800),
    );
    _fadeAnimation = CurvedAnimation(
      parent: _fadeController,
      curve: Curves.easeOut,
    );
    _fadeController.forward();
    _initGoogleSignIn();
    _initAppleSignIn();
    _initPasskeySupport();
    _initBiometricQuickLogin();
    _maybeShowSessionExpired();
    _resumePendingMfa();
    // The password form and the single sign-on button follow what the server offers.
    ServerEdition.current.addListener(_onEditionChanged);
  }

  void _onEditionChanged() {
    if (mounted) setState(() {});
  }

  /// Single sign-on can end in an MFA challenge (the account has app MFA). It lands here with the
  /// challenge already pending, so pick the method the same way as after a typed password.
  void _resumePendingMfa() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      final auth = context.read<AuthProvider>();
      final challenge = auth.mfaChallenge;
      if (auth.status == AuthStatus.mfaRequired && challenge != null) {
        final email = challenge.email;
        if (email != null && _emailController.text.trim().isEmpty) {
          _emailController.text = email;
        }
        _autoSelectMfaMethod(challenge);
      }
    });
  }

  /// "Sign in with (provider)": leave for the server's single sign-on. The web page navigates away;
  /// a phone opens the browser and comes back through the deep link.
  Future<void> _handleOidcSignIn() async {
    setState(() => _oidcLoading = true);
    final opened = await oidcSignInService.begin();
    if (!mounted) return;
    setState(() => _oidcLoading = false);
    if (!opened) {
      showAppToast(
        context,
        AppLocalizations.of(context).oidcOpenFailed,
        type: AppToastType.error,
        clearsBottomNav: false,
      );
    }
  }

  /// If we landed here because a session expired (not a deliberate logout), explain
  /// it — otherwise the redirect looks like a random kick to login. One-shot flag
  /// consumed from AuthProvider so it never re-fires on rebuild.
  void _maybeShowSessionExpired() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      if (context.read<AuthProvider>().consumeSessionExpired()) {
        showAppToast(
          context,
          AppLocalizations.of(context).sessionExpired,
          type: AppToastType.neutral,
          clearsBottomNav: false,
        );
      }
    });
  }

  /// Mobile-only: if the user previously enabled biometric quick-login, surface
  /// the fingerprint button and auto-prompt once after first frame — a returning
  /// user can sign in with a single biometric, Chase/Robinhood style.
  Future<void> _initBiometricQuickLogin() async {
    if (kIsWeb) return;
    try {
      final enabled = await biometricService.isBiometricLoginEnabled();
      final typeName = await biometricService.getBiometricTypeName();
      if (!mounted) return;
      setState(() {
        _biometricLoginEnabled = enabled;
        _biometricTypeName = typeName;
      });
      if (enabled) {
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) _handleBiometricLogin();
        });
      }
    } catch (_) {
      // Detection failed — leave quick-login hidden.
    }
  }

  /// Run biometric quick-login. On success the GoRouter auth redirect navigates
  /// (mirroring the email/password path). If the server asks for MFA (account
  /// has MFA and this device is not trusted yet) we show the normal MFA screen,
  /// with the method picked the same way as after a typed password. On
  /// failure/cancel we stay on the form silently: the error banner is cleared so
  /// a cancelled prompt isn't alarming.
  Future<void> _handleBiometricLogin() async {
    final authProvider = context.read<AuthProvider>();
    final success = await authProvider.biometricLogin();
    if (!mounted || success) return;

    final challenge = authProvider.mfaChallenge;
    if (authProvider.status == AuthStatus.mfaRequired && challenge != null) {
      // The form was never filled on this path; the passkey step reads the
      // account from the email field, and Cancel lands back on a filled form.
      final email = challenge.email;
      if (email != null && _emailController.text.trim().isEmpty) {
        _emailController.text = email;
      }
      await _autoSelectMfaMethod(challenge);
      return;
    }
    authProvider.clearError();
  }

  /// Detect whether this platform (including web) can use passkeys, so the MFA
  /// screen can offer a passkey button instead of blanket-hiding it.
  Future<void> _initPasskeySupport() async {
    try {
      final supported = await passkeyService.isSupported();
      if (mounted) setState(() => _passkeySupported = supported);
    } catch (_) {
      // Leave _passkeySupported = false on any detection error.
    }
  }

  @override
  void dispose() {
    ServerEdition.current.removeListener(_onEditionChanged);
    _authEventSub?.cancel();
    _emailController.dispose();
    _passwordController.dispose();
    _mfaCodeController.dispose();
    _scrollController.dispose();
    _emailFocusNode.dispose();
    _passwordFocusNode.dispose();
    _fadeController.dispose();
    super.dispose();
  }

  Future<void> _initGoogleSignIn() async {
    try {
      await googleSignInService.initialize();
      _authEventSub = googleSignInService.authenticationEvents.listen(
        _handleGoogleAuthEvent,
        onError: (Object error) {
          debugPrint('Google auth error: $error');
        },
      );
      googleSignInService.attemptLightweightAuthentication();
      if (mounted) setState(() => _googleInitialized = true);
    } catch (e) {
      debugPrint('Google Sign-In init failed: $e');
    }
  }

  /// Determine whether native "Sign in with Apple" is offered on this device
  /// (iOS/macOS). On web/Android the button stays hidden.
  Future<void> _initAppleSignIn() async {
    try {
      final available = await appleSignInService.isAvailable();
      if (mounted) setState(() => _appleAvailable = available);
    } catch (e) {
      debugPrint('Apple Sign-In availability check failed: $e');
    }
  }

  /// Native Apple Sign-In (iOS/macOS). Mirrors the Google flow: present the OS
  /// sheet, forward the identity token to the backend, navigate on success.
  Future<void> _handleAppleSignIn() async {
    setState(() => _appleSignInLoading = true);
    try {
      final result = await appleSignInService.signIn();
      if (result == null) {
        // User cancelled — not an error.
        if (mounted) setState(() => _appleSignInLoading = false);
        return;
      }
      if (!mounted) return;
      final authProvider = context.read<AuthProvider>();
      // Preserve any invitation token across the async gap (invite links land here).
      final invitationToken =
          GoRouterState.of(context).uri.queryParameters['invitation'];
      final success = await authProvider.appleLogin(AppleLoginRequest(
        identityToken: result.identityToken,
        authorizationCode: result.authorizationCode,
        nonce: result.rawNonce,
        email: result.email,
        // Apple sends the name only on the FIRST authorization — pass it through
        // so the backend can persist it on account creation.
        firstName: result.givenName,
        lastName: result.familyName,
        userIdentifier: result.userIdentifier,
      ));
      if (!mounted) return;
      if (success) {
        if (invitationToken != null && invitationToken.isNotEmpty) {
          context.go('/accept-invitation/$invitationToken');
        } else {
          context.go(PostAuthRedirect.destination());
        }
      } else {
        showAppToast(
          context,
          authProvider.errorMessage ?? AppLocalizations.of(context).appleSignInFailed,
          type: AppToastType.error,
          clearsBottomNav: false,
        );
      }
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          e is AppleSignInUnavailableException
              ? AppLocalizations.of(context).appleSignInUnavailable
              : AppLocalizations.of(context).appleSignInFailed,
          type: AppToastType.error,
          clearsBottomNav: false,
        );
      }
    } finally {
      if (mounted) setState(() => _appleSignInLoading = false);
    }
  }

  /// Official "Sign in with Apple" button (Apple HIG styling — required by Apple's
  /// design guidelines, not a custom look). White variant because the app's
  /// cinematic background is dark and Apple specifies white-on-dark / black-on-light.
  /// Only rendered when [_appleAvailable].
  Widget _buildAppleSignInButton(AuthProvider auth, double width) {
    final disabled = auth.isLoading || _appleSignInLoading;
    return Center(
      child: SizedBox(
        width: width,
        child: Opacity(
          opacity: disabled ? 0.6 : 1.0,
          child: SignInWithAppleButton(
            onPressed: disabled ? () {} : _handleAppleSignIn,
            text: AppLocalizations.of(context).continueWithApple,
            height: 44,
            borderRadius: BorderRadius.circular(16),
            style: SignInWithAppleButtonStyle.white,
          ),
        ),
      ),
    );
  }

  Future<void> _handleGoogleAuthEvent(GoogleSignInAuthenticationEvent event) async {
    if (!mounted) return;
    final user = switch (event) {
      GoogleSignInAuthenticationEventSignIn() => event.user,
      GoogleSignInAuthenticationEventSignOut() => null,
    };
    if (user == null) return;

    final idToken = user.authentication.idToken;
    if (idToken == null) return;

    setState(() => _googleSignInLoading = true);
    try {
      final authProvider = context.read<AuthProvider>();
      // Capture any invitation token before the async gap so we can honor it
      // after sign-in (a user arriving from an invite lands on /login?invitation=).
      final invitationToken =
          GoRouterState.of(context).uri.queryParameters['invitation'];
      final success = await authProvider.googleLogin(idToken);
      // Navigate explicitly on success rather than relying solely on the
      // GoRouter redirect, which can race the auth-state notification.
      if (success && mounted) {
        if (invitationToken != null && invitationToken.isNotEmpty) {
          context.go('/accept-invitation/$invitationToken');
        } else {
          context.go(PostAuthRedirect.destination());
        }
      }
    } on GoogleAccountNotFoundException {
      // No account for this Google email — send the user to sign up, carrying any
      // invitation token along so the invite survives the detour through register.
      if (mounted) {
        final invitationToken =
            GoRouterState.of(context).uri.queryParameters['invitation'];
        final target = (invitationToken != null && invitationToken.isNotEmpty)
            ? '/register?google_no_account=1&invitation=$invitationToken'
            : '/register?google_no_account=1';
        context.go(target);
      }
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          AppLocalizations.of(context).googleSignInFailedDetail(e.toString()),
          type: AppToastType.error,
          clearsBottomNav: false,
        );
      }
    } finally {
      if (mounted) setState(() => _googleSignInLoading = false);
    }
  }

  Future<void> _handleLogin() async {
    if (!_formKey.currentState!.validate()) return;

    final email = _emailController.text.trim();
    final password = _passwordController.text;
    final authProvider = context.read<AuthProvider>();
    final success = await authProvider.login(email, password);

    // Navigation handled by GoRouter redirect when auth state changes.
    // Only handle MFA case here.
    final challenge = authProvider.mfaChallenge;
    if (!success && mounted && authProvider.status == AuthStatus.mfaRequired &&
        challenge != null) {
      await _autoSelectMfaMethod(challenge);
      return;
    }

    // On a successful password login, offer to set up biometric quick-login for
    // next time (mobile only). The password is in scope here; it is handed
    // straight to the secure enrolment and never persisted otherwise.
    if (success) {
      await _maybeOfferBiometricEnrollment(email, password, authProvider.user);
    }
  }

  /// Show the post-login "set up {biometric}?" offer when eligible.
  ///
  /// The dialog is presented on the ROOT navigator's context (captured before any
  /// async gap) because the post-login GoRouter redirect replaces the login route
  /// with the dashboard — a dialog anchored to the login page's own context would
  /// be at risk if the page were disposed mid-check. The pushed dialog itself
  /// survives the redirect (Flutter re-anchors pageless routes onto the new page),
  /// so it floats over the dashboard exactly as intended.
  Future<void> _maybeOfferBiometricEnrollment(
      String email, String password, User? user) async {
    if (kIsWeb || user == null) return;

    final NavigatorState rootNav;
    try {
      rootNav = Navigator.of(context, rootNavigator: true);
    } catch (_) {
      return;
    }

    try {
      if (!await biometricService.isDeviceSupported()) return;
      if (!await biometricService.canCheckBiometrics()) return;
      if (await biometricService.isBiometricLoginEnabled()) return;
      if (await biometricService.isBiometricOfferDeclined()) return;
    } catch (_) {
      return;
    }

    final typeName = await biometricService.getBiometricTypeName();
    if (!rootNav.mounted) return;

    await showDialog<void>(
      context: rootNav.context,
      // Not barrier-dismissible: a tap-outside must not skip the dialog, or the
      // "Not now" decline never persists and the offer re-nags on every login.
      barrierDismissible: false,
      barrierColor: Colors.black.withValues(alpha: 0.6),
      builder: (ctx) => _BiometricEnrollmentDialog(
        typeName: typeName,
        email: email,
        password: password,
        userId: user.id,
      ),
    );
  }

  /// Mobile-only: trigger interactive Google sign-in.
  /// On web, the renderButton handles this.
  Future<void> _handleGoogleSignIn() async {
    if (kIsWeb) return; // Web uses renderButton
    setState(() => _googleSignInLoading = true);
    try {
      await googleSignInService.authenticate();
      // Result comes via _handleGoogleAuthEvent
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          AppLocalizations.of(context).googleSignInFailedDetail(e.toString()),
          type: AppToastType.error,
          clearsBottomNav: false,
        );
        setState(() => _googleSignInLoading = false);
      }
    }
  }

  /// Build the Google sign-in button:
  /// - Web: Google's official renderButton (once initialized)
  /// - Mobile: custom glass outline button
  Widget _buildGoogleSignInButton(AuthProvider auth, double width) {
    if (kIsWeb && _googleInitialized) {
      final webButton = buildGoogleSignInWebButton(minimumWidth: width);
      if (webButton != null) {
        return Center(
          child: SizedBox(
            width: width,
            height: 44,
            child: _googleSignInLoading
                ? Center(
                    child: SizedBox(
                      width: 20,
                      height: 20,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: Theme.of(context).colorScheme.onSurface.withValues(alpha: 0.5),
                      ),
                    ),
                  )
                : webButton,
          ),
        );
      }
    }
    // Mobile (and web before Google's renderButton is ready): the shared
    // branded button, so login and register cannot look like different products.
    return GoogleAuthButton(
      label: AppLocalizations.of(context).continueWithGoogle,
      isLoading: _googleSignInLoading,
      onPressed: (auth.isLoading || _googleSignInLoading) ? null : _handleGoogleSignIn,
    );
  }

  /// Methods the account offers that are actually usable on THIS platform.
  /// Passkey counts only where the platform supports WebAuthn (web included).
  List<String> _usableMethods(MfaChallenge challenge) {
    return challenge.availableMethods.where((m) {
      if (m.toLowerCase() == 'passkey') return _passkeySupported && ServerEdition.showPasskeys;
      return true;
    }).toList();
  }

  Future<void> _autoSelectMfaMethod(MfaChallenge challenge) async {
    final hasPasskey = ServerEdition.showPasskeys &&
        challenge.availableMethods.any((m) => m.toLowerCase() == 'passkey');

    // Passkeys work on web + native via the `passkeys` package — prefer them
    // when the account has one AND this platform can actually use it.
    if (hasPasskey) {
      final supported = await passkeyService.isSupported();
      if (mounted) setState(() => _passkeySupported = supported);
      if (supported) {
        _selectMfaMethod('passkey');
        return;
      }
    }

    // No usable passkey → pick a code-based method (TOTP, or the email fallback
    // the backend offers for passkey-only accounts).
    final codeMethods = challenge.availableMethods
        .where((m) => m.toLowerCase() != 'passkey')
        .toList();

    if (codeMethods.isEmpty) return;
    if (codeMethods.length == 1) {
      _selectMfaMethod(codeMethods.first);
    } else if (challenge.preferredMethod != null &&
        codeMethods.contains(challenge.preferredMethod)) {
      _selectMfaMethod(challenge.preferredMethod!);
    } else {
      _selectMfaMethod(codeMethods.first);
    }
  }

  Future<void> _handlePasskeyVerify() async {
    final authProvider = context.read<AuthProvider>();
    await authProvider.loginWithPasskey(
      username: _emailController.text.trim(),
      rememberDevice: _rememberDevice,
    );
    // Success → GoRouter redirect handles navigation. Failure → provider sets the
    // error message and keeps us on the MFA screen.
  }

  Future<void> _selectMfaMethod(String method) async {
    setState(() => _selectedMfaMethod = method);

    if (method.toLowerCase() == 'email') {
      await _sendEmailMfaCode();
    }
  }

  Future<void> _sendEmailMfaCode() async {
    final authProvider = context.read<AuthProvider>();
    final mfaToken = authProvider.mfaChallenge?.mfaToken;
    if (mfaToken == null) return;

    setState(() => _sendingEmailCode = true);

    try {
      await authService.sendLoginMfaCode(mfaToken);
    } catch (_) {
      // Error handled silently — user can resend
    } finally {
      if (mounted) setState(() => _sendingEmailCode = false);
    }
  }

  Future<void> _handleMfaVerify() async {
    if (_mfaCodeController.text.isEmpty) return;

    final authProvider = context.read<AuthProvider>();
    await authProvider.verifyMfa(
      _mfaCodeController.text.trim(),
      rememberDevice: _rememberDevice,
    );
  }

  void _handleCancelMfa() {
    final authProvider = context.read<AuthProvider>();
    authProvider.cancelMfa();
    _mfaCodeController.clear();
    setState(() {
      _selectedMfaMethod = null;
      _rememberDevice = false;
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Stack(
        children: [
          // 0. Reactive Background
          Positioned.fill(
            child: InteractiveBackgroundLayer(mouseNotifier: _globalMousePos),
          ),

          // 1. Noise Overlay
          const Positioned.fill(child: NoiseOverlay(opacity: 0.1)),

          // 2. Main Content
          Center(
            child: SingleChildScrollView(
              controller: _scrollController,
              // Extra top inset so the absolutely-positioned Back button
              // (top: 32, ~40px tall) doesn't sit on top of the hero title
              // on narrow screens.
              padding: const EdgeInsets.fromLTRB(24, 96, 24, 48),
              // Centering happens INSIDE the scroll view, not around it. Wrapping
              // the scroll view in Center hands it loose constraints, so it
              // shrink-wraps to the card and Flutter's automatic web scrollbar
              // rides that box's edge — a scrollbar stranded mid-page.
              child: Center(
                child: FadeTransition(
                  opacity: _fadeAnimation,
                  child: Consumer<AuthProvider>(
                    builder: (context, auth, _) {
                      // Guard the mfaChallenge! deref in _buildMfaView: only show it
                      // when a challenge is actually present. Defensive — status and
                      // challenge are set together — but this is a fatal-if-wrong path.
                      if (auth.status == AuthStatus.mfaRequired &&
                          auth.mfaChallenge != null) {
                        return _buildMfaView(theme, auth);
                      }
                      return _buildLoginView(theme, auth);
                    },
                  ),
                ),
              ),
            ),
          ),

          // 3. Back button — web only. The marketing site lives at the domain root, so a
          //    "back to landing" affordance only makes sense on web; on mobile there's
          //    nowhere to go back to (it would just no-op back to /login), so hide it.
          if (kIsWeb)
            Positioned(
              top: 32,
              left: 32,
              child: _GlassButton(
                onTap: () => goToMarketingHome(context),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(Icons.arrow_back, size: 16, color: theme.colorScheme.onSurface.withValues(alpha: 0.7)),
                    const SizedBox(width: 8),
                    Text(AppLocalizations.of(context).back, style: theme.textTheme.bodyMedium),
                  ],
                ),
              ),
            ),

          // 4. Mouse Tracker
          Positioned.fill(
            child: MouseRegion(
              hitTestBehavior: HitTestBehavior.translucent,
              onHover: (e) => _globalMousePos.value = e.localPosition,
              child: const SizedBox.expand(),
            ),
          ),
        ],
      ),
    );
  }

  Widget _buildLoginView(ThemeData theme, AuthProvider auth) {
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 440),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Hero Text
          Text(
            AppLocalizations.of(context).loginWelcome,
            style: theme.textTheme.displayLarge?.copyWith(fontSize: 48),
          ),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).loginSubtitle,
            style: theme.textTheme.displayMedium?.copyWith(fontSize: 24),
          ),
          const SizedBox(height: 48),

          // Glass Card
          _GlassCard(
            child: Form(
              key: _formKey,
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  // Error
                  if (auth.errorMessage != null) ...[
                    _ErrorBanner(message: auth.errorMessage!),
                    const SizedBox(height: 20),
                  ],

                  // Email and password, unless the server turned passwords off for single sign-on.
                  if (ServerEdition.showPasswordLogin) ...[
                  // Email
                  _CinematicTextField(
                    controller: _emailController,
                    label: AppLocalizations.of(context).emailLabel,
                    hint: 'you@example.com',
                    keyboardType: TextInputType.emailAddress,
                    textInputAction: TextInputAction.next,
                    focusNode: _emailFocusNode,
                    onFieldSubmitted: (_) => _passwordFocusNode.requestFocus(),
                    prefixIcon: Icons.mail_outline,
                    validator: (value) {
                      if (value == null || value.isEmpty) return AppLocalizations.of(context).emailRequired;
                      if (!value.contains('@')) return AppLocalizations.of(context).emailInvalid;
                      return null;
                    },
                  ),
                  const SizedBox(height: 20),

                  // Password
                  _CinematicTextField(
                    controller: _passwordController,
                    label: AppLocalizations.of(context).passwordLabel,
                    hint: '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022',
                    obscureText: _obscurePassword,
                    textInputAction: TextInputAction.done,
                    focusNode: _passwordFocusNode,
                    onFieldSubmitted: (_) => _handleLogin(),
                    prefixIcon: Icons.lock_outline,
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscurePassword ? Icons.visibility_outlined : Icons.visibility_off_outlined,
                        color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                        size: 20,
                      ),
                      onPressed: () => setState(() => _obscurePassword = !_obscurePassword),
                    ),
                    validator: (value) {
                      if (value == null || value.isEmpty) return AppLocalizations.of(context).passwordRequired;
                      return null;
                    },
                  ),
                  const SizedBox(height: 12),

                  // Forgot Password: a self-hosted server with no mail server cannot send the link.
                  if (ServerEdition.current.value.email)
                  Align(
                    alignment: Alignment.centerRight,
                    child: TextButton(
                      onPressed: () => context.go('/forgot-password'),
                      style: TextButton.styleFrom(
                        padding: const EdgeInsets.symmetric(horizontal: 4, vertical: 2),
                        minimumSize: Size.zero,
                        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
                      ),
                      child: Text(
                        AppLocalizations.of(context).forgotPassword,
                        style: TextStyle(
                          color: theme.colorScheme.secondary,
                          fontSize: 13,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ),
                  ),
                  const SizedBox(height: 24),

                  // Sign In Button
                  _AccentButton(
                    onPressed: auth.isLoading ? null : _handleLogin,
                    isLoading: auth.isLoading,
                    label: AppLocalizations.of(context).signIn,
                  ),

                  // Biometric quick-login — only when the user has enrolled it on
                  // this device (mobile only). Re-triggers the prompt manually.
                  if (_biometricLoginEnabled) ...[
                    const SizedBox(height: 16),
                    _GlassOutlineButton(
                      onPressed: auth.isLoading ? null : _handleBiometricLogin,
                      icon: Icon(Icons.fingerprint,
                          size: 22, color: theme.colorScheme.secondary),
                      label: AppLocalizations.of(context).signInWithBiometric(_biometricTypeName),
                    ),
                  ],
                  const SizedBox(height: 24),
                  ],

                  // Social sign-in only when this server offers it (a self-hosted server
                  // without a Google client has nothing to show).
                  ListenableBuilder(
                    listenable: ServerEdition.changes,
                    builder: (context, _) {
                      final showGoogle = ServerEdition.showGoogleSignIn;
                      final showApple = _appleAvailable && ServerEdition.showAppleSignIn;
                      final showOidc = ServerEdition.showOidcSignIn;
                      if (!showGoogle && !showApple && !showOidc) return const SizedBox.shrink();
                      return Column(
                        children: [
                          if (ServerEdition.showPasswordLogin) ...[
                            _OrDivider(),
                            const SizedBox(height: 24),
                          ],
                          LayoutBuilder(
                            builder: (context, constraints) {
                              final oauthWidth = constraints.maxWidth.isFinite
                                  ? constraints.maxWidth.clamp(0.0, 400.0)
                                  : 320.0;
                              return Column(
                                children: [
                                  if (showOidc) ...[
                                    OidcSignInButton(
                                      label: AppLocalizations.of(context)
                                          .oidcSignInWith(ServerEdition.oidcDisplayName),
                                      isLoading: _oidcLoading,
                                      width: oauthWidth.toDouble(),
                                      onPressed: auth.isLoading ? null : _handleOidcSignIn,
                                    ),
                                    if (showGoogle || showApple) const SizedBox(height: 16),
                                  ],
                                  if (showGoogle) _buildGoogleSignInButton(auth, oauthWidth),
                                  if (showApple) ...[
                                    if (showGoogle) const SizedBox(height: 16),
                                    _buildAppleSignInButton(auth, oauthWidth),
                                  ],
                                ],
                              );
                            },
                          ),
                        ],
                      );
                    },
                  ),
                ],
              ),
            ),
          ),

          const SizedBox(height: 32),

          // Register link
          ValueListenableBuilder<ServerCapabilities>(
            valueListenable: ServerEdition.current,
            builder: (context, edition, _) => Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text(
                AppLocalizations.of(context).noAccountPrompt,
                style: theme.textTheme.bodyMedium,
              ),
              MouseRegion(
                cursor: SystemMouseCursors.click,
                child: GestureDetector(
                  onTap: () => context.go('/register'),
                  child: Text(
                    // Not "free": your own server has no plan. A new server's first account is its admin.
                    edition.isSelfHost || AppConfig.isCustomServer
                        ? AppLocalizations.of(context).createAccountLink
                        : AppLocalizations.of(context).getStartedFree,
                    style: TextStyle(
                      color: theme.colorScheme.secondary,
                      fontWeight: FontWeight.w600,
                      fontSize: 16,
                    ),
                  ),
                ),
              ),
            ],
          ),
          ),

          // Which server this app signs in to (BuoyAI Cloud or one you run yourself).
          const SizedBox(height: 16),
          ServerPickerControl(onChanged: _onServerChanged),
        ],
      ),
    );
  }

  /// The user picked another server: saved quick sign-in was cleared with the old server, and
  /// the form follows what the new one offers (Google, Apple, forgot password).
  void _onServerChanged() {
    if (!mounted) return;
    context.read<AuthProvider>().clearError();
    setState(() => _biometricLoginEnabled = false);
  }

  Widget _buildMfaView(ThemeData theme, AuthProvider auth) {
    final challenge = auth.mfaChallenge!;
    final isPasskey = _selectedMfaMethod?.toLowerCase() == 'passkey';
    final methodDisplay = _selectedMfaMethod?.toLowerCase() == 'totp'
        ? 'authenticator app'
        : _selectedMfaMethod?.toLowerCase() == 'email'
            ? 'email'
            : _selectedMfaMethod?.toLowerCase() ?? 'authenticator app';
    // An emailed code is offered as a fallback for passkey-only accounts.
    final hasEmailFallback =
        challenge.availableMethods.any((m) => m.toLowerCase() == 'email');
    final usableCount = _usableMethods(challenge).length;

    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 440),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Shield Icon
          Container(
            width: 72,
            height: 72,
            decoration: BoxDecoration(
              shape: BoxShape.circle,
              color: theme.colorScheme.secondary.withValues(alpha: 0.1),
              border: Border.all(
                color: theme.colorScheme.secondary.withValues(alpha: 0.3),
              ),
            ),
            child: Icon(
              isPasskey ? Icons.fingerprint : Icons.shield_outlined,
              size: 32,
              color: theme.colorScheme.secondary,
            ),
          ),
          const SizedBox(height: 24),

          Text(
            AppLocalizations.of(context).verificationRequired,
            style: theme.textTheme.displayLarge?.copyWith(fontSize: 36),
          ),
          const SizedBox(height: 8),
          Text(
            isPasskey
                ? AppLocalizations.of(context).usePasskeyToFinish
                : AppLocalizations.of(context).enterCodeFrom(methodDisplay),
            style: theme.textTheme.bodyMedium,
            textAlign: TextAlign.center,
          ),
          const SizedBox(height: 48),

          _GlassCard(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (auth.errorMessage != null) ...[
                  _ErrorBanner(message: auth.errorMessage!),
                  const SizedBox(height: 20),
                ],

                // MFA Code Input (hidden on the passkey path — nothing to type)
                if (!isPasskey) ...[
                  _CinematicTextField(
                    controller: _mfaCodeController,
                    label: AppLocalizations.of(context).verificationCodeLabel,
                    hint: '000000',
                    keyboardType: TextInputType.number,
                    prefixIcon: Icons.pin_outlined,
                    maxLength: 6,
                    textAlign: TextAlign.center,
                    style: GoogleFonts.firaCode(
                      fontSize: 28,
                      letterSpacing: 12,
                      color: theme.colorScheme.onSurface,
                    ),
                  ),
                  const SizedBox(height: 20),
                ],

                // Remember Device
                MouseRegion(
                  cursor: SystemMouseCursors.click,
                  child: GestureDetector(
                    onTap: () => setState(() => _rememberDevice = !_rememberDevice),
                    child: Row(
                      children: [
                        AnimatedContainer(
                          duration: const Duration(milliseconds: 200),
                          width: 20,
                          height: 20,
                          decoration: BoxDecoration(
                            borderRadius: BorderRadius.circular(6),
                            color: _rememberDevice
                                ? theme.colorScheme.secondary
                                : Colors.transparent,
                            border: Border.all(
                              color: _rememberDevice
                                  ? theme.colorScheme.secondary
                                  : theme.colorScheme.onSurface.withValues(alpha: 0.3),
                            ),
                          ),
                          child: _rememberDevice
                              ? Icon(Icons.check, size: 14, color: theme.scaffoldBackgroundColor)
                              : null,
                        ),
                        const SizedBox(width: 12),
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(
                                AppLocalizations.of(context).rememberThisDevice,
                                style: TextStyle(
                                  color: theme.colorScheme.onSurface.withValues(alpha: 0.9),
                                  fontSize: 14,
                                  fontWeight: FontWeight.w500,
                                ),
                              ),
                              Text(
                                AppLocalizations.of(context).skipVerificationThirtyDays,
                                style: TextStyle(
                                  color: theme.colorScheme.onSurface.withValues(alpha: 0.4),
                                  fontSize: 12,
                                ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
                const SizedBox(height: 24),

                // Primary action: run the passkey ceremony, or verify a code.
                if (isPasskey)
                  _AccentButton(
                    onPressed: auth.isLoading ? null : _handlePasskeyVerify,
                    isLoading: auth.isLoading,
                    label: AppLocalizations.of(context).usePasskey,
                  )
                else
                  _AccentButton(
                    onPressed: (auth.isLoading || _sendingEmailCode) ? null : _handleMfaVerify,
                    isLoading: auth.isLoading || _sendingEmailCode,
                    label: AppLocalizations.of(context).verify,
                  ),

                // Secondary actions: fallback / resend / change method
                const SizedBox(height: 16),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    // Passkey path: offer the emailed-code fallback (passkey-only accounts)
                    if (isPasskey && hasEmailFallback)
                      TextButton(
                        onPressed: auth.isLoading ? null : () => _selectMfaMethod('email'),
                        child: Text(
                          AppLocalizations.of(context).useEmailCodeInstead,
                          style: TextStyle(
                            color: theme.colorScheme.secondary.withValues(alpha: 0.8),
                            fontSize: 13,
                          ),
                        ),
                      ),
                    if (!isPasskey && _selectedMfaMethod?.toLowerCase() == 'email')
                      TextButton(
                        onPressed: _sendingEmailCode ? null : _sendEmailMfaCode,
                        child: Text(
                          AppLocalizations.of(context).resendCode,
                          style: TextStyle(
                            color: theme.colorScheme.secondary.withValues(alpha: 0.8),
                            fontSize: 13,
                          ),
                        ),
                      ),
                    if (usableCount > 1 && !(isPasskey && hasEmailFallback))
                      TextButton(
                        onPressed: () => _showMethodPicker(challenge),
                        child: Text(
                          AppLocalizations.of(context).useDifferentMethod,
                          style: TextStyle(
                            color: theme.colorScheme.secondary.withValues(alpha: 0.8),
                            fontSize: 13,
                          ),
                        ),
                      ),
                  ],
                ),
              ],
            ),
          ),

          const SizedBox(height: 24),

          // Back to Login
          MouseRegion(
            cursor: SystemMouseCursors.click,
            child: GestureDetector(
              onTap: _handleCancelMfa,
              child: Text(
                AppLocalizations.of(context).authBackToSignIn,
                style: TextStyle(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
                  fontSize: 14,
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }

  void _showMethodPicker(MfaChallenge challenge) {
    final theme = Theme.of(context);
    // Only offer methods usable on this platform (passkey where WebAuthn is
    // supported, including web).
    final methods = _usableMethods(challenge);

    showDialog(
      context: context,
      builder: (ctx) => Dialog(
        backgroundColor: Colors.transparent,
        child: ClipRRect(
          borderRadius: BorderRadius.circular(24),
          child: BackdropFilter(
            filter: ImageFilter.blur(sigmaX: 16, sigmaY: 16),
            child: Container(
              constraints: const BoxConstraints(maxWidth: 360),
              padding: const EdgeInsets.all(32),
              decoration: BoxDecoration(
                color: theme.scaffoldBackgroundColor.withValues(alpha: 0.85),
                borderRadius: BorderRadius.circular(24),
                border: Border.all(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.08),
                ),
              ),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    AppLocalizations.of(context).chooseMethod,
                    style: theme.textTheme.headlineMedium,
                  ),
                  const SizedBox(height: 24),
                  ...methods.map((method) {
                    final isTotp = method.toLowerCase() == 'totp';
                    final isEmail = method.toLowerCase() == 'email';
                    final isPasskey = method.toLowerCase() == 'passkey';
                    return Padding(
                      padding: const EdgeInsets.only(bottom: 12),
                      child: _GlassOutlineButton(
                        onPressed: () {
                          Navigator.pop(ctx);
                          _selectMfaMethod(method);
                        },
                        icon: Icon(
                          isTotp
                              ? Icons.phone_android
                              : isEmail
                                  ? Icons.mail_outline
                                  : isPasskey
                                      ? Icons.fingerprint
                                      : Icons.security,
                          size: 20,
                          color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
                        ),
                        label: isTotp
                            ? AppLocalizations.of(context).authenticatorApp
                            : isEmail
                                ? AppLocalizations.of(context).emailCode
                                : isPasskey
                                    ? AppLocalizations.of(context).passkey
                                    : method,
                      ),
                    );
                  }),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

// ─── Shared Cinematic Widgets ───────────────────────────────────────────────

class _GlassCard extends StatelessWidget {
  final Widget child;
  const _GlassCard({required this.child});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return ClipRRect(
      borderRadius: BorderRadius.circular(32),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
        child: Container(
          padding: const EdgeInsets.all(36),
          decoration: BoxDecoration(
            color: theme.scaffoldBackgroundColor.withValues(alpha: 0.55),
            borderRadius: BorderRadius.circular(32),
            border: Border.all(
              color: theme.colorScheme.onSurface.withValues(alpha: 0.06),
            ),
            boxShadow: [
              BoxShadow(
                color: Colors.black.withValues(alpha: 0.3),
                blurRadius: 40,
                offset: const Offset(0, 16),
              ),
            ],
          ),
          child: child,
        ),
      ),
    );
  }
}

class _GlassButton extends StatelessWidget {
  final VoidCallback onTap;
  final Widget child;
  const _GlassButton({required this.onTap, required this.child});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: ClipRRect(
          borderRadius: BorderRadius.circular(20),
          child: BackdropFilter(
            filter: ImageFilter.blur(sigmaX: 8, sigmaY: 8),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
              decoration: BoxDecoration(
                color: theme.scaffoldBackgroundColor.withValues(alpha: 0.4),
                borderRadius: BorderRadius.circular(20),
                border: Border.all(
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.08),
                ),
              ),
              child: child,
            ),
          ),
        ),
      ),
    );
  }
}

class _CinematicTextField extends StatelessWidget {
  final TextEditingController controller;
  final String label;
  final String? hint;
  final TextInputType? keyboardType;
  final bool obscureText;
  final IconData? prefixIcon;
  final Widget? suffixIcon;
  final String? Function(String?)? validator;
  final int? maxLength;
  final TextAlign textAlign;
  final TextStyle? style;
  final TextInputAction? textInputAction;
  final ValueChanged<String>? onFieldSubmitted;
  final FocusNode? focusNode;

  const _CinematicTextField({
    required this.controller,
    required this.label,
    this.hint,
    this.keyboardType,
    this.obscureText = false,
    this.prefixIcon,
    this.suffixIcon,
    this.validator,
    this.maxLength,
    this.textAlign = TextAlign.start,
    this.style,
    this.textInputAction,
    this.onFieldSubmitted,
    this.focusNode,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final borderColor = theme.colorScheme.onSurface.withValues(alpha: 0.1);
    final focusBorderColor = theme.colorScheme.secondary.withValues(alpha: 0.5);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          label,
          style: GoogleFonts.sora(
            fontSize: 13,
            fontWeight: FontWeight.w500,
            color: theme.colorScheme.onSurface.withValues(alpha: 0.6),
            letterSpacing: 0.5,
          ),
        ),
        const SizedBox(height: 8),
        TextFormField(
          controller: controller,
          keyboardType: keyboardType,
          obscureText: obscureText,
          autocorrect: false,
          maxLength: maxLength,
          textAlign: textAlign,
          textInputAction: textInputAction,
          onFieldSubmitted: onFieldSubmitted,
          focusNode: focusNode,
          style: style ?? GoogleFonts.sora(
            fontSize: 15,
            color: theme.colorScheme.onSurface,
          ),
          validator: validator,
          decoration: InputDecoration(
            hintText: hint,
            hintStyle: GoogleFonts.sora(
              fontSize: 15,
              color: theme.colorScheme.onSurface.withValues(alpha: 0.2),
            ),
            counterText: '',
            prefixIcon: prefixIcon != null
                ? Icon(prefixIcon, size: 20, color: theme.colorScheme.onSurface.withValues(alpha: 0.3))
                : null,
            suffixIcon: suffixIcon,
            filled: true,
            fillColor: theme.scaffoldBackgroundColor.withValues(alpha: 0.4),
            contentPadding: const EdgeInsets.symmetric(horizontal: 16, vertical: 16),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: borderColor),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: borderColor),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: focusBorderColor, width: 1.5),
            ),
            errorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: theme.colorScheme.error.withValues(alpha: 0.5)),
            ),
            focusedErrorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: theme.colorScheme.error, width: 1.5),
            ),
          ),
        ),
      ],
    );
  }
}

class _AccentButton extends StatefulWidget {
  final VoidCallback? onPressed;
  final bool isLoading;
  final String label;
  const _AccentButton({this.onPressed, this.isLoading = false, required this.label});

  @override
  State<_AccentButton> createState() => _AccentButtonState();
}

class _AccentButtonState extends State<_AccentButton> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent = theme.colorScheme.secondary;

    return MouseRegion(
      cursor: widget.onPressed != null ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.symmetric(vertical: 18),
          decoration: BoxDecoration(
            color: widget.onPressed != null
                ? (_hovering ? accent : accent.withValues(alpha: 0.85))
                : accent.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(16),
            boxShadow: _hovering && widget.onPressed != null
                ? [BoxShadow(color: accent.withValues(alpha: 0.3), blurRadius: 20, offset: const Offset(0, 4))]
                : null,
          ),
          alignment: Alignment.center,
          child: widget.isLoading
              ? SizedBox(
                  width: 22,
                  height: 22,
                  child: CircularProgressIndicator(
                    strokeWidth: 2.5,
                    color: theme.scaffoldBackgroundColor,
                  ),
                )
              : Text(
                  widget.label,
                  style: GoogleFonts.sora(
                    fontSize: 16,
                    fontWeight: FontWeight.w600,
                    color: theme.scaffoldBackgroundColor,
                    letterSpacing: 0.3,
                  ),
                ),
        ),
      ),
    );
  }
}

class _GlassOutlineButton extends StatefulWidget {
  final VoidCallback? onPressed;
  final Widget icon;
  final String label;
  const _GlassOutlineButton({this.onPressed, required this.icon, required this.label});

  @override
  State<_GlassOutlineButton> createState() => _GlassOutlineButtonState();
}

class _GlassOutlineButtonState extends State<_GlassOutlineButton> {
  bool _hovering = false;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return MouseRegion(
      cursor: widget.onPressed != null ? SystemMouseCursors.click : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.symmetric(vertical: 16),
          decoration: BoxDecoration(
            color: _hovering
                ? theme.colorScheme.onSurface.withValues(alpha: 0.05)
                : Colors.transparent,
            borderRadius: BorderRadius.circular(16),
            border: Border.all(
              color: theme.colorScheme.onSurface.withValues(alpha: _hovering ? 0.15 : 0.1),
            ),
          ),
          child: Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              widget.icon,
              const SizedBox(width: 12),
              Text(
                widget.label,
                style: GoogleFonts.sora(
                  fontSize: 15,
                  fontWeight: FontWeight.w500,
                  color: theme.colorScheme.onSurface.withValues(alpha: 0.8),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _OrDivider extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dividerColor = theme.colorScheme.onSurface.withValues(alpha: 0.08);
    return Row(
      children: [
        Expanded(child: Divider(color: dividerColor, thickness: 1)),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Text(
            AppLocalizations.of(context).authOr,
            style: GoogleFonts.sora(
              fontSize: 13,
              color: theme.colorScheme.onSurface.withValues(alpha: 0.3),
            ),
          ),
        ),
        Expanded(child: Divider(color: dividerColor, thickness: 1)),
      ],
    );
  }
}

/// Post-login offer to enable biometric quick-login. Cinematic glass dialog
/// matching the MFA method-picker. "Enable" runs the secure enrolment (which
/// itself prompts for a biometric); "Not now" persists the decline so it stops
/// appearing. Uses only its own [BuildContext] — the login page that spawned it
/// may already be gone (post-login redirect).
class _BiometricEnrollmentDialog extends StatefulWidget {
  final String typeName;
  final String email;
  final String password;
  final String userId;

  const _BiometricEnrollmentDialog({
    required this.typeName,
    required this.email,
    required this.password,
    required this.userId,
  });

  @override
  State<_BiometricEnrollmentDialog> createState() =>
      _BiometricEnrollmentDialogState();
}

class _BiometricEnrollmentDialogState
    extends State<_BiometricEnrollmentDialog> {
  bool _enabling = false;

  Future<void> _enable() async {
    setState(() => _enabling = true);
    final ok = await biometricService.enableBiometricLogin(
      email: widget.email,
      password: widget.password,
      userId: widget.userId,
    );
    if (!mounted) return;
    Navigator.of(context).pop();
    showAppToast(
      context,
      ok
          ? AppLocalizations.of(context).quickLoginEnabled(widget.typeName)
          : AppLocalizations.of(context).couldntEnableLogin(widget.typeName),
      type: ok ? AppToastType.success : AppToastType.error,
      clearsBottomNav: false,
    );
  }

  Future<void> _decline() async {
    await biometricService.setBiometricOfferDeclined(true);
    if (mounted) Navigator.of(context).pop();
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Dialog(
      backgroundColor: Colors.transparent,
      child: ClipRRect(
        borderRadius: BorderRadius.circular(24),
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 16, sigmaY: 16),
          child: Container(
            constraints: const BoxConstraints(maxWidth: 360),
            padding: const EdgeInsets.all(32),
            decoration: BoxDecoration(
              color: theme.scaffoldBackgroundColor.withValues(alpha: 0.85),
              borderRadius: BorderRadius.circular(24),
              border: Border.all(
                color: theme.colorScheme.onSurface.withValues(alpha: 0.08),
              ),
            ),
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Container(
                  width: 64,
                  height: 64,
                  decoration: BoxDecoration(
                    shape: BoxShape.circle,
                    color: theme.colorScheme.secondary.withValues(alpha: 0.1),
                    border: Border.all(
                      color: theme.colorScheme.secondary.withValues(alpha: 0.3),
                    ),
                  ),
                  child: Icon(Icons.fingerprint,
                      size: 30, color: theme.colorScheme.secondary),
                ),
                const SizedBox(height: 20),
                Text(
                  AppLocalizations.of(context).authQuickLoginOffer(widget.typeName),
                  textAlign: TextAlign.center,
                  style: theme.textTheme.headlineMedium?.copyWith(fontSize: 20),
                ),
                const SizedBox(height: 12),
                Text(
                  AppLocalizations.of(context).authQuickLoginBody(widget.typeName),
                  textAlign: TextAlign.center,
                  style: GoogleFonts.sora(
                    fontSize: 13,
                    height: 1.5,
                    color: theme.colorScheme.onSurface.withValues(alpha: 0.7),
                  ),
                ),
                const SizedBox(height: 28),
                _AccentButton(
                  onPressed: _enabling ? null : _enable,
                  isLoading: _enabling,
                  label: 'Enable',
                ),
                const SizedBox(height: 8),
                TextButton(
                  onPressed: _enabling ? null : _decline,
                  child: Text(
                    AppLocalizations.of(context).consentNotNow,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.5),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _ErrorBanner extends StatelessWidget {
  final String message;
  const _ErrorBanner({required this.message});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: theme.colorScheme.error.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: theme.colorScheme.error.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline, size: 18, color: theme.colorScheme.error.withValues(alpha: 0.8)),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              message,
              style: GoogleFonts.sora(
                fontSize: 13,
                color: theme.colorScheme.error.withValues(alpha: 0.9),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
