#!/usr/bin/env bash
set -euo pipefail
[[ $# == 4 && "$1" == --execute ]] || { echo 'build-invocation-rejected' >&2; exit 1; }
exec /usr/bin/bash "$(dirname -- "$0")/build-fdd.sh" "$@" N14
