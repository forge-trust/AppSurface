#!/bin/bash
# Test-only borrowed timeout, keeping tools in the registered outer Bash group.
set -euo pipefail
exec /usr/bin/timeout --foreground "$@"
