#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
FIXTURE_PROJECT="$ROOT_DIR/tests/fixtures/coverage-hang/CoverageHang.Tests.csproj"
WORK_DIR="${WORK_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/appsurface-coverage-progress.XXXXXX")}"
mkdir -p "$WORK_DIR"

dotnet restore "$FIXTURE_PROJECT" --locked-mode
dotnet build "$FIXTURE_PROJECT" --configuration Release --no-restore -v quiet
dotnet build "$CLI_PROJECT" --configuration Release --no-restore -v quiet

for mode in quiet progress hang; do
  category="HealthySequence"
  if [[ "$mode" == "hang" ]]; then category="Hang"; fi
  run_args=(--test-project "$FIXTURE_PROJECT" --output "$WORK_DIR/$mode" --configuration Release
    --no-restore --no-build --no-progress-timeout 10s --heartbeat-interval 1s)
  if [[ "$mode" != "quiet" ]]; then run_args+=(--logger "console;verbosity=normal"); fi
  run_args+=(--test-argument '--filter' --test-argument "Category=$category")
  set +e
  dotnet run --project "$CLI_PROJECT" --configuration Release --no-restore --no-build -- coverage run \
    "${run_args[@]}" \
    >"$WORK_DIR/$mode.log" 2>&1
  status=$?
  set -e
  printf '%s exit=%s\n' "$mode" "$status"
  python3 - "$WORK_DIR" "$mode" "$status" <<'PY'
import json
import pathlib
import sys

work, mode, status = pathlib.Path(sys.argv[1]), sys.argv[2], int(sys.argv[3])
log = (work / (mode + ".log")).read_text()
if mode == "progress":
    assert status == 0, log
    assert "Total tests: 12" in log, log
    assert "Passed: 12" in log, log
    assert "ASCOV121" not in log, log
    assert (work / mode / "coverage.cobertura.xml").is_file()
else:
    assert status == 124, log
    assert "ASCOV121" in log, log
    incident = json.loads((work / mode / "coverage-watchdog.json").read_text())
    assert incident["watchdogMode"] == "fail", incident
    assert incident["noProgressTimeoutMilliseconds"] == 10000, incident
    assert incident["primary"]["noProgressMilliseconds"] >= 10000, incident
PY
done

printf 'Coverage progress checks passed. Artifacts: %s\n' "$WORK_DIR"
