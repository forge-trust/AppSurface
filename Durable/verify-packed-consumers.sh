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
  "Durable/ForgeTrust.AppSurface.Durable.PostgreSql/ForgeTrust.AppSurface.Durable.PostgreSql.csproj"
)

packed_packages=(
  "ForgeTrust.AppSurface.Core"
  "ForgeTrust.AppSurface.Flow"
  "ForgeTrust.AppSurface.Workers"
  "ForgeTrust.AppSurface.Durable"
  "ForgeTrust.AppSurface.Durable.Provider"
  "ForgeTrust.AppSurface.Durable.PostgreSql"
)

fail() {
  echo "Packed Durable consumer verification failed: $1" >&2
  exit 1
}

lowercase() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]'
}

verify_restored_package() {
  local package_id="$1"
  local package_file="$FEED_DIR/$package_id.$PACKAGE_VERSION.nupkg"
  local package_cache_directory="$NUGET_PACKAGES/$(lowercase "$package_id")/$(lowercase "$PACKAGE_VERSION")"
  local restored_package_file="$package_cache_directory/$(lowercase "$package_id").$(lowercase "$PACKAGE_VERSION").nupkg"
  local package_metadata_file="$package_cache_directory/.nupkg.metadata"

  [[ -f "$package_file" ]] || fail "the freshly packed package is missing: $package_file"
  [[ -f "$restored_package_file" ]] || fail "the restored package is missing: $restored_package_file"
  [[ -f "$package_metadata_file" ]] || fail "NuGet restore metadata is missing: $package_metadata_file"
  grep -Fq "\"source\": \"$FEED_DIR\"" "$package_metadata_file" \
    || fail "$package_id/$PACKAGE_VERSION was not restored from the generated local feed"

  # Byte identity is stronger than package ID/version or source mapping alone and
  # avoids non-portable sha512sum/shasum command differences between CI hosts.
  cmp -s "$package_file" "$restored_package_file" \
    || fail "$package_id/$PACKAGE_VERSION differs from the freshly packed local artifact"
}

verify_assets_package() {
  local assets_file="$1"
  local package_id="$2"

  grep -Fq "\"$package_id/$PACKAGE_VERSION\":" "$assets_file" \
    || fail "$assets_file does not contain the expected $package_id/$PACKAGE_VERSION package"
}

for project in "${projects[@]}"; do
  dotnet restore "$ROOT_DIR/$project" \
    --locked-mode \
    -m:1 \
    -p:UseSharedCompilation=false
  dotnet pack "$ROOT_DIR/$project" \
    --configuration Release \
    --no-restore \
    --output "$FEED_DIR" \
    -m:1 \
    -p:PackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  package_id="${project##*/}"
  package_id="${package_id%.csproj}"
  [[ -f "$FEED_DIR/$package_id.$PACKAGE_VERSION.nupkg" ]] \
    || fail "packing did not produce the expected $package_id/$PACKAGE_VERSION artifact"
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

for consumer in Adopter Provider PostgreSqlProvider; do
  consumer_dir="$WORK_DIR/$consumer"
  cp -R "$ROOT_DIR/Durable/packed-consumers/$consumer" "$consumer_dir"
  mv "$consumer_dir/$consumer.csproj.template" "$consumer_dir/$consumer.csproj"
  dotnet restore "$consumer_dir/$consumer.csproj" \
    --configfile "$CONFIG_FILE" \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false

  assets_file="$consumer_dir/obj/project.assets.json"
  [[ -f "$assets_file" ]] || fail "restore did not produce $assets_file"
  case "$consumer" in
    Adopter)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable"
      ;;
    Provider)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable.Provider"
      ;;
    PostgreSqlProvider)
      verify_assets_package "$assets_file" "ForgeTrust.AppSurface.Durable.PostgreSql"
      ;;
    *)
      fail "unknown packed consumer: $consumer"
      ;;
  esac

  dotnet build "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-restore \
    -m:1 \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:UseSharedCompilation=false
  dotnet run --project "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-build \
    --no-restore
done

for package_id in "${packed_packages[@]}"; do
  verify_restored_package "$package_id"
done

echo "Packed Durable adopter and provider consumers compiled and ran successfully, including the four-kind admission contract."
