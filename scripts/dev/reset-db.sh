#!/usr/bin/env bash
# Buoy Money dev slots: reset a slot's database to the freshly-seeded baseline.
# The Postgres container is volume-less, so recycling the slot re-runs EF migrations
# and TestDataSeeder, giving deterministic data for repeatable e2e runs.
# Pass the same mode flags you booted with.
#
#   scripts/dev/reset-db.sh                 # this worktree's slot
#   scripts/dev/reset-db.sh --api-only
#   scripts/dev/reset-db.sh --slot 2 --api-only
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/lib.sh"

FORCE_SLOT=""
PASS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --slot) FORCE_SLOT="$2"; shift 2 ;;
    --api-only|--chrome) PASS+=("$1"); shift ;;
    --help) sed -n '2,11p' "$0"; exit 0 ;;
    *) log_error "Unknown option: $1"; exit 1 ;;
  esac
done

DEV="$(dev_dir)"
SLOT="${FORCE_SLOT:-$(cat "$DEV/slot" 2>/dev/null || true)}"
[ -n "$SLOT" ] || { log_error "No slot for this worktree - run dev-up.sh first."; exit 1; }

log_info "Resetting slot $SLOT to the seeded baseline (recycle)…"
"$SCRIPT_DIR/dev-down.sh" --slot "$SLOT"
"$SCRIPT_DIR/dev-up.sh" --slot "$SLOT" ${PASS[@]+"${PASS[@]}"}
log_success "Slot $SLOT reset - fresh migrations + seed data."
