import '../config/app_brand.dart';
import '../l10n/generated/app_localizations.dart';
import 'dart:async';

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

import '../providers/auth_provider.dart';
import '../services/biometric_service.dart';

/// Biometric app-unlock gate (Chase/Robinhood style).
///
/// A LOCAL gate over an already-authenticated session: when app-lock is enabled
/// it shows an opaque cinematic scrim that hides the app until the user passes a
/// biometric check. It works offline and for every auth type (email, Google,
/// Apple, passkey) because it re-verifies the person, not the session.
///
/// Mounted as the LAST child of the [MaterialApp.router] `builder:` Stack in
/// main.dart so the scrim sits ABOVE all app content INCLUDING the OfflineBanner
/// — the lock doubles as a privacy screen. It renders [child] (which is
/// `SizedBox.shrink()` in production, since the real content lives in the layers
/// below it in the Stack) whenever the app is unlocked, and the full-screen scrim
/// only while locked.
///
/// Locking rules (never on web; only while [AuthStatus.authenticated]):
///  - cold start: the first authenticated build after session restore, and
///  - resume: coming back to the foreground after being backgrounded longer than
///    [BiometricService.appLockResumeThreshold].
///
/// Fail-open: if the device reports it can no longer check biometrics at lock
/// time (e.g. the user removed all enrolled biometrics), the gate does NOT lock —
/// a permanently locked-out user is a worse outcome than a skipped lock. A Sentry
/// breadcrumb records the skip.
class AppLockGate extends StatefulWidget {
  final Widget child;

  /// Whether the gate should render the scrim on its VERY FIRST build, before the
  /// async [BiometricService.isAppLockEnabled] read completes. main.dart awaits
  /// that flag before runApp (mobile only) and passes it here so a locked user
  /// never sees a single frame of content on cold start. The async biometric
  /// prompt still fires afterwards via the cold-start evaluation.
  final bool initiallyLocked;

  const AppLockGate({
    super.key,
    required this.child,
    this.initiallyLocked = false,
  });

  @override
  State<AppLockGate> createState() => _AppLockGateState();
}

class _AppLockGateState extends State<AppLockGate> with WidgetsBindingObserver {
  // VaporClinic palette. Hard-coded (not Theme.of) because the scrim is mounted
  // above the app content and must render identically regardless of the route
  // below it — same approach as the cinematic error fallback in main.dart.
  static const Color _bg = Color(0xFF04040A);
  static const Color _accent = Color(0xFF7B61FF);
  static const Color _textGhost = Color(0xFFF0EFF4);
  static const Color _textMuted = Color(0xFF8A8A9A);

  bool _locked = false;
  // Synchronously-readable mirror of biometricService.isAppLockEnabled(). Seeded
  // from [AppLockGate.initiallyLocked] and refreshed on every cold-start/resume
  // evaluation. Lets didChangeAppLifecycleState raise the scrim the INSTANT we
  // leave the foreground (before the OS snapshot) without awaiting secure storage.
  bool _appLockEnabledCached = false;
  // Guards re-entrant biometric prompts: the auto-prompt and a manual "Unlock"
  // tap must never stack two OS dialogs.
  bool _authInFlight = false;
  // After a failed/cancelled attempt, reveal the manual "Unlock" button + note.
  bool _showRetry = false;
  bool _coldStartEvaluated = false;
  DateTime? _pausedAt;
  AuthStatus? _lastStatus;

  @override
  void initState() {
    super.initState();
    _locked = widget.initiallyLocked;
    _appLockEnabledCached = widget.initiallyLocked;
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  bool get _isAuthenticated {
    try {
      return context.read<AuthProvider>().status == AuthStatus.authenticated;
    } catch (_) {
      return false;
    }
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (kIsWeb) return;
    if (state == AppLifecycleState.paused ||
        state == AppLifecycleState.inactive ||
        state == AppLifecycleState.hidden) {
      // Record the FIRST moment we left the foreground so the threshold measures
      // total time away — don't overwrite on inactive→paused transitions.
      _pausedAt ??= DateTime.now();
      // Raise the scrim IMMEDIATELY — before the OS captures the app-switcher
      // snapshot — whenever app-lock is enabled. This is SEPARATE from whether a
      // biometric re-auth is required on resume (that still honours the 2-min
      // threshold in _maybeLockOnResume): the scrim goes up on ANY background so
      // the last rendered frame is the privacy screen, never financial content.
      // Uses the synchronous cached hint since there's no time to await storage.
      if (_appLockEnabledCached && !_locked && _isAuthenticated) {
        setState(() {
          _locked = true;
          _showRetry = false;
          _authInFlight = false;
        });
      }
    } else if (state == AppLifecycleState.resumed) {
      _maybeLockOnResume();
    }
    super.didChangeAppLifecycleState(state);
  }

  /// On resume, decide whether the scrim raised at backgrounding should drop
  /// (short app-switch) or stay up behind a fresh biometric prompt (away past the
  /// threshold). Also refreshes the cached enabled-hint.
  Future<void> _maybeLockOnResume() async {
    final pausedAt = _pausedAt;
    _pausedAt = null;

    if (!_isAuthenticated) {
      _dropScrim();
      return;
    }

    final enabled = await biometricService.isAppLockEnabled();
    _appLockEnabledCached = enabled;
    if (!enabled) {
      // App-lock was turned off — never strand an optimistic scrim.
      _dropScrim();
      return;
    }

    final awayLongEnough = pausedAt != null &&
        DateTime.now().difference(pausedAt) >=
            BiometricService.appLockResumeThreshold;
    if (!awayLongEnough) {
      // Brief switch away: the privacy scrim protected the snapshot; no re-auth.
      _dropScrim();
      return;
    }

    // Away long enough → require a biometric re-auth. Keep/raise the scrim and
    // fire the prompt.
    await _engageLock();
  }

  Future<void> _maybeLockOnColdStart() async {
    if (kIsWeb || !_isAuthenticated) return;
    final enabled = await biometricService.isAppLockEnabled();
    _appLockEnabledCached = enabled;
    if (!enabled) {
      // Not enabled — clear any optimistic scrim from [initiallyLocked].
      _dropScrim();
      return;
    }
    await _engageLock();
  }

  /// Drop the scrim without requiring auth (short switch / lock disabled / signed
  /// out). No-op if already unlocked.
  void _dropScrim() {
    if (!_locked || !mounted) return;
    setState(() {
      _locked = false;
      _showRetry = false;
      _authInFlight = false;
    });
  }

  /// Show the lock scrim (unless biometrics are unavailable — fail-open) and fire
  /// the automatic unlock prompt shortly after.
  Future<void> _engageLock() async {
    // Fail-open: if the device can no longer check biometrics, don't strand the
    // user behind a lock they cannot pass — drop any scrim already raised.
    final canCheck = await biometricService.canCheckBiometrics();
    if (!canCheck) {
      Sentry.addBreadcrumb(Breadcrumb(
        category: 'app_lock',
        message: 'app-lock skipped: biometrics unavailable at lock time',
        level: SentryLevel.warning,
      ));
      _dropScrim();
      return;
    }
    if (!mounted) return;
    setState(() {
      _locked = true;
      _showRetry = false;
      _authInFlight = false;
    });
    // Give the scrim a beat to paint before the OS sheet slides up, so the app
    // content is never briefly visible behind the prompt.
    Timer(const Duration(milliseconds: 300), () {
      if (mounted && _locked && !_authInFlight) _runAuth();
    });
  }

  Future<void> _runAuth() async {
    if (_authInFlight) return;
    setState(() => _authInFlight = true);
    final ok = await biometricService.authenticate(reason: 'Unlock ${AppBrand.name}');
    if (!mounted) return;
    setState(() {
      _authInFlight = false;
      if (ok) {
        _locked = false;
        _showRetry = false;
        // A fresh unlock resets the away-timer baseline.
        _pausedAt = null;
      } else {
        _showRetry = true;
      }
    });
  }

  Future<void> _logout() async {
    // Drop the lock first so that, once the provider flips to unauthenticated and
    // the router shows the login page, the scrim isn't lingering on top of it.
    setState(() {
      _locked = false;
      _showRetry = false;
      _authInFlight = false;
    });
    try {
      await context.read<AuthProvider>().logout();
    } catch (_) {
      // Best-effort — the state reset above already dismissed the gate.
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = context.watch<AuthProvider>().status;

    // React to auth transitions. Clear any residual lock on logout. We key off
    // `unauthenticated` specifically (NOT "anything but authenticated") so the
    // scrim stays up through the transient `initial`/`loading` states during a
    // cold-start session restore — otherwise an `initiallyLocked` gate would
    // flash content for the frames before the provider resolves to authenticated.
    if (status != _lastStatus) {
      _lastStatus = status;
      if (status == AuthStatus.unauthenticated) {
        _locked = false;
        _coldStartEvaluated = false;
        _authInFlight = false;
        _showRetry = false;
        _pausedAt = null;
      }
    }

    // Cold-start evaluation: the first authenticated build after session restore.
    if (!kIsWeb &&
        status == AuthStatus.authenticated &&
        !_coldStartEvaluated) {
      _coldStartEvaluated = true;
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _maybeLockOnColdStart();
      });
    }

    if (!_locked) return widget.child;
    return _buildLockScreen();
  }

  Widget _buildLockScreen() {
    // Opaque, full-bleed, and pointer-absorbing so nothing behind it is visible
    // or interactive. Wrapped in Material because it is mounted above the app's
    // Navigator (bare Text would otherwise render with the yellow debug underline).
    return Material(
      color: _bg,
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onTap: () {},
        child: SafeArea(
          child: Center(
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 32),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Text(
                    AppBrand.name,
                    style: GoogleFonts.instrumentSerif(
                      fontSize: 44,
                      color: _textGhost,
                      letterSpacing: 0.5,
                    ),
                  ),
                  const SizedBox(height: 40),
                  Container(
                    width: 88,
                    height: 88,
                    decoration: BoxDecoration(
                      shape: BoxShape.circle,
                      color: _accent.withValues(alpha: 0.12),
                      border: Border.all(color: _accent.withValues(alpha: 0.35)),
                    ),
                    child: const Icon(Icons.fingerprint,
                        size: 44, color: _accent),
                  ),
                  const SizedBox(height: 28),
                  Text(
                    AppLocalizations.of(context).uiLocked,
                    style: GoogleFonts.sora(
                      fontSize: 20,
                      fontWeight: FontWeight.w600,
                      color: _textGhost,
                    ),
                  ),
                  const SizedBox(height: 8),
                  Text(
                    _showRetry
                        ? 'Authentication failed — try again'
                        : 'Verify to unlock ${AppBrand.name}',
                    textAlign: TextAlign.center,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      color: _showRetry
                          ? const Color(0xFFF87171)
                          : _textMuted,
                    ),
                  ),
                  const SizedBox(height: 36),
                  // The manual re-prompt only appears once the auto-prompt has been
                  // dismissed/failed, so a first-run success needs no button tap.
                  if (_showRetry)
                    _UnlockButton(
                      onPressed: _authInFlight ? null : _runAuth,
                      loading: _authInFlight,
                    ),
                  const SizedBox(height: 12),
                  TextButton(
                    onPressed: _authInFlight ? null : _logout,
                    child: Text(
                      AppLocalizations.of(context).uiLogOut,
                      style: GoogleFonts.sora(
                        fontSize: 14,
                        color: _textMuted,
                      ),
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

/// Primary "Unlock" action, styled to the login-page accent button.
class _UnlockButton extends StatelessWidget {
  final VoidCallback? onPressed;
  final bool loading;
  const _UnlockButton({this.onPressed, this.loading = false});

  static const Color _accent = Color(0xFF7B61FF);
  static const Color _bg = Color(0xFF04040A);

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onPressed,
      child: Container(
        width: 220,
        padding: const EdgeInsets.symmetric(vertical: 16),
        decoration: BoxDecoration(
          color: onPressed != null
              ? _accent.withValues(alpha: 0.9)
              : _accent.withValues(alpha: 0.3),
          borderRadius: BorderRadius.circular(16),
        ),
        alignment: Alignment.center,
        child: loading
            ? const SizedBox(
                width: 22,
                height: 22,
                child: CircularProgressIndicator(strokeWidth: 2.5, color: _bg),
              )
            : Text(
                AppLocalizations.of(context).uiUnlock,
                style: GoogleFonts.sora(
                  fontSize: 16,
                  fontWeight: FontWeight.w600,
                  color: _bg,
                  letterSpacing: 0.3,
                ),
              ),
      ),
    );
  }
}
