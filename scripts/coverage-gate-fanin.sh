#!/usr/bin/env bash
# Evaluate freshly collected or independently verified fan-in evidence. No collection is skipped by coverage-solution.sh.
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
COVERAGE_GATE_DIFF_BASE="${COVERAGE_GATE_DIFF_BASE-origin/main}"
COVERAGE_DIRECTORY="${COVERAGE_DIRECTORY:-$ROOT_DIR/TestResults/coverage-merged}"
COVERAGE_FANIN_REPORT="${COVERAGE_FANIN_REPORT:-$(dirname "$COVERAGE_DIRECTORY")/coverage-fanin}"
CLI_PROJECT="$ROOT_DIR/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
cli=(dotnet run --project "$CLI_PROJECT" --configuration "$BUILD_CONFIGURATION")
if [[ "${BUILD_NO_RESTORE:-false}" == "true" ]]; then
  cli+=(--no-restore)
fi
cli+=(-- coverage gate)
cd "$ROOT_DIR"

if [[ -n "${COVERAGE_FANIN_NATIVE:-}" ]]; then
  # Revalidate provenance and every input checksum immediately before evaluation.
  # A fresh destination avoids accidentally accepting a leftover merged report.
  verified_directory="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-verified-fanin.XXXXXXXX")"
  trap 'rm -f "$verified_directory/report/coverage.cobertura.xml" "$verified_directory/report/primary-paths.cobertura.xml" "$verified_directory/report/verification.json"; rmdir "$verified_directory/report" "$verified_directory"' EXIT
  python3 "$ROOT_DIR/scripts/coverage_fanin.py" merge \
    --configuration "$BUILD_CONFIGURATION" \
    --primary "$COVERAGE_DIRECTORY/fanin-primary" --native "$COVERAGE_FANIN_NATIVE" \
    --output "$verified_directory/report"
  cmp "$verified_directory/report/coverage.cobertura.xml" "$COVERAGE_FANIN_REPORT/coverage.cobertura.xml"
  cmp "$verified_directory/report/primary-paths.cobertura.xml" "$COVERAGE_FANIN_REPORT/primary-paths.cobertura.xml"
  aggregate_args=(--coverage "$COVERAGE_FANIN_REPORT/primary-paths.cobertura.xml"
    --min-line 95 --min-branch 85 --output "$COVERAGE_DIRECTORY/aggregate-gate")
  if [[ -n "$COVERAGE_GATE_DIFF_BASE" ]]; then
    aggregate_args+=(--diff-base "$COVERAGE_GATE_DIFF_BASE" --patch-line-mode codecov)
  fi
  "${cli[@]}" "${aggregate_args[@]}"
  if [[ -n "$COVERAGE_GATE_DIFF_BASE" ]]; then
    "${cli[@]}" --coverage "$COVERAGE_FANIN_REPORT/coverage.cobertura.xml" \
      --diff-base "$COVERAGE_GATE_DIFF_BASE" --min-patch-line 95 --min-patch-branch 85 \
      --patch-line-mode codecov --output "$COVERAGE_DIRECTORY"
    python3 - "$COVERAGE_DIRECTORY" <<'CHECK'
import json
import pathlib
import sys
root = pathlib.Path(sys.argv[1])
aggregate = json.loads((root / "aggregate-gate/coverage-gate.json").read_text())
patch = json.loads((root / "coverage-gate.json").read_text())
if not aggregate["passed"] or not patch["passed"]:
    raise SystemExit("Both aggregate and patch gates must pass")
if aggregate["thresholds"]["line"] != 95 or aggregate["thresholds"]["branch"] != 85:
    raise SystemExit("Aggregate policy changed")
if patch["thresholds"]["patchLine"] != 95 or patch["thresholds"]["patchBranch"] != 85:
    raise SystemExit("Patch policy changed")
if aggregate["tolerancePercent"] != 0.5 or patch["tolerancePercent"] != 0.5:
    raise SystemExit("Coverage tolerance changed")
if patch["patchLineMode"] != "codecov" or aggregate["patchDiffSource"] != patch["patchDiffSource"]:
    raise SystemExit("Patch policy or diff differs")
for dimension in ("patchLine", "patchBranch"):
    if patch[dimension]["measurable"] < aggregate[dimension]["measurable"]:
        raise SystemExit("Fan-in removed measurable patch coverage points")
if aggregate["patchLine"]["measurable"] == 0 or patch["patchLine"]["measurable"] == 0:
    raise SystemExit("Cross-platform patch acceptance requires nonempty measurable coverage")
CHECK
  fi
else
  gate_args=(--coverage "$COVERAGE_DIRECTORY/coverage.cobertura.xml" --min-line 95 --min-branch 85)
  if [[ -n "$COVERAGE_GATE_DIFF_BASE" ]]; then
    gate_args+=(--diff-base "$COVERAGE_GATE_DIFF_BASE" --min-patch-line 95 --min-patch-branch 85
      --patch-line-mode codecov)
  fi
  "${cli[@]}" "${gate_args[@]}"
fi
