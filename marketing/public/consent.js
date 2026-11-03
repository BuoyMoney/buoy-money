// Analytics consent + PostHog bootstrap for the marketing site.
//
// The marketing site and the Flutter app share the buoy-ai.com origin (the app lives
// under /app/), so consent is stored in one first-party cookie both can read. That is
// what keeps a visitor from being asked twice: whichever surface they land on first
// shows a prompt, and the other reads the answer. The app half of this contract is
// flutter/lib/services/consent_store_web.dart — the cookie name and the exact
// 'granted' / 'denied' values must match there.
//
// Nothing is sent before consent. PostHog is not even downloaded until the visitor
// accepts, so declining costs them no request at all.

const COOKIE = 'buoy_consent';
const MAX_AGE = 31536000; // one year, matching the app side

function readConsent() {
  for (const part of document.cookie.split(';')) {
    const entry = part.trim();
    if (entry.startsWith(COOKIE + '=')) return entry.slice(COOKIE.length + 1);
  }
  return null;
}

function writeConsent(granted) {
  // Secure is omitted on plain http so local dev can still persist a choice; a Secure
  // cookie is silently dropped over http and the banner would return on every reload.
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie =
    COOKIE + '=' + (granted ? 'granted' : 'denied') +
    '; path=/; max-age=' + MAX_AGE + '; SameSite=Lax' + secure;
}

// Loads posthog-js from the CDN allowlisted in nginx-csp-*.conf. Resolves to false if
// the script fails (blocked, offline, ad blocker) so callers can carry on quietly —
// analytics must never break the page.
function loadPostHog() {
  if (window.posthog && typeof window.posthog.init === 'function') {
    return Promise.resolve(true);
  }
  return new Promise((resolve) => {
    const s = document.createElement('script');
    s.src = 'https://us-assets.i.posthog.com/static/array.full.js';
    s.async = true;
    s.onload = () => resolve(true);
    s.onerror = () => resolve(false);
    document.head.appendChild(s);
  });
}

let started = false;

async function startAnalytics(key) {
  // Reopening the banner and accepting again would otherwise re-run init on an
  // already-initialised instance.
  if (!key || started) return;
  started = true;
  const ok = await loadPostHog();
  if (!ok || !window.posthog) return;
  window.posthog.init(key, {
    api_host: 'https://us.i.posthog.com',
    capture_pageview: true,
    capture_pageleave: true,
    // The app deliberately disables autocapture to avoid double counting with the
    // posthog_flutter plugin. Here there is no plugin, and knowing which marketing
    // CTA gets clicked is the entire point, so it stays on.
    autocapture: true,
    disable_session_recording: true,
  });
}

function init() {
  const banner = document.getElementById('consent-banner');
  if (!banner) return;
  const key = banner.dataset.posthogKey || '';

  wireBanner(banner, key);
  wireReopen(banner);

  const decided = readConsent();
  if (decided === 'granted') {
    startAnalytics(key);
    return;
  }
  if (decided === 'denied') return;

  banner.hidden = false;
}

function wireBanner(banner, key) {
  banner.querySelector('[data-consent-accept]')?.addEventListener('click', () => {
    writeConsent(true);
    banner.hidden = true;
    startAnalytics(key);
  });

  banner.querySelector('[data-consent-decline]')?.addEventListener('click', () => {
    writeConsent(false);
    banner.hidden = true;
  });
}

// The footer link becomes a "reopen the banner" button. It is an <a> to the privacy
// policy in the markup so it still goes somewhere useful if this script never runs.
function wireReopen(banner) {
  for (const el of document.querySelectorAll('[data-consent-reopen]')) {
    el.addEventListener('click', (e) => {
      e.preventDefault();
      banner.hidden = false;
      banner.scrollIntoView({ block: 'nearest' });
    });
  }
}

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init);
} else {
  init();
}
