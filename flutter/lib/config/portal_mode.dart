import 'package:flutter/foundation.dart' show kIsWeb, visibleForTesting;

/// The BuoyAI Cloud portal: the hosted web build served a second time at `https://buoy-ai.com/cloud/` with its own
/// `index.html` (`<base href="/cloud/">`, see `scripts/docker/make-portal-shell.sh`). There the app runs a small
/// router of its own (`lib/router/portal_router.dart`): link a server by code, sign in, Cloud-only sign up, Cloud
/// add-ons, billing and your servers. No dashboard, no hosted onboarding, no bank connections.
///
/// The portal is kept out of `/app/` on purpose: an installed app or web app that claims `/app/` links would
/// otherwise grab the link a self-hosted server sends its owner to.
class PortalMode {
  PortalMode._();

  /// The path the portal is served under. GoRouter paths inside it drop this prefix (`/cloud/link` is `/link`).
  static const basePath = '/cloud/';

  /// What a sign-up started in the portal sends as `signupContext`: the new account is Cloud-only (no hosted
  /// trial, no hosted onboarding emails).
  static const signupContext = 'cloud';

  static bool? _override;

  /// Forces portal mode on or off in tests; null restores the real check.
  @visibleForTesting
  static set debugOverride(bool? value) => _override = value;

  /// True when this is the web build running under `/cloud/`. Never on a phone or desktop build.
  static bool get isActive =>
      _override ?? (kIsWeb && isPortalPath(Uri.base.path));

  /// True for `/cloud` and anything under `/cloud/`, and nothing else (`/cloudy`, `/app/cloud/link`).
  static bool isPortalPath(String path) =>
      path == '/cloud' || path.startsWith(basePath);

  /// [signupContext] while in portal mode, otherwise null (an ordinary sign-up).
  static String? get signupContextOrNull => isActive ? signupContext : null;

  /// Where a signed-in person lands when nothing else is waiting: the portal home, or the dashboard.
  static String get home => isActive ? '/' : '/dashboard';
}
