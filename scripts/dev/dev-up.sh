#!/usr/bin/env bash
# Buoy Money dev slots: bring up a slot.
#
#   scripts/dev/dev-up.sh                 # claim lowest free slot, full stack
#   scripts/dev/dev-up.sh --slot 2        # force slot 2
#   scripts/dev/dev-up.sh --api-only      # API + DB only (no Flutter/Chrome)
#   scripts/dev/dev-up.sh --chrome        # open a real Chrome instead of web-server
#   scripts/dev/dev-up.sh --rebuild       # dotnet build before starting
#
# Each git worktree gets a sticky slot. Slots are 1..BUOY_MAX_SLOTS (default 5).
# See scripts/dev/README.md for the port map.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

FORCE_SLOT="" API_ONLY=false USE_CHROME=false REBUILD=false
while [ $# -gt 0 ]; do
  case "$1" in
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --api-only) API_ONLY=true; shift ;;
    --chrome) USE_CHROME=true; shift ;;
    --rebuild) REBUILD=true; shift ;;
    --help) sed -n '2,14p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

ROOT="$(repo_root)"; [ -n "$ROOT" ] || { log_error "Not in a git repo"; exit 1; }
DEV="$(dev_dir)"; LOGS="$DEV/logs"; mkdir -p "$LOGS"

SLOT="$(choose_slot "$FORCE_SLOT")" || exit 1
write_lock "$SLOT"
write_slot_env "$SLOT"
buoy_slot_env "$SLOT"   # exports BUOY_* into this shell

log_info "Slot $SLOT  →  API :$BUOY_API_PORT  Flutter :$BUOY_FLUTTER_PORT  Dashboard :$BUOY_DASHBOARD_PORT  DB $BUOY_DB_NAME"

if port_in_use "$BUOY_API_PORT" && http_ok "$BUOY_API_HEALTH_URL"; then
  log_success "API for slot $SLOT already healthy - skipping backend start"
else
  ensure_dev_appsettings "$ROOT"   # a fresh worktree has no dev config; scaffold it so boot doesn't crash (#644)
  [ "$REBUILD" = true ] && { log_info "Building backend…"; (cd "$ROOT/backend" && dotnet build -c Debug --nologo -v quiet); }
  remove_slot_containers   # clear any stale orphans so Aspire can reclaim the fixed names
  log_info "Starting Aspire AppHost (slot $SLOT) in background…"
  start_backend_bg "$ROOT" "$DEV" "$LOGS"
  log_info "Waiting for API health at $BUOY_API_HEALTH_URL (Aspire boot + migrations, ~30s)…"
  if wait_for_http "$BUOY_API_HEALTH_URL" 60; then
    log_success "API healthy on :$BUOY_API_PORT"
  else
    log_error "API did not become healthy in time. Last log lines:"; tail -25 "$LOGS/aspire.log" || true
    print_resource_error   # the aspire.log only shows the proxy; the real fatal is in the resource log (#644)
    exit 1
  fi
fi

# Record the AppHost's true Windows PID (from the dashboard port) - the $! pidfiles are
# MSYS pids that taskkill can't reliably use. This is what dev-down kills.
pid_on_port "$BUOY_DASHBOARD_PORT" > "$DEV/apphost.winpid" 2>/dev/null || true

if [ "$API_ONLY" = false ]; then
  DEVICE="web-server"; [ "$USE_CHROME" = true ] && DEVICE="chrome"
  log_info "Starting Flutter ($DEVICE) on :$BUOY_FLUTTER_PORT → API $BUOY_API_BASE_URL…"
  (
    cd "$ROOT/flutter"
    nohup flutter run -d "$DEVICE" --web-port "$BUOY_FLUTTER_PORT" \
      --dart-define=API_BASE_URL="$BUOY_API_BASE_URL" > "$LOGS/flutter.log" 2>&1 &
    echo $! > "$DEV/flutter.pid"
  )
  if wait_for_http "$BUOY_FLUTTER_URL" 90; then
    log_success "Flutter serving on $BUOY_FLUTTER_URL"
  else
    log_warn "Flutter not reachable yet - check $LOGS/flutter.log (first build can be slow)"
  fi
  pid_on_port "$BUOY_FLUTTER_PORT" > "$DEV/flutter.winpid" 2>/dev/null || true
fi

# Flutter VM service URI (for Dart / Marionette MCP) is printed in flutter.log.
VM_URI="$(grep -oE 'http://127\.0\.0\.1:[0-9]+/[A-Za-z0-9_=-]+/?' "$LOGS/flutter.log" 2>/dev/null | head -1 || true)"

cat <<CARD

  ┌─ slot $SLOT ────────────────────────────────────────────
  │ API        $BUOY_API_BASE_URL
  │ Health     $BUOY_API_HEALTH_URL
  │ Flutter    $BUOY_FLUTTER_URL
  │ Dashboard  $BUOY_DASHBOARD_URL
  │ Database   $BUOY_DB_NAME
  │ VM service ${VM_URI:-"(see $LOGS/flutter.log once built)"}
  │ Env file   $DEV/slot.env
  └─────────────────────────────────────────────────────────
  logs:  tail -f $LOGS/aspire.log   |   tail -f $LOGS/flutter.log
  down:  scripts/dev/dev-down.sh
CARD
