#!/usr/bin/env bash
# Buoy Money dev slots: shared helpers for the slot manager.
# Sourced by dev-up.sh / dev-down.sh / dev-restart.sh / dev-status.sh.
# Targets Git Bash on Windows (netstat / taskkill), with Docker Desktop for Aspire.

# shellcheck disable=SC2155
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./slot-env.sh
source "$SCRIPT_DIR/slot-env.sh"

MAX_SLOTS="${BUOY_MAX_SLOTS:-5}"
SLOTS_DIR="${BUOY_SLOTS_DIR:-$HOME/.buoyai/slots}"

# ── logging ─────────────────────────────────────────────────────────────────
_c() { printf '\033[%sm%s\033[0m' "$1" "$2"; }
log_info()    { echo "$(_c '0;34' '[info]')   $*"; }
log_success() { echo "$(_c '0;32' '[ok]')     $*"; }
log_warn()    { echo "$(_c '1;33' '[warn]')   $*"; }
log_error()   { echo "$(_c '0;31' '[error]')  $*" >&2; }

# ── repo / worktree paths ───────────────────────────────────────────────────
# Worktree root (each git worktree is one agent = one sticky slot).
repo_root() { git rev-parse --show-toplevel 2>/dev/null; }
dev_dir()   { echo "$(repo_root)/.dev"; }

# ── chromedriver (shared by every worktree, kept matched to Chrome) ─────────
# The driver lives in the MAIN checkout's .dev-tools (gitignored), so a fresh worktree or an
# overnight job finds it too. One folder per Chrome major version; when Chrome updates, the
# matching driver is fetched once from Google's Chrome for Testing downloads.
shared_tools_dir() {
  local common; common="$(git rev-parse --path-format=absolute --git-common-dir 2>/dev/null)" || return 1
  echo "$(dirname "$common")/.dev-tools"
}

# Version of the chrome.exe that flutter drive will launch (not a staged update).
chrome_version() {
  local exe='C:\Program Files\Google\Chrome\Application\chrome.exe'
  powershell.exe -NoProfile -Command "(Get-Item '$exe').VersionInfo.ProductVersion" 2>/dev/null | tr -d '[:space:]'
}

# Prints the path of a chromedriver for the installed Chrome, downloading it if needed.
ensure_chromedriver() {
  local dir major ver exe lock legacy
  dir="$(shared_tools_dir)" || return 1
  major="$(chrome_version | cut -d. -f1)"
  legacy="$dir/chromedriver-win64/chromedriver.exe"
  if [ -z "$major" ]; then  # can't read Chrome's version: use whatever driver is there
    [ -x "$legacy" ] && echo "$legacy"; return 0
  fi
  if [ -x "$legacy" ] && [ "$("$legacy" --version 2>/dev/null | awk '{print $2}' | cut -d. -f1)" = "$major" ]; then
    echo "$legacy"; return 0
  fi
  exe="$dir/chromedriver-$major/chromedriver-win64/chromedriver.exe"
  if [ ! -x "$exe" ]; then
    mkdir -p "$dir"; lock="$dir/.chromedriver-$major.lock"
    local got=""
    for _ in $(seq 1 120); do  # one download at a time; stop waiting once another one finished
      mkdir "$lock" 2>/dev/null && { got=1; break; }
      [ ! -d "$lock" ] && [ -x "$exe" ] && break  # the owner finished (lock gone), not mid-extract
      sleep 1
    done
    if [ ! -x "$exe" ]; then
      if [ -z "$got" ]; then
        echo "chromedriver $major: another download still holds $lock after 2 minutes; remove it if nothing is downloading" >&2
        return 1
      fi
      ver="$(curl -fsS "https://googlechromelabs.github.io/chrome-for-testing/LATEST_RELEASE_$major")" || { rmdir "$lock"; return 1; }
      log_info "Chrome $major: fetching chromedriver $ver into $dir" >&2
      curl -fsSL -o "$dir/chromedriver-$major.zip"         "https://storage.googleapis.com/chrome-for-testing-public/$ver/win64/chromedriver-win64.zip"         && unzip -q -o "$dir/chromedriver-$major.zip" -d "$dir/chromedriver-$major"         && rm -f "$dir/chromedriver-$major.zip"
    fi
    [ -n "$got" ] && rmdir "$lock" 2>/dev/null
  fi
  [ -x "$exe" ] && echo "$exe"
}

# ── port + process helpers (Windows) ────────────────────────────────────────
# True if a TCP port is in LISTENING state (Windows netstat).
port_in_use() {
  local port="$1"
  netstat -ano 2>/dev/null | grep -iE "LISTENING" | grep -qE "[:.]${port}[[:space:]]"
}

# PID of the process LISTENING on a TCP port (empty if none).
pid_on_port() {
  netstat -ano 2>/dev/null | grep -iE "LISTENING" | grep -E "[:.]${1}[[:space:]]" \
    | awk '{print $NF}' | head -1
}

# True if an HTTP GET returns a 2xx/3xx within the timeout.
http_ok() {
  local url="$1" timeout="${2:-3}"
  local code
  code="$(curl -s -k -o /dev/null -m "$timeout" -w '%{http_code}' "$url" 2>/dev/null)"
  [[ "$code" =~ ^(2|3)[0-9][0-9]$ ]]
}

# Poll an HTTP URL until it's healthy or the deadline passes.
wait_for_http() {
  local url="$1" max="${2:-60}" i=1
  while [ "$i" -le "$max" ]; do
    if http_ok "$url" 3; then return 0; fi
    sleep 2; i=$((i + 1))
  done
  return 1
}

# Kill a process tree by PID (Windows). Aspire/Flutter spawn children.
kill_tree() {
  local pid="$1"
  [ -n "$pid" ] || return 0
  taskkill //PID "$pid" //T //F >/dev/null 2>&1 || true
}

# ── slot lifecycle ──────────────────────────────────────────────────────────
_lock_file() { echo "$SLOTS_DIR/$1.lock"; }

# Print the worktree path recorded in a slot's lock (empty if no lock). Always succeeds.
_lock_owner() {
  local f; f="$(_lock_file "$1")"
  [ -f "$f" ] || return 0
  sed -n 's/^worktree=//p' "$f" | head -1
}

# State of a slot relative to THIS worktree:
#   free  - no lock
#   mine  - lock owned by this worktree
#   stale - lock owned by another worktree but its API port is dead (reclaimable)
#   taken - lock owned by another worktree and its API port is live
slot_state() {
  local slot="$1"
  local f; f="$(_lock_file "$slot")"
  [ -f "$f" ] || { echo free; return; }
  local owner; owner="$(_lock_owner "$slot")"
  if [ "$owner" = "$(repo_root)" ]; then echo mine; return; fi
  buoy_slot_env "$slot"
  if port_in_use "$BUOY_API_PORT"; then echo taken; else echo stale; fi
}

# Choose a slot: explicit --slot wins (must be free/mine/stale); otherwise reuse
# this worktree's sticky slot, else the lowest available one.
choose_slot() {
  local forced="$1"
  if [ -n "$forced" ]; then
    case "$(slot_state "$forced")" in
      free|mine|stale) echo "$forced"; return 0 ;;
      *) log_error "Slot $forced is already taken by $(_lock_owner "$forced")"; return 1 ;;
    esac
  fi
  local sticky; sticky="$(cat "$(dev_dir)/slot" 2>/dev/null || true)"
  if [ -n "$sticky" ]; then
    case "$(slot_state "$sticky")" in free|mine|stale) echo "$sticky"; return 0 ;; esac
  fi
  local s
  for s in $(seq 1 "$MAX_SLOTS"); do
    case "$(slot_state "$s")" in free|mine|stale) echo "$s"; return 0 ;; esac
  done
  log_error "No free slot (1..$MAX_SLOTS all taken). Tear one down with dev-down.sh."
  return 1
}

write_lock() {
  local slot="$1"
  mkdir -p "$SLOTS_DIR"
  cat > "$(_lock_file "$slot")" <<EOF
worktree=$(repo_root)
slot=$slot
created=$(date '+%Y-%m-%dT%H:%M:%S')
EOF
  mkdir -p "$(dev_dir)"
  echo "$slot" > "$(dev_dir)/slot"
}

release_lock() {
  local slot="$1"
  rm -f "$(_lock_file "$slot")"
}

# Start the Aspire AppHost for the current slot in the background.
# Requires buoy_slot_env to have been called (BUOY_* exported). Args: ROOT DEV LOGS.
# Uses --no-launch-profile so per-slot dashboard ports aren't overridden by launchSettings.
start_backend_bg() {
  local root="$1" dev="$2" logs="$3"
  (
    cd "$root/backend/src/BuoyAI.AppHost" || exit 1
    export BUOY_SLOT
    export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development
    export ASPNETCORE_URLS="http://localhost:${BUOY_DASHBOARD_PORT}"
    export DOTNET_DASHBOARD_OTLP_ENDPOINT_URL="http://localhost:${BUOY_OTLP_PORT}"
    export DOTNET_RESOURCE_SERVICE_ENDPOINT_URL="http://localhost:${BUOY_RESOURCE_PORT}"
    export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS=true
    nohup dotnet run --no-launch-profile --environment Development > "$logs/aspire.log" 2>&1 &
    echo $! > "$dev/aspire.pid"
  )
}

# Stop the AppHost (backend) for the current slot: kill the Windows pid captured at
# boot, then sweep ALL of the slot's AppHost/API ports. Aspire splits the dashboard /
# OTLP / resource-service across processes, so every port must be covered or a leftover
# process blocks the next start. Requires buoy_slot_env. Does NOT touch containers/Flutter.
stop_backend() {
  local dev="$1"
  [ -f "$dev/apphost.winpid" ] && { kill_tree "$(cat "$dev/apphost.winpid")"; rm -f "$dev/apphost.winpid"; }
  kill_tree "$(pid_on_port "$BUOY_DASHBOARD_PORT")"
  kill_tree "$(pid_on_port "$BUOY_OTLP_PORT")"
  kill_tree "$(pid_on_port "$BUOY_RESOURCE_PORT")"
  kill_tree "$(pid_on_port "$BUOY_API_PORT")"
  rm -f "$dev/aspire.pid"
}

# Remove this slot's Docker containers (requires buoy_slot_env). Aspire orphans them
# on the forced kill we must use on Windows; the names are deterministic, so this is
# exact and safe (won't touch other slots).
remove_slot_containers() {
  docker rm -f "$BUOY_PG_CONTAINER" "$BUOY_QDRANT_CONTAINER" >/dev/null 2>&1 || true
}

# Ensure a local dev appsettings exists so a FRESH worktree can boot the slot (#644).
# The base appsettings*.json are gitignored - a clean checkout ships only appsettings.Example.json,
# whose other placeholders are dev-safe. Jwt:Key / Encryption:Key placeholders are REFUSED at startup
# (StartupSecretsValidator), so the scaffold swaps in random ones. Other startup checks:
# Stripe:SecretKey non-empty (StripeService). The DB connection is injected by Aspire
# (ConnectionStrings:BuoyAI), not read from this file. Never overwrites an existing file, so real
# local secrets / User-Secrets setups are preserved. Arg: repo root.
ensure_dev_appsettings() {
  local root="$1"
  local api_dir="$root/backend/src/BuoyAI.API"
  local target="$api_dir/appsettings.Development.json"
  local example="$api_dir/appsettings.Example.json"
  [ -f "$target" ] && return 0
  if [ ! -f "$example" ]; then
    log_warn "No appsettings.Development.json and no Example to scaffold from - the API will likely fail to boot."
    return 0
  fi
  cp "$example" "$target"
  # The API refuses to start with the placeholder Jwt:Key / Encryption:Key from the Example, so give
  # this local file its own random ones. Random per scaffold: they only protect this machine's dev DB.
  local jwt_key enc_key
  jwt_key="$(head -c 48 /dev/urandom | base64 | tr -d '
/+=' | head -c 48)"
  enc_key="$(head -c 48 /dev/urandom | base64 | tr -d '
/+=' | head -c 40)"
  sed -i "s|YOUR_JWT_SECRET_KEY_AT_LEAST_32_CHARACTERS_LONG|${jwt_key}|; s|YOUR_32_CHARACTER_ENCRYPTION_KEY|${enc_key}|" "$target"
  log_info "Scaffolded appsettings.Development.json from Example (random local Jwt/Encryption keys; DB via Aspire)."
  log_info "  → add real Plaid/Gemini/Stripe/Google keys to it only if you need those integrations locally."
}

# On an API health timeout the generic message hides the real cause. Aspire writes each resource's
# stdout/stderr to %LOCALAPPDATA%/Temp/aspire.<id>/resource-executable-*.log - surface the actual
# fatal line (e.g. "JWT key must be at least 32 characters", "Stripe API key not configured") so a
# fresh-worktree config gap is obvious instead of a bare "did not become healthy" (#644).
print_resource_error() {
  local tmp
  tmp="$(cygpath -u "${LOCALAPPDATA:-$USERPROFILE/AppData/Local}" 2>/dev/null)/Temp"
  [ -d "$tmp" ] || return 0
  local logf
  logf="$(ls -t "$tmp"/aspire.*/resource-executable-*.log 2>/dev/null | head -1 || true)"
  if [ -z "${logf:-}" ]; then
    log_warn "  (couldn't locate an Aspire resource-executable log under $tmp to show the real error)"
    return 0
  fi
  log_error "Underlying startup error - from $(basename "$logf"):"
  { grep -iE "exception|error|must be|not configured|fatal|unhandled" "$logf" || tail -20 "$logf"; } | tail -20 >&2
}

# Persist the derived env for a slot to .dev/slot.env (for inspection + e2e).
write_slot_env() {
  local slot="$1"
  buoy_slot_env "$slot"
  mkdir -p "$(dev_dir)"
  cat > "$(dev_dir)/slot.env" <<EOF
BUOY_SLOT=$BUOY_SLOT
BUOY_API_PORT=$BUOY_API_PORT
BUOY_FLUTTER_PORT=$BUOY_FLUTTER_PORT
BUOY_DASHBOARD_PORT=$BUOY_DASHBOARD_PORT
BUOY_DB_NAME=$BUOY_DB_NAME
BUOY_QDRANT_VOLUME=$BUOY_QDRANT_VOLUME
BUOY_API_BASE_URL=$BUOY_API_BASE_URL
BUOY_API_HEALTH_URL=$BUOY_API_HEALTH_URL
BUOY_FLUTTER_URL=$BUOY_FLUTTER_URL
BUOY_DASHBOARD_URL=$BUOY_DASHBOARD_URL
EOF
}
