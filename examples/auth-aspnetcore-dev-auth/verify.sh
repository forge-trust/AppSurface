#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repo_root/examples/auth-aspnetcore-dev-auth/AuthAspNetCoreDevAuthExample.csproj"
port="${APP_SURFACE_DEV_AUTH_PORT:-61258}"
base_url="http://127.0.0.1:$port"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-dev-auth.XXXXXX")"
app_log="$work_dir/app.log"
app_pid=""
cookie_name=".AppSurface.DevAuth.Persona"
devauth_cookie=""

cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

fail() {
  echo "DevAuth proof failed: $1" >&2
  echo "App log: $app_log" >&2
  tail -n 80 "$app_log" >&2 || true
  exit 1
}

request() {
  local name="$1"
  local method="$2"
  local path="$3"
  local body_file="$work_dir/$name.body"
  local status_file="$work_dir/$name.status"
  local cookie_header="Cookie:"
  [[ -n "$devauth_cookie" ]] && cookie_header="Cookie: $devauth_cookie"

  curl -sS -X "$method" -L \
    -D "$work_dir/$name.headers" \
    -o "$body_file" \
    -w "%{http_code}" \
    -H "$cookie_header" \
    "$base_url$path" > "$status_file" \
    || fail "$name transport failed"

  local set_cookie
  set_cookie="$(grep -i "^set-cookie: $cookie_name=" "$work_dir/$name.headers" | head -1 | tr -d '\r' || true)"
  if [[ -n "$set_cookie" ]]; then
    devauth_cookie="${set_cookie#*: }"
    devauth_cookie="${devauth_cookie%%;*}"
  fi
}

assert_status() {
  local name="$1"
  local expected="$2"
  local actual
  actual="$(cat "$work_dir/$name.status")"
  [[ "$actual" == "$expected" ]] || fail "$name expected HTTP $expected, got HTTP $actual"
}

assert_body_contains() {
  local name="$1"
  local expected="$2"
  grep -Fq "$expected" "$work_dir/$name.body" || fail "$name body did not contain '$expected'"
}

DOTNET_ENVIRONMENT=Development dotnet run --project "$project" -- --urls "$base_url" > "$app_log" 2>&1 &
app_pid="$!"

for _ in {1..60}; do
  if curl -sS "$base_url/" | grep -Fq "AppSurface DevAuth proof is running."; then
    break
  fi
  sleep 1
done

curl -sS "$base_url/" | grep -Fq "AppSurface DevAuth proof is running." \
  || fail "app did not become ready"

request "control" "GET" "/_appsurface/dev-auth/"
assert_status "control" "200"
assert_body_contains "control" "AppSurface Dev Auth [DEVELOPMENT ONLY]"

request "select-admin" "POST" "/_appsurface/dev-auth/select/admin"
assert_status "select-admin" "200"
assert_body_contains "select-admin" "Local Admin"

request "admin-proof" "GET" "/api/auth-proof"
assert_status "admin-proof" "200"
assert_body_contains "admin-proof" "\"result\":\"allowed\""
assert_body_contains "admin-proof" "\"subject\":\"admin-1\""

request "select-viewer" "POST" "/_appsurface/dev-auth/select/viewer"
assert_status "select-viewer" "200"
assert_body_contains "select-viewer" "Local Viewer"

request "viewer-proof" "GET" "/api/auth-proof"
assert_status "viewer-proof" "403"
assert_body_contains "viewer-proof" "\"appsurfaceAuthOutcome\":\"Forbid\""

request "clear" "POST" "/_appsurface/dev-auth/clear"
assert_status "clear" "200"
assert_body_contains "clear" "Anonymous"

request "anonymous-proof" "GET" "/api/auth-proof"
assert_status "anonymous-proof" "401"
assert_body_contains "anonymous-proof" "\"appsurfaceAuthOutcome\":\"Challenge\""

echo "AppSurface DevAuth proof passed at $base_url"
