#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fixture_root="$repo_root/tests/evidencehost-cleanup-consumer"
work_directory=
total_timeout_seconds=900
package_version="${PACKAGE_VERSION:-0.1.0-evidencehost-cleanup.$(date +%Y%m%d%H%M%S)}"

usage() {
  cat <<'EOF'
Usage: scripts/verify-evidencehost-cleanup-package.sh [options]

Options:
  --work-directory DIR          New directory for the package feed and readable logs.
  --package-version VERSION     Candidate version for both packages and the consumer.
  --total-timeout-seconds N     Whole-operation deadline (default: 900 seconds).
EOF
}

while (($# > 0)); do
  case "$1" in
    --work-directory) work_directory=$2; shift 2 ;;
    --package-version) package_version=$2; shift 2 ;;
    --total-timeout-seconds) total_timeout_seconds=$2; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ! [[ "$total_timeout_seconds" =~ ^[1-9][0-9]*$ ]]; then
  echo "--total-timeout-seconds must be a positive integer." >&2
  exit 2
fi

if [[ -z "$work_directory" ]]; then
  work_directory=$(mktemp -d "${TMPDIR:-/tmp}/appsurface-evidencehost-cleanup.XXXXXX")
else
  if [[ -e "$work_directory" ]]; then
    echo "Work directory already exists; choose a new path: $work_directory" >&2
    exit 2
  fi
  mkdir -p "$(dirname "$work_directory")"
  mkdir "$work_directory"
fi

echo "EvidenceHost package-consumer workspace: $work_directory"
echo "Candidate package version: $package_version"
echo "Whole-operation deadline: ${total_timeout_seconds}s"

exec python3 - "$repo_root" "$fixture_root" "$work_directory" "$package_version" "$total_timeout_seconds" <<'PY'
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time
import zipfile
import xml.etree.ElementTree as ET

repo_root = Path(sys.argv[1]).resolve()
fixture_root = Path(sys.argv[2]).resolve()
work_root = Path(sys.argv[3]).resolve()
package_version = sys.argv[4]
total_timeout = int(sys.argv[5])
deadline = time.monotonic() + total_timeout
source_root = work_root / "pack-source"
feed_root = work_root / "local-feed"
consumer_root = work_root / "consumer"
package_cache = work_root / "nuget-packages"
logs_root = work_root / "logs"
operation_log = work_root / "operation.log"
result_file = work_root / "result.txt"
interrupted_signal = None

class VerificationInterrupted(Exception):
    def __init__(self, signum):
        self.signum = signum
        super().__init__(f"Verification interrupted by {signal.Signals(signum).name}.")

def request_interruption(signum, frame):
    # Record rather than raise inside Popen: the stage must acquire its handle
    # before cancellation can unwind and stop the independently grouped child.
    global interrupted_signal
    if interrupted_signal is None:
        interrupted_signal = signum

def check_interruption():
    if interrupted_signal is not None:
        raise VerificationInterrupted(interrupted_signal)

signal.signal(signal.SIGINT, request_interruption)
signal.signal(signal.SIGTERM, request_interruption)

logs_root.mkdir(parents=True, exist_ok=True)
feed_root.mkdir()

def record(message):
    line = message.rstrip() + "\n"
    print(line, end="", flush=True)
    with operation_log.open("a", encoding="utf-8") as stream:
        stream.write(line)

def run_stage(label, argv, cwd, env):
    check_interruption()
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise TimeoutError(f"Whole-operation deadline expired before {label}.")

    log_path = logs_root / f"{label}.log"
    record(f"[{label}] {' '.join(str(part) for part in argv)}")
    with log_path.open("wb") as log:
        process = subprocess.Popen(
            [str(part) for part in argv],
            cwd=cwd,
            env=env,
            stdout=log,
            stderr=subprocess.STDOUT,
            start_new_session=True,
        )
        try:
            while True:
                check_interruption()
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(f"Whole-operation deadline expired during {label}; process group was stopped.")
                try:
                    return_code = process.wait(timeout=min(remaining, 0.25))
                    check_interruption()
                    break
                except subprocess.TimeoutExpired:
                    continue
        except BaseException:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait(timeout=5)
            print(log_path.read_text(encoding="utf-8", errors="replace"), end="", flush=True)
            raise

    output = log_path.read_text(encoding="utf-8", errors="replace")
    if output:
        print(output, end="" if output.endswith("\n") else "\n", flush=True)
    with operation_log.open("a", encoding="utf-8") as stream:
        stream.write(output)
        if output and not output.endswith("\n"):
            stream.write("\n")
    if return_code != 0:
        raise RuntimeError(f"Stage {label} exited with status {return_code}; see {log_path}.")
    record(f"[{label}] PASS")

def copy_build_inputs():
    source_root.mkdir()
    for name in ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config", "NuGet.Config", "LICENSE"):
        candidate = repo_root / name
        if candidate.is_file():
            shutil.copy2(candidate, source_root / name)

    for relative in (
        Path("Evidence/ForgeTrust.AppSurface.Evidence.Contracts"),
        Path("Evidence/ForgeTrust.AppSurface.Evidence.Aspire"),
    ):
        shutil.copytree(repo_root / relative, source_root / relative, ignore=shutil.ignore_patterns("bin", "obj", "packages.lock.json"))

    shutil.copytree(fixture_root, consumer_root, ignore=shutil.ignore_patterns("bin", "obj", "packages.lock.json"))

def package_id(path):
    with zipfile.ZipFile(path) as archive:
        nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
        root = ET.fromstring(archive.read(nuspec))
    metadata = next(node for node in root if node.tag.endswith("metadata"))
    values = {node.tag.split("}")[-1]: node.text for node in metadata}
    return values

def verify_assets():
    assets_path = consumer_root / "obj" / "project.assets.json"
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    framework = assets["project"]["frameworks"]["net10.0"]
    direct = framework["dependencies"]
    if set(direct) != {"ForgeTrust.AppSurface.Evidence.Aspire"}:
        raise RuntimeError(f"Expected exactly one direct package dependency, found: {sorted(direct)}")
    if assets["project"]["restore"].get("projectReferences"):
        raise RuntimeError("Restored consumer assets contain project references.")
    if any(value.get("type") != "package" for value in assets["targets"]["net10.0"].values()):
        raise RuntimeError("Restored consumer assets contain a non-package target library.")

try:
    check_interruption()
    copy_build_inputs()
    env = os.environ.copy()
    env["NUGET_PACKAGES"] = str(package_cache)
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise RuntimeError("The .NET SDK executable 'dotnet' was not found on PATH.")

    pack_common = ["--configuration", "Release", "--output", feed_root, f"-p:Version={package_version}", f"-p:PackageVersion={package_version}", "-p:UseSharedCompilation=false", "-nodeReuse:false"]
    contracts_project = source_root / "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/ForgeTrust.AppSurface.Evidence.Contracts.csproj"
    aspire_project = source_root / "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj"
    run_stage("pack-contracts", [dotnet, "pack", contracts_project, *pack_common], source_root, env)
    run_stage("pack-aspire", [dotnet, "pack", aspire_project, *pack_common], source_root, env)

    packages = sorted(feed_root.glob("*.nupkg"))
    expected_ids = {"ForgeTrust.AppSurface.Evidence.Aspire", "ForgeTrust.AppSurface.Evidence.Contracts"}
    found = {package_id(path)["id"]: package_id(path)["version"] for path in packages}
    if found != {name: package_version for name in expected_ids}:
        raise RuntimeError(f"Packed feed did not contain exactly the two expected candidate packages: {found}")
    record("[feed] Contains Aspire and Contracts at the candidate version")

    consumer_project = consumer_root / "EvidenceHostCleanupConsumer.csproj"
    if any(node.tag.endswith("ProjectReference") for node in ET.parse(consumer_project).iter("ProjectReference")):
        raise RuntimeError("Consumer project declares a ProjectReference.")
    nuget_org = "https://api.nuget.org/v3/index.json"
    run_stage(
        "restore-consumer",
        [dotnet, "restore", consumer_project, "--force", "--packages", package_cache, "--source", feed_root, "--source", nuget_org, f"-p:EvidenceHostPackageVersion={package_version}"],
        consumer_root,
        env,
    )
    verify_assets()
    record("[assets] One direct Aspire package; no project references; all resolved targets are packages")

    run_stage(
        "build-consumer",
        [dotnet, "build", consumer_project, "--no-restore", "--configuration", "Release", f"-p:EvidenceHostPackageVersion={package_version}"],
        consumer_root,
        env,
    )
    consumer_dll = consumer_root / "bin/Release/net10.0/EvidenceHostCleanupConsumer.dll"
    run_stage("run-consumer", [dotnet, consumer_dll], consumer_root, env)

    check_interruption()
    elapsed = total_timeout - max(0.0, deadline - time.monotonic())
    report = (
        "EvidenceHost cleanup package consumer: PASS\n"
        f"Package version: {package_version}\n"
        f"Elapsed seconds: {elapsed:.1f}\n"
        "Consumer: standalone net10.0 console app; sole direct package is ForgeTrust.AppSurface.Evidence.Aspire\n"
        "Scenarios: safe no-claim cleanup timeout; direct ASEVD306; stop/join before disposal; primary failure with cleanup diagnostic\n"
        "Host terminal output bound: 3 seconds per scenario\n"
        f"Logs: {logs_root}\n"
    )
    result_file.write_text(report, encoding="utf-8")
    record(report)
except Exception as error:
    report = (
        "EvidenceHost cleanup package consumer: FAIL\n"
        f"Package version: {package_version}\n"
        f"Failure: {type(error).__name__}: {error}\n"
        f"Operation log: {operation_log}\n"
        f"Stage logs: {logs_root}\n"
    )
    result_file.write_text(report, encoding="utf-8")
    record(report)
    if isinstance(error, VerificationInterrupted):
        raise SystemExit(128 + error.signum)
    raise
PY
