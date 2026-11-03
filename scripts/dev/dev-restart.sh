#!/usr/bin/env bash
# Buoy Money dev slots: restart the BACKEND for this worktree's slot.
# Flutter hot-reloads on save; the .NET API does NOT - run this after editing *.cs.
#
#   scripts/dev/dev-restart.sh           # this worktree's slot
#   scripts/dev/dev-restart.sh --slot 2
#   scripts/dev/dev-restart.sh --rebuild
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

FORCE_SLOT="" REBUILD=false
while [ $# -gt 0 ]; do
  case "$1" in
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --rebuild) REBUILD=true; shift ;;
    --help) sed -n '2,9p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

ROOT="$(repo_root)"; DEV="$(dev_dir)"; LOGS="$DEV/logs"; mkdir -p "$LOGS"
SLOT="${FORCE_SLOT:-$(cat "$DEV/slot" 2>/dev/null || true)}"
[ -n "$SLOT" ] || { log_error "No slot for this worktree - run dev-up.sh first."; exit 1; }
buoy_slot_env "$SLOT"

log_info "Stopping backend for slot $SLOT…"
stop_backend "$DEV"
sleep 2

[ "$REBUILD" = true ] && { log_info "Building backend…"; (cd "$ROOT/backend" && dotnet build -c Debug --nologo -v quiet); }

log_info "Restarting Aspire AppHost (slot $SLOT)…"
start_backend_bg "$ROOT" "$DEV" "$LOGS"
if wait_for_http "$BUOY_API_HEALTH_URL" 60; then
  log_success "Backend healthy again on :$BUOY_API_PORT"
  pid_on_port "$BUOY_DASHBOARD_PORT" > "$DEV/apphost.winpid" 2>/dev/null || true
else
  log_error "Backend did not come back healthy. Last log lines:"; tail -25 "$LOGS/aspire.log" || true
  exit 1
fi
