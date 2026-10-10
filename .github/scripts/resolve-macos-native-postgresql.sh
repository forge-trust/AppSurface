#!/usr/bin/env bash
# Called by durable-template-evidence.yml within its 30-minute native job bound.
# Print only the complete installed PostgreSQL 16 bin directory to stdout; keep
# acquisition diagnostics on stderr. No PostgreSQL service or cluster is started.
# Homebrew --prefix alone also succeeds for an uninstalled formula:
# https://docs.brew.sh/Manpage
set -euo pipefail

if ! prefix="$(brew --prefix --installed postgresql@16)"; then
  brew install postgresql@16 >&2
  prefix="$(brew --prefix --installed postgresql@16)"
fi

if [[ -z "$prefix" || "$prefix" != /* || "$prefix" == *$'\n'* || "$prefix" == *$'\r'* ]]; then
  echo 'Native PostgreSQL installed prefix is missing or invalid.' >&2
  exit 1
fi

# Homebrew's opt prefix is a symlink. Match the physical executable path used
# by the native verifier's postmaster ownership check.
bin="$(cd -- "$prefix/bin" && pwd -P)"
if [[ -z "$bin" || "$bin" != /* || "$bin" == *$'\n'* || "$bin" == *$'\r'* ]]; then
  echo 'Native PostgreSQL resolved bin directory is missing or invalid.' >&2
  exit 1
fi
for tool in initdb postgres pg_ctl psql; do
  if [[ ! -f "$bin/$tool" ]]; then
    echo 'Native PostgreSQL tool set is incomplete.' >&2
    exit 1
  fi
done
printf '%s\n' "$bin"
