#!/usr/bin/env bash
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOLUTION_PATH="$ROOT_DIR/ForgeTrust.AppSurface.slnx"
OUTPUT_DIR="$ROOT_DIR/TestResults/coverage-merged"
BUILD_CONFIGURATION="${BUILD_CONFIGURATION:-Debug}"
BUILD_NO_RESTORE="${BUILD_NO_RESTORE:-false}"
INCLUDE_FILTER="${INCLUDE_FILTER:-[ForgeTrust.AppSurface.*]*}"
EXCLUDE_FILTER="${EXCLUDE_FILTER:-[*.Tests]*,[*.IntegrationTests]*}"
EXCLUDE_FILTER="${EXCLUDE_FILTER//,/%2c}"
GROUP_NAME="${TEST_GROUP:-all}"
MERGE_ONLY=false
MERGE_SOURCE_DIR=""
BUILD_SOLUTION="${BUILD_SOLUTION:-}"
LIST_GROUPS=false

usage() {
  cat <<EOF
Usage:
  scripts/coverage-solution.sh [solution] [output]
  scripts/coverage-solution.sh --group <name> [--output <dir>] [--solution <path>]
  scripts/coverage-solution.sh --merge-only <source-dir> [--output <dir>]

Groups:
  all, core, tools, web, docs, razorwire, integration

Environment:
  TEST_GROUP            Test group to run. Defaults to all.
  BUILD_CONFIGURATION   Test configuration. Defaults to Debug.
  BUILD_SOLUTION        true or false. Defaults to true for all, false for named groups.
  BUILD_NO_RESTORE      true/false. Adds --no-restore to build and test commands after a prior restore.
  INCLUDE_FILTER        Coverlet include filter.
  EXCLUDE_FILTER        Coverlet exclude filter.
EOF
}

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --group)
      if [[ "$#" -lt 2 ]]; then
        echo "--group requires a value" >&2
        exit 2
      fi
      GROUP_NAME="$2"
      shift 2
      ;;
    --list-groups)
      LIST_GROUPS=true
      shift
      ;;
    --merge-only)
      if [[ "$#" -lt 2 ]]; then
        echo "--merge-only requires a source directory" >&2
        exit 2
      fi
      MERGE_ONLY=true
      MERGE_SOURCE_DIR="$2"
      shift 2
      ;;
    --output)
      if [[ "$#" -lt 2 ]]; then
        echo "--output requires a value" >&2
        exit 2
      fi
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --solution)
      if [[ "$#" -lt 2 ]]; then
        echo "--solution requires a value" >&2
        exit 2
      fi
      SOLUTION_PATH="$2"
      shift 2
      ;;
    --build-solution)
      BUILD_SOLUTION=true
      shift
      ;;
    --skip-solution-build)
      BUILD_SOLUTION=false
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --*)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
    *)
      if [[ "$SOLUTION_PATH" == "$ROOT_DIR/ForgeTrust.AppSurface.slnx" ]]; then
        SOLUTION_PATH="$1"
      elif [[ "$OUTPUT_DIR" == "$ROOT_DIR/TestResults/coverage-merged" ]]; then
        OUTPUT_DIR="$1"
      else
        echo "Unexpected argument: $1" >&2
        usage >&2
        exit 2
      fi
      shift
      ;;
  esac
done

if [[ "$LIST_GROUPS" == true ]]; then
  printf '%s\n' all core tools web docs razorwire integration
  exit 0
fi

if [[ "$MERGE_ONLY" == true ]]; then
  GROUP_NAME="merge-only"
  BUILD_SOLUTION=false
elif [[ -z "$BUILD_SOLUTION" ]]; then
  if [[ "$GROUP_NAME" == "all" ]]; then
    BUILD_SOLUTION=true
  else
    BUILD_SOLUTION=false
  fi
fi

case "$BUILD_SOLUTION" in
  true|false) ;;
  *)
    echo "BUILD_SOLUTION must be true or false." >&2
    exit 2
    ;;
esac

if [[ "$MERGE_ONLY" != true && ! -f "$SOLUTION_PATH" ]]; then
  echo "Solution not found: $SOLUTION_PATH" >&2
  exit 1
fi

if [[ "$MERGE_ONLY" != true ]]; then
  case "$GROUP_NAME" in
    all|core|tools|web|docs|razorwire|integration) ;;
    *)
      echo "Unknown test group: $GROUP_NAME" >&2
      echo "Valid groups: all, core, tools, web, docs, razorwire, integration" >&2
      exit 2
      ;;
  esac
fi

canonicalize_directory() {
  local label="$1"
  local path="$2"
  local canonical

  if ! canonical="$(mkdir -p "$path" && cd "$path" && pwd)"; then
    echo "Unable to prepare $label directory: $path" >&2
    return 1
  fi

  if [[ -z "$canonical" ]]; then
    echo "Unable to resolve $label directory: $path" >&2
    return 1
  fi

  printf '%s\n' "$canonical"
}

canonicalize_existing_directory() {
  local label="$1"
  local path="$2"
  local canonical

  if [[ ! -d "$path" ]]; then
    echo "$label directory not found: $path" >&2
    return 1
  fi

  if ! canonical="$(cd "$path" && pwd)"; then
    echo "Unable to resolve $label directory: $path" >&2
    return 1
  fi

  if [[ -z "$canonical" ]]; then
    echo "Unable to resolve $label directory: $path" >&2
    return 1
  fi

  printf '%s\n' "$canonical"
}

SOLUTION_DIR=""
if [[ "$MERGE_ONLY" != true ]]; then
  SOLUTION_DIR="$(cd "$(dirname "$SOLUTION_PATH")" && pwd)"
fi
if ! OUTPUT_DIR="$(canonicalize_directory "output" "$OUTPUT_DIR")"; then
  exit 1
fi
TIMINGS_FILE="$OUTPUT_DIR/timings.json"

seconds_now() {
  date +%s
}

json_escape() {
  printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'
}

project_slug() {
  printf '%s' "$(basename "$1" .csproj)" | tr -c 'A-Za-z0-9_.-' '-'
}

project_group() {
  case "$1" in
    Aspire/*|Auth/*|Caching/*|Config/*|Console/*|Dependency/*|Flow/*|ForgeTrust.AppSurface.Core.Tests/*)
      printf '%s\n' core
      ;;
    Cli/*|tools/*)
      printf '%s\n' tools
      ;;
    Web/ForgeTrust.AppSurface.Docs.Tests/*)
      printf '%s\n' docs
      ;;
    Web/ForgeTrust.RazorWire.IntegrationTests/*)
      printf '%s\n' integration
      ;;
    Web/ForgeTrust.RazorWire.Tests/*|Web/ForgeTrust.RazorWire.Cli.Tests/*)
      printf '%s\n' razorwire
      ;;
    Web/*)
      printf '%s\n' web
      ;;
    *)
      printf '%s\n' core
      ;;
  esac
}

ensure_reportgenerator() {
  if [[ ! -f "$ROOT_DIR/.config/dotnet-tools.json" ]]; then
    echo "Missing .NET tool manifest: $ROOT_DIR/.config/dotnet-tools.json" >&2
    exit 1
  fi

  if ! (cd "$ROOT_DIR" && dotnet tool restore); then
    echo "Failed to restore local .NET tools" >&2
    exit 1
  fi
}

join_by_semicolon() {
  local joined=""
  local item
  for item in "$@"; do
    if [[ -z "$joined" ]]; then
      joined="$item"
    else
      joined="$joined;$item"
    fi
  done
  printf '%s' "$joined"
}

copy_junit_files() {
  local source_dir="$1"
  local copied=0

  while IFS= read -r -d '' junit_file; do
    cp "$junit_file" "$OUTPUT_DIR/$(basename "$junit_file")"
    copied=$((copied + 1))
  done < <(find "$source_dir" -type f -name 'junit-*.xml' -print0)

  printf '%s\n' "$copied"
}

merge_coverage_files() {
  local source_dir="$1"
  local merge_dir="$OUTPUT_DIR/reportgenerator"
  local coverage_files=()
  local shard_coverage_files=()
  local coverage_file

  while IFS= read -r -d '' coverage_file; do
    shard_coverage_files+=("$coverage_file")
  done < <(find "$source_dir" -type f -name 'coverage.cobertura.xml' ! -path '*/projects/*' -print0)

  if [[ "${#shard_coverage_files[@]}" -gt 0 ]]; then
    coverage_files=("${shard_coverage_files[@]}")
  fi

  while IFS= read -r -d '' coverage_file; do
    if [[ "${#shard_coverage_files[@]}" -eq 0 ]]; then
      coverage_files+=("$coverage_file")
    fi
  done < <(find "$source_dir" -type f -name 'coverage.cobertura.xml' -print0)

  if [[ "${#coverage_files[@]}" -eq 0 ]]; then
    echo "No Cobertura coverage files found under $source_dir" >&2
    exit 1
  fi

  ensure_reportgenerator
  rm -rf "$merge_dir"
  mkdir -p "$merge_dir"

  local reports
  reports="$(join_by_semicolon "${coverage_files[@]}")"

  if ! (cd "$ROOT_DIR" && dotnet tool run reportgenerator -- \
      "-reports:$reports" \
      "-targetdir:$merge_dir" \
      "-reporttypes:Cobertura;TextSummary"); then
    echo "Coverage merge failed" >&2
    exit 1
  fi

  if [[ ! -f "$merge_dir/Cobertura.xml" ]]; then
    echo "ReportGenerator did not create $merge_dir/Cobertura.xml" >&2
    exit 1
  fi

  cp "$merge_dir/Cobertura.xml" "$OUTPUT_DIR/coverage.cobertura.xml"
  if [[ -f "$merge_dir/Summary.txt" ]]; then
    cp "$merge_dir/Summary.txt" "$OUTPUT_DIR/reportgenerator-summary.txt"
  fi
}

coverage_file_count_for_merge() {
  local source_dir="$1"
  local shard_count
  shard_count="$(find "$source_dir" -type f -name 'coverage.cobertura.xml' ! -path '*/projects/*' | wc -l | tr -d ' ')"

  if [[ "$shard_count" -gt 0 ]]; then
    printf '%s\n' "$shard_count"
  else
    find "$source_dir" -type f -name 'coverage.cobertura.xml' | wc -l | tr -d ' '
  fi
}

write_timings() {
  local total_seconds="$1"
  local build_seconds="$2"
  local merge_seconds="$3"
  local junit_count="$4"
  local coverage_count="$5"
  local project_json="${6:-}"

  cat > "$TIMINGS_FILE" <<EOF
{
  "solution": "$(json_escape "$SOLUTION_PATH")",
  "group": "$(json_escape "$GROUP_NAME")",
  "configuration": "$(json_escape "$BUILD_CONFIGURATION")",
  "buildSolution": $BUILD_SOLUTION,
  "durations": {
    "solutionBuildSeconds": $build_seconds,
    "coverageMergeSeconds": $merge_seconds,
    "totalSeconds": $total_seconds
  },
  "artifacts": {
    "junitFiles": $junit_count,
    "coverageFiles": $coverage_count,
    "cobertura": "$(json_escape "$OUTPUT_DIR/coverage.cobertura.xml")"
  },
  "projects": [
$project_json
  ]
}
EOF
}

extract_coverage_attr() {
  local coverage_file="$1"
  local attr="$2"
  tr '\n' ' ' < "$coverage_file" \
    | grep -oE "${attr}=\"[0-9]+\"" \
    | head -n 1 \
    | grep -oE '[0-9]+'
}

write_summary() {
  local coverage_file="$OUTPUT_DIR/coverage.cobertura.xml"

  if [[ ! -f "$coverage_file" ]]; then
    echo "Merged Cobertura file was not created: $coverage_file" >&2
    exit 1
  fi

  local lines_covered lines_valid branches_covered branches_valid
  lines_covered="$(extract_coverage_attr "$coverage_file" "lines-covered")"
  lines_valid="$(extract_coverage_attr "$coverage_file" "lines-valid")"
  branches_covered="$(extract_coverage_attr "$coverage_file" "branches-covered")"
  branches_valid="$(extract_coverage_attr "$coverage_file" "branches-valid")"

  for value_name in lines_covered lines_valid branches_covered branches_valid; do
    value="${!value_name:-}"
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
      echo "Failed to parse numeric coverage attribute '$value_name' from $coverage_file" >&2
      exit 1
    fi
  done

  local line_rate branch_rate
  line_rate="$(awk -v c="$lines_covered" -v v="$lines_valid" 'BEGIN { if (v == 0) printf "0.00"; else printf "%.2f", (c * 100 / v) }')"
  branch_rate="$(awk -v c="$branches_covered" -v v="$branches_valid" 'BEGIN { if (v == 0) printf "0.00"; else printf "%.2f", (c * 100 / v) }')"

  cat > "$OUTPUT_DIR/summary.txt" <<EOF
Solution coverage summary
Group: $GROUP_NAME
Line coverage: $line_rate% ($lines_covered/$lines_valid)
Branch coverage: $branch_rate% ($branches_covered/$branches_valid)
Cobertura: $coverage_file
Timings: $TIMINGS_FILE
EOF

  cat "$OUTPUT_DIR/summary.txt"
}

if [[ "$MERGE_ONLY" == true ]]; then
  start_time="$(seconds_now)"
  if ! MERGE_SOURCE_DIR="$(canonicalize_existing_directory "Merge source" "$MERGE_SOURCE_DIR")"; then
    exit 1
  fi
  rm -f "$OUTPUT_DIR/coverage.cobertura.xml" "$OUTPUT_DIR/summary.txt" "$TIMINGS_FILE" "$OUTPUT_DIR"/junit-*.xml

  merge_start="$(seconds_now)"
  merge_coverage_files "$MERGE_SOURCE_DIR"
  merge_seconds="$(( $(seconds_now) - merge_start ))"
  junit_count="$(copy_junit_files "$MERGE_SOURCE_DIR")"
  coverage_count="$(coverage_file_count_for_merge "$MERGE_SOURCE_DIR")"

  write_summary
  write_timings "$(( $(seconds_now) - start_time ))" 0 "$merge_seconds" "$junit_count" "$coverage_count" ""
  exit 0
fi

rm -f "$OUTPUT_DIR/coverage.cobertura.xml" "$OUTPUT_DIR/summary.txt" "$TIMINGS_FILE" "$OUTPUT_DIR"/junit-*.xml
rm -rf "$OUTPUT_DIR/projects" "$OUTPUT_DIR/reportgenerator"
mkdir -p "$OUTPUT_DIR/projects"

test_projects=()
while IFS= read -r project; do
  if [[ "$GROUP_NAME" == "all" || "$(project_group "$project")" == "$GROUP_NAME" ]]; then
    test_projects+=("$project")
  fi
done < <(
  dotnet sln "$SOLUTION_PATH" list \
    | grep -E '(Tests|IntegrationTests)\.csproj$'
)

if [[ "${#test_projects[@]}" -eq 0 ]]; then
  echo "No test projects found for group '$GROUP_NAME' in $SOLUTION_PATH" >&2
  exit 1
fi

start_time="$(seconds_now)"
build_seconds=0

if [[ "$BUILD_SOLUTION" == true ]]; then
  echo "Building solution..."
  build_start="$(seconds_now)"
  build_args=(dotnet build "$SOLUTION_PATH" --configuration "$BUILD_CONFIGURATION" -v minimal)
  if [[ "$BUILD_NO_RESTORE" == true ]]; then
    build_args+=(--no-restore)
  fi

  if ! "${build_args[@]}"; then
    echo "Build failed for $SOLUTION_PATH" >&2
    exit 1
  fi
  build_seconds="$(( $(seconds_now) - build_start ))"
else
  echo "Skipping solution build; each selected test project will build its own graph."
fi

failures=()
overall_exit=0
project_timings=()

for i in "${!test_projects[@]}"; do
  project_rel="${test_projects[$i]}"
  if [[ "$project_rel" = /* ]]; then
    project_path="$project_rel"
  else
    project_path="$SOLUTION_DIR/$project_rel"
  fi

  slug="$(project_slug "$project_path")"
  project_output_dir="$OUTPUT_DIR/projects/$slug"
  mkdir -p "$project_output_dir"

  echo "[$((i + 1))/${#test_projects[@]}][$GROUP_NAME] dotnet test $project_rel"
  junit_file="$OUTPUT_DIR/junit-$GROUP_NAME-$((i + 1))-$slug.xml"

  args=(
    dotnet test "$project_path"
    --configuration "$BUILD_CONFIGURATION"
    -v minimal
    "--logger:GitHubActions;report-warnings=false"
    "--logger:junit;LogFilePath=$junit_file"
    /p:CollectCoverage=true
    "/p:CoverletOutput=$project_output_dir/coverage"
    "/p:CoverletOutputFormat=cobertura"
    "/p:Include=$INCLUDE_FILTER"
    "/p:Exclude=$EXCLUDE_FILTER"
  )

  if [[ "$BUILD_SOLUTION" == true ]]; then
    args+=(--no-build)
  fi

  if [[ "$BUILD_NO_RESTORE" == true ]]; then
    args+=(--no-restore)
  fi

  project_start="$(seconds_now)"
  project_exit=0
  "${args[@]}" || project_exit=$?
  project_seconds="$(( $(seconds_now) - project_start ))"

  project_timings+=("    { \"project\": \"$(json_escape "$project_rel")\", \"group\": \"$(project_group "$project_rel")\", \"seconds\": $project_seconds, \"exitCode\": $project_exit }")

  if [[ "$project_exit" -ne 0 ]]; then
    overall_exit=$project_exit
    failures+=("$project_rel (exit $project_exit)")
    echo "Test run failed for $project_rel (exit $project_exit)" >&2
  fi
done

merge_start="$(seconds_now)"
merge_coverage_files "$OUTPUT_DIR/projects"
merge_seconds="$(( $(seconds_now) - merge_start ))"

junit_count="$(find "$OUTPUT_DIR" -maxdepth 1 -type f -name 'junit-*.xml' | wc -l | tr -d ' ')"
coverage_count="$(find "$OUTPUT_DIR/projects" -type f -name 'coverage.cobertura.xml' | wc -l | tr -d ' ')"
project_json="$(printf '%s\n' "${project_timings[@]}" | sed '$!s/$/,/')"

write_summary
write_timings "$(( $(seconds_now) - start_time ))" "$build_seconds" "$merge_seconds" "$junit_count" "$coverage_count" "$project_json"

if [[ "${#failures[@]}" -gt 0 ]]; then
  echo >&2
  echo "One or more test projects failed:" >&2
  for failure in "${failures[@]}"; do
    echo "  - $failure" >&2
  done
  exit "$overall_exit"
fi
