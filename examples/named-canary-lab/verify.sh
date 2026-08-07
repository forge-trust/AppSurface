#!/usr/bin/env bash
set -euo pipefail
set +x

# This development-only verifier starts the sample, triggers its application-owned
# workflow, and invokes the source CLI. It intentionally never prints the local
# credential, marker, response body, marker fingerprint, or application log.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
lab_project="$repo_root/examples/named-canary-lab/NamedCanaryLab.csproj"
cli_project="$repo_root/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
scenario="${1:-pass}"
port="${NAMED_CANARY_LAB_PORT:-61260}"
base_url="http://127.0.0.1:$port"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/named-canary-lab.XXXXXX")"
app_log="$work_dir/app.log"
app_pid=""
operator_token="local-operator-$(date +%s)-$$"
marker="local-marker-$(date +%s)-$$"

case "$scenario" in
  pass) expected_exit=0; configured_scenario="Pass"; timeout="5s" ;;
  pending) expected_exit=6; configured_scenario="Pending"; timeout="1s" ;;
  stale) expected_exit=3; configured_scenario="Stale"; timeout="5s" ;;
  *)
    echo "Usage: $0 [pass|pending|stale]" >&2
    exit 2
    ;;
esac

cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  rm -rf "$work_dir"
}
trap cleanup EXIT INT TERM

dotnet build "$lab_project" --nologo
dotnet build "$cli_project" --nologo

ASPNETCORE_ENVIRONMENT=Development \
NamedCanaryLab__OperatorToken="$operator_token" \
NamedCanaryLab__Candidate="local-candidate" \
NamedCanaryLab__Environment="development" \
NamedCanaryLab__Scenario="$configured_scenario" \
dotnet run --project "$lab_project" --no-build --no-launch-profile -- --port "$port" >"$app_log" 2>&1 &
app_pid="$!"

for ((attempt = 0; attempt < 1200; attempt++)); do
  if curl --silent --show-error --fail "$base_url/" >/dev/null 2>&1; then
    break
  fi

  if ! kill -0 "$app_pid" 2>/dev/null; then
    echo "The named-canary lab did not start. Inspect its local log before sharing it." >&2
    exit 3
  fi

  sleep 0.1
done

if ! curl --silent --show-error --fail "$base_url/" >/dev/null 2>&1; then
  echo "The named-canary lab did not become reachable before the local deadline." >&2
  exit 3
fi

fresh_since="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
curl --silent --show-error --fail \
  --request POST \
  --config - \
  --output /dev/null \
  "$base_url/lab/canary/trigger" <<EOF
header = "Authorization: Bearer $operator_token"
header = "X-AppSurface-Canary-Marker: $marker"
EOF

set +e
APPSURFACE_CANARY_TOKEN="$operator_token" \
APPSURFACE_CANARY_MARKER="$marker" \
dotnet run --project "$cli_project" --no-build --no-launch-profile -- \
  canary poll \
  --url "$base_url" \
  --name lab.proof \
  --bearer-token-env APPSURFACE_CANARY_TOKEN \
  --marker-env APPSURFACE_CANARY_MARKER \
  --fresh-since "$fresh_since" \
  --timeout "$timeout" \
  --interval 100ms \
  --no-github-summary
actual_exit=$?
set -e

if [[ "$actual_exit" -ne "$expected_exit" ]]; then
  echo "The verifier received an unexpected terminal exit code." >&2
  exit 4
fi

echo "Named-canary lab '$scenario' scenario verified safely."
