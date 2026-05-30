#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repo_root/examples/web-error-pages/WebErrorPagesExample.csproj"
port="${APP_SURFACE_WEB_ERROR_PAGES_PORT:-61249}"
base_url="http://127.0.0.1:$port"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-web-error-pages.XXXXXX")"
app_log="$work_dir/app.log"
app_pid=""
ready_body="$work_dir/startup.body"
proof_marker="AppSurface Web error-page proof is running."

cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

fail() {
  local name="$1"
  local url="$2"
  local command="$3"
  local expected="$4"
  local actual="$5"
  local body_file="$6"
  local headers_file="$7"
  local likely_cause="${8:-The app did not return the expected proof response.}"

  {
    echo "Proof failed: $name"
    echo "URL: $url"
    echo "Command: $command"
    echo "Expected: $expected"
    echo "Actual: $actual"
    echo "Body: $body_file"
    echo "Headers: $headers_file"
    echo "App log: $app_log"
    echo "Likely cause: $likely_cause"
    echo
    echo "Response headers:"
    sed -n '1,40p' "$headers_file" 2>/dev/null || true
    echo
    echo "Response body:"
    sed -n '1,80p' "$body_file" 2>/dev/null || true
    echo
    echo "App log tail:"
    tail -n 80 "$app_log" 2>/dev/null || true
  } >&2

  exit 1
}

request() {
  local name="$1"
  local method="$2"
  local path="$3"
  local accept="$4"
  shift 4
  local url="$base_url$path"
  local body_file="$work_dir/$name.body"
  local headers_file="$work_dir/$name.headers"
  local status_file="$work_dir/$name.status"
  local display_command="curl -i -sS -X $method -H 'Accept: $accept'"

  if [[ "$#" -gt 0 ]]; then
    display_command+=" $*"
  fi

  display_command+=" '$url'"

  if [[ "$#" -gt 0 ]]; then
    curl -sS -X "$method" -H "Accept: $accept" "$@" -D "$headers_file" -o "$body_file" -w "%{http_code}" "$url" > "$status_file" \
      || fail "$name" "$url" "$display_command" "curl completes without transport failure" "curl failed" "$body_file" "$headers_file" "The app may not be listening, the port may be blocked, or the request command is malformed."
  else
    curl -sS -X "$method" -H "Accept: $accept" -D "$headers_file" -o "$body_file" -w "%{http_code}" "$url" > "$status_file" \
      || fail "$name" "$url" "$display_command" "curl completes without transport failure" "curl failed" "$body_file" "$headers_file" "The app may not be listening, the port may be blocked, or the request command is malformed."
  fi
}

status_for() {
  cat "$work_dir/$1.status"
}

body_for() {
  printf '%s' "$work_dir/$1.body"
}

headers_for() {
  printf '%s' "$work_dir/$1.headers"
}

probe_ready() {
  curl -sS "$base_url/" -o "$ready_body" >/dev/null 2>&1 && grep -Fq "$proof_marker" "$ready_body"
}

assert_status() {
  local name="$1"
  local expected="$2"
  local actual
  actual="$(status_for "$name")"

  if [[ "$actual" != "$expected" ]]; then
    fail "$name" "$3" "$4" "HTTP $expected" "HTTP $actual" "$(body_for "$name")" "$(headers_for "$name")" "$5"
  fi
}

assert_body_contains() {
  local name="$1"
  local needle="$2"
  local description="$3"

  if ! grep -Fq "$needle" "$(body_for "$name")"; then
    fail "$name" "$4" "$5" "$description" "body did not contain '$needle'" "$(body_for "$name")" "$(headers_for "$name")" "$6"
  fi
}

assert_body_not_contains() {
  local name="$1"
  local needle="$2"
  local description="$3"

  if grep -Fq "$needle" "$(body_for "$name")"; then
    fail "$name" "$4" "$5" "$description" "body contained '$needle'" "$(body_for "$name")" "$(headers_for "$name")" "$6"
  fi
}

assert_header_contains() {
  local name="$1"
  local needle="$2"
  local description="$3"

  if ! grep -Fqi "$needle" "$(headers_for "$name")"; then
    fail "$name" "$4" "$5" "$description" "headers did not contain '$needle'" "$(body_for "$name")" "$(headers_for "$name")" "$6"
  fi
}

echo "Starting AppSurface Web error-page proof on $base_url"
ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production \
  dotnet run --project "$project" --no-launch-profile -- --port "$port" --environment Production >"$app_log" 2>&1 &
app_pid="$!"

for _ in {1..80}; do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    fail "startup" "$base_url/" "dotnet run --project '$project' -- --port '$port' --environment Production" "app keeps running" "process exited" "$app_log" "$app_log" "The app failed during startup; inspect the log for restore, build, or port-binding failures."
  fi

  if probe_ready; then
    break
  fi

  sleep 0.25
done

if ! probe_ready; then
  fail "startup" "$base_url/" "curl -sS '$base_url/'" "root response contains '$proof_marker'" "missing proof marker" "$ready_body" "$app_log" "The fixed proof port may already be in use by another process, or the app failed before binding. Set APP_SURFACE_WEB_ERROR_PAGES_PORT to another local port and inspect the app log."
fi

request "empty-401-html" "GET" "/empty-401" "text/html"
assert_status "empty-401-html" "401" "$base_url/empty-401" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-401'" "HTML browser requests should preserve the 401 status."
assert_header_contains "empty-401-html" "content-type: text/html" "text/html response" "$base_url/empty-401" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-401'" "Browser status pages should render HTML for browser requests."
assert_body_contains "empty-401-html" "AppSurface default 401" "AppSurface default 401 marker" "$base_url/empty-401" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-401'" "The conventional browser 401 page did not render."

request "empty-403-html" "GET" "/empty-403" "text/html"
assert_status "empty-403-html" "403" "$base_url/empty-403" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-403'" "HTML browser requests should preserve the 403 status."
assert_body_contains "empty-403-html" "AppSurface default 403" "AppSurface default 403 marker" "$base_url/empty-403" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-403'" "The conventional browser 403 page did not render."

request "empty-404-html" "GET" "/empty-404" "text/html"
assert_status "empty-404-html" "404" "$base_url/empty-404" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-404'" "HTML browser requests should preserve the 404 status."
assert_body_contains "empty-404-html" "AppSurface default 404" "AppSurface default 404 marker" "$base_url/empty-404" "curl -i -sS -H 'Accept: text/html' '$base_url/empty-404'" "The conventional browser 404 page did not render."

request "api-not-found-json" "GET" "/api/not-found" "application/json"
assert_status "api-not-found-json" "404" "$base_url/api/not-found" "curl -i -sS -H 'Accept: application/json' '$base_url/api/not-found'" "API 404 keeps HTTP 404."
assert_header_contains "api-not-found-json" "content-type: application/json" "application/json response" "$base_url/api/not-found" "curl -i -sS -H 'Accept: application/json' '$base_url/api/not-found'" "API clients should not receive browser HTML."
assert_body_contains "api-not-found-json" '"status":404' "JSON 404 payload" "$base_url/api/not-found" "curl -i -sS -H 'Accept: application/json' '$base_url/api/not-found'" "The API route should return its JSON body."
assert_body_not_contains "api-not-found-json" "AppSurface default 404" "no browser status page marker" "$base_url/api/not-found" "curl -i -sS -H 'Accept: application/json' '$base_url/api/not-found'" "API clients should not receive the browser 404 page."

request "throws-html" "GET" "/throws" "text/html"
assert_status "throws-html" "500" "$base_url/throws" "curl -i -sS -H 'Accept: text/html' '$base_url/throws'" "Browser exceptions render a production 500."
assert_body_contains "throws-html" "Something went wrong" "generic production 500 copy" "$base_url/throws" "curl -i -sS -H 'Accept: text/html' '$base_url/throws'" "The conventional exception page did not render."
assert_body_not_contains "throws-html" "synthetic-browser-exception-secret" "no exception message leak" "$base_url/throws" "curl -i -sS -H 'Accept: text/html' '$base_url/throws'" "The production 500 page should not echo exception messages."

request "api-throws-json" "GET" "/api/throws" "application/json"
assert_status "api-throws-json" "500" "$base_url/api/throws" "curl -i -sS -H 'Accept: application/json' '$base_url/api/throws'" "API exceptions keep HTTP 500."
assert_body_not_contains "api-throws-json" "Something went wrong" "no browser exception page copy" "$base_url/api/throws" "curl -i -sS -H 'Accept: application/json' '$base_url/api/throws'" "API clients should not receive browser HTML exception copy."
assert_body_not_contains "api-throws-json" "synthetic-api-exception-secret" "no exception message leak" "$base_url/api/throws" "curl -i -sS -H 'Accept: application/json' '$base_url/api/throws'" "API exception responses should not echo the synthetic exception message."

route_sentinel="synthetic-route-proof-249"
header_sentinel="synthetic-header-proof-249"
cookie_sentinel="synthetic-cookie-proof-249"
form_sentinel="synthetic-form-proof-249"
post_command="curl -i -sS -X POST -H 'Accept: text/html' -H 'X-Proof-Sentinel: $header_sentinel' -H 'Cookie: proof-cookie=$cookie_sentinel' -H 'Content-Type: application/x-www-form-urlencoded' --data 'proof-form=$form_sentinel' '$base_url/throws/$route_sentinel'"
request "post-throws-html" "POST" "/throws/$route_sentinel" "text/html" \
  -H "X-Proof-Sentinel: $header_sentinel" \
  -H "Cookie: proof-cookie=$cookie_sentinel" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  --data "proof-form=$form_sentinel"
assert_status "post-throws-html" "500" "$base_url/throws/$route_sentinel" "$post_command" "POST exceptions render a production 500 for browser-like requests."
assert_body_contains "post-throws-html" "Something went wrong" "generic production 500 copy" "$base_url/throws/$route_sentinel" "$post_command" "The conventional exception page did not render for POST."
for sentinel in "$route_sentinel" "$header_sentinel" "$cookie_sentinel" "$form_sentinel" "synthetic-post-exception-secret"; do
  assert_body_not_contains "post-throws-html" "$sentinel" "sentinel '$sentinel' absent from response body" "$base_url/throws/$route_sentinel" "$post_command" "Sentinel checks cover only the response body, not URLs, logs, shell history, proxies, or telemetry."
done

echo "AppSurface Web error-page proof passed."
echo "Response captures are in $work_dir"
