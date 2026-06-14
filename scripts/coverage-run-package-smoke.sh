#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_INDEX_PROJECT="$ROOT_DIR/tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj"
PACKAGE_VERSION_SUFFIX="${PACKAGE_VERSION_SUFFIX:-coverage-run-smoke.$(date +%Y%m%d%H%M%S)}"
PACKAGE_VERSION="${PACKAGE_VERSION:-0.1.0-${PACKAGE_VERSION_SUFFIX}}"
AUTO_WORK_DIR=0
if [[ -z "${WORK_DIR:-}" ]]; then
  WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-coverage-proof.XXXXXX")"
  AUTO_WORK_DIR=1
fi

AUTO_PACKAGE_ARTIFACTS=0
if [[ -z "${PACKAGE_ARTIFACTS:-}" ]]; then
  PACKAGE_ARTIFACTS="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-package-artifacts.XXXXXX")"
  AUTO_PACKAGE_ARTIFACTS=1
fi
COVERAGE_PROOF_WORK_DIR="${COVERAGE_PROOF_WORK_DIR:-$WORK_DIR/coverage-cli-consumer-proof}"
COVERAGE_PROOF_REPORT="${COVERAGE_PROOF_REPORT:-$PACKAGE_ARTIFACTS/coverage-cli-consumer-proof.md}"
PACKAGE_VALIDATION_REPORT="${PACKAGE_VALIDATION_REPORT:-$PACKAGE_ARTIFACTS/package-validation-report.md}"
PACKAGE_ARTIFACT_MANIFEST="${PACKAGE_ARTIFACT_MANIFEST:-$PACKAGE_ARTIFACTS/package-artifact-manifest.json}"

cleanup() {
  local status=$?
  if [[ "$AUTO_WORK_DIR" == "1" && "$status" == "0" ]]; then
    rm -rf "$WORK_DIR"
  elif [[ "$AUTO_WORK_DIR" == "1" ]]; then
    echo "Package coverage proof failed. Preserved workspace: $WORK_DIR" >&2
  fi

  if [[ "$AUTO_PACKAGE_ARTIFACTS" == "1" && "$status" == "0" ]]; then
    rm -rf "$PACKAGE_ARTIFACTS"
  elif [[ "$AUTO_PACKAGE_ARTIFACTS" == "1" ]]; then
    echo "Package coverage proof failed. Preserved package artifacts: $PACKAGE_ARTIFACTS" >&2
  fi
}
trap cleanup EXIT

mkdir -p "$PACKAGE_ARTIFACTS"
echo "Authoritative package coverage proof workspace: $WORK_DIR"

dotnet run --project "$PACKAGE_INDEX_PROJECT" -- \
  verify-packages \
  --repo-root "$ROOT_DIR" \
  --package-version "$PACKAGE_VERSION" \
  --artifacts-output "$PACKAGE_ARTIFACTS" \
  --artifact-manifest "$PACKAGE_ARTIFACT_MANIFEST" \
  --report "$PACKAGE_VALIDATION_REPORT" \
  --coverage-proof-work-dir "$COVERAGE_PROOF_WORK_DIR" \
  --coverage-proof-report "$COVERAGE_PROOF_REPORT"

echo "Packaged coverage CLI consumer proof passed."
echo "Package validation report: $PACKAGE_VALIDATION_REPORT"
echo "Coverage proof report: $COVERAGE_PROOF_REPORT"
