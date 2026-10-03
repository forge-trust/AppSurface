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
import time
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath


ROOT = Path(__file__).resolve().parents[2]
CLI_PROJECT = ROOT / "Cli" / "ForgeTrust.AppSurface.Cli" / "ForgeTrust.AppSurface.Cli.csproj"
CLI_DLL_NAME = "ForgeTrust.AppSurface.Cli.dll"
SUBJECT_PROJECT_RELATIVE = "tests/evidencehost-consumer/RuntimeSubject/RuntimeSubject.csproj"
SUBJECT_PROJECT = ROOT / SUBJECT_PROJECT_RELATIVE
STAGED_SUBJECT_FILES = (
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    SUBJECT_PROJECT_RELATIVE, "tests/evidencehost-consumer/RuntimeSubject/Program.cs",
    "tests/evidencehost-consumer/RuntimeSubject/packages.lock.json",
)
RELOCATED_PROOF_SOURCES = (
    ("Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageProducer.cs",
     "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs"),
    ("Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageTransport.cs",
     "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageTransport.cs"),
)
MAX_STAGED_SUBJECT_BYTES = 20 * 1024 * 1024
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
RUNTIME_WORKSPACE_PARENT = Path("/run")
RUNTIME_WORKSPACE_PREFIX = "appsurface-evidencehost-runtime-"
PRIVATE_WORKER_JOURNAL_NAME = "launcher-worker-journal.log"
PRIVATE_WORKER_JOURNAL_ARCHIVE = "worker-journal.tar"
MAX_PRIVATE_WORKER_JOURNAL_BYTES = 4096
MAX_PRIVATE_WORKER_JOURNAL_ARCHIVE_BYTES = 10240
PRIVATE_FAILURE_OUTPUT_NAMES = ("evidence-plan.json", "evidence-manifest.json", "evidence-summary.json")
PRIVATE_FAILURE_OUTPUT_ARCHIVE = "failure-output.tar"
MAX_PRIVATE_FAILURE_FILE_BYTES = 128 * 1024
MAX_PRIVATE_FAILURE_OUTPUT_BYTES = 384 * 1024
MAX_PRIVATE_FAILURE_ARCHIVE_BYTES = 400 * 1024
PRIVATE_FAILURE_OUTPUT_ROOT_SCRIPT = '''import io,os,re,stat,sys,tarfile
NAMES=("evidence-plan.json","evidence-manifest.json","evidence-summary.json")
def identity(info):
    return (info.st_dev,info.st_ino,info.st_uid,info.st_gid,info.st_mode,info.st_nlink,info.st_size,info.st_mtime_ns,info.st_ctime_ns)
def archive_from_output(output_fd,slot,*,expected_root_uid=0,expected_root_gid=0):
    # Overrides exercise actual portable FDs only; production always requires root.
    if not isinstance(slot,str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}",slot):
        raise ValueError()
    outer=os.fstat(output_fd)
    if not stat.S_ISDIR(outer.st_mode) or outer.st_uid!=expected_root_uid or outer.st_gid!=expected_root_gid or stat.S_IMODE(outer.st_mode)!=0o755:
        raise ValueError()
    entries=os.listdir(output_fd)
    # The held launcher creates one root-selected 12-hex anchor, never a caller path.
    if len(entries)!=1 or not re.fullmatch(r"run-[0-9a-f]{12}",entries[0]):
        raise ValueError()
    run_fd=slot_fd=-1
    pinned=[]
    try:
        run_fd=os.open(entries[0],os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=output_fd)
        run=os.fstat(run_fd)
        if not stat.S_ISDIR(run.st_mode) or run.st_uid<=0 or run.st_gid<=0 or stat.S_IMODE(run.st_mode)!=0o700 or identity(run)!=identity(os.stat(entries[0],dir_fd=output_fd,follow_symlinks=False)):
            raise ValueError()
        slot_fd=os.open(slot,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=run_fd)
        selected=os.fstat(slot_fd)
        if not stat.S_ISDIR(selected.st_mode) or (selected.st_uid,selected.st_gid)!=(run.st_uid,run.st_gid) or stat.S_IMODE(selected.st_mode)!=0o700 or identity(selected)!=identity(os.stat(slot,dir_fd=run_fd,follow_symlinks=False)):
            raise ValueError()
        contents={}
        for name in NAMES:
            try:
                fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK,dir_fd=slot_fd)
            except FileNotFoundError:
                continue
            pinned.append((name,fd,None))
            try:
                before=os.fstat(fd)
                pinned[-1]=(name,fd,before)
                if not stat.S_ISREG(before.st_mode) or (before.st_uid,before.st_gid)!=(run.st_uid,run.st_gid) or stat.S_IMODE(before.st_mode)!=0o600 or before.st_nlink!=1 or not 0<=before.st_size<=131072 or identity(before)!=identity(os.stat(name,dir_fd=slot_fd,follow_symlinks=False)):
                    raise ValueError()
                data=bytearray()
                while len(data)<before.st_size:
                    part=os.read(fd,min(65536,before.st_size-len(data)))
                    if not part: raise ValueError()
                    data.extend(part)
                if identity(before)!=identity(os.fstat(fd)) or identity(before)!=identity(os.stat(name,dir_fd=slot_fd,follow_symlinks=False)):
                    raise ValueError()
                contents[name]=bytes(data)
            except BaseException:
                raise
        if not contents or sum(map(len,contents.values()))>393216:
            raise ValueError()
        for fd,before,parent,name in ((slot_fd,selected,run_fd,slot),(run_fd,run,output_fd,entries[0])):
            if identity(before)!=identity(os.fstat(fd)) or identity(before)!=identity(os.stat(name,dir_fd=parent,follow_symlinks=False)):
                raise ValueError()
        if identity(outer)!=identity(os.fstat(output_fd)): raise ValueError()
        output=io.BytesIO()
        with tarfile.open(fileobj=output,mode="w",format=tarfile.USTAR_FORMAT) as archive:
            for name in NAMES:
                if name not in contents: continue
                item=tarfile.TarInfo(name);item.mode=0o600;item.uid=item.gid=0;item.size=len(contents[name])
                archive.addfile(item,io.BytesIO(contents[name]))
        result=output.getvalue()
        if len(result)>409600: raise ValueError()
        for name,fd,before in pinned:
            if identity(before)!=identity(os.fstat(fd)) or identity(before)!=identity(os.stat(name,dir_fd=slot_fd,follow_symlinks=False)):
                raise ValueError()
        return result
    finally:
        for name,fd,before in pinned: os.close(fd)
        for fd in (slot_fd,run_fd):
            if fd>=0: os.close(fd)
def main():
    if os.geteuid()!=0 or len(sys.argv)!=7 or not re.fullmatch(r"[0-9a-f]{32}",sys.argv[1]) or any(not re.fullmatch(r"[0-9]{1,20}",v) for v in sys.argv[2:6]):
        raise ValueError()
    descriptors=[]
    try:
        parent=os.open("/run",os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC);descriptors.append(parent)
        info=os.fstat(parent)
        if info.st_uid!=0 or info.st_gid!=0 or info.st_mode&0o022: raise ValueError()
        work_name="appsurface-evidencehost-runtime-"+sys.argv[1]
        work=os.open(work_name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent);descriptors.append(work)
        info=os.fstat(work)
        if (info.st_dev,info.st_ino)!=(int(sys.argv[2]),int(sys.argv[3])) or info.st_uid!=0 or info.st_gid!=0 or stat.S_IMODE(info.st_mode)!=0o755: raise ValueError()
        output=os.open("output-parent",os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=work);descriptors.append(output)
        outer=os.fstat(output)
        if (outer.st_dev,outer.st_ino)!=(int(sys.argv[4]),int(sys.argv[5])): raise ValueError()
        result=archive_from_output(output,sys.argv[6])
        if identity(outer)!=identity(os.stat("output-parent",dir_fd=work,follow_symlinks=False)) or identity(info)!=identity(os.fstat(work)) or identity(info)!=identity(os.stat(work_name,dir_fd=parent,follow_symlinks=False)):
            raise ValueError()
        sys.stdout.buffer.write(result)
    finally:
        for fd in reversed(descriptors): os.close(fd)
if __name__=="__main__":
    try: main()
    except Exception: sys.exit(1)
'''
PRIVATE_WORKER_JOURNAL_ROOT_SCRIPT = '''import io,os,re,stat,sys,tarfile
def identity(info):
    return (info.st_dev,info.st_ino,info.st_uid,info.st_gid,info.st_mode,info.st_nlink,info.st_size,info.st_mtime_ns,info.st_ctime_ns)
def archive_from_directory(directory_fd,*,expected_owner_uid=0,expected_owner_gid=0):
    # Owner overrides are portable test seams; the production entry always uses root.
    parent=os.fstat(directory_fd)
    if not stat.S_ISDIR(parent.st_mode) or parent.st_uid!=expected_owner_uid or parent.st_gid!=expected_owner_gid or stat.S_IMODE(parent.st_mode)!=0o755:
        raise ValueError()
    name="launcher-worker-journal.log"
    fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW|os.O_CLOEXEC|os.O_NONBLOCK,dir_fd=directory_fd)
    try:
        before=os.fstat(fd)
        if not stat.S_ISREG(before.st_mode) or before.st_uid!=expected_owner_uid or before.st_gid!=expected_owner_gid or stat.S_IMODE(before.st_mode)!=0o600 or before.st_nlink!=1 or not 0<=before.st_size<=4096:
            raise ValueError()
        if identity(before)!=identity(os.stat(name,dir_fd=directory_fd,follow_symlinks=False)):
            raise ValueError()
        data=bytearray()
        while len(data)<before.st_size:
            part=os.read(fd,before.st_size-len(data))
            if not part:
                raise ValueError()
            data.extend(part)
        if identity(before)!=identity(os.fstat(fd)) or identity(before)!=identity(os.stat(name,dir_fd=directory_fd,follow_symlinks=False)):
            raise ValueError()
        current=os.fstat(directory_fd)
        if (parent.st_dev,parent.st_ino,parent.st_uid,parent.st_gid,parent.st_mode)!=(current.st_dev,current.st_ino,current.st_uid,current.st_gid,current.st_mode):
            raise ValueError()
        output=io.BytesIO()
        with tarfile.open(fileobj=output,mode="w",format=tarfile.USTAR_FORMAT) as archive:
            member=tarfile.TarInfo(name)
            member.mode=0o600
            member.uid=member.gid=0
            member.size=len(data)
            archive.addfile(member,io.BytesIO(data))
        result=output.getvalue()
        if len(result)>10240:
            raise ValueError()
        return result
    finally:
        os.close(fd)
def main():
    if os.geteuid()!=0 or len(sys.argv)!=4 or not re.fullmatch(r"[0-9a-f]{32}",sys.argv[1]) or any(not re.fullmatch(r"[0-9]{1,20}",value) for value in sys.argv[2:]):
        raise ValueError()
    parent=os.open("/run",os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
    child=-1
    try:
        info=os.fstat(parent)
        if info.st_uid!=0 or info.st_gid!=0 or info.st_mode&0o022:
            raise ValueError()
        name="appsurface-evidencehost-runtime-"+sys.argv[1]
        child=os.open(name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent)
        info=os.fstat(child)
        if (info.st_dev,info.st_ino)!=(int(sys.argv[2]),int(sys.argv[3])):
            raise ValueError()
        result=archive_from_directory(child)
        named=os.stat(name,dir_fd=parent,follow_symlinks=False)
        if (info.st_dev,info.st_ino,info.st_uid,info.st_gid,info.st_mode)!=(named.st_dev,named.st_ino,named.st_uid,named.st_gid,named.st_mode):
            raise ValueError()
        sys.stdout.buffer.write(result)
    finally:
        if child>=0:
            os.close(child)
        os.close(parent)
if __name__=="__main__":
    try:
        main()
    except Exception:
        sys.exit(1)
'''
RUNTIME_WORKSPACE_ROOT_SCRIPT = '''import os,re,stat,sys
mode,token,uid,gid=sys.argv[1:5]
uid,gid=int(uid),int(gid)
if os.geteuid()!=0 or mode not in ("create","freeze") or not re.fullmatch(r"[0-9a-f]{32}",token) or not 0<uid<4294967295 or not 0<gid<4294967295:
    sys.exit(1)
parent=os.open("/run",os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC)
child=-1
try:
    info=os.fstat(parent)
    if info.st_uid!=0 or info.st_mode&0o022:
        sys.exit(1)
    name="appsurface-evidencehost-runtime-"+token
    if mode=="create":
        os.mkdir(name,0o700,dir_fd=parent)
    child=os.open(name,os.O_RDONLY|os.O_DIRECTORY|os.O_NOFOLLOW|os.O_CLOEXEC,dir_fd=parent)
    info=os.fstat(child)
    if stat.S_IMODE(info.st_mode)!=0o700:
        sys.exit(1)
    if mode=="create":
        if info.st_uid!=0 or len(sys.argv)!=5:
            sys.exit(1)
        os.fchown(child,uid,gid)
        os.fchmod(child,0o700)
    else:
        if len(sys.argv)!=7 or info.st_uid!=uid or info.st_gid!=gid or (info.st_dev,info.st_ino)!=(int(sys.argv[5]),int(sys.argv[6])):
            sys.exit(1)
        os.fchown(child,0,0)
        os.fchmod(child,0o755)
finally:
    if child>=0:
        os.close(child)
    os.close(parent)
'''


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
    """Require the shared execution sources and their pinned project dependency inputs."""
    relative_paths = [
        "scripts/verify-evidencehost-linux-runtime.sh",
        "scripts/evidencehost-linux-launcher.py",
        "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
        "Cli/ForgeTrust.AppSurface.Cli/packages.lock.json",
        "Cli/ForgeTrust.AppSurface.Cli/EvidenceWorkerCommand.cs",
        "Cli/ForgeTrust.AppSurface.Cli/CoverageRun.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceProtectedCliExecution.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceCliWorkflow.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/ForgeTrust.AppSurface.Evidence.Cli.csproj",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/packages.lock.json",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/CoverageExecutionBoundary.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducerFactory.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/ForgeTrust.AppSurface.Evidence.Coverage.csproj",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/packages.lock.json",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceProtectedWorkerInputs.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationCatalogue.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidencePlanner.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/ForgeTrust.AppSurface.Evidence.Planner.csproj",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/packages.lock.json",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceAdmission.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxWorkerSupervisor.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxApplicationProtocol.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxArtifactRoot.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceContracts.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceModeSelection.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceRestrictedProducerLease.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceWorkerExecution.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/ForgeTrust.AppSurface.Evidence.Contracts.csproj",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/packages.lock.json",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceHostBootstrap.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceRestrictedAspireApplication.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/ForgeTrust.AppSurface.Evidence.Aspire.csproj",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/packages.lock.json",
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
    relocated = [current for _, current in RELOCATED_PROOF_SOURCES]
    return [ROOT / item for item in dict.fromkeys([*relative_paths, *relocated, *STAGED_SUBJECT_FILES])]


def hash_sources() -> tuple[dict[str, str], str]:
    """Bind every required file's exact bytes, rejecting stale copies at both old locations.

    The caller repeats this inventory after execution and requires an identical map and
    canonical JSON digest. These source bindings alone grant no execution authority.
    """
    for previous, _ in RELOCATED_PROOF_SOURCES:
        path = ROOT / previous
        if path.exists() or path.is_symlink():
            fail(f"Relocated proof source still exists at its previous location: {previous}")
    hashes: dict[str, str] = {}
    for path in source_paths():
        if not path.is_file() or path.is_symlink():
            fail(f"Required proof source is missing or unsafe: {path.relative_to(ROOT)}")
        relative = path.relative_to(ROOT).as_posix()
        hashes[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
    encoded = json.dumps(hashes, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return hashes, hashlib.sha256(encoded).hexdigest()


def stage_runtime_subject(work_root: Path, source_hashes: dict[str, str], *, source_root: Path = ROOT) -> tuple[Path, dict[str, str]]:
    """Stage exactly the dependency-free fixture and its pinned regular build inputs.

    This is an explicit input projection, not an exclusion filter over a checkout. New ancestor
    build inputs or explicit imports require a reviewed update to the six-file declaration.
    Candidate Git/source provenance remains bound to the original checkout.
    """
    if not source_root.is_absolute() or source_root.is_symlink() or not source_root.is_dir():
        fail("RuntimeSubject staging requires a regular candidate source root.")
    build_names = ("Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                   "global.json", "NuGet.Config", "nuget.config")
    for relative_parent in ("", "tests", "tests/evidencehost-consumer", "tests/evidencehost-consumer/RuntimeSubject"):
        for name in build_names:
            relative = str(PurePosixPath(relative_parent) / name)
            path = source_root / relative
            if relative not in STAGED_SUBJECT_FILES and (path.exists() or path.is_symlink()):
                fail("RuntimeSubject has an undeclared ancestor build input.")
    contents = {}
    total = 0
    for relative in STAGED_SUBJECT_FILES:
        path = source_root / relative
        if any((source_root / Path(*Path(relative).parts[:index])).is_symlink()
               for index in range(1, len(Path(relative).parts))):
            fail("RuntimeSubject source inputs cannot traverse links.")
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as source:
                before = os.fstat(source.fileno())
                if not stat.S_ISREG(before.st_mode) or before.st_size > MAX_STAGED_SUBJECT_BYTES - total:
                    fail("RuntimeSubject source input shape or byte bound is invalid.")
                data = source.read(MAX_STAGED_SUBJECT_BYTES - total + 1)
                after = os.fstat(source.fileno())
        except OSError:
            fail("A required RuntimeSubject source input is missing or unsafe.")
        if (len(data) != before.st_size or len(data) > MAX_STAGED_SUBJECT_BYTES - total
                or (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
                != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
                or hashlib.sha256(data).hexdigest() != source_hashes.get(relative)):
            fail("RuntimeSubject source bytes differ from the pinned candidate inputs.")
        if relative.endswith((".props", ".targets", ".csproj")):
            try:
                document = ET.fromstring(data)
            except ET.ParseError:
                fail("RuntimeSubject build input is not valid XML.")
            if any(element.tag.rsplit("}", 1)[-1] in ("Import", "ProjectReference") for element in document.iter()):
                fail("RuntimeSubject has an undeclared build import or project reference.")
        contents[relative] = data
        total += len(data)
    directory = work_root / "subject-source"
    try:
        directory.mkdir(mode=0o755)
        for relative, data in contents.items():
            path = directory / relative
            path.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
            with path.open("xb") as target:
                target.write(data)
            path.chmod(0o644)
    except OSError:
        fail("RuntimeSubject staging requires a fresh private destination.")
    hashes = {relative: hashlib.sha256(data).hexdigest() for relative, data in contents.items()}
    return directory, hashes


def verify_staged_subject(directory: Path, expected: dict[str, str]) -> None:
    """Require the prepared six-file input tree to remain unchanged through execution."""
    if directory.is_symlink() or not directory.is_dir():
        fail("The staged RuntimeSubject root changed.")
    paths = list(directory.rglob("*"))
    if any(path.is_symlink() for path in paths):
        fail("The staged RuntimeSubject input set contains a link.")
    files = {path.relative_to(directory).as_posix() for path in paths if not path.is_dir()}
    if files != set(STAGED_SUBJECT_FILES) or set(expected) != set(STAGED_SUBJECT_FILES):
        fail("The staged RuntimeSubject input set changed.")
    total = 0
    for relative, digest in expected.items():
        path = directory / relative
        try:
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
            with os.fdopen(fd, "rb") as source:
                info = os.fstat(source.fileno())
                if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_STAGED_SUBJECT_BYTES - total:
                    fail("The staged RuntimeSubject shape or byte bound changed.")
                data = source.read(MAX_STAGED_SUBJECT_BYTES - total + 1)
        except OSError:
            fail("The staged RuntimeSubject input is missing or unsafe.")
        if len(data) != info.st_size or len(data) > MAX_STAGED_SUBJECT_BYTES - total or hashlib.sha256(data).hexdigest() != digest:
            fail("The staged RuntimeSubject bytes changed.")
        total += len(data)


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


def load_launcher_contract():
    """Load only the host-owned parser and pure diagnostic validation helpers."""
    spec = importlib.util.spec_from_file_location("evidencehost_linux_launcher", LAUNCHER)
    if spec is None or spec.loader is None:
        fail("Could not load the Linux launcher parser for the pure argument contract check.")
    launcher = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = launcher
    try:
        spec.loader.exec_module(launcher)
    except (ImportError, OSError, ValueError):
        fail("Could not load the Linux launcher parser for the pure argument contract check.")
    return launcher


def validate_launcher_contract() -> None:
    """Check our CLI argument vector against the launcher's bounded public parser contract."""
    launcher = load_launcher_contract()

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


def runtime_workspace_path(work_root: Path) -> bool:
    """Accept only a closed UUID child of the fixed runtime workspace parent."""
    return (work_root.parent == RUNTIME_WORKSPACE_PARENT
            and re.fullmatch(re.escape(RUNTIME_WORKSPACE_PREFIX) + r"[0-9a-f]{32}", work_root.name) is not None)


def create_launcher_workspace() -> Path:
    """Root-create one exclusive /run UUID child and hand its private preparation to this driver.

    No caller-selected path is accepted. The fixed root helper pins /run without following a
    link, rejects an existing child, and changes only its new directory to the nonroot driver.
    A failed/partial preparation is retained on the disposable runner, never adopted or deleted.
    """
    uid, gid = os.geteuid(), os.getegid()
    if type(uid) is not int or type(gid) is not int or not 0 < uid < 4294967295 or not 0 < gid < 4294967295:
        fail("Runtime workspace preparation requires a nonroot driver identity.")
    token = uuid.uuid4().hex
    work_root = RUNTIME_WORKSPACE_PARENT / (RUNTIME_WORKSPACE_PREFIX + token)
    if not runtime_workspace_path(work_root):
        fail("Runtime workspace UUID is invalid.")
    code, stdout, _stderr = root_command(
        ["/usr/bin/python3", "-I", "-c", RUNTIME_WORKSPACE_ROOT_SCRIPT, "create", token, str(uid), str(gid)],
        cwd=ROOT, timeout=15, label="create private runtime workspace",
    )
    if code != 0 or stdout:
        fail("Root runtime workspace creation failed.")
    try:
        info = work_root.lstat()
    except OSError:
        fail("Root runtime workspace creation did not take effect.")
    if (not stat.S_ISDIR(info.st_mode) or info.st_uid != uid or info.st_gid != gid
            or stat.S_IMODE(info.st_mode) != 0o700):
        fail("Root runtime workspace creation did not take effect.")
    return work_root


def protect_launcher_workspace(work_root: Path) -> None:
    """Make the fresh private parent immutable to the runner and traversable by launcher identities.

    The launcher narrows tool/output child permissions to its selected worker. Cache and
    build-home children retain their private modes. Only the generated parent is changed;
    a root-owned parent also prevents the runner from replacing protected child names.
    """
    try:
        before = work_root.lstat()
    except OSError:
        fail("The launcher workspace must be a fresh generated temporary directory.")
    if (not runtime_workspace_path(work_root) or not stat.S_ISDIR(before.st_mode)
            or before.st_uid != os.geteuid() or before.st_gid != os.getegid()
            or stat.S_IMODE(before.st_mode) != 0o700):
        fail("The launcher workspace must be a fresh generated temporary directory.")
    code, stdout, _stderr = root_command(
        ["/usr/bin/python3", "-I", "-c", RUNTIME_WORKSPACE_ROOT_SCRIPT, "freeze",
         work_root.name[len(RUNTIME_WORKSPACE_PREFIX):], str(os.geteuid()), str(os.getegid()),
         str(before.st_dev), str(before.st_ino)],
        cwd=ROOT, timeout=15, label="freeze runtime workspace parent",
    )
    if code != 0 or stdout:
        fail("The launcher workspace parent protection failed.")
    info = work_root.lstat()
    if (not stat.S_ISDIR(info.st_mode) or (info.st_dev, info.st_ino) != (before.st_dev, before.st_ino)
            or info.st_uid != 0 or info.st_gid != 0 or stat.S_IMODE(info.st_mode) != 0o755):
        fail("The launcher workspace parent protection did not take effect.")


def _valid_private_journal_archive(data: bytes) -> bool:
    """Accept one canonical USTAR member; content remains private hostile bytes."""
    if not isinstance(data, bytes) or len(data) > MAX_PRIVATE_WORKER_JOURNAL_ARCHIVE_BYTES:
        return False
    try:
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            members = archive.getmembers()
            if len(members) != 1:
                return False
            member = members[0]
            if (member.name != PRIVATE_WORKER_JOURNAL_NAME or member.type != tarfile.REGTYPE
                    or member.mode != 0o600 or member.uid != 0 or member.gid != 0
                    or not 0 <= member.size <= MAX_PRIVATE_WORKER_JOURNAL_BYTES
                    or member.linkname or member.pax_headers or member.uname or member.gname or member.mtime != 0):
                return False
            source = archive.extractfile(member)
            if source is None:
                return False
            with source:
                content = source.read(MAX_PRIVATE_WORKER_JOURNAL_BYTES + 1)
            if len(content) != member.size:
                return False
        canonical = io.BytesIO()
        with tarfile.open(fileobj=canonical, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            expected = tarfile.TarInfo(PRIVATE_WORKER_JOURNAL_NAME)
            expected.mode, expected.uid, expected.gid, expected.size = 0o600, 0, 0, len(content)
            archive.addfile(expected, io.BytesIO(content))
        return canonical.getvalue() == data
    except (OSError, ValueError, tarfile.TarError):
        return False


def retain_private_worker_journal(work_root: Path, proof_directory: Path) -> bool:
    """Optionally retain one root-validated journal archive without rendering any bytes.

    Root receives only the UUID and pinned device/inode of this fresh /run workspace.
    This private diagnostic has no success, provenance or admission authority.
    Every missing/invalid/copy failure returns false without changing the launch failure.
    """
    parent_fd = private_fd = archive_fd = -1
    try:
        if not runtime_workspace_path(work_root):
            return False
        info = work_root.lstat()
        if (not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_gid != 0
                or stat.S_IMODE(info.st_mode) != 0o755):
            return False
        code, data, _stderr = root_command(
            ["/usr/bin/python3", "-I", "-c", PRIVATE_WORKER_JOURNAL_ROOT_SCRIPT,
             work_root.name[len(RUNTIME_WORKSPACE_PREFIX):], str(info.st_dev), str(info.st_ino)],
            cwd=ROOT, timeout=10, label="retain private worker journal", binary_output=True)
        if code != 0 or not _valid_private_journal_archive(data):
            return False
        parent_fd = os.open(proof_directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        parent = os.fstat(parent_fd)
        if parent.st_uid != os.geteuid() or parent.st_gid != os.getegid() or parent.st_mode & 0o022:
            return False
        os.mkdir("private-diagnostics", 0o700, dir_fd=parent_fd)
        private_fd = os.open("private-diagnostics", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                             dir_fd=parent_fd)
        private = os.fstat(private_fd)
        if (private.st_uid != os.geteuid() or private.st_gid != os.getegid()
                or stat.S_IMODE(private.st_mode) != 0o700):
            return False
        archive_fd = os.open(PRIVATE_WORKER_JOURNAL_ARCHIVE,
                             os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                             0o600, dir_fd=private_fd)
        os.fchmod(archive_fd, 0o600)
        with os.fdopen(archive_fd, "wb", closefd=False) as output:
            output.write(data)
            output.flush()
        final = os.fstat(archive_fd)
        named = os.stat(PRIVATE_WORKER_JOURNAL_ARCHIVE, dir_fd=private_fd, follow_symlinks=False)
        named_private = os.stat("private-diagnostics", dir_fd=parent_fd, follow_symlinks=False)
        named_parent = proof_directory.lstat()
        return (stat.S_ISREG(final.st_mode) and final.st_uid == os.geteuid() and final.st_gid == os.getegid()
                and stat.S_IMODE(final.st_mode) == 0o600 and final.st_nlink == 1 and final.st_size == len(data)
                and (final.st_dev, final.st_ino) == (named.st_dev, named.st_ino)
                and (private.st_dev, private.st_ino) == (named_private.st_dev, named_private.st_ino)
                and (parent.st_dev, parent.st_ino) == (named_parent.st_dev, named_parent.st_ino))
    except (ProofFailure, OSError, ValueError, TypeError, subprocess.SubprocessError):
        return False
    finally:
        for fd in (archive_fd, private_fd, parent_fd):
            if fd >= 0:
                try:
                    os.close(fd)
                except OSError:
                    pass


def _private_failure_output_contents(data: bytes) -> dict[str, bytes] | None:
    """Validate canonical private USTAR bytes; no artifact content is interpreted as authority."""
    if not isinstance(data, bytes) or len(data) > MAX_PRIVATE_FAILURE_ARCHIVE_BYTES:
        return None
    try:
        contents = {}
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            members = archive.getmembers()
            if not 1 <= len(members) <= len(PRIVATE_FAILURE_OUTPUT_NAMES):
                return None
            for member in members:
                if (member.name not in PRIVATE_FAILURE_OUTPUT_NAMES or member.name in contents
                        or member.type != tarfile.REGTYPE or member.mode != 0o600 or member.uid or member.gid
                        or not 0 <= member.size <= MAX_PRIVATE_FAILURE_FILE_BYTES or member.linkname
                        or member.pax_headers or member.uname or member.gname or member.mtime):
                    return None
                stream = archive.extractfile(member)
                if stream is None:
                    return None
                with stream:
                    content = stream.read(MAX_PRIVATE_FAILURE_FILE_BYTES + 1)
                if len(content) != member.size:
                    return None
                contents[member.name] = content
        if sum(map(len, contents.values())) > MAX_PRIVATE_FAILURE_OUTPUT_BYTES:
            return None
        canonical = io.BytesIO()
        with tarfile.open(fileobj=canonical, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name in PRIVATE_FAILURE_OUTPUT_NAMES:
                if name in contents:
                    member = tarfile.TarInfo(name)
                    member.mode, member.uid, member.gid, member.size = 0o600, 0, 0, len(contents[name])
                    archive.addfile(member, io.BytesIO(contents[name]))
        return contents if canonical.getvalue() == data else None
    except (OSError, ValueError, tarfile.TarError):
        return None


def retain_private_failure_output(work_root: Path, output_parent: Path, slot: str,
                                  proof_directory: Path, record: dict) -> bool:
    """Retain fixed failed-worker files only after validated completed protocol/owned exit.

    Paths come from this invocation, never uploaded JSON. Root pins the fixed workspace
    and output parent; portable owner overrides exist only inside the FD procedure.
    Missing, unsafe, changed, oversized or occupied capture returns false. Neither raw
    bytes nor capture success can replace the original failed launcher verdict.
    """
    fds = []
    try:
        if (record.get("cause") != "worker-unsuccessful" or record.get("operation") != "worker-exit"
                or record.get("worker_main_code") != 1 or type(record.get("worker_main_status")) is not int
                or not 1 <= record["worker_main_status"] <= 255
                or any(record.get(name) is not True for name in
                       ("broker_ready_seen", "broker_wait_completed", "broker_exited", "broker_work_closed"))
                or any(type(record.get(name)) is not int or record[name] != 0 for name in
                       ("broker_active_handlers", "broker_active_runs"))
                or not runtime_workspace_path(work_root) or output_parent != work_root / "output-parent"
                or not isinstance(slot, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}", slot)):
            return False
        work, outer = work_root.lstat(), output_parent.lstat()
        if any(not stat.S_ISDIR(info.st_mode) or info.st_uid != 0 or info.st_gid != 0
               or stat.S_IMODE(info.st_mode) != 0o755 for info in (work, outer)):
            return False
        code, data, _stderr = root_command(
            ["/usr/bin/python3", "-I", "-c", PRIVATE_FAILURE_OUTPUT_ROOT_SCRIPT,
             work_root.name[len(RUNTIME_WORKSPACE_PREFIX):], str(work.st_dev), str(work.st_ino),
             str(outer.st_dev), str(outer.st_ino), slot],
            cwd=ROOT, timeout=10, label="retain private failed worker output", binary_output=True)
        contents = _private_failure_output_contents(data) if code == 0 else None
        if contents is None:
            return False
        parent_fd = os.open(proof_directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        fds.append(parent_fd)
        parent = os.fstat(parent_fd)
        if parent.st_uid != os.geteuid() or parent.st_gid != os.getegid() or parent.st_mode & 0o022:
            return False
        try:
            os.mkdir("private-diagnostics", 0o700, dir_fd=parent_fd)
        except FileExistsError:
            pass
        private_fd = os.open("private-diagnostics", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent_fd)
        fds.append(private_fd)
        private = os.fstat(private_fd)
        if private.st_uid != os.geteuid() or private.st_gid != os.getegid() or stat.S_IMODE(private.st_mode) != 0o700:
            return False
        os.mkdir("failure-output", 0o700, dir_fd=private_fd)
        capture_fd = os.open("failure-output", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=private_fd)
        fds.append(capture_fd)
        capture = os.fstat(capture_fd)
        if capture.st_uid != os.geteuid() or capture.st_gid != os.getegid() or stat.S_IMODE(capture.st_mode) != 0o700:
            return False
        written = []
        for name, content in {**contents, PRIVATE_FAILURE_OUTPUT_ARCHIVE: data}.items():
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                         0o600, dir_fd=capture_fd)
            fds.append(fd)
            os.fchmod(fd, 0o600)
            with os.fdopen(fd, "wb", closefd=False) as stream:
                stream.write(content)
                stream.flush()
            info = os.fstat(fd)
            written.append((name, fd, info, len(content)))
        def identity(info):
            return (info.st_dev, info.st_ino, info.st_uid, info.st_gid, info.st_mode, info.st_nlink,
                    info.st_size, info.st_mtime_ns, info.st_ctime_ns)
        for name, fd, before, size in written:
            if (not stat.S_ISREG(before.st_mode) or before.st_uid != os.geteuid() or before.st_gid != os.getegid()
                    or stat.S_IMODE(before.st_mode) != 0o600 or before.st_nlink != 1 or before.st_size != size
                    or identity(before) != identity(os.fstat(fd))
                    or identity(before) != identity(os.stat(name, dir_fd=capture_fd, follow_symlinks=False))):
                return False
        for fd, before, named in ((parent_fd, parent, proof_directory.lstat()),
                                 (private_fd, private, os.stat("private-diagnostics", dir_fd=parent_fd, follow_symlinks=False)),
                                 (capture_fd, capture, os.stat("failure-output", dir_fd=private_fd, follow_symlinks=False))):
            current = os.fstat(fd)
            if ((before.st_dev, before.st_ino, before.st_uid, before.st_gid, before.st_mode)
                    != (current.st_dev, current.st_ino, current.st_uid, current.st_gid, current.st_mode)
                    or identity(current) != identity(named)):
                return False
        return True
    except (ProofFailure, OSError, ValueError, TypeError, subprocess.SubprocessError):
        return False
    finally:
        for fd in reversed(fds):
            try:
                os.close(fd)
            except OSError:
                pass


def run_observation_launcher(command: list[str], work_root: Path, proof_directory: Path, *,
                             output_parent: Path | None = None, slot: str | None = None) -> tuple[bytes, bytes]:
    """On failure publish only a validated host-category receipt, never launcher output tails.

    The root launcher owns the optional private 0600 record. The driver reads it through
    the root helper after failure; these diagnostics confer no runtime or gate authority.
    Closed-field validation delegates to the same launcher's validate_failure_diagnostic;
    broker checkpoints and journal metadata are permitted only for worker-exit with
    worker-protocol-incomplete or worker-unsuccessful. No second schema or raw-output
    fallback is maintained here, and either cause still terminates this proof as failed.
    """
    command = [*command, "--diagnostic-directory", str(work_root)]
    code, stdout, stderr = root_command(
        command, cwd=ROOT, timeout=LAUNCHER_TIMEOUT_SECONDS, label="production Observation launcher")
    if code == 0:
        return stdout, stderr
    safe_record = None
    reader = (
        "import importlib.util,json,sys; "
        "spec=importlib.util.spec_from_file_location('diagnostic_launcher',sys.argv[1]); "
        "module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module); "
        "from pathlib import Path; "
        "print(json.dumps(module.read_failure_diagnostic(Path(sys.argv[2])),separators=(',',':')))"
    )
    try:
        read_code, data, _ = root_command(
            ["/usr/bin/python3", "-c", reader, str(LAUNCHER), str(work_root)],
            cwd=ROOT, timeout=15, label="private launcher failure categories", binary_output=True)
        if read_code == 0 and len(data) <= 4096:
            launcher = load_launcher_contract()
            try:
                safe_record = launcher.validate_failure_diagnostic(json.loads(data))
            except (launcher.LauncherError, TypeError, ValueError, UnicodeDecodeError):
                pass
    except ProofFailure:
        pass
    retain_private_worker_journal(work_root, proof_directory)
    if safe_record is not None and output_parent is not None and slot is not None:
        retain_private_failure_output(work_root, output_parent, slot, proof_directory, safe_record)
    if safe_record is None:
        fail(f"Production Observation launcher exited {code}; safe diagnostic unavailable.")
    encoded = json.dumps(safe_record, sort_keys=True, separators=(",", ":")) + "\n"
    try:
        with (proof_directory / "launcher-failure.json").open("x") as output:
            output.write(encoded)
    except OSError:
        fail(f"Production Observation launcher exited {code}; safe diagnostic could not be retained.")
    fail(f"Production Observation launcher exited {code}.\n{encoded.strip()}")


def launcher_args(
    *,
    tool_root: Path,
    policy_file: Path,
    output_parent: Path,
    bindings: dict[str, str],
    mode: str,
    slot: str,
    subject_root: Path = ROOT,
) -> list[str]:
    return [
        "/usr/bin/python3", str(LAUNCHER),
        "--tool-root", str(tool_root),
        "--subject-root", str(subject_root),
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
    *, tool_root: Path, policy_file: Path, output_parent: Path, bindings: dict[str, str], env: dict[str, str],
    subject_root: Path = ROOT,
) -> None:
    slot = "trusted-proof-denied"
    command = launcher_args(
        tool_root=tool_root,
        policy_file=policy_file,
        output_parent=output_parent,
        bindings=bindings,
        mode="trusted",
        slot=slot,
        subject_root=subject_root,
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


def reserve_structural_verification_directory(work_root: Path) -> Path:
    """Reserve driver-owned private copies before the launcher parent becomes root-owned.

    Only a fresh generated workspace owned by this driver is accepted. An existing child
    is never adopted, and these local copies supply no admission or protected-gate authority.
    """
    try:
        info = work_root.lstat()
    except OSError:
        fail("Structural verification requires a fresh private workspace.")
    if (not runtime_workspace_path(work_root)
            or not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid()
            or stat.S_IMODE(info.st_mode) != 0o700):
        fail("Structural verification requires a fresh private workspace.")
    directory = work_root / "structural-verification"
    try:
        directory.mkdir(mode=0o700)
    except OSError:
        fail("The structural verification directory must be a fresh private child.")
    return directory


def verify_collected_structure(cli_dll: Path, artifacts: dict[str, bytes], work_root: Path) -> str:
    """Re-resolve the collected plan and recompute manifest bindings with the actual published CLI.

    This is structural verification only. The protected artifact channel, current-run binding,
    and consumer/platform acceptance remain separate proof obligations. Its private directory
    must already be reserved before the workspace parent is protected by the root launcher.
    """
    directory = work_root / "structural-verification"
    try:
        parent_info = work_root.lstat()
        info = directory.lstat()
    except OSError:
        fail("The reserved structural verification directory is missing or unsafe.")
    if (not runtime_workspace_path(work_root)
            or not stat.S_ISDIR(parent_info.st_mode) or parent_info.st_uid != 0
            or stat.S_IMODE(parent_info.st_mode) != 0o755
            or not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid()
            or stat.S_IMODE(info.st_mode) != 0o700):
        fail("The reserved structural verification directory is missing or unsafe.")
    for name in ("evidence-plan.json", "evidence-manifest.json"):
        path = directory / name
        try:
            with path.open("xb") as target:
                target.write(artifacts[name])
            os.chmod(path, 0o600)
        except OSError:
            fail("Structural verification requires fresh private plan and manifest copies.")
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
    staged_subject_hashes: dict[str, str],
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
        "subjectInputScope": "declared-runtime-fixture",
        "stagedSubjectFilesSha256": staged_subject_hashes,
        "stagedSubjectManifestSha256": hashlib.sha256(
            json.dumps(staged_subject_hashes, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
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

    work_root = create_launcher_workspace()
    try:
        reserve_structural_verification_directory(work_root)
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
        subject_source, staged_subject_hashes = stage_runtime_subject(work_root, source_hashes)
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

        protect_launcher_workspace(work_root)
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

        assert_trusted_denied(
            tool_root=tool_root, policy_file=policy_file, output_parent=trusted_parent,
            bindings=bindings, env=env,
            subject_root=subject_source,
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
            subject_root=subject_source,
        )
        stdout, stderr = run_observation_launcher(observation, work_root, proof_directory,
                                                  output_parent=output_parent, slot=observation_slot)
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
        verify_staged_subject(subject_source, staged_subject_hashes)

        write_public_artifacts(
            proof_directory, artifacts, bindings, checked_out_revision, source_hashes,
            source_digest, policy_bytes, cli_hash, reporter_hash, reporter_target,
            build_log_hash, verification, observation_slot, mechanism_results, staged_subject_hashes,
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
