#!/usr/bin/env bash
# Runs the migration lint and the upgrade-from-oldest tests against a throwaway Postgres.
# Needs Docker. Same tests as .github/workflows/migration-upgrade.yml.
#
#   scripts/dev/migration-upgrade-check.sh            # starts postgres:15-alpine, runs, removes it
#   BUOYAI_MIGRATION_TEST_CONNECTION="Host=...;Username=postgres;Password=...;Database=postgres" \
#     scripts/dev/migration-upgrade-check.sh          # use a server you already have (not removed)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
NAME="buoy-migration-check-$$"
PORT="${MIGRATION_CHECK_PORT:-55432}"
STARTED=0

cleanup() { [ "$STARTED" = 1 ] && docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

if [ -z "${BUOYAI_MIGRATION_TEST_CONNECTION:-}" ]; then
  command -v docker >/dev/null || { echo "Docker is required (or set BUOYAI_MIGRATION_TEST_CONNECTION)." >&2; exit 2; }
  docker run -d --name "$NAME" -e POSTGRES_PASSWORD=pw -p "127.0.0.1:${PORT}:5432" postgres:15-alpine >/dev/null
  STARTED=1
  for _ in $(seq 1 30); do
    docker exec "$NAME" pg_isready -U postgres >/dev/null 2>&1 && break
    sleep 1
  done
  export BUOYAI_MIGRATION_TEST_CONNECTION="Host=127.0.0.1;Port=${PORT};Username=postgres;Password=pw;Database=postgres"
fi

cd "$ROOT/backend"
dotnet test tests/BuoyAI.UnitTests/BuoyAI.UnitTests.csproj \
  --filter "FullyQualifiedName~BuoyAI.UnitTests.Data.Migration|FullyQualifiedName~BuoyAI.UnitTests.Data.SqlMigrationRunnerDbTests"
