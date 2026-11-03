#!/usr/bin/env bash
# Buoy Money dev slots: automated overflow sweep.
#
# Runs the renderer-proof device×page overflow audit (issue #539) as a live
# integration_test: boots the REAL app via `flutter drive`, authenticates against
# THIS slot's backend with a debug token, walks every protected route at every
# device width, and reports a device×page overflow table.
#
#   scripts/dev/overflow-sweep.sh                 # use this worktree's slot, report only
#   scripts/dev/overflow-sweep.sh --strict        # exit non-zero if any overflow (CI gate)
#   scripts/dev/overflow-sweep.sh --slot 2        # target a specific slot's API
#   scripts/dev/overflow-sweep.sh --user premium-user@buoy-ai.com
#
# Only needs the slot's BACKEND up (`dev-up.sh --api-only` is enough) - flutter
# drive serves its own app instance; it just talks to the slot API for data/auth.
#
# Needs chromedriver matching the installed Chrome: fetched into the main checkout's
# .dev-tools/chromedriver-<major>/ on first use (override with $CHROMEDRIVER).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

STRICT=false FORCE_SLOT="" SWEEP_USER="family-owner@buoy-ai.com"
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) STRICT=true; shift ;;
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --user) SWEEP_USER="$2"; shift 2 ;;
    --help) sed -n '2,18p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

ROOT="$(repo_root)"; [ -n "$ROOT" ] || { log_error "Not in a git repo"; exit 1; }
CHROMEDRIVER="${CHROMEDRIVER:-$(ensure_chromedriver || true)}"

# ── Resolve the slot whose API we test against ───────────────────────────────
if [ -n "$FORCE_SLOT" ]; then
  SLOT="$FORCE_SLOT"
elif [ -f "$ROOT/.dev/slot.env" ]; then
  SLOT="$(grep -E '^BUOY_SLOT=' "$ROOT/.dev/slot.env" | cut -d= -f2)"
else
  SLOT=0
fi
buoy_slot_env "$SLOT"   # exports BUOY_API_*
DRIVER_PORT="${CHROMEDRIVER_PORT:-$((4444 + SLOT))}"

if ! http_ok "$BUOY_API_HEALTH_URL"; then
  log_error "Slot $SLOT backend not healthy at $BUOY_API_HEALTH_URL"
  log_error "Bring it up first:  scripts/dev/dev-up.sh --api-only${FORCE_SLOT:+ --slot $SLOT}"
  exit 1
fi
log_info "Sweeping against slot $SLOT API → $BUOY_API_BASE_URL (user: $SWEEP_USER)"

[ -x "$CHROMEDRIVER" ] || { log_error "chromedriver not found at $CHROMEDRIVER"; \
  log_error "Set \$CHROMEDRIVER, or download the chromedriver that matches your Chrome (Chrome for Testing) once."; exit 1; }

# ── chromedriver (cleaned up on exit) ────────────────────────────────────────
DRIVER_WINPID=""
cleanup() {
  [ -n "$DRIVER_WINPID" ] && taskkill //PID "$DRIVER_WINPID" //T //F >/dev/null 2>&1 || true
}
trap cleanup EXIT

if port_in_use "$DRIVER_PORT"; then
  log_info "Reusing chromedriver already on :$DRIVER_PORT"
else
  log_info "Starting chromedriver on :$DRIVER_PORT…"
  "$CHROMEDRIVER" --port="$DRIVER_PORT" >"$ROOT/.dev/chromedriver.log" 2>&1 &
  for _ in $(seq 1 20); do port_in_use "$DRIVER_PORT" && break; sleep 0.5; done
  port_in_use "$DRIVER_PORT" || { log_error "chromedriver failed to start - see .dev/chromedriver.log"; exit 1; }
  DRIVER_WINPID="$(pid_on_port "$DRIVER_PORT")"
fi

# ── Run the sweep ────────────────────────────────────────────────────────────
log_info "flutter drive - building app + sweeping (first build is slow, ~1–2 min)…"
set +e
(
  cd "$ROOT/flutter"
  flutter drive \
    --driver=test_driver/integration_test.dart \
    --target=integration_test/overflow_sweep_test.dart \
    -d web-server --browser-name=chrome --driver-port="$DRIVER_PORT" \
    --dart-define=API_BASE_URL="$BUOY_API_BASE_URL" \
    --dart-define=SWEEP_USER="$SWEEP_USER" \
    --dart-define=OVERFLOW_STRICT="$STRICT" \
    --dart-define=COLLECT_ALL="${COLLECT_ALL:-false}"
)
DRIVE_RC=$?
set -e

# ── Report ───────────────────────────────────────────────────────────────────
REPORT="$ROOT/flutter/build/integration_response_data.json"
echo
if [ -f "$REPORT" ]; then
  log_info "Results ($REPORT):"
  # Pretty-print the table + count using python (always available here).
  python - "$REPORT" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
diag = d.get("sizeDiagnostic") or []
if diag:
    print("size diagnostic (requested width -> logical width app sees):")
    for line in diag:
        print("  " + line)
    print()
print(d.get("table", "(no table in report)"))
print(f"\noverflowCount = {d.get('overflowCount')}")
print(f"selfCheckPassed = {d.get('selfCheckPassed')}  (collector proven able to detect overflow)")
PY
else
  log_warn "No report file at $REPORT - the drive may have failed before reporting."
fi

if [ "$DRIVE_RC" -ne 0 ]; then
  log_error "flutter drive exited $DRIVE_RC (strict mode fails on overflow; otherwise a real error)."
  exit "$DRIVE_RC"
fi
log_success "Overflow sweep complete."
