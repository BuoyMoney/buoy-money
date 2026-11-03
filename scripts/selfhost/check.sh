#!/usr/bin/env bash
# Buoy Money self-host test checklist (stage 3). Runs the checks that can be scripted against the
# stack in selfhost/compose.yaml, prints PASS / FAIL / SKIP per check, and exits non-zero on any FAIL.
#
#   scripts/selfhost/check.sh                          # build the image from this checkout, run everything it can
#   scripts/selfhost/check.sh --image ghcr.io/buoymoney/buoy-money --version 1.5.31
#   scripts/selfhost/check.sh --from-image ghcr.io/buoymoney/buoy-money:1.5.30   # also test upgrading from that build
#   scripts/selfhost/check.sh --ollama http://host.docker.internal:11434/v1 --model qwen3:4b
#
# It uses its own compose project (buoy-check), port and web subnet (172.31.213.0/24), so it can run beside
# an install you run for real without touching it,
# and it deletes its containers and volumes when it finishes (keep them with --keep).
# Needs: docker with the compose plugin, curl, a clone of this repository (compose file, migration scripts).
# Not scripted: a week of real SimpleFIN syncing, and the Raspberry Pi soak (see docs/self_host/home_lab_tester.md).
set -u

root="$(cd "$(dirname "$0")/../.." && pwd)"
# docker.exe wants a Windows path under Git Bash (pwd -W); everywhere else pwd -W does not exist.
native_root="$(cd "$root" && (pwd -W 2>/dev/null || pwd))"
compose_file="$native_root/selfhost/compose.yaml"
sql_dir="$root/backend/src/BuoyAI.Infrastructure/Migrations/Sql"
project=buoy-check
port="${CHECK_PORT:-18080}"
admin_email="check-admin@example.com"
password="Harbor-Lantern-47-Quill!"
image="" version="" from_image="" ollama_url="" ollama_model="" keep=0 rewind=2

while [ $# -gt 0 ]; do
  case "$1" in
    --image) image="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --from-image) from_image="$2"; shift 2 ;;
    --ollama) ollama_url="$2"; shift 2 ;;
    --model) ollama_model="$2"; shift 2 ;;
    --rewind) rewind="$2"; shift 2 ;;
    --keep) keep=1; shift ;;
    -h|--help) sed -n 2,12p "$0"; exit 0 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

# Git Bash on Windows rewrites paths that look like /something; this is harmless elsewhere.
export MSYS_NO_PATHCONV=1
workdir="$(cd "$(mktemp -d)" && (pwd -W 2>/dev/null || pwd))"
envfile="$workdir/env"
pass=0 fail=0 skip=0

ok()   { echo "PASS  $*"; pass=$((pass+1)); }
bad()  { echo "FAIL  $*"; fail=$((fail+1)); }
skp()  { echo "SKIP  $*"; skip=$((skip+1)); }
note() { echo "      $*"; }
step() { echo; echo "== $*"; }

# write_env IMAGE VERSION [extra KEY=VALUE ...]
write_env() {
  local img="$1" ver="$2"; shift 2
  { echo "BUOY_IMAGE=$img"; echo "BUOY_VERSION=$ver"; echo "BUOY_PORT=$port"
    echo "BUOY_ADMIN_EMAIL=$admin_email"
    echo "BUOY_WEB_SUBNET=172.31.213.0/24"; echo "BUOY_CADDY_IP=172.31.213.250"   # not the real install's 172.30.0.0/24
     [ $# -gt 0 ] && printf '%s\n' "$@"; } > "$envfile"
}
dc() { docker compose -p "$project" -f "$compose_file" --env-file "$envfile" "$@"; }

# http METHOD PATH [JSON_BODY] [TOKEN] -> sets $code and $body
http() {
  local method="$1" path="$2" data="${3:-}" token="${4:-}" args=()
  [ -n "$data" ] && args+=(-H "Content-Type: application/json" -d "$data")
  [ -n "$token" ] && args+=(-H "Authorization: Bearer $token")
  code=$(curl -sS -o "$workdir/body" -w '%{http_code}' -X "$method" ${args[@]+"${args[@]}"} --max-time "${http_timeout:-30}" "http://127.0.0.1:$port$path") || code=000
  body="$(cat "$workdir/body" 2>/dev/null)"
}
json_str() { echo "$body" | sed -n "s/.*\"$1\":\"\([^\"]*\)\".*/\1/p" | head -1; }

wait_healthy() { # SECONDS; prints the seconds it took
  local limit="$1" start=$SECONDS
  while [ $((SECONDS-start)) -lt "$limit" ]; do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 "http://127.0.0.1:$port/health")" = 200 ]; then
      echo $((SECONDS-start)); return 0
    fi
    sleep 1
  done
  return 1
}

psql_db() { dc exec -T db psql -U buoy -d buoy -v ON_ERROR_STOP=1 -tA "$@" | tr -d '\r'; }

cleanup() {
  if [ "$keep" = 1 ]; then
    echo; echo "Kept the stack: docker compose -p $project -f selfhost/compose.yaml --env-file $envfile ... (port $port)"
  else
    dc down -v --remove-orphans >/dev/null 2>&1
    rm -rf "$workdir"
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------- image
step "Image"
if [ -z "$image" ]; then
  image=buoy-money-check; version=local
  echo "Building $image:$version from this checkout (the first build is slow; later ones are cached)"
  if docker build -q -f "$native_root/Dockerfile.selfhost" -t "$image:$version" "$native_root" >/dev/null; then
    ok "image built from this checkout"
  else
    bad "image build failed"; exit 1
  fi
else
  version="${version:-latest}"
  docker image inspect "$image:$version" >/dev/null 2>&1 || docker pull -q "$image:$version" >/dev/null || { bad "cannot get $image:$version"; exit 1; }
  ok "using $image:$version"
fi

# ---------------------------------------------------------------- fresh install (on the older image when upgrading)
start_img="$image"; start_ver="$version"
if [ -n "$from_image" ]; then
  start_img="$from_image"; start_ver=latest
  case "${from_image##*:}" in */*) ;; "$from_image") ;; *) start_img="${from_image%:*}"; start_ver="${from_image##*:}" ;; esac   # a colon before a "/" is a registry port, not a tag
  docker image inspect "$start_img:$start_ver" >/dev/null 2>&1 || docker pull -q "$start_img:$start_ver" >/dev/null || { bad "cannot get $from_image"; exit 1; }
fi

step "1. Fresh install"
write_env "$start_img" "$start_ver"
dc down -v --remove-orphans >/dev/null 2>&1
if ! dc up -d >"$workdir/up.log" 2>&1; then bad "docker compose up failed"; cat "$workdir/up.log"; exit 1; fi
if secs=$(wait_healthy 180); then ok "healthy after ${secs}s ($start_img:$start_ver, empty database)"; else
  bad "not healthy after 180s"; dc logs --tail 40 app; exit 1; fi
http GET /api/version
[ "$code" = 200 ] && ok "version endpoint answers: $body" || bad "GET /api/version returned $code"
http GET /api/system/version
echo "$body" | grep -qi 'selfhost' && ok "reports the self-host edition" || bad "edition is not selfhost: $body"
migrations_before=0
if [ "$(psql_db -c "select count(*) from information_schema.tables where table_name='schemaversions'")" = 1 ]; then
  migrations_before=$(psql_db -c "select count(*) from schemaversions")
  ok "database migrated ($migrations_before migrations)"
else bad "no migration journal in the database"; fi
exposed=$(docker port "${project}-db-1" 2>/dev/null)
[ -z "$exposed" ] && ok "database publishes no port" || bad "database publishes a port: $exposed"

if [ -n "$from_image" ]; then
  step "2-3. Sign-up lock and SimpleFIN"
  skp "skipped: the older build may not have them; run without --from-image to test them on this build"
  # The older build still needs an account so the upgrade can be checked for data loss.
  http POST /api/auth/register "{\"email\":\"$admin_email\",\"password\":\"$password\",\"firstName\":\"Check\",\"lastName\":\"User\"}"
  [ "$code" = 200 ] && ok "account created on the older build" || bad "registration on the older build: $code $body"
else
step "2. Sign-up lock"
http GET /api/registration/status
echo "$body" | grep -q '"firstAccount":true' && ok "fresh server offers the first-run screen" || bad "status on a fresh server: $code $body"
reg() { http POST /api/auth/register "{\"email\":\"$1\",\"password\":\"$password\",\"firstName\":\"Check\",\"lastName\":\"User\"}"; }
reg "$admin_email"
token=$(json_str token)
if [ "$code" = 200 ] && [ -n "$token" ]; then ok "first account registers"; else bad "first registration: $code $body"; fi
http GET /api/registration/admin "" "$token"
[ "$code" = 200 ] && ok "first account (the admin email) is the admin" || bad "admin route returned $code"
reg "stranger@example.com"
if [ "$code" = 403 ] && echo "$body" | grep -q registration_closed; then ok "a stranger is refused (403 registration_closed)"; else bad "stranger registration: $code $body"; fi
http GET /api/registration/status
echo "$body" | grep -q '"requiresInvite":true' && ok "status now says an invite is required" || bad "status after first account: $body"
http POST /api/registration/invites '{"note":"check","validDays":1}' "$token"
invite=$(json_str code)
if [ "$code" = 200 ] && [ -n "$invite" ]; then
  http POST /api/auth/register "{\"email\":\"invited@example.com\",\"password\":\"$password\",\"firstName\":\"In\",\"lastName\":\"Vited\",\"inviteCode\":\"$invite\"}"
  [ "$code" = 200 ] && ok "an invite code lets one more person in" || bad "invited registration: $code $body"
else bad "creating an invite: $code $body"; fi

step "3. SimpleFIN connect smoke"
http GET /api/aggregators "" "$token"
if echo "$body" | grep -qi simplefin; then ok "SimpleFIN is offered"; else bad "SimpleFIN not offered: $code $body"; fi
http POST /api/aggregators/SimpleFin/connect/complete '{"artifact":"not-a-token"}' "$token"
if echo "$body" | grep -q '"success":false' && [ "$code" -lt 500 ]; then ok "a garbage setup token is refused cleanly"; else bad "garbage token: $code $body"; fi
internal=$(printf 'http://127.0.0.1:9/simplefin/claim/x' | base64 | tr -d '\n')
http POST /api/aggregators/SimpleFin/connect/complete "{\"artifact\":\"$internal\"}" "$token"
if echo "$body" | grep -q '"success":false' && [ "$code" -lt 500 ]; then ok "a setup token pointing at an internal address is refused"; else bad "internal-address token: $code $body"; fi
if [ -n "${SIMPLEFIN_TOKEN:-}" ]; then
  http POST /api/aggregators/SimpleFin/connect/complete "{\"artifact\":\"$SIMPLEFIN_TOKEN\"}" "$token"
  [ "$code" = 200 ] && ok "real SimpleFIN setup token connected" || bad "real SimpleFIN token: $code $body"
else
  skp "real SimpleFIN connect (set SIMPLEFIN_TOKEN to a fresh setup token to run it)"
fi

fi

step "4. Memory"
stats=$(docker stats --no-stream --format '{{.Name}} {{.MemUsage}} {{.MemPerc}}' "${project}-app-1" "${project}-db-1" 2>/dev/null)
echo "$stats" | sed 's/^/      /'
app_pct=$(echo "$stats" | awk '/-app-1/{gsub("%","",$NF); print int($NF)}')
if [ -n "$app_pct" ] && [ "$app_pct" -lt 80 ]; then ok "app is at ${app_pct}% of its 768 MB limit"; else bad "app memory ${app_pct:-unknown}% of limit"; fi
note "Idle numbers on this machine. On the Pi, repeat this after a day and after a SimpleFIN sync."

step "5. Power cut during an upgrade"
dc stop app >/dev/null 2>&1
if [ -n "$from_image" ]; then
  # Real upgrade path: the database was created by the older image; the new image will migrate it.
  write_env "$image" "$version"
  note "database created by $from_image; starting $image:$version against it"
else
  # No older image: rewind the newest $rewind migrations with their real down scripts, so the next start has work to do.
  mapfile -t ups < <(ls "$sql_dir"/*.up.sql | sort | tail -n "$rewind")
  for ((i=${#ups[@]}-1; i>=0; i--)); do
    base="$(basename "${ups[$i]}" .up.sql)"
    down="$sql_dir/$base.down.sql"
    if [ ! -f "$down" ]; then bad "no down script for $base"; continue; fi
    psql_db -1 < "$down" >/dev/null 2>&1 || bad "down script failed for $base"
    psql_db -c "delete from schemaversions where scriptname like '%$base.up.sql'" >/dev/null
  done
  note "rewound the database to $(psql_db -c "select count(*) from schemaversions") migrations (was $migrations_before)"
fi
pre_kill=$(psql_db -c "select count(*) from schemaversions")
# --force-recreate gives a new container, so the logs below are only from this start (the old container's
# log already says "pending" from step 1).
dc up -d --force-recreate app >/dev/null 2>&1
# Kill the app the moment this start reports pending migrations: the closest a script gets to pulling the plug.
killed=0
for _ in $(seq 1 600); do
  if dc logs app 2>&1 | grep -q "SQL migrations pending: [1-9]"; then docker kill "${project}-app-1" >/dev/null 2>&1 && killed=1; break; fi
  sleep 0.05
done
sleep 2
mid=$(psql_db -c "select count(*) from schemaversions")
if [ "$killed" = 1 ] && [ "$mid" -lt "$migrations_before" ]; then
  ok "app killed (SIGKILL) during the upgrade ($pre_kill migrations before, $mid after the kill, $migrations_before to reach)"
elif [ "$killed" = 1 ]; then
  skp "app was killed, but only after all $mid migrations had been applied; the cut did not land inside the migration window"
else
  skp "app finished starting before it could be killed; the cut was not simulated"
fi
note "journal right after the kill: $mid migrations (each migration commits with its journal row, so a half-applied step is not possible)"
dc up -d app >/dev/null 2>&1
if secs=$(wait_healthy 180); then ok "restarts healthy after the cut (${secs}s)"; else bad "did not come back after the cut"; dc logs --tail 40 app; fi
after=$(psql_db -c "select count(*) from schemaversions")
latest=$(ls "$sql_dir"/*.up.sql | wc -l | tr -d ' ')
[ "$after" -ge "$latest" ] && ok "migration journal complete ($after, $latest scripts shipped)" || bad "journal has $after of $latest migrations"
http POST /api/auth/login "{\"email\":\"$admin_email\",\"password\":\"$password\"}"
[ "$code" = 200 ] && ok "the admin account survived and can sign in" || bad "login after the cut: $code $body"
token=$(json_str token)
backups=$(dc exec -T app sh -c 'ls /data/backups 2>/dev/null | wc -l' 2>/dev/null | tr -d '\r ')
[ "${backups:-0}" -ge 1 ] && ok "pre-upgrade backup kept in /data/backups ($backups file(s))" || bad "no pre-upgrade backup found"
if [ -n "$from_image" ]; then ok "upgrade from $from_image reached $after migrations"; else skp "upgrade from an older image (pass --from-image <image:tag>)"; fi

step "6. AI off, then on"
http GET /api/aisettings "" "$token"
if echo "$body" | grep -q '"enabled":false'; then ok "AI is off by default"; else bad "AI status by default: $code $body"; fi
if [ -n "$ollama_url" ] && [ -n "$ollama_model" ]; then
  write_env "$image" "$version" BUOY_AI_PROVIDER=ollama "BUOY_AI_BASE_URL=$ollama_url" "BUOY_AI_MODEL=$ollama_model" BUOY_AI_REASONING_EFFORT=none BUOY_AI_TIMEOUT_SECONDS=300
else
  write_env "$image" "$version" BUOY_AI_PROVIDER=ollama BUOY_AI_MODEL=check-model
fi
dc up -d app >/dev/null 2>&1
wait_healthy 120 >/dev/null || bad "app did not restart with AI settings"
http GET /api/aisettings "" "$token"
if echo "$body" | grep -q '"enabled":true'; then ok "choosing the ollama provider turns AI on"; else bad "AI did not turn on: $code $body"; fi
if [ -n "$ollama_url" ] && [ -n "$ollama_model" ]; then
  http_timeout=600 http POST /api/aisettings/probe "" "$token"   # the first call loads the model into memory
  if echo "$body" | grep -q '"ok":true'; then ok "model probe passed ($ollama_model)"; else bad "model probe failed: $code $body"; fi
else
  skp "model probe (pass --ollama http://host.docker.internal:11434/v1 --model <name> with Ollama running)"
fi
write_env "$image" "$version"
dc up -d app >/dev/null 2>&1
wait_healthy 120 >/dev/null
http GET /api/aisettings "" "$token"
echo "$body" | grep -q '"enabled":false' && ok "removing the settings turns AI off again" || bad "AI did not turn off: $body"

echo
echo "Result: $pass passed, $fail failed, $skip skipped"
[ "$fail" = 0 ]
