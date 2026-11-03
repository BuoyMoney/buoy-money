#!/usr/bin/env bash
# Buoy Money dev slots: show all slots and what's running.
#   scripts/dev/dev-status.sh
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

printf '%-5s %-7s %-9s %-9s %s\n' "SLOT" "API" "FLUTTER" "STATE" "OWNER"
printf '%-5s %-7s %-9s %-9s %s\n' "----" "----" "-------" "-----" "-----"
for s in $(seq 1 "$MAX_SLOTS"); do
  buoy_slot_env "$s"
  api="down"; port_in_use "$BUOY_API_PORT" && api="up"
  flutter="down"; port_in_use "$BUOY_FLUTTER_PORT" && flutter="up"
  state="$(slot_state "$s")"
  owner="$(_lock_owner "$s")"; owner="${owner:+$(basename "$owner")}"
  printf '%-5s %-7s %-9s %-9s %s\n' "$s" ":$BUOY_API_PORT/$api" ":$BUOY_FLUTTER_PORT/$flutter" "$state" "${owner:--}"
done
