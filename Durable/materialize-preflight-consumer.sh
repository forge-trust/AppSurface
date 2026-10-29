#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 CONSUMER_PROJECT_TEMPLATE REPOSITORY_ROOT NEW_DESTINATION_DIRECTORY" >&2
  exit 2
fi

consumer_template="$1"
repo_root="$2"
destination_root="$3"

[[ -f "$consumer_template" && ! -L "$consumer_template" ]] || { echo "consumer-template-invalid" >&2; exit 1; }
[[ "$(basename "$consumer_template")" == "PostgreSqlPreflightConsumer.csproj.template" ]] \
  || { echo "consumer-template-name-invalid" >&2; exit 1; }
[[ -f "$repo_root/Directory.Build.props" && ! -L "$repo_root/Directory.Build.props" \
  && -f "$repo_root/Directory.Build.targets" && ! -L "$repo_root/Directory.Build.targets" ]] \
  || { echo "consumer-build-policy-missing" >&2; exit 1; }
[[ ! -e "$destination_root" && ! -L "$destination_root" ]] || { echo "consumer-destination-exists" >&2; exit 1; }

mkdir "$destination_root"
consumer_name="$(basename "$consumer_template" .csproj.template)"
consumer_project="$destination_root/$consumer_name.csproj"
cp -- "$consumer_template" "$consumer_project"
source_count=0
for source_file in "$(dirname "$consumer_template")"/*.cs; do
  [[ -f "$source_file" && ! -L "$source_file" ]] || { echo "consumer-source-invalid" >&2; exit 1; }
  cp -- "$source_file" "$destination_root/"
  source_count=$((source_count + 1))
done
[[ "$source_count" -gt 0 ]] || { echo "consumer-sources-missing" >&2; exit 1; }
cp -- "$repo_root/Directory.Build.props" "$destination_root/Directory.Build.props"
cp -- "$repo_root/Directory.Build.targets" "$destination_root/Directory.Build.targets"
printf '%s\n' "$consumer_project"
