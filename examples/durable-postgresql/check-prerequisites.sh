#!/usr/bin/env bash
set -euo pipefail

port="${APPSURFACE_DURABLE_PREREQUISITE_PORT:-54329}"
missing=0

require_command() {
  if command -v "$1" >/dev/null 2>&1; then
    printf '[ok] %s\n' "$1"
  else
    printf '[missing] %s\n' "$1" >&2
    missing=1
  fi
}

require_command dotnet
require_command docker

if command -v dotnet >/dev/null 2>&1; then
  if dotnet_version="$(dotnet --version)"; then
    dotnet_major="${dotnet_version%%.*}"
    if [[ "$dotnet_major" =~ ^[0-9]+$ ]] && (( dotnet_major >= 10 )); then
      printf '[ok] .NET SDK %s\n' "$dotnet_version"
    else
      printf '[missing] .NET 10 SDK (found %s)\n' "$dotnet_version" >&2
      missing=1
    fi
  else
    printf '[missing] .NET SDK version could not be read\n' >&2
    missing=1
  fi
fi

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  printf '[ok] Docker daemon\n'
else
  printf '[missing] Docker daemon\n' >&2
  missing=1
fi

if [[ ! "$port" =~ ^[0-9]{1,5}$ ]] || (( 10#$port < 1 || 10#$port > 65535 )); then
  printf '[missing] local TCP port %s must be an integer from 1 through 65535\n' "$port" >&2
  missing=1
elif (: >/dev/tcp/127.0.0.1/"$port") >/dev/null 2>&1; then
  printf '[missing] local TCP port %s is already in use\n' "$port" >&2
  missing=1
else
  printf '[ok] local TCP port %s is available\n' "$port"
fi

printf '%s\n' 'Later configuration names (values are not checked or printed):'
printf '  %s\n' \
  DOTNET_ENVIRONMENT \
  APPSURFACE_DURABLE_LOCAL_PROOF \
  APPSURFACE_DURABLE_MIGRATION_CONNECTION \
  APPSURFACE_DURABLE_DISPATCHER_CONNECTION \
  APPSURFACE_DURABLE_RUNTIME_CONNECTION \
  APPSURFACE_DURABLE_RUNTIME_EPOCH

exit "$missing"
