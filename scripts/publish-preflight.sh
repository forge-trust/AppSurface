#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 0 || $(uname -s) != Linux ]]; then
  printf '%s\n' 'ASPUB003: Run ./scripts/publish-preflight.sh without arguments on Linux.' >&2
  exit 2
fi

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"
scratch=$(mktemp -d "${TMPDIR:-/tmp}/appsurface-publish-preflight.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
scratch=$(cd -- "$scratch" && pwd -P)

# Pass a private output and receipt location. Never use the ordinary publish tree.
dotnet build tools/PublishPreflight/ForgeTrust.AppSurface.PublishPreflight.csproj --configuration Release >&2
dotnet publish examples/razorwire-mvc/RazorWireWebExample.csproj \
  --configuration Release --no-self-contained --output "$scratch/publish" \
  -p:AppSurfacePublishPreflight=true -p:AppSurfacePreflightReceiptDirectory="$scratch" >&2

if [[ ! -s "$scratch/paths.ok" || ! -s "$scratch/permissions.ok" ]]; then
  printf '%s\n' 'ASPUB003: The SDK did not complete both publish preflight phases.' >&2
  exit 1
fi

printf '%s\n' 'Publish preflight passed: RazorWire MVC paths and output permissions.'
