import 'dart:async';
import 'package:app_links/app_links.dart';
import 'package:flutter/foundation.dart' show kDebugMode, kIsWeb;
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart' show SemanticsBinding;
import 'package:flutter/services.dart'
    show SystemChrome, SystemUiOverlayStyle, Brightness;
import 'package:flutter_web_plugins/url_strategy.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:provider/provider.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import 'offline/background_sync.dart'
    if (dart.library.html) 'offline/background_sync_web.dart';
import 'config/app_brand.dart';
import 'config/app_config.dart';
import 'config/first_run.dart';
import 'l10n/generated/app_localizations.dart';
import 'offline/connectivity_service.dart';
import 'offline/offline_banner.dart';
import 'offline/offline_store.dart';
import 'offline/sync_service.dart';
import 'providers/auth_provider.dart';
import 'router/app_router.dart';
import 'router/post_auth_redirect.dart';
import 'config/portal_mode.dart';
import 'widgets/auth_restore_gate.dart';
import 'services/analytics_service.dart';
import 'services/api_client.dart';
import 'services/review_prompt_service.dart';
import 'services/biometric_service.dart';
import 'services/push_notification_service.dart';
import 'services/oidc_sign_in_service.dart';
import 'services/referral_service.dart';
import 'services/store_billing_service.dart';
import 'theme/app_theme.dart';
import 'utils/background_preferences.dart';
import 'utils/celebration_fx.dart';
import 'utils/link_url_scrubber.dart';
import 'utils/secure_window.dart';
import 'utils/ticker_preferences.dart';
import 'widgets/app_lock_gate.dart';
import 'widgets/consent_banner.dart';
import 'widgets/server_version_gate.dart';

// Sentry DSN — pass via --dart-define=SENTRY_DSN=https://...
const _sentryDsn = String.fromEnvironment('SENTRY_DSN', defaultValue: '');

// App version — passed via --dart-define=APP_VERSION=X.Y.Z at build time (the
// release tag, minus the leading 'v'). Drives the Sentry release so errors are
// attributed to the shipped build instead of a frozen constant. Falls back to
// 'dev' for local/un-versioned builds.
const _appVersion = String.fromEnvironment('APP_VERSION', defaultValue: 'dev');

/// The live router, exposed so the global [ErrorWidget.builder] fallback can
/// offer a "Go to dashboard" escape hatch out of a broken screen. Assigned once
/// the app's root state initializes.
GoRouter? _appRouter;

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  // The server the user picked on the sign-in screen (self-hosted), if any. Must be read before
  // the first API request so nothing is sent to the build's default server by mistake.
  await AppConfig.loadCustomServer();
  apiClient.applyBaseUrl();
  // Whether the first-launch "hosted or your own server" question was answered; the router reads it.
  await FirstRun.load();

  // The app always renders its dark cinematic theme (#04040A). Force light status-bar
  // glyphs so the clock / battery / signal stay visible — otherwise, on an iPhone set to
  // Light appearance, iOS draws dark icons on the near-black background and they vanish.
  // (Main pages have no AppBar to annotate a per-route style, so we set it globally.)
  SystemChrome.setSystemUIOverlayStyle(
    const SystemUiOverlayStyle(
      statusBarColor: Color(0x00000000),
      statusBarBrightness: Brightness.dark, // iOS: dark background → light glyphs
      statusBarIconBrightness: Brightness.light, // Android
    ),
  );

  // Global build-error fallback. Without this, any uncaught exception thrown in
  // a widget's build() renders Flutter's default grey (release) / red (debug)
  // error box — an ugly dead end for the user. Replace it with a branded,
  // cinematic fallback that at least offers a way back to the dashboard.
  ErrorWidget.builder = (FlutterErrorDetails details) =>
      _CinematicErrorFallback(details: details);

  // Capture a referral code from the launch URL (?ref=CODE) to redeem once authenticated.
  // Fire-and-forget: capture must never delay startup. Redemption re-reads storage anyway.
  unawaited(referralService.capturePending(Uri.base));

  // A page held across sign-in (OAuth consent, "Link your server") survives a reload during sign-up.
  unawaited(PostAuthRedirect.restore());

  // Use clean path-based URLs on web (e.g. /app/dashboard) instead of the
  // default hash strategy (/#/dashboard). The app is served under a /app/
  // base-href behind nginx, with the Astro marketing site owning the root.
  // No-op on mobile (conditional export in flutter_web_plugins). See issue #516.
  usePathUrlStrategy();

  // Agent fleet: force the accessibility/semantics tree on in debug builds so the
  // semantics-DOM automation (Chrome MCP targeting <flt-semantics> nodes) can drive
  // Flutter web without a screen reader. No effect on release/production builds.
  if (kDebugMode) {
    SemanticsBinding.instance.ensureSemantics();
  }

  // Preload the Google Fonts the app uses so first paint is already
  // styled — otherwise every page flashes Roboto then re-paints once
  // the font lands from the Google CDN.
  //
  // IMPORTANT: each (family, weight, style) combination is a separate
  // font FILE that must be fetched. Calling `GoogleFonts.sora()` only
  // preloads the regular (w400) weight; the first widget that uses
  // `GoogleFonts.sora(fontWeight: FontWeight.w600)` will trigger a
  // network round trip and a layout flash. We preload every weight
  // the app actually renders so scope/route swaps don't flash on
  // semi-bold or bold text — see the Budgets page section headers.
  await Future.wait<void>([
    GoogleFonts.pendingFonts([
      // Sora — regular UI text, varies w400/w500/w600/w700 across pages
      GoogleFonts.sora(),
      GoogleFonts.sora(fontWeight: FontWeight.w500),
      GoogleFonts.sora(fontWeight: FontWeight.w600),
      GoogleFonts.sora(fontWeight: FontWeight.w700),
      // Fira Code — numeric/monetary text, regular + w600 + bold
      GoogleFonts.firaCode(),
      GoogleFonts.firaCode(fontWeight: FontWeight.w600),
      GoogleFonts.firaCode(fontWeight: FontWeight.bold),
      // Instrument Serif — large display headings, regular + bold
      GoogleFonts.instrumentSerif(),
      GoogleFonts.instrumentSerif(fontWeight: FontWeight.bold),
    ]),
  ]);

  // Load the saved background bubble density before first paint so the
  // DeepWaterCurrentPainter renders the user's chosen particle count.
  await BackgroundPreferences.init();
  await TickerPreferences.init();
  await SoundPreferences.init();

  // Offline-first mobile architecture (#493): open the local cache/outbox store
  // and start watching connectivity before first paint. This is cheap — drift
  // opens the database lazily on first query — so it doesn't slow app open. On
  // web these resolve to a no-op store, keeping today's network-only behaviour.
  await initOfflineStore();
  await connectivityService.init();
  syncService.start();

  // In-app purchases (RevenueCat): configured once, anonymously, before the
  // first frame; AuthProvider identifies the user on every auth change. No-op
  // on web and on any platform without a key baked in.
  await storeBilling.configure();

  // Analytics is opt-in (#606): read the saved consent and only start PostHog if
  // the user already opted in. Otherwise the ConsentGate banner asks first.
  await analytics.loadConsentStatus();
  if (analytics.consentGranted) await analytics.init();

  // Biometric app-lock (mobile only): read the persisted enabled flag BEFORE the
  // first frame so the AppLockGate can render its scrim synchronously on cold
  // start (no content flash), and push the flag to the native layer so Android
  // FLAG_SECURE / the iOS blur overlay protect the app-switcher snapshot from the
  // very first background. No-op on web.
  var initialAppLock = false;
  if (!kIsWeb) {
    try {
      initialAppLock = await biometricService.isAppLockEnabled();
      await applyAppLockSecureWindow(initialAppLock);
    } catch (_) {
      // Never block app start on this — the gate re-evaluates async as a fallback.
    }
  }

  // Initialize Sentry if DSN is configured
  if (_sentryDsn.isNotEmpty) {
    await SentryFlutter.init(
      (options) {
        options.dsn = _sentryDsn;
        options.environment = const String.fromEnvironment(
          'ENVIRONMENT',
          defaultValue: 'development',
        );
        options.release = 'buoyai-flutter@$_appVersion';
        options.tracesSampleRate = 0.1;
        options.sendDefaultPii = false;
        // The /cloud/link handshake carries a secret `state`; never ship it.
        options.beforeSend = LinkUrlScrubber.event;
        options.beforeBreadcrumb = LinkUrlScrubber.breadcrumb;
      },
      appRunner: () => runApp(BuoyAIApp(initiallyLocked: initialAppLock)),
    );
  } else {
    runApp(BuoyAIApp(initiallyLocked: initialAppLock));
  }
}

class BuoyAIApp extends StatefulWidget {
  /// Cold-start app-lock state, resolved in main() before runApp (mobile only).
  /// Threaded to [AppLockGate] so the privacy scrim paints on the first frame.
  final bool initiallyLocked;

  const BuoyAIApp({super.key, this.initiallyLocked = false});

  @override
  State<BuoyAIApp> createState() => _BuoyAIAppState();
}

class _BuoyAIAppState extends State<BuoyAIApp> with WidgetsBindingObserver {
  final _authProvider = AuthProvider();
  late final GoRouter _router;
  // Stable instance so the dispatcher isn't recreated on rebuild. Guards the go_router
  // Android back-button crash — see _SafeBackButtonDispatcher at the bottom of this file.
  final BackButtonDispatcher _backButtonDispatcher = _SafeBackButtonDispatcher();
  AppLinks? _appLinks;
  StreamSubscription<Uri>? _linkSub;
  // Tracks the auth transition so push registration fires once per sign-in, not on
  // every AuthProvider notification.
  bool _wasAuthenticated = false;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _router = createRouter(_authProvider);
    _appRouter = _router;
    _authProvider.initialize();
    // Web navigates via the browser URL directly; Universal Links / App Links are
    // a mobile concern (e.g. returning from hosted Stripe Checkout on iOS).
    if (!kIsWeb) _initDeepLinks();
    // Register the periodic background outbox drain + cache warm after first
    // frame (mobile only — no-op stub on web). Deferred so it never delays paint.
    if (!kIsWeb) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        registerBackgroundSync();
      });
    }
    // Push notifications (#190): mobile-only, and a silent no-op until the owner runs
    // `flutterfire configure` (see lib/firebase_options.dart). Token registration rides
    // the auth state — on each sign-in the current FCM token is (re)linked to the user.
    if (!kIsWeb) {
      _authProvider.addListener(_onAuthChangedForPush);
      // init() is async — Firebase.initializeApp is a native round trip — and it
      // races the session restore started by _authProvider.initialize() above.
      // When the restore wins (the common case: reading secure storage beats
      // native Firebase setup), _onAuthChangedForPush has already fired and
      // syncRegistration bailed on !_firebaseReady, with nothing to retry it.
      // didChangeAppLifecycleState doesn't rescue it either: `resumed` is only
      // delivered on a real foreground transition, never for the initial launch,
      // so a cold start into an already-signed-in session registered no token at
      // all. Syncing once init resolves means whichever of the two finishes last
      // performs the registration.
      pushNotificationService.init(_router).then((_) {
        if (!mounted) return;
        if (_authProvider.isAuthenticated) {
          pushNotificationService.syncRegistration();
        }
      });
    }
  }

  void _onAuthChangedForPush() {
    final isAuthed = _authProvider.isAuthenticated;
    if (isAuthed && !_wasAuthenticated) {
      pushNotificationService.syncRegistration();
    }
    _wasAuthenticated = isAuthed;
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Coming back to the foreground is a good moment to flush any queued offline
    // writes (#493) — connectivity may have returned while backgrounded.
    if (state == AppLifecycleState.resumed) {
      syncService.drainOutbox();
      // Session count feeds review-prompt eligibility (debounced inside).
      if (!kIsWeb) {
        reviewPrompts.recordSession();
      }
      // Re-report the FCM token so a registration that went missing (explicit
      // logout, or the backend pruning it as stale/invalid) comes back without
      // needing a full sign-out and sign-in. Self-throttled to once a week, and
      // a no-op when notification permission isn't granted.
      if (!kIsWeb && _authProvider.isAuthenticated) {
        pushNotificationService.syncRegistration();
      }
    }
    super.didChangeAppLifecycleState(state);
  }

  /// Listen for incoming Universal Links / App Links and route them into the app.
  /// The return from hosted Stripe Checkout on iOS arrives as
  /// `https://buoy-ai.com/app/billing?checkout=success|cancel`; we map it to the
  /// in-app `/billing?checkout=…` route (the billing page already reads that param).
  Future<void> _initDeepLinks() async {
    try {
      _appLinks = AppLinks();
      // Cold start: the app was launched by tapping the link.
      final initial = await _appLinks!.getInitialLink();
      if (initial != null) _routeIncomingUri(initial);
      // Warm: links received while the app is already running.
      _linkSub = _appLinks!.uriLinkStream.listen(
        _routeIncomingUri,
        onError: (_) {},
      );
    } catch (e) {
      debugPrint('Deep link init failed: $e');
    }
  }

  void _routeIncomingUri(Uri uri) {
    // Single sign-on return on a phone: com.buoyai.buoymoney://oidc?code=... (custom scheme, so
    // the path is empty and the host is "oidc").
    if (OidcSignInService.isCallbackLink(uri)) {
      _router.go(uri.hasQuery ? '/auth/oidc?${uri.query}' : '/auth/oidc');
      return;
    }
    // Strip the "/app" web base-href prefix (present only in the public URL) and
    // keep the remaining path + query as the in-app route.
    var path = uri.path;
    if (path.startsWith('/app/')) {
      path = path.substring(4); // drop leading "/app"
    } else if (path == '/app') {
      path = '/';
    }
    if (path.isEmpty) path = '/';
    // Plaid's OAuth return (the iOS redirect_uri, a Universal Link). Plaid Link is still open
    // on top of the Accounts page and resumes by itself; go() here would replace the stack,
    // dispose that page and drop the connection it is waiting to receive.
    if (path == '/plaid-oauth') return;
    final target = uri.hasQuery ? '$path?${uri.query}' : path;
    _router.go(target);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    if (!kIsWeb) {
      _authProvider.removeListener(_onAuthChangedForPush);
      pushNotificationService.dispose();
    }
    _linkSub?.cancel();
    if (identical(_appRouter, _router)) _appRouter = null;
    _router.dispose();
    _authProvider.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return ChangeNotifierProvider.value(
      value: _authProvider,
      child: MaterialApp.router(
        title: AppBrand.name,
        debugShowCheckedModeBanner: false,
        // gen_l10n scaffold (#941): English-only today. New user-facing strings
        // belong in lib/l10n/app_en.arb; adding a locale = adding an ARB file.
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        theme: AppTheme.lightTheme,
        darkTheme: AppTheme.darkTheme,
        themeMode: ThemeMode.dark,
        // Expanded from `routerConfig: _router` so we can supply a guarded back-button
        // dispatcher — MaterialApp.router rejects `backButtonDispatcher` alongside `routerConfig`.
        routerDelegate: _router.routerDelegate,
        routeInformationParser: _router.routeInformationParser,
        routeInformationProvider: _router.routeInformationProvider,
        backButtonDispatcher: _backButtonDispatcher,
        // Consent banner (#606) overlays every route until the user decides.
        // The offline status banner (#493) is stacked on top; it renders
        // nothing when online+synced, and collapses to a small chip otherwise,
        // so it only ever intercepts touches on its own visible pixels.
        builder: (context, child) => Stack(
          children: [
            Positioned.fill(
              // Replaces the app with an "update needed" screen only when the server and
              // this app clearly cannot work together (self-hosted servers can lag behind).
              child: ServerVersionGate(
                child: ConsentGate(
                  child: AuthRestoreGate(
                    auth: _authProvider,
                    child: child ?? const SizedBox.shrink(),
                  ),
                ),
              ),
            ),
            const OfflineBanner(),
            // Biometric app-unlock scrim (#biometric). LAST child so it covers
            // everything — app content AND the OfflineBanner — when locked, and
            // doubles as a privacy screen. Renders nothing (SizedBox.shrink) when
            // unlocked. Positioned.fill guarantees full-screen coverage.
            Positioned.fill(
              child: AppLockGate(
                initiallyLocked: widget.initiallyLocked,
                child: const SizedBox.shrink(),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Root back-button dispatcher that survives a go_router crash on Android.
///
/// Pressing the system back button while go_router's route-match list is momentarily empty
/// throws `StateError: Bad state: No element` deep inside
/// `GoRouterDelegate.popRoute → _findCurrentNavigators` (it calls `.last` on `[]`). We observed
/// this on Android after returning from the Google Sign-In `HiddenActivity` (the auth state flips
/// and the router rebuilds; a back press in that window hit the empty list). It surfaced in Sentry.
///
/// Catch that `StateError` and report the back press as unhandled (`false`) so the OS handles it —
/// backgrounding the app, which is exactly the right behavior when there is nothing left to pop.
class _SafeBackButtonDispatcher extends RootBackButtonDispatcher {
  @override
  Future<bool> didPopRoute() async {
    try {
      return await super.didPopRoute();
    } on StateError catch (e) {
      if (kDebugMode) {
        debugPrint('Swallowed back-button StateError (empty go_router stack): $e');
      }
      return false;
    }
  }
}

/// Branded fallback rendered by [ErrorWidget.builder] when a widget's build()
/// throws. Self-contained (no MaterialApp/Directionality ancestor required, as
/// build errors can surface anywhere in the tree) and cinematic, matching the
/// app's dark deep-water aesthetic. Offers a "Go to dashboard" escape hatch so
/// a single broken screen doesn't strand the user.
class _CinematicErrorFallback extends StatelessWidget {
  final FlutterErrorDetails details;
  const _CinematicErrorFallback({required this.details});

  static const _bg = Color(0xFF04040A);
  static const _accent = Color(0xFF7B61FF);
  static const _textGhost = Color(0xFFF0EFF4);
  static const _textMuted = Color(0xFF8A8A9A);

  @override
  Widget build(BuildContext context) {
    // This widget can be inserted ANYWHERE the failing build() sat — including
    // inside a Row/Column or an unbounded/zero-size box, and possibly above the
    // MaterialApp (so no MediaQuery/Directionality ancestor is guaranteed). It
    // must never itself throw a secondary layout error, or Flutter falls all the
    // way back to its raw red/grey error box. So: supply our own Directionality,
    // only apply SafeArea when a MediaQuery actually exists, collapse infinite
    // constraints to a finite size, and make the content scrollable + width-
    // bounded so it can't overflow no matter how little space it is handed.
    return Directionality(
      textDirection: TextDirection.ltr,
      child: LayoutBuilder(
        builder: (context, constraints) {
          // Fall back to a sensible finite size when a parent hands us infinite
          // space (e.g. the cross axis of a Row/Column, or a scroll viewport).
          final width =
              constraints.hasBoundedWidth ? constraints.maxWidth : 400.0;
          final height =
              constraints.hasBoundedHeight ? constraints.maxHeight : 640.0;

          Widget content = Center(
            child: SingleChildScrollView(
              padding: const EdgeInsets.all(32),
              child: ConstrainedBox(
                // Bound text width so it wraps instead of demanding infinite
                // width, and keep the column readable on very wide surfaces.
                constraints: const BoxConstraints(maxWidth: 360),
                child: _buildBody(),
              ),
            ),
          );

          // SafeArea reads MediaQuery.paddingOf and throws without an ancestor;
          // only wrap when one is present.
          if (MediaQuery.maybeOf(context) != null) {
            content = SafeArea(child: content);
          }

          return SizedBox(
            width: width,
            height: height,
            child: ColoredBox(color: _bg, child: content),
          );
        },
      ),
    );
  }

  Widget _buildBody() {
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          padding: const EdgeInsets.all(18),
          decoration: BoxDecoration(
            color: _accent.withValues(alpha: 0.12),
            shape: BoxShape.circle,
            border: Border.all(color: _accent.withValues(alpha: 0.35)),
          ),
          child: const Icon(Icons.warning_amber_rounded,
              size: 40, color: _accent),
        ),
        const SizedBox(height: 24),
        // Deliberately NOT localized: this is the global ErrorWidget.builder
        // fallback, which can render outside any Localizations ancestor (and
        // during failures where localization itself may be broken). English
        // literals keep the crash screen dependency-free. (#941 sweep exemption)
        const Text(
          'Something went adrift',
          textAlign: TextAlign.center,
          style: TextStyle(
            color: _textGhost,
            fontSize: 22,
            fontWeight: FontWeight.w600,
          ),
        ),
        const SizedBox(height: 10),
        const Text(
          'This screen hit an unexpected snag. Your data is safe — '
          'head back to the dashboard and try again.',
          textAlign: TextAlign.center,
          style: TextStyle(color: _textMuted, fontSize: 14, height: 1.5),
        ),
        // Only surface the raw exception in debug builds, never to end users.
        if (kDebugMode) ...[
          const SizedBox(height: 16),
          Text(
            details.exceptionAsString(),
            textAlign: TextAlign.center,
            maxLines: 4,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(color: _textMuted, fontSize: 11),
          ),
        ],
        const SizedBox(height: 28),
        GestureDetector(
          onTap: () {
            // Guard: routing can throw if the router isn't ready or the failure
            // originated from routing itself. Never let the escape hatch crash.
            try {
              _appRouter?.go(PortalMode.home);
            } catch (_) {
              // Nothing more we can safely do from the fallback.
            }
          },
          child: Container(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 12),
            decoration: BoxDecoration(
              gradient: const LinearGradient(
                colors: [_accent, Color(0xFF9F7AEA)],
              ),
              borderRadius: BorderRadius.circular(12),
            ),
            child: const Text(
              'Go to dashboard',
              style: TextStyle(
                color: _textGhost,
                fontSize: 14,
                fontWeight: FontWeight.w600,
              ),
            ),
          ),
        ),
      ],
    );
  }
}


