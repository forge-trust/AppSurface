#!/usr/bin/env bash
set -euo pipefail

# Exit codes are intentionally script-internal: 2 preflight, 3 build/launch,
# 4 readiness, 5 HTTP proof, and 6 cleanup. INT and TERM use 130 and 143 only
# after cleanup succeeds; exhausted cleanup retains exit 6 and failure evidence.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="$repo_root/examples/auth-aspnetcore-dev-auth/AuthAspNetCoreDevAuthExample.csproj"
app_dll="$repo_root/examples/auth-aspnetcore-dev-auth/bin/Release/net10.0/AuthAspNetCoreDevAuthExample.dll"
port="${APP_SURFACE_DEV_AUTH_PORT:-61258}"
ready_timeout="${APP_SURFACE_DEV_AUTH_READY_TIMEOUT_SECONDS:-60}"
base_url=""
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-dev-auth.XXXXXX")"
app_log="$work_dir/app.log"
cookie_jar="$work_dir/cookies.txt"
app_pid=""
summary_pid=""
child_status=""
current_stage="PREFLIGHT"
failure_reason=""
failure_artifact_dir=""
started_at="$(date +%s)"
listen_elapsed=""
cleanup_state=0

: > "$app_log"
: > "$cookie_jar"

elapsed_seconds() {
  local now
  now="$(date +%s)"
  echo $((now - started_at))
}

signal_child() {
  # Use Bash's builtin directly so BASH_ENV functions and PATH shims cannot
  # intercept lifecycle signals in a normal verifier invocation.
  builtin kill "$@"
}

sanitize_log() {
  local safe_log="$work_dir/safe-app.log"

  # Retain only fixed hosting diagnostics. Arbitrary application text is replaced,
  # not denylist-redacted, so an unknown secret format cannot enter evidence.
  tail -n 80 "$app_log" 2>/dev/null \
    | awk -v configured_url="$base_url" '
      index($0, "Now listening on: " configured_url) {
        print "Now listening on: " configured_url
        next
      }
      tolower($0) ~ /address already in use/ {
        print "Kestrel bind failure: address already in use"
        next
      }
      $0 ~ /^[[:space:]]*(trace|debug|info|warn|fail|crit):[[:space:]]*[A-Za-z0-9_.+`-]+(\[[0-9]+\])?[[:space:]]*$/ {
        line = $0
        sub(/^[[:space:]]*/, "", line)
        sub(/:.*/, ": [REDACTED CATEGORY]", line)
        print line
        next
      }
      $0 ~ /^[[:space:]]*[A-Za-z0-9_.+`-]+Exception(:.*)?$/ {
        print "[REDACTED EXCEPTION TYPE]: [REDACTED]"
        next
      }
      $0 ~ /^[[:space:]]*at[[:space:]]/ {
        print "   at [REDACTED STACK FRAME]"
        next
      }
      $0 ~ /^[[:space:]]*--- End of inner exception stack trace ---[[:space:]]*$/ {
        print "--- End of inner exception stack trace ---"
        next
      }
      $0 ~ /^[[:space:]]*$/ {
        print ""
        next
      }
      { print "[REDACTED]" }
    ' > "$safe_log"

  # Materialize before applying the byte cap. Piping directly into head under
  # pipefail can turn a valid verifier failure into SIGPIPE status 141.
  head -c 32768 "$safe_log"
}

preserve_failure_evidence() {
  local elapsed
  elapsed="$(elapsed_seconds)"
  failure_artifact_dir="$(mktemp -d "${TMPDIR:-/tmp}/appsurface-dev-auth-failure.XXXXXX")"

  {
    echo "stage=$current_stage"
    echo "reason=$failure_reason"
    echo "elapsed_seconds=$elapsed"
    [[ -n "$summary_pid" ]] && echo "pid=$summary_pid"
    [[ -n "$base_url" ]] && echo "url=$base_url"
    [[ -n "$child_status" ]] && echo "child_exit_status=$child_status"
    echo "artifact_directory=$failure_artifact_dir"
  } > "$failure_artifact_dir/stage-summary.txt"

  sanitize_log > "$failure_artifact_dir/application-log-tail.txt"
  rm -rf "$work_dir"

  echo "Safe failure evidence: $failure_artifact_dir" >&2
  cat "$failure_artifact_dir/application-log-tail.txt" >&2
}

reap_child() {
  if [[ -z "$app_pid" ]]; then
    return 0
  fi

  summary_pid="$app_pid"
  if wait "$app_pid" 2>/dev/null; then
    child_status=0
  else
    child_status=$?
  fi
  app_pid=""
  return 0
}

cleanup_child() {
  local waited=0
  local term_delivered=1

  if [[ "$cleanup_state" -eq 2 ]]; then
    return 0
  fi
  if [[ "$cleanup_state" -eq 3 ]]; then
    return 6
  fi
  if [[ "$cleanup_state" -eq 1 ]]; then
    return 6
  fi
  cleanup_state=1

  if [[ -z "$app_pid" ]]; then
    cleanup_state=2
    return 0
  fi

  summary_pid="$app_pid"
  if ! signal_child -0 "$app_pid" 2>/dev/null; then
    echo "[stage=CLEANUP reason=CHILD_ALREADY_EXITED] The direct child exited before cleanup; reaping pid=$app_pid." >&2
    reap_child
    cleanup_state=2
    return 0
  fi

  if ! signal_child -TERM "$app_pid" 2>/dev/null && signal_child -0 "$app_pid" 2>/dev/null; then
    term_delivered=0
    echo "[stage=CLEANUP reason=TERM_DELIVERY_FAILED_ESCALATED_TO_KILL] SIGTERM delivery failed; sending SIGKILL pid=$app_pid." >&2
  fi
  while [[ "$term_delivered" -eq 1 ]] && signal_child -0 "$app_pid" 2>/dev/null && [[ "$waited" -lt 5 ]]; do
    sleep 1
    waited=$((waited + 1))
  done

  if signal_child -0 "$app_pid" 2>/dev/null; then
    echo "[stage=CLEANUP reason=TERM_ESCALATED_TO_KILL] Child ignored SIGTERM; sending SIGKILL pid=$app_pid." >&2
    if ! signal_child -KILL "$app_pid" 2>/dev/null && signal_child -0 "$app_pid" 2>/dev/null; then
      cleanup_state=0
      return 6
    fi

    waited=0
    while signal_child -0 "$app_pid" 2>/dev/null && [[ "$waited" -lt 5 ]]; do
      sleep 1
      waited=$((waited + 1))
    done
    if signal_child -0 "$app_pid" 2>/dev/null; then
      cleanup_state=0
      return 6
    fi
  fi

  reap_child
  cleanup_state=2
  return 0
}

cleanup_child_with_retry() {
  if cleanup_child; then
    return 0
  fi

  echo "[stage=CLEANUP reason=RETRYING_REAP] The first cleanup attempt failed; retrying once." >&2
  if cleanup_child; then
    return 0
  fi

  cleanup_state=3
  return 6
}

on_exit() {
  local status=$?
  trap - EXIT
  trap '' INT TERM

  if [[ "$cleanup_state" -eq 3 ]]; then
    status=6
    current_stage="CLEANUP"
    failure_reason="REAP_FAILED"
  elif ! cleanup_child_with_retry; then
    status=6
    current_stage="CLEANUP"
    failure_reason="REAP_FAILED"
    echo "[stage=CLEANUP reason=REAP_FAILED] Both cleanup attempts failed; the recorded child PID may still be alive." >&2
  fi

  if [[ "$status" -eq 0 ]]; then
    rm -rf "$work_dir"
  else
    [[ -n "$failure_reason" ]] || failure_reason="CHILD_EXITED"
    preserve_failure_evidence
  fi

  exit "$status"
}

on_int() {
  trap '' INT TERM
  trap - EXIT
  echo "[stage=CLEANUP] Received INT; stopping the direct child." >&2
  if ! cleanup_child_with_retry; then
    current_stage="CLEANUP"
    failure_reason="REAP_FAILED"
    echo "[stage=CLEANUP reason=REAP_FAILED] Both cleanup attempts failed; the recorded child PID may still be alive." >&2
    preserve_failure_evidence
    exit 6
  fi
  current_stage="CLEANUP"
  failure_reason="INTERRUPTED_INT"
  preserve_failure_evidence
  exit 130
}

on_term() {
  trap '' INT TERM
  trap - EXIT
  echo "[stage=CLEANUP] Received TERM; stopping the direct child." >&2
  if ! cleanup_child_with_retry; then
    current_stage="CLEANUP"
    failure_reason="REAP_FAILED"
    echo "[stage=CLEANUP reason=REAP_FAILED] Both cleanup attempts failed; the recorded child PID may still be alive." >&2
    preserve_failure_evidence
    exit 6
  fi
  current_stage="CLEANUP"
  failure_reason="INTERRUPTED_TERM"
  preserve_failure_evidence
  exit 143
}

trap on_exit EXIT
trap on_int INT
trap on_term TERM

fail() {
  local stage="$1"
  local reason="$2"
  local code="$3"
  local problem="$4"
  local cause="$5"
  local fix="$6"

  current_stage="$stage"
  failure_reason="$reason"
  echo "[stage=$stage reason=$reason] $problem Cause: $cause Fix: $fix" >&2
  exit "$code"
}

validate_decimal_range() {
  local value="$1"
  local minimum="$2"
  local maximum="$3"
  local normalized

  [[ "$value" =~ ^[0-9]+$ ]] || return 1
  normalized="$(normalize_decimal "$value")"
  [[ "${#normalized}" -le "${#maximum}" ]] || return 1
  [[ "$normalized" -ge "$minimum" && "$normalized" -le "$maximum" ]] 2>/dev/null
}

normalize_decimal() {
  local value="$1"
  local normalized

  normalized="$(printf '%s' "$value" | sed 's/^0*//')"
  [[ -n "$normalized" ]] || normalized=0
  printf '%s' "$normalized"
}

request() {
  local name="$1"
  local method="$2"
  local path="$3"
  local body_file="$work_dir/$name.body"
  local status_file="$work_dir/$name.status"

  # Redirects are intentionally not followed. HTTP cookies are not port-scoped,
  # so even a private jar could forward the DevAuth persona to another listener
  # on 127.0.0.1 after a cross-port redirect.
  if ! curl --disable -sS -X "$method" \
    --no-location \
    --noproxy '*' \
    --connect-timeout 5 \
    --max-time 15 \
    -D "$work_dir/$name.headers" \
    -o "$body_file" \
    -w "%{http_code}" \
    --cookie "$cookie_jar" \
    --cookie-jar "$cookie_jar" \
    "$base_url$path" > "$status_file"; then
    fail "HTTP_PROOF" "HTTP_TRANSPORT_FAILED" 5 "The $name request could not reach the verifier child." "The loopback HTTP request failed after Kestrel reported the configured address." "Inspect the safe application-log tail and rerun the verifier."
  fi
}

assert_status() {
  local name="$1"
  local expected="$2"
  local actual
  actual="$(cat "$work_dir/$name.status")"
  if [[ "$actual" != "$expected" ]]; then
    fail "HTTP_PROOF" "HTTP_CONTRACT_FAILED" 5 "The $name assertion failed." "The endpoint returned an unexpected HTTP status." "Inspect the named stage and application behavior without printing the response payload."
  fi
}

assert_body_contains() {
  local name="$1"
  local expected="$2"
  if ! grep -Fq "$expected" "$work_dir/$name.body"; then
    fail "HTTP_PROOF" "HTTP_CONTRACT_FAILED" 5 "The $name assertion failed." "The expected response marker was absent." "Inspect the named endpoint contract without printing the response payload."
  fi
}

assert_body_order() {
  local name="$1"
  shift
  local previous=0
  local expected
  for expected in "$@"; do
    local line
    line="$(grep -nF -m1 "$expected" "$work_dir/$name.body" | cut -d: -f1 || true)"
    if [[ -z "$line" ]] || ((line <= previous)); then
      fail "HTTP_PROOF" "HTTP_CONTRACT_FAILED" 5 "The $name layout assertion failed." "The expected marker was absent or out of order." "Inspect the responsive marker layout contract."
    fi
    previous="$line"
  done
}

for required_command in dotnet curl; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    fail "PREFLIGHT" "MISSING_COMMAND" 2 "Required command '$required_command' was not found." "The verifier prerequisite is unavailable on PATH." "Install $required_command and rerun the verifier."
  fi
done

if ! validate_decimal_range "$port" 1 65535; then
  fail "PREFLIGHT" "INVALID_PORT" 2 "APP_SURFACE_DEV_AUTH_PORT is invalid." "The value must be a decimal integer from 1 through 65535." "Set APP_SURFACE_DEV_AUTH_PORT to an available loopback port in that range."
fi

if ! validate_decimal_range "$ready_timeout" 1 300; then
  fail "PREFLIGHT" "INVALID_TIMEOUT" 2 "APP_SURFACE_DEV_AUTH_READY_TIMEOUT_SECONDS is invalid." "The value must be a decimal integer from 1 through 300." "Set the timeout to a value in that range."
fi

port="$(normalize_decimal "$port")"
ready_timeout="$(normalize_decimal "$ready_timeout")"
base_url="http://127.0.0.1:$port"
echo "[stage=PREFLIGHT] DevAuth real-socket verifier url=$base_url"

current_stage="BUILD"
echo "[stage=BUILD] Building DevAuth example configuration=Release"
if dotnet build "$project" --configuration Release --nologo; then
  :
else
  build_status=$?
  fail "BUILD" "BUILD_FAILED" 3 "The DevAuth example build failed with status $build_status; readiness was not attempted." "The compiler or restore command returned nonzero." "Resolve the build output above and rerun the verifier."
fi

current_stage="LAUNCH"
DOTNET_ENVIRONMENT=Development \
DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false \
  dotnet "$app_dll" --urls "$base_url" > "$app_log" 2>&1 &
app_pid="$!"
summary_pid="$app_pid"
echo "[stage=LAUNCH] Started pid=$app_pid url=$base_url"

if ! signal_child -0 "$app_pid" 2>/dev/null; then
  reap_child
  fail "LAUNCH" "CHILD_EXITED" 3 "The DevAuth child exited before readiness began with status $child_status." "The compiled application could not remain running." "Inspect the safe application-log tail for the launch or bind diagnostic."
fi

current_stage="READINESS"
echo "[stage=READINESS] Waiting for child-owned Kestrel evidence"
readiness_started="$(date +%s)"
last_progress=0
while true; do
  if ! signal_child -0 "$app_pid" 2>/dev/null; then
    reap_child
    fail "READINESS" "CHILD_EXITED" 3 "The DevAuth child exited before binding with status $child_status." "Kestrel did not remain alive through readiness." "Inspect the safe application-log tail for the startup or bind diagnostic."
  fi

  if grep -Fq "Now listening on: $base_url" "$app_log"; then
    if ! signal_child -0 "$app_pid" 2>/dev/null; then
      reap_child
      fail "READINESS" "CHILD_EXITED" 3 "The DevAuth child exited after reporting its address with status $child_status." "The owned Kestrel process died before HTTP proof." "Inspect the safe application-log tail and rerun."
    fi
    listen_elapsed=$(( $(date +%s) - readiness_started ))
    break
  fi

  ready_elapsed=$(( $(date +%s) - readiness_started ))
  if [[ "$ready_elapsed" -ge "$ready_timeout" ]]; then
    fail "READINESS" "READINESS_TIMEOUT" 4 "The child remains alive but has not reported its configured address; elapsed=${ready_elapsed}s timeout=${ready_timeout}s pid=$app_pid url=$base_url." "No child-owned Kestrel listening record was observed." "Inspect the safe log tail; restricted file-watcher environments may require the child-scoped reload control used by this verifier."
  fi
  if [[ "$ready_elapsed" -ge 5 && "$ready_elapsed" -ge $((last_progress + 5)) ]]; then
    echo "[stage=READINESS] Still waiting elapsed=${ready_elapsed}s timeout=${ready_timeout}s pid=$app_pid"
    last_progress="$ready_elapsed"
  fi
  sleep 1
done

current_stage="HTTP_PROOF"
request "root" "GET" "/"
assert_status "root" "200"
assert_body_contains "root" "AppSurface DevAuth proof is running."
assert_body_contains "root" '<meta name="viewport" content="width=device-width, initial-scale=1">'
assert_body_contains "root" '@media(max-width:640px)'
assert_body_contains "root" 'position:static'
assert_body_contains "root" '.demo-dev-auth { margin: 12px 16px; }'
assert_body_order "root" '<header>AppSurface local proof</header>' 'aria-label="AppSurface development authentication state"' '<main>'
echo "[stage=HTTP_PROOF] Root marker passed"

request "control" "GET" "/_appsurface/dev-auth/"
assert_status "control" "200"
assert_body_contains "control" "AppSurface Dev Auth [FAKE LOCAL AUTH]"
echo "[stage=HTTP_PROOF] DevAuth control page passed"

request "select-admin" "POST" "/_appsurface/dev-auth/select/admin"
assert_status "select-admin" "200"
assert_body_contains "select-admin" "Local Admin"
request "admin-proof" "GET" "/api/auth-proof"
assert_status "admin-proof" "200"
assert_body_contains "admin-proof" '"result":"allowed"'
assert_body_contains "admin-proof" '"subject":"admin-1"'
request "admin-status" "GET" "/_appsurface/dev-auth/status"
assert_status "admin-status" "200"
assert_body_contains "admin-status" '"enabled":true'
assert_body_contains "admin-status" '"environment":"Development"'
assert_body_contains "admin-status" '"scheme":"AppSurface.DevAuth"'
assert_body_contains "admin-status" '"pathPrefix":"/_appsurface/dev-auth"'
assert_body_contains "admin-status" '"personaId":"admin"'
assert_body_contains "admin-status" '"displayName":"Local Admin"'
assert_body_contains "admin-status" '"subject":"admin-1"'
assert_body_contains "admin-status" '"isAnonymous":false'
assert_body_contains "admin-status" '"warnings":[]'
echo "[stage=HTTP_PROOF] Admin persona, protected proof, and selected status passed"

request "select-viewer" "POST" "/_appsurface/dev-auth/select/viewer"
assert_status "select-viewer" "200"
assert_body_contains "select-viewer" "Local Viewer"
request "viewer-proof" "GET" "/api/auth-proof"
assert_status "viewer-proof" "403"
assert_body_contains "viewer-proof" '"appsurfaceAuthOutcome":"Forbid"'
echo "[stage=HTTP_PROOF] Viewer forbid passed"

request "clear" "POST" "/_appsurface/dev-auth/clear"
assert_status "clear" "200"
assert_body_contains "clear" "Anonymous"
request "anonymous-status" "GET" "/_appsurface/dev-auth/status"
assert_status "anonymous-status" "200"
assert_body_contains "anonymous-status" '"personaId":null'
assert_body_contains "anonymous-status" '"displayName":null'
assert_body_contains "anonymous-status" '"subject":null'
assert_body_contains "anonymous-status" '"isAnonymous":true'
request "anonymous-proof" "GET" "/api/auth-proof"
assert_status "anonymous-proof" "401"
assert_body_contains "anonymous-proof" '"appsurfaceAuthOutcome":"Challenge"'
echo "[stage=HTTP_PROOF] Clear, anonymous status, and challenge passed"

trap '' INT TERM
if ! cleanup_child_with_retry; then
  fail "CLEANUP" "REAP_FAILED" 6 "The verifier child could not be reaped." "Cleanup did not finish for the recorded direct child." "Inspect the recorded PID before rerunning."
fi

total_elapsed="$(elapsed_seconds)"
echo "[stage=COMPLETE reason=PASSED] AppSurface DevAuth real-socket proof passed url=$base_url time_to_listen=${listen_elapsed}s total=${total_elapsed}s"
