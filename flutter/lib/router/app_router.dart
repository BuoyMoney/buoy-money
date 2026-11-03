import '../l10n/generated/app_localizations.dart';
import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:posthog_flutter/posthog_flutter.dart';
import 'package:sentry_flutter/sentry_flutter.dart';
import '../config/portal_mode.dart';
import '../config/server_capabilities.dart';
import '../providers/auth_provider.dart';
import '../models/billing_models.dart';
import '../services/analytics_service.dart';
import '../pages/public/login_page.dart';
import '../pages/public/first_run_page.dart';
import '../pages/public/oidc_callback_page.dart';
import '../services/oidc_sign_in_service.dart';
import '../pages/public/privacy_policy_page.dart';
import '../pages/public/terms_of_service_page.dart';
import '../pages/register_page.dart';
import '../pages/welcome_name_page.dart';
import '../pages/forgot_password_page.dart';
import '../pages/reset_password_page.dart';
import '../pages/accept_invitation_page.dart';
import '../pages/dashboard_page.dart';
import '../pages/transactions_page.dart';
import '../pages/budgets_page.dart';
import '../pages/accounts_page.dart';
import '../pages/ai_settings_page.dart';
import '../pages/admin_broadcast_page.dart';
import '../pages/ai_usage_page.dart';
import '../pages/ai_chat_page.dart';
import '../pages/savings_page.dart';
import '../pages/subscriptions_page.dart';
import '../pages/notifications_page.dart';
import '../pages/profile_page.dart';
import '../pages/settings_page.dart';
import '../pages/billing_page.dart';
import '../pages/insights_page.dart';
import '../pages/achievements_page.dart';
import '../pages/family_page.dart';
import '../pages/categorization_rules_page.dart';
import '../pages/email_preferences_page.dart';
import '../pages/receipts_page.dart';
import '../pages/trusted_devices_page.dart';
import '../pages/update_connection_page.dart';
import '../pages/ai_quiz_page.dart';
import '../pages/connect_page.dart';
import '../pages/cloud_billing_page.dart';
import '../pages/cloud_link_page.dart';
import '../pages/cloud_page.dart';
import '../pages/import_page.dart';
import '../pages/simplefin_page.dart';
import '../pages/wrapped_page.dart';
import '../utils/navigation_depth.dart';
import 'portal_router.dart';
import 'post_auth_redirect.dart';
import 'signed_out_landing.dart';
import '../widgets/cinematic/cinematic_nav_shell.dart';

/// Builds a depth-aware slide transition for protected routes.
/// Content slides UP when diving deeper, DOWN when surfacing.
/// Both entering and exiting pages move in the same direction.
CustomTransitionPage<void> _depthTransitionPage({
  required GoRouterState state,
  required Widget child,
}) {
  final direction = NavigationDepth.getSlideDirection(state.uri.path);

  return CustomTransitionPage<void>(
    key: state.pageKey,
    child: child,
    transitionDuration: const Duration(milliseconds: 500),
    reverseTransitionDuration: const Duration(milliseconds: 400),
    transitionsBuilder: (context, animation, secondaryAnimation, child) {
      // ENTERING: animation goes 0→1 when this page appears.
      // Delay ~200ms so the exiting page clears first.
      final delayedEnter = const Interval(0.35, 1.0, curve: Curves.easeOutCubic)
          .transform(animation.value);
      final enterOffset =
          Offset.lerp(direction, Offset.zero, delayedEnter)!;
      final enterOpacity = const Interval(0.30, 0.80)
          .transform(animation.value)
          .clamp(0.0, 1.0);

      // EXITING: secondaryAnimation goes 0→1 when a NEW page
      // pushes this one out. Compressed to 0–30% so it clears
      // before the entering page starts at 35%.
      final exitProgress = const Interval(0.0, 0.30, curve: Curves.easeInCubic)
          .transform(secondaryAnimation.value);
      final exitEnd = Offset(0, -NavigationDepth.lastDirection.dy);
      final exitOffset =
          Offset.lerp(Offset.zero, exitEnd, exitProgress)!;
      final exitOpacity = (1.0 -
              const Interval(0.0, 0.25)
                  .transform(secondaryAnimation.value))
          .clamp(0.0, 1.0);

      // Combine enter + exit transforms.
      // Use RepaintBoundary to isolate transition painting from
      // the background and nav layers.
      return RepaintBoundary(
        child: Opacity(
          opacity: (enterOpacity * exitOpacity).clamp(0.0, 1.0),
          child: FractionalTranslation(
            translation: enterOffset + exitOffset,
            child: child,
          ),
        ),
      );
    },
  );
}

/// Neutral splash shown at the app root ('/') while auth state initializes,
/// before the router redirects to /dashboard or /login. Kept intentionally
/// minimal — it is on screen for a fraction of a second on cold load.
class _SplashPage extends StatelessWidget {
  const _SplashPage();

  @override
  Widget build(BuildContext context) {
    return const Scaffold(
      backgroundColor: Color(0xFF04040A),
      body: Center(
        child: CircularProgressIndicator(
          color: Color(0xFF7B61FF),
          strokeWidth: 2,
        ),
      ),
    );
  }
}

/// Shown by the router's [GoRouter.errorBuilder] when a location doesn't match
/// any route (unknown path, malformed deep link). Offers a way back rather than
/// dead-ending on GoRouter's default error scaffold.
class _RouteNotFoundPage extends StatelessWidget {
  final Uri uri;
  const _RouteNotFoundPage({required this.uri});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: const Color(0xFF04040A),
      body: Center(
        child: Padding(
          padding: const EdgeInsets.all(32),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(Icons.explore_off_outlined,
                  size: 48,
                  color: const Color(0xFF7B61FF).withValues(alpha: 0.6)),
              const SizedBox(height: 16),
              Text(
                AppLocalizations.of(context).uiPageNotFound,
                style: GoogleFonts.sora(
                  fontSize: 22,
                  fontWeight: FontWeight.w600,
                  color: const Color(0xFFF0EFF4),
                ),
              ),
              const SizedBox(height: 8),
              Text(
                AppLocalizations.of(context).uiCouldntFindPath(uri.path),
                textAlign: TextAlign.center,
                style: GoogleFonts.sora(
                  fontSize: 13,
                  color: const Color(0xFF8A8A9A),
                ),
              ),
              const SizedBox(height: 24),
              GestureDetector(
                onTap: () => context.go('/dashboard'),
                child: Container(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 24, vertical: 12),
                  decoration: BoxDecoration(
                    gradient: const LinearGradient(
                      colors: [Color(0xFF7B61FF), Color(0xFF9F7AEA)],
                    ),
                    borderRadius: BorderRadius.circular(12),
                  ),
                  child: Text(
                    AppLocalizations.of(context).uiGoToDashboard,
                    style: GoogleFonts.sora(
                      fontSize: 14,
                      fontWeight: FontWeight.w600,
                      color: const Color(0xFFF0EFF4),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

/// Cinematic placeholder for pages not yet implemented.
class CinematicPlaceholderPage extends StatelessWidget {
  final String title;
  const CinematicPlaceholderPage(this.title, {super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      backgroundColor: Colors.transparent,
      body: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(Icons.construction_outlined,
                size: 48,
                color: const Color(0xFF7B61FF).withValues(alpha: 0.5)),
            const SizedBox(height: 16),
            Text(
              title,
              style: GoogleFonts.sora(
                fontSize: 24,
                fontWeight: FontWeight.bold,
                color: const Color(0xFFF0EFF4),
              ),
            ),
            const SizedBox(height: 8),
            Text(
              AppLocalizations.of(context).uiComingSoon,
              style: GoogleFonts.sora(
                fontSize: 14,
                color: const Color(0xFF8A8A9A),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

// Global Router Conf
GoRouter createRouter(AuthProvider authProvider) {
  // Under buoy-ai.com/cloud/ the same bundle is the Cloud portal, with its own short list of pages.
  if (PortalMode.isActive) return createPortalRouter(authProvider);
  return GoRouter(
    initialLocation: '/',
    refreshListenable: authProvider,
    // Unknown / malformed routes (bad deep links, stale bookmarks) land here
    // instead of GoRouter's bare-bones default error page.
    errorBuilder: (context, state) => _RouteNotFoundPage(uri: state.uri),
    observers: [
      // PostHog automatic screen view tracking — only when the user has opted
      // into analytics (#606). If they opt in later via the banner, screen-view
      // tracking resumes on the next app launch (explicit events fire either way).
      if (analytics.consentGranted) PosthogObserver(),
      // Attribute every Sentry event to a route: sets the current screen as the
      // event transaction and emits navigation breadcrumbs, so a crash/500 is
      // tied to the screen the user was on instead of transaction=None/root.
      SentryNavigatorObserver(),
    ],
    redirect: (context, state) {
      // A sign-on return that reached the router as the raw custom-scheme link
      // (com.buoyai.buoymoney://oidc?code=...) goes to the page that trades the code.
      if (OidcSignInService.isCallbackLink(state.uri)) {
        return state.uri.hasQuery ? '/auth/oidc?${state.uri.query}' : '/auth/oidc';
      }
      // If auth is still initializing or loading, don't redirect yet — the root
      // route shows a neutral splash during the token check so we don't flash
      // login before a returning user's session is restored.
      if (authProvider.isLoading || authProvider.status == AuthStatus.initial) {
        return null;
      }

      final isAuth = authProvider.isAuthenticated;

      // The in-app landing page was removed — the Astro marketing site now owns
      // the domain root. The SPA root ('/app/' → GoRouter '/') is only hit on a
      // bare app load or bookmark, so funnel it to the dashboard when signed in,
      // otherwise to login. "Home"/logout buttons navigate out to Astro directly
      // (see goToMarketingHome), which is a full-page nav GoRouter can't express.
      if (state.uri.path == '/') {
        if (isAuth) return '/dashboard';

        // A referral link, the first-launch question, or sign-up vs sign-in: see signedOutLanding. The
        // referral code itself was already captured at startup and persisted, so it survives this redirect
        // and the OAuth round trips that follow.
        final hasReferral =
            (state.uri.queryParameters['ref'] ?? '').trim().isNotEmpty;
        return signedOutLanding(
            hasSignedInBefore: authProvider.hasSignedInBefore,
            hasReferral: hasReferral);
      }

      final isAuthRoute = state.uri.path == '/login' || state.uri.path == '/register';
      final isInvitationRoute = state.uri.path.startsWith('/accept-invitation');
      final isPublicRoute = state.uri.path == '/privacy-policy'
          || state.uri.path == '/terms'
          || state.uri.path == '/forgot-password'
          || state.uri.path.startsWith('/reset-password')
          // Single sign-on return: trades its one-time code before anyone is signed in.
          || state.uri.path == '/auth/oidc'
          // First launch: "How would you like to use Buoy Money?"
          || state.uri.path == firstRunPath
          || isInvitationRoute;

      if (isAuth && isAuthRoute) {
        // If logging in / registering with an invitation token, go to accept page
        final invitationToken = state.uri.queryParameters['invitation'];
        if (invitationToken != null && invitationToken.isNotEmpty) {
          return '/accept-invitation/$invitationToken';
        }
        // Preserve an OAuth-consent deep link across the login round-trip: a user
        // bounced from /connect?request=<id> carries the destination as ?redirect=
        // so ?request= survives login. Scoped to /connect only (the sole protected
        // deep link that must round-trip today) — never an open redirect.
        // Scoped to the pages in PostAuthRedirect.allowedPaths (/connect and
        // /cloud/link) and to in-app paths only, so never an open redirect. The
        // remembered target covers a trip through register, or a reload.
        final redirectTo = state.uri.queryParameters['redirect'];
        if (PostAuthRedirect.isAllowed(redirectTo)) return redirectTo;
        return PostAuthRedirect.destination();
      }

      // Signed out on sign-in or sign-up with a ?redirect= to a page that must
      // come back after: remember it, so switching between the two forms (or
      // making a new account) still lands there.
      if (!isAuth && isAuthRoute) {
        PostAuthRedirect.remember(state.uri.queryParameters['redirect']);
      }

      // Authenticated users on accept-invitation should stay there
      if (isAuth && isPublicRoute && !isInvitationRoute) {
        return '/dashboard';
      }

      // If heading to a protected route and not logged in, go to login
      if (!isAuth && !isAuthRoute && !isPublicRoute) {
        // The consent page must survive the detour through login with its
        // ?request= intact — the bare guard would drop it. Carry the full
        // destination (path+query) as an encoded ?redirect= that the
        // isAuth&&isAuthRoute branch above honors after sign-in.
        if (state.uri.path == '/connect') {
          return '/login?redirect=${Uri.encodeComponent(state.uri.toString())}';
        }
        // "Link your server" from a self-hosted server: the same round trip.
        // Someone who has never signed in on this device most likely has no
        // account yet, so they get sign-up (which brings them back here too).
        if (state.uri.path == '/cloud/link') {
          PostAuthRedirect.remember(state.uri.toString());
          final form = authProvider.hasSignedInBefore ? '/login' : '/register';
          return '$form?redirect=${Uri.encodeComponent(state.uri.toString())}';
        }
        return '/login';
      }

      // Name gate: social sign-ins (Apple, "Hide My Email") can leave the account
      // with no name, which would greet the user as a bare ".". Funnel such users
      // to a one-field name page before the app proper — parity with the register
      // form, which requires a name. Cleared as soon as a name is saved.
      final isNameRoute = state.uri.path == '/welcome-name';
      final needsName =
          isAuth && (authProvider.user?.firstName.trim().isEmpty ?? false);
      if (needsName && !isNameRoute && !isPublicRoute) {
        return '/welcome-name';
      }
      if (!needsName && isNameRoute) {
        return isAuth ? PostAuthRedirect.destination() : '/login';
      }

      // Arrived at the page we were holding on to: forget it.
      if (isAuth && PostAuthRedirect.allowedPaths.contains(state.uri.path)) {
        PostAuthRedirect.clear();
      }

      return null;
    },
    routes: [
      // App root ('/app/'). The old in-app landing page is gone; the redirect
      // above bounces '/' to /dashboard or /login. This builder only renders
      // during auth initialization (redirect returns null while loading), so it
      // shows a neutral splash instead of flashing a page or a router error.
      GoRoute(
        path: '/',
        builder: (context, state) => const _SplashPage(),
      ),
      GoRoute(
        path: '/login',
        builder: (context, state) => const LoginPage(),
      ),
      // First launch of the phone app: hosted by Buoy Money, or a server of your own.
      GoRoute(
        path: firstRunPath,
        builder: (context, state) => const FirstRunPage(),
      ),
      // Single sign-on lands here (web redirect, or the phone's deep link via main.dart).
      GoRoute(
        path: '/auth/oidc',
        builder: (context, state) => OidcCallbackPage(
          code: state.uri.queryParameters['code'],
          error: state.uri.queryParameters['error'],
          next: state.uri.queryParameters['next'],
        ),
      ),
      GoRoute(
        path: '/welcome-name',
        builder: (context, state) => const WelcomeNamePage(),
      ),
      GoRoute(
        path: '/register',
        builder: (context, state) => RegisterPage(
          invitationToken: state.uri.queryParameters['invitation'],
          fromGoogleNoAccount:
              state.uri.queryParameters['google_no_account'] == '1',
        ),
      ),
      GoRoute(
        path: '/privacy-policy',
        builder: (context, state) => const PrivacyPolicyPage(),
      ),
      GoRoute(
        path: '/terms',
        builder: (context, state) => const TermsOfServicePage(),
      ),
      GoRoute(
        path: '/forgot-password',
        builder: (context, state) => const ForgotPasswordPage(),
      ),
      GoRoute(
        path: '/reset-password/:token',
        builder: (context, state) => ResetPasswordPage(
          token: state.pathParameters['token']!,
        ),
      ),
      GoRoute(
        path: '/accept-invitation/:token',
        builder: (context, state) => AcceptInvitationPage(
          token: state.pathParameters['token']!,
        ),
      ),

      // Plaid's iOS OAuth redirect_uri (https://buoy-ai.com/app/plaid-oauth). In the native app
      // the Universal Link is swallowed in main.dart and Plaid Link resumes on its own; this
      // route only catches the case where the link opens in a browser instead, and sends the
      // user somewhere useful.
      GoRoute(
        path: '/plaid-oauth',
        redirect: (context, state) => '/accounts',
      ),

      // OAuth consent for "BuoyAI Connect" (RFC #782). Top-level (not inside the
      // CinematicNavShell) so it renders full-screen without the bottom nav —
      // it's a standalone consent surface, not part of the authed app shell. It
      // is NOT in the public-route list, so the redirect guard above requires a
      // session (bouncing to login and back with ?request= preserved).
      // "Link your Buoy Money server" (hosted): a self-hosted server sends its
      // admin here to approve it. Top-level like /connect, and like /connect it
      // is not public, so a signed-out visitor goes to sign-in or sign-up and
      // comes back with the same query.
      GoRoute(
        path: '/cloud/link',
        builder: (context, state) => CloudLinkPage(
          callback: state.uri.queryParameters['callback'],
          state: state.uri.queryParameters['state'],
          name: state.uri.queryParameters['name'],
        ),
      ),
      GoRoute(
        path: '/connect',
        builder: (context, state) => ConnectPage(
          requestId: state.uri.queryParameters['request'],
        ),
      ),

      // Money Wrapped stories. Top-level (no nav shell) so they own the whole
      // screen — same pattern as /connect; auth-guarded by absence from the
      // public-route list. Deep-linked from money_wrapped notifications.
      GoRoute(
        path: '/wrapped/yearly/:year',
        redirect: (context, state) =>
            int.tryParse(state.pathParameters['year'] ?? '') == null
                ? '/dashboard'
                : null,
        builder: (context, state) => WrappedPage(
          year: int.parse(state.pathParameters['year']!),
        ),
      ),
      GoRoute(
        path: '/wrapped/:year/:month',
        redirect: (context, state) {
          final y = int.tryParse(state.pathParameters['year'] ?? '');
          final m = int.tryParse(state.pathParameters['month'] ?? '');
          return (y == null || m == null || m < 1 || m > 12) ? '/dashboard' : null;
        },
        builder: (context, state) => WrappedPage(
          year: int.parse(state.pathParameters['year']!),
          month: int.parse(state.pathParameters['month']!),
        ),
      ),

      // PROTECTED ROUTES — wrapped in CinematicNavShell
      ShellRoute(
        // Track authenticated page views (dashboard, accounts, transactions, …). They live on
        // THIS ShellRoute's nested navigator, which the root router's PosthogObserver never sees —
        // so without this, PostHog recorded zero authed screens (only pre-auth /, /login).
        observers: [
          if (analytics.consentGranted) PosthogObserver(),
          // Authed screens live on this nested navigator; the root observer never
          // sees them, so register a SentryNavigatorObserver here too — otherwise
          // dashboard/transactions/budgets crashes carry no route/transaction.
          SentryNavigatorObserver(),
        ],
        builder: (context, state, child) =>
            CinematicNavShell(state: state, child: child),
        routes: [
          GoRoute(
            path: '/dashboard',
            pageBuilder: (context, state) =>
                _depthTransitionPage(state: state, child: const DashboardPage()),
          ),
          GoRoute(
            path: '/transactions',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: TransactionsPage(
                  initialCategoryId: state.uri.queryParameters['categoryId'],
                  initialStartDate: state.uri.queryParameters['startDate'],
                  initialEndDate: state.uri.queryParameters['endDate'],
                )),
          ),
          GoRoute(
            path: '/budgets',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const BudgetsPage()),
          ),
          GoRoute(
            path: '/savings',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const SavingsPage()),
          ),
          GoRoute(
            path: '/accounts',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                // ?add=1 opens "Add a bank" straight away (the owner's setup checklist).
                child: AccountsPage(
                    openAddBank: state.uri.queryParameters['add'] == '1')),
          ),
          GoRoute(
            path: '/subscriptions',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const SubscriptionsPage()),
          ),
          GoRoute(
            path: '/insights',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const InsightsPage()),
          ),
          GoRoute(
            path: '/ai-chat',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AIChatPage()),
          ),
          GoRoute(
            path: '/achievements',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AchievementsPage()),
          ),
          GoRoute(
            path: '/notifications',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const NotificationsPage()),
          ),
          GoRoute(
            path: '/profile',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const ProfilePage()),
          ),
          GoRoute(
            path: '/settings',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const SettingsPage()),
          ),
          GoRoute(
            path: '/billing',
            // A self-hosted server has no plans to manage.
            redirect: (context, state) =>
                ServerEdition.current.value.hasBilling ? null : '/settings',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const BillingPage()),
          ),
          GoRoute(
            path: '/admin/broadcast',
            // Hosted edition and administrators only; the server enforces both again.
            redirect: (context, state) {
              if (!ServerEdition.current.value.hasAdminBroadcast) return '/settings';
              final tier = authProvider.user?.subscriptionTier;
              // Profile still loading (cold deep link): keep the route; refreshListenable re-runs this.
              if (tier == null && authProvider.isLoading) return null;
              final isAdmin = tier != null &&
                  SubscriptionTierExtension.fromString(tier) == SubscriptionTier.admin;
              return isAdmin ? null : '/dashboard';
            },
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AdminBroadcastPage()),
          ),
          GoRoute(
            path: '/family',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const FamilyPage()),
          ),
          GoRoute(
            path: '/settings/rules',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const CategorizationRulesPage()),
          ),
          GoRoute(
            path: '/settings/import',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const ImportPage()),
          ),
          GoRoute(
            path: '/settings/cloud',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: CloudPage(
                    linked: state.uri.queryParameters['linked'] == '1',
                    linkError: state.uri.queryParameters['link_error'])),
          ),
          GoRoute(
            path: '/settings/cloud/billing',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const CloudBillingPage()),
          ),
          GoRoute(
            path: '/settings/simplefin',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const SimpleFinPage()),
          ),
          GoRoute(
            path: '/settings/receipts',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const ReceiptsPage()),
          ),
          GoRoute(
            path: '/settings/ai',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AiSettingsPage()),
          ),
          GoRoute(
            path: '/settings/ai-usage',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AiUsagePage()),
          ),
          GoRoute(
            path: '/settings/email-preferences',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const EmailPreferencesPage()),
          ),
          GoRoute(
            path: '/profile/trusted-devices',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const TrustedDevicesPage()),
          ),
          GoRoute(
            path: '/ai-quiz',
            pageBuilder: (context, state) => _depthTransitionPage(
                state: state,
                child: const AiQuizPage()),
          ),
          GoRoute(
            path: '/update-connection/:connectionId',
            // A malformed connectionId (e.g. a deep link with a non-numeric or
            // missing segment) must not throw a FormatException in the builder —
            // redirect back to accounts instead of hard-crashing the route.
            redirect: (context, state) {
              final raw = state.pathParameters['connectionId'];
              if (!ServerEdition.current.value.hasBankConnection) return '/accounts';
              return int.tryParse(raw ?? '') == null ? '/accounts' : null;
            },
            pageBuilder: (context, state) {
              final connectionId =
                  int.tryParse(state.pathParameters['connectionId'] ?? '') ?? 0;
              final institutionName = state.uri.queryParameters['institutionName'];
              // newAccounts=1 (from the NEW_ACCOUNTS_AVAILABLE notification) opens Link
              // update mode with account selection so the user can add the new accounts.
              final newAccounts =
                  state.uri.queryParameters['newAccounts'] == '1';
              return _depthTransitionPage(
                state: state,
                child: UpdateConnectionPage(
                  connectionId: connectionId,
                  institutionName: institutionName,
                  accountSelection: newAccounts,
                ),
              );
            },
          ),
        ],
      ),
    ],
  );
}
