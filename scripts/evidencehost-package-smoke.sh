#!/usr/bin/env bash
# Package preparation and consumer QA only; this script grants no runtime authority.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec python3 -B "$root/.github/issue779-checkpoint4/package-smoke.py" "$@"
