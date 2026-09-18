#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
old_commit=540b59f7e2a3e1cb072645d0d7bff519dcf55589
old_package_version=0.1.0-previous.540b59f7
archive_root=/tmp/appsurface-config-compatibility/archive
package_root=/tmp/appsurface-config-compatibility/previous-packages
fixture_root=/tmp/appsurface-config-compatibility/previous-fixtures
candidate_root=
configuration=Release

usage() {
  cat <<'EOF'
Usage: scripts/verify-config-compatibility.sh [options]

Options:
  --archive DIR             Isolated previous-source checkout.
  --packages DIR            Retained previous nupkg directory.
  --fixtures DIR            Retained fixture build directory.
  --candidate-packages DIR  Candidate package directory to preflight.
  --configuration NAME      Build configuration (default: Release).
EOF
}

while (($# > 0)); do
  case "$1" in
    --archive) archive_root=$2; shift 2 ;;
    --packages) package_root=$2; shift 2 ;;
    --fixtures) fixture_root=$2; shift 2 ;;
    --candidate-packages) candidate_root=$2; shift 2 ;;
    --configuration) configuration=$2; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

mkdir -p "$package_root" "$fixture_root"
run_root=$(mktemp -d "$fixture_root/run.XXXXXX")
nuget_cache="$run_root/nuget-packages"
mkdir -p "$nuget_cache"
export NUGET_PACKAGES="$nuget_cache"
: "${NUGET_HTTP_CACHE_PATH:=/tmp/config-compat-http}"
nuget_http_cache="$NUGET_HTTP_CACHE_PATH"
mkdir -p "$nuget_http_cache"
export NUGET_HTTP_CACHE_PATH="$nuget_http_cache"

if [[ ! -d "$archive_root/.git" ]]; then
  mkdir -p "$(dirname "$archive_root")"
  git clone --no-local "$repo_root" "$archive_root"
fi

git -C "$archive_root" checkout --detach "$old_commit" >/dev/null
if [[ "$(git -C "$archive_root" rev-parse HEAD)" != "$old_commit" ]]; then
  echo "Unable to prepare exact previous checkout at $old_commit." >&2
  exit 2
fi

# The historical Config source used Hosting/Logging types without declaring those references in its project.
# Copying this build-only overlay into /tmp keeps the archived source and the current production tree untouched.
cp "$repo_root/tests/config-key-compatibility/old-build/Directory.Build.targets" "$archive_root/Directory.Build.targets"

old_projects=(
  "$archive_root/ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
  "$archive_root/Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj"
  "$archive_root/Config/ForgeTrust.AppSurface.Config.LocalSecrets/ForgeTrust.AppSurface.Config.LocalSecrets.csproj"
  "$archive_root/Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/ForgeTrust.AppSurface.Config.GoogleSecretManager.csproj"
)

echo "Building previous package family from $old_commit"
for project in "${old_projects[@]}"; do
  dotnet restore "$project" --force-evaluate --disable-build-servers -m:1
  dotnet pack "$project" --no-restore --disable-build-servers -m:1 --configuration "$configuration" \
    -p:Version="$old_package_version" \
    -p:PackageVersion="$old_package_version" \
    --output "$package_root"
done

required_ids=(
  ForgeTrust.AppSurface.Core
  ForgeTrust.AppSurface.Config
  ForgeTrust.AppSurface.Config.LocalSecrets
  ForgeTrust.AppSurface.Config.GoogleSecretManager
)

package_path() {
  local root=$1 id=$2 version=$3
  printf '%s/%s.%s.nupkg' "$root" "$id" "$version"
}

for id in "${required_ids[@]}"; do
  artifact=$(package_path "$package_root" "$id" "$old_package_version")
  if [[ ! -f "$artifact" ]]; then
    echo "Missing previous package: $artifact" >&2
    exit 2
  fi
done

fixture_source="$repo_root/tests/config-key-compatibility/previous-consumer/PreviousConsumer.csproj"
fixture_obj="$run_root/obj"
fixture_bin="$run_root/bin"
mkdir -p "$fixture_obj" "$fixture_bin"

dotnet restore "$fixture_source" --disable-build-servers -m:1 \
  --source "$package_root" \
  --source https://api.nuget.org/v3/index.json \
  -p:AppSurfacePackageVersion="$old_package_version" \
  -p:BaseIntermediateOutputPath="$fixture_obj/" \
  -p:MSBuildProjectExtensionsPath="$fixture_obj/"
dotnet build "$fixture_source" --no-restore --disable-build-servers -m:1 --configuration "$configuration" \
  -p:AppSurfacePackageVersion="$old_package_version" \
  -p:BaseIntermediateOutputPath="$fixture_obj/" \
  -p:MSBuildProjectExtensionsPath="$fixture_obj/" \
  -p:OutputPath="$fixture_bin/"

baseline_output="$fixture_root/baseline-result.txt"
dotnet "$fixture_bin/PreviousConsumer.dll" | tee "$baseline_output"
if ! rg -q '^BASELINE PASS:' "$baseline_output"; then
  echo "Previous consumer fixture did not report BASELINE PASS." >&2
  exit 1
fi

plugin_source="$repo_root/tests/config-key-compatibility/previous-provider-plugin/PreviousProviderPlugin.csproj"
plugin_obj="$run_root/plugin-obj"
plugin_bin="$run_root/plugin-bin"
mkdir -p "$plugin_obj" "$plugin_bin"
dotnet restore "$plugin_source" --disable-build-servers -m:1 \
  --source "$package_root" \
  --source https://api.nuget.org/v3/index.json \
  -p:AppSurfacePackageVersion="$old_package_version" \
  -p:NuGetLockFilePath="$plugin_obj/packages.lock.json" \
  -p:RestoreLockedMode=false \
  -p:BaseIntermediateOutputPath="$plugin_obj/" \
  -p:MSBuildProjectExtensionsPath="$plugin_obj/"
dotnet build "$plugin_source" --no-restore --disable-build-servers -m:1 --configuration "$configuration" \
  -p:AppSurfacePackageVersion="$old_package_version" \
  -p:NuGetLockFilePath="$plugin_obj/packages.lock.json" \
  -p:BaseIntermediateOutputPath="$plugin_obj/" \
  -p:MSBuildProjectExtensionsPath="$plugin_obj/" \
  -p:OutputPath="$plugin_bin/"
plugin_path="$plugin_bin/PreviousProviderPlugin.dll"
if [[ ! -f "$plugin_path" ]]; then
  echo "Missing previous provider plugin: $plugin_path" >&2
  exit 2
fi

echo "PREVIOUS PACKAGE FAMILY: $package_root"
echo "PREVIOUS FIXTURE OUTPUT: $fixture_root"
echo "PREVIOUS CHECKOUT: $archive_root"
echo "PREVIOUS PROVIDER PLUGIN: $plugin_path"

if [[ -z "$candidate_root" ]]; then
  echo "CANDIDATE FOUNDATION PENDING: provide --candidate-packages DIR after the four candidate packages build."
  exit 0
fi

if [[ ! -d "$candidate_root" ]]; then
  echo "Candidate package directory does not exist: $candidate_root" >&2
  exit 2
fi

candidate_package_files=("$candidate_root"/*.nupkg)
if [[ ! -e "${candidate_package_files[0]}" || ${#candidate_package_files[@]} -ne ${#required_ids[@]} ]]; then
  echo "CANDIDATE FOUNDATION INCOMPLETE: expected exactly four AppSurface packages in $candidate_root" >&2
  exit 2
fi

core_matches=("$candidate_root/ForgeTrust.AppSurface.Core."*.nupkg)
if [[ ! -e "${core_matches[0]}" || ${#core_matches[@]} -ne 1 ]]; then
  echo "CANDIDATE FOUNDATION INCOMPLETE: expected one Core package in $candidate_root" >&2
  exit 2
fi
core_filename=$(basename "${core_matches[0]}")
candidate_version=${core_filename#ForgeTrust.AppSurface.Core.}
candidate_version=${candidate_version%.nupkg}

for id in "${required_ids[@]}"; do
  artifact=$(package_path "$candidate_root" "$id" "$candidate_version")
  if [[ ! -f "$artifact" ]]; then
    echo "CANDIDATE FOUNDATION INVALID: missing exact $id package version $candidate_version in $candidate_root" >&2
    exit 2
  fi
done

candidate_obj="$run_root/candidate-obj"
candidate_bin="$run_root/candidate-bin"
mkdir -p "$candidate_obj" "$candidate_bin"
candidate_source="$repo_root/tests/config-key-compatibility/candidate-bootstrap/CandidateBootstrap.csproj"
candidate_restore_log="$fixture_root/candidate-restore.log"
dotnet restore "$candidate_source" --disable-build-servers -m:1 \
  --source "$candidate_root" \
  --source "$package_root" \
  --source https://api.nuget.org/v3/index.json \
  -p:AppSurfaceCorePackageVersion="$candidate_version" \
  -p:AppSurfaceConfigPackageVersion="$candidate_version" \
  -p:AppSurfaceLocalSecretsPackageVersion="$candidate_version" \
  -p:AppSurfaceGooglePackageVersion="$candidate_version" \
  -p:NuGetLockFilePath="$candidate_obj/packages.lock.json" \
  -p:RestoreLockedMode=false \
  -p:BaseIntermediateOutputPath="$candidate_obj/" \
  -p:MSBuildProjectExtensionsPath="$candidate_obj/" 2>&1 | tee "$candidate_restore_log"
dotnet build "$candidate_source" --no-restore --disable-build-servers -m:1 --configuration "$configuration" \
  -p:AppSurfaceCorePackageVersion="$candidate_version" \
  -p:AppSurfaceConfigPackageVersion="$candidate_version" \
  -p:AppSurfaceLocalSecretsPackageVersion="$candidate_version" \
  -p:AppSurfaceGooglePackageVersion="$candidate_version" \
  -p:NuGetLockFilePath="$candidate_obj/packages.lock.json" \
  -p:BaseIntermediateOutputPath="$candidate_obj/" \
  -p:MSBuildProjectExtensionsPath="$candidate_obj/" \
  -p:OutputPath="$candidate_bin/"

extract_package_assembly() {
  local package_file=$1 assembly_name=$2 extract_root=$3
  local target="$extract_root/$assembly_name.dll"
  if [[ ! -f "$target" ]]; then
    mkdir -p "$extract_root"
    unzip -oq "$package_file" "lib/net10.0/$assembly_name.dll" -d "$extract_root"
    mv "$extract_root/lib/net10.0/$assembly_name.dll" "$target"
    rmdir -p "$extract_root/lib/net10.0" 2>/dev/null || true
  fi
  printf '%s' "$target"
}

old_assembly_root="$run_root/old-package-assemblies"
old_core_dll=$(extract_package_assembly "$(package_path "$package_root" ForgeTrust.AppSurface.Core "$old_package_version")" ForgeTrust.AppSurface.Core "$old_assembly_root/core")
old_config_dll=$(extract_package_assembly "$(package_path "$package_root" ForgeTrust.AppSurface.Config "$old_package_version")" ForgeTrust.AppSurface.Config "$old_assembly_root/config")
old_local_dll=$(extract_package_assembly "$(package_path "$package_root" ForgeTrust.AppSurface.Config.LocalSecrets "$old_package_version")" ForgeTrust.AppSurface.Config.LocalSecrets "$old_assembly_root/local")
old_google_dll=$(extract_package_assembly "$(package_path "$package_root" ForgeTrust.AppSurface.Config.GoogleSecretManager "$old_package_version")" ForgeTrust.AppSurface.Config.GoogleSecretManager "$old_assembly_root/google")
candidate_bootstrap="$candidate_bin/CandidateBootstrap.dll"

run_bootstrap() {
  local label=$1 mode=$2 assembly_path=$3
  local output="$fixture_root/$label.txt"
  dotnet "$candidate_bootstrap" "$mode" "$assembly_path" | tee "$output"
}

guarded_output=$(run_bootstrap guarded guarded "$plugin_path" 2>&1)
printf '%s\n' "$guarded_output" > "$fixture_root/guarded-old-plugin.txt"
if ! rg -q '^GUARDED REJECTED: Code: config-package-version-mismatch$' "$fixture_root/guarded-old-plugin.txt"; then
  echo "Guarded old-plugin execution did not produce the normative package mismatch diagnostic." >&2
  exit 1
fi

unguarded_output=$(run_bootstrap unguarded unguarded "$plugin_path" 2>&1)
printf '%s\n' "$unguarded_output" > "$fixture_root/unguarded-old-plugin.txt"
if ! rg -q '^UNGUARDED RUNTIME FAILURE: (TypeLoadException|FileLoadException|FileNotFoundException|ReflectionTypeLoadException)$' "$fixture_root/unguarded-old-plugin.txt"; then
  echo "Unguarded old-plugin control did not demonstrate an actual runtime loader failure." >&2
  exit 1
fi

for boundary in startup dependencies host-builder; do
  output=$(run_bootstrap "$boundary" "$boundary" "$plugin_path" 2>&1)
  printf '%s\n' "$output" > "$fixture_root/$boundary.txt"
  if ! rg -q 'REJECTED: Code: config-package-version-mismatch$' "$fixture_root/$boundary.txt" \
      || ! rg -q '^CALLBACKS: root-factory=0 dependency-registration=0$' "$fixture_root/$boundary.txt"; then
    echo "$boundary did not reject the old plugin before module callbacks." >&2
    exit 1
  fi
done

for matrix in \
  "mixed-old-core:$old_core_dll" \
  "mixed-old-config:$old_config_dll" \
  "mixed-old-local-secrets:$old_local_dll" \
  "mixed-old-google:$old_google_dll"; do
  label=${matrix%%:*}
  assembly=${matrix#*:}
  output=$(run_bootstrap "$label" guarded-package "$assembly" 2>&1)
  printf '%s\n' "$output" > "$fixture_root/$label.txt"
  if ! rg -q '^GUARDED REJECTED: Code: config-package-version-mismatch$' "$fixture_root/$label.txt"; then
    echo "$label did not produce the normative package mismatch diagnostic." >&2
    exit 1
  fi
done

cat <<EOF
RUNTIME GUARD PASS: old plugin was loaded as metadata, rejected by ConfigPackageCompatibility.ValidateAssemblies before GetTypes/activation, and produced config-package-version-mismatch.
STARTUP BOUNDARIES PASS: RunAsync(string[]), early RegisterDependencies, and CreateHostBuilder rejected the old plugin with zero root-factory and dependency callbacks.
UNGUARDED CONTROL PASS: old plugin produced an actual loader failure when activation was attempted without the guard.
MIXED FAMILY PASS: old Core, Config, LocalSecrets, and Google assemblies each rejected independently by own-identity validation, with no old plugin loaded.
CANDIDATE BOOTSTRAP: $candidate_bootstrap
EOF
