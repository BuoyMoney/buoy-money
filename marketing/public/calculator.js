// Daily-allowance / count-up savings calculator. Static external script so it
// satisfies CSP script-src 'self' (no inline handlers).
(function () {
  var monthly = document.getElementById('monthly');
  var days = document.getElementById('days');
  var skips = document.getElementById('skips');
  var skipsOut = document.getElementById('skipsOut');
  var allowanceEl = document.getElementById('allowance');
  var bankedEl = document.getElementById('banked');
  var bankedSub = document.getElementById('bankedSub');
  var yearlyEl = document.getElementById('yearly');
  if (!monthly || !days || !skips) return;

  var money = function (n) {
    return '$' + n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  };
  var money0 = function (n) {
    return '$' + Math.round(n).toLocaleString('en-US');
  };

  function update() {
    // Cap the budget so extreme inputs can't overflow the layout (0 … $1B).
    var m = Math.min(1000000000, Math.max(0, parseFloat(monthly.value) || 0));
    // Days: clamp to [1, 365]. An empty field falls back to 30 (a sensible month);
    // 0 or negative values clamp to 1 rather than silently jumping to 30.
    var rawDays = parseInt(days.value, 10);
    var d = isNaN(rawDays) ? 30 : Math.min(365, Math.max(1, rawDays));
    var s = Math.min(7, Math.max(0, parseInt(skips.value, 10) || 0));

    var allowance = m / d;
    // spend-free days in the period earn the allowance without spending it
    var spendFreeDays = Math.round((d / 7) * s);
    var banked = spendFreeDays * allowance;

    skipsOut.textContent = String(s);
    allowanceEl.textContent = money(allowance);
    bankedEl.textContent = money0(banked);
    bankedSub.textContent =
      s === 0 ? 'add a spend-free day to start banking' : 'by skipping ' + s + (s === 1 ? ' day' : ' days') + ' a week';
    yearlyEl.textContent = money0(banked * 12);
  }

  ['input', 'change'].forEach(function (evt) {
    monthly.addEventListener(evt, update);
    days.addEventListener(evt, update);
    skips.addEventListener(evt, update);
  });
  update();
})();
