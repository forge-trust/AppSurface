#!/usr/bin/env bash
# Opt-in, non-claiming image feasibility check on a native Linux x64 runner.
# The protected-base evidence workflow must not use this branch-owned result as
# a subject, isolation, artifact, or gate attestation.
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
runner_temp="$(cd "${RUNNER_TEMP:?RUNNER_TEMP is required}" && pwd -P)"

if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo 'Native Linux x64 is required for the offline subject image check.' >&2
  exit 2
fi

work_root="$(mktemp -d "$runner_temp/appsurface-subject-image.XXXXXXXX")"
global_packages="$work_root/global-packages"
context="$work_root/context"
cold_root="$work_root/cold"
image_tag='appsurface-subject-native-validation:local'
mkdir -m 0700 "$global_packages" "$context" "$cold_root"

# This ordinary branch validation restore supplies a clean Linux package cache.
# The production evidence controller must never restore a PR head on its host.
dotnet restore "$repository_root/ForgeTrust.AppSurface.slnx" \
  --locked-mode --packages "$global_packages" --verbosity quiet

python3 "$repository_root/scripts/evidence-gate-build-offline-feed.py" \
  --checkout "$repository_root" \
  --solution ForgeTrust.AppSurface.slnx \
  --global-packages "$global_packages" \
  --output "$context/locked-dependencies"
cp "$repository_root/scripts/evidence-gate-subject-entrypoint.py" \
  "$context/evidence-gate-subject-entrypoint.py"
docker build --platform linux/amd64 \
  --file "$repository_root/scripts/evidence-gate-subject-image/Containerfile" \
  --tag "$image_tag" "$context"
docker image inspect --format 'local image ID={{.Id}} architecture={{.Architecture}}' "$image_tag"

# The disposable source copy keeps package extraction and MSBuild obj writes
# away from the checkout. Only this copy, its empty cache, and the fixed offline
# source config enter the no-network validation container.
mkdir -m 0700 "$cold_root/source" "$cold_root/cache" "$cold_root/home" "$cold_root/tmp"
cp -a "$repository_root/." "$cold_root/source/"
cat > "$cold_root/offline-nuget.config" <<'CONFIG'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="locked-dependencies" value="/opt/appsurface/locked-dependencies" />
  </packageSources>
</configuration>
CONFIG
sudo chown -R 65532:65532 "$cold_root"

docker run --rm --platform linux/amd64 \
  --pull never --network none --read-only --user 65532:65532 \
  --cap-drop ALL --security-opt no-new-privileges \
  --pids-limit 256 --memory 4g --cpus 2 \
  --mount "type=bind,src=$cold_root,dst=/work,rw=true" \
  --tmpfs /tmp:rw,noexec,nosuid,nodev,size=512m,mode=1777 \
  --workdir /work/source \
  --env HOME=/work/home \
  --env DOTNET_CLI_HOME=/work/home \
  --env DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  --env DOTNET_NOLOGO=1 \
  --env NUGET_PACKAGES=/work/cache \
  --env NUGET_HTTP_CACHE_PATH=/work/http-cache \
  --env TMPDIR=/work/tmp \
  --entrypoint /bin/sh "$image_tag" -c '
    exec timeout --signal=TERM --kill-after=10s 15m \
      dotnet restore /work/source/ForgeTrust.AppSurface.slnx \
      --locked-mode --disable-parallel -m:1 \
      --configfile /work/offline-nuget.config \
      --source /opt/appsurface/locked-dependencies \
      --packages /work/cache --verbosity minimal
  '

if [[ -z "$(find "$cold_root/cache" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo 'The offline restore produced no packages in its empty cache.' >&2
  exit 2
fi
echo 'Native Linux x64 cold-cache locked restore passed with only the embedded feed.'
