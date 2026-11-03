#!/usr/bin/env bash
# Buoy Money dev slots: scripted UI smoke.
#
# Drives the REAL app through a login → dashboard → navigate flow via `flutter
# drive` against THIS slot's backend. Exercises the renderer-proof login path
# (real widget-tree text entry) that the API e2e and overflow sweep can't.
#
#   scripts/dev/ui-smoke.sh                       # this worktree's slot
#   scripts/dev/ui-smoke.sh --slot 2
#   scripts/dev/ui-smoke.sh --email premium-user@buoy-ai.com --password 'BuoyDevLocal2025!'
#
# Needs the slot BACKEND up (`dev-up.sh --api-only`). The chromedriver matching the installed
# Chrome is fetched into the main checkout's .dev-tools/ on first use (override with
# $CHROMEDRIVER).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

FORCE_SLOT="" EMAIL="family-owner@buoy-ai.com" PASSWORD="${BUOYAI_TEST_PASSWORD:-BuoyDevLocal2025!}"
while [ $# -gt 0 ]; do
  case "$1" in
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --email) EMAIL="$2"; shift 2 ;;
    --password) PASSWORD="$2"; shift 2 ;;
    --help) sed -n '2,16p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

ROOT="$(repo_root)"; [ -n "$ROOT" ] || { log_error "Not in a git repo"; exit 1; }
CHROMEDRIVER="${CHROMEDRIVER:-$(ensure_chromedriver || true)}"

if [ -n "$FORCE_SLOT" ]; then SLOT="$FORCE_SLOT"
elif [ -f "$ROOT/.dev/slot.env" ]; then SLOT="$(grep -E '^BUOY_SLOT=' "$ROOT/.dev/slot.env" | cut -d= -f2)"
else SLOT=0; fi
buoy_slot_env "$SLOT"
DRIVER_PORT="${CHROMEDRIVER_PORT:-$((4444 + SLOT))}"

if ! http_ok "$BUOY_API_HEALTH_URL"; then
  log_error "Slot $SLOT backend not healthy at $BUOY_API_HEALTH_URL"
  log_error "Bring it up first:  scripts/dev/dev-up.sh --api-only${FORCE_SLOT:+ --slot $SLOT}"
  exit 1
fi
[ -x "$CHROMEDRIVER" ] || { log_error "chromedriver not found at $CHROMEDRIVER (see TESTING.md)"; exit 1; }
log_info "UI smoke against slot $SLOT API → $BUOY_API_BASE_URL (login: $EMAIL)"

DRIVER_WINPID=""
cleanup() { [ -n "$DRIVER_WINPID" ] && taskkill //PID "$DRIVER_WINPID" //T //F >/dev/null 2>&1 || true; }
trap cleanup EXIT

if port_in_use "$DRIVER_PORT"; then
  log_info "Reusing chromedriver already on :$DRIVER_PORT"
else
  log_info "Starting chromedriver on :$DRIVER_PORT…"
  "$CHROMEDRIVER" --port="$DRIVER_PORT" >"$ROOT/.dev/chromedriver.log" 2>&1 &
  for _ in $(seq 1 20); do port_in_use "$DRIVER_PORT" && break; sleep 0.5; done
  port_in_use "$DRIVER_PORT" || { log_error "chromedriver failed to start"; exit 1; }
  DRIVER_WINPID="$(pid_on_port "$DRIVER_PORT")"
fi

log_info "flutter drive - building app + running login flow (first build ~1–2 min)…"
( cd "$ROOT/flutter"
  flutter drive \
    --driver=test_driver/integration_test.dart \
    --target=integration_test/login_smoke_test.dart \
    -d web-server --browser-name=chrome --driver-port="$DRIVER_PORT" \
    --dart-define=API_BASE_URL="$BUOY_API_BASE_URL" \
    --dart-define=LOGIN_EMAIL="$EMAIL" \
    --dart-define=LOGIN_PASSWORD="$PASSWORD"
)
