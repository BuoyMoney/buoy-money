// Founding-member page behaviour. Static external script so it satisfies the production CSP
// (script-src 'self', no inline code). The API is same-origin, so fetch('/api/...') needs no
// CORS or extra connect-src.
//
// Jobs: show the live "X of 150 founding spots left" line and bar (/api/founding/status; it
// stays hidden if that call fails), swap the button for a "taken" note when the spots are gone,
// and turn the form into a Stripe Checkout redirect.
(function () {
  var form = document.getElementById('founding-form');
  if (!form) return;

  var openBlock = document.getElementById('founding-open');
  var soon = document.getElementById('founding-soon');
  var button = document.getElementById('founding-button');
  var errorEl = document.getElementById('founding-error');
  var fullEl = document.getElementById('founding-full');
  var choicesEl = document.getElementById('founding-choices');
  var spotEls = document.querySelectorAll('[data-spots]');

  function withTimeout(opts, ms) {
    if (typeof AbortController === 'function') {
      var controller = new AbortController();
      opts.signal = controller.signal;
      setTimeout(function () {
        controller.abort();
      }, ms);
    }
    return opts;
  }

  function showSoon() {
    if (openBlock) openBlock.hidden = true;
    if (soon) soon.hidden = false;
  }

  // Ask the API whether reservations are open. If the request fails we leave the button in
  // place: a failed check should not hide a working page, and the checkout call itself will
  // report a real problem.
  fetch('/api/reservations/stats', withTimeout({ method: 'GET' }, 10000))
    .then(function (res) {
      return res.ok ? res.json() : null;
    })
    .then(function (data) {
      if (!data) return;
      if (!data.enabled) {
        showSoon();
        return;
      }
    })
    .catch(function () {});

  function showFull() {
    if (openBlock) openBlock.hidden = true;
    if (choicesEl) choicesEl.hidden = true;
    if (soon) soon.hidden = true;
    if (fullEl) fullEl.hidden = false;
  }

  // Live spots left. Any failure or odd payload leaves the line hidden: never guess a number.
  function isCount(n) {
    return typeof n === 'number' && isFinite(n) && n >= 0;
  }
  function renderSpots(status) {
    var limit = status.limit;
    var remaining = Math.min(status.remaining, limit);
    var full = status.open === false || remaining === 0;
    var pct = Math.max(0, Math.min(100, ((limit - remaining) / limit) * 100));
    for (var i = 0; i < spotEls.length; i++) {
      var el = spotEls[i];
      if (full) {
        // The "all taken" note replaces the line and the bar; no second message.
        el.style.display = 'none';
        continue;
      }
      el.style.display = '';
      var num = el.querySelector('[data-spots-num]');
      var label = el.querySelector('[data-spots-label]');
      var fill = el.querySelector('[data-spots-fill]');
      if (num) num.textContent = String(remaining);
      if (label) {
        label.textContent = 'of ' + limit + (remaining === 1 ? ' founding spot left' : ' founding spots left');
      }
      if (fill) fill.style.width = pct + '%';
      el.hidden = false;
    }
    if (full) showFull();
  }

  // The hidden spots block reserves layout space (see .spots[hidden] in founding.astro). If the
  // status never arrives, collapse it so the form does not keep an invisible gap.
  function collapseSpots() {
    for (var i = 0; i < spotEls.length; i++) spotEls[i].style.display = 'none';
  }

  fetch('/api/founding/status', withTimeout({ method: 'GET' }, 10000))
    .then(function (res) {
      return res.ok ? res.json() : null;
    })
    .then(function (data) {
      if (!data || !isCount(data.limit) || data.limit === 0 || !isCount(data.remaining)) {
        collapseSpots();
        return;
      }
      renderSpots(data);
    })
    .catch(collapseSpots);

  form.addEventListener('submit', function (event) {
    event.preventDefault();
    if (errorEl) errorEl.hidden = true;

    var chosen = form.querySelector('input[name="choice"]:checked');
    var choice = chosen ? chosen.value : 'unsure';
    var source = 'founding_page';
    try {
      var ref = new URLSearchParams(window.location.search).get('ref');
      if (ref) source = ref.slice(0, 80);
    } catch (e) {}

    if (button) button.disabled = true;

    var opts = withTimeout(
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ choice: choice, source: source }),
      },
      15000
    );

    fetch('/api/reservations/checkout', opts)
      .then(function (res) {
        if (res.status === 404) {
          showSoon();
          return null;
        }
        if (res.status === 409) {
          return res
            .json()
            .catch(function () {
              return {};
            })
            .then(function (body) {
              if (body && body.code === 'founding_full') {
                renderSpots({ limit: body.limit || 150, remaining: 0, open: false });
              }
              var err = new Error('conflict');
              err.userMessage = body && body.message;
              throw err;
            });
        }
        if (!res.ok) throw new Error('checkout failed');
        return res.json();
      })
      .then(function (data) {
        if (!data) return;
        if (!data.url) throw new Error('no url');
        window.location.href = data.url;
      })
      .catch(function (err) {
        if (button) button.disabled = false;
        if (errorEl) {
          if (!errorEl.hasAttribute('data-default')) {
            errorEl.setAttribute('data-default', errorEl.textContent);
          }
          errorEl.textContent =
            err && err.userMessage ? err.userMessage : errorEl.getAttribute('data-default');
          errorEl.hidden = false;
        }
      });
  });
})();
