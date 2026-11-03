/// Normalizes a server-provided `actionUrl` (from a notification, push message, etc.) into an
/// in-app GoRouter route, or null if it isn't a route we should navigate to internally.
///
/// The `/app` prefix is the **web base-href** (the app is served at buoy-ai.com/app/…). It belongs
/// in email and external deep links, never in GoRouter's route space — which is unprefixed on web
/// (the base-href is applied by the browser) and on mobile alike. So a stored `/app/achievements`
/// navigates to a route that doesn't exist and shows "Page not found". Stripping it here keeps
/// notifications that were written with the prefix working, regardless of the backend fix.
///
/// External `http(s)` links are the OS's job (a browser), not GoRouter — return null so the caller
/// doesn't feed them to `context.go`.
String? normalizeInAppRoute(String? actionUrl) {
  if (actionUrl == null) return null;
  final url = actionUrl.trim();
  if (url.isEmpty) return null;
  if (url.startsWith('http://') || url.startsWith('https://')) return null;
  if (!url.startsWith('/')) return null;
  if (url == '/app') return '/';
  if (url.startsWith('/app/')) return url.substring(4); // '/app/achievements' → '/achievements'
  return url;
}
