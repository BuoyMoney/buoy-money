#!/usr/bin/env bash
# Buoy Money dev slots: slot env derivation.
# Single source of truth for per-slot ports/names. Mirrors the formulas in
# backend/src/BuoyAI.AppHost/Program.cs - keep the two in sync.
#
# Usage:
#   source scripts/dev/slot-env.sh && buoy_slot_env <slot>   # exports BUOY_* vars
#   scripts/dev/slot-env.sh <slot>                            # prints derived env
#
# Slot 0 / empty / non-numeric => legacy defaults (API 5000, db "BuoyAI") so the
# existing start-flutter-dev.sh keeps working unchanged.

buoy_slot_env() {
  local slot="${1:-0}"
  if ! [[ "$slot" =~ ^[0-9]+$ ]] || [ "$slot" -lt 1 ]; then
    slot=0
  fi

  export BUOY_SLOT="$slot"
  if [ "$slot" -eq 0 ]; then
    export BUOY_API_PORT=5000
    export BUOY_FLUTTER_PORT=3000
    export BUOY_DASHBOARD_PORT=17111
    export BUOY_OTLP_PORT=17112
    export BUOY_RESOURCE_PORT=17113
    export BUOY_DB_NAME="BuoyAI"
    export BUOY_QDRANT_VOLUME="buoy-qdrant"
  else
    export BUOY_API_PORT=$((5000 + slot * 100))
    export BUOY_FLUTTER_PORT=$((3000 + slot * 100))
    export BUOY_DASHBOARD_PORT=$((18000 + slot * 10))
    export BUOY_OTLP_PORT=$((18000 + slot * 10 + 1))
    export BUOY_RESOURCE_PORT=$((18000 + slot * 10 + 2))
    export BUOY_DB_NAME="BuoyAI_s${slot}"
    export BUOY_QDRANT_VOLUME="buoy-qdrant-s${slot}"
  fi

  # Deterministic container names - mirror BuoyAI.AppHost/Program.cs.
  if [ "$slot" -eq 0 ]; then
    export BUOY_PG_CONTAINER="buoy-pg"
    export BUOY_QDRANT_CONTAINER="buoy-qdrant-srv"
  else
    export BUOY_PG_CONTAINER="buoy-pg-s${slot}"
    export BUOY_QDRANT_CONTAINER="buoy-qdrant-s${slot}"
  fi

  export BUOY_API_BASE_URL="http://localhost:${BUOY_API_PORT}/api"
  export BUOY_API_HEALTH_URL="http://localhost:${BUOY_API_PORT}/health"
  export BUOY_API_SWAGGER_URL="http://localhost:${BUOY_API_PORT}/swagger/index.html"
  export BUOY_FLUTTER_URL="http://localhost:${BUOY_FLUTTER_PORT}"
  # Legacy keeps the https dashboard; slots run it unsecured-http for clean automation.
  if [ "$slot" -eq 0 ]; then
    export BUOY_DASHBOARD_URL="https://localhost:${BUOY_DASHBOARD_PORT}"
  else
    export BUOY_DASHBOARD_URL="http://localhost:${BUOY_DASHBOARD_PORT}"
  fi
}

# When executed directly (not sourced), print the derived env for inspection.
if [ "${BASH_SOURCE[0]}" = "${0}" ]; then
  buoy_slot_env "${1:-0}"
  cat <<EOF
BUOY_SLOT=$BUOY_SLOT
BUOY_API_PORT=$BUOY_API_PORT
BUOY_FLUTTER_PORT=$BUOY_FLUTTER_PORT
BUOY_DASHBOARD_PORT=$BUOY_DASHBOARD_PORT
BUOY_OTLP_PORT=$BUOY_OTLP_PORT
BUOY_RESOURCE_PORT=$BUOY_RESOURCE_PORT
BUOY_DB_NAME=$BUOY_DB_NAME
BUOY_QDRANT_VOLUME=$BUOY_QDRANT_VOLUME
BUOY_API_BASE_URL=$BUOY_API_BASE_URL
BUOY_API_HEALTH_URL=$BUOY_API_HEALTH_URL
BUOY_API_SWAGGER_URL=$BUOY_API_SWAGGER_URL
BUOY_FLUTTER_URL=$BUOY_FLUTTER_URL
BUOY_DASHBOARD_URL=$BUOY_DASHBOARD_URL
EOF
fi
