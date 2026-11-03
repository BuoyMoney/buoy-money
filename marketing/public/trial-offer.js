// Trial-offer email capture. Static external script so it satisfies CSP
// script-src 'self' (no inline handlers).
//
// The whole point of this file is restraint. An email popup is only worth having if it
// asks at most twice and then never again, so the rules are:
//
//   * Two prompts per visitor, ever, until the suppression window lapses:
//       1. shortly after arrival (or once they've read a bit), and
//       2. one "are you sure" as they leave, only if they dismissed the first.
//   * Both are once-per-SESSION, so browsing five pages does not mean five popups.
//   * Dismissing once suppresses for 30 days. Dismissing twice suppresses for a year.
//     That is the "respect their decision" rule: a second no means stop asking.
//   * Converting suppresses forever.
//   * Never on legal/utility pages, never when a ?nopopup=1 escape hatch is present.
//   * On the calculator we skip the arrival prompt entirely (it interrupts a tool the
//     visitor is actively using) and only offer on exit, where we can trade something
//     concrete: their numbers.
(function () {
  var root = document.getElementById('trial-offer');
  if (!root) return;

  // ---------------------------------------------------------------- config

  var STORE_KEY = 'buoy.trialOffer.v1';
  var SESSION_KEY = 'buoy.trialOffer.session.v1';
  var DAY = 86400000;

  // How long a dismissal suppresses the popup, by how many times they've said no.
  // Index 0 is unused (zero dismissals never suppresses).
  var SUPPRESS_MS = [0, 30 * DAY, 365 * DAY];

  var ARRIVAL_DELAY_MS = 18000; // "shortly after arrival", not instantly
  var ARRIVAL_SCROLL_PCT = 0.4; // ...or once they've read ~40% of the page

  // Paths that never get a popup: legal pages people arrive at with a specific
  // question, and the delete-account flow where an upsell would be tasteless.
  var BLOCKED_PATHS = [
    '/privacy-policy',
    '/terms',
    '/security',
    '/delete-account',
    '/404',
  ];

  var CALCULATOR_PATH = '/calculator';

  // ---------------------------------------------------------------- storage
  // Private mode and disabled storage must degrade to "don't show", never to a crash
  // or to a popup on every page.


  // Analytics. PostHog only exists once the visitor accepted the consent banner
  // (see consent.js), so every call is guarded: capture must never be the reason
  // the popup breaks, and a declined visitor simply produces no events.
  function track(event, props) {
    try {
      if (window.posthog && typeof window.posthog.capture === 'function') {
        window.posthog.capture(event, props || {});
      }
    } catch (e) {
      /* analytics must never break the offer */
    }
  }

  function readJson(storage, key) {
    try {
      var raw = storage.getItem(key);
      return raw ? JSON.parse(raw) : null;
    } catch (e) {
      return null;
    }
  }

  function writeJson(storage, key, value) {
    try {
      storage.setItem(key, JSON.stringify(value));
    } catch (e) {
      /* quota or disabled: the popup just loses its memory this session */
    }
  }

  function getState() {
    var s = readJson(window.localStorage, STORE_KEY);
    if (!s || typeof s !== 'object') return { status: 'none', dismissals: 0, at: 0 };
    return {
      status: s.status === 'converted' || s.status === 'dismissed' ? s.status : 'none',
      dismissals: typeof s.dismissals === 'number' ? s.dismissals : 0,
      at: typeof s.at === 'number' ? s.at : 0,
    };
  }

  function setState(next) {
    writeJson(window.localStorage, STORE_KEY, next);
  }

  function getSession() {
    var s = readJson(window.sessionStorage, SESSION_KEY);
    if (!s || typeof s !== 'object') return { arrival: false, exit: false, dismissed: false };
    return {
      arrival: !!s.arrival,
      exit: !!s.exit,
      dismissed: !!s.dismissed,
    };
  }

  function setSession(next) {
    writeJson(window.sessionStorage, SESSION_KEY, next);
  }

  // ---------------------------------------------------------------- eligibility

  function isSuppressed() {
    var state = getState();
    if (state.status === 'converted') return true;
    if (state.status !== 'dismissed') return false;

    var window_ = SUPPRESS_MS[Math.min(state.dismissals, SUPPRESS_MS.length - 1)];
    if (!window_) return false;
    return Date.now() - state.at < window_;
  }

  function pathBlocked() {
    var path = window.location.pathname.replace(/\/+$/, '') || '/';
    for (var i = 0; i < BLOCKED_PATHS.length; i++) {
      if (path === BLOCKED_PATHS[i] || path.indexOf(BLOCKED_PATHS[i] + '/') === 0) return true;
    }
    // The app itself is served under /app/: never prompt a signed-in surface.
    return path.indexOf('/app') === 0;
  }

  function escapeHatch() {
    try {
      return new URLSearchParams(window.location.search).has('nopopup');
    } catch (e) {
      return false;
    }
  }

  function onCalculator() {
    return window.location.pathname.replace(/\/+$/, '') === CALCULATOR_PATH;
  }

  // Exit intent needs a real cursor. Touch devices get the arrival prompt only,
  // because every mobile "exit intent" heuristic is a guess that fires on scroll.
  function hasPointer() {
    return window.matchMedia && window.matchMedia('(hover: hover) and (pointer: fine)').matches;
  }

  if (escapeHatch() || pathBlocked() || isSuppressed()) return;

  // ---------------------------------------------------------------- elements

  var card = root.querySelector('.offer-card');
  var formPane = root.querySelector('[data-offer-form]');
  var successPane = root.querySelector('[data-offer-success]');
  var form = root.querySelector('[data-offer-submit]');
  var input = root.querySelector('#offer-email');
  var cta = root.querySelector('[data-offer-cta]');
  var errorEl = root.querySelector('[data-offer-error]');
  var registerLink = root.querySelector('[data-offer-register]');

  var VARIANTS = ['is-primary', 'is-second', 'is-calc'];
  var currentVariant = null;
  var lastFocused = null;

  // ---------------------------------------------------------------- calculator

  // Reads whatever the visitor currently has on screen. Every field is optional: a
  // markup change on the calculator must degrade to "no snapshot", never block capture.
  function readCalculator() {
    if (!onCalculator()) return null;
    var num = function (id) {
      var el = document.getElementById(id);
      if (!el) return null;
      var raw = (el.value !== undefined ? el.value : el.textContent) || '';
      var parsed = parseFloat(String(raw).replace(/[^0-9.-]/g, ''));
      return isNaN(parsed) ? null : parsed;
    };
    var monthly = num('monthly');
    if (monthly === null) return null;
    return {
      monthlyBudget: monthly,
      days: num('days') || 30,
      skipsPerWeek: num('skips') || 0,
      dailyAllowance: num('allowance') || 0,
      banked: num('banked') || 0,
      yearly: num('yearly') || 0,
    };
  }

  // ---------------------------------------------------------------- open/close

  function open(variant, source) {
    if (root.hidden === false) return;

    currentVariant = variant;
    root.setAttribute('data-source', source);
    for (var i = 0; i < VARIANTS.length; i++) root.classList.remove(VARIANTS[i]);
    root.classList.add(variant);

    lastFocused = document.activeElement;
    root.hidden = false;
    document.documentElement.style.overflow = 'hidden';

    track('trial_offer_shown', { variant: variant, source: source });

    // Focus the field, not the close button, so a keyboard user can just type.
    if (input) {
      try {
        input.focus({ preventScroll: true });
      } catch (e) {
        input.focus();
      }
    }
  }

  function close(reason) {
    if (root.hidden) return;
    root.hidden = true;
    document.documentElement.style.overflow = '';

    // reason distinguishes an explicit "no thanks" from an incidental close
    // (escape key, backdrop), which are very different signals about the offer.
    track('trial_offer_dismissed', {
      reason: reason || 'unknown',
      variant: currentVariant,
      source: root.getAttribute('data-source') || '',
    });

    if (lastFocused && lastFocused.focus) {
      try {
        lastFocused.focus({ preventScroll: true });
      } catch (e) {
        /* the element may be gone */
      }
    }

    if (reason === 'dismiss') {
      var state = getState();
      setState({
        status: 'dismissed',
        dismissals: state.dismissals + 1,
        at: Date.now(),
      });
      var session = getSession();
      session.dismissed = true;
      setSession(session);
    }
  }

  function markConverted() {
    setState({ status: 'converted', dismissals: getState().dismissals, at: Date.now() });
  }

  // ---------------------------------------------------------------- triggers

  var armed = { arrival: false, exit: false };

  // NOTE: neither of these re-checks isSuppressed(). Suppression is evaluated once, at
  // load, and gates whether this page gets any prompt at all. Re-checking here would be
  // wrong: dismissing the arrival prompt sets the 30-day window immediately, which would
  // swallow the very "are you sure" follow-up that dismissal is supposed to trigger.
  // Within one page load the session flags govern; the next page load re-reads the
  // window and stays quiet, which is what stops a browsing visitor seeing this again.
  function showArrival() {
    if (armed.arrival) return;
    armed.arrival = true;

    var session = getSession();
    if (session.arrival) return;
    session.arrival = true;
    setSession(session);

    open('is-primary', 'popup_timed');
  }

  function showExit() {
    if (armed.exit) return;

    var session = getSession();
    if (session.exit) return;

    armed.exit = true;
    session.exit = true;
    setSession(session);

    // "Are you sure" only makes sense as a reply to a no. If they never saw the first
    // ask, this is still the first ask and gets the normal pitch.
    var variant = onCalculator() ? 'is-calc' : session.dismissed ? 'is-second' : 'is-primary';
    var source = onCalculator()
      ? 'calculator_exit'
      : session.dismissed
        ? 'popup_exit'
        : 'popup_timed';

    open(variant, source);
  }

  // Arrival prompt: whichever comes first, a dwell timer or reading ~40% of the page.
  // Skipped on the calculator, where interrupting the tool is worse than waiting for
  // the exit prompt that can offer to save their numbers.
  if (!onCalculator()) {
    window.setTimeout(showArrival, ARRIVAL_DELAY_MS);

    var onScroll = function () {
      var doc = document.documentElement;
      var scrollable = doc.scrollHeight - window.innerHeight;
      if (scrollable > 400 && window.scrollY / scrollable >= ARRIVAL_SCROLL_PCT) {
        window.removeEventListener('scroll', onScroll);
        showArrival();
      }
    };
    window.addEventListener('scroll', onScroll, { passive: true });
  }

  // Exit intent: cursor leaves through the top of the viewport with no related target
  // (which distinguishes "left the window" from "moved onto a child element").
  if (hasPointer()) {
    document.addEventListener('mouseout', function (e) {
      if (e.clientY > 0 || e.relatedTarget) return;
      // Don't ambush someone the instant the page loads.
      if (performance.now() < 4000) return;
      showExit();
    });
  }

  // ---------------------------------------------------------------- wiring

  var closers = root.querySelectorAll('[data-offer-close]');
  for (var c = 0; c < closers.length; c++) {
    closers[c].addEventListener('click', function () {
      close('dismiss');
    });
  }

  document.addEventListener('keydown', function (e) {
    if (root.hidden) return;
    if (e.key === 'Escape') {
      close('dismiss');
      return;
    }
    // Minimal focus trap: keep Tab inside the dialog while it's open.
    if (e.key !== 'Tab' || !card) return;
    var focusable = card.querySelectorAll(
      'button:not([disabled]), input:not([disabled]), a[href]'
    );
    var visible = [];
    for (var i = 0; i < focusable.length; i++) {
      if (focusable[i].offsetParent !== null) visible.push(focusable[i]);
    }
    if (!visible.length) return;
    var first = visible[0];
    var last = visible[visible.length - 1];
    if (e.shiftKey && document.activeElement === first) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  });

  function showError(message) {
    if (!errorEl) return;
    errorEl.textContent = message;
    errorEl.hidden = false;
  }

  if (form) {
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      if (errorEl) errorEl.hidden = true;

      var email = (input && input.value ? input.value : '').trim();
      // Deliberately permissive: the server validates properly, and an over-strict
      // client regex rejecting a valid address is a lost lead.
      if (!email || email.indexOf('@') < 1 || email.indexOf('.', email.indexOf('@')) < 0) {
        showError('Please enter a valid email address.');
        if (input) input.focus();
        return;
      }

      if (cta) {
        cta.disabled = true;
        cta.textContent = 'Reserving…';
      }

      track('trial_offer_submitted', {
        variant: currentVariant,
        source: root.getAttribute('data-source') || '',
      });

      fetch('/api/leads/extended-trial', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          email: email,
          source: root.getAttribute('data-source') || 'popup_timed',
          calculator: readCalculator(),
        }),
      })
        .then(function (res) {
          if (!res.ok) throw new Error('http ' + res.status);
          return res.json().catch(function () {
            return null;
          });
        })
        .then(function (data) {
          markConverted();

          // Distinct from `submitted`: this one only fires once the server has the
          // lead, so the gap between the two is failed captures rather than churn.
          track('trial_offer_captured', {
            variant: currentVariant,
            source: root.getAttribute('data-source') || '',
            trial_days: (data && data.trialDays) || null,
          });

          if (formPane) formPane.hidden = true;
          if (successPane) {
            successPane.hidden = false;
            if (registerLink) registerLink.focus();
          }
        })
        .catch(function () {
          track('trial_offer_failed', {
            variant: currentVariant,
            source: root.getAttribute('data-source') || '',
          });
          if (cta) {
            cta.disabled = false;
            cta.textContent = 'Reserve my free month';
          }
          showError('Something went wrong. Please try again in a moment.');
        });
    });
  }
})();
