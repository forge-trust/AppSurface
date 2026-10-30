#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"

fail_legacy_input() {
  local input="$1"
  local replacement="$2"

  printf 'ERROR: %s is no longer supported by scripts/coverage-solution.sh.\n' "$input" >&2
  printf 'Use: %s\n' "$replacement" >&2
  exit 2
}

if [[ "$#" -gt 0 ]]; then
  case "$1" in
    --group)
      fail_legacy_input "--group" "dotnet tool run appsurface coverage run --test-project <path-to-project.csproj>"
      ;;
    --list-groups)
      fail_legacy_input "--list-groups" "dotnet tool run appsurface coverage run --help"
      ;;
    --merge-only)
      fail_legacy_input "--merge-only" "dotnet tool run appsurface coverage merge --source <directory> --output <directory>"
      ;;
    *)
      fail_legacy_input "arguments" "dotnet tool run appsurface coverage run --solution <solution> --output <directory>"
      ;;
  esac
fi

if [[ -n "${TEST_GROUP+x}" ]]; then
  fail_legacy_input "TEST_GROUP (unset it)" "dotnet tool run appsurface coverage run --test-project <path-to-project.csproj>"
fi

if [[ -n "${INCLUDE_FILTER+x}" ]]; then
  fail_legacy_input "INCLUDE_FILTER" "dotnet tool run appsurface coverage run --include <filter>"
fi

if [[ -n "${EXCLUDE_FILTER+x}" ]]; then
  fail_legacy_input "EXCLUDE_FILTER" "dotnet tool run appsurface coverage run --exclude <filter>"
fi

if [[ -n "${BUILD_SOLUTION+x}" ]]; then
  fail_legacy_input "BUILD_SOLUTION" "dotnet tool run appsurface coverage run --no-build"
fi

BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"

# An unset value gives contributors the same patch-coverage policy against their
# tracked main branch. CI sets this explicitly: pull-request merge checkouts use
# HEAD^1, while baseline builds set it to an empty value and run only the
# aggregate gate.
if [[ -z "${COVERAGE_GATE_DIFF_BASE+x}" ]]; then
  COVERAGE_GATE_DIFF_BASE="origin/main"
fi

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
  # These suites start containers, nested dotnet builds, or time-sensitive child processes.
  --exclusive-test-project ForgeTrust.AppSurface.Config.Tests.csproj
  --exclusive-test-project ForgeTrust.AppSurface.Aspire.Tests.csproj
  --exclusive-test-project AuthAspNetCoreDevAuthExample.Tests.csproj
  --exclusive-test-project AuthWebRazorWireProofExample.Tests.csproj
  --exclusive-test-project ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj
  --exclusive-test-project ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj
  --exclusive-test-project ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj
  --exclusive-test-project ForgeTrust.RazorWire.Cli.Tests.csproj
  --exclusive-test-project ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj
  # Passed-test results are real progress for the output-sensitive watchdog.
  --logger "console;verbosity=normal"
  --test-results junit
  --slow-test-diagnostics
)

if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
  dotnet_run_args+=(--no-restore)
fi

if [[ "${COVERAGE_REQUIRE_NON_SANDBOX:-true}" != "false" ]]; then
  dotnet_run_args+=(--require-non-sandbox)
fi

cd "$ROOT_DIR"
dotnet "${dotnet_run_args[@]}"

# With attested native evidence, retain the original full-solution aggregate gate
# independently. The cross-platform report is used only for the patch gate.
if [[ -n "${COVERAGE_FANIN_NATIVE:-}" ]]; then
  python3 "$ROOT_DIR/scripts/coverage_fanin.py" capture \
    --configuration "$BUILD_CONFIGURATION" --scope solution \
    --report "$ROOT_DIR/TestResults/coverage-merged/coverage.cobertura.xml" \
    --timings "$ROOT_DIR/TestResults/coverage-merged/timings.json" \
    --output "$ROOT_DIR/TestResults/coverage-merged/fanin-primary"
  if [[ -z "${COVERAGE_FANIN_REPORT:-}" ]]; then
    python3 "$ROOT_DIR/scripts/coverage_fanin.py" prepare-default \
      --configuration "$BUILD_CONFIGURATION" --output "$ROOT_DIR/TestResults/coverage-fanin"
  fi
  python3 "$ROOT_DIR/scripts/coverage_fanin.py" merge \
    --configuration "$BUILD_CONFIGURATION" \
    --primary "$ROOT_DIR/TestResults/coverage-merged/fanin-primary" \
    --native "$COVERAGE_FANIN_NATIVE" \
    --output "${COVERAGE_FANIN_REPORT:-$ROOT_DIR/TestResults/coverage-fanin}"
fi

"$ROOT_DIR/scripts/coverage-gate-fanin.sh"
