#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COVERAGE_RUNNER_PROJECT="$ROOT_DIR/tools/ForgeTrust.AppSurface.CoverageRunner/ForgeTrust.AppSurface.CoverageRunner.csproj"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
export COVERAGE_CALLER_DIRECTORY="${COVERAGE_CALLER_DIRECTORY:-$PWD}"

dotnet_run_args=(
  run
  --project "$COVERAGE_RUNNER_PROJECT"
  --configuration "$BUILD_CONFIGURATION"
)

if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
  dotnet_run_args+=(--no-restore)
fi

dotnet_run_args+=(-- "$@")

cd "$ROOT_DIR"
exec dotnet "${dotnet_run_args[@]}"
