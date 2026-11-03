import 'package:sentry_flutter/sentry_flutter.dart';

/// Keeps the one-time `/cloud/link` handshake (`state`, `callback`, `name`, `code`) out of
/// telemetry. The `state` value is a secret the self-hosted server checks on return and
/// the `code` is the pairing code that authorises linking a server, so neither may reach
/// Sentry or PostHog. The URL can show up two ways: directly
/// (`/cloud/link?state=..`) or tucked inside the sign-in detour
/// (`/login?redirect=%2Fcloud%2Flink%3Fstate%3D..`).
///
/// Rule: any URL or path that mentions the link page loses its whole query and
/// fragment. A Cloud link code in an API path (`/cloud-servers/link-sessions/BCDF-GH23`,
/// also `/approve` and `/deny`) is replaced by `[code]`. Everything else is returned untouched.
class LinkUrlScrubber {
  LinkUrlScrubber._();

  /// What a link code in an API path becomes.
  static const placeholder = '[code]';

  /// The code segment after `link-sessions/`, except the server's own `poll` call.
  static final RegExp _linkSessionCode = RegExp(
    r'(link-sessions(?:/|%2f))(?!poll(?:[/?#]|$))(?!\[code\])([^/?#]+)',
    caseSensitive: false,
  );

  /// Three shapes count as sensitive:
  ///  - `cloud/link` or `cloud%2Flink`: the page itself, or the main app's detour to it;
  ///  - `/link?` or `%2Flink%3F` (or `/link#`): the cloud portal's own signed-out detour,
  ///    `/cloud/login?redirect=%2Flink%3Fcode%3D..`, where the in-app path has no `cloud` part;
  ///  - a `code=` query parameter, which is the pairing code (`/link?code=ABCD-EFGH`).
  static final RegExp _mentionsLink = RegExp(
    r'cloud(/|%2f)link'
    r'|(/|%2f)link(\?|%3f|#|%23)'
    r'|(^|[?&]|%3f|%26)code(=|%3d)',
    caseSensitive: false,
  );

  /// True when [value] points at, or carries a redirect to, the link page, or
  /// carries a link code in an API path.
  static bool isSensitive(String? value) =>
      value != null &&
      (_mentionsLink.hasMatch(value) || _linkSessionCode.hasMatch(value));

  /// [value] with any link code in its path replaced, and its query and
  /// fragment removed when it points at the link page.
  static String? scrub(String? value) {
    if (value == null) return null;
    final redacted =
        value.replaceAllMapped(_linkSessionCode, (m) => '${m[1]}$placeholder');
    if (!_mentionsLink.hasMatch(redacted)) return redacted;
    final cut = redacted.indexOf(RegExp(r'[?#]'));
    return cut < 0 ? redacted : redacted.substring(0, cut);
  }

  static dynamic _scrubValue(dynamic v) {
    if (v is String) return isSensitive(v) ? scrub(v) : v;
    if (v is Map) {
      return v.map((k, val) => MapEntry(k, _scrubValue(val)));
    }
    if (v is List) return v.map(_scrubValue).toList();
    return v;
  }

  /// Sentry `beforeBreadcrumb`: scrubs the message and every string in the data.
  static Breadcrumb? breadcrumb(Breadcrumb? crumb, Hint hint) {
    if (crumb == null) return null;
    crumb.message = scrub(crumb.message);
    final data = crumb.data;
    if (data != null) {
      crumb.data = Map<String, dynamic>.from(_scrubValue(data) as Map);
    }
    return crumb;
  }

  /// Sentry `beforeSend`: scrubs the request URL (web adds the full page address),
  /// the transaction name and the breadcrumbs already on the event.
  static SentryEvent event(SentryEvent event, Hint hint) {
    final request = event.request;
    if (request != null &&
        (isSensitive(request.url) || isSensitive(request.queryString))) {
      request.url = scrub(request.url);
      request.queryString = '';
      request.fragment = '';
    }
    event.transaction = scrub(event.transaction);
    for (final c in event.breadcrumbs ?? const <Breadcrumb>[]) {
      breadcrumb(c, hint);
    }
    // A second line of defence for anything that set a path directly.
    final tags = event.tags;
    if (tags != null && tags.values.any(isSensitive)) {
      event.tags = tags.map((k, v) => MapEntry(k, scrub(v) ?? v));
    }
    final fingerprint = event.fingerprint;
    if (fingerprint != null && fingerprint.any(isSensitive)) {
      event.fingerprint = fingerprint.map((f) => scrub(f) ?? f).toList();
    }
    return event;
  }
}
