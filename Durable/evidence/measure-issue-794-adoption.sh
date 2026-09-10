#!/usr/bin/env bash
set -uo pipefail
set -m

usage() {
  printf 'Usage: %s LABEL RUNS -- COMMAND [ARG ...]\n' "$0" >&2
}

if [[ "$#" -lt 4 || "$3" != "--" || ! "$2" =~ ^[1-9][0-9]*$ ]]; then
  usage
  exit 2
fi

LABEL="$1"
RUNS="$2"
shift 3

if [[ -z "${APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS:-}" ]]; then
  APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS=420
fi
if [[ ! "$APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS" =~ ^[1-9][0-9]*$ ]]; then
  printf 'APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS must be a positive integer.\n' >&2
  exit 2
fi

timeout_marker="$(mktemp -t appsurface-durable-measurement-timeout.XXXXXX)"
command_pid=""
watchdog_pid=""
cleanup() {
  terminate_process_group "$command_pid"
  terminate_process_group "$watchdog_pid"
  rm -f "$timeout_marker"
}
interrupt() {
  cleanup
  exit 130
}
signal_process_group() {
  local signal="$1"
  local pid="$2"
  if [[ -z "$pid" ]]; then
    return
  fi

  kill "$signal" -- "-$pid" >/dev/null 2>&1 || kill "$signal" "$pid" >/dev/null 2>&1 || true
}
process_group_is_alive() {
  local pid="$1"
  kill -0 -- "-$pid" >/dev/null 2>&1 || kill -0 "$pid" >/dev/null 2>&1
}
terminate_process_group() {
  local pid="$1"
  if [[ -z "$pid" ]]; then
    return
  fi

  signal_process_group -TERM "$pid"
  for _ in {1..4}; do
    if ! process_group_is_alive "$pid"; then
      return
    fi
    sleep 0.05
  done
  signal_process_group -KILL "$pid"
}
trap cleanup EXIT
trap interrupt INT TERM

printf '| Journey | Run | Started (UTC) | Elapsed seconds | Result |\n'
printf '| --- | ---: | --- | ---: | --- |\n'

overall=0
for ((run = 1; run <= RUNS; run++)); do
  started_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  started_seconds="$(date +%s)"
  : > "$timeout_marker"
  ("$@") >&2 &
  command_pid="$!"
  (
    sleep "$APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS"
    printf 'Measurement `%s` run %s exceeded its %s-second deadline.\n' \
      "$LABEL" "$run" "$APPSURFACE_DURABLE_MEASUREMENT_TIMEOUT_SECONDS" >&2
    printf 'timeout' > "$timeout_marker"
    terminate_process_group "$command_pid"
  ) &
  watchdog_pid="$!"

  command_exit=0
  wait "$command_pid" 2>/dev/null || command_exit="$?"
  command_pid=""
  terminate_process_group "$watchdog_pid"
  wait "$watchdog_pid" 2>/dev/null || true
  watchdog_pid=""

  if [[ -s "$timeout_marker" ]]; then
    result="timeout"
    overall=1
  elif [[ "$command_exit" == 0 ]]; then
    result="pass"
  else
    result="fail"
    overall=1
  fi
  elapsed_seconds="$(( $(date +%s) - started_seconds ))"
  printf '| `%s` | %s | `%s` | %s | %s |\n' \
    "$LABEL" "$run" "$started_at" "$elapsed_seconds" "$result"
done

exit "$overall"
