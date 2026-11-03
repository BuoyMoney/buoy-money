#!/usr/bin/env bash
# Writes the files a self-hoster downloads from a release: compose.yaml, env.example and the
# optional Caddyfile. Used by publish-selfhost-image.yml to attach them to a release, and runnable
# by hand to see exactly what a release will carry.
#
#   scripts/publish/selfhost-assets.sh <out-dir>
#
# The compose file in the repo builds the image from a checkout (build: blocks). The release copy
# has none, so it works from an empty folder: `docker compose up -d` pulls the published image.
# The image tag still defaults to "latest" (BUOY_VERSION in .env pins one), so
# `docker compose pull && docker compose up -d` upgrades, and a hand-edited compose.yaml is never
# overwritten by an upgrade.
set -euo pipefail
OUT="${1:?usage: selfhost-assets.sh <out-dir>}"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../selfhost" && pwd)"
mkdir -p "$OUT"

sed \
  -e '/^    build:$/,/^      dockerfile: Dockerfile\.selfhost$/d' \
  -e '/^# To build the image from this checkout/d' \
  "$SRC/compose.yaml" | tr -d '\r' >"$OUT/compose.yaml"

tr -d '\r' <"$SRC/.env.example" >"$OUT/env.example"
tr -d '\r' <"$SRC/caddy/Caddyfile" >"$OUT/Caddyfile"

if grep -q 'dockerfile:' "$OUT/compose.yaml" || grep -q 'context: \.\.' "$OUT/compose.yaml"; then
  echo "release compose.yaml still has a build block" >&2; exit 1
fi
echo "wrote $OUT/compose.yaml, $OUT/env.example, $OUT/Caddyfile"
