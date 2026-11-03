import 'package:buoy_ai/config/portal_mode.dart';
import 'package:buoy_ai/models/auth_models.dart';
import 'package:buoy_ai/providers/auth_provider.dart';
import 'package:buoy_ai/router/app_router.dart';
import 'package:buoy_ai/router/portal_router.dart';
import 'package:buoy_ai/router/post_auth_redirect.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// An [AuthProvider] in a fixed state, so the portal's sign-in rules can be checked without a server.
class _Auth extends AuthProvider {
  _Auth({
    this.fixedStatus = AuthStatus.unauthenticated,
    this.signedInBefore = false,
    this.fixedUser,
  });

  final AuthStatus fixedStatus;
  final bool signedInBefore;
  final User? fixedUser;

  @override
  AuthStatus get status => fixedStatus;
  @override
  bool get isAuthenticated => fixedStatus == AuthStatus.authenticated;
  @override
  bool get isLoading => fixedStatus == AuthStatus.loading;
  @override
  bool get hasSignedInBefore => signedInBefore;
  @override
  User? get user => fixedUser;
}

User _user({String firstName = 'Pat'}) => User(
      id: '1',
      email: 'pat@example.com',
      firstName: firstName,
      lastName: 'Lee',
      subscriptionTier: 'Free',
      createdAt: DateTime(2026, 10, 31),
    );

List<String> _topLevelPaths(GoRouter router) => [
      for (final route in router.configuration.routes)
        if (route is GoRoute) route.path,
    ];

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  setUp(() {
    SharedPreferences.setMockInitialValues({});
    PostAuthRedirect.now = DateTime.now;
    PortalMode.debugOverride = true;
    PostAuthRedirect.clear();
  });

  tearDown(() {
    PostAuthRedirect.clear();
    PortalMode.debugOverride = null;
  });

  group('portal mode', () {
    test('is only the /cloud/ path', () {
      expect(PortalMode.isPortalPath('/cloud/'), isTrue);
      expect(PortalMode.isPortalPath('/cloud/link'), isTrue);
      expect(PortalMode.isPortalPath('/cloud'), isTrue);
      for (final path in [
        '/app/',
        '/app/cloud/link',
        '/cloudy',
        '/',
        '/link'
      ]) {
        expect(PortalMode.isPortalPath(path), isFalse, reason: path);
      }
    });

    test('is off outside a browser (flutter test is not web)', () {
      PortalMode.debugOverride = null;
      expect(PortalMode.isActive, isFalse);
      expect(PortalMode.signupContextOrNull, isNull);
      expect(PortalMode.home, '/dashboard');
    });

    test(
        'a sign-up in the portal is a cloud sign-up and lands on the portal home',
        () {
      expect(PortalMode.signupContextOrNull, 'cloud');
      expect(PortalMode.home, '/');
      expect(PostAuthRedirect.destination(), '/');
    });
  });

  group('the portal router', () {
    test('has only the portal pages', () {
      final router = createRouter(_Auth());
      addTearDown(router.dispose);

      expect(_topLevelPaths(router), unorderedEquals(portalRoutePaths));
      expect(router.configuration.routes.whereType<ShellRoute>(), isEmpty,
          reason: 'no nav shell: no dashboard, onboarding or bank pages');
      for (final hosted in [
        '/dashboard',
        '/accounts',
        '/transactions',
        '/settings',
        '/settings/cloud',
        '/billing/plans',
        '/connect',
        '/update-connection/:connectionId',
        '/ai-quiz',
      ]) {
        expect(_topLevelPaths(router), isNot(contains(hosted)), reason: hosted);
      }
    });

    test('the app outside the portal keeps its own router', () {
      PortalMode.debugOverride = false;
      final router = createRouter(_Auth());
      addTearDown(router.dispose);

      expect(_topLevelPaths(router), contains('/cloud/link'));
      expect(_topLevelPaths(router), isNot(contains('/link')));
      expect(router.configuration.routes.whereType<ShellRoute>(), isNotEmpty);
    });
  });

  group('portal sign-in rules', () {
    test('wait while the session is still loading', () {
      expect(
          portalRedirect(
              _Auth(fixedStatus: AuthStatus.initial), Uri.parse('/link')),
          isNull);
      expect(
          portalRedirect(
              _Auth(fixedStatus: AuthStatus.loading), Uri.parse('/')),
          isNull);
    });

    test('a first-time visitor to a code link gets sign-up, carrying the code',
        () {
      final target = portalRedirect(_Auth(), Uri.parse('/link?code=BCDF-GH23'));

      expect(target,
          '/register?redirect=${Uri.encodeComponent('/link?code=BCDF-GH23')}');
      expect(PostAuthRedirect.pending, '/link?code=BCDF-GH23');
    });

    test('someone who signed in here before gets sign-in instead', () {
      final target = portalRedirect(
          _Auth(signedInBefore: true), Uri.parse('/link?code=BCDF-GH23'));

      expect(target, startsWith('/login?redirect='));
    });

    test('every other portal page needs sign-in', () {
      for (final path in ['/', '/billing', '/welcome-name']) {
        expect(portalRedirect(_Auth(), Uri.parse(path)), '/login',
            reason: path);
      }
    });

    test('sign-in, sign-up and the public pages are open', () {
      for (final path in [
        '/login',
        '/register',
        '/forgot-password',
        '/privacy-policy',
        '/terms'
      ]) {
        expect(portalRedirect(_Auth(), Uri.parse(path)), isNull, reason: path);
      }
    });

    test('after sign-in, the waiting code page, else the portal home', () {
      final signedIn =
          _Auth(fixedStatus: AuthStatus.authenticated, fixedUser: _user());

      expect(portalRedirect(signedIn, Uri.parse('/login')), '/');

      PostAuthRedirect.remember('/link?code=BCDF-GH23');
      expect(portalRedirect(signedIn, Uri.parse('/register')),
          '/link?code=BCDF-GH23');
      expect(
        portalRedirect(
            signedIn,
            Uri.parse(
                '/login?redirect=${Uri.encodeComponent('/link?code=ZZZZ-2222')}')),
        '/link?code=ZZZZ-2222',
      );
    });

    test('never sends anyone into the hosted app', () {
      final signedIn =
          _Auth(fixedStatus: AuthStatus.authenticated, fixedUser: _user());

      expect(
          portalRedirect(signedIn, Uri.parse('/login?redirect=%2Fdashboard')),
          '/');
      expect(
          portalRedirect(
              signedIn, Uri.parse('/login?redirect=%2Fcloud%2Flink')),
          '/');
      expect(PostAuthRedirect.isAllowed('/connect?request=1'), isFalse);
      expect(PostAuthRedirect.isAllowed('/link?code=X'), isTrue);
    });

    test('reaching the code page forgets the remembered target', () {
      final signedIn =
          _Auth(fixedStatus: AuthStatus.authenticated, fixedUser: _user());
      PostAuthRedirect.remember('/link?code=BCDF-GH23');

      expect(
          portalRedirect(signedIn, Uri.parse('/link?code=BCDF-GH23')), isNull);
      expect(PostAuthRedirect.pending, isNull);
    });

    test('a social sign-up with no name is asked for one first', () {
      final noName = _Auth(
          fixedStatus: AuthStatus.authenticated,
          fixedUser: _user(firstName: ''));

      expect(portalRedirect(noName, Uri.parse('/link?code=BCDF-GH23')),
          '/welcome-name');
    });

    test('a signed-in visitor on forgot-password goes home', () {
      final signedIn =
          _Auth(fixedStatus: AuthStatus.authenticated, fixedUser: _user());

      expect(portalRedirect(signedIn, Uri.parse('/forgot-password')), '/');
    });
  });
}
