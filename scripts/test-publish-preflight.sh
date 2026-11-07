#!/usr/bin/env bash
set -euo pipefail

[[ $(uname -s) == Linux ]] || { printf '%s\n' 'Run these publish fixtures on Linux.' >&2; exit 2; }
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
scratch=$(mktemp -d)
trap 'rm -rf -- "$scratch"' EXIT
scratch=$(cd "$scratch" && pwd -P)
targets="$root/tools/PublishPreflight/PublishPreflight.targets"
dotnet build "$root/tools/PublishPreflight/ForgeTrust.AppSurface.PublishPreflight.csproj" -c Release

mkdir -p "$scratch/fixture/fixture-inputs" "$scratch/fixture/wwwroot/content"
cd "$scratch/fixture"
cat > Fixture.csproj <<'XML'
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <None Update="fixture-inputs/first.txt" CopyToPublishDirectory="Always" TargetPath="runtime/first.txt" Condition="'$(FixtureMode)' == 'clean'" />
    <None Update="fixture-inputs/second.txt" CopyToPublishDirectory="Always" TargetPath="wwwroot/Content/badge.txt" Condition="'$(FixtureMode)' == 'cross'" />
    <None Update="fixture-inputs/first.txt" CopyToPublishDirectory="Always" TargetPath="content/Badge.txt" Condition="'$(FixtureMode)' == 'ordinary'" />
    <None Update="fixture-inputs/second.txt" CopyToPublishDirectory="Always" TargetPath="Content/badge.txt" Condition="'$(FixtureMode)' == 'ordinary'" />
    <None Update="fixture-inputs/options.txt" CopyToPublishDirectory="Always" TargetPath="runtime/options.txt" Condition="'$(FixtureMode)' == 'unsafe'" />
  </ItemGroup>
  <Import Project="$(FixtureTargets)" Condition="'$(AppSurfacePublishPreflight)' == 'true'" />
  <Target Name="CaptureFixtureInventory" AfterTargets="Publish" Condition="'$(FixtureInventory)' != ''">
    <WriteLinesToFile File="$(FixtureInventory)/ordinary.txt" Overwrite="true"
      Lines="@(_ResolvedFileToPublishPreserveNewest->'%(Identity)|%(RelativePath)');@(_ResolvedFileToPublishAlways->'%(Identity)|%(RelativePath)')" />
    <WriteLinesToFile File="$(FixtureInventory)/static.txt" Overwrite="true"
      Lines="@(_PublishStaticWebAssetsPreserveNewest->'%(Identity)|%(TargetPath)');@(_PublishStaticWebAssetsCopyAlways->'%(Identity)|%(TargetPath)')" />
    <WriteLinesToFile File="$(FixtureInventory)/checked-static.txt" Overwrite="true"
      Lines="@(_AppSurfaceStaticPublishPaths->'%(Identity)|%(TargetPath)')" />
  </Target>
</Project>
XML
cat > Program.cs <<'CS'
var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
CS
printf 'first\n' > fixture-inputs/first.txt
printf 'second\n' > fixture-inputs/second.txt
printf 'fixture only\n' > fixture-inputs/options.txt
cp fixture-inputs/first.txt wwwroot/content/Badge.txt
chmod 0666 fixture-inputs/options.txt
dotnet restore Fixture.csproj --ignore-failed-sources -v:quiet

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  if [[ $# -gt 1 ]]; then grep -E 'error |ASPUB00[123]' "$2" | tail -18 >&2 || true; fi
  exit 1
}

publish() {
  local mode=$1 name=$2 enabled=$3
  local fixture_targets=${4:-$targets}
  mkdir -p "$scratch/$name"
  dotnet publish Fixture.csproj -c Release --no-restore --no-self-contained -v:diag \
    -o "$scratch/$name/output" "-p:FixtureMode=$mode" "-p:FixtureTargets=$fixture_targets" \
    "-p:FixtureInventory=$scratch/$name" "-p:AppSurfacePublishPreflight=$enabled" \
    "-p:AppSurfacePreflightReceiptDirectory=$scratch/$name" >"$scratch/$name.log" 2>&1
  local result=$?
  if [[ $result -eq 0 && "$enabled" == true ]]; then
    touch "$scratch/$name/coverage-started"
  fi
  return "$result"
}

target_line() {
  awk -v name="$2" 'index($0, ">Target \"" name ": (TargetId:") { print NR; exit }' "$1"
}

ordinary_writers=(_CopyResolvedFilesToPublishPreserveNewest _CopyResolvedFilesToPublishAlways)
static_writers=(_PublishCopyStaticWebAssetsPreserveNewest _PublishCopyStaticWebAssetsAlways)
writers=("${ordinary_writers[@]}" "${static_writers[@]}")

publish cross baseline false || fail 'ordinary cross-list baseline' "$scratch/baseline.log"
cmp fixture-inputs/first.txt "$scratch/baseline/output/wwwroot/content/Badge.txt"
cmp fixture-inputs/second.txt "$scratch/baseline/output/wwwroot/Content/badge.txt"
[[ ! -e "$scratch/baseline/paths.ok" && -z $(target_line "$scratch/baseline.log" AppSurfaceCheckPublishPaths) ]]

for mode in cross ordinary; do
  if publish "$mode" "$mode" true; then fail "$mode unexpectedly succeeded"; fi
  log="$scratch/$mode.log"
  grep -q 'error ASPUB001' "$log" || fail "$mode diagnostic" "$log"
  grep -Fq 'content/Badge.txt' "$log"
  grep -Fq 'Content/badge.txt' "$log"
  for writer in "${writers[@]}"; do
    [[ -z $(target_line "$log" "$writer") ]] || fail "$mode started $writer"
  done
  [[ ! -e "$scratch/$mode/paths.ok" && ! -e "$scratch/$mode/permissions.ok" && ! -e "$scratch/$mode/coverage-started" ]]
  [[ -z $(find "$scratch/$mode/output" -type f -print -quit) ]] || fail "$mode wrote a publish payload"
  printf 'PASS: %s collision refused before either writer and coverage\n' "$mode"
done

if publish unsafe unsafe true; then fail 'unsafe output unexpectedly succeeded'; fi
grep -E 'error ASPUB002.*runtime/options\.txt.*0666' "$scratch/unsafe.log" >/dev/null || fail 'unsafe mode diagnostic' "$scratch/unsafe.log"
[[ -s "$scratch/unsafe/paths.ok" && ! -e "$scratch/unsafe/permissions.ok" && ! -e "$scratch/unsafe/coverage-started" ]]
[[ $(stat -c %a fixture-inputs/options.txt) == 666 && $(stat -c %a "$scratch/unsafe/output/runtime/options.txt") == 666 ]]
printf '%s\n' 'PASS: published 0666 mode refused, unchanged, before coverage'

publish clean plain-clean false || fail 'clean ordinary publish' "$scratch/plain-clean.log"
publish clean guarded true || fail 'clean guarded publish' "$scratch/guarded.log"
[[ -s "$scratch/guarded/paths.ok" && -s "$scratch/guarded/permissions.ok" && -f "$scratch/guarded/coverage-started" ]]
log="$scratch/guarded.log"
paths_line=$(target_line "$log" AppSurfaceCheckPublishPaths)
permissions_line=$(target_line "$log" AppSurfaceCheckPublishPermissions)
[[ -n "$paths_line" && -n "$permissions_line" ]]
check_writer_order() {
  local found=false writer line
  for writer in "$@"; do
    line=$(target_line "$log" "$writer")
    [[ -n "$line" ]] || continue  # A copy option can have no matching inputs.
    found=true
    (( paths_line < line && line < permissions_line )) || fail "wrong order for $writer"
  done
  [[ "$found" == true ]] || fail "no writer ran in pipeline: $*"
}
check_writer_order "${ordinary_writers[@]}"
check_writer_order "${static_writers[@]}"
check_writer_order _TransformWebConfig
for list in ordinary static; do
  [[ -s "$scratch/guarded/$list.txt" ]]
  diff -u <(sort "$scratch/plain-clean/$list.txt") <(sort "$scratch/guarded/$list.txt")
done
[[ -s "$scratch/guarded/checked-static.txt" ]]
diff -u <(sort "$scratch/guarded/static.txt") <(sort "$scratch/guarded/checked-static.txt")
printf '%s\n' 'PASS: clean guard order, both SDK lists match, coverage sentinel reached'

# A failed invocation must not leave success receipts from an earlier invocation.
publish clean reused true || fail 'initial reused-directory publish' "$scratch/reused.log"
rm -rf -- "$scratch/reused/output"
rm -- "$scratch/reused/coverage-started"
if publish cross reused true; then fail 'reused-directory collision unexpectedly succeeded'; fi
grep -q 'error ASPUB001' "$scratch/reused.log" || fail 'reused collision diagnostic' "$scratch/reused.log"
[[ ! -e "$scratch/reused/paths.ok" && ! -e "$scratch/reused/permissions.ok" && ! -e "$scratch/reused/coverage-started" ]] ||
  fail 'reused directory retained stale success receipts'
[[ -z $(find "$scratch/reused/output" -type f -print -quit) ]]
printf '%s\n' 'PASS: rejected publish removes both prior receipts from a reused directory'

# A permissions-only target call cannot borrow a successful prior path check.
if dotnet msbuild Fixture.csproj -t:AppSurfaceCheckPublishPermissions \
  -p:Configuration=Release "-p:PublishDir=$scratch/guarded/output/" \
  "-p:FixtureTargets=$targets" -p:AppSurfacePublishPreflight=true \
  "-p:AppSurfacePreflightReceiptDirectory=$scratch/guarded" >"$scratch/permissions-only.log" 2>&1; then
  fail 'permissions-only call accepted stale path receipt'
fi
grep -q 'error ASPUB003' "$scratch/permissions-only.log" ||
  fail 'permissions-only diagnostic' "$scratch/permissions-only.log"
printf '%s\n' 'PASS: permissions phase requires the path check in the current invocation'

# Configuration refusals clear previous receipts too.
mkdir "$scratch/unsupported-config"
cp "$scratch/guarded/paths.ok" "$scratch/guarded/permissions.ok" "$scratch/unsupported-config/"
if dotnet msbuild Fixture.csproj -t:AppSurfaceCheckPublishPaths -p:Configuration=Debug \
  "-p:FixtureTargets=$targets" -p:AppSurfacePublishPreflight=true \
  "-p:AppSurfacePreflightReceiptDirectory=$scratch/unsupported-config" >"$scratch/unsupported-config.log" 2>&1; then
  fail 'unsupported configuration unexpectedly succeeded'
fi
grep -q 'error ASPUB003' "$scratch/unsupported-config.log" ||
  fail 'unsupported configuration diagnostic' "$scratch/unsupported-config.log"
[[ ! -e "$scratch/unsupported-config/paths.ok" && ! -e "$scratch/unsupported-config/permissions.ok" ]]
printf '%s\n' 'PASS: configuration refusal removes previous success receipts'

# Copy only the import to represent a checkout where the Release task was not built.
mkdir "$scratch/missing-task"
cp "$targets" "$scratch/missing-task/PublishPreflight.targets"
if publish clean missing-task true "$scratch/missing-task/PublishPreflight.targets"; then
  fail 'missing task assembly unexpectedly succeeded'
fi
grep -E 'error ASPUB003.*publish-preflight\.sh' "$scratch/missing-task.log" >/dev/null ||
  fail 'missing task assembly diagnostic' "$scratch/missing-task.log"
for writer in "${writers[@]}"; do
  [[ -z $(target_line "$scratch/missing-task.log" "$writer") ]] || fail "missing task started $writer"
done
[[ ! -e "$scratch/missing-task/paths.ok" && ! -e "$scratch/missing-task/permissions.ok" ]]
printf '%s\n' 'PASS: missing task assembly fails with actionable ASPUB003 before copying'

printf '\n#error anonymized SDK failure\n' >> Program.cs
if publish clean sdk-error true; then fail 'SDK error unexpectedly succeeded'; fi
grep -q 'error CS1029' "$scratch/sdk-error.log" || fail 'SDK diagnostic was lost' "$scratch/sdk-error.log"
[[ ! -e "$scratch/sdk-error/coverage-started" ]]
printf '%s\n' 'PASS: original SDK failure propagates without starting coverage'
