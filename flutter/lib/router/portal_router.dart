import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:provider/provider.dart';
import 'package:sentry_flutter/sentry_flutter.dart';

import '../config/portal_mode.dart';
import '../l10n/generated/app_localizations.dart';
import '../pages/cloud_billing_page.dart';
import '../pages/cloud_code_link_page.dart';
import '../pages/cloud_link_page.dart';
import '../pages/cloud_page.dart';
import '../pages/forgot_password_page.dart';
import '../pages/public/login_page.dart';
import '../pages/public/privacy_policy_page.dart';
import '../pages/public/terms_of_service_page.dart';
import '../pages/register_page.dart';
import '../pages/welcome_name_page.dart';
import '../providers/auth_provider.dart';
import '../widgets/cinematic/glass_settings.dart';
import 'post_auth_redirect.dart';

/// Every page the Cloud portal has (paths inside `/cloud/`). Nothing else exists there: no dashboard, no hosted
/// onboarding, no bank connections. Kept as a list so a test can hold the portal to it.
const portalRoutePaths = <String>[
  '/', // add-ons, billing card, your servers
  '/link', // type or open a code a server shows, then approve or deny it
  '/billing',
  '/login',
  '/register', // Cloud-only sign up
  '/forgot-password',
  '/welcome-name',
  '/privacy-policy',
  '/terms',
];

const _authRoutes = {'/login', '/register'};
const _publicRoutes = {'/forgot-password', '/privacy-policy', '/terms'};

/// The portal's sign-in rules, as a function a test can call. Null means stay.
///
/// Signed out: everything but sign-in, sign-up and the public pages goes to sign-in. The link page survives the
/// trip with its code (remembered, and carried as `?redirect=`); someone who has never signed in here gets sign-up
/// instead, because they most likely have no account yet. Signed in: sign-in and sign-up go to the remembered page
/// or the portal home.
@visibleForTesting
String? portalRedirect(AuthProvider auth, Uri uri) {
  if (auth.isLoading || auth.status == AuthStatus.initial) return null;

  final path = uri.path;
  final isAuth = auth.isAuthenticated;
  final isAuthRoute = _authRoutes.contains(path);
  final isPublic = _publicRoutes.contains(path);

  if (isAuth && isAuthRoute) {
    final redirectTo = uri.queryParameters['redirect'];
    if (PostAuthRedirect.isAllowed(redirectTo)) return redirectTo;
    return PostAuthRedirect.destination();
  }
  if (!isAuth && isAuthRoute) {
    PostAuthRedirect.remember(uri.queryParameters['redirect']);
    return null;
  }

  if (!isAuth && !isPublic) {
    if (path == '/link') {
      final target = uri.toString();
      PostAuthRedirect.remember(target);
      final form = auth.hasSignedInBefore ? '/login' : '/register';
      return '$form?redirect=${Uri.encodeComponent(target)}';
    }
    return '/login';
  }

  if (isAuth && path == '/forgot-password') return '/';

  // Same name gate as the app: a social sign-up with no name gets one field first.
  final needsName = isAuth && (auth.user?.firstName.trim().isEmpty ?? false);
  if (needsName && path != '/welcome-name' && !isPublic) return '/welcome-name';
  if (!needsName && path == '/welcome-name') {
    return isAuth ? PostAuthRedirect.destination() : '/login';
  }

  if (isAuth && PostAuthRedirect.allowedPaths.contains(path)) {
    PostAuthRedirect.clear();
  }
  return null;
}

/// The router the app uses when it runs as the Cloud portal ([PortalMode.isActive]).
GoRouter createPortalRouter(AuthProvider authProvider) {
  return GoRouter(
    initialLocation: '/',
    refreshListenable: authProvider,
    errorBuilder: (context, state) => const _PortalNotFoundPage(),
    observers: [SentryNavigatorObserver()],
    redirect: (context, state) => portalRedirect(authProvider, state.uri),
    routes: [
      GoRoute(path: '/', builder: (context, state) => const _PortalHome()),
      GoRoute(
        path: '/link',
        builder: (context, state) {
          final q = state.uri.queryParameters;
          // A server on the older one-click flow (1.5.35) still sends ?callback=&state=: keep it working.
          if (q.containsKey('callback') && q.containsKey('state')) {
            return CloudLinkPage(
                callback: q['callback'], state: q['state'], name: q['name']);
          }
          return CloudCodeLinkPage(code: q['code']);
        },
      ),
      GoRoute(
        path: '/billing',
        builder: (context, state) => const CloudBillingPage(portal: true),
      ),
      GoRoute(path: '/login', builder: (context, state) => const LoginPage()),
      GoRoute(
        path: '/register',
        builder: (context, state) => RegisterPage(
          fromGoogleNoAccount:
              state.uri.queryParameters['google_no_account'] == '1',
        ),
      ),
      GoRoute(
        path: '/forgot-password',
        builder: (context, state) => const ForgotPasswordPage(),
      ),
      GoRoute(
        path: '/welcome-name',
        builder: (context, state) => const WelcomeNamePage(),
      ),
      GoRoute(
        path: '/privacy-policy',
        builder: (context, state) => const PrivacyPolicyPage(),
      ),
      GoRoute(
        path: '/terms',
        builder: (context, state) => const TermsOfServicePage(),
      ),
    ],
  );
}

/// The portal home. Until the session is known it shows a spinner (the redirect sends a signed-out visitor to
/// sign-in once it is).
class _PortalHome extends StatelessWidget {
  const _PortalHome();

  @override
  Widget build(BuildContext context) {
    final auth = context.watch<AuthProvider>();
    if (!auth.isAuthenticated) {
      return const Scaffold(
        backgroundColor: Color(0xFF04040A),
        body: Center(child: GlassSpinner()),
      );
    }
    return const CloudPage(portal: true);
  }
}

class _PortalNotFoundPage extends StatelessWidget {
  const _PortalNotFoundPage();

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return GlassSubPage(
      title: l10n.uiPageNotFound,
      backdrop: true,
      showBack: false,
      maxWidth: 560,
      children: [
        GlassNote(
            key: const ValueKey('portal-not-found'), text: l10n.portalNotFound),
        const SizedBox(height: 16),
        GlassPrimaryButton(
          key: const ValueKey('portal-not-found-home'),
          onPressed: () => context.go('/'),
          label: l10n.portalGoHome,
        ),
      ],
    );
  }
}
