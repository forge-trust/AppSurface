#!/usr/bin/env bash
set -euo pipefail
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$script_directory/verify_config_package_consumer.py" "$@"
