#!/usr/bin/env bash
set -uo pipefail

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

printf '| Journey | Run | Started (UTC) | Elapsed seconds | Result |\n'
printf '| --- | ---: | --- | ---: | --- |\n'

overall=0
for ((run = 1; run <= RUNS; run++)); do
  started_at="$(date -u '+%Y-%m-%dT%H:%M:%SZ')"
  started_seconds="$(date +%s)"
  if "$@" >&2; then
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
