#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
FIXTURE_PROJECT="$ROOT_DIR/tests/fixtures/coverage-hang/CoverageHang.Tests.csproj"
WORK_DIR="${WORK_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/appsurface-coverage-hang.XXXXXX")}"
SCALE_SECONDS="${SCALE_SECONDS:-180}"
HEALTHY_SECONDS="${HEALTHY_SECONDS:-130}"
RUN_HANG="${RUN_HANG:-1}"
RUN_HEALTHY_FAIL="${RUN_HEALTHY_FAIL:-1}"

if [[ "$RUN_HANG" != "0" && "$RUN_HANG" != "1" ]] || [[ "$RUN_HEALTHY_FAIL" != "0" && "$RUN_HEALTHY_FAIL" != "1" ]]; then
  printf 'RUN_HANG and RUN_HEALTHY_FAIL must be 1 or 0.\n' >&2
  exit 2
fi
if [[ ! "$SCALE_SECONDS" =~ ^[0-9]+$ ]] || (( SCALE_SECONDS < 90 )); then
  printf 'SCALE_SECONDS must be an integer of at least 90 (180 is the #815 target).\n' >&2
  exit 2
fi
if [[ ! "$HEALTHY_SECONDS" =~ ^[0-9]+$ ]] || (( HEALTHY_SECONDS < 1 )); then
  printf 'HEALTHY_SECONDS must be a positive integer.\n' >&2
  exit 2
fi
if [[ "$RUN_HEALTHY_FAIL" == "1" ]]; then
  reserve_seconds=$(( (SCALE_SECONDS + 2) / 3 ))
  if (( reserve_seconds > 60 )); then reserve_seconds=60; fi
  vstest_budget=$(( SCALE_SECONDS - reserve_seconds ))
  if (( HEALTHY_SECONDS <= vstest_budget )); then
    printf 'HEALTHY_SECONDS must exceed the derived VSTest timeout (%ss) when RUN_HEALTHY_FAIL=1.\n' "$vstest_budget" >&2
    exit 2
  fi
fi
mkdir -p "$WORK_DIR"

printf 'Restoring the isolated .NET 10 VSTest fixture...\n'
dotnet restore "$FIXTURE_PROJECT" --locked-mode

run_driver() {
  local driver="$1"
  local mode="$2"
  local output="$WORK_DIR/$driver-$mode"
  local cli_log="$WORK_DIR/$driver-$mode-cli.log"
  local started ended elapsed status
  started="$(date +%s)"
  set +e
  if [[ "$mode" == "hang" || "$mode" == "healthy-fail" ]]; then
    local category="Hang"
    if [[ "$mode" == "healthy-fail" ]]; then category="HealthyLong"; fi
    COVERAGE_HANG_HEALTHY_SECONDS="$HEALTHY_SECONDS" dotnet run --project "$CLI_PROJECT" -- coverage run \
      --test-project "$FIXTURE_PROJECT" --output "$output" --coverage-driver "$driver" \
      --no-progress-timeout "${SCALE_SECONDS}s" \
      --test-argument '--filter' --test-argument "Category=$category" >"$cli_log" 2>&1
    status=$?
  else
    COVERAGE_HANG_HEALTHY_SECONDS="$HEALTHY_SECONDS" dotnet run --project "$CLI_PROJECT" -- coverage run \
      --test-project "$FIXTURE_PROJECT" --output "$output" --coverage-driver "$driver" \
      --watchdog off --no-progress-timeout "$((HEALTHY_SECONDS + 60))s" \
      --test-argument '--filter' --test-argument 'Category=HealthyLong' >"$cli_log" 2>&1
    status=$?
  fi
  set -e
  ended="$(date +%s)"
  elapsed=$((ended - started))
  printf '%s %s elapsed=%ss exit=%s\n' "$driver" "$mode" "$elapsed" "$status"
  cat "$cli_log"

  if [[ "$mode" == "hang" || "$mode" == "healthy-fail" ]]; then
    local expected_test="CoverageHang.Tests.HangTests.NeverCompletes"
    if [[ "$mode" == "healthy-fail" ]]; then expected_test="CoverageHang.Tests.HangTests.HealthyLongRunningTest"; fi
    local sequence dump
    if [[ "$status" -eq 0 ]] || ! grep -Fq 'ASCOV120' "$cli_log" || grep -Fq 'ASCOV121' "$cli_log"; then
      printf '%s hang run did not retain the VSTest failure as the primary outcome.\n' "$driver" >&2
      return 1
    fi
    if [[ "$mode" == "healthy-fail" ]]; then
      if ! grep -Fq "Data collector 'Blame' message: The specified inactivity time of ${vstest_budget} seconds has elapsed." "$cli_log"; then
        printf '%s healthy test did not report the derived VSTest inactivity timeout (%ss).\n' "$driver" "$vstest_budget" >&2
        return 1
      fi
    fi
    sequence="$(find "$output" -type f \( -name 'Sequence*.xml' -o -name '*_Sequence.xml' \) -print -quit)"
    if [[ -z "$sequence" || ! -s "$sequence" ]]; then
      printf '%s hang run did not flush a non-empty Sequence.xml.\n' "$driver" >&2
      return 1
    fi
    dump="$(find "$output" -type f \( -name '*.dmp' -o -name '*.dump' -o -name '*.core' \) -print -quit)"
    if [[ -n "$dump" ]]; then
      printf '%s hang run produced an unexpected dump artifact.\n' "$driver" >&2
      return 1
    fi
    if ! grep -Fq 'VSTest sequence "' "$cli_log"; then
      printf '%s hang run did not have AppSurface inspect its flushed VSTest sequence.\n' "$driver" >&2
      return 1
    fi
    if ! python3 - "$output/timings.json" "$SCALE_SECONDS" "$expected_test" "$mode" <<'PY'
import json
import sys

path, budget, expected_test, mode = sys.argv[1], int(sys.argv[2]), sys.argv[3], sys.argv[4]
with open(path, encoding="utf-8") as source:
    projects = json.load(source)["projects"]
project = projects[0]
diagnostic = project["hangDiagnostics"]
expected = int(budget - min(60, budget / 3))
assert diagnostic["source"] == "automatic", diagnostic
assert diagnostic["status"] == "found", diagnostic
assert diagnostic["effectiveVstestTimeoutSeconds"] == expected, diagnostic
if mode == "healthy-fail":
    assert expected - 2 <= project["seconds"] <= expected + 30, (project["seconds"], expected)
assert diagnostic["sequencePaths"], diagnostic
assert any(item["lastStartedTest"] == expected_test
           for item in diagnostic["observations"]), diagnostic
PY
    then
      printf '%s hang run did not record the automatic, inspected no-dump sequence and expected timeout.\n' "$driver" >&2
      return 1
    fi
  else
    if [[ "$status" -ne 0 ]]; then
      printf '%s healthy long test did not complete with watchdog off.\n' "$driver" >&2
      return 1
    fi
    if [[ ! -s "$output/coverage.cobertura.xml" ]]; then
      printf '%s healthy long test did not produce merged coverage.\n' "$driver" >&2
      return 1
    fi
  fi
}

for driver in collector msbuild; do
  if [[ "$RUN_HANG" == "1" ]]; then
    run_driver "$driver" hang
  fi
  if [[ "$RUN_HEALTHY_FAIL" == "1" ]]; then
    run_driver "$driver" healthy-fail
  fi
  run_driver "$driver" healthy-off
done

printf 'Coverage fixture checks passed. 180s is the documented #815 target; SCALE_SECONDS=%ss; RUN_HANG=%s; RUN_HEALTHY_FAIL=%s. Artifacts: %s\n' "$SCALE_SECONDS" "$RUN_HANG" "$RUN_HEALTHY_FAIL" "$WORK_DIR"
