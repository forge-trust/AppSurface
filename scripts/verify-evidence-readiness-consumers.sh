#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)
configuration=Release
work_root="$repo_root/.gstack/tmp/issue-867-readiness-consumers"

usage() {
  cat <<'EOF'
Usage: scripts/verify-evidence-readiness-consumers.sh [--work-directory DIR] [--configuration NAME]

Requires a clean committed checkout. Exports HEAD and packs that immutable
readiness package closure into a private local feed, then restores, builds, and
runs standalone neutral and typed consumer programs. Run artifacts and stage
logs are retained under .gstack/tmp by default; consumerRoot retains the source
export and standalone programs.
EOF
}

while (($# > 0)); do
  case "$1" in
    --work-directory) work_root=$2; shift 2 ;;
    --configuration) configuration=$2; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

mkdir -p "$work_root"
work_root=$(cd "$work_root" && pwd -P)
run_root=$(mktemp -d "$work_root/run.XXXXXX")
feed="$run_root/feed"
producer_artifacts="$run_root/producer-build"
mkdir -p "$feed"
consumer_root=$(mktemp -d "${TMPDIR:-/tmp}/appsurface-issue867-consumers.XXXXXX")
consumer_root=$(cd "$consumer_root" && pwd -P)
source_commit=$(git -C "$repo_root" rev-parse HEAD)
source_tree_status=clean
source_root="$consumer_root/source"
package_version="0.1.0-issue867.$(date -u +%Y%m%d%H%M%S).$$"
completed=0

report_failure() {
  status=$?
  if (( status != 0 && completed == 0 )); then
    printf 'EVIDENCE_READINESS_CONSUMERS_COMPLETE status=FAIL exitCode=%s logs=%s consumerRoot=%s producerRoot=%s\n' "$status" "$run_root" "$consumer_root" "$source_root"
  fi
}
trap report_failure EXIT

if [[ -n "$(git -C "$repo_root" status --porcelain)" ]]; then
  echo "Package proof requires a clean committed checkout; commit the intended source before running." >&2
  exit 2
fi
mkdir -p "$source_root"
# All package and fixture inputs come from this commit export, even if the checkout changes later.
git -C "$repo_root" archive --format=tar "$source_commit" | tar -xf - -C "$source_root"

stage() {
  name=$1
  shift
  printf '\n=== %s ===\n' "$name"
  printf 'log=%s/%s.log\n' "$run_root" "$name"
  if "$@" 2>&1 | tee "$run_root/$name.log"; then
    printf 'STAGE_COMPLETE name=%s status=PASS\n' "$name"
  else
    status=$?
    printf 'STAGE_COMPLETE name=%s status=FAIL exitCode=%s\n' "$name" "$status" >&2
    return "$status"
  fi
}

# NuGet source attributes must round-trip valid work-directory paths as XML.
feed_xml=${feed//&/'&amp;'}
feed_xml=${feed_xml//</'&lt;'}
feed_xml=${feed_xml//>/'&gt;'}
feed_xml=${feed_xml//\"/'&quot;'}
feed_xml=${feed_xml//$'\t'/'&#x9;'}
feed_xml=${feed_xml//$'\r'/'&#xD;'}
feed_xml=${feed_xml//$'\n'/'&#xA;'}

producer_config="$run_root/nuget-producer.config"
cat >"$producer_config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
EOF

neutral_config="$run_root/nuget-neutral.config"
cat >"$neutral_config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="issue867-local-feed" value="$feed_xml" /></packageSources></configuration>
EOF

typed_config="$run_root/nuget-typed.config"
cat >"$typed_config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="issue867-local-feed" value="$feed_xml" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
EOF

export NUGET_PACKAGES="$run_root/producer-cache"
mkdir -p "$NUGET_PACKAGES"

core="$source_root/ForgeTrust.AppSurface.Core/ForgeTrust.AppSurface.Core.csproj"
readiness="$source_root/Evidence/ForgeTrust.AppSurface.Evidence.Readiness/ForgeTrust.AppSurface.Evidence.Readiness.csproj"
aspire="$source_root/Aspire/ForgeTrust.AppSurface.Aspire/ForgeTrust.AppSurface.Aspire.csproj"
testing="$source_root/Aspire/ForgeTrust.AppSurface.Aspire.Testing/ForgeTrust.AppSurface.Aspire.Testing.csproj"
contracts="$source_root/Evidence/ForgeTrust.AppSurface.Evidence.Contracts/ForgeTrust.AppSurface.Evidence.Contracts.csproj"
evidence_aspire="$source_root/Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj"

stage restore-package-closure dotnet restore "$testing" --artifacts-path "$producer_artifacts" --configfile "$producer_config" --force-evaluate --disable-build-servers -m:1
stage restore-evidence-adapter-closure dotnet restore "$evidence_aspire" --artifacts-path "$producer_artifacts" --configfile "$producer_config" --force-evaluate --disable-build-servers -m:1

for package_project in \
  "$core" \
  "$source_root/Console/ForgeTrust.AppSurface.Console/ForgeTrust.AppSurface.Console.csproj" \
  "$source_root/Deployment/ForgeTrust.AppSurface.Deployment/ForgeTrust.AppSurface.Deployment.csproj" \
  "$readiness" \
  "$aspire" \
  "$testing" \
  "$contracts" \
  "$evidence_aspire"; do
  package_name=$(basename "$(dirname "$package_project")")
  stage "pack-$package_name" dotnet pack "$package_project" --artifacts-path "$producer_artifacts" --no-restore --disable-build-servers -m:1 \
    --configuration "$configuration" --output "$feed" \
    -p:Version="$package_version" -p:PackageVersion="$package_version" \
    -p:RepositoryCommit="$source_commit" -p:SourceRevisionId="$source_commit"
done

neutral_root="$consumer_root/neutral"
mkdir -p "$neutral_root"
sed "s/{{PACKAGE_VERSION}}/$package_version/g" \
  "$source_root/tests/evidence-readiness-consumer/Consumer.csproj.template" >"$neutral_root/Consumer.csproj"
cp "$source_root/tests/evidence-readiness-consumer/Program.cs.fixture" "$neutral_root/Program.cs"
neutral_cache="$run_root/neutral-cache"
export NUGET_PACKAGES="$neutral_cache"
mkdir -p "$neutral_cache"
stage restore-neutral-consumer dotnet restore "$neutral_root/Consumer.csproj" \
  --configfile "$neutral_config" --disable-build-servers -m:1
stage build-neutral-consumer dotnet build "$neutral_root/Consumer.csproj" --no-restore \
  --disable-build-servers -m:1 --configuration "$configuration"
stage run-neutral-consumer bash -c 'cd "$1" && shift && exec "$@"' _ "$neutral_root" \
  dotnet "$neutral_root/bin/$configuration/net10.0/Consumer.dll" \
  --package-version "$package_version" --source-commit "$source_commit" \
  --package-file "$feed/ForgeTrust.AppSurface.Evidence.Readiness.$package_version.nupkg" \
  --package-cache "$neutral_cache" --local-feed "$feed" --source-tree-status "$source_tree_status"

typed_root="$consumer_root/typed"
mkdir -p "$typed_root"
sed "s/{{PACKAGE_VERSION}}/$package_version/g" \
  "$source_root/tests/evidence-readiness-typed-consumer/Consumer.csproj.template" >"$typed_root/Consumer.csproj"
cp "$source_root/tests/evidence-readiness-typed-consumer/Program.cs.fixture" "$typed_root/Program.cs"
typed_cache="$run_root/typed-cache"
export NUGET_PACKAGES="$typed_cache"
mkdir -p "$typed_cache"
stage restore-typed-consumer dotnet restore "$typed_root/Consumer.csproj" \
  --configfile "$typed_config" --disable-build-servers -m:1
stage build-typed-consumer dotnet build "$typed_root/Consumer.csproj" --no-restore \
  --disable-build-servers -m:1 --configuration "$configuration"
stage run-typed-consumer bash -c 'cd "$1" && shift && exec "$@"' _ "$typed_root" \
  dotnet "$typed_root/bin/$configuration/net10.0/EvidenceReadinessTypedConsumer.dll" \
  --package-version "$package_version" --source-commit "$source_commit" --source-tree-status "$source_tree_status" \
  --package-cache "$typed_cache" --local-feed "$feed"

completed=1
printf '\nEVIDENCE_READINESS_CONSUMERS_COMPLETE status=PASS packageVersion=%s sourceCommit=%s sourceTree=%s sourceSnapshot=git-archive feed=%s logs=%s consumerRoot=%s timing=unmeasured\n' \
  "$package_version" "$source_commit" "$source_tree_status" "$feed" "$run_root" "$consumer_root"
