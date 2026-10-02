#!/usr/bin/env python3
"""Run the production EvidenceHost Linux Observation consumer proof on a disposable Ubuntu VM."""
from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
import platform
import re
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[2]
CLI_PROJECT = ROOT / "Cli" / "ForgeTrust.AppSurface.Cli" / "ForgeTrust.AppSurface.Cli.csproj"
CLI_DLL_NAME = "ForgeTrust.AppSurface.Cli.dll"
SUBJECT_PROJECT_RELATIVE = "tests/evidencehost-consumer/RuntimeSubject/RuntimeSubject.csproj"
SUBJECT_PROJECT = ROOT / SUBJECT_PROJECT_RELATIVE
LAUNCHER = ROOT / "scripts" / "evidencehost-linux-launcher.py"
DRIVER = Path(__file__).resolve()
REPORT_GENERATOR_SOURCE = ROOT / "Cli" / "ForgeTrust.AppSurface.Cli" / "CoverageRun.cs"
REPORT_GENERATOR_PUBLISH_VERSION = "5.5.10"
DOTNET_EXECUTABLE: Path | None = None
PROFILE_ID = "runtime-observation"
PRODUCER_ID = "runtime-coverage"
ASSERTION_ID = "appsurface/coverage/behavioral-patch@1"
MAX_REPORT_BYTES = 20 * 1024 * 1024
JOB_SECONDS = 420
ADMISSION_SECONDS = 30
START_SECONDS = 60
COLLECTION_SECONDS = 60
CLEANUP_SECONDS = 120
STOPPING_SECONDS = 15
PRODUCER_SECONDS = 120
LAUNCHER_TIMEOUT_SECONDS = JOB_SECONDS + 90
WARNING_DIAGNOSTIC = re.compile(r"(?im)^.*\bwarning\s+[A-Z]{2,}[0-9]+\s*:")
WARNING_SUMMARY = re.compile(r"(?im)^\s*([0-9]+)\s+Warning\(s\)\s*$")
BUILD_LOG_FILES = (
    "subject-build.stdout.log", "subject-build.stderr.log",
    "cli-build.stdout.log", "cli-build.stderr.log",
    "cli-publish.stdout.log", "cli-publish.stderr.log",
)
RUN_ID_PATTERN = re.compile(r"^([0-9]+)/([0-9]+)$")
SHA_PATTERN = re.compile(r"^[0-9a-fA-F]{40,64}$")


class ProofFailure(RuntimeError):
    """Bounded, non-secret failure for one proof command or assertion."""


def fail(message: str) -> None:
    raise ProofFailure(message)


def bounded_run(
    argv: list[str],
    *,
    cwd: Path,
    env: dict[str, str],
    timeout: int,
    label: str,
    binary_output: bool = False,
    log_prefix: Path | None = None,
) -> tuple[int, bytes, bytes]:
    """Run one process tree with its own deadline and bounded diagnostic rendering."""
    try:
        process = subprocess.Popen(
            argv,
            cwd=cwd,
            env=env,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            start_new_session=True,
        )
    except OSError as error:
        fail(f"{label} could not start ({type(error).__name__}).")

    stdout, stderr = b"", b""
    try:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                stdout, stderr = process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                stdout, stderr = process.communicate()
            fail(f"{label} exceeded its {timeout}-second process deadline.")
    finally:
        if log_prefix is not None:
            for stream, content in (("stdout", stdout), ("stderr", stderr)):
                path = log_prefix.with_name(f"{log_prefix.name}.{stream}.log")
                path.write_bytes(content)
                os.chmod(path, 0o644)

    if not binary_output:
        stdout.decode("utf-8", "replace")
        stderr.decode("utf-8", "replace")
    return process.returncode, stdout, stderr


def render_tail(stdout: bytes, stderr: bytes, limit: int = 6000) -> str:
    combined = (stdout + b"\n" + stderr).decode("utf-8", "replace")
    return combined[-limit:].strip()


def run_success(
    argv: list[str], *, cwd: Path, env: dict[str, str], timeout: int, label: str,
    log_prefix: Path | None = None,
) -> tuple[bytes, bytes]:
    code, stdout, stderr = bounded_run(
        argv, cwd=cwd, env=env, timeout=timeout, label=label, log_prefix=log_prefix,
    )
    if code != 0:
        detail = render_tail(stdout, stderr)
        fail(f"{label} exited {code}." + (f"\n{detail}" if detail else ""))
    return stdout, stderr


def dotnet_host() -> Path:
    if DOTNET_EXECUTABLE is None:
        fail("The sanitized .NET host path was not initialized.")
    return DOTNET_EXECUTABLE


def resolve_dotnet_host() -> Path:
    selected = shutil.which("dotnet", path=os.environ.get("PATH", ""))
    if not selected:
        fail("The workflow-selected .NET SDK host is not on the runner PATH.")
    try:
        executable = Path(selected).resolve(strict=True)
        launcher_executable = Path("/usr/bin/dotnet").resolve(strict=True)
    except OSError:
        fail("The workflow-selected or launcher-visible .NET SDK host could not be resolved.")
    if executable != launcher_executable:
        fail("The workflow-selected .NET SDK differs from the host selected by the root launcher.")
    executable = launcher_executable
    if not executable.is_file() or not os.access(executable, os.X_OK):
        fail("The workflow-selected .NET SDK host is not an executable file.")
    if any(character.isspace() for character in str(executable)) or os.pathsep in str(executable.parent):
        fail("The resolved .NET SDK host path is not safe for the protected launcher.")
    return executable


def sanitized_environment(work_root: Path, executable: Path) -> dict[str, str]:
    temp_root = work_root / "tmp"
    cache_root = work_root / "cache"
    dotnet_home = cache_root / "dotnet"
    nuget_root = cache_root / "nuget"
    for directory in (temp_root, dotnet_home, nuget_root):
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    home = work_root / "home"
    home.mkdir(mode=0o700)
    return {
        "PATH": f"{executable.parent}:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "HOME": str(home),
        "LANG": "C.UTF-8",
        "TMPDIR": str(temp_root),
        "DOTNET_CLI_HOME": str(dotnet_home),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_HOST_PATH": str(executable),
        "NUGET_PACKAGES": str(nuget_root),
    }


def require_no_warning_diagnostics(output: bytes, label: str) -> None:
    text = output.decode("utf-8", "replace")
    if WARNING_DIAGNOSTIC.search(text):
        fail(f"{label} emitted a warning diagnostic despite warnings-as-errors.")


def require_zero_warning_build(output: bytes, label: str) -> None:
    require_no_warning_diagnostics(output, label)
    text = output.decode("utf-8", "replace")
    counts = WARNING_SUMMARY.findall(text)
    if not counts or any(int(value) != 0 for value in counts):
        fail(f"{label} did not prove an exact zero-warning build summary.")


def source_paths() -> list[Path]:
    relative_paths = [
        "scripts/verify-evidencehost-linux-runtime.sh",
        "scripts/evidencehost-linux-launcher.py",
        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
        "Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs",
        "Cli/ForgeTrust.AppSurface.Cli/CoverageRun.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceProtectedCliExecution.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageProducer.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageTransport.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceCliWorkflow.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/CoverageExecutionBoundary.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceProtectedWorkerInputs.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidencePlanner.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceAdmission.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxWorkerSupervisor.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxArtifactRoot.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceModeSelection.cs",
        ".github/workflows/evidencehost-runtime-proof.yml",
        "tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj",
        "tests/evidencehost-consumer/ControlProtocolWorker/Program.cs",
        "tests/evidencehost-consumer/test_control_protocol.py",
        "tests/evidencehost-consumer/LifecycleWorker/LifecycleWorker.csproj",
        "tests/evidencehost-consumer/LifecycleWorker/Program.cs",
        "tests/evidencehost-consumer/test_lifecycle_worker.py",
        "tests/evidencehost-consumer/RuntimeSubject/RuntimeSubject.csproj",
        "tests/evidencehost-consumer/RuntimeSubject/Program.cs",
        "tests/evidencehost-consumer/RuntimeSubject/README.md",
        "tests/evidencehost-consumer/runtime-proof.py",
        "tests/evidencehost-consumer/test_runtime_proof.py",
    ]
    return [ROOT / item for item in relative_paths]


def hash_sources() -> tuple[dict[str, str], str]:
    hashes: dict[str, str] = {}
    for path in source_paths():
        if not path.is_file() or path.is_symlink():
            fail(f"Required proof source is missing or unsafe: {path.relative_to(ROOT)}")
        relative = path.relative_to(ROOT).as_posix()
        hashes[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    encoded = json.dumps(hashes, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashes, hashlib.sha256(encoded).hexdigest()


def read_bindings() -> dict[str, str]:
    names = (
        "EVIDENCEHOST_PROOF_LABEL",
        "EVIDENCEHOST_WORKFLOW_IDENTITY",
        "EVIDENCEHOST_SOURCE_HEAD_SHA",
        "EVIDENCEHOST_BASE_REVISION",
        "EVIDENCEHOST_RUN_ID",
        "EVIDENCEHOST_RUN_ATTEMPT",
        "EVIDENCEHOST_OUTPUT_PARENT",
    )
    values = {name: os.environ.get(name, "") for name in names}
    missing = [name for name, value in values.items() if not value]
    if missing:
        fail("Required workflow bindings are missing: " + ", ".join(missing))
    if values["EVIDENCEHOST_PROOF_LABEL"] != "candidate-no-trust-acceptance":
        fail("EVIDENCEHOST_PROOF_LABEL must be candidate-no-trust-acceptance.")
    if not values["EVIDENCEHOST_RUN_ID"].isdigit() or not values["EVIDENCEHOST_RUN_ATTEMPT"].isdigit():
        fail("EVIDENCEHOST_RUN_ID and EVIDENCEHOST_RUN_ATTEMPT must be decimal values.")
    values["runId"] = f"{values['EVIDENCEHOST_RUN_ID']}/{values['EVIDENCEHOST_RUN_ATTEMPT']}"
    for name in ("EVIDENCEHOST_BASE_REVISION", "EVIDENCEHOST_SOURCE_HEAD_SHA"):
        if not SHA_PATTERN.fullmatch(values[name]):
            fail(f"{name} must be a full hexadecimal Git revision.")
    if len(values["EVIDENCEHOST_WORKFLOW_IDENTITY"]) > 256 or any(
        ord(character) < 32 for character in values["EVIDENCEHOST_WORKFLOW_IDENTITY"]
    ):
        fail("EVIDENCEHOST_WORKFLOW_IDENTITY is empty, too long, or contains control characters.")
    output_parent = Path(values["EVIDENCEHOST_OUTPUT_PARENT"])
    if not output_parent.is_absolute() or output_parent.exists() or output_parent.is_symlink():
        fail("EVIDENCEHOST_OUTPUT_PARENT must be an absolute path that does not exist yet.")
    values["EVIDENCEHOST_OUTPUT_PARENT"] = str(output_parent)
    values["EVIDENCE_RUN_ID"] = values["runId"]
    values["EVIDENCE_BASE_REVISION"] = values["EVIDENCEHOST_BASE_REVISION"]
    values["EVIDENCE_SUBJECT_REVISION"] = values["EVIDENCEHOST_SOURCE_HEAD_SHA"]
    values["EVIDENCE_WORKFLOW_IDENTITY"] = values["EVIDENCEHOST_WORKFLOW_IDENTITY"]
    return values


def git_head(env: dict[str, str]) -> str:
    stdout, _ = run_success(
        ["git", "rev-parse", "HEAD"], cwd=ROOT, env=env, timeout=15, label="checkout revision lookup"
    )
    value = stdout.decode("ascii", "strict").strip()
    if value != os.environ["EVIDENCEHOST_SOURCE_HEAD_SHA"]:
        fail("EVIDENCEHOST_SOURCE_HEAD_SHA does not match the checked-out commit.")
    return value


def require_linux_host(env: dict[str, str]) -> None:
    if sys.platform != "linux" or platform.machine() not in ("x86_64", "amd64"):
        fail("This proof requires an x86_64 Linux host.")
    os_release = Path("/etc/os-release").read_text(encoding="utf-8")
    if not re.search(r"(?m)^ID=\"?ubuntu\"?$", os_release) or not re.search(
        r"(?m)^VERSION_ID=\"24\.04\"$", os_release
    ):
        fail("This proof requires Ubuntu 24.04.")
    if Path("/proc/1/comm").read_text(encoding="ascii").strip() != "systemd":
        fail("PID 1 must be systemd; this harness does not treat containers as a skip.")
    if " - cgroup2 " not in Path("/proc/self/mountinfo").read_text(encoding="utf-8"):
        fail("A unified cgroup-v2 mount is required.")
    stdout, stderr = run_success(
        ["systemctl", "--version"], cwd=ROOT, env=env, timeout=10, label="systemd version check"
    )
    version = (stdout + stderr).decode("utf-8", "replace").splitlines()
    if not version or not re.match(r"^systemd\s+255(?:\s|$)", version[0]):
        fail("This proof requires systemd 255.")


def create_policy() -> dict:
    return {
        "id": "evidencehost-runtime-proof",
        "version": "1",
        "conservativeProfileId": PROFILE_ID,
        "profiles": [
            {
                "id": PROFILE_ID,
                "scope": "Targeted",
                "resources": [],
                "producers": [
                    {
                        "id": PRODUCER_ID,
                        "kind": "coverage",
                        "version": "1.0.0",
                        "requiredResources": [],
                        "assertionIds": [ASSERTION_ID],
                        "artifactSlots": [
                            {
                                "logicalName": "coverage-report",
                                "relativeRoot": "merged",
                                "mediaType": "application/xml",
                                "required": True,
                                "maximumBytes": MAX_REPORT_BYTES,
                            }
                        ],
                        "timeoutSeconds": PRODUCER_SECONDS,
                        "coverageGate": {
                            "minLinePercent": 0,
                            "minBranchPercent": 0,
                            "tolerancePercent": 0,
                        },
                    }
                ],
                "obligations": [
                    {
                        "id": "runtime-coverage-report-required",
                        "riskClass": "runtime-boundary-proof",
                        "rationale": "The consumer fixture must execute and return a real Cobertura coverage report.",
                        "requiredProducerIds": [PRODUCER_ID],
                        "requiredAssertionId": ASSERTION_ID,
                    }
                ],
            }
        ],
        "rules": [],
    }


def validate_launcher_contract() -> None:
    """Check our CLI argument vector against the launcher's bounded public parser contract."""
    spec = importlib.util.spec_from_file_location("evidencehost_linux_launcher", LAUNCHER)
    if spec is None or spec.loader is None:
        fail("Could not load the Linux launcher parser for the pure argument contract check.")
    launcher = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = launcher
    try:
        spec.loader.exec_module(launcher)
    except (ImportError, OSError, ValueError):
        fail("Could not load the Linux launcher parser for the pure argument contract check.")

    minimum_reserve = ADMISSION_SECONDS + COLLECTION_SECONDS + CLEANUP_SECONDS
    admission_stages = ADMISSION_SECONDS * 2
    if JOB_SECONDS < minimum_reserve or PRODUCER_SECONDS + admission_stages + minimum_reserve > JOB_SECONDS:
        fail("Configured producer and launcher stage budgets do not fit the frozen job deadline.")

    bindings = {
        "EVIDENCE_BASE_REVISION": "a" * 40,
        "EVIDENCE_SUBJECT_REVISION": "b" * 40,
        "EVIDENCE_WORKFLOW_IDENTITY": "owner/repository/.github/workflows/evidencehost-runtime-proof.yml@refs/pull/1/merge",
        "EVIDENCE_RUN_ID": "123456/1",
    }
    arguments = launcher_args(
        tool_root=Path("/opt/evidence-tool"),
        policy_file=Path("/opt/evidence-tool/evidence.policy.json"),
        output_parent=Path("/var/tmp/evidence-output"),
        bindings=bindings,
        mode="observation",
        slot="observation-123456",
    )
    try:
        parsed = launcher.parser().parse_args(arguments[2:])
        budgets = launcher.validate_budgets(parsed)
    except (SystemExit, AttributeError, TypeError, ValueError, launcher.LauncherError):
        fail("Runtime proof launcher arguments or stage budgets violate the actual launcher contract.")
    if (
        budgets != {
            "admission_seconds": ADMISSION_SECONDS,
            "start_seconds": START_SECONDS,
            "collection_seconds": COLLECTION_SECONDS,
            "cleanup_seconds": CLEANUP_SECONDS,
            "stopping_seconds": STOPPING_SECONDS,
        }
        or parsed.observation_profile != [PROFILE_ID]
        or parsed.observation_producer != [PRODUCER_ID]
        or parsed.solution != SUBJECT_PROJECT_RELATIVE
        or parsed.mode != "observation"
        or parsed.job_seconds != JOB_SECONDS
        or parsed.path != [
            "tests/evidencehost-consumer/RuntimeSubject/Program.cs",
            SUBJECT_PROJECT_RELATIVE,
        ]
        or create_policy()["profiles"][0]["producers"][0]["timeoutSeconds"] != PRODUCER_SECONDS
    ):
        fail("Runtime proof arguments do not select the exact single-producer Observation contract.")


def report_generator_version() -> str:
    source = REPORT_GENERATOR_SOURCE.read_text(encoding="utf-8")
    match = re.search(r"internal const string Version = \"([^\"]+)\";", source)
    if not match:
        fail("Could not resolve the CLI's pinned ReportGenerator package version.")
    version = match.group(1)
    if version != REPORT_GENERATOR_PUBLISH_VERSION:
        fail("The ReportGenerator source locator and proof package pin differ.")
    return version


def prepare_report_generator(tool_root: Path, env: dict[str, str]) -> tuple[str, str]:
    version = report_generator_version()
    package_root = Path(env["NUGET_PACKAGES"]) / "reportgenerator" / version / "tools"
    target = "net10.0"
    if not (package_root / target / "ReportGenerator.dll").is_file():
        fail("The resolved ReportGenerator package is missing the protected CLI's required net10.0 payload.")
    source = package_root / target
    destination = tool_root / "reportgenerator" / target
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, destination)
    payload = destination / "ReportGenerator.dll"
    return target, hashlib.sha256(payload.read_bytes()).hexdigest()


def validate_publish_tree(path: Path) -> None:
    if not path.is_dir() or path.is_symlink():
        fail("The CLI publish root is missing or is a symlink.")
    for current, directories, files in os.walk(path, followlinks=False):
        base = Path(current)
        for name in directories + files:
            entry = base / name
            metadata = entry.lstat()
            if entry.is_symlink() or not (entry.is_dir() or entry.is_file()):
                fail("The CLI publish root contains a link or special file.")
            if entry.is_file() and metadata.st_nlink > 1:
                fail("The CLI publish root contains a multiply-linked entry.")


def build_project(project: Path, *, env: dict[str, str], label: str, timeout: int = 300) -> tuple[bytes, bytes]:
    code, stdout, stderr = bounded_run(
        [str(dotnet_host()), "build", str(project), "--configuration", "Debug", "--verbosity", "minimal",
         "--nologo", "-warnaserror", "-p:RestoreLockedMode=false"],
        cwd=ROOT, env=env, timeout=timeout, label=f"{label} zero-warning build",
    )
    if code != 0:
        detail = render_tail(stdout, stderr)
        fail(f"{label} build exited {code}." + (f"\n{detail}" if detail else ""))
    require_zero_warning_build(stdout + stderr, f"{label} build")
    return stdout, stderr


def run_mechanism_fixtures(env: dict[str, str]) -> dict[str, str]:
    driver_stdout, driver_stderr = run_success(
        ["python3", "-B", "tests/evidencehost-consumer/test_runtime_proof.py"],
        cwd=ROOT, env=env, timeout=30, label="candidate proof rejection regression controls",
    )
    protocol_project = ROOT / "tests/evidencehost-consumer/ControlProtocolWorker/ControlProtocolWorker.csproj"
    lifecycle_project = ROOT / "tests/evidencehost-consumer/LifecycleWorker/LifecycleWorker.csproj"
    protocol_stdout, protocol_stderr = build_project(protocol_project, env=env, label="control protocol fixture")
    lifecycle_stdout, lifecycle_stderr = build_project(lifecycle_project, env=env, label="worker lifecycle fixture")
    protocol_dll = protocol_project.parent / "bin/Debug/net10.0/EvidenceHost.ControlProtocolWorker.dll"
    if not protocol_dll.is_file():
        fail("Control protocol fixture build did not produce its worker assembly.")
    protocol_test_stdout, protocol_test_stderr = root_success(
        ["/usr/bin/python3", "-B", "tests/evidencehost-consumer/test_control_protocol.py",
         "--worker-dll", str(protocol_dll)],
        cwd=ROOT, timeout=240, label="control protocol mechanism fixture",
    )
    lifecycle_test_stdout, lifecycle_test_stderr = run_success(
        ["python3", "-B", "tests/evidencehost-consumer/test_lifecycle_worker.py"],
        cwd=ROOT, env=env, timeout=180, label="worker lifecycle mechanism fixture",
    )
    lifecycle_log_hash = hashlib.sha256(lifecycle_stdout + lifecycle_stderr).hexdigest()
    protocol_log_hash = hashlib.sha256(protocol_stdout + protocol_stderr).hexdigest()
    return {
        "proofDriverTestLogSha256": hashlib.sha256(driver_stdout + driver_stderr).hexdigest(),
        "controlProtocolBuildLogSha256": protocol_log_hash,
        "controlProtocolTestLogSha256": hashlib.sha256(protocol_test_stdout + protocol_test_stderr).hexdigest(),
        "workerLifecycleBuildLogSha256": lifecycle_log_hash,
        "workerLifecycleTestLogSha256": hashlib.sha256(lifecycle_test_stdout + lifecycle_test_stderr).hexdigest(),
        "controlProtocolPassed": "true",
        "workerLifecyclePassed": "true",
    }


def build_before_root(tool_root: Path, env: dict[str, str], proof_directory: Path) -> tuple[str, str]:
    """Check build summaries, then publish the same CLI configuration without rebuilding.

    Raw logs survive command and validation failures in the existing public proof directory.
    The returned build digest hashes the six raw streams in BUILD_LOG_FILES order; older
    records without buildLogFiles hashed only the CLI publish stdout followed by stderr.
    """
    subject_build = [
        str(dotnet_host()), "build", str(SUBJECT_PROJECT), "--configuration", "Debug",
        "--verbosity", "minimal", "--nologo", "-warnaserror", "-p:RestoreLockedMode=false",
    ]
    subject_stdout, subject_stderr = run_success(
        subject_build, cwd=ROOT, env=env, timeout=300, label="RuntimeSubject zero-warning build",
        log_prefix=proof_directory / "subject-build",
    )
    require_zero_warning_build(subject_stdout + subject_stderr, "RuntimeSubject build")

    cli_build = [
        str(dotnet_host()), "build", str(CLI_PROJECT), "--configuration", "Release",
        "--runtime", "linux-x64", "--self-contained", "false",
        "--verbosity", "minimal", "--nologo", "-warnaserror", "-p:RestoreLockedMode=false",
    ]
    cli_stdout, cli_stderr = run_success(
        cli_build, cwd=ROOT, env=env, timeout=480, label="production CLI zero-warning build",
        log_prefix=proof_directory / "cli-build",
    )
    require_zero_warning_build(cli_stdout + cli_stderr, "CLI build")

    publish = [
        str(dotnet_host()), "publish", str(CLI_PROJECT), "--configuration", "Release",
        "--runtime", "linux-x64", "--self-contained", "false",
        "--output", str(tool_root), "--verbosity", "minimal", "--nologo", "-warnaserror",
        "--no-build", "--no-restore", "-p:RestoreLockedMode=false",
    ]
    publish_stdout, publish_stderr = run_success(
        publish, cwd=ROOT, env=env, timeout=480, label="production CLI publish",
        log_prefix=proof_directory / "cli-publish",
    )
    require_no_warning_diagnostics(publish_stdout + publish_stderr, "CLI publish")
    validate_publish_tree(tool_root)
    cli_dll = tool_root / CLI_DLL_NAME
    if not cli_dll.is_file():
        fail("CLI publish did not produce the production command assembly.")
    build_log = subject_stdout + subject_stderr + cli_stdout + cli_stderr + publish_stdout + publish_stderr
    return hashlib.sha256(cli_dll.read_bytes()).hexdigest(), hashlib.sha256(build_log).hexdigest()


def run_cli_rejection(cli_dll: Path, env: dict[str, str], args: list[str], code: str, label: str) -> None:
    command = [str(dotnet_host()), str(cli_dll), "evidence", "run", *args]
    exit_code, stdout, stderr = bounded_run(
        command, cwd=ROOT, env=env, timeout=45, label=label
    )
    output = render_tail(stdout, stderr)
    if exit_code == 0 or code not in output:
        fail(f"{label} did not reject with {code} as required." + (f"\n{output}" if output else ""))


def root_command(argv: list[str], *, cwd: Path, timeout: int, label: str, binary_output: bool = False) -> tuple[int, bytes, bytes]:
    sudo = shutil.which("sudo", path="/usr/sbin:/usr/bin:/sbin:/bin")
    if not sudo:
        fail("Passwordless noninteractive sudo is required on the disposable Linux host.")
    path = f"{dotnet_host().parent}:/usr/sbin:/usr/bin:/sbin:/bin"
    env = {"PATH": path, "HOME": "/nonexistent", "LANG": "C.UTF-8"}
    command = [sudo, "-n", "--", "/usr/bin/env", "-i", f"PATH={path}",
               f"DOTNET_HOST_PATH={dotnet_host()}", "HOME=/nonexistent", "LANG=C.UTF-8", *argv]
    return bounded_run(command, cwd=cwd, env=env, timeout=timeout, label=label, binary_output=binary_output)


def root_success(argv: list[str], *, cwd: Path, timeout: int, label: str) -> tuple[bytes, bytes]:
    code, stdout, stderr = root_command(argv, cwd=cwd, timeout=timeout, label=label)
    if code != 0:
        detail = render_tail(stdout, stderr)
        fail(f"{label} exited {code}." + (f"\n{detail}" if detail else ""))
    return stdout, stderr


def protect_launcher_workspace(work_root: Path) -> None:
    """Make the fresh private parent immutable to the runner and traversable by launcher identities.

    The launcher narrows tool/output child permissions to its selected worker. Cache and
    build-home children retain their private modes. Only the generated parent is changed;
    a root-owned parent also prevents the runner from replacing protected child names.
    """
    if (work_root.parent != Path("/tmp")
            or not work_root.name.startswith("appsurface-evidencehost-runtime-")
            or work_root.is_symlink() or not work_root.is_dir()):
        fail("The launcher workspace must be a fresh generated temporary directory.")
    root_success(
        ["/usr/bin/chown", "--no-dereference", "root:root", str(work_root)],
        cwd=ROOT, timeout=30, label="protect launcher workspace owner",
    )
    root_success(
        ["/usr/bin/chmod", "0755", str(work_root)],
        cwd=ROOT, timeout=30, label="protect launcher workspace traversal",
    )
    info = work_root.lstat()
    if info.st_uid != 0 or stat.S_IMODE(info.st_mode) != 0o755:
        fail("The launcher workspace parent protection did not take effect.")


def launcher_args(
    *,
    tool_root: Path,
    policy_file: Path,
    output_parent: Path,
    bindings: dict[str, str],
    mode: str,
    slot: str,
) -> list[str]:
    return [
        "/usr/bin/python3", str(LAUNCHER),
        "--tool-root", str(tool_root),
        "--subject-root", str(ROOT),
        "--policy-file", str(policy_file),
        "--job-seconds", str(JOB_SECONDS),
        "--admission-seconds", str(ADMISSION_SECONDS),
        "--start-seconds", str(START_SECONDS),
        "--collection-seconds", str(COLLECTION_SECONDS),
        "--cleanup-seconds", str(CLEANUP_SECONDS),
        "--stopping-seconds", str(STOPPING_SECONDS),
        "--mode", mode,
        "--output-parent", str(output_parent),
        "--output-slot", slot,
        "--base-revision", bindings["EVIDENCE_BASE_REVISION"],
        "--subject-revision", bindings["EVIDENCE_SUBJECT_REVISION"],
        "--workflow-identity", bindings["EVIDENCE_WORKFLOW_IDENTITY"],
        "--run-id", bindings["EVIDENCE_RUN_ID"],
        "--solution", SUBJECT_PROJECT_RELATIVE,
        "--path", "tests/evidencehost-consumer/RuntimeSubject/Program.cs",
        "--path", SUBJECT_PROJECT_RELATIVE,
        "--observation-profile", PROFILE_ID,
        "--observation-producer", PRODUCER_ID,
    ]


def assert_trusted_denied(
    *, tool_root: Path, policy_file: Path, output_parent: Path, bindings: dict[str, str], env: dict[str, str]
) -> None:
    slot = "trusted-proof-denied"
    command = launcher_args(
        tool_root=tool_root,
        policy_file=policy_file,
        output_parent=output_parent,
        bindings=bindings,
        mode="trusted",
        slot=slot,
    )
    code, stdout, stderr = root_command(
        command, cwd=ROOT, timeout=90, label="Trusted missing-proof rejection"
    )
    try:
        rejection = json.loads(stderr)
    except (json.JSONDecodeError, UnicodeDecodeError):
        fail("Trusted missing-proof rejection did not return its declared diagnostic.")
    if code != 1 or stdout.strip() or rejection != {"status": "failed", "diagnostic": "ASEVD407"}:
        fail("Trusted missing-proof rejection did not fail specifically with ASEVD407.")
    if list(output_parent.iterdir()):
        fail("Trusted missing-proof rejection created an output anchor.")


def collect_root_output(output_parent: Path, slot: str, env: dict[str, str]) -> dict[str, bytes]:
    expected = {
        "evidence-plan.json": f"run-*/{slot}/evidence-plan.json",
        "evidence-manifest.json": f"run-*/{slot}/evidence-manifest.json",
        "evidence-summary.json": f"run-*/{slot}/evidence-summary.json",
        "coverage-report": f"run-*/{slot}/{PRODUCER_ID}/merged/coverage.cobertura.xml",
    }
    patterns = ["/usr/bin/tar", "--create", "--file=-", "--directory", str(output_parent), "--wildcards", "--no-recursion"]
    patterns.extend(expected.values())
    code, archive, stderr = root_command(
        patterns, cwd=ROOT, timeout=45, label="post-exit protected artifact collection", binary_output=True
    )
    if code != 0:
        detail = render_tail(archive, stderr)
        fail("Launcher returned without a complete readable fresh output slot." +
             (f"\n{detail}" if detail else ""))

    by_suffix: dict[str, bytes] = {}
    allowed = set(expected.values())
    try:
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as bundle:
            for member in bundle.getmembers():
                parts = PurePosixPath(member.name).parts
                if (len(parts) < 3 or not re.fullmatch(r"run-[0-9a-f]{12}", parts[0])
                        or parts[1] != slot or not member.isfile()):
                    fail("Protected output collection contained an unexpected path or entry type.")
                suffix = "/".join(parts[2:])
                if suffix == "evidence-plan.json":
                    key = suffix
                elif suffix == "evidence-manifest.json":
                    key = suffix
                elif suffix == "evidence-summary.json":
                    key = suffix
                elif suffix == f"{PRODUCER_ID}/merged/coverage.cobertura.xml":
                    key = "coverage-report"
                else:
                    fail("Protected output collection contained an undeclared artifact.")
                if key in by_suffix:
                    fail("Protected output collection returned duplicate artifact paths.")
                maximum = MAX_REPORT_BYTES if key == "coverage-report" else 8 * 1024 * 1024
                if member.size < 1 or member.size > maximum:
                    fail(f"Collected artifact {key} is empty or exceeds its bound.")
                stream = bundle.extractfile(member)
                if stream is None:
                    fail(f"Collected artifact {key} could not be read.")
                content = stream.read(maximum + 1)
                if len(content) != member.size or len(content) > maximum:
                    fail(f"Collected artifact {key} changed length during transfer.")
                by_suffix[key] = content
    except (tarfile.TarError, OSError) as error:
        fail(f"Protected output archive was invalid ({type(error).__name__}).")

    if set(by_suffix) != set(expected):
        fail("Launcher output is missing a required plan, manifest, summary, or Cobertura report.")
    return by_suffix


def load_json_artifact(artifacts: dict[str, bytes], name: str) -> dict:
    try:
        value = json.loads(artifacts[name])
    except (KeyError, json.JSONDecodeError, UnicodeDecodeError):
        fail(f"Collected {name} is not valid JSON.")
    if not isinstance(value, dict):
        fail(f"Collected {name} has an unexpected JSON shape.")
    return value


def verify_collected_structure(cli_dll: Path, artifacts: dict[str, bytes], work_root: Path) -> str:
    """Re-resolve the collected plan and recompute manifest bindings with the actual published CLI.

    This is structural verification only. The protected artifact channel, current-run binding,
    and consumer/platform acceptance remain separate proof obligations.
    """
    directory = work_root / "structural-verification"
    directory.mkdir(mode=0o700)
    for name in ("evidence-plan.json", "evidence-manifest.json"):
        (directory / name).write_bytes(artifacts[name])
        os.chmod(directory / name, 0o600)
    stdout, stderr = root_success(
        [str(dotnet_host()), str(cli_dll), "evidence", "verify",
         str(directory / "evidence-manifest.json"), "--plan", str(directory / "evidence-plan.json")],
        cwd=ROOT, timeout=45, label="collected plan/manifest structural verification",
    )
    if b"Evidence manifest structurally verified: ObservationOnly (Informational)." not in stdout:
        fail("The published CLI did not confirm structural verification of the Observation output.")
    return hashlib.sha256(stdout + stderr).hexdigest()


def verify_observation_output(artifacts: dict[str, bytes], policy: dict) -> dict:
    plan = load_json_artifact(artifacts, "evidence-plan.json")
    manifest = load_json_artifact(artifacts, "evidence-manifest.json")
    summary = load_json_artifact(artifacts, "evidence-summary.json")
    if plan.get("policySnapshot") != policy:
        fail("The immutable plan policy snapshot differs from the policy supplied to the launcher.")
    if plan.get("profile", {}).get("id") != PROFILE_ID:
        fail("The protected plan did not select the dependency-free Observation profile.")
    if plan.get("profile", {}).get("resources") != []:
        fail("The Observation profile unexpectedly selected an external resource.")
    if manifest.get("planDigest") != plan.get("planDigest") or not plan.get("planDigest"):
        fail("The manifest does not bind the exact emitted plan digest.")
    if manifest.get("mode", "").casefold() != "observation":
        fail("The production manifest does not record Observation mode.")
    if manifest.get("claimKind", "").casefold() != "observationonly":
        fail("The production manifest does not record an ObservationOnly claim.")
    if manifest.get("eligibility", "").casefold() != "informational":
        fail("The Observation manifest is not explicitly informational and gate-ineligible.")
    if manifest.get("envelopeAssertion") is not None:
        fail("Observation unexpectedly contains a protected Trusted envelope assertion.")
    if manifest.get("metrics", {}).get("cleanupCompleted") is not True:
        fail("The production manifest does not confirm completed cleanup.")
    if summary.get("mode", "").casefold() != "observation" or summary.get("eligibility", "").casefold() != "informational":
        fail("The final summary does not preserve the Observation gate-ineligible status.")

    producer = next(
        (item for item in manifest.get("producerResults", []) if item.get("producerId") == PRODUCER_ID),
        None,
    )
    if producer is None or producer.get("outcome", "").casefold() != "passed":
        fail("The required production coverage producer did not pass.")
    report_metadata = next(
        (item for item in producer.get("artifacts", []) if item.get("logicalName") == "coverage-report"),
        None,
    )
    report = artifacts["coverage-report"]
    if report_metadata is None:
        fail("The manifest omits its required Cobertura artifact metadata.")
    report_hash = hashlib.sha256(report).hexdigest()
    if report_metadata.get("relativePath") != "merged/coverage.cobertura.xml":
        fail("The required coverage report has an unexpected manifest path.")
    if report_metadata.get("lengthBytes") != len(report) or report_metadata.get("sha256") != report_hash:
        fail("The required coverage report bytes do not match the manifest metadata.")
    try:
        report_root = ET.fromstring(report)
    except ET.ParseError:
        fail("The required coverage report is not valid XML.")
    if report_root.tag.rsplit("}", 1)[-1].casefold() != "coverage":
        fail("The required coverage report is not Cobertura XML.")

    return {
        "plan": plan,
        "manifest": manifest,
        "summary": summary,
        "coverageReportSha256": report_hash,
        "coverageReportBytes": len(report),
        "coverageReportRoot": report_root.tag.rsplit("}", 1)[-1],
    }


def write_public_artifacts(
    proof_directory: Path, artifacts: dict[str, bytes], bindings: dict[str, str],
    checked_out_revision: str, source_hashes: dict[str, str], source_digest: str,
    policy_bytes: bytes, cli_hash: str, reporter_hash: str, reporter_target: str,
    build_log_hash: str, verification: dict, observation_slot: str, mechanism_results: dict[str, str],
) -> None:
    file_map = {
        "evidence-plan.json": "evidence-plan.json",
        "evidence-manifest.json": "evidence-manifest.json",
        "evidence-summary.json": "evidence-summary.json",
        "coverage-report": "coverage.cobertura.xml",
    }
    artifact_hashes: dict[str, str] = {}
    for key, filename in file_map.items():
        content = artifacts[key]
        destination = proof_directory / filename
        destination.write_bytes(content)
        os.chmod(destination, 0o644)
        artifact_hashes[filename] = hashlib.sha256(content).hexdigest()

    run_match = RUN_ID_PATTERN.fullmatch(bindings["EVIDENCE_RUN_ID"])
    assert run_match is not None
    record = {
        "schema": "forge-trust-evidencehost-runtime-proof-v1",
        "admission": "none",
        "mode": "Observation",
        "gateEligible": False,
        "runId": bindings["EVIDENCE_RUN_ID"],
        "runNumber": run_match.group(1),
        "runAttempt": run_match.group(2),
        "baseRevision": bindings["EVIDENCE_BASE_REVISION"],
        "subjectRevision": bindings["EVIDENCE_SUBJECT_REVISION"],
        "checkedOutRevision": checked_out_revision,
        "workflowIdentity": bindings["EVIDENCE_WORKFLOW_IDENTITY"],
        "sourceFilesSha256": source_hashes,
        "sourceManifestSha256": source_digest,
        "policySha256": hashlib.sha256(policy_bytes).hexdigest(),
        "planDigest": verification["plan"].get("planDigest"),
        "manifestDigest": verification["manifest"].get("manifestDigest"),
        "policySnapshotMatches": True,
        "coverageReport": {
            "path": "coverage.cobertura.xml",
            "sha256": verification["coverageReportSha256"],
            "lengthBytes": verification["coverageReportBytes"],
            "rootElement": verification["coverageReportRoot"],
        },
        "publishedCliSha256": cli_hash,
        "reportGeneratorSha256": reporter_hash,
        "reportGeneratorTarget": reporter_target,
        "buildLogSha256": build_log_hash,
        "buildLogFiles": list(BUILD_LOG_FILES),
        "mechanismFixtures": mechanism_results,
        "launcherOutputSlot": observation_slot,
        "launcherExitedSuccessfullyBeforeArtifactRead": True,
        "freshOutputFilesSha256": artifact_hashes,
        "negativeNeighbors": {
            "wrongMode": {"rejected": True, "code": "ASEVD401"},
            "missingSupervisor": {"rejected": True, "code": "ASEVD402"},
            "trustedWithoutConsumerProof": {"rejected": True, "outputAnchorCreated": False},
        },
        "limitations": [
            "Observation is informational and gate-ineligible.",
            "This record is not protected workflow acceptance or Trusted consumer proof.",
            "The base revision is recorded but was not independently checked out as a protected tool revision.",
        ],
    }
    record_path = proof_directory / "runtime-proof.json"
    record_path.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.chmod(record_path, 0o644)


def main() -> int:
    global DOTNET_EXECUTABLE
    bindings = read_bindings()
    if len(sys.argv) != 2 or str(Path(sys.argv[1])) != bindings["EVIDENCEHOST_OUTPUT_PARENT"]:
        fail("Pass EVIDENCEHOST_OUTPUT_PARENT as the sole driver argument.")
    if any(character.isspace() for character in str(ROOT)):
        fail("The checkout path contains whitespace, which the current root launcher rejects.")
    validate_launcher_contract()
    DOTNET_EXECUTABLE = resolve_dotnet_host()

    proof_directory = Path(bindings["EVIDENCEHOST_OUTPUT_PARENT"])
    if proof_directory.exists() or proof_directory.is_symlink():
        fail(f"Proof artifact directory already exists: {proof_directory}")
    proof_directory.mkdir(parents=True, mode=0o700)
    os.chmod(proof_directory, 0o755)

    work_root = Path(tempfile.mkdtemp(prefix="appsurface-evidencehost-runtime-", dir="/tmp"))
    os.chmod(work_root, 0o700)
    try:
        env = sanitized_environment(work_root, dotnet_host())
        require_linux_host(env)
        checked_out_revision = git_head(env)
        source_hashes, source_digest = hash_sources()
        mechanism_results = run_mechanism_fixtures(env)

        tool_root = work_root / "tool-root"
        output_parent = work_root / "output-parent"
        trusted_parent = work_root / "trusted-output-parent"
        tool_root.mkdir(mode=0o755)
        output_parent.mkdir(mode=0o755)
        trusted_parent.mkdir(mode=0o755)

        cli_hash, build_log_hash = build_before_root(tool_root, env, proof_directory)
        reporter_target, reporter_hash = prepare_report_generator(tool_root, env)
        policy = create_policy()
        policy_bytes = (json.dumps(policy, indent=2, sort_keys=True) + "\n").encode("utf-8")
        policy_file = tool_root / "evidence.policy.json"
        policy_file.write_bytes(policy_bytes)
        os.chmod(policy_file, 0o644)
        validate_publish_tree(tool_root)

        run_cli_rejection(
            tool_root / CLI_DLL_NAME, env,
            ["--mode", "trusted", "--observation-only"], "ASEVD401", "wrong-mode rejection",
        )
        run_cli_rejection(
            tool_root / CLI_DLL_NAME, env,
            ["--mode", "observation"], "ASEVD402", "missing-supervisor rejection",
        )

        for path in (tool_root, output_parent, trusted_parent):
            code, stdout, stderr = root_command(
                ["/usr/bin/chown", "-R", "--no-dereference", "root:root", str(path)],
                cwd=ROOT, timeout=60, label=f"protect {path.name}",
            )
            if code != 0:
                fail(f"Could not make {path.name} root-owned before launcher invocation." +
                     (f"\n{render_tail(stdout, stderr)}" if stdout or stderr else ""))
        for path in (tool_root, output_parent, trusted_parent):
            code, stdout, stderr = root_command(
                ["/usr/bin/chmod", "0755", str(path)],
                cwd=ROOT, timeout=30, label=f"protect permissions for {path.name}",
            )
            if code != 0:
                fail(f"Could not set protected permissions for {path.name}.")

        protect_launcher_workspace(work_root)
        assert_trusted_denied(
            tool_root=tool_root, policy_file=policy_file, output_parent=trusted_parent,
            bindings=bindings, env=env,
        )
        if list(trusted_parent.iterdir()):
            fail("Trusted missing-proof rejection left an output entry behind.")

        observation_slot = "observation-" + RUN_ID_PATTERN.fullmatch(bindings["EVIDENCE_RUN_ID"]).group(1)
        observation = launcher_args(
            tool_root=tool_root,
            policy_file=policy_file,
            output_parent=output_parent,
            bindings=bindings,
            mode="observation",
            slot=observation_slot,
        )
        stdout, stderr = root_success(
            observation, cwd=ROOT, timeout=LAUNCHER_TIMEOUT_SECONDS, label="production Observation launcher"
        )
        try:
            launcher_result = json.loads(stdout)
        except json.JSONDecodeError:
            fail("The production launcher returned a non-JSON completion result.")
        if launcher_result.get("status") != "completed" or launcher_result.get("output_slot") != observation_slot:
            fail("The production launcher did not report the expected completed output slot.")

        artifacts = collect_root_output(output_parent, observation_slot, env)
        verification = verify_observation_output(artifacts, policy)
        mechanism_results["structuralVerificationLogSha256"] = verify_collected_structure(
            tool_root / CLI_DLL_NAME, artifacts, work_root)
        after_hashes, after_digest = hash_sources()
        if after_hashes != source_hashes or after_digest != source_digest:
            fail("Proof source files changed during the runtime observation.")

        write_public_artifacts(
            proof_directory, artifacts, bindings, checked_out_revision, source_hashes,
            source_digest, policy_bytes, cli_hash, reporter_hash, reporter_target,
            build_log_hash, verification, observation_slot, mechanism_results,
        )
        print(f"EvidenceHost Observation proof passed: {proof_directory / 'runtime-proof.json'}")
        return 0
    finally:
        # Root-owned launcher files cannot be removed by this non-root driver. The disposable
        # runner retains this fresh workspace for diagnosis; it is never reused or uploaded.
        print(f"Preserved private runtime proof workspace on disposable runner: {work_root}", file=sys.stderr)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except ProofFailure as error:
        print(f"EvidenceHost runtime proof failed: {error}", file=sys.stderr)
        sys.exit(1)
