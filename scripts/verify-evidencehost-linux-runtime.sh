#!/usr/bin/env bash
# Candidate-only production consumer proof. This produces Observation receipts, never Trust acceptance.
set -euo pipefail

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s /absolute/fresh/output-parent\n' "$0" >&2
    exit 2
fi
if [[ -z "${EVIDENCEHOST_OUTPUT_PARENT:-}" || "$1" != "$EVIDENCEHOST_OUTPUT_PARENT" ]]; then
    printf 'Pass EVIDENCEHOST_OUTPUT_PARENT as the sole argument.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec python3 -B "$repo_root/tests/evidencehost-consumer/runtime-proof.py" "$1"
