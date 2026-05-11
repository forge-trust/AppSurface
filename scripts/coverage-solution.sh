#!/usr/bin/env bash
set -uo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOLUTION_PATH="${1:-$ROOT_DIR/ForgeTrust.AppSurface.slnx}"
OUTPUT_DIR="${2:-$ROOT_DIR/TestResults/coverage-merged}"
INCLUDE_FILTER="${INCLUDE_FILTER:-[ForgeTrust.AppSurface.*]*}"
EXCLUDE_FILTER="${EXCLUDE_FILTER:-[*.Tests]*,[*.IntegrationTests]*}"
EXCLUDE_FILTER="${EXCLUDE_FILTER//,/%2c}"

if [[ ! -f "$SOLUTION_PATH" ]]; then
  echo "Solution not found: $SOLUTION_PATH" >&2
  exit 1
fi

SOLUTION_DIR="$(cd "$(dirname "$SOLUTION_PATH")" && pwd)"

mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR/coverage.json" "$OUTPUT_DIR/coverage.cobertura.xml" "$OUTPUT_DIR/summary.txt"

test_projects=()
while IFS= read -r project; do
  test_projects+=("$project")
done < <(
  dotnet sln "$SOLUTION_PATH" list \
    | grep -E '(Tests|IntegrationTests)\.csproj$'
)

if [[ "${#test_projects[@]}" -eq 0 ]]; then
  echo "No test projects found in $SOLUTION_PATH" >&2
  exit 1
fi

echo "Building solution..."
if ! dotnet build "$SOLUTION_PATH" -v minimal; then
  echo "Build failed for $SOLUTION_PATH" >&2
  exit 1
fi

failures=()
overall_exit=0

for i in "${!test_projects[@]}"; do
  project_rel="${test_projects[$i]}"
  if [[ "$project_rel" = /* ]]; then
    project_path="$project_rel"
  else
    project_path="$SOLUTION_DIR/$project_rel"
  fi
  echo "[$((i + 1))/${#test_projects[@]}] dotnet test $project_rel"

  args=(
    dotnet test "$project_path"
    --no-build
    -v minimal
    /p:CollectCoverage=true
    "/p:CoverletOutput=$OUTPUT_DIR/coverage"
    "/p:CoverletOutputFormat=json%2ccobertura"
    "/p:Include=$INCLUDE_FILTER"
    "/p:Exclude=$EXCLUDE_FILTER"
  )

  if [[ -f "$OUTPUT_DIR/coverage.json" ]]; then
    args+=("/p:MergeWith=$OUTPUT_DIR/coverage.json")
  fi

  project_exit=0
  "${args[@]}" || project_exit=$?
  if [[ "$project_exit" -ne 0 ]]; then
    overall_exit=$project_exit
    failures+=("$project_rel (exit $project_exit)")
    echo "Test run failed for $project_rel (exit $project_exit)" >&2
  fi
done

coverage_file="$OUTPUT_DIR/coverage.cobertura.xml"
if [[ ! -f "$coverage_file" ]]; then
  if [[ "${#failures[@]}" -gt 0 ]]; then
    echo "One or more test projects failed:" >&2
    for failure in "${failures[@]}"; do
      echo "  - $failure" >&2
    done
    echo >&2
    echo "Merged Cobertura file was not created: $coverage_file" >&2
    exit "$overall_exit"
  fi

  echo "Merged Cobertura file was not created: $coverage_file" >&2
  exit 1
fi

# Extract a numeric attribute value from the Cobertura <coverage> element.
extract_coverage_attr() {
  local attr="$1"
  tr '\n' ' ' < "$coverage_file" \
    | grep -oE "${attr}=\"[0-9]+\"" \
    | head -n 1 \
    | grep -oE '[0-9]+'
}

lines_covered="$(extract_coverage_attr "lines-covered")"
lines_valid="$(extract_coverage_attr "lines-valid")"
branches_covered="$(extract_coverage_attr "branches-covered")"
branches_valid="$(extract_coverage_attr "branches-valid")"

for value_name in lines_covered lines_valid branches_covered branches_valid; do
  value="${!value_name:-}"
  if [[ ! "$value" =~ ^[0-9]+$ ]]; then
    echo "Failed to parse numeric coverage attribute '$value_name' from $coverage_file" >&2
    exit 1
  fi
done

line_rate="$(awk -v c="$lines_covered" -v v="$lines_valid" 'BEGIN { if (v == 0) printf "0.00"; else printf "%.2f", (c * 100 / v) }')"
branch_rate="$(awk -v c="$branches_covered" -v v="$branches_valid" 'BEGIN { if (v == 0) printf "0.00"; else printf "%.2f", (c * 100 / v) }')"

cat > "$OUTPUT_DIR/summary.txt" <<EOF
Solution coverage summary
Line coverage: $line_rate% ($lines_covered/$lines_valid)
Branch coverage: $branch_rate% ($branches_covered/$branches_valid)
Cobertura: $coverage_file
EOF

cat "$OUTPUT_DIR/summary.txt"

if [[ "${#failures[@]}" -gt 0 ]]; then
  echo >&2
  echo "One or more test projects failed:" >&2
  for failure in "${failures[@]}"; do
    echo "  - $failure" >&2
  done
  exit "$overall_exit"
fi
