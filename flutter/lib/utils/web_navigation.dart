import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/widgets.dart';
import 'package:go_router/go_router.dart';
import 'package:url_launcher/url_launcher.dart';

/// Navigates to the marketing "home" experience.
///
/// The in-app landing page was removed once the Astro marketing site took over
/// the domain root (issue #516 — the Flutter app is served under `/app/`). On
/// web this performs a full-page navigation *out* of the SPA to the Astro site
/// at the origin root, so "Home" and post-logout land on the real marketing
/// pages rather than a defunct in-app screen. On mobile there is no marketing
/// site, so we fall back to the login screen.
Future<void> goToMarketingHome(BuildContext context) async {
  if (kIsWeb) {
    // `Uri.base.origin` is e.g. https://buoy-ai.com — appending `/` targets the
    // domain root (Astro), not the app's `/app/` base-href. `_self` replaces the
    // current tab so the SPA is fully torn down.
    await launchUrl(
      Uri.parse('${Uri.base.origin}/'),
      webOnlyWindowName: '_self',
    );
  } else {
    context.go('/login');
  }
}

/// Full-page navigation OUT of the SPA to an arbitrary external URL.
///
/// Used by the OAuth consent page (`/connect`) to send the browser to the
/// assistant's `redirect_uri` after an approve/deny decision. On web this
/// REPLACES the current tab (`_self`) so the SPA is torn down and the browser
/// follows the OAuth callback — a new tab would strip the callback from the
/// original tab and leave a dead consent page behind. On native (deep-link
/// entry isn't a v1 path, but kept correct) it opens the system browser.
///
/// Returns `true` if the launch was dispatched, `false` on failure — the caller
/// always renders a manual "return to the assistant" link as a fallback.
Future<bool> navigateToExternalUrl(String url) async {
  final uri = Uri.tryParse(url);
  if (uri == null) return false;
  // Defence-in-depth: only ever hand http/https to the launcher. A malicious or
  // malformed redirect_uri using javascript:/data:/file: etc. must never reach
  // window.location — those schemes can execute script in the SPA's origin.
  if (uri.scheme != 'http' && uri.scheme != 'https') return false;
  try {
    if (kIsWeb) {
      return await launchUrl(uri, webOnlyWindowName: '_self');
    }
    return await launchUrl(uri, mode: LaunchMode.externalApplication);
  } catch (_) {
    // Plugin unavailable (tests) or the platform refused the scheme — the manual
    // link in the UI is the fallback.
    return false;
  }
}
