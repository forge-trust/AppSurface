#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COVERAGE_RUNNER_PROJECT="$ROOT_DIR/tools/ForgeTrust.AppSurface.CoverageRunner/ForgeTrust.AppSurface.CoverageRunner.csproj"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
export COVERAGE_CALLER_DIRECTORY="${COVERAGE_CALLER_DIRECTORY:-$PWD}"

use_legacy=false
if [[ "$#" -gt 0 ]]; then
  use_legacy=true
fi

if [[ "${TEST_GROUP:-all}" != "all" ]]; then
  use_legacy=true
fi

case "${BUILD_SOLUTION:-true}" in
  true|TRUE|True)
    ;;
  *)
    use_legacy=true
    ;;
esac

if [[ "$use_legacy" == "false" ]]; then
  dotnet_run_args=(
    run
    --project "$CLI_PROJECT"
    --configuration "$BUILD_CONFIGURATION"
  )

  if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
    dotnet_run_args+=(--no-restore)
  fi

  dotnet_run_args+=(
    --
    coverage
    run
    --solution "$ROOT_DIR/ForgeTrust.AppSurface.slnx"
    --output "$ROOT_DIR/TestResults/coverage-merged"
    --configuration "$BUILD_CONFIGURATION"
    --parallelism "${COVERAGE_PARALLELISM:-1}"
    --exclusive-test-project ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj
    --include "${INCLUDE_FILTER:-[ForgeTrust.AppSurface.*]*}"
    --exclude "${EXCLUDE_FILTER:-[*.Tests]*,[*.IntegrationTests]*}"
    --test-results junit
    --slow-test-diagnostics
    --logger "GitHubActions;report-warnings=false"
  )

  cd "$ROOT_DIR"
  exec dotnet "${dotnet_run_args[@]}"
fi

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
