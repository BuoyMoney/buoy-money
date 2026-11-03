// PostHog web initialization.
//
// Loaded after array.full.js (which registers window.posthog). We load PostHog as an
// external script rather than the usual inline snippet because the app CSP has no
// 'unsafe-inline' in script-src; us-assets.i.posthog.com (script) and us.i.posthog.com
// (connect) are allowlisted in nginx-csp-*.conf.
//
// The __POSTHOG_KEY__ placeholder is replaced at image-build time by Dockerfile.flutter
// (sed, from the POSTHOG_KEY build arg / CI secret), mirroring the Google client-id swap.
// In local dev (no Docker) the placeholder stays; with opt-out-by-default nothing is sent.
//
// Analytics is opt-in (#606): we start OPTED OUT. The Flutter layer calls
// Posthog().enable() (-> posthog.opt_in_capturing) only after the user consents.
// Events and pageviews are driven by the posthog_flutter plugin, so posthog-js's own
// autocapture / pageview capture are disabled here to avoid double counting.
(function () {
  if (!window.posthog || typeof window.posthog.init !== 'function') return;
  window.posthog.init('__POSTHOG_KEY__', {
    api_host: 'https://us.i.posthog.com',
    opt_out_capturing_by_default: true,
    capture_pageview: false,
    capture_pageleave: false,
    autocapture: false,
    disable_session_recording: true,
    // The /cloud/link handshake carries a secret `state` in its query, and the Cloud portal's
    // link page carries the pairing code (`?code=BCDF-GH23`). posthog-js stamps every event
    // with the page URL, so drop the query and fragment of any URL that is, or redirects to,
    // a link page. Same rule as the app's Sentry scrubber (lib/utils/link_url_scrubber.dart):
    //  - cloud/link or cloud%2Flink (the page, or the main app's detour to it);
    //  - /link? or %2Flink%3F (the portal's own sign-in and sign-up detours,
    //    /cloud/login?redirect=%2Flink%3Fcode%3D.. and /cloud/register?redirect=..);
    //  - a code= query parameter.
    // A link code in an API path (link-sessions/BCDF-GH23) is replaced with [code].
    sanitize_properties: function (properties) {
      var link = /cloud(\/|%2f)link|(\/|%2f)link(\?|%3f|#|%23)|(^|[?&]|%3f|%26)code(=|%3d)/i;
      var linkCode = /(link-sessions(?:\/|%2f))(?!poll(?:[\/?#]|$))([^\/?#]+)/gi;
      Object.keys(properties || {}).forEach(function (k) {
        var v = properties[k];
        if (typeof v !== 'string') return;
        v = v.replace(linkCode, '$1[code]');
        if (link.test(v)) v = v.split(/[?#]/)[0];
        properties[k] = v;
      });
      return properties;
    },
  });
})();
