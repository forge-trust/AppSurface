#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASELINE_COMMIT="${1:-e55c549ed23a9da9abb747c87bd202b504d1c736}"
CURRENT_VERSION="${CURRENT_VERSION:-0.0.0-file-secret-smoke}"
BASELINE_VERSION="${BASELINE_VERSION:-0.0.0-baseline-${BASELINE_COMMIT:0:12}}"
WORK_DIR="${WORK_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/appsurface-file-secret-smoke.XXXXXX")}"
mkdir -p "$WORK_DIR"
WORK_DIR="$(cd "$WORK_DIR" && pwd -P)"
CURRENT_SOURCE="$WORK_DIR/current-source"
PREVIOUS_SOURCE="$WORK_DIR/previous-source"
CURRENT_FEED="$WORK_DIR/current-feed"
BASELINE_FEED="$WORK_DIR/baseline-feed"
CURRENT_CONSUMER="$WORK_DIR/current-consumer"
PREVIOUS_CONSUMER="$WORK_DIR/previous-consumer"
PREVIOUS_PUBLISH="$WORK_DIR/previous-publish"
ARTIFACT_REPORT="${ARTIFACT_REPORT:-$WORK_DIR/file-secret-references-smoke.md}"
NUGET_PACKAGES="$WORK_DIR/nuget-packages"
NUGET_HTTP_CACHE_PATH="$WORK_DIR/nuget-http-cache"

mkdir -p "$CURRENT_SOURCE" "$PREVIOUS_SOURCE" "$CURRENT_FEED" "$BASELINE_FEED" \
  "$CURRENT_CONSUMER" "$PREVIOUS_CONSUMER" "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
export NUGET_PACKAGES NUGET_HTTP_CACHE_PATH
export DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false
export DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false

PACK_PROJECTS=(
  "ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
  "Config/ForgeTrust.AppSurface.Config/ForgeTrust.AppSurface.Config.csproj"
  "Config/ForgeTrust.AppSurface.Config.GoogleSecretManager/ForgeTrust.AppSurface.Config.GoogleSecretManager.csproj"
)

run_bounded() {
  local seconds="$1"
  shift
  python3 - "$seconds" "$@" <<'PY'
import subprocess
import sys

timeout = float(sys.argv[1])
argv = sys.argv[2:]
try:
    result = subprocess.run(argv, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                            text=True, timeout=timeout)
except subprocess.TimeoutExpired as error:
    if error.stdout:
        sys.stdout.write(error.stdout if isinstance(error.stdout, str) else error.stdout.decode(errors="replace"))
    print(f"TIMEOUT after {timeout:.0f}s: {argv!r}", file=sys.stderr)
    raise SystemExit(124)
sys.stdout.write(result.stdout)
raise SystemExit(result.returncode)
PY
}

run_logged() {
  local label="$1" seconds="$2"
  shift 2
  local log="$WORK_DIR/$label.log"
  if ! run_bounded "$seconds" "$@" >"$log" 2>&1; then
    printf 'FAILED %s; exact output: %s\n' "$label" "$log" >&2
    cat "$log" >&2
    return 1
  fi
  cat "$log"
}

copy_current_package_source() {
  local item
  for item in Directory.* global.json NuGet.config LICENSE README.md; do
    [[ -e "$ROOT_DIR/$item" ]] && cp -R "$ROOT_DIR/$item" "$CURRENT_SOURCE/"
  done
  for item in ForgeTrust.AppSurface.Core Config/ForgeTrust.AppSurface.Config \
    Config/ForgeTrust.AppSurface.Config.GoogleSecretManager; do
    mkdir -p "$CURRENT_SOURCE/$(dirname "$item")"
    mkdir -p "$CURRENT_SOURCE/$item"
    rsync -a --exclude bin --exclude obj "$ROOT_DIR/$item/" "$CURRENT_SOURCE/$item/"
  done
}

pack_current() {
  copy_current_package_source
  local project
  for project in "${PACK_PROJECTS[@]}"; do
    run_logged "current-pack-$(basename "$project" .csproj)" 300 \
      dotnet pack "$CURRENT_SOURCE/$project" --configuration Release --output "$CURRENT_FEED" \
      -p:Version="$CURRENT_VERSION" -p:PackageVersion="$CURRENT_VERSION" \
      -p:UseSharedCompilation=false -nodeReuse:false
  done
}

pack_baseline() {
  git -C "$ROOT_DIR" archive "$BASELINE_COMMIT" | tar -x -C "$PREVIOUS_SOURCE"
  local before after project
  before="$(python3 - "$PREVIOUS_SOURCE" <<'PY'
import hashlib
from pathlib import Path
import sys
root = Path(sys.argv[1])
print('\n'.join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(root).as_posix()}" for p in sorted(x for x in root.rglob('*') if x.is_file() and not ({'obj', 'bin', '.obj', '.bin', 'packages.lock.json'} & set(x.parts)))))
PY
)"
  for project in "${PACK_PROJECTS[@]}"; do
    # The historical Config project omitted Hosting/Logging references. The external targets overlay
    # supplies them for this build without changing any file in the archived source.
    if ! run_logged "baseline-pack-$(basename "$project" .csproj)" 300 \
      dotnet pack "$PREVIOUS_SOURCE/$project" --configuration Release --output "$BASELINE_FEED" \
      -p:Version="$BASELINE_VERSION" -p:PackageVersion="$BASELINE_VERSION" \
      -p:DirectoryBuildTargetsPath="$ROOT_DIR/tests/config-key-compatibility/old-build/Directory.Build.targets" \
      -p:UseSharedCompilation=false -nodeReuse:false; then
      printf 'BASELINE PACKAGING FAILED WITHOUT SOURCE MUTATION.\n' >&2
      printf 'The archived baseline source kept its original project dependency graph.\n' >&2
      printf 'Inspect the exact compiler output at %s.\n' "$WORK_DIR/baseline-pack-$(basename "$project" .csproj).log" >&2
      return 1
    fi
  done
  after="$(python3 - "$PREVIOUS_SOURCE" <<'PY'
import hashlib
from pathlib import Path
import sys
root = Path(sys.argv[1])
print('\n'.join(f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(root).as_posix()}" for p in sorted(x for x in root.rglob('*') if x.is_file() and not ({'obj', 'bin', '.obj', '.bin', 'packages.lock.json'} & set(x.parts)))))
PY
)"
  [[ "$before" == "$after" ]] || { echo "Baseline archive changed during packaging" >&2; return 1; }
}

write_current_consumer() {
  cp "$ROOT_DIR/examples/file-secret-references/"*.cs "$CURRENT_CONSUMER/"
  cp "$ROOT_DIR/examples/file-secret-references/"*.json "$CURRENT_CONSUMER/"
  cat >"$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <IsPackable>false</IsPackable><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ForgeTrust.AppSurface.Core" Version="$CURRENT_VERSION" />
    <PackageReference Include="ForgeTrust.AppSurface.Config" Version="$CURRENT_VERSION" />
    <PackageReference Include="ForgeTrust.AppSurface.Config.GoogleSecretManager" Version="$CURRENT_VERSION" />
  </ItemGroup>
  <ItemGroup><None Update="appsettings*.json" CopyToOutputDirectory="PreserveNewest" /></ItemGroup>
</Project>
EOF
}

write_previous_consumer() {
  cat >"$PREVIOUS_CONSUMER/PreviousConsumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <IsPackable>false</IsPackable><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ForgeTrust.AppSurface.Core" Version="$BASELINE_VERSION" />
    <PackageReference Include="ForgeTrust.AppSurface.Config" Version="$BASELINE_VERSION" />
    <PackageReference Include="ForgeTrust.AppSurface.Config.GoogleSecretManager" Version="$BASELINE_VERSION" />
  </ItemGroup>
  <ItemGroup><None Update="appsettings.Production.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" /></ItemGroup>
</Project>
EOF
  cat >"$PREVIOUS_CONSUMER/appsettings.Production.json" <<'EOF'
{"Service":{"Plain":"legacy-plain"}}
EOF
  cat >"$PREVIOUS_CONSUMER/Program.cs" <<'EOF'
using System.Text;
using ForgeTrust.AppSurface.Config;
using ForgeTrust.AppSurface.Config.GoogleSecretManager;
using ForgeTrust.AppSurface.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var startup = (IAppSurfaceStartup)new LegacyStartup();
var context = new StartupContext([], new LegacyModule());
using var host = startup.CreateHostBuilder(context).Build();
var hostStarted = false;
try
{
    await host.StartAsync();
    hostStarted = true;
    var manager = host.Services.GetRequiredService<IConfigManager>();
    var apiKey = manager.GetValue<string>("Production", "Service:ApiKey");
    var plain = manager.GetValue<string>("Production", "Service.Plain");
    if (apiKey != "legacy-google-value" || plain != "legacy-plain")
        throw new InvalidOperationException("Legacy configuration did not resolve through the default manager.");
    Console.WriteLine("PREVIOUS_OK api-key=legacy-google-value plain=legacy-plain");
}
finally
{
    if (hostStarted) await host.StopAsync();
}

return 0;

public sealed class LegacyModule : IAppSurfaceHostModule
{
    public void RegisterDependentModules(ModuleDependencyBuilder builder)
    {
        builder.AddModule<AppSurfaceConfigModule>();
        builder.AddModule<AppSurfaceGoogleSecretManagerModule>();
    }

    public void ConfigureServices(StartupContext context, IServiceCollection services)
    {
        services.ConfigureAppSurfaceGoogleSecretManager(options =>
        {
            options.ProjectId = "legacy-project";
            options.MapSecret("Service:ApiKey", "legacy-api-key", "4");
        });
        services.UseAppSurfaceGoogleSecretManagerClient(new FakeGoogle());
    }

    public void ConfigureHostBeforeServices(StartupContext context, IHostBuilder builder) { }
    public void ConfigureHostAfterServices(StartupContext context, IHostBuilder builder) { }
}

sealed class LegacyStartup : AppSurfaceStartup<LegacyModule>
{
    protected override void ConfigureServicesForAppType(StartupContext context, IServiceCollection services)
    {
    }
}

sealed class FakeGoogle : IAppSurfaceGoogleSecretManagerClient
{
    public AppSurfaceGoogleSecretPayload AccessSecretVersion(string resourceName, TimeSpan timeout)
    {
        if (resourceName != "projects/legacy-project/secrets/legacy-api-key/versions/4")
            throw new InvalidOperationException("Unexpected legacy resource.");
        return new(Encoding.UTF8.GetBytes("legacy-google-value"), resourceName);
    }
}
EOF
}

restore_and_build_current() {
  run_logged current-restore 300 dotnet restore "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" \
    --packages "$NUGET_PACKAGES" --force-evaluate --source "$CURRENT_FEED" --source https://api.nuget.org/v3/index.json
  run_logged current-build 300 dotnet build "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" --no-restore --configuration Release \
    -p:UseSharedCompilation=false -nodeReuse:false
}

run_current_case() {
  local label="$1" expected="$2"
  shift 2
  local log="$WORK_DIR/current-$label.log"
  run_bounded 30 "$@" >"$log" 2>&1 || { cat "$log" >&2; return 1; }
  grep -Fqx "$expected" "$log" || { echo "Missing exact line '$expected' in $log" >&2; cat "$log" >&2; return 1; }
  cat "$log"
}

manifest() {
  python3 - "$1" "$2" <<'PY'
import hashlib
from pathlib import Path
import sys
root, output = Path(sys.argv[1]), Path(sys.argv[2])
lines = [f"{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(root).as_posix()}" for p in sorted(x for x in root.rglob('*') if x.is_file())]
output.write_text('\n'.join(lines) + '\n')
PY
}

echo "Smoke workspace: $WORK_DIR"
echo "Packing immutable baseline $BASELINE_COMMIT before current packages..."
pack_baseline
write_previous_consumer
run_logged previous-restore 300 dotnet restore "$PREVIOUS_CONSUMER/PreviousConsumer.csproj" \
  --packages "$NUGET_PACKAGES" --force-evaluate --source "$BASELINE_FEED" --source https://api.nuget.org/v3/index.json
run_logged previous-build 300 dotnet build "$PREVIOUS_CONSUMER/PreviousConsumer.csproj" --no-restore --configuration Release \
  -p:UseSharedCompilation=false -nodeReuse:false
run_logged previous-publish 300 dotnet publish "$PREVIOUS_CONSUMER/PreviousConsumer.csproj" --no-restore --configuration Release \
  --output "$PREVIOUS_PUBLISH" -p:UseSharedCompilation=false -nodeReuse:false
cp "$PREVIOUS_CONSUMER/appsettings.Production.json" "$PREVIOUS_PUBLISH/"
run_logged previous-before 30 dotnet "$PREVIOUS_PUBLISH/PreviousConsumer.dll"
grep -Fqx 'PREVIOUS_OK api-key=legacy-google-value plain=legacy-plain' "$WORK_DIR/previous-before.log"
manifest "$PREVIOUS_PUBLISH" "$WORK_DIR/previous-manifest-before.sha256"

pack_current
write_current_consumer
restore_and_build_current
start_seconds="$(python3 -c 'import time; print(time.monotonic())')"
run_current_case disabled 'PASS mode=Production hasValue=False provider=none googleCalls=0' \
  dotnet run --project "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" --configuration Release --no-build --no-restore
run_current_case rescue 'PASS mode=Production hasValue=True provider=EnvironmentConfigProvider googleCalls=0' \
  env FILESECRETREFERENCES__APIKEY=environment-value dotnet run --project "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" --configuration Release --no-build --no-restore
end_seconds="$(python3 -c 'import time; print(time.monotonic())')"
rescue_elapsed="$(python3 - "$start_seconds" "$end_seconds" <<'PY'
import sys
print(f"{float(sys.argv[2]) - float(sys.argv[1]):.3f}")
PY
)"
python3 - "$rescue_elapsed" <<'PY'
import sys
if float(sys.argv[1]) >= 300:
    raise SystemExit(f"disabled+rescue exceeded five minutes: {sys.argv[1]}s")
PY
run_current_case enabled 'PASS mode=Development hasValue=True provider=google-secret-manager googleCalls=1' \
  env DOTNET_ENVIRONMENT=Development dotnet run --project "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" --configuration Release --no-build --no-restore
failure_log="$WORK_DIR/current-failure.log"
run_bounded 30 env DOTNET_ENVIRONMENT=Failure dotnet run --project "$CURRENT_CONSUMER/FileSecretReferencesExample.csproj" --configuration Release --no-build --no-restore -- failure >"$failure_log" 2>&1
grep -Fqx 'EXPECTED FAILURE: secret-not-found at FileSecretReferences:ApiKey' "$failure_log"
! grep -Fq 'fake-google-value' "$failure_log"

run_logged previous-after 30 dotnet "$PREVIOUS_PUBLISH/PreviousConsumer.dll"
grep -Fqx 'PREVIOUS_OK api-key=legacy-google-value plain=legacy-plain' "$WORK_DIR/previous-after.log"
manifest "$PREVIOUS_PUBLISH" "$WORK_DIR/previous-manifest-after.sha256"
cmp -s "$WORK_DIR/previous-manifest-before.sha256" "$WORK_DIR/previous-manifest-after.sha256"

python3 - "$ARTIFACT_REPORT" "$BASELINE_COMMIT" "$CURRENT_VERSION" "$BASELINE_VERSION" "$rescue_elapsed" "$WORK_DIR" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" <<'PY'
from pathlib import Path
import sys
target = Path(sys.argv[1])
target.write_text(f"""# File secret references smoke evidence

- Date: {sys.argv[7]}
- Baseline commit: `{sys.argv[2]}`
- Current package version: `{sys.argv[3]}`
- Baseline package version: `{sys.argv[4]}`
- Current consumer: clean copied example restored from an isolated local feed.
- Cases: disabled no-read, exact uppercase `FILESECRETREFERENCES__APIKEY` rescue, enabled fake Google provenance, and expected missing failure.
- Disabled plus rescue elapsed time: `{sys.argv[5]}s`.
- Rollback: the baseline consumer was published and run before current packages, then the same published files and matching `appsettings.Production.json` were run after current cases.
- Rollback manifest comparison: `previous-manifest-before.sha256` equals `previous-manifest-after.sha256`.
- Logs: `{sys.argv[6]}`.
""")
PY

echo "File secret references package smoke passed."
echo "Evidence: $ARTIFACT_REPORT"
echo "Workspace: $WORK_DIR"
