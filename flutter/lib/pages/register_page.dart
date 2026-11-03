import 'dart:async';
import 'dart:ui';
import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import '../router/post_auth_redirect.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:google_sign_in/google_sign_in.dart';
import 'package:provider/provider.dart';
import 'package:sign_in_with_apple/sign_in_with_apple.dart';
import '../l10n/generated/app_localizations.dart';
import '../config/portal_mode.dart';
import '../config/server_capabilities.dart';
import '../models/family_models.dart';
import '../models/auth_models.dart';
import '../providers/auth_provider.dart';
import '../widgets/google_auth_button.dart';
import '../widgets/oidc_sign_in_button.dart';
import '../services/oidc_sign_in_service.dart';
import '../services/analytics_service.dart';
import '../services/referral_service.dart';
import '../services/registration_service.dart';
import '../utils/formatters.dart';
import '../services/family_service.dart';
import '../services/google_sign_in_service.dart';
import '../services/apple_sign_in_service.dart';
import '../widgets/app_toast.dart';
import '../widgets/google_sign_in_button.dart';
import '../widgets/server_picker.dart';
import '../widgets/cinematic/glass_settings.dart' show GlassNote;
import '../widgets/cinematic/noise_overlay.dart';
import '../widgets/cinematic/backgrounds/interactive_background_layer.dart';
import '../utils/web_navigation.dart';

/// Cinematic Register Page
///
/// Full cinematic registration with Google Sign-In, email/password,
/// password strength indicator, and family invitation support.
class RegisterPage extends StatefulWidget {
  final String? invitationToken;

  /// True when the user was redirected here after attempting Google sign-in with
  /// no existing account. Shows an explanatory banner prompting them to sign up.
  final bool fromGoogleNoAccount;

  const RegisterPage({
    super.key,
    this.invitationToken,
    this.fromGoogleNoAccount = false,
  });

  @override
  State<RegisterPage> createState() => _RegisterPageState();
}

class _RegisterPageState extends State<RegisterPage>
    with SingleTickerProviderStateMixin {
  final _formKey = GlobalKey<FormState>();
  final _firstNameController = TextEditingController();
  final _lastNameController = TextEditingController();
  final _emailController = TextEditingController();
  final _passwordController = TextEditingController();
  final _confirmPasswordController = TextEditingController();
  final _inviteCodeController = TextEditingController();

  /// Self-hosted servers only: whether this person is first, or needs an invite code.
  RegistrationStatus? _registrationStatus;
  final _scrollController = ScrollController();
  final ValueNotifier<Offset> _globalMousePos = ValueNotifier(Offset.zero);

  bool _obscurePassword = true;
  bool _obscureConfirmPassword = true;
  bool _googleSignInLoading = false;
  bool _oidcLoading = false;
  bool _googleInitialized = false;
  bool _appleSignInLoading = false;
  bool _appleAvailable = false;
  StreamSubscription<GoogleSignInAuthenticationEvent>? _authEventSub;

  // Password strength
  double _passwordStrength = 0;
  String _passwordStrengthLabel = '';
  Color _passwordStrengthColor = Colors.grey;

  // Family invitation
  FamilyInvitation? _invitation;
  bool _loadingInvitation = false;

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
    // Top of the activation funnel: reaching this page is the only "started" signal
    // that covers every sign-up method, since the OAuth buttons leave the app.
    analytics.track(AnalyticsEvents.signupStarted);
    _initGoogleSignIn();
    _initAppleSignIn();
    _loadInvitation();
    ServerEdition.current.addListener(_onEditionKnown);
    _onEditionKnown();
  }

  /// A self-hosted server has a sign-up lock; ask it who may register. Hosted keeps the old form.
  Future<void> _onEditionKnown() async {
    if (!ServerEdition.current.value.isSelfHost || _registrationStatus != null) {
      if (mounted) setState(() {});
      return;
    }
    final status = await registrationService.getStatus();
    if (mounted) setState(() => _registrationStatus = status);
  }

  @override
  void dispose() {
    ServerEdition.current.removeListener(_onEditionKnown);
    _inviteCodeController.dispose();
    _authEventSub?.cancel();
    _firstNameController.dispose();
    _lastNameController.dispose();
    _emailController.dispose();
    _passwordController.dispose();
    _confirmPasswordController.dispose();
    _scrollController.dispose();
    _fadeController.dispose();
    super.dispose();
  }

  // ─── Google Sign-In ───────────────────────────────────────────────

  Future<void> _initGoogleSignIn() async {
    try {
      await googleSignInService.initialize();
      _authEventSub = googleSignInService.authenticationEvents.listen(
        _handleGoogleAuthEvent,
        onError: (Object error) {
          debugPrint('Google auth error: $error');
        },
      );
      if (mounted) setState(() => _googleInitialized = true);
    } catch (e) {
      debugPrint('Google Sign-In init failed: $e');
    }
  }

  Future<void> _handleGoogleAuthEvent(
      GoogleSignInAuthenticationEvent event) async {
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
      final success =
          await authProvider.googleLogin(idToken, isRegistration: true);
      if (!success) return;
      // After successful Google signup, accept invitation if present
      if (mounted && widget.invitationToken != null) {
        try {
          await familyService.acceptInvitation(widget.invitationToken!);
        } catch (_) {
          // Continue even if invitation acceptance fails
        }
      }
      // Navigate explicitly rather than relying solely on the GoRouter redirect,
      // which can race the auth-state notification and leave the user on /register.
      if (mounted) {
        final token = widget.invitationToken;
        if (token != null && token.isNotEmpty) {
          context.go('/accept-invitation/$token');
        } else {
          context.go(PostAuthRedirect.destination());
        }
      }
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          'Google sign-up failed: ${e.toString()}',
          type: AppToastType.error,
          clearsBottomNav: false,
        );
      }
    } finally {
      if (mounted) setState(() => _googleSignInLoading = false);
    }
  }

  /// Determine whether native "Sign in with Apple" is offered (iOS/macOS).
  Future<void> _initAppleSignIn() async {
    try {
      final available = await appleSignInService.isAvailable();
      if (mounted) setState(() => _appleAvailable = available);
    } catch (e) {
      debugPrint('Apple Sign-In availability check failed: $e');
    }
  }

  /// Native Apple Sign-Up (iOS/macOS). Apple auto-creates the account on first
  /// authorization, so this doubles as sign-up; mirrors the Google sign-up path.
  Future<void> _handleAppleSignIn() async {
    setState(() => _appleSignInLoading = true);
    try {
      final result = await appleSignInService.signIn();
      if (result == null) {
        if (mounted) setState(() => _appleSignInLoading = false);
        return; // cancelled
      }
      if (!mounted) return;
      final authProvider = context.read<AuthProvider>();
      final success = await authProvider.appleLogin(AppleLoginRequest(
        identityToken: result.identityToken,
        authorizationCode: result.authorizationCode,
        nonce: result.rawNonce,
        email: result.email,
        firstName: result.givenName,
        lastName: result.familyName,
        userIdentifier: result.userIdentifier,
      ));
      if (!success) {
        if (mounted) {
          showAppToast(
            context,
            authProvider.errorMessage ?? 'Apple sign-up failed',
            type: AppToastType.error,
            clearsBottomNav: false,
          );
        }
        return;
      }
      // Accept a pending family invitation if present (mirrors Google sign-up).
      if (mounted && widget.invitationToken != null) {
        try {
          await familyService.acceptInvitation(widget.invitationToken!);
        } catch (_) {}
      }
      if (mounted) {
        final token = widget.invitationToken;
        if (token != null && token.isNotEmpty) {
          context.go('/accept-invitation/$token');
        } else {
          context.go(PostAuthRedirect.destination());
        }
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

  /// "Continue with Apple" button, matching the Google button's glass style.
  Widget _buildAppleSignInButton(AuthProvider auth, double width) {
    final disabled = auth.isLoading || _appleSignInLoading;
    return Center(
      child: SizedBox(
        width: width,
        child: Opacity(
          opacity: disabled ? 0.6 : 1.0,
          child: SignInWithAppleButton(
            onPressed: disabled ? () {} : _handleAppleSignIn,
            text: 'Continue with Apple',
            height: 44,
            borderRadius: BorderRadius.circular(16),
            style: SignInWithAppleButtonStyle.white,
          ),
        ),
      ),
    );
  }

  Future<void> _handleGoogleSignUp() async {
    if (kIsWeb) return; // Web uses renderButton
    setState(() => _googleSignInLoading = true);
    try {
      await googleSignInService.authenticate();
    } catch (e) {
      if (mounted) {
        showAppToast(
          context,
          'Google sign-up failed: ${e.toString()}',
          type: AppToastType.error,
          clearsBottomNav: false,
        );
        setState(() => _googleSignInLoading = false);
      }
    }
  }

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
                        color: Theme.of(context)
                            .colorScheme
                            .onSurface
                            .withValues(alpha: 0.5),
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
      label: 'Sign up with Google',
      isLoading: _googleSignInLoading,
      onPressed: (auth.isLoading || _googleSignInLoading) ? null : _handleGoogleSignUp,
    );
  }

  // ─── Family Invitation ────────────────────────────────────────────

  Future<void> _loadInvitation() async {
    final token = widget.invitationToken;
    if (token == null || token.isEmpty) return;

    setState(() => _loadingInvitation = true);
    try {
      final invitation = await familyService.getInvitationByToken(token);
      if (!mounted) return;
      setState(() {
        _invitation = invitation;
        _loadingInvitation = false;
      });
      // Pre-fill email
      if (invitation.email.isNotEmpty) {
        _emailController.text = invitation.email;
      }
    } catch (_) {
      if (mounted) {
        setState(() => _loadingInvitation = false);
      }
    }
  }

  // ─── Password Strength ────────────────────────────────────────────

  /// The password rules, in the order they're shown. Kept as one list so the
  /// checklist, the strength meter and the validator can never disagree about
  /// what is required.
  ///
  /// The special-character class deliberately matches
  /// PasswordValidationService on the backend. It previously did not: the
  /// client accepted only `!@#$%^&*(),.?":{}|<>` while the server also allows
  /// `_ + - = [ ] ; ' \ /`, so a perfectly acceptable password like
  /// `my_password1A` was rejected here and would have been accepted there.
  static final List<({String label, bool Function(String) test})>
      _passwordRules = [
    (label: 'at least 8 characters', test: (p) => p.length >= 8),
    (label: 'an uppercase letter', test: (p) => p.contains(RegExp(r'[A-Z]'))),
    (label: 'a lowercase letter', test: (p) => p.contains(RegExp(r'[a-z]'))),
    (label: 'a number', test: (p) => p.contains(RegExp(r'[0-9]'))),
    (
      label: 'a special character',
      test: (p) =>
          p.contains(RegExp(r'''[!@#$%^&*()_+\-=\[\]{};':"\\|,.<>\/?]''')),
    ),
  ];

  List<String> _unmetPasswordRules(String password) => _passwordRules
      .where((r) => !r.test(password))
      .map((r) => r.label)
      .toList();

  void _updatePasswordStrength(String password) {
    double strength = 0;

    if (password.isEmpty) {
      setState(() {
        _passwordStrength = 0;
        _passwordStrengthLabel = '';
        _passwordStrengthColor = Colors.grey;
      });
      return;
    }

    if (password.length >= 8) strength += 0.2;
    if (password.length >= 12) strength += 0.1;
    if (password.contains(RegExp(r'[a-z]'))) strength += 0.15;
    if (password.contains(RegExp(r'[A-Z]'))) strength += 0.15;
    if (password.contains(RegExp(r'[0-9]'))) strength += 0.15;
    if (password
        .contains(RegExp(r'''[!@#$%^&*()_+\-=\[\]{};':"\\|,.<>\/?]'''))) {
      strength += 0.15;
    }
    if (!RegExp(r'(.)\1{2,}|123|234|345|456|567|678|789|abc|bcd|cde')
        .hasMatch(password.toLowerCase())) {
      strength += 0.1;
    }

    String label;
    Color color;
    if (strength < 0.3) {
      label = 'Weak';
      color = Colors.red;
    } else if (strength < 0.5) {
      label = 'Fair';
      color = Colors.orange;
    } else if (strength < 0.7) {
      label = 'Good';
      color = Colors.amber;
    } else {
      label = 'Strong';
      color = const Color(0xFF4ADE80);
    }

    setState(() {
      _passwordStrength = strength.clamp(0, 1);
      _passwordStrengthLabel = label;
      _passwordStrengthColor = color;
    });
  }

  /// "Sign up with (provider)": single sign-on on a self-hosted server. Carries the invite code,
  /// if one was typed, so the server can let this person in.
  Future<void> _handleOidcSignUp() async {
    setState(() => _oidcLoading = true);
    final opened = await oidcSignInService.begin(invite: _inviteCodeController.text);
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

  // ─── Register Handler ─────────────────────────────────────────────

  Future<void> _handleRegister() async {
    if (!_formKey.currentState!.validate()) return;

    final authProvider = context.read<AuthProvider>();
    final success = await authProvider.register(
      email: _emailController.text.trim(),
      password: _passwordController.text,
      firstName: _firstNameController.text.trim(),
      lastName: _lastNameController.text.trim(),
      inviteCode: _inviteCodeController.text.trim(),
    );

    if (!success || !mounted) return;

    // Accept invitation if present
    if (widget.invitationToken != null) {
      try {
        await familyService.acceptInvitation(widget.invitationToken!);
      } catch (_) {
        // Continue even if invitation acceptance fails
      }
    }
    // Navigate explicitly rather than relying solely on the GoRouter redirect,
    // which can race the auth-state notification and leave the user on /register.
    if (mounted) {
      final token = widget.invitationToken;
      if (token != null && token.isNotEmpty) {
        context.go('/accept-invitation/$token');
      } else {
        context.go(PostAuthRedirect.destination());
      }
    }
  }

  // ─── Build ────────────────────────────────────────────────────────

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
              padding:
                  const EdgeInsets.fromLTRB(24, 96, 24, 48),
              // Centering happens INSIDE the scroll view, not around it. Wrapping
              // the scroll view in Center hands it loose constraints, so it
              // shrink-wraps to the 480px card and Flutter's automatic web
              // scrollbar rides that box's edge — a scrollbar stranded mid-page.
              // This way the scroll view spans the window (scrollbar at the true
              // right edge) while the card stays centred.
              child: Center(
                child: FadeTransition(
                  opacity: _fadeAnimation,
                  child: Consumer<AuthProvider>(
                    builder: (context, auth, _) =>
                        _buildRegisterView(theme, auth),
                  ),
                ),
              ),
            ),
          ),

          // 3. Back button — web only (marketing site lives at the domain root;
          //    on mobile there's nowhere to go back to, so hide it).
          if (kIsWeb)
            Positioned(
              top: 32,
              left: 32,
              child: _GlassButton(
                onTap: () => goToMarketingHome(context),
                child: Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Icon(Icons.arrow_back,
                        size: 16,
                        color:
                            theme.colorScheme.onSurface.withValues(alpha: 0.7)),
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

  Widget _buildRegisterView(ThemeData theme, AuthProvider auth) {
    return ConstrainedBox(
      constraints: const BoxConstraints(maxWidth: 480),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Hero Text
          Text(
            AppLocalizations.of(context).regGetStarted,
            style: theme.textTheme.displayLarge?.copyWith(fontSize: 48),
          ),
          const SizedBox(height: 8),
          Text(
            AppLocalizations.of(context).regCreateYourAccount,
            style: theme.textTheme.displayMedium?.copyWith(fontSize: 24),
          ),
          const SizedBox(height: 48),

          // Signing up in the Cloud portal (buoy-ai.com/cloud/) makes a Cloud-only account: say what it is for.
          if (PortalMode.isActive)
            Padding(
              padding: const EdgeInsets.only(bottom: 24),
              child: GlassNote(
                key: const ValueKey('register-portal-note'),
                text: AppLocalizations.of(context).portalSignUpNote,
              ),
            ),

          // Confirms an invite was picked up. The ?ref= parameter disappears from the
          // address bar as soon as the router redirects, and OAuth bounces the user off
          // the site entirely, so without this there is no way to tell whether the code
          // survived — the difference between "my friend's invite worked" and "I just
          // signed up normally" is invisible right up until the reward does or does not
          // arrive.
          if (referralService.pendingCode != null)
            Padding(
              padding: const EdgeInsets.only(bottom: 24),
              child: _ReferralBanner(code: referralService.pendingCode!),
            ),

          // Shown when redirected here after a Google login with no account
          if (widget.fromGoogleNoAccount)
            Padding(
              padding: const EdgeInsets.only(bottom: 24),
              child: _GoogleNoAccountBanner(),
            ),

          // Family Invitation Banner
          if (_loadingInvitation)
            Padding(
              padding: const EdgeInsets.only(bottom: 24),
              child: _InvitationBanner(
                isLoading: true,
                invitation: null,
              ),
            ),
          if (_invitation != null && !_loadingInvitation)
            Padding(
              padding: const EdgeInsets.only(bottom: 24),
              child: _InvitationBanner(
                isLoading: false,
                invitation: _invitation,
              ),
            ),

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

                  // Self-hosted server: say who is signing up, and take the invite code when
                  // the sign-up lock needs one.
                  if (_registrationStatus?.firstAccount == true) ...[
                    const _FirstAccountNotice(),
                    const SizedBox(height: 24),
                  ] else if (_registrationStatus?.requiresInvite == true) ...[
                    _CinematicTextField(
                      controller: _inviteCodeController,
                      label: AppLocalizations.of(context).regInviteCodeLabel,
                      hint: 'XXXXX-XXXXX-XXXXX-XXXXX',
                      prefixIcon: Icons.vpn_key_outlined,
                      textInputAction: TextInputAction.next,
                      validator: (value) => (value == null || value.trim().isEmpty)
                          ? AppLocalizations.of(context).regInviteCodeRequired
                          : null,
                    ),
                    const SizedBox(height: 24),
                  ],

                  // Social sign-in only when this server offers it.
                  ListenableBuilder(
                    listenable: ServerEdition.changes,
                    builder: (context, _) {
                      final showGoogle = ServerEdition.showGoogleSignIn;
                      final showApple = _appleAvailable && ServerEdition.showAppleSignIn;
                      final showOidc = ServerEdition.showOidcSignIn;
                      if (!showGoogle && !showApple && !showOidc) return const SizedBox.shrink();
                      return Column(
                        children: [
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
                                          .oidcSignUpWith(ServerEdition.oidcDisplayName),
                                      isLoading: _oidcLoading,
                                      width: oauthWidth.toDouble(),
                                      onPressed: auth.isLoading ? null : _handleOidcSignUp,
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
                          if (ServerEdition.showPasswordLogin) ...[
                            const SizedBox(height: 24),

                            // Divider
                            _OrDivider(),
                            const SizedBox(height: 24),
                          ],
                        ],
                      );
                    },
                  ),

                  // The password sign-up form, unless the server turned passwords off.
                  if (ServerEdition.showPasswordLogin) ...[
                  // Name Row
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: _CinematicTextField(
                          controller: _firstNameController,
                          label: 'First Name',
                          hint: 'Jane',
                          prefixIcon: Icons.person_outline,
                          textInputAction: TextInputAction.next,
                          validator: (value) {
                            if (value == null || value.trim().isEmpty) {
                              return 'Required';
                            }
                            return null;
                          },
                        ),
                      ),
                      const SizedBox(width: 16),
                      Expanded(
                        child: _CinematicTextField(
                          controller: _lastNameController,
                          label: 'Last Name',
                          hint: 'Doe',
                          textInputAction: TextInputAction.next,
                          validator: (value) {
                            if (value == null || value.trim().isEmpty) {
                              return 'Required';
                            }
                            return null;
                          },
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 20),

                  // Email
                  _CinematicTextField(
                    controller: _emailController,
                    label: 'Email',
                    hint: 'you@example.com',
                    keyboardType: TextInputType.emailAddress,
                    prefixIcon: Icons.mail_outline,
                    textInputAction: TextInputAction.next,
                    enabled: _invitation == null,
                    validator: (value) {
                      if (value == null || value.isEmpty) {
                        return 'Email is required';
                      }
                      if (!RegExp(r'^[^@\s]+@[^@\s]+\.[^@\s]+$').hasMatch(value)) {
                        return 'Enter a valid email';
                      }
                      return null;
                    },
                  ),
                  const SizedBox(height: 20),

                  // Password
                  _CinematicTextField(
                    controller: _passwordController,
                    label: 'Password',
                    hint: '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022',
                    obscureText: _obscurePassword,
                    prefixIcon: Icons.lock_outline,
                    textInputAction: TextInputAction.next,
                    onChanged: _updatePasswordStrength,
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscurePassword
                            ? Icons.visibility_outlined
                            : Icons.visibility_off_outlined,
                        color: theme.colorScheme.onSurface
                            .withValues(alpha: 0.4),
                        size: 20,
                      ),
                      onPressed: () =>
                          setState(() => _obscurePassword = !_obscurePassword),
                    ),
                    // Reports EVERY unmet rule at once. Returning on the first
                    // failure meant fixing one rule only revealed the next, so a
                    // password short of three rules took three submit-and-read
                    // cycles to correct ("missing a number", then "missing a
                    // special character", then "missing a capital"). Testers
                    // asked for the requirements up front; the live checklist
                    // below is the primary answer and this is the backstop.
                    validator: (value) {
                      if (value == null || value.isEmpty) {
                        return 'Password is required';
                      }
                      final unmet = _unmetPasswordRules(value);
                      if (unmet.isEmpty) return null;
                      return 'Still needed: ${unmet.join(', ')}';
                    },
                  ),
                  // Every rule, always visible, ticking off as it's met. This
                  // replaces a single low-contrast sentence that testers read
                  // past — they then discovered the rules one submit at a time
                  // and asked to "just list all the password requirements at
                  // once". Showing progress also means you can see you're one
                  // rule away instead of guessing.
                  const SizedBox(height: 10),
                  ..._passwordRules.map((rule) {
                    final met = rule.test(_passwordController.text);
                    final empty = _passwordController.text.isEmpty;
                    return Padding(
                      padding: const EdgeInsets.only(bottom: 4),
                      child: Row(
                        children: [
                          Icon(
                            met
                                ? Icons.check_circle
                                : Icons.circle_outlined,
                            size: 14,
                            color: met
                                ? const Color(0xFF1EB442)
                                : theme.colorScheme.onSurface
                                    .withValues(alpha: empty ? 0.3 : 0.45),
                          ),
                          const SizedBox(width: 8),
                          Text(
                            rule.label,
                            style: GoogleFonts.sora(
                              fontSize: 11.5,
                              color: met
                                  ? const Color(0xFF1EB442)
                                  : theme.colorScheme.onSurface
                                      .withValues(alpha: empty ? 0.5 : 0.7),
                            ),
                          ),
                        ],
                      ),
                    );
                  }),

                  // Password Strength Indicator
                  if (_passwordController.text.isNotEmpty) ...[
                    const SizedBox(height: 10),
                    Row(
                      children: [
                        Expanded(
                          child: ClipRRect(
                            borderRadius: BorderRadius.circular(4),
                            child: LinearProgressIndicator(
                              value: _passwordStrength,
                              backgroundColor: theme.colorScheme.onSurface
                                  .withValues(alpha: 0.08),
                              color: _passwordStrengthColor,
                              minHeight: 3,
                            ),
                          ),
                        ),
                        const SizedBox(width: 12),
                        Text(
                          _passwordStrengthLabel,
                          style: GoogleFonts.sora(
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                            color: _passwordStrengthColor,
                          ),
                        ),
                      ],
                    ),
                  ],
                  const SizedBox(height: 20),

                  // Confirm Password
                  _CinematicTextField(
                    controller: _confirmPasswordController,
                    label: 'Confirm Password',
                    hint: '\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022',
                    obscureText: _obscureConfirmPassword,
                    prefixIcon: Icons.lock_outline,
                    textInputAction: TextInputAction.done,
                    onFieldSubmitted: (_) => _handleRegister(),
                    suffixIcon: IconButton(
                      icon: Icon(
                        _obscureConfirmPassword
                            ? Icons.visibility_outlined
                            : Icons.visibility_off_outlined,
                        color: theme.colorScheme.onSurface
                            .withValues(alpha: 0.4),
                        size: 20,
                      ),
                      onPressed: () => setState(
                          () => _obscureConfirmPassword = !_obscureConfirmPassword),
                    ),
                    validator: (value) {
                      if (value == null || value.isEmpty) {
                        return 'Please confirm your password';
                      }
                      if (value != _passwordController.text) {
                        return 'Passwords do not match';
                      }
                      return null;
                    },
                  ),
                  const SizedBox(height: 28),

                  // Create Account Button
                  _AccentButton(
                    onPressed: auth.isLoading ? null : _handleRegister,
                    isLoading: auth.isLoading,
                    label: 'Create Account',
                  ),
                  const SizedBox(height: 16),
                  ],

                  // Terms
                  Text.rich(
                    TextSpan(
                      text: 'By creating an account, you agree to our ',
                      children: [
                        WidgetSpan(
                          alignment: PlaceholderAlignment.baseline,
                          baseline: TextBaseline.alphabetic,
                          child: MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: GestureDetector(
                              onTap: () => context.push('/terms'),
                              child: Text(
                                AppLocalizations.of(context).regTermsOfService,
                                style: GoogleFonts.sora(
                                  fontSize: 11,
                                  color: theme.colorScheme.secondary.withValues(alpha: 0.7),
                                  decoration: TextDecoration.underline,
                                  decorationColor: theme.colorScheme.secondary.withValues(alpha: 0.4),
                                ),
                              ),
                            ),
                          ),
                        ),
                        const TextSpan(text: ' and '),
                        WidgetSpan(
                          alignment: PlaceholderAlignment.baseline,
                          baseline: TextBaseline.alphabetic,
                          child: MouseRegion(
                            cursor: SystemMouseCursors.click,
                            child: GestureDetector(
                              onTap: () => context.push('/privacy-policy'),
                              child: Text(
                                AppLocalizations.of(context).regPrivacyPolicy,
                                style: GoogleFonts.sora(
                                  fontSize: 11,
                                  color: theme.colorScheme.secondary.withValues(alpha: 0.7),
                                  decoration: TextDecoration.underline,
                                  decorationColor: theme.colorScheme.secondary.withValues(alpha: 0.4),
                                ),
                              ),
                            ),
                          ),
                        ),
                      ],
                    ),
                    textAlign: TextAlign.center,
                    style: GoogleFonts.sora(
                      fontSize: 11,
                      color:
                          theme.colorScheme.onSurface.withValues(alpha: 0.35),
                    ),
                  ),
                ],
              ),
            ),
          ),

          const SizedBox(height: 32),

          // Login link
          Row(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Text(
                AppLocalizations.of(context).regAlreadyHaveAccount,
                style: theme.textTheme.bodyMedium,
              ),
              MouseRegion(
                cursor: SystemMouseCursors.click,
                child: GestureDetector(
                  onTap: () => context.go('/login'),
                  child: Text(
                    AppLocalizations.of(context).regSignInLink,
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

          // Which server the account is created on (BuoyAI Cloud or one you run yourself).
          const SizedBox(height: 16),
          ServerPickerControl(onChanged: _onServerChanged),
        ],
      ),
    );
  }

  /// The user picked another server: its sign-up rules (invite code, first account) are its own.
  void _onServerChanged() {
    if (!mounted) return;
    context.read<AuthProvider>().clearError();
    setState(() => _registrationStatus = null);
    _onEditionKnown();
  }
}

// ─── Family Invitation Banner ─────────────────────────────────────────────

class _InvitationBanner extends StatelessWidget {
  final bool isLoading;
  final FamilyInvitation? invitation;
  const _InvitationBanner({required this.isLoading, this.invitation});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent = theme.colorScheme.secondary;

    return ClipRRect(
      borderRadius: BorderRadius.circular(16),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 12, sigmaY: 12),
        child: Container(
          padding: const EdgeInsets.all(16),
          decoration: BoxDecoration(
            color: accent.withValues(alpha: 0.06),
            borderRadius: BorderRadius.circular(16),
            border: Border.all(color: accent.withValues(alpha: 0.15)),
          ),
          child: isLoading
              ? Row(
                  children: [
                    SizedBox(
                      width: 18,
                      height: 18,
                      child: CircularProgressIndicator(
                        strokeWidth: 2,
                        color: accent.withValues(alpha: 0.6),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Text(
                      AppLocalizations.of(context).regLoadingInvitationDetails,
                      style: GoogleFonts.sora(
                        fontSize: 13,
                        color: theme.colorScheme.onSurface
                            .withValues(alpha: 0.6),
                      ),
                    ),
                  ],
                )
              : Row(
                  children: [
                    Icon(Icons.family_restroom,
                        size: 22, color: accent.withValues(alpha: 0.8)),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          Text(
                            AppLocalizations.of(context).invFamilyInvitation,
                            style: GoogleFonts.sora(
                              fontSize: 14,
                              fontWeight: FontWeight.w600,
                              color: theme.colorScheme.onSurface,
                            ),
                          ),
                          const SizedBox(height: 2),
                          Text(
                            AppLocalizations.of(context).regJoinFamilyAs(
                                invitation!.familyName,
                                invitation!.invitedRole.displayName),
                            style: GoogleFonts.sora(
                              fontSize: 12,
                              color: theme.colorScheme.onSurface
                                  .withValues(alpha: 0.5),
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
        ),
      ),
    );
  }
}

// ─── Shared Cinematic Widgets ─────────────────────────────────────────────

/// Shown on a brand-new self-hosted server: nobody has an account yet.
class _FirstAccountNotice extends StatelessWidget {
  const _FirstAccountNotice();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Container(
      key: const Key('firstAccountNotice'),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: theme.colorScheme.primary.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: theme.colorScheme.primary.withValues(alpha: 0.25)),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.admin_panel_settings_outlined, size: 20, color: theme.colorScheme.primary),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              AppLocalizations.of(context).regFirstAccountNotice,
              style: theme.textTheme.bodySmall,
            ),
          ),
        ],
      ),
    );
  }
}

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

/// Confirms a referral code was captured, so the invited user can see it took effect
/// before they commit to signing up. The advertised credit comes from the backend,
/// localized to the device's currency when a matching reward coupon exists (#941);
/// until it loads (or if it fails) the USD offer shows, which is also what an
/// unconfigured currency resolves to server-side.
class _ReferralBanner extends StatefulWidget {
  const _ReferralBanner({required this.code});

  final String code;

  @override
  State<_ReferralBanner> createState() => _ReferralBannerState();
}

class _ReferralBannerState extends State<_ReferralBanner> {
  ReferralRewardInfo _reward = ReferralRewardInfo.usdDefault;

  @override
  void initState() {
    super.initState();
    referralService.getRewardInfo().then((info) {
      if (mounted) setState(() => _reward = info);
    });
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    const accent = Color(0xFF1EB442);

    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
      decoration: BoxDecoration(
        color: accent.withValues(alpha: 0.10),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: accent.withValues(alpha: 0.30)),
      ),
      child: Row(
        children: [
          const Icon(Icons.card_giftcard_rounded, size: 20, color: accent),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  AppLocalizations.of(context).regInviteCodeApplied(widget.code),
                  style: theme.textTheme.bodyMedium?.copyWith(
                      fontWeight: FontWeight.w600,
                      color: theme.colorScheme.onSurface),
                ),
                const SizedBox(height: 2),
                Text(
                  // States the condition rather than implying signup alone pays, which is
                  // what the reward sweep actually requires.
                  AppLocalizations.of(context).regReferralReward(
                      formatCurrencyCode(_reward.amount, _reward.currency, decimalDigits: 0)),
                  style: theme.textTheme.bodySmall?.copyWith(
                      color: theme.colorScheme.onSurface.withValues(alpha: 0.7)),
                ),
              ],
            ),
          ),
        ],
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
              padding:
                  const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
              decoration: BoxDecoration(
                color:
                    theme.scaffoldBackgroundColor.withValues(alpha: 0.4),
                borderRadius: BorderRadius.circular(20),
                border: Border.all(
                  color:
                      theme.colorScheme.onSurface.withValues(alpha: 0.08),
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
  final TextInputAction? textInputAction;
  final ValueChanged<String>? onFieldSubmitted;
  final ValueChanged<String>? onChanged;
  final bool enabled;

  const _CinematicTextField({
    required this.controller,
    required this.label,
    this.hint,
    this.keyboardType,
    this.obscureText = false,
    this.prefixIcon,
    this.suffixIcon,
    this.validator,
    this.textInputAction,
    this.onFieldSubmitted,
    this.onChanged,
    this.enabled = true,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final borderColor = theme.colorScheme.onSurface.withValues(alpha: 0.1);
    final focusBorderColor =
        theme.colorScheme.secondary.withValues(alpha: 0.5);

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
          textInputAction: textInputAction,
          onFieldSubmitted: onFieldSubmitted,
          onChanged: onChanged,
          enabled: enabled,
          style: GoogleFonts.sora(
            fontSize: 15,
            color: theme.colorScheme.onSurface,
          ),
          validator: validator,
          decoration: InputDecoration(
            hintText: hint,
            hintStyle: GoogleFonts.sora(
              fontSize: 15,
              color:
                  theme.colorScheme.onSurface.withValues(alpha: 0.2),
            ),
            counterText: '',
            prefixIcon: prefixIcon != null
                ? Icon(prefixIcon,
                    size: 20,
                    color: theme.colorScheme.onSurface
                        .withValues(alpha: 0.3))
                : null,
            suffixIcon: suffixIcon,
            filled: true,
            fillColor: theme.scaffoldBackgroundColor
                .withValues(alpha: 0.4),
            contentPadding: const EdgeInsets.symmetric(
                horizontal: 16, vertical: 16),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: borderColor),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(color: borderColor),
            ),
            disabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(
                  color: borderColor.withValues(alpha: 0.05)),
            ),
            focusedBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide:
                  BorderSide(color: focusBorderColor, width: 1.5),
            ),
            errorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(
                  color: theme.colorScheme.error
                      .withValues(alpha: 0.5)),
            ),
            focusedErrorBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(16),
              borderSide: BorderSide(
                  color: theme.colorScheme.error, width: 1.5),
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
  const _AccentButton(
      {this.onPressed, this.isLoading = false, required this.label});

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
      cursor: widget.onPressed != null
          ? SystemMouseCursors.click
          : SystemMouseCursors.basic,
      onEnter: (_) => setState(() => _hovering = true),
      onExit: (_) => setState(() => _hovering = false),
      child: GestureDetector(
        onTap: widget.onPressed,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 200),
          padding: const EdgeInsets.symmetric(vertical: 18),
          decoration: BoxDecoration(
            color: widget.onPressed != null
                ? (_hovering
                    ? accent
                    : accent.withValues(alpha: 0.85))
                : accent.withValues(alpha: 0.3),
            borderRadius: BorderRadius.circular(16),
            boxShadow: _hovering && widget.onPressed != null
                ? [
                    BoxShadow(
                        color: accent.withValues(alpha: 0.3),
                        blurRadius: 20,
                        offset: const Offset(0, 4))
                  ]
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

class _OrDivider extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final dividerColor =
        theme.colorScheme.onSurface.withValues(alpha: 0.08);
    return Row(
      children: [
        Expanded(child: Divider(color: dividerColor, thickness: 1)),
        Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16),
          child: Text(
            AppLocalizations.of(context).regOrContinueWithEmail,
            style: GoogleFonts.sora(
              fontSize: 13,
              color:
                  theme.colorScheme.onSurface.withValues(alpha: 0.3),
            ),
          ),
        ),
        Expanded(child: Divider(color: dividerColor, thickness: 1)),
      ],
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
        border: Border.all(
            color: theme.colorScheme.error.withValues(alpha: 0.2)),
      ),
      child: Row(
        children: [
          Icon(Icons.error_outline,
              size: 18,
              color:
                  theme.colorScheme.error.withValues(alpha: 0.8)),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              message,
              style: GoogleFonts.sora(
                fontSize: 13,
                color:
                    theme.colorScheme.error.withValues(alpha: 0.9),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _GoogleNoAccountBanner extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final accent = theme.colorScheme.secondary;
    return Container(
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: accent.withValues(alpha: 0.08),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: accent.withValues(alpha: 0.25)),
      ),
      child: Row(
        children: [
          Icon(Icons.info_outline, size: 18, color: accent),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              AppLocalizations.of(context).regGoogleNoAccountBanner,
              style: GoogleFonts.sora(
                fontSize: 13,
                color: theme.colorScheme.onSurface.withValues(alpha: 0.9),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
