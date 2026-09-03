#!/usr/bin/env bash
set -euo pipefail

artifact_directory=""
package_version=""
work_directory=""
report_path=""

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --artifacts)
      artifact_directory="$2"
      shift 2
      ;;
    --package-version)
      package_version="$2"
      shift 2
      ;;
    --work-directory)
      work_directory="$2"
      shift 2
      ;;
    --report-path)
      report_path="$2"
      shift 2
      ;;
    *)
      printf 'Unknown argument: %s\n' "$1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$artifact_directory" || -z "$package_version" || -z "$work_directory" ]]; then
  printf 'Usage: %s --artifacts <directory> --package-version <version> --work-directory <directory> [--report-path <path>]\n' "$0" >&2
  exit 2
fi

xml_escape() {
  local value="$1"
  value="${value//&/\&amp;}"
  value="${value//</\&lt;}"
  value="${value//>/\&gt;}"
  value="${value//\"/\&quot;}"
  value="${value//\'/\&apos;}"
  printf '%s' "$value"
}

if [[ ! -f "$artifact_directory/ForgeTrust.AppSurface.Web.Tailwind.$package_version.nupkg" ]]; then
  printf 'Missing packed Tailwind package for version %s in %s.\n' "$package_version" "$artifact_directory" >&2
  exit 1
fi

tailwind_package="$artifact_directory/ForgeTrust.AppSurface.Web.Tailwind.$package_version.nupkg"
tailwind_version="$(unzip -p "$tailwind_package" build/tailwind.version | tr -d '\r\n')"
if [[ ! "$tailwind_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  printf 'Packed Tailwind version metadata is invalid: %s.\n' "$tailwind_version" >&2
  exit 1
fi

consumer_directory="$work_directory/consumer"
cache_directory="$work_directory/tailwind-cache"
report_path="${report_path:-$work_directory/tailwind-package-consumer-proof.md}"
nuget_packages_directory="$work_directory/nuget-packages"
nuget_http_cache_directory="$work_directory/nuget-http-cache"
dotnet_home_directory="$work_directory/dotnet-home"
artifact_directory_xml="$(xml_escape "$artifact_directory")"
cache_directory_xml="$(xml_escape "$cache_directory")"
mkdir -p "$consumer_directory/wwwroot/css"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64)
    host_rid="linux-x64"
    binary_name="tailwindcss-linux-x64"
    ;;
  Linux-aarch64|Linux-arm64)
    host_rid="linux-arm64"
    binary_name="tailwindcss-linux-arm64"
    ;;
  Darwin-x86_64)
    host_rid="osx-x64"
    binary_name="tailwindcss-macos-x64"
    ;;
  Darwin-arm64)
    host_rid="osx-arm64"
    binary_name="tailwindcss-macos-arm64"
    ;;
  MINGW*-*|MSYS*-*|CYGWIN*-*)
    host_rid="win-x64"
    binary_name="tailwindcss-windows-x64.exe"
    ;;
  *)
    printf 'This packed-consumer proof has no supported host mapping for %s-%s.\n' "$(uname -s)" "$(uname -m)" >&2
    exit 1
    ;;
esac

cat > "$consumer_directory/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-artifacts" value="$artifact_directory_xml" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-artifacts">
      <package pattern="ForgeTrust.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="CliWrap" />
      <package pattern="Microsoft.Extensions.*" />
      <package pattern="System.Diagnostics.EventLog" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

cat > "$consumer_directory/Directory.Packages.props" <<'EOF'
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
</Project>
EOF

cat > "$consumer_directory/Tailwind.PackageConsumerProof.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <TailwindDownloadCacheRoot>$cache_directory_xml</TailwindDownloadCacheRoot>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ForgeTrust.AppSurface.Web.Tailwind" Version="$package_version" />
  </ItemGroup>
</Project>
EOF

cat > "$consumer_directory/wwwroot/css/app.css" <<'EOF'
@import "tailwindcss";
EOF

NUGET_PACKAGES="$nuget_packages_directory" NUGET_HTTP_CACHE_PATH="$nuget_http_cache_directory" DOTNET_CLI_HOME="$dotnet_home_directory" dotnet restore "$consumer_directory/Tailwind.PackageConsumerProof.csproj" --configfile "$consumer_directory/NuGet.config" --force-evaluate
NUGET_PACKAGES="$nuget_packages_directory" NUGET_HTTP_CACHE_PATH="$nuget_http_cache_directory" DOTNET_CLI_HOME="$dotnet_home_directory" dotnet restore "$consumer_directory/Tailwind.PackageConsumerProof.csproj" --configfile "$consumer_directory/NuGet.config" --locked-mode
NUGET_PACKAGES="$nuget_packages_directory" NUGET_HTTP_CACHE_PATH="$nuget_http_cache_directory" DOTNET_CLI_HOME="$dotnet_home_directory" dotnet build "$consumer_directory/Tailwind.PackageConsumerProof.csproj" --configuration Release --no-restore

generated_css="$consumer_directory/wwwroot/css/site.gen.css"
expected_cache_binary="$cache_directory/tailwind-$tailwind_version/$host_rid/$binary_name"

if [[ ! -s "$generated_css" ]]; then
  printf 'The packed Tailwind consumer did not generate %s.\n' "$generated_css" >&2
  exit 1
fi

if [[ ! -f "$expected_cache_binary" ]]; then
  printf 'The packed Tailwind consumer did not acquire the expected %s cache entry %s.\n' "$host_rid" "$expected_cache_binary" >&2
  exit 1
fi

if grep -Fq 'ForgeTrust.AppSurface.Web.Tailwind.Runtime.' "$consumer_directory/obj/project.assets.json"; then
  printf 'The packed Tailwind consumer restore graph still contains a runtime companion package.\n' >&2
  exit 1
fi

if find "$consumer_directory/bin" "$consumer_directory/obj" -type f -name 'tailwindcss-*' -print -quit | grep -q .; then
  printf 'The packed Tailwind consumer copied a native Tailwind executable into a build output.\n' >&2
  exit 1
fi

mkdir -p "$(dirname "$report_path")"
cat > "$report_path" <<EOF
# Tailwind packed-consumer proof

- Package: ForgeTrust.AppSurface.Web.Tailwind $package_version
- Host RID: $host_rid
- Restore graph: source-mapped to local first-party artifacts plus reviewed CliWrap, Microsoft.Extensions, and System.Diagnostics.EventLog packages; lock file verified with --locked-mode; no Tailwind runtime companion dependency
- Build: generated wwwroot/css/site.gen.css from the packed targets and task
- Host cache: acquired and verified $expected_cache_binary
- Consumer output: no tailwindcss-* native executable
EOF
