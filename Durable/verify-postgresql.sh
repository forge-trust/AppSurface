#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj"
postgres_image="postgres:17.5@sha256:aadf2c0696f5ef357aa7a68da995137f0cf17bad0bf6e1f17de06ae5c769b302"
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
v2_worktree=""

usage() {
  echo "Usage: $0 --quick|--ci [--flow|--schedule] [--evidence-mode cold|warm --evidence-output DIR [--replace-evidence]]" >&2
}

cleanup() {
  if [[ -n "$v2_worktree" && -d "$v2_worktree" ]]; then
    git -C "$repo_root" worktree remove --force "$v2_worktree" >/dev/null 2>&1 || true
  fi
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
  command -v shasum >/dev/null 2>&1 || fail "shasum is required to bind readiness evidence to its source and scenarios"
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
  printf '%s\n' \
    "$repo_root/Durable/verify-postgresql.sh" \
    "$repo_root/Durable/packed-consumers/PostgreSqlProvider/PostgreSqlReadmeProof.cs" \
    "$repo_root/Durable/configure-postgresql-roles.sql" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli/DurableSchemaCommand.cs" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli.Tests/DurableSchemaCommandTests.cs" \
    "$repo_root/Cli/ForgeTrust.AppSurface.Cli/README.md" \
    "$repo_root/Web/ForgeTrust.AppSurface.Docs.Tests/DurableSlice7AdoptionDocumentationContractTests.cs" \
    "$repo_root/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/README.md" \
    "$repo_root/releases/unreleased.md" \
    >> "$source_file_list"
  LC_ALL=C sort -u -o "$source_file_list" "$source_file_list"
  while IFS= read -r source_file; do
    source_hash="$(shasum -a 256 "$source_file" | awk '{print $1}')"
    printf '%s  %s\n' "$source_hash" "${source_file#"$repo_root/"}"
  done < "$source_file_list" > "$source_hashes"
  source_fingerprint="$(shasum -a 256 "$source_hashes" | awk '{print $1}')"

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
fi

started_at_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
started_epoch="$(date +%s)"

if [[ "$use_flow" == "true" ]]; then
  target_test_class="DurableSlice4ReferenceWorkloadTests"
  target_test_filter="FullyQualifiedName~Flow_StartWaitEventResumeComplete_IsIdempotentAndAuthoritative|FullyQualifiedName~EventBeforeWait_DoesNotConsumeIdentity_AndChangedStartConflicts|FullyQualifiedName~ActivityCompletion_ProjectsWorkResultAndResumesParentAtomically|FullyQualifiedName~TimerAndEventRace_HasOneRevisionWinnerAndDuplicateStableLoser|FullyQualifiedName~ScopeDisable_SuspendsFlowDispatchWaitAndHistoryTogether"
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
      --filter "$target_test_filter" >"$list_log" \
      || fail "test discovery failed"
    grep -Fq "$target_test_class" "$list_log" \
      || fail "the named reference workload selected zero tests"
    if [[ -n "$evidence_output" ]]; then
      dotnet test "$project" \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal' | tee "$test_log"
      recovery_test_class="ForgeTrust.AppSurface.Durable.PostgreSql.Tests.PostgreSqlSchemaIntegrationTests"
      recovery_test_method="FailedMigration_RollsBackPartialDdlAndRetriesFromLastCommittedVersion"
      recovery_test_name="$recovery_test_class.$recovery_test_method"
      recovery_test_filter="FullyQualifiedName~$recovery_test_method"
      dotnet test "$project" --list-tests \
        --filter "$recovery_test_filter" >"$recovery_list_log" \
        || fail "forward-only migration recovery test discovery failed"
      grep -Fq "$recovery_test_method" "$recovery_list_log" \
        || fail "the forward-only migration recovery test selected zero cases"
      recovery_started_at_utc="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
      recovery_started_epoch="$(date +%s)"
      dotnet test "$project" \
        --filter "$recovery_test_filter" \
        --logger 'console;verbosity=normal' | tee "$recovery_test_log"
      grep -Eq '^Total tests:[[:space:]]+1$' "$recovery_test_log" \
        || fail "the evidence run did not execute exactly one forward-only migration recovery test"
      grep -Eq '^[[:space:]]+Passed:[[:space:]]+1$' "$recovery_test_log" \
        || fail "the evidence run did not pass the forward-only migration recovery test"
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
        '}' > "$evidence_output/forward-migration-recovery.json"
    elif [[ "$use_schedule" == "true" ]]; then
      dotnet test "$project" \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal'
    else
      dotnet test "$project" \
        --filter "$target_test_filter" \
        --logger 'console;verbosity=normal'
    fi
    ;;
  --ci)
    if [[ "$use_flow" == "true" ]]; then
      v2_commit="0e57477bab00b1951192c82ca28fdda977da2092"
      git -C "$repo_root" cat-file -e "$v2_commit^{commit}" \
        || fail "the pinned v2 compatibility commit $v2_commit is unavailable"
      v2_worktree="$work_dir/v2-source"
      git -C "$repo_root" worktree add --detach "$v2_worktree" "$v2_commit" >/dev/null \
        || fail "the pinned v2 source worktree could not be created"
      v2_package_version="0.0.0-v2-compat-${started_epoch}"
      v2_packages="$work_dir/v2-packages"
      mkdir -p "$v2_packages"
      v2_provider_project="$v2_worktree/Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj"
      # Preserve the pinned SQL bytes while making their intended manifest names deterministic outside the original checkout.
      sed -i.bak \
        's#<EmbeddedResource Include="Migrations/[*].sql" />#<EmbeddedResource Include="$(MSBuildProjectDirectory)/Migrations/*.sql" LogicalName="ForgeTrust.AppSurface.Durable.PostgreSql.Migrations.%(Filename)%(Extension)" />#' \
        "$v2_provider_project"
      grep -Fq 'LogicalName="ForgeTrust.AppSurface.Durable.PostgreSql.Migrations.%(Filename)%(Extension)"' \
        "$v2_provider_project" \
        || fail "the pinned v2 migration resource normalization did not apply"
      v2_restore_root="$v2_worktree/Durable/ForgeTrust.AppSurface.Durable.PostgreSql.TestHost/ForgeTrust.AppSurface.Durable.PostgreSql.TestHost.csproj"
      dotnet restore "$v2_restore_root" \
        || fail "the pinned v2 dependency graph did not restore"
      v2_pack_projects=(
        "$v2_worktree/ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
        "$v2_worktree/Workers/ForgeTrust.AppSurface.Workers/ForgeTrust.AppSurface.Workers.csproj"
        "$v2_worktree/Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj"
        "$v2_worktree/Durable/ForgeTrust.AppSurface.Durable/ForgeTrust.AppSurface.Durable.csproj"
        "$v2_worktree/Durable/ForgeTrust.AppSurface.Durable.Provider/ForgeTrust.AppSurface.Durable.Provider.csproj"
        "$v2_provider_project"
      )
      for v2_pack_project in "${v2_pack_projects[@]}"; do
        dotnet pack "$v2_pack_project" --configuration Release --output "$v2_packages" --no-restore \
          -p:PackageVersion="$v2_package_version" -p:Version="$v2_package_version" \
          || fail "a pinned v2 compatibility package did not pack: $v2_pack_project"
      done
      v2_harness_project="$repo_root/Durable/compatibility/V2WorkHarness/V2WorkHarness.csproj"
      v2_harness_bin="$work_dir/v2-harness-bin"
      # The pinned packages are timestamped for this isolated compatibility build, so keep its generated lock file
      # in the temporary work directory rather than rewriting the repository lock file.
      dotnet build "$v2_harness_project" --configuration Release \
        -p:V2PackageVersion="$v2_package_version" \
        -p:RestoreAdditionalProjectSources="$v2_packages" \
        -p:NuGetLockFilePath="$work_dir/v2-harness-packages.lock.json" \
        -p:RestoreLockedMode=false \
        -p:BaseOutputPath="$v2_harness_bin/" \
        -p:BaseIntermediateOutputPath="$work_dir/v2-harness-obj/" \
        || fail "the tiny harness could not build against the pinned v2 packages"
      export APPSURFACE_DURABLE_V2_TESTHOST_PATH="$v2_harness_bin/Release/net10.0/ForgeTrust.AppSurface.Durable.PostgreSql.TestHost.dll"
      export APPSURFACE_REQUIRE_V2_BINARY=true
      dotnet test "$project" \
        --filter "FullyQualifiedName~PostgreSqlMixedVersionCompatibilityTests" \
        --logger 'console;verbosity=normal' \
        || fail "the pinned v2/current v3 compatibility preflight failed"
      dotnet test "$project" --logger 'console;verbosity=normal'
    else
      dotnet test "$project" --logger 'console;verbosity=normal'
    fi
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
  grep -Eq "^Total tests:[[:space:]]+$primary_expected_test_count$" "$test_log" \
    || fail "the evidence run did not execute exactly $primary_expected_test_count reference workload tests"
  grep -Eq "^[[:space:]]+Passed:[[:space:]]+$primary_expected_test_count$" "$test_log" \
    || fail "the evidence run did not pass exactly $primary_expected_test_count reference workload tests"
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
    scenario_hash="$(shasum -a 256 "$scenario_file" | awk '{print $1}')"
    printf '%s  %s.json\n' "$scenario_hash" "$scenario_name" >> "$scenario_hashes"
  done
  scenario_count="$(find "$evidence_output" -maxdepth 1 -type f -name '*.json' ! -name 'run.json' | wc -l | tr -d ' ')"
  [[ "$scenario_count" == "$expected_test_count" ]] \
    || fail "evidence output contains $scenario_count scenario files; expected the exact $expected_test_count-file set"
  scenario_fingerprint="$(shasum -a 256 "$scenario_hashes" | awk '{print $1}')"
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
