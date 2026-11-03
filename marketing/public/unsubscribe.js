// Opt-out handler for /unsubscribe. Static external script so it satisfies CSP
// script-src 'self' (no inline handlers).
//
// Fires automatically on load rather than behind a "confirm" button: the click that got them
// here was already the decision, and making someone click twice to stop hearing from you is
// the kind of friction that earns spam complaints instead of unsubscribes.
(function () {
  var root = document.getElementById('unsub');
  if (!root) return;

  var setState = function (s) {
    root.setAttribute('data-state', s);
  };

  var token;
  try {
    token = new URLSearchParams(window.location.search).get('t');
  } catch (e) {
    token = null;
  }

  if (!token) {
    setState('missing');
    return;
  }

  // A request that never settles would leave the page on "One moment…" forever, which reads
  // as a broken button. Fail visibly instead, so the error copy and support address show up.
  var timeout = null;
  var opts = {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ token: token }),
  };
  if (typeof AbortController === 'function') {
    var controller = new AbortController();
    opts.signal = controller.signal;
    timeout = setTimeout(function () {
      controller.abort();
    }, 10000);
  }

  fetch('/api/leads/unsubscribe', opts)
    .then(function (res) {
      if (timeout) clearTimeout(timeout);
      // The endpoint answers 200 for unknown tokens too, deliberately, so there is no
      // "was that token real" branch to handle here.
      setState(res.ok ? 'done' : 'error');
    })
    .catch(function () {
      if (timeout) clearTimeout(timeout);
      setState('error');
    });
})();
