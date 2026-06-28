#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
port="${APP_SURFACE_WEB_PWA_PORT:-5055}"
project="$repo_root/examples/web-pwa-install/WebPwaInstallExample.csproj"
cli_project="$repo_root/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
log_file="$(mktemp)"

cleanup() {
  if [[ -n "${app_pid:-}" ]]; then
    kill "$app_pid" >/dev/null 2>&1 || true
    wait "$app_pid" >/dev/null 2>&1 || true
  fi
  rm -f "$log_file"
}
trap cleanup EXIT

dotnet run --project "$project" -- --environment Development --port "$port" >"$log_file" 2>&1 &
app_pid=$!

ready=0
for _ in {1..120}; do
  if curl -fsS "http://127.0.0.1:$port/manifest.webmanifest" >/dev/null 2>&1; then
    ready=1
    break
  fi
  if ! kill -0 "$app_pid" >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
done

if [[ "$ready" != "1" ]]; then
  cat "$log_file" >&2
  exit 1
fi

curl -fsS "http://127.0.0.1:$port/_appsurface/pwa/status.json" >/dev/null
dotnet run --project "$cli_project" -- pwa verify --url "http://127.0.0.1:$port"
