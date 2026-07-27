#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj"
postgres_image="postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302"
mode="--quick"
evidence_mode=""
evidence_output=""
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-durable-postgresql.XXXXXX")"
list_log="$work_dir/list-tests.log"
test_log="$work_dir/test-output.log"

cleanup() {
  if [[ -d "$work_dir" && "$work_dir" == "${TMPDIR:-/tmp}"/appsurface-durable-postgresql.* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

fail() {
  echo "Durable PostgreSQL verification failed: $1" >&2
  echo "Test project: $project" >&2
  echo "Use APPSURFACE_POSTGRES_TEST_CONNECTION for an external PostgreSQL 17.5 database," >&2
  echo "or start Docker so the pinned Testcontainers path can run." >&2
  exit 1
}

[[ -f "$project" ]] || fail "the PostgreSQL test project is missing"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --quick|--ci)
      mode="$1"
      shift
      ;;
    --evidence-mode)
      [[ $# -ge 2 ]] || fail "--evidence-mode requires cold or warm"
      evidence_mode="$2"
      shift 2
      ;;
    --evidence-output)
      [[ $# -ge 2 ]] || fail "--evidence-output requires a directory"
      evidence_output="$2"
      shift 2
      ;;
    *)
      echo "Usage: $0 --quick|--ci [--evidence-mode cold|warm --evidence-output DIR]" >&2
      exit 2
      ;;
  esac
done

if [[ "${CI:-}" == "true" && "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP:-}" == "true" ]]; then
  fail "APPSURFACE_POSTGRES_TEST_ALLOW_SKIP is a local-only escape hatch and cannot be enabled in CI"
fi

if [[ -n "$evidence_mode" || -n "$evidence_output" ]]; then
  [[ "$mode" == "--quick" ]] || fail "readiness evidence is supported only by the focused --quick workload"
  [[ "$evidence_mode" == "cold" || "$evidence_mode" == "warm" ]] \
    || fail "--evidence-mode must be cold or warm"
  [[ -n "$evidence_output" ]] || fail "--evidence-output is required with --evidence-mode"
  [[ -z "${APPSURFACE_POSTGRES_TEST_CONNECTION:-}" ]] \
    || fail "cold/warm Docker evidence cannot be classified when an external PostgreSQL connection is configured"
  [[ "${APPSURFACE_POSTGRES_TEST_ALLOW_SKIP:-}" != "true" ]] \
    || fail "APPSURFACE_POSTGRES_TEST_ALLOW_SKIP cannot be enabled while recording readiness evidence"
  command -v docker >/dev/null 2>&1 || fail "Docker is required for classified cold/warm evidence"
  command -v shasum >/dev/null 2>&1 || fail "shasum is required to bind readiness evidence to its source and scenarios"
  if docker image inspect "$postgres_image" >/dev/null 2>&1; then
    observed_mode="warm"
  else
    observed_mode="cold"
  fi
  [[ "$observed_mode" == "$evidence_mode" ]] \
    || fail "requested $evidence_mode evidence but the pinned image cache is $observed_mode"
  mkdir -p "$evidence_output"
  evidence_output="$(cd "$evidence_output" && pwd)"
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  export APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_DIRECTORY="$evidence_output"
  export APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_MODE="$evidence_mode"
  export APPSURFACE_POSTGRES_REFERENCE_EVIDENCE_RUN_ID="$run_id"

  source_file_list="$work_dir/source-files.txt"
  source_hashes="$work_dir/source-hashes.txt"
  find \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.Provider" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.TestHost" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests" \
    -type f \
    ! -path '*/bin/*' \
    ! -path '*/obj/*' \
    -print > "$source_file_list"
  printf '%s\n' \
    "$repo_root/Durable/verify-postgresql.sh" \
    "$repo_root/Durable/packed-consumers/PostgreSqlProvider/PostgreSqlReadmeProof.cs" \
    >> "$source_file_list"
  LC_ALL=C sort -u -o "$source_file_list" "$source_file_list"
  while IFS= read -r source_file; do
    source_hash="$(shasum -a 256 "$source_file" | awk '{print $1}')"
    printf '%s  %s\n' "$source_hash" "${source_file#"$repo_root/"}"
  done < "$source_file_list" > "$source_hashes"
  source_fingerprint="$(shasum -a 256 "$source_hashes" | awk '{print $1}')"

  scenario_names=(
    caller-owned-transaction
    operator-disable-scope
    process-loss-idempotent
    process-loss-manualresolution
    process-loss-providerkeyed
    process-loss-reconcilebeforeretry
  )
  output_names=("${scenario_names[@]}" run)
  for output_name in "${output_names[@]}"; do
    output_file="$evidence_output/$output_name.json"
    [[ ! -L "$output_file" ]] || fail "evidence output must not be a symbolic link: $output_name.json"
    [[ ! -e "$output_file" || -f "$output_file" ]] \
      || fail "evidence output must be a regular file when it already exists: $output_name.json"
    rm -f -- "$output_file"
  done
fi

started_at_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
started_epoch="$(date +%s)"

case "$mode" in
  --quick)
    dotnet test "$project" --list-tests \
      --filter 'FullyQualifiedName~DurableSlice3ReferenceWorkloadTests' >"$list_log" \
      || fail "test discovery failed"
    grep -Fq 'DurableSlice3ReferenceWorkloadTests' "$list_log" \
      || fail "the named reference workload selected zero tests"
    if [[ -n "$evidence_output" ]]; then
      dotnet test "$project" \
        --filter 'FullyQualifiedName~DurableSlice3ReferenceWorkloadTests' \
        --logger 'console;verbosity=normal' | tee "$test_log"
    else
      dotnet test "$project" \
        --filter 'FullyQualifiedName~DurableSlice3ReferenceWorkloadTests' \
        --logger 'console;verbosity=normal'
    fi
    ;;
  --ci)
    dotnet test "$project" --logger 'console;verbosity=normal'
    ;;
  *)
    echo "Usage: $0 --quick|--ci [--evidence-mode cold|warm --evidence-output DIR]" >&2
    exit 2
    ;;
esac

if [[ -n "$evidence_output" ]]; then
  elapsed_seconds=$(( $(date +%s) - started_epoch ))
  if [[ "$evidence_mode" == "cold" ]]; then
    threshold_seconds=600
  else
    threshold_seconds=300
  fi
  [[ "$elapsed_seconds" -le "$threshold_seconds" ]] \
    || fail "$evidence_mode workload took ${elapsed_seconds}s, exceeding the ${threshold_seconds}s readiness target"
  test_count="$(grep -c 'DurableSlice3ReferenceWorkloadTests' "$list_log" | tr -d ' ')"
  [[ "$test_count" == "6" ]] || fail "expected exactly 6 discovered reference workload cases, found $test_count"
  grep -Eq '^Total tests:[[:space:]]+6$' "$test_log" || fail "the evidence run did not execute exactly 6 tests"
  grep -Eq '^[[:space:]]+Passed:[[:space:]]+6$' "$test_log" || fail "the evidence run did not pass exactly 6 tests"

  scenario_names=(
    caller-owned-transaction
    operator-disable-scope
    process-loss-idempotent
    process-loss-manualresolution
    process-loss-providerkeyed
    process-loss-reconcilebeforeretry
  )
  scenario_hashes="$work_dir/scenario-hashes.txt"
  : > "$scenario_hashes"
  for scenario_name in "${scenario_names[@]}"; do
    scenario_file="$evidence_output/$scenario_name.json"
    [[ -f "$scenario_file" ]] || fail "expected scenario evidence is missing: $scenario_name.json"
    grep -Fq "\"RunId\": \"$run_id\"" "$scenario_file" \
      || fail "scenario evidence was not freshly written by this run: $scenario_name.json"
    grep -Fq "\"Mode\": \"$evidence_mode\"" "$scenario_file" \
      || fail "scenario evidence has the wrong mode: $scenario_name.json"
    grep -Fq "\"DatabaseSource\": \"$postgres_image\"" "$scenario_file" \
      || fail "scenario evidence used a different PostgreSQL image: $scenario_name.json"
    scenario_hash="$(shasum -a 256 "$scenario_file" | awk '{print $1}')"
    printf '%s  %s.json\n' "$scenario_hash" "$scenario_name" >> "$scenario_hashes"
  done
  scenario_count="$(find "$evidence_output" -maxdepth 1 -type f -name '*.json' ! -name 'run.json' | wc -l | tr -d ' ')"
  [[ "$scenario_count" == "6" ]] || fail "evidence output contains $scenario_count scenario files; expected the exact 6-file set"
  scenario_fingerprint="$(shasum -a 256 "$scenario_hashes" | awk '{print $1}')"
  host_os="$(uname -s)"
  host_architecture="$(uname -m)"
  image_platform="$(docker image inspect "$postgres_image" --format '{{.Os}}/{{.Architecture}}')"
  base_commit_sha="$(git -C "$repo_root" rev-parse HEAD)"
  manifest_file="$work_dir/run.json"
  printf '%s\n' \
    '{' \
    '  "schemaVersion": 1,' \
    "  \"mode\": \"$evidence_mode\"," \
    "  \"startedAtUtc\": \"$started_at_utc\"," \
    "  \"elapsedSeconds\": $elapsed_seconds," \
    "  \"thresholdSeconds\": $threshold_seconds," \
    "  \"postgresImage\": \"$postgres_image\"," \
    "  \"imagePlatform\": \"$image_platform\"," \
    "  \"hostOs\": \"$host_os\"," \
    "  \"hostArchitecture\": \"$host_architecture\"," \
    "  \"baseCommitSha\": \"$base_commit_sha\"," \
    '  "sourceState": "working-tree-fingerprint",' \
    "  \"sourceSha256\": \"$source_fingerprint\"," \
    "  \"scenarioSetSha256\": \"$scenario_fingerprint\"," \
    "  \"discoveredTests\": $test_count," \
    '  "result": "passed"' \
    '}' > "$manifest_file"
  mv -- "$manifest_file" "$evidence_output/run.json"
  echo "Recorded $evidence_mode readiness evidence in $evidence_output."
fi

echo "Durable PostgreSQL $mode verification passed."
