#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
BACKEND_DIR="$SCRIPT_DIR"
OUTPUT_DIR="$BACKEND_DIR/TestResults/CoverageSummary"

cd "$BACKEND_DIR"

echo "Restoring local dotnet tools..."
dotnet tool restore >/dev/null

echo "Running tests with coverage collection..."
dotnet test "$BACKEND_DIR/BuoyAI.sln" \
  --collect:"XPlat Code Coverage"

echo "Collecting Cobertura reports..."
mapfile -t coverage_files < <(find "$BACKEND_DIR/tests" -type f -name "coverage.cobertura.xml")

if command -v cygpath >/dev/null 2>&1; then
  for i in "${!coverage_files[@]}"; do
    coverage_files[$i]=$(cygpath -w "${coverage_files[$i]}")
  done
fi

if [ ${#coverage_files[@]} -eq 0 ]; then
  echo "No coverage reports were generated." >&2
  exit 1
fi

reports_arg=$(printf ";%s" "${coverage_files[@]}")
reports_arg=${reports_arg:1}

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Generating consolidated coverage summary..."
dotnet tool run reportgenerator \
  "-reports:${reports_arg}" \
  "-targetdir:${OUTPUT_DIR}" \
  "-reporttypes:TextSummary;Html" \
  "-classfilters:-BuoyAI.Infrastructure.Migrations.*"

echo "Coverage summary written to $OUTPUT_DIR/Summary.txt"
