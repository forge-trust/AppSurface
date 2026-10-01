#!/usr/bin/env bash
# Provisional fixture only. This script cannot issue admission or a gate claim.
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]] || [[ ! -f /sys/fs/cgroup/cgroup.controllers ]] ||
   [[ "$(cat /proc/1/comm)" != "systemd" ]]; then
    printf 'EvidenceHost proof requires a disposable Linux systemd/cgroup-v2 VM.\n' >&2
    exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
report_directory="${1:?Usage: verify-evidencehost-linux-mechanism.sh /absolute/report-directory}"
if [[ "$report_directory" != /* ]] || [[ -e "$report_directory" ]]; then
    printf 'Use a fresh absolute report directory.\n' >&2
    exit 2
fi
mkdir -m 700 "$report_directory"
fixture_build="$(mktemp -d /var/tmp/evidence779-build.XXXXXXXX)"
trap 'rm -rf -- "$fixture_build"' EXIT
python3 -B "$repo_root/tests/evidencehost-consumer/test_linux_proof.py"

# Build only this explicit proof entry, outside the subject and repository MSBuild files.
cp "$repo_root/tests/evidencehost-consumer/Worker.cs" "$fixture_build/Program.cs"
cat > "$fixture_build/Worker.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
PROJECT
dotnet publish "$fixture_build/Worker.csproj" --configuration Release --output "$fixture_build/published"
cc -std=c11 -O2 -Wall -Wextra -Werror \
    "$repo_root/tests/evidencehost-consumer/linux-allocation-probe.c" -o "$fixture_build/allocation-probe"

# Elevated operations are limited to the disposable proof, which creates separate non-login users.
sudo --non-interactive "$fixture_build/allocation-probe" > "$report_directory/allocation.json"
sudo --non-interactive python3 "$repo_root/tests/evidencehost-consumer/linux-proof.py" \
    --worker "$fixture_build/published/Worker.dll" \
    --dotnet "$(readlink -f "$(command -v dotnet)")" \
    --report "$report_directory/supervision.json"
python3 - "$report_directory" <<'PYTHON'
import hashlib
import json
import pathlib
import subprocess
import sys

reports = pathlib.Path(sys.argv[1])
revision = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
manifest = {
    "schema": "issue779-mechanism-artifacts-v1",
    "admission": "none",
    "revision": revision,
    "files": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
              for path in reports.glob("*.json")},
}
(reports / "artifacts.json").write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
PYTHON
