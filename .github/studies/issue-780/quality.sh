#!/usr/bin/env bash
# Derive selection against predeclared obligations with the real package planner.
set -euo pipefail
root="$1"
definition="$2"
validation="$3"
actual="$(jq -r .PlanDigest "$root/plan/evidence-plan.json")"
for scope in coverage native; do
  dotnet "$validation" --operation plan --scope "$scope" --policy "$root/policy.json" \
    --changed-paths "$root/changes/changed-paths.json" --output "$root/$scope"
  jq -e '.producerIds == [] and (.requiresNativeCoverage|not)' "$root/$scope/selection.json" > /dev/null
  if [[ "$ARM" == baseline ]]; then
    test "$(jq -r .PlanDigest "$root/$scope/evidence-plan.json")" = "$(jq -r ".$(printf %s "$scope")PlanDigest" "$definition")"
  fi
done

plan_control() {
  local name="$1" expected="$2"
  shift 2
  jq -n --args '$ARGS.positional | map({Path:.,Kind:"modified",PreviousPath:null})' -- "$@" > "$root/$name-paths.json"
  dotnet "$validation" --operation plan --scope packages --policy "$root/policy.json" \
    --changed-paths "$root/$name-paths.json" --output "$root/$name"
  jq -e --argjson expected "$expected" '.producerIds == $expected' "$root/$name/selection.json" > /dev/null
}

plan_control targeted '["package-checks"]' packages/package-index.yml
plan_control mixed '["package-checks"]' packages/ci-evidence.md packages/package-index.yml
plan_control fallback '["package-checks"]' eng/unknown.ext
plan_control no-evidence '[]' docs/designs/ci-example.md
jq -e '.profileId == "native-acceptance"' "$root/fallback/selection.json" > /dev/null
jq -n --arg actual "$actual" '{
  planDigest: $actual,
  packageIndex: ["package-checks"],
  mixed: ["package-checks"],
  fallback: ["package-checks"],
  noEvidence: []
}' > "$root/quality.json"
