#!/usr/bin/env bash
# Local-only official-SDK, reference-host, and packed-consumer proof. No cloud resources or publication.
set -euo pipefail
script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_directory/.." && pwd)"
proof_output="${1:-$repo_root/artifacts/cloudtasks-activation}"
# Ordinary verification snapshots the candidate commit. Pass a fetched target ref for strict historical acceptance.
if [[ "$#" -gt 2 ]]; then
  printf 'Usage: %s [output-directory] [historical-target-ref]\n' "$0" >&2
  exit 2
fi
if [[ "$#" -eq 2 ]]; then
  if [[ -z "$2" ]]; then
    printf 'The historical target ref must be nonempty; omit it for ordinary verification.\n' >&2
    exit 2
  fi
fi
mkdir -p "$proof_output"
proof_output="$(cd "$proof_output" && pwd)"
cd "$repo_root"
# The coverage build pins the repository's tested .NET 10 SDK in build.yml.
required_sdk="$(sed -n 's/^[[:space:]]*COVERAGE_SDK_VERSION: //p' .github/workflows/build.yml | head -1)"
if [[ "$(dotnet --version)" != "$required_sdk" ]]; then
  printf 'Use repository CI SDK %s on PATH before running this proof.\n' "$required_sdk" >&2
  exit 1
fi
cloudtasks_tests="Durable/ForgeTrust.AppSurface.Durable.CloudTasks.Tests/ForgeTrust.AppSurface.Durable.CloudTasks.Tests.csproj"
host_tests="examples/durable-external-activation.tests/ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj"
package_tool="tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj"
for proof_project in "$cloudtasks_tests" "$host_tests" "$package_tool"; do
  dotnet restore "$proof_project" --locked-mode -p:UseSharedCompilation=false
done
dotnet test "$cloudtasks_tests" --no-restore -p:UseSharedCompilation=false --logger 'console;verbosity=normal'
dotnet test "$host_tests" --no-restore -p:UseSharedCompilation=false --logger 'console;verbosity=normal'
package_proof_args=(verify-packages --package-version 0.2.0-cloudtasksproof.1
  --artifacts-output "$proof_output/packages" --report "$proof_output/package-validation.md"
  --artifact-manifest "$proof_output/package-artifact-manifest.json")
if [[ "$#" -eq 2 ]]; then
  package_proof_args+=(--cloudtasks-baseline-ref "$2")
fi
dotnet run --project "$package_tool" --no-restore -p:UseSharedCompilation=false -- "${package_proof_args[@]}"
printf 'Local Cloud Tasks proof passed: %s\n' "$proof_output"
