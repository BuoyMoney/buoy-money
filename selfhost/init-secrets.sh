#!/bin/sh
# Creates the secrets a new Buoy Money install needs, once, and leaves existing ones alone.
#
# Runs as the "init" service in compose.yaml before the database and the app start. Each secret is
# a file in $SECRETS_DIR (default /secrets) named after the setting it fills, with "__" standing for
# ":" (so Jwt__Key becomes Jwt:Key). The app reads that folder at startup when
# BuoyMoney:Edition is selfhost. An environment variable or appsettings value still wins.
#
# These files are the keys to the database and to every stored bank token. Back up the "secrets"
# volume together with the database. Losing it means losing the data it protects.
set -eu

dir="${SECRETS_DIR:-/secrets}"
umask 077
mkdir -p "$dir"

# rand N: N random letters and digits.
rand() {
  head -c 256 /dev/urandom | base64 | tr -d '/+=\n' | head -c "$1"
}

# put NAME VALUE: write the file unless it already has content. Atomic, so a crash never leaves half a secret.
put() {
  if [ -s "$dir/$1" ]; then
    echo "kept    $1"
    return 0
  fi
  printf '%s' "$2" > "$dir/$1.tmp"
  mv "$dir/$1.tmp" "$dir/$1"
  echo "created $1"
}

put db_password "$(rand 32)"
db_password="$(cat "$dir/db_password")"

# Same database name and user as the "db" service in compose.yaml. The pool is capped below
# Postgres max_connections (30) on purpose: the app holds two pools (EF and the pgvector store), 10 each.
put ConnectionStrings__BuoyAI "Host=db;Port=5432;Database=buoy;Username=buoy;Password=${db_password};Maximum Pool Size=10"
put Jwt__Key "$(rand 64)"
put Encryption__Key "$(rand 40)"
put Mcp__SigningKey "$(rand 64)"

echo "secrets ready in $dir"
