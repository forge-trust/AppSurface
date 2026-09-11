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

for project in "${projects[@]}"; do
  dotnet restore "$ROOT_DIR/$project" --locked-mode
  dotnet pack "$ROOT_DIR/$project" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
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

for consumer in Adopter Provider PostgreSqlProvider; do
  consumer_dir="$WORK_DIR/$consumer"
  cp -R "$ROOT_DIR/Durable/packed-consumers/$consumer" "$consumer_dir"
  mv "$consumer_dir/$consumer.csproj.template" "$consumer_dir/$consumer.csproj"
  dotnet restore "$consumer_dir/$consumer.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  dotnet run --project "$consumer_dir/$consumer.csproj" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
done

negative_root="$ROOT_DIR/Durable/packed-consumers/Negative"
python3 "$negative_root/check_sarif.py" --self-test

negative_count=0
for fixture_dir in "$negative_root"/*/; do
  [[ -f "$fixture_dir/expected.json" ]] || continue
  fixture_name="$(basename "$fixture_dir")"
  positive_dir="$WORK_DIR/positive-$fixture_name"
  fixture_work_dir="$WORK_DIR/negative-$fixture_name"
  mkdir -p "$positive_dir" "$fixture_work_dir"
  cp "$fixture_dir/$fixture_name.csproj.template" "$positive_dir/$fixture_name.csproj"
  cp "$fixture_dir/$fixture_name.csproj.template" "$fixture_work_dir/$fixture_name.csproj"

  expected_file="$fixture_dir/expected.json"
  invalid_source="$fixture_dir/$(jq -r '.invalid.file' "$expected_file")"
  positive_source="$fixture_dir/$(jq -r '.positive.file' "$expected_file")"
  python3 "$negative_root/check_sarif.py" --manifest "$expected_file" "$invalid_source" "$positive_source"

  cp "$positive_source" "$positive_dir/Program.cs"
  dotnet restore "$positive_dir/$fixture_name.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  dotnet build "$positive_dir/$fixture_name.csproj" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    --nologo

  cp "$invalid_source" "$fixture_work_dir/Program.cs"
  dotnet restore "$fixture_work_dir/$fixture_name.csproj" \
    --configfile "$CONFIG_FILE" \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION"
  sarif_file="$fixture_work_dir/compiler.sarif"
  set +e
  dotnet build "$fixture_work_dir/$fixture_name.csproj" \
    --configuration Release \
    --no-restore \
    -p:UseSharedCompilation=false \
    -p:AppSurfacePackageVersion="$PACKAGE_VERSION" \
    -p:ErrorLog="$sarif_file,version=2.1" \
    --nologo
  invalid_exit=$?
  set -e
  if [[ "$invalid_exit" -eq 0 ]]; then
    echo "Negative fixture unexpectedly compiled: $fixture_name" >&2
    exit 1
  fi
  python3 "$negative_root/check_sarif.py" "$fixture_work_dir/Program.cs" "$sarif_file"
  negative_count=$((negative_count + 1))
done

[[ "$negative_count" -eq 8 ]] || {
  echo "Expected 8 negative fixtures, found $negative_count." >&2
  exit 1
}

echo "Packed Durable adopter and provider consumers compiled and ran successfully; verified $negative_count exact negative fixtures."
