#!/usr/bin/env bash
# Run locally what GitHub CI runs, in order, stopping at the first failure.
#
# GitHub only runs CI for PRs into main or release/** (and pushes to those). A PR
# into another feature branch (a stacked PR) gets NO CI, so run this before pushing.
#
# Usage: scripts/dev/ci-local.sh [--quick] [--backend-only] [--flutter-only] [--with-integration]
#   --quick             build + unit tests + analyze (no flutter test, no license scan)
#   --backend-only      backend steps only (plus the export tooling tests)
#   --flutter-only      flutter steps only
#   --with-integration  also run backend integration tests (needs Postgres on
#                       localhost:5432 as in ci.yml, plus JWT_TEST_KEY and ENCRYPTION_TEST_KEY)
#
# Mirrors .github/workflows/ci.yml (backend), flutter.yml (flutter), publish-scripts.yml
# (export tooling) and license-scan.yml (report mode). The migration lint runs inside the
# backend unit tests. Set BUOYAI_MIGRATION_TEST_CONNECTION to also run the database-backed
# migration tests (otherwise they skip, as they do not need to for the lint).
# Works in Git Bash on Windows and on Linux/macOS.
set -u

QUICK=0; BACKEND=1; FLUTTER=1; INTEGRATION=0
for a in "$@"; do
  case "$a" in
    --quick) QUICK=1 ;;
    --backend-only) FLUTTER=0 ;;
    --flutter-only) BACKEND=0 ;;
    --with-integration) INTEGRATION=1 ;;
    -h|--help) sed -n 2,19p "$0"; exit 0 ;;
    *) echo "Unknown flag: $a (see --help)"; exit 2 ;;
  esac
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 1

PY=""
for c in python3 python; do
  if "$c" -c "import sys" >/dev/null 2>&1; then PY="$c"; break; fi
done

SUMMARY=()
START_ALL=$SECONDS

# step "name" cmd... : runs the command, records timing, exits on failure.
step() {
  local name="$1"; shift
  echo ""
  echo "=== $name"
  local t0=$SECONDS
  "$@"
  local rc=$?
  local dt=$((SECONDS - t0))
  if [ $rc -ne 0 ]; then
    SUMMARY+=("FAIL  ${dt}s  $name")
    finish 1
  fi
  SUMMARY+=("ok    ${dt}s  $name")
}

skip() { SUMMARY+=("skip  -     $1"); }

finish() {
  echo ""
  echo "================ ci-local summary ================"
  printf '%s\n' "${SUMMARY[@]}"
  echo "total $((SECONDS - START_ALL))s"
  if [ "$1" -eq 0 ]; then echo "RESULT: all steps passed"; else echo "RESULT: FAILED (stopped at first failure)"; fi
  exit "$1"
}

# ---- backend (ci.yml: ci-complete) ----
backend_restore() { dotnet restore backend/BuoyAI.sln; }
backend_build() { dotnet build backend/BuoyAI.sln --no-restore --configuration Release; }
backend_unit() {
  (cd backend && dotnet test tests/BuoyAI.UnitTests/BuoyAI.UnitTests.csproj \
    --configuration Release --no-build --no-restore --verbosity minimal)
}
backend_integration() {
  (cd backend && \
    ConnectionStrings__DefaultConnection="Host=localhost;Port=5432;Database=BuoyAI_Test;Username=postgres;Password=TestPassword123" \
    Jwt__Key="${JWT_TEST_KEY:-}" Jwt__Issuer="BuoyAI" Jwt__Audience="BuoyAI-Users" Jwt__ExpiryInMinutes="60" \
    Encryption__Key="${ENCRYPTION_TEST_KEY:-}" Sentry__Dsn="" \
    dotnet test tests/BuoyAI.IntegrationTests/BuoyAI.IntegrationTests.csproj \
      --configuration Release --no-build --no-restore --verbosity minimal)
}

# ---- flutter (flutter.yml: analyze + test) ----
flutter_legal() { mkdir -p flutter/assets/legal && cp legal/*.md flutter/assets/legal/; }
flutter_pub() { (cd flutter && flutter pub get); }
flutter_l10n() { (cd flutter && flutter gen-l10n); }
flutter_analyze() { (cd flutter && flutter analyze); }
flutter_clean_ephemeral() { rm -rf flutter/ios/Flutter/ephemeral; }  # Windows long-path issue
flutter_test() { (cd flutter && flutter test); }

# ---- export tooling (publish-scripts.yml) ----
publish_tests() { (cd scripts/publish && "$PY" -m unittest test_publish); }

# ---- license scan, report mode (license-scan.yml) ----
license_tests() { (cd scripts/license && "$PY" -m unittest test_license_scan); }
license_dotnet() { "$PY" scripts/license/license_scan.py --ecosystem dotnet; }
license_dart() { "$PY" scripts/license/license_scan.py --ecosystem dart; }

if [ "$BACKEND" -eq 1 ]; then
  step "backend: dotnet restore" backend_restore
  step "backend: dotnet build (Release)" backend_build
  step "backend: unit tests (includes the migration lint)" backend_unit
  if [ "$INTEGRATION" -eq 1 ]; then
    step "backend: integration tests" backend_integration
  else
    skip "backend: integration tests (need Postgres; pass --with-integration)"
  fi
fi

if [ "$FLUTTER" -eq 1 ]; then
  step "flutter: copy legal assets" flutter_legal
  step "flutter: pub get" flutter_pub
  step "flutter: gen-l10n" flutter_l10n
  step "flutter: analyze" flutter_analyze
  if [ "$QUICK" -eq 1 ]; then
    skip "flutter: test (--quick)"
  else
    step "flutter: clear ios ephemeral dir" flutter_clean_ephemeral
    step "flutter: test" flutter_test
  fi
fi

if [ -z "$PY" ]; then
  skip "export tooling + license scan (no python found)"
else
  if [ "$BACKEND" -eq 1 ] || [ "$FLUTTER" -eq 0 ]; then
    step "export tooling tests (scripts/publish)" publish_tests
  fi
  if [ "$QUICK" -eq 0 ]; then
    step "license scanner tests" license_tests
    [ "$BACKEND" -eq 1 ] && step "license scan: dotnet (report only)" license_dotnet
    [ "$FLUTTER" -eq 1 ] && step "license scan: dart (report only)" license_dart
  else
    skip "license scan (--quick)"
  fi
fi

finish 0
