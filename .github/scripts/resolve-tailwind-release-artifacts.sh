#!/usr/bin/env bash
set -euo pipefail

# Trusted workflow transport only. Artifact names select candidates here; every
# consumer receives the immutable ID resolved from the exact repository/run.
usage() {
  cat >&2 <<'EOF'
Usage:
  resolve-tailwind-release-artifacts.sh resolve-producer --repository R --run-id N --run-attempt N --name NAME --output FILE
  resolve-tailwind-release-artifacts.sh resolve-native-evidence --repository R --run-id N --invocation ID --output FILE
  resolve-tailwind-release-artifacts.sh resolve-publication --repository R --run-id N --run-attempt N --name NAME --output FILE
EOF
  exit 2
}

mode="${1:-}"
[[ -n "$mode" ]] || usage
shift
repository="" run_id="" attempt="" name="" invocation="" output=""
while (($#)); do
  case "$1" in
    --repository) (($# >= 2)) || usage; repository="$2"; shift 2 ;;
    --run-id) (($# >= 2)) || usage; run_id="$2"; shift 2 ;;
    --run-attempt) (($# >= 2)) || usage; attempt="$2"; shift 2 ;;
    --name) (($# >= 2)) || usage; name="$2"; shift 2 ;;
    --invocation) (($# >= 2)) || usage; invocation="$2"; shift 2 ;;
    --output) (($# >= 2)) || usage; output="$2"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; usage ;;
  esac
done
[[ "$repository" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || usage
[[ "$run_id" =~ ^[1-9][0-9]*$ ]] || usage
[[ -n "$output" ]] || usage
if [[ -n "$attempt" ]]; then [[ "$attempt" =~ ^[1-9][0-9]*$ ]] || usage; fi

api_started=$SECONDS
api_requests=0
api_limit=500
time_limit_seconds=300

gh_api_array() {
  local endpoint="$1" key="$2" filter="$3" page=1 result='[]' page_json count elapsed remaining timeout_seconds
  while :; do
    (( api_requests < api_limit )) || { echo "GitHub API request limit ($api_limit) exceeded." >&2; return 1; }
    elapsed=$((SECONDS - api_started))
    (( elapsed < time_limit_seconds )) || { echo "GitHub API resolver exceeded its five-minute time budget." >&2; return 1; }
    remaining=$((time_limit_seconds - elapsed))
    timeout_seconds=30
    (( remaining < timeout_seconds )) && timeout_seconds="$remaining"
    api_requests=$((api_requests + 1))
    local -a query_args=(-F per_page=100 -F "page=$page")
    [[ "$filter" == all ]] && query_args+=(-F filter=all)
    page_json="$(timeout "${timeout_seconds}s" gh api "$endpoint" "${query_args[@]}" --jq ".${key}")" || {
      echo "GitHub API request failed or exceeded its bounded timeout: $endpoint (page $page)." >&2
      return 1
    }
    [[ "$(jq -r 'type' <<<"$page_json")" == array ]] || { echo "GitHub API returned invalid '$key' page." >&2; return 1; }
    result="$(jq -cn --argjson old "$result" --argjson next "$page_json" '$old + $next')"
    count="$(jq 'length' <<<"$page_json")"
    (( count < 100 )) && break
    (( page < 5 )) || { echo "GitHub API pagination exceeded the bounded 500-record limit for $endpoint." >&2; return 1; }
    page=$((page + 1))
  done
  printf '%s\n' "$result"
}

artifacts_json="$(gh_api_array "repos/${repository}/actions/runs/${run_id}/artifacts" artifacts none)"

select_exactly_one() {
  local exact_name="$1"
  local matches count expired
  matches="$(jq -c --arg wanted "$exact_name" '[.[] | select(.name == $wanted)]' <<<"$artifacts_json")"
  count="$(jq 'length' <<<"$matches")"
  [[ "$count" == 1 ]] || {
    echo "Expected exactly one artifact named '$exact_name' in run $run_id, including expired records; found $count." >&2
    return 1
  }
  expired="$(jq -r '.[0].expired' <<<"$matches")"
  [[ "$expired" == false ]] || { echo "Artifact '$exact_name' is expired; the original candidate cannot be recovered." >&2; return 1; }
  jq -c '.[0]' <<<"$matches"
}

write_artifact_outputs() {
  local artifact="$1" prefix="$2"
  {
    echo "${prefix}_artifact_id=$(jq -r '.id' <<<"$artifact")"
    echo "${prefix}_artifact_name=$(jq -r '.name' <<<"$artifact")"
    echo "${prefix}_artifact_expires_at=$(jq -r '.expires_at' <<<"$artifact")"
  } >>"$output"
}

assert_safe_repack_history() {
  local previous_attempt="$1" jobs_json producer_job publish_job upload_step start_step
  # The attempt endpoint is paginated. Missing jobs/steps, incomplete API data,
  # cancellation, or ambiguous upload outcomes are not proof of a safe repack.
  jobs_json="$(gh_api_array "repos/${repository}/actions/runs/${run_id}/attempts/${previous_attempt}/jobs" jobs all)"
  [[ "$(jq -r 'type' <<<"$jobs_json")" == array ]] || { echo "History for attempt $previous_attempt is unreadable." >&2; return 1; }
  producer_job="$(jq -c '[.[] | select(.name == "pack-and-verify")] | if length == 1 then .[0] else empty end' <<<"$jobs_json")"
  publish_job="$(jq -c '[.[] | select(.name == "publish-nuget")] | if length == 1 then .[0] else empty end' <<<"$jobs_json")"
  [[ -n "$producer_job" && -n "$publish_job" ]] || { echo "Attempt $previous_attempt lacks complete producer/publication job history." >&2; return 1; }
  upload_step="$(jq -r '[.steps[]? | select(.name == "Upload frozen producer bundle")][0].conclusion // "unknown"' <<<"$producer_job")"
  start_step="$(jq -r '[.steps[]? | select(.name == "Upload publication-start receipt")][0].conclusion // "unknown"' <<<"$publish_job")"
  [[ "$upload_step" == skipped ]] || { echo "Attempt $previous_attempt producer upload outcome '$upload_step' does not prove that no frozen candidate was uploaded." >&2; return 1; }
  [[ "$start_step" == skipped ]] || { echo "Attempt $previous_attempt publication-start outcome '$start_step' does not prove publication never started." >&2; return 1; }
}

case "$mode" in
  resolve-producer)
    [[ -n "$attempt" && -n "$name" ]] || usage
    matches="$(jq -c --arg wanted "$name" '[.[] | select(.name == $wanted)]' <<<"$artifacts_json")"
    count="$(jq 'length' <<<"$matches")"
    if [[ "$count" == 1 ]]; then
      artifact="$(jq -c '.[0]' <<<"$matches")"
      [[ "$(jq -r '.expired' <<<"$artifact")" == false ]] || { echo "Frozen producer artifact has expired; refusing to repack." >&2; exit 1; }
      printf 'producer_found=true\nproducer_reused=true\n' >"$output"
      write_artifact_outputs "$artifact" producer
      printf 'producer_run_id=%s\nproducer_attempt=%s\nproducer_repository=%s\nproducer_source_commit=%s\n' \
        "$run_id" "$(jq -r '.workflow_run.run_attempt // empty' <<<"$artifact")" "$repository" \
        "$(jq -r '.workflow_run.head_sha // empty' <<<"$artifact")" >>"$output"
    elif [[ "$count" == 0 && "$attempt" == 1 ]]; then
      printf 'producer_found=false\nproducer_reused=false\n' >"$output"
    elif [[ "$count" == 0 ]]; then
      for ((prior=1; prior<attempt; prior++)); do assert_safe_repack_history "$prior"; done
      printf 'producer_found=false\nproducer_reused=false\n' >"$output"
    else
      echo "Multiple producer artifacts named '$name' exist in run $run_id; refusing ambiguous recovery." >&2
      exit 1
    fi
    ;;
  resolve-native-evidence)
    [[ -n "$invocation" ]] || usage
    rids=(linux-x64 linux-arm64 osx-x64 osx-arm64 win-x64)
    map_file="${output}.json"
    : >"$output"
    missing=()
    selected='[]'
    for rid in "${rids[@]}"; do
      artifact_name="tailwind-native-host-${run_id}-${invocation}-${rid}"
      matches="$(jq -c --arg wanted "$artifact_name" '[.[] | select(.name == $wanted)]' <<<"$artifacts_json")"
      count="$(jq 'length' <<<"$matches")"
      if [[ "$count" != 1 || "$(jq -r '.[0].expired == false' <<<"$matches")" != true ]]; then
        missing+=("$rid:$count$(if [[ "$count" == 1 ]]; then printf ':expired'; fi)")
        continue
      fi
      artifact_id="$(jq -r '.[0].id' <<<"$matches")"
      selected="$(jq -c --arg rid "$rid" --arg artifactId "$artifact_id" --arg directory "$rid" \
        '. + [{rid:$rid,artifactId:$artifactId,directory:$directory}]' <<<"$selected")"
    done
    printf '%s\n' "$selected" >"${output}.json"
    while IFS=$'\t' read -r rid artifact_id; do
      printf 'artifact_%s_id=%s\n' "${rid//-/_}" "$artifact_id" >>"$output"
    done < <(jq -r '.[] | [.rid,.artifactId] | @tsv' <<<"$selected")
    if ((${#missing[@]})); then
      printf 'native_evidence_missing=%s\n' "${missing[*]}" >>"$output"
      echo "Current native invocation is incomplete (${missing[*]}). Rerun all jobs; do not use --failed or mix prior attempts." >&2
      exit 1
    fi
    ;;
  resolve-publication)
    [[ -n "$name" && -n "$attempt" ]] || usage
    : >"$output"
    matches="$(jq -c --arg wanted "$name" '[.[] | select(.name == $wanted)]' <<<"$artifacts_json")"
    count="$(jq 'length' <<<"$matches")"
    if [[ "$count" == 0 ]]; then
      if (( attempt > 1 )); then
        for ((prior=1; prior<attempt; prior++)); do
          jobs_json="$(gh_api_array "repos/${repository}/actions/runs/${run_id}/attempts/${prior}/jobs" jobs all)"
          publish_job="$(jq -c '[.[] | select(.name == "publish-nuget")] | if length == 1 then .[0] else empty end' <<<"$jobs_json")"
          [[ -n "$publish_job" ]] || { echo "Prior-attempt job history is incomplete; publication-start recovery is unknown." >&2; exit 1; }
          start_step="$(jq -r '[.steps[]? | select(.name == "Upload publication-start receipt")][0].conclusion // "unknown"' <<<"$publish_job")"
          [[ "$start_step" == skipped ]] || { echo "Prior-attempt job history does not prove publication-start was never uploaded (attempt $prior: $start_step)." >&2; exit 1; }
        done
      fi
      printf 'publication_found=false\n' >>"$output"
      exit 0
    fi
    [[ "$count" == 1 ]] || { echo "Multiple publication-start artifacts named '$name' exist in run $run_id." >&2; exit 1; }
    artifact="$(jq -c '.[0]' <<<"$matches")"
    [[ "$(jq -r '.expired' <<<"$artifact")" == false ]] || { echo "Original publication-start artifact expired; original-candidate recovery is unavailable." >&2; exit 1; }
    printf 'publication_found=true\n' >>"$output"
    write_artifact_outputs "$artifact" publication
    ;;
  *) usage ;;
esac
