#!/usr/bin/env bash
# Buoy Money dev slots: tear down a slot.
#
#   scripts/dev/dev-down.sh            # this worktree's slot
#   scripts/dev/dev-down.sh --slot 2  # a specific slot
#   scripts/dev/dev-down.sh --prune   # also remove the slot's Qdrant volume
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

FORCE_SLOT="" PRUNE=false
while [ $# -gt 0 ]; do
  case "$1" in
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --prune) PRUNE=true; shift ;;
    --help) sed -n '2,7p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

DEV="$(dev_dir)"
SLOT="${FORCE_SLOT:-$(cat "$DEV/slot" 2>/dev/null || true)}"
[ -n "$SLOT" ] || { log_warn "No slot recorded for this worktree (nothing to do)."; exit 0; }
buoy_slot_env "$SLOT"

log_info "Tearing down slot $SLOT (API :$BUOY_API_PORT, Flutter :$BUOY_FLUTTER_PORT)…"

# Stop the backend (AppHost winpid + all AppHost/API ports), then Flutter.
stop_backend "$DEV"
[ -f "$DEV/flutter.winpid" ] && { kill_tree "$(cat "$DEV/flutter.winpid")"; rm -f "$DEV/flutter.winpid"; }
kill_tree "$(pid_on_port "$BUOY_FLUTTER_PORT")"
rm -f "$DEV/flutter.pid"
sleep 1

# Remove the slot's containers (Aspire orphans them on the forced kill).
log_info "Removing slot containers ($BUOY_PG_CONTAINER, $BUOY_QDRANT_CONTAINER)…"
remove_slot_containers

release_lock "$SLOT"
rm -f "$DEV/slot" "$DEV/slot.env"

if [ "$PRUNE" = true ]; then
  log_info "Removing Qdrant volume $BUOY_QDRANT_VOLUME…"
  docker volume rm "$BUOY_QDRANT_VOLUME" >/dev/null 2>&1 || log_warn "Volume not found / still in use."
fi

log_success "Slot $SLOT released."
