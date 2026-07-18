#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_VERSION="${APP_SURFACE_PACKAGE_VERSION:-0.1.0}"
TMP_ROOT="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
WORK_DIR="$(mktemp -d "$TMP_ROOT/appsurface-durable-consumers.XXXXXX")"
FEED_DIR="$WORK_DIR/feed"
CONFIG_FILE="$WORK_DIR/NuGet.config"

cleanup() {
  if [[ -n "${WORK_DIR:-}" \
    && -d "$WORK_DIR" \
    && "$WORK_DIR" == "$TMP_ROOT"/appsurface-durable-consumers.* ]]; then
    rm -rf -- "$WORK_DIR"
  fi
}

trap cleanup EXIT

mkdir -p "$FEED_DIR"

projects=(
  "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
  "Flow/ForgeTrust.AppSurface.Flow/ForgeTrust.AppSurface.Flow.csproj"
  "Workers/ForgeTrust.AppSurface.Workers/ForgeTrust.AppSurface.Workers.csproj"
  "Durable/ForgeTrust.AppSurface.Durable/ForgeTrust.AppSurface.Durable.csproj"
  "Durable/ForgeTrust.AppSurface.Durable.Provider/ForgeTrust.AppSurface.Durable.Provider.csproj"
)

for project in "${projects[@]}"; do
  dotnet restore "$ROOT_DIR/$project" --locked-mode
  dotnet pack "$ROOT_DIR/$project" \
    --configuration Release \
    --no-restore \
    --output "$FEED_DIR" \
    -p:PackageVersion="$PACKAGE_VERSION"
done

sed "s|__LOCAL_FEED__|$FEED_DIR|g" > "$CONFIG_FILE" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="durable-local" value="__LOCAL_FEED__" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="durable-local">
      <package pattern="ForgeTrust.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

export NUGET_PACKAGES="$WORK_DIR/packages"
export DOTNET_CLI_HOME="$WORK_DIR/dotnet-home"
mkdir -p "$NUGET_PACKAGES" "$DOTNET_CLI_HOME"

for consumer in Adopter Provider; do
  consumer_dir="$WORK_DIR/$consumer"
  cp -R "$ROOT_DIR/Durable/packed-consumers/$consumer" "$consumer_dir"
  mv "$consumer_dir/$consumer.csproj.template" "$consumer_dir/$consumer.csproj"
  dotnet restore "$consumer_dir/$consumer.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  dotnet run --project "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-restore \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
done

echo "Packed Durable adopter and provider consumers compiled and ran successfully."
