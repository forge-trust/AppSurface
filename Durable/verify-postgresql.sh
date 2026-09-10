#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj"
postgres_image="postgres:16.5@sha256:53f3e608f9475ce120ced2d0f430b89458d7faa28530e0b0977a6af64d294877"
mode="--quick"
use_flow=false
use_schedule=false
evidence_mode=""
evidence_output=""
replace_evidence=false
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-durable-postgresql.XXXXXX")"
list_log="$work_dir/list-tests.log"
test_log="$work_dir/test-output.log"
recovery_list_log="$work_dir/recovery-list-tests.log"
recovery_test_log="$work_dir/recovery-test-output.log"
ci_all_list_log="$work_dir/ci-all-list-tests.log"
ci_remaining_list_log="$work_dir/ci-remaining-list-tests.log"
ci_remaining_test_log="$work_dir/ci-remaining-test-output.log"
recovery_evidence_file=""
v2_package_version="0.2.0-preview.8"
v2_package_sha256="62a48f6b7ec299ad608f3714a49a39f18c5cb1fe45293d53915e5549422d311e"

usage() {
  echo "Usage: $0 --quick|--ci [--flow|--schedule] [--evidence-mode cold|warm --evidence-output DIR [--replace-evidence]]" >&2
}

cleanup() {
  if [[ -n "$recovery_evidence_file" && -f "$recovery_evidence_file" ]]; then
    rm -f -- "$recovery_evidence_file"
  fi
  if [[ -d "$work_dir" && "$work_dir" == "${TMPDIR:-/tmp}"/appsurface-durable-postgresql.* ]]; then
    rm -rf -- "$work_dir"
  fi
}
trap cleanup EXIT

fail() {
  echo "Durable PostgreSQL verification failed: $1" >&2
  echo "Test project: $project" >&2
  echo "Use APPSURFACE_POSTGRES_TEST_CONNECTION for an external PostgreSQL 16.0+ database," >&2
  echo "or start Docker so the pinned Testcontainers path can run." >&2
  exit 1
}

sha256_file() {
  local file="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$file" | awk '{print $1}'
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$file" | awk '{print $1}'
  else
    fail "SHA-256 hashing requires sha256sum or shasum -a 256"
  fi
}

count_discovered_tests() {
  local log_file="$1"
  grep -Ec '^[[:space:]]+ForgeTrust\.AppSurface\.Durable\.PostgreSql\.Tests\.' "$log_file" | tr -d ' '
}

count_exact_discovered_test() {
  local log_file="$1"
  local expected_test="$2"
  awk -v expected="$expected_test" '
    {
      line = $0
      sub(/^[[:space:]]+/, "", line)
      sub(/[[:space:]]+$/, "", line)
      if (line == expected) {
        count++
      }
    }
    END { print count + 0 }
  ' "$log_file"
}

verify_test_summary() {
  local log_file="$1"
  local expected_count="$2"
  local description="$3"
  grep -Eq "^Total tests:[[:space:]]+$expected_count$" "$log_file" \
    || fail "$description did not execute exactly $expected_count tests"
  grep -Eq "^[[:space:]]+Passed:[[:space:]]+$expected_count$" "$log_file" \
    || fail "$description did not report exactly $expected_count passing tests"
  if grep -Eq '^[[:space:]]+Skipped:[[:space:]]+[1-9][0-9]*$' "$log_file"; then
    fail "$description skipped one or more required PostgreSQL tests"
  fi
}

[[ -f "$project" ]] || fail "the PostgreSQL test project is missing"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --quick|--ci)
      mode="$1"
      shift
      ;;
    --flow)
      use_flow=true
      shift
      ;;
    --schedule)
      use_schedule=true
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
    --replace-evidence)
      replace_evidence=true
      shift
      ;;
    *)
      usage
      exit 2
      ;;
  esac
done

[[ "$use_flow" == "false" || "$use_schedule" == "false" ]] \
  || fail "--flow and --schedule select different focused workloads; choose one"

if [[ "$use_schedule" == "true" && ( -n "$evidence_mode" || -n "$evidence_output" ) ]]; then
  fail "Schedule readiness evidence is not implemented yet; use the named real-PostgreSQL Schedule test directly"
fi

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
  if docker image inspect "$postgres_image" >/dev/null 2>&1; then
    observed_mode="warm"
  else
    observed_mode="cold"
  fi
  [[ "$observed_mode" == "$evidence_mode" ]] \
    || fail "requested $evidence_mode evidence but the pinned image cache is $observed_mode"
  mkdir -p "$evidence_output"
  evidence_output="$(cd -P "$evidence_output" && pwd)"
  if [[ -n "$(find "$evidence_output" -mindepth 1 -maxdepth 1 -print -quit)" && "$replace_evidence" != "true" ]]; then
    fail "evidence output must be new or empty; pass --replace-evidence to replace AppSurface-owned evidence files"
  fi
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
  find "$repo_root/examples/durable-postgresql" \
    -type f \
    ! -path '*/bin/*' \
    ! -path '*/obj/*' \
    -print >> "$source_file_list"
  find "$repo_root/examples/durable-postgresql.tests" \
    -type f \
    ! -path '*/bin/*' \
    ! -path '*/obj/*' \
    -print >> "$source_file_list"
  find "$repo_root/Durable/compatibility/V2WorkHarness" \
    -type f \
    ! -path '*/bin/*' \
    ! -path '*/obj/*' \
    -print >> "$source_file_list"
  find "$repo_root" -maxdepth 1 -type f \
    \( \
      -name 'Directory.Build.props' \
      -o -name 'Directory.Build.targets' \
      -o -name 'Directory.Packages.props' \
      -o -name 'global*.json' \
      -o -iname '*nuget*.config' \
      -o -name 'packages.lock.json' \
    \) \
    ! -path '*/bin/*' \
    ! -path '*/obj/*' \
    -print >> "$source_file_list"
  printf '%s\n' \
    "$repo_root/Durable/verify-postgresql.sh" \
    "$repo_root/Durable/packed-consumers/PostgreSqlProvider/PostgreSqlReadmeProof.cs" \
    "$repo_root/Durable/configure-postgresql-roles.sql" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli/README.md" \
    "$repo_root/Web/ForgeTrust.AppSurface.Docs.Tests/DurableSlice7AdoptionDocumentationContractTests.cs" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md" \
    "$repo_root/ForgeTrust.AppSurface.slnx" \
    "$repo_root/NuGet.package-gate.config" \
    "$repo_root/releases/unreleased.md" \
    >> "$source_file_list"
  LC_ALL=C sort -u -o "$source_file_list" "$source_file_list"
  while IFS= read -r source_file; do
    source_hash="$(sha256_file "$source_file")"
    printf '%s  %s\n' "$source_hash" "${source_file#"$repo_root/"}"
  done < "$source_file_list" > "$source_hashes"
  source_fingerprint="$(sha256_file "$source_hashes")"

  flow_scenarios=(flow-activity-resume flow-event-resume flow-identity-retry flow-scope-disable flow-timer-race)
  work_scenarios=(
    caller-owned-transaction
    operator-disable-scope
    process-loss-idempotent
    process-loss-manualresolution
    process-loss-providerkeyed
    process-loss-reconcilebeforeretry
  )
  if [[ "$use_flow" == "true" ]]; then
    scenario_names=("${flow_scenarios[@]}")
  else
    scenario_names=("${work_scenarios[@]}")
  fi
  if [[ "$use_flow" == "false" ]]; then
    scenario_names+=(forward-migration-recovery)
  fi
  output_names=("${scenario_names[@]}" run)
  for output_name in "${output_names[@]}"; do
    output_file="$evidence_output/$output_name.json"
    [[ ! -L "$output_file" ]] || fail "evidence output must not be a symbolic link: $output_name.json"
    [[ ! -e "$output_file" || -f "$output_file" ]] \
      || fail "evidence output must be a regular file when it already exists: $output_name.json"
    rm -f -- "$output_file"
  done
  recovery_evidence_file="$(mktemp "$evidence_output/.forward-migration-recovery.XXXXXX")"
fi

started_at_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
started_epoch="$(date +%s)"

if [[ "$use_flow" == "true" ]]; then
  target_test_class="DurableSlice4ReferenceWorkloadTests"
  target_test_filter="FullyQualifiedName~Flow_StartWaitEventResumeComplete_IsIdempotentAndAuthoritative|FullyQualifiedName~EventBeforeWait_DoesNotConsumeIdentity_AndChangedStartConflicts|FullyQualifiedName~ActivityCompletion_ProjectsWorkResultAndResumesParentAtomically|FullyQualifiedName~TimerAndEventRace_HasOneRevisionWinnerAndDuplicateStableLoser|FullyQualifiedName~ScopeDisable_SuspendsFlowDispatchWaitAndHistoryTogether|FullyQualifiedName~PostgreSqlDurableFlowRepairTests"
elif [[ "$use_schedule" == "true" ]]; then
  target_test_class="PostgreSqlDurableScheduleTests"
  target_test_filter="FullyQualifiedName~PostgreSqlDurableScheduleTests"
else
  target_test_class="DurableSlice3ReferenceWorkloadTests"
  target_test_filter="FullyQualifiedName~$target_test_class"
fi

case "$mode" in
  --quick)
    dotnet test "$project" --list-tests \
      -m:1 -p:UseSharedCompilation=false \
      --filter "$target_test_filter" >"$list_log" \
      || fail "test discovery failed"
    grep -Fq "$target_test_class" "$list_log" \
      || fail "the named reference workload selected zero tests"
    quick_expected_test_count="$(count_discovered_tests "$list_log")"
    [[ "$quick_expected_test_count" -gt 0 ]] \
      || fail "the named reference workload selected zero tests"
    if [[ -n "$evidence_output" ]]; then
      dotnet test "$project" \
        -m:1 -p:UseSharedCompilation=false \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal' | tee "$test_log"
      verify_test_summary "$test_log" "$quick_expected_test_count" "the focused reference workload"
      recovery_test_class="ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlSchemaIntegrationTests"
      recovery_test_method="FailedMigration_RollsBackPartialDdlAndRetriesFromLastCommittedVersion"
      recovery_test_name="$recovery_test_class.$recovery_test_method"
      recovery_test_filter="FullyQualifiedName~$recovery_test_method"
      dotnet test "$project" --list-tests \
        -m:1 -p:UseSharedCompilation=false \
        --filter "$recovery_test_filter" >"$recovery_list_log" \
        || fail "forward-only migration recovery test discovery failed"
      grep -Fq "$recovery_test_method" "$recovery_list_log" \
        || fail "the forward-only migration recovery test selected zero cases"
      recovery_started_at_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      recovery_started_epoch="$(date +%s)"
      dotnet test "$project" \
        -m:1 -p:UseSharedCompilation=false \
        --filter "$recovery_test_filter" \
        --logger 'console;verbosity=normal' | tee "$recovery_test_log"
      verify_test_summary "$recovery_test_log" 1 "the forward-only migration recovery proof"
      recovery_elapsed_milliseconds=$(( ($(date +%s) - recovery_started_epoch) * 1000 ))
      printf '%s\n' \
        '{' \
        '  "SchemaVersion": 1,' \
        "  \"RunId\": \"$run_id\"," \
        "  \"DatabaseSource\": \"$postgres_image\"," \
        "  \"StartedAtUtc\": \"$recovery_started_at_utc\"," \
        "  \"ElapsedMilliseconds\": $recovery_elapsed_milliseconds," \
        '  "Scenario": "forward-migration-recovery",' \
        "  \"TestName\": \"$recovery_test_name\"," \
        "  \"Mode\": \"$evidence_mode\"," \
        "  \"SourceSha256\": \"$source_fingerprint\"," \
        '  "FinalState": "retried-from-last-committed-version",' \
        '  "Events": [' \
        '    {' \
        '      "Sequence": 1,' \
        '      "Category": "schema",' \
        '      "Operation": "migration-failure",' \
        '      "Outcome": "rolled-back",' \
        '      "TransactionBoundary": "migration-owner",' \
        "      \"ElapsedMilliseconds\": $recovery_elapsed_milliseconds," \
        '      "SourceElapsedMilliseconds": null' \
        '    },' \
        '    {' \
        '      "Sequence": 2,' \
        '      "Category": "schema",' \
        '      "Operation": "migration-retry",' \
        '      "Outcome": "committed",' \
        '      "TransactionBoundary": "migration-owner",' \
        "      \"ElapsedMilliseconds\": $recovery_elapsed_milliseconds," \
        '      "SourceElapsedMilliseconds": null' \
        '    }' \
        '  ]' \
        '}' > "$recovery_evidence_file"
      mv -- "$recovery_evidence_file" "$evidence_output/forward-migration-recovery.json"
      recovery_evidence_file=""
    elif [[ "$use_schedule" == "true" ]]; then
      dotnet test "$project" \
        -m:1 -p:UseSharedCompilation=false \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal' | tee "$test_log"
      verify_test_summary "$test_log" "$quick_expected_test_count" "the focused Schedule workload"
    else
      dotnet test "$project" \
        -m:1 -p:UseSharedCompilation=false \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal' | tee "$test_log"
      verify_test_summary "$test_log" "$quick_expected_test_count" "the focused reference workload"
    fi
    ;;
  --ci)
    v2_harness_project="$repo_root/Durable/compatibility/V2WorkHarness/V2WorkHarness.csproj"
    v2_harness_bin="$work_dir/v2-harness-bin"
    v2_harness_obj="$work_dir/v2-harness-obj"
    dotnet restore "$v2_harness_project" --locked-mode \
      -m:1 -p:UseSharedCompilation=false \
      -p:BaseIntermediateOutputPath="$v2_harness_obj/" \
      || fail "the locked exact $v2_package_version compatibility graph did not restore"
    global_packages="$(dotnet nuget locals global-packages --list --force-english-output | sed -n 's/^global-packages: //p')"
    [[ -n "$global_packages" ]] \
      || fail "the NuGet global-packages directory could not be resolved"
    v2_package_path="$global_packages/forgetrust.appsurface.durable.postgresql/$v2_package_version/forgetrust.appsurface.durable.postgresql.$v2_package_version.nupkg"
    [[ -f "$v2_package_path" ]] \
      || fail "the restored ForgeTrust.AppSurface.Durable.PostgreSql $v2_package_version artifact is missing"
    actual_v2_package_sha256="$(sha256_file "$v2_package_path")"
    [[ "$actual_v2_package_sha256" == "$v2_package_sha256" ]] \
      || fail "the $v2_package_version artifact SHA-256 was $actual_v2_package_sha256, expected $v2_package_sha256"
    dotnet build "$v2_harness_project" --configuration Release --no-restore \
      -m:1 -p:UseSharedCompilation=false \
      -p:BaseOutputPath="$v2_harness_bin/" \
      -p:BaseIntermediateOutputPath="$v2_harness_obj/" \
      || fail "the compatibility harness could not build against exact package $v2_package_version"
    v2_harness_path="$v2_harness_bin/Release/net10.0/ForgeTrust.AppSurface.Durable.PostgreSql.TestHost.dll"
    [[ -f "$v2_harness_path" ]] \
      || fail "the exact $v2_package_version compatibility harness output is missing"
    v2_release_test="ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlMixedVersionCompatibilityTests.ExactV020Preview8Package_OperatesAfterSchema10AndSupportsBinaryRollback"
    dotnet test "$project" --list-tests \
      -m:1 -p:UseSharedCompilation=false \
      >"$ci_all_list_log" \
      || fail "full CI test discovery failed"
    ci_all_expected_test_count="$(count_discovered_tests "$ci_all_list_log")"
    [[ "$ci_all_expected_test_count" -gt 0 ]] \
      || fail "full CI test discovery selected zero tests"
    ci_release_discovered_test_count="$(count_exact_discovered_test "$ci_all_list_log" "$v2_release_test")"
    [[ "$ci_release_discovered_test_count" == "1" ]] \
      || fail "the exact $v2_package_version release proof was discovered $ci_release_discovered_test_count times"
    ci_remaining_test_filter="FullyQualifiedName!=$v2_release_test"
    dotnet test "$project" --list-tests \
      -m:1 -p:UseSharedCompilation=false \
      --filter "$ci_remaining_test_filter" >"$ci_remaining_list_log" \
      || fail "remaining CI test discovery failed"
    ci_remaining_expected_test_count="$(count_discovered_tests "$ci_remaining_list_log")"
    ci_remaining_release_count="$(count_exact_discovered_test "$ci_remaining_list_log" "$v2_release_test")"
    [[ "$ci_remaining_release_count" == "0" ]] \
      || fail "the exact $v2_package_version release proof was included in the remaining CI suite"
    [[ "$((ci_remaining_expected_test_count + ci_release_discovered_test_count))" == "$ci_all_expected_test_count" ]] \
      || fail "CI test discovery was not partitioned exactly between the release proof and remaining suite"
    APPSURFACE_REQUIRE_V020_RELEASE_PROOF=true \
    APPSURFACE_DURABLE_V020_HARNESS_PATH="$v2_harness_path" \
    APPSURFACE_DURABLE_V020_PACKAGE_PATH="$v2_package_path" \
      dotnet test "$project" \
        -m:1 -p:UseSharedCompilation=false \
        --filter "FullyQualifiedName=$v2_release_test" \
        --logger 'console;verbosity=normal' | tee "$work_dir/v2-release-test-output.log" \
        || fail "the exact $v2_package_version/current schema-10 release proof failed"
    verify_test_summary \
      "$work_dir/v2-release-test-output.log" \
      1 \
      "the exact $v2_package_version release proof"
    dotnet test "$project" \
      -m:1 -p:UseSharedCompilation=false \
      --filter "$ci_remaining_test_filter" \
      --logger 'console;verbosity=normal' | tee "$ci_remaining_test_log" \
      || fail "the remaining CI PostgreSQL suite failed"
    verify_test_summary \
      "$ci_remaining_test_log" \
      "$ci_remaining_expected_test_count" \
      "the remaining CI PostgreSQL suite"
    ;;
  *)
    usage
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
  expected_test_count="${#scenario_names[@]}"
  primary_expected_test_count="$expected_test_count"
  if [[ "$use_flow" == "false" ]]; then
    primary_expected_test_count=$(( primary_expected_test_count - 1 ))
  fi
  primary_test_count="$(grep -c "$target_test_class" "$list_log" | tr -d ' ')"
  [[ "$primary_test_count" == "$primary_expected_test_count" ]] \
    || fail "expected exactly $primary_expected_test_count discovered reference workload cases, found $primary_test_count"
  verify_test_summary "$test_log" "$primary_expected_test_count" "the evidence reference workload"
  test_count="$primary_test_count"
  if [[ "$use_flow" == "false" ]]; then
    recovery_test_count="$(grep -c "$recovery_test_method" "$recovery_list_log" | tr -d ' ')"
    [[ "$recovery_test_count" == "1" ]] \
      || fail "expected exactly one discovered forward-only migration recovery case, found $recovery_test_count"
    test_count=$(( test_count + recovery_test_count ))
  fi
  [[ "$test_count" == "$expected_test_count" ]] \
    || fail "evidence test count $test_count did not match the expected $expected_test_count scenarios"
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
    if [[ "$scenario_name" == "forward-migration-recovery" ]]; then
      grep -Fq '"SchemaVersion": 1' "$scenario_file" \
        || fail "forward recovery evidence has no schema version"
      grep -Fq "\"TestName\": \"$recovery_test_name\"" "$scenario_file" \
        || fail "forward recovery evidence has the wrong test identity"
      grep -Fq "\"SourceSha256\": \"$source_fingerprint\"" "$scenario_file" \
        || fail "forward recovery evidence is not bound to this source fingerprint"
      grep -Fq '"FinalState": "retried-from-last-committed-version"' "$scenario_file" \
        || fail "forward recovery evidence has the wrong final state"
    fi
    scenario_hash="$(sha256_file "$scenario_file")"
    printf '%s  %s.json\n' "$scenario_hash" "$scenario_name" >> "$scenario_hashes"
  done
  scenario_count="$(find "$evidence_output" -maxdepth 1 -type f -name '*.json' ! -name 'run.json' | wc -l | tr -d ' ')"
  [[ "$scenario_count" == "$expected_test_count" ]] \
    || fail "evidence output contains $scenario_count scenario files; expected the exact $expected_test_count-file set"
  scenario_fingerprint="$(sha256_file "$scenario_hashes")"
  host_os="$(uname -s)"
  host_architecture="$(uname -m)"
  image_platform="$(docker image inspect "$postgres_image" --format '{{.Os}}/{{.Architecture}}')"
  head_commit_sha="$(git -C "$repo_root" rev-parse HEAD)"
  merge_base_sha="$(git -C "$repo_root" merge-base HEAD origin/main)"
  if [[ -z "$(git -C "$repo_root" status --porcelain)" ]]; then
    worktree_state="clean"
  else
    worktree_state="dirty"
  fi
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
    "  \"headCommitSha\": \"$head_commit_sha\"," \
    "  \"mergeBaseSha\": \"$merge_base_sha\"," \
    "  \"worktreeState\": \"$worktree_state\"," \
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
