#!/usr/bin/env bash
# Run the pinned package gate with the study's exact event and restore state.
set -euo pipefail

definition="$GITHUB_WORKSPACE/control/.github/studies/issue-780/definition.json"
validation="tools/ForgeTrust.AppSurface.Evidence.Validation/bin/Release/net10.0/ForgeTrust.AppSurface.Evidence.Validation.dll"
project="tools/ForgeTrust.AppSurface.Evidence.Validation/ForgeTrust.AppSurface.Evidence.Validation.csproj"
root="$RUNNER_TEMP/study"
repo="$GITHUB_WORKSPACE/subject"
cd "$repo"
export NUGET_PACKAGES="$RUNNER_TEMP/study-caches/dotnet"
export NUGET_HTTP_CACHE_PATH="$RUNNER_TEMP/study-caches/http"
export npm_config_store_dir="$RUNNER_TEMP/study-caches/pnpm"

fact() { jq -er "$1" "$definition"; }
digest() { sha256sum "$1" | cut -d ' ' -f 1; }
invoke() { dotnet "$validation" "$@" | tee -a "$root/phases"; }
assert_platform() {
  test "${RUNNER_OS:-}" = Linux
  test "${ImageVersion:-}" = "$(fact .runner.build)"
  test "${RUNNER_ARCH:-}" = "$(fact .runner.arch)"
  test "$(dotnet --version)" = "$(fact .runner.sdk)"
}
case "$1" in
  init)
    assert_platform
    test "$(git rev-parse HEAD)" = "$(fact .head)"
    test "$(git rev-parse 'HEAD^{tree}')" = "$(fact .tree)"
    test "$(git merge-base "$(fact .base)" HEAD)" = "$(fact .base)"
    test "$(git rev-parse HEAD:.github/workflows/package-gate.yml)" = "$(fact .ordinaryPackageWorkflowBlob)"
    test "$(digest .github/evidence.policy.json)" = "$(fact .baselinePolicySha256)"
    {
      printf 'NUGET_PACKAGES=%s\n' "$NUGET_PACKAGES"
      printf 'NUGET_HTTP_CACHE_PATH=%s\n' "$NUGET_HTTP_CACHE_PATH"
    } >> "$GITHUB_ENV"
    mkdir "$root"
    mkdir -p "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH" "$npm_config_store_dir"
    test -z "$(find "$RUNNER_TEMP/study-caches" -type f -print -quit)"
    jq -n --arg base "$(fact .base)" --arg head "$(fact .head)" \
      '{pull_request:{base:{sha:$base}, head:{sha:$head}}}' > "$root/event.json"
    if [[ "$ARM" == candidate ]]; then
      jq --slurpfile definition "$definition" '.rules += $definition[0].candidateRules' .github/evidence.policy.json > "$root/policy.json"
      test "$(digest "$root/policy.json")" = "$(fact .candidatePolicySha256)"
    else
      cp .github/evidence.policy.json "$root/policy.json"
    fi
    ;;
  seed)
    assert_platform
    test "$(node --version)" = "v$(fact .runner.node)"
    test "$(pnpm --version)" = "$(fact .runner.pnpm)"
    dotnet restore "$project" --locked-mode
    pnpm --dir Web install --frozen-lockfile --store-dir "$npm_config_store_dir"
    mkdir -p "$RUNNER_TEMP/seed"
    tar -C "$RUNNER_TEMP/study-caches" -czf "$RUNNER_TEMP/seed/caches.tar.gz" dotnet pnpm
    echo "digest=$(digest "$RUNNER_TEMP/seed/caches.tar.gz")" >> "$GITHUB_OUTPUT"
    ;;
  restore)
    if [[ "$STATE" == warm ]]; then
      archive="$RUNNER_TEMP/seed/caches.tar.gz"
      test -n "$WARM_SEED_SHA"
      test "$(digest "$archive")" = "$WARM_SEED_SHA"
      tar -C "$RUNNER_TEMP/study-caches" -xzf "$archive"
      rm "$archive"
      test -n "$(find "$NUGET_PACKAGES" -type f -print -quit)"
      test -n "$(find "$npm_config_store_dir" -type f -print -quit)"
    else
      test "$STATE" = cold
      test -z "$(find "$RUNNER_TEMP/study-caches" -type f -print -quit)"
    fi
    ;;
  plan)
    python3 "$GITHUB_WORKSPACE/control/.github/studies/issue-780/instrument.py"
    dotnet restore "$project" --locked-mode
    dotnet build "$project" --configuration Release --no-restore
    invoke --operation collect --repository "$repo" --event-file "$root/event.json" --event-name pull_request \
      --workflow-sha "$(fact .merge)" --output "$root/changes"
    invoke --operation plan --scope packages --policy "$root/policy.json" \
      --changed-paths "$root/changes/changed-paths.json" --output "$root/plan"
    selection="$root/plan/selection.json"
    plan="$root/plan/evidence-plan.json"
    test "$(jq -er .sourceDigest "$root/changes/source-state.json")" = "$(fact .sourceDigest)"
    jq -e '(.requiresCoverage|not) and (.requiresNativeCoverage|not)' "$selection" > /dev/null
    if [[ "$ARM" == baseline ]]; then
      jq -e '.producerIds == ["package-checks"] and .requiresPackageChecks' "$selection" > /dev/null
      test "$(jq -er .PlanDigest "$plan")" = "$(fact .baselinePlanDigest)"
    else
      jq -e '.profileId == "no-evidence" and .producerIds == [] and (.requiresPackageChecks|not)' "$selection" > /dev/null
      if [[ "$PAIR" != 0 ]]; then
        test "$(digest "$root/policy.json")" = "$CANDIDATE_POLICY_SHA"
        test "$(jq -er .PlanDigest "$plan")" = "$CANDIDATE_PLAN_DIGEST"
      fi
    fi
    echo "packages=$(jq -r .requiresPackageChecks "$selection")" >> "$GITHUB_OUTPUT"
    ;;
  web)
    test "$(node --version)" = "v$(fact .runner.node)"
    test "$(pnpm --version)" = "$(fact .runner.pnpm)"
    pnpm --dir Web install --frozen-lockfile --store-dir "$npm_config_store_dir"
    pnpm --dir Web run assets:typecheck
    pnpm --dir Web run assets:test
    pnpm --dir Web run assets:verify
    ;;
  evidence)
    input=(--scope packages --policy "$root/policy.json" --changed-paths "$root/changes/changed-paths.json" --plan "$root/plan/evidence-plan.json")
    invoke --operation run "${input[@]}" --repository "$repo" --dotnet "$(command -v dotnet)" --output "$root/proof"
    invoke --operation verify "${input[@]}" --manifest "$root/proof/evidence-manifest.json" --artifact-root "$root/proof/artifacts"
    ;;
  receipt)
    python3 "$GITHUB_WORKSPACE/control/.github/studies/issue-780/receipt.py" "$root" "$definition"
    if [[ "$PAIR" == 0 ]]; then
      "$GITHUB_WORKSPACE/control/.github/studies/issue-780/quality.sh" "$root" "$definition" "$validation"
    fi
    ;;
  *) exit 2 ;;
esac
