#!/usr/bin/env bash
# Run ordinary local coverage gates or the installed native fan-in workflow.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
COVERAGE_GATE_DIFF_BASE="${COVERAGE_GATE_DIFF_BASE-origin/main}"
COVERAGE_DIRECTORY="${COVERAGE_DIRECTORY:-$ROOT_DIR/TestResults/coverage-merged}"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"

cd "$ROOT_DIR"

if [[ -n "${COVERAGE_FANIN_NATIVE:-}" ]]; then
  : "${COVERAGE_CLI_PATH:?COVERAGE_CLI_PATH must name the installed candidate appsurface executable}"
  : "${COVERAGE_DOGFOOD_OUTPUT:?COVERAGE_DOGFOOD_OUTPUT must name a unique, fresh output root}"
  : "${COVERAGE_RUN_SCOPE:?COVERAGE_RUN_SCOPE must identify the trusted repository scope}"
  : "${COVERAGE_RUN_ID:?COVERAGE_RUN_ID must identify this workflow run}"
  : "${COVERAGE_RUN_ATTEMPT:?COVERAGE_RUN_ATTEMPT must identify this workflow attempt}"

  dogfood_args=(
    --operation fanin
    --repository "$ROOT_DIR"
    --cli "$COVERAGE_CLI_PATH"
    --output "$COVERAGE_DOGFOOD_OUTPUT"
    --configuration "$BUILD_CONFIGURATION"
    --primary "$COVERAGE_DIRECTORY/coverage.cobertura.xml"
    --timings "$COVERAGE_DIRECTORY/timings.json"
    --native-inputs "$COVERAGE_FANIN_NATIVE"
    --run-scope "$COVERAGE_RUN_SCOPE"
    --run-id "$COVERAGE_RUN_ID"
    --attempt "$COVERAGE_RUN_ATTEMPT"
  )
  if [[ -n "$COVERAGE_GATE_DIFF_BASE" ]]; then
    dogfood_args+=(--diff-base "$COVERAGE_GATE_DIFF_BASE")
  fi

  dotnet_run_args=(run --project "$ROOT_DIR/tools/ForgeTrust.AppSurface.Coverage.Dogfood/ForgeTrust.AppSurface.Coverage.Dogfood.csproj"
    --configuration "$BUILD_CONFIGURATION")
  if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
    dotnet_run_args+=(--no-restore)
  fi
  dotnet "${dotnet_run_args[@]}" -- "${dogfood_args[@]}"
  exit 0
fi

cli=(dotnet run --project "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION")
if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
  cli+=(--no-restore)
fi
cli+=(-- coverage gate)

gate_args=(--coverage "$COVERAGE_DIRECTORY/coverage.cobertura.xml" --min-line 95 --min-branch 85)
if [[ -n "$COVERAGE_GATE_DIFF_BASE" ]]; then
  gate_args+=(--diff-base "$COVERAGE_GATE_DIFF_BASE" --min-patch-line 95 --min-patch-branch 85
    --patch-line-mode codecov)
fi
"${cli[@]}" "${gate_args[@]}"
