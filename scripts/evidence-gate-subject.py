#!/usr/bin/env python3
"""Launch one bounded, credentialless AppSurface subject profile with rootless Podman.

This is an execution primitive, not an evidence verifier or a gate.  It accepts
only controller-owned paths, a digest-pinned image, a policy profile ID, and
validated resource limits. The writable container scratch is a fixed-size,
fixed-inode host tmpfs bind mount; the trusted entrypoint streams its small
fixed result record over the attached process output, where this launcher
validates it and writes it outside the mount to private host scratch. Other
container scratch artifacts are discarded. The
image must contain the trusted fixed entrypoint
``/usr/local/libexec/appsurface-subject-runner`` and its locked offline inputs.
The entrypoint receives a fixed profile contract; this program never reads a
command from the subject checkout.  Only ``code-coverage`` is enabled, with
network disabled.  Resource-backed profiles remain rejected until their
network/resource envelope has a separately reviewed implementation.

The launcher consumes the bounded result record in memory, then requires an
independent supervisor's completed cleanup attestation before returning. The
supervisor removes the container, private host tmpfs, and per-run scratch. All
output remains untrusted; even exit code zero does not create complete Evidence
or authorize a CI verdict.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import selectors
import shutil
import signal
import stat
import subprocess
import sys
import threading
import time
from typing import Any, Callable, Mapping, Protocol, Sequence


ENGINE_PATH = "/usr/bin:/usr/local/bin:/bin"
SUDO_PATH = "/usr/bin/sudo"
MOUNTINFO_PATH = Path("/proc/self/mountinfo")
PROC_ROOT = Path("/proc")
CONTAINER_ENTRYPOINT = "/usr/local/libexec/appsurface-subject-runner"
SUPERVISOR_SCRIPT_NAME = "evidence-gate-subject-supervisor.py"
SUPERVISOR_OWNER_LABEL = "io.forge-trust.appsurface.cleanup-token"
SUPERVISOR_CLEANUP_DEADLINE_SECONDS = 10 * 60
SUPERVISOR_STARTUP_TIMEOUT_SECONDS = 30
MAX_SUPERVISOR_RECORD_BYTES = 4096
SUPERVISOR_TOKEN_PATTERN = re.compile(r"[0-9a-f]{32}\Z")
CONTAINER_UID = 65532
CONTAINER_GID = 65532
PODMAN_DEFAULT_CAPABILITIES = frozenset(
    {
        "CAP_CHOWN", "CAP_DAC_OVERRIDE", "CAP_FOWNER", "CAP_FSETID", "CAP_KILL",
        "CAP_NET_BIND_SERVICE", "CAP_SETFCAP", "CAP_SETGID", "CAP_SETPCAP",
        "CAP_SETUID", "CAP_SYS_CHROOT",
    }
)
ENGINE_PHASE_TIMEOUT_SECONDS = 10
MOUNT_PHASE_TIMEOUT_SECONDS = 30
CLEANUP_TIMEOUT_SECONDS = 5
MAX_ENGINE_OUTPUT_BYTES = 256 * 1024
MAX_PROFILE_TIMEOUT_SECONDS = 1200
MAX_PROFILE_MEMORY_MIB = 4096
MAX_PROFILE_CPU_MILLIS = 2000
MAX_PROFILE_PIDS = 256
MAX_PROFILE_OUTPUT_BYTES = 1024 * 1024
MAX_PROFILE_SCRATCH_BYTES = 4 * 1024 * 1024 * 1024
MAX_PROFILE_SCRATCH_INODES = 262_144
MAX_SUBJECT_RESULT_BYTES = 16 * 1024
MAX_SOURCE_DIFF_BYTES = 20 * 1024 * 1024
MIN_OUTPUT_BYTES = 4096
SOURCE_DIFF_CONTAINER_PATH = "/source.diff"
SOURCE_DIFF_SHA256_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
IMAGE_REFERENCE_PATTERN = re.compile(r"[a-z0-9][a-z0-9./:_-]*@sha256:[0-9a-f]{64}\Z")
RUN_ID_PATTERN = re.compile(r"[1-9][0-9]{0,18}\Z")
RUN_ATTEMPT_PATTERN = re.compile(r"[1-9][0-9]{0,8}\Z")
SUBJECT_RESULT_RELATIVE_PATH = "evidence-subject-result.json"
SUBJECT_RESULT_STEP_NAMES = (
    "dotnet-sdk-version",
    "dotnet-runtime-list",
    "offline-locked-restore",
    "coverage-run",
    "coverage-gate",
)
SUBJECT_STEP_DIGEST_PATTERN = re.compile(r"[0-9a-f]{64}\Z")

# This is a closed registry, intentionally independent of subject files and
# policy-provided command strings. Resource-backed profiles are named here so
# they fail with an actionable, stable diagnostic instead of an unknown-profile
# fallback that might accidentally grant a weaker capability.
RESOURCE_BACKED_PROFILES = frozenset(
    {
        "postgresql-integration",
        "keycloak-theme-integration",
        "package-release-preparation",
        "pr-conservative",
        "protected-release",
    }
)
PROFILE_ENTRYPOINT_ARGUMENTS: Mapping[str, tuple[str, ...]] = {
    "code-coverage": (
        "--profile-id=code-coverage",
        "--subject-root=/subject",
        "--scratch-root=/scratch",
        "--dependency-source=/opt/appsurface/locked-dependencies",
        "--diff-file=/source.diff",
        "--offline",
    ),
}
CONTAINER_ENVIRONMENT = (
    "PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
    "HOME=/scratch/home",
    "TMPDIR=/scratch/tmp",
    "TMP=/scratch/tmp",
    "TEMP=/scratch/tmp",
    "DOTNET_CLI_HOME=/scratch/home/.dotnet",
    "DOTNET_CLI_TELEMETRY_OPTOUT=1",
    "DOTNET_NOLOGO=1",
    "NUGET_PACKAGES=/opt/appsurface/locked-dependencies",
)


class SubjectLauncherError(Exception):
    """A stable, bounded launcher diagnostic safe to print in trusted logs."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


class _ProcessCancelled(Exception):
    pass


class _ProcessTimedOut(Exception):
    pass


class _ProcessOutputExceeded(Exception):
    pass


class _ProcessStartFailed(Exception):
    pass


class _ProcessCouldNotStop(Exception):
    pass


@dataclass(frozen=True)
class SubjectLimits:
    """Controller-selected limits, each checked against a reviewed hard cap."""

    timeout_seconds: int
    memory_mib: int
    cpu_millis: int
    pids: int
    output_bytes: int


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: bytes
    stderr: bytes


@dataclass(frozen=True)
class SubjectRunResult:
    """Untrusted output and one bounded result record; profile artifacts are not exported and this is never gate-eligible."""

    exit_code: int
    stdout: bytes
    stderr: bytes
    scratch_directory: Path
    execution_record: Mapping[str, Any] | None = None

    @property
    def claim_eligible(self) -> bool:
        return False


@dataclass(frozen=True)
class SupervisorSession:
    """Validated identity and private paths for one detached cleanup supervisor."""

    owner_token: str
    owner_label: str
    manifest_path: Path
    state_directory: Path
    supervisor_pid: int
    supervisor_start_time: int
    ready: bool
    state_directory_device: int
    state_directory_inode: int

    @property
    def attestation_path(self) -> Path:
        return self.state_directory / "cleanup-attestation.json"


class CleanupSupervisorClient(Protocol):
    """Public lifecycle seam used by ``run_profile`` and its integration tests."""

    def start(
        self,
        *,
        container_name: str,
        scratch_directory: Path,
        mountpoint: Path,
        runner_temp: Path,
        home: Path,
        runtime: Path,
        parent_pid: int,
    ) -> SupervisorSession: ...

    def assert_alive(self, session: SupervisorSession) -> None: ...

    def request_and_wait(self, session: SupervisorSession) -> Mapping[str, Any]: ...


CommandExecutor = Callable[..., CommandResult]


def _linux_process_identity(pid: int, *, proc_root: Path = PROC_ROOT) -> tuple[int, str] | None:
    """Read the Linux process start time and state used to reject PID reuse."""
    try:
        raw = (proc_root / str(pid) / "stat").read_text(encoding="ascii")
        closing_paren = raw.rfind(")")
        if closing_paren < 0:
            return None
        fields = raw[closing_paren + 1 :].split()
        if len(fields) <= 19 or len(fields[0]) != 1:
            return None
        return int(fields[19]), fields[0]
    except (OSError, UnicodeError, ValueError):
        return None


def _read_private_json_record(path: Path, *, directory: Path, uid: int, maximum_bytes: int) -> Mapping[str, Any]:
    """Read a bounded regular file without following a replaced path or symlink."""
    try:
        directory_info = directory.lstat()
        if (
            not stat.S_ISDIR(directory_info.st_mode)
            or directory_info.st_uid != uid
            or stat.S_IMODE(directory_info.st_mode) != 0o700
        ):
            raise ValueError("unsafe record directory")
        directory_fd = os.open(
            directory,
            os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            opened_directory_info = os.fstat(directory_fd)
            if (
                opened_directory_info.st_dev != directory_info.st_dev
                or opened_directory_info.st_ino != directory_info.st_ino
                or opened_directory_info.st_uid != uid
                or stat.S_IMODE(opened_directory_info.st_mode) != 0o700
            ):
                raise ValueError("record directory changed")
            fd = os.open(
                path.name,
                os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=directory_fd,
            )
            try:
                info = os.fstat(fd)
                if (
                    not stat.S_ISREG(info.st_mode)
                    or info.st_uid != uid
                    or stat.S_IMODE(info.st_mode) != 0o600
                    or info.st_nlink != 1
                    or info.st_size <= 0
                    or info.st_size > maximum_bytes
                ):
                    raise ValueError("unsafe record")
                chunks: list[bytes] = []
                remaining = maximum_bytes + 1
                while remaining:
                    chunk = os.read(fd, min(4096, remaining))
                    if not chunk:
                        break
                    chunks.append(chunk)
                    remaining -= len(chunk)
                content = b"".join(chunks)
                if len(content) > maximum_bytes:
                    raise ValueError("oversized record")
            finally:
                os.close(fd)
        finally:
            os.close(directory_fd)
    except FileNotFoundError:
        raise
    except (OSError, ValueError):
        raise _fail("ASEGS017", "The cleanup supervisor record is unavailable or unsafe.") from None
    try:
        value = _parse_json(content, "cleanup supervisor record")
    except SubjectLauncherError:
        raise _fail("ASEGS017", "The cleanup supervisor record is malformed.") from None
    if not isinstance(value, dict):
        raise _fail("ASEGS017", "The cleanup supervisor record is malformed.")
    return value


class SubjectCleanupSupervisorClient:
    """Start and communicate with the fixed detached cleanup supervisor."""

    def __init__(
        self,
        *,
        runner_temp: Path,
        home: Path,
        runtime: Path,
        executor: CommandExecutor | None = None,
        process_identity_reader: Callable[[int], tuple[int, str] | None] = _linux_process_identity,
        monotonic: Callable[[], float] = time.monotonic,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        self.runner_temp = runner_temp
        self.home = home
        self.runtime = runtime
        self.executor = executor or _run_command
        self.process_identity_reader = process_identity_reader
        self.monotonic = monotonic
        self.sleep = sleep
        self.uid = os.geteuid()
        self.environment = {
            "PATH": ENGINE_PATH,
            "HOME": str(home),
            "XDG_RUNTIME_DIR": str(runtime),
            "RUNNER_TEMP": str(runner_temp),
        }
        self.script_path = Path(__file__).resolve().with_name(SUPERVISOR_SCRIPT_NAME)

    def _run(self, arguments: Sequence[str], *, timeout_seconds: float) -> CommandResult:
        return self.executor(
            arguments,
            timeout_seconds=timeout_seconds,
            maximum_output_bytes=MAX_SUPERVISOR_RECORD_BYTES,
            environment=self.environment,
            cancel_event=threading.Event(),
        )

    def _validate_session_paths(self, session: SupervisorSession) -> Path:
        if (
            not isinstance(session, SupervisorSession)
            or session.ready is not True
            or not isinstance(session.owner_token, str)
            or SUPERVISOR_TOKEN_PATTERN.fullmatch(session.owner_token) is None
            or session.owner_label != SUPERVISOR_OWNER_LABEL
            or type(session.supervisor_pid) is not int
            or session.supervisor_pid <= 1
            or type(session.supervisor_start_time) is not int
            or session.supervisor_start_time <= 0
        ):
            raise _fail("ASEGS017", "The cleanup supervisor did not provide a ready owned session.")
        expected_state = self.runner_temp / f"appsurface-subject-supervisor-{session.owner_token}"
        expected_manifest = expected_state / "supervisor.json"
        try:
            state = _path_without_symlink_components(session.state_directory, "The cleanup supervisor state")
            manifest = _path_without_symlink_components(session.manifest_path, "The cleanup supervisor manifest")
            info = state.lstat()
            if (
                state != expected_state
                or manifest != expected_manifest
                or state.parent != self.runner_temp
                or info.st_uid != self.uid
                or not stat.S_ISDIR(info.st_mode)
                or stat.S_IMODE(info.st_mode) != 0o700
                or (info.st_dev, info.st_ino)
                != (session.state_directory_device, session.state_directory_inode)
            ):
                raise ValueError("state identity mismatch")
        except (SubjectLauncherError, OSError, ValueError):
            raise _fail("ASEGS017", "The cleanup supervisor state path is unsafe.") from None
        return state

    def start(
        self,
        *,
        container_name: str,
        scratch_directory: Path,
        mountpoint: Path,
        runner_temp: Path,
        home: Path,
        runtime: Path,
        parent_pid: int,
    ) -> SupervisorSession:
        if runner_temp != self.runner_temp or home != self.home or runtime != self.runtime:
            raise _fail("ASEGS017", "The cleanup supervisor context differs from the validated runner paths.")
        try:
            resolved_script = _path_without_symlink_components(
                self.script_path, "The trusted cleanup supervisor"
            )
            if not stat.S_ISREG(resolved_script.stat().st_mode):
                raise _fail("ASEGS017", "The trusted cleanup supervisor is unavailable.")
        except (SubjectLauncherError, OSError):
            raise _fail("ASEGS017", "The trusted cleanup supervisor is unavailable.") from None
        parent_identity = self.process_identity_reader(parent_pid)
        if (
            parent_identity is None
            or not isinstance(parent_identity, tuple)
            or len(parent_identity) != 2
            or parent_identity[1] in {"Z", "X"}
        ):
            raise _fail("ASEGS017", "The launcher process identity could not be verified for cleanup.")
        parent_start_time = parent_identity[0]
        result = self._run(
            [
                sys.executable,
                str(resolved_script),
                "start",
                "--container-name",
                container_name,
                "--scratch-directory",
                str(scratch_directory),
                "--mountpoint",
                str(mountpoint),
                "--parent-pid",
                str(parent_pid),
                "--parent-start-time",
                str(parent_start_time),
            ],
            timeout_seconds=SUPERVISOR_STARTUP_TIMEOUT_SECONDS,
        )
        if (
            result.returncode != 0
            or result.stderr
            or len(result.stdout) > MAX_SUPERVISOR_RECORD_BYTES
        ):
            raise _fail("ASEGS017", "The cleanup supervisor failed its bounded startup handshake.")
        document = _parse_json(result.stdout, "cleanup supervisor readiness record")
        expected_keys = {
            "schemaVersion", "claimEligible", "ownerToken", "stateDirectory", "manifestPath",
            "supervisorPid", "supervisorStartTime", "supervisorReady", "ownerLabel",
        }
        if (
            not isinstance(document, dict)
            or set(document) != expected_keys
            or type(document.get("schemaVersion")) is not int
            or document.get("schemaVersion") != 1
            or document.get("claimEligible") is not False
            or document.get("supervisorReady") is not True
            or document.get("ownerLabel") != SUPERVISOR_OWNER_LABEL
            or not isinstance(document.get("ownerToken"), str)
            or SUPERVISOR_TOKEN_PATTERN.fullmatch(document["ownerToken"]) is None
            or type(document.get("supervisorPid")) is not int
            or document["supervisorPid"] <= 1
            or type(document.get("supervisorStartTime")) is not int
            or document["supervisorStartTime"] <= 0
            or not isinstance(document.get("stateDirectory"), str)
            or not isinstance(document.get("manifestPath"), str)
        ):
            raise _fail("ASEGS017", "The cleanup supervisor readiness record is incomplete.")
        token = document["ownerToken"]
        session = SupervisorSession(
            owner_token=token,
            owner_label=SUPERVISOR_OWNER_LABEL,
            manifest_path=Path(document["manifestPath"]),
            state_directory=Path(document["stateDirectory"]),
            supervisor_pid=document["supervisorPid"],
            supervisor_start_time=document["supervisorStartTime"],
            ready=True,
            state_directory_device=-1,
            state_directory_inode=-1,
        )
        state = self._validate_session_start_paths(session)
        manifest = _read_private_json_record(
            session.manifest_path,
            directory=state,
            uid=self.uid,
            maximum_bytes=MAX_SUPERVISOR_RECORD_BYTES,
        )
        if (
            manifest.get("schemaVersion") != 1
            or manifest.get("ownerToken") != token
            or manifest.get("containerName") != container_name
            or manifest.get("scratchDirectory") != str(scratch_directory)
            or manifest.get("mountpoint") != str(mountpoint)
            or manifest.get("parentPid") != parent_pid
            or manifest.get("parentStartTime") != parent_start_time
        ):
            raise _fail("ASEGS017", "The cleanup supervisor manifest does not match this subject run.")
        state_info = state.lstat()
        session = SupervisorSession(
            **{**session.__dict__, "state_directory_device": state_info.st_dev, "state_directory_inode": state_info.st_ino}
        )
        self._validate_session_paths(session)
        try:
            session.attestation_path.lstat()
        except FileNotFoundError:
            pass
        except OSError:
            raise _fail("ASEGS017", "The cleanup supervisor attestation path is unsafe.") from None
        else:
            raise _fail("ASEGS017", "The cleanup supervisor returned a stale attestation.")
        self.assert_alive(session)
        return session

    def _validate_session_start_paths(self, session: SupervisorSession) -> Path:
        expected_state = self.runner_temp / f"appsurface-subject-supervisor-{session.owner_token}"
        expected_manifest = expected_state / "supervisor.json"
        try:
            state = _path_without_symlink_components(session.state_directory, "The cleanup supervisor state")
            manifest = _path_without_symlink_components(session.manifest_path, "The cleanup supervisor manifest")
            state_info = state.lstat()
            if (
                state != expected_state
                or manifest != expected_manifest
                or state.parent != self.runner_temp
                or state_info.st_uid != self.uid
                or not stat.S_ISDIR(state_info.st_mode)
                or stat.S_IMODE(state_info.st_mode) != 0o700
            ):
                raise ValueError("unsafe state path")
            return state
        except (SubjectLauncherError, OSError, ValueError):
            raise _fail("ASEGS017", "The cleanup supervisor state path is unsafe.") from None

    def assert_alive(self, session: SupervisorSession) -> None:
        self._validate_session_paths(session)
        identity = self.process_identity_reader(session.supervisor_pid)
        if (
            identity is None
            or not isinstance(identity, tuple)
            or len(identity) != 2
            or identity[0] != session.supervisor_start_time
            or identity[1] in {"Z", "X"}
        ):
            raise _fail("ASEGS017", "The cleanup supervisor stopped before subject setup completed.")

    def request_and_wait(self, session: SupervisorSession) -> Mapping[str, Any]:
        state = self._validate_session_paths(session)
        try:
            session.attestation_path.lstat()
        except FileNotFoundError:
            pass
        except OSError:
            raise _fail("ASEGS017", "The cleanup supervisor attestation path is unsafe.") from None
        else:
            record = _read_private_json_record(
                session.attestation_path,
                directory=state,
                uid=self.uid,
                maximum_bytes=MAX_SUPERVISOR_RECORD_BYTES,
            )
            self._validate_attestation(record)
            return record
        self.assert_alive(session)
        deadline = self.monotonic() + SUPERVISOR_CLEANUP_DEADLINE_SECONDS
        request = self._run(
            [
                sys.executable,
                str(self.script_path),
                "request",
                "--manifest",
                str(session.manifest_path),
                "--owner-token",
                session.owner_token,
            ],
            timeout_seconds=min(
                SUPERVISOR_STARTUP_TIMEOUT_SECONDS,
                max(0.001, deadline - self.monotonic()),
            ),
        )
        if request.returncode != 0 or request.stdout or request.stderr:
            raise _fail("ASEGS017", "The cleanup supervisor rejected the bounded cleanup request.")
        attestation_path = session.attestation_path
        while True:
            try:
                _path_without_symlink_components(state, "The cleanup supervisor state")
                record = _read_private_json_record(
                    attestation_path,
                    directory=state,
                    uid=self.uid,
                    maximum_bytes=MAX_SUPERVISOR_RECORD_BYTES,
                )
            except FileNotFoundError:
                if self.monotonic() >= deadline:
                    raise _fail("ASEGS017", "The cleanup supervisor did not produce a completed attestation.") from None
                self.sleep(min(0.2, max(0.0, deadline - self.monotonic())))
                continue
            self._validate_attestation(record)
            return record

    @staticmethod
    def _validate_attestation(record: Mapping[str, Any]) -> None:
        expected_keys = {
            "schemaVersion", "claimEligible", "published", "status", "trigger", "containerStatus",
            "mountStatus", "scratchStatus", "failureCode", "cleanupDeadlineSeconds",
        }
        if (
            not isinstance(record, Mapping)
            or set(record) != expected_keys
            or type(record.get("schemaVersion")) is not int
            or record.get("schemaVersion") != 1
            or record.get("claimEligible") is not False
            or record.get("published") is not False
            or record.get("status") != "complete"
            or record.get("trigger") not in {"request", "parent-exit"}
            or record.get("containerStatus") not in {"removed", "absent"}
            or record.get("mountStatus") not in {"unmounted", "absent"}
            or record.get("scratchStatus") not in {"removed", "absent"}
            or record.get("failureCode") != "none"
            or record.get("cleanupDeadlineSeconds") != SUPERVISOR_CLEANUP_DEADLINE_SECONDS
        ):
            raise _fail("ASEGS017", "The cleanup supervisor attestation is incomplete; no claim may be issued.")


def _fail(code: str, message: str) -> SubjectLauncherError:
    return SubjectLauncherError(code, message)


def _bounded_integer(value: Any, name: str, minimum: int, maximum: int) -> int:
    if type(value) is not int or value < minimum or value > maximum:
        raise _fail("ASEGS001", f"{name} is outside its controller-approved bounds.")
    return value


def _validate_limits(limits: SubjectLimits) -> None:
    if not isinstance(limits, SubjectLimits):
        raise _fail("ASEGS001", "Controller-owned profile limits are required.")
    _bounded_integer(limits.timeout_seconds, "Execution timeout", 1, MAX_PROFILE_TIMEOUT_SECONDS)
    _bounded_integer(limits.memory_mib, "Memory limit", 256, MAX_PROFILE_MEMORY_MIB)
    _bounded_integer(limits.cpu_millis, "CPU limit", 250, MAX_PROFILE_CPU_MILLIS)
    if limits.cpu_millis % 250 != 0:
        raise _fail("ASEGS001", "CPU limit must use a supported quarter-core increment.")
    _bounded_integer(limits.pids, "PID limit", 32, MAX_PROFILE_PIDS)
    _bounded_integer(limits.output_bytes, "Output limit", MIN_OUTPUT_BYTES, MAX_PROFILE_OUTPUT_BYTES)


def _path_without_symlink_components(value: str | os.PathLike[str], name: str) -> Path:
    raw = os.fspath(value)
    if not isinstance(raw, str) or not raw or "\x00" in raw or not Path(raw).is_absolute():
        raise _fail("ASEGS002", f"{name} must be an absolute controller-owned path.")
    path = Path(raw)
    if any(part in {".", ".."} for part in path.parts):
        raise _fail("ASEGS002", f"{name} contains an unsafe path component.")
    current = Path(path.anchor)
    try:
        for part in path.parts[1:]:
            current = current / part
            item_stat = current.lstat()
            if stat.S_ISLNK(item_stat.st_mode):
                raise _fail("ASEGS002", f"{name} traverses a symbolic link.")
        resolved = path.resolve(strict=True)
    except SubjectLauncherError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise _fail("ASEGS002", f"{name} is unavailable or unsafe.") from None
    if resolved != path:
        raise _fail("ASEGS002", f"{name} is not canonical.")
    return resolved


def _validate_subject_root(value: str | os.PathLike[str]) -> Path:
    raw = os.fspath(value)
    if any(char in raw for char in ",\r\n"):
        raise _fail("ASEGS002", "The trusted subject checkout path contains an unsafe mount option character.")
    path = _path_without_symlink_components(value, "The trusted subject checkout")
    try:
        if not stat.S_ISDIR(path.stat().st_mode):
            raise _fail("ASEGS002", "The trusted subject checkout is not a directory.")
    except OSError:
        raise _fail("ASEGS002", "The trusted subject checkout is unavailable.") from None
    return path


def _validate_runner_context(
    environment: Mapping[str, str],
    *,
    system_name: str,
    effective_uid: int,
) -> tuple[Path, Path, Path]:
    if system_name != "Linux":
        raise _fail("ASEGS003", "Subject execution requires a Linux runner.")
    if effective_uid == 0:
        raise _fail("ASEGS003", "Subject execution cannot run as host root.")
    if environment.get("GITHUB_ACTIONS") != "true" or environment.get("RUNNER_ENVIRONMENT") != "github-hosted":
        raise _fail("ASEGS003", "Subject execution requires an ephemeral GitHub-hosted runner.")
    if RUN_ID_PATTERN.fullmatch(environment.get("GITHUB_RUN_ID", "")) is None:
        raise _fail("ASEGS003", "Trusted GitHub run identity is missing or malformed.")
    if RUN_ATTEMPT_PATTERN.fullmatch(environment.get("GITHUB_RUN_ATTEMPT", "")) is None:
        raise _fail("ASEGS003", "Trusted GitHub run attempt is missing or malformed.")

    try:
        runner_temp = _path_without_symlink_components(environment["RUNNER_TEMP"], "RUNNER_TEMP")
        home = _path_without_symlink_components(environment["HOME"], "The host engine home")
        runtime_value = environment.get("XDG_RUNTIME_DIR") or f"/run/user/{effective_uid}"
        runtime = _path_without_symlink_components(runtime_value, "The rootless engine runtime directory")
        if not all(stat.S_ISDIR(path.stat().st_mode) for path in (runner_temp, home, runtime)):
            raise _fail("ASEGS003", "The hosted runner directories are unavailable.")
        runtime_stat = runtime.stat()
        if runtime_stat.st_uid != effective_uid or stat.S_IMODE(runtime_stat.st_mode) & 0o077:
            raise _fail("ASEGS003", "The rootless engine runtime directory is not private to this runner user.")
    except KeyError:
        raise _fail("ASEGS003", "The hosted runner directory context is incomplete.") from None
    except OSError:
        raise _fail("ASEGS003", "The hosted runner directory context is unavailable.") from None
    return runner_temp, home, runtime


def _validate_scratch_path(value: str | os.PathLike[str], runner_temp: Path) -> Path:
    raw = os.fspath(value)
    if not isinstance(raw, str) or not raw or "\x00" in raw or not Path(raw).is_absolute():
        raise _fail("ASEGS002", "The scratch path must be an absolute new path under RUNNER_TEMP.")
    path = Path(raw)
    if any(part in {".", ".."} for part in path.parts) or any(char in raw for char in ",\r\n"):
        raise _fail("ASEGS002", "The scratch path contains an unsafe component.")
    if path.parent != runner_temp or path.name in {"", ".", ".."}:
        raise _fail("ASEGS002", "The scratch directory must be a direct child of RUNNER_TEMP.")
    try:
        path.lstat()
    except FileNotFoundError:
        return path
    except OSError:
        raise _fail("ASEGS002", "The scratch destination cannot be inspected safely.") from None
    raise _fail("ASEGS002", "The scratch directory must be new and must not already exist.")


def _validate_image_reference(image: str) -> None:
    if not isinstance(image, str) or IMAGE_REFERENCE_PATTERN.fullmatch(image) is None:
        raise _fail("ASEGS004", "The subject image must use a canonical sha256 digest reference.")
    image_name = image.split("@", 1)[0]
    if "//" in image_name or any(part in {".", ".."} for part in image_name.split("/")):
        raise _fail("ASEGS004", "The subject image reference is malformed.")


def _validate_profile(profile_id: str) -> tuple[str, ...]:
    if not isinstance(profile_id, str):
        raise _fail("ASEGS005", "The subject profile ID is malformed.")
    if profile_id in RESOURCE_BACKED_PROFILES:
        raise _fail(
            "ASEGS005",
            "Resource-backed subject profiles are disabled until an explicit bounded network envelope is implemented.",
        )
    arguments = PROFILE_ENTRYPOINT_ARGUMENTS.get(profile_id)
    if arguments is None:
        raise _fail("ASEGS005", "The subject profile is not in the controller-owned offline profile registry.")
    return arguments


def _validate_source_diff(value: str | os.PathLike[str], expected_sha256: str) -> Path:
    if not isinstance(expected_sha256, str) or SOURCE_DIFF_SHA256_PATTERN.fullmatch(expected_sha256) is None:
        raise _fail("ASEGS020", "The controller source diff digest is malformed.")
    raw = os.fspath(value)
    if not isinstance(raw, str) or any(character in raw for character in ",\r\n"):
        raise _fail("ASEGS020", "The controller source diff path contains an unsafe mount option character.")
    path = _path_without_symlink_components(value, "The controller source diff")
    descriptor = -1
    try:
        metadata = path.lstat()
        if (
            stat.S_ISLNK(metadata.st_mode)
            or not stat.S_ISREG(metadata.st_mode)
            or metadata.st_nlink != 1
            or metadata.st_size <= 0
            or metadata.st_size > MAX_SOURCE_DIFF_BYTES
        ):
            raise _fail("ASEGS020", "The controller source diff is not a bounded, private regular file.")
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
        opened = os.fstat(descriptor)
        if (
            not stat.S_ISREG(opened.st_mode)
            or opened.st_nlink != 1
            or (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino)
            or opened.st_size <= 0
            or opened.st_size > MAX_SOURCE_DIFF_BYTES
        ):
            raise _fail("ASEGS020", "The controller source diff changed or exceeds its byte limit.")
        digest = hashlib.sha256()
        total = 0
        while True:
            chunk = os.read(descriptor, min(1024 * 1024, MAX_SOURCE_DIFF_BYTES + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > MAX_SOURCE_DIFF_BYTES:
                raise _fail("ASEGS020", "The controller source diff exceeds its byte limit.")
            digest.update(chunk)
        after = os.fstat(descriptor)
        if (
            total != opened.st_size
            or (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
            != (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns)
            or digest.hexdigest() != expected_sha256
        ):
            raise _fail("ASEGS020", "The controller source diff changed or does not match its digest.")
    except SubjectLauncherError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise _fail("ASEGS020", "The controller source diff is unavailable or unsafe.") from None
    finally:
        if descriptor >= 0:
            try:
                os.close(descriptor)
            except OSError:
                raise _fail("ASEGS020", "The controller source diff could not be closed safely.") from None
    return path


def _resolve_podman() -> str:
    candidate = shutil.which("podman", path=ENGINE_PATH)
    if candidate is None:
        raise _fail("ASEGS006", "The required rootless Podman engine is unavailable.")
    try:
        resolved = Path(candidate).resolve(strict=True)
        info = resolved.stat()
        if not stat.S_ISREG(info.st_mode) or not os.access(resolved, os.X_OK):
            raise _fail("ASEGS006", "The Podman engine binary is unavailable or unsafe.")
        if info.st_uid != 0 or stat.S_IMODE(info.st_mode) & 0o022:
            raise _fail("ASEGS006", "The Podman engine binary is not protected by the runner image.")
    except SubjectLauncherError:
        raise
    except (OSError, RuntimeError):
        raise _fail("ASEGS006", "The Podman engine binary is unavailable or unsafe.") from None
    return str(resolved)


def _signal_process_group(process: Any, signal_number: int) -> None:
    try:
        os.killpg(process.pid, signal_number)
    except ProcessLookupError:
        return
    except OSError:
        try:
            if signal_number == signal.SIGTERM:
                process.terminate()
            else:
                process.kill()
        except OSError:
            pass


def _stop_process(process: Any) -> None:
    _signal_process_group(process, signal.SIGTERM)
    if process.poll() is None:
        try:
            process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            pass
    # The CLI leader may exit while a descendant still holds a captured pipe.
    _signal_process_group(process, signal.SIGKILL)
    if process.poll() is None:
        try:
            process.wait(timeout=1)
        except subprocess.TimeoutExpired:
            raise _ProcessCouldNotStop from None


def _run_command(
    arguments: Sequence[str],
    *,
    timeout_seconds: float,
    maximum_output_bytes: int,
    environment: Mapping[str, str],
    cancel_event: threading.Event,
) -> CommandResult:
    """Run a trusted fixed argv with bounded combined output and hard cancellation."""
    if not arguments or any(not isinstance(argument, str) or "\x00" in argument for argument in arguments):
        raise _ProcessStartFailed
    try:
        process = subprocess.Popen(
            list(arguments),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            close_fds=True,
            start_new_session=True,
            bufsize=0,
            cwd="/",
            env=dict(environment),
        )
    except (OSError, ValueError):
        raise _ProcessStartFailed from None

    selector = selectors.DefaultSelector()
    stdout = bytearray()
    stderr = bytearray()
    deadline = time.monotonic() + timeout_seconds
    try:
        if process.stdout is None or process.stderr is None:
            raise _ProcessStartFailed
        for pipe, label in ((process.stdout, "stdout"), (process.stderr, "stderr")):
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ, label)

        while selector.get_map() or process.poll() is None:
            if cancel_event.is_set():
                _stop_process(process)
                raise _ProcessCancelled
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                _stop_process(process)
                raise _ProcessTimedOut
            for key, _ in selector.select(min(0.1, remaining)):
                stream = stdout if key.data == "stdout" else stderr
                remaining_output = maximum_output_bytes - len(stdout) - len(stderr)
                try:
                    chunk = os.read(key.fileobj.fileno(), min(64 * 1024, remaining_output + 1))
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                    continue
                stream.extend(chunk)
                if len(stdout) + len(stderr) > maximum_output_bytes:
                    _stop_process(process)
                    raise _ProcessOutputExceeded

        return CommandResult(process.wait(), bytes(stdout), bytes(stderr))
    except (_ProcessCancelled, _ProcessTimedOut, _ProcessOutputExceeded, _ProcessStartFailed):
        if process.poll() is None:
            _stop_process(process)
        raise
    except (OSError, ValueError, selectors.SelectorError):
        if process.poll() is None:
            _stop_process(process)
        raise _ProcessStartFailed from None
    except BaseException:
        if process.poll() is None:
            _stop_process(process)
        raise
    finally:
        selector.close()
        for pipe in (process.stdout, process.stderr):
            if pipe is not None and not pipe.closed:
                pipe.close()


def _parse_json(data: bytes, description: str) -> Any:
    def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate JSON key")
            result[key] = value
        return result

    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise _fail("ASEGS007", f"The rootless OCI engine returned malformed {description}.") from None


def _expect_success(result: CommandResult, stage: str) -> CommandResult:
    if result.returncode != 0:
        raise _fail("ASEGS008", f"The rootless OCI engine failed during {stage}.")
    return result


def _preflight_rootless_engine(
    engine: str,
    *,
    environment: Mapping[str, str],
    cancel_event: threading.Event,
    executor: CommandExecutor,
) -> None:
    result = _expect_success(
        executor(
            [engine, "--remote=false", "info", "--format=json"],
            timeout_seconds=ENGINE_PHASE_TIMEOUT_SECONDS,
            maximum_output_bytes=MAX_ENGINE_OUTPUT_BYTES,
            environment=environment,
            cancel_event=cancel_event,
        ),
        "engine capability inspection",
    )
    info = _parse_json(result.stdout, "engine capability document")
    host = info.get("host") if isinstance(info, dict) else None
    security = host.get("security") if isinstance(host, dict) else None
    controllers = host.get("cgroupControllers") if isinstance(host, dict) else None
    if (
        not isinstance(host, dict)
        or not isinstance(security, dict)
        or security.get("rootless") is not True
        or host.get("cgroupVersion") != "v2"
        or not isinstance(controllers, list)
        or not all(isinstance(controller, str) for controller in controllers)
        or not {"cpu", "memory", "pids"}.issubset(set(controllers))
    ):
        raise _fail("ASEGS009", "Rootless OCI, cgroup v2, CPU, memory, and PID enforcement are required.")


def _verify_local_image(
    engine: str,
    image: str,
    *,
    environment: Mapping[str, str],
    cancel_event: threading.Event,
    executor: CommandExecutor,
) -> None:
    exists = executor(
        [engine, "--remote=false", "image", "exists", image],
        timeout_seconds=ENGINE_PHASE_TIMEOUT_SECONDS,
        maximum_output_bytes=4096,
        environment=environment,
        cancel_event=cancel_event,
    )
    if exists.returncode != 0:
        raise _fail("ASEGS010", "The pinned subject image is not already present in local engine storage.")
    inspected = _expect_success(
        executor(
            [engine, "--remote=false", "image", "inspect", "--format=json", image],
            timeout_seconds=ENGINE_PHASE_TIMEOUT_SECONDS,
            maximum_output_bytes=MAX_ENGINE_OUTPUT_BYTES,
            environment=environment,
            cancel_event=cancel_event,
        ),
        "pinned image inspection",
    )
    images = _parse_json(inspected.stdout, "pinned image document")
    if not isinstance(images, list) or len(images) != 1 or not isinstance(images[0], dict):
        raise _fail("ASEGS011", "The pinned subject image inspection was ambiguous.")
    item = images[0]
    config = item.get("Config")
    digests = item.get("RepoDigests")
    if not isinstance(config, dict) or not isinstance(digests, list) or image not in digests:
        raise _fail("ASEGS011", "The local image does not prove the exact controller-pinned digest.")
    declared_volumes = config.get("Volumes")
    if declared_volumes not in (None, {}):
        raise _fail("ASEGS011", "The pinned subject image declares writable or anonymous volumes.")


def _cpu_argument(cpu_millis: int) -> str:
    whole, fraction = divmod(cpu_millis, 1000)
    return f"{whole}.{fraction:03d}"


def _container_create_arguments(
    engine: str,
    *,
    name: str,
    image: str,
    owner_token: str,
    subject_root: Path,
    source_diff: Path,
    scratch_mountpoint: Path,
    profile_arguments: Sequence[str],
    limits: SubjectLimits,
) -> list[str]:
    subject_mount = f"type=bind,src={subject_root},dst=/subject,ro=true,bind-propagation=rprivate"
    diff_mount = f"type=bind,src={source_diff},dst={SOURCE_DIFF_CONTAINER_PATH},ro=true,bind-propagation=rprivate"
    scratch_mount = (
        f"type=bind,src={scratch_mountpoint},dst=/scratch,rw=true,bind-propagation=rprivate"
    )
    arguments = [
        engine,
        "--remote=false",
        "create",
        "--pull=never",
        "--http-proxy=false",
        f"--name={name}",
        f"--label={SUPERVISOR_OWNER_LABEL}={owner_token}",
        "--network=none",
        "--pid=private",
        "--ipc=private",
        "--uts=private",
        "--cgroupns=private",
        "--read-only",
        "--read-only-tmpfs=false",
        "--image-volume=ignore",
        "--userns=keep-id:uid=65532,gid=65532",
        "--user=65532:65532",
        "--cap-drop=ALL",
        "--security-opt=no-new-privileges",
        f"--pids-limit={limits.pids}",
        f"--memory={limits.memory_mib}m",
        f"--cpus={_cpu_argument(limits.cpu_millis)}",
        "--mount",
        subject_mount,
        "--mount",
        diff_mount,
        "--mount",
        scratch_mount,
        "--unsetenv-all",
    ]
    arguments.extend(f"--env={value}" for value in CONTAINER_ENVIRONMENT)
    arguments.extend(
        [
            "--workdir=/subject",
            f"--entrypoint={CONTAINER_ENTRYPOINT}",
            image,
            *profile_arguments,
        ]
    )
    return arguments


def _one_inspect_object(data: bytes) -> Mapping[str, Any]:
    inspected = _parse_json(data, "container configuration")
    if not isinstance(inspected, list) or len(inspected) != 1 or not isinstance(inspected[0], dict):
        raise _fail("ASEGS012", "The created container configuration was ambiguous.")
    return inspected[0]


def _decode_mountinfo_field(value: str) -> str:
    return re.sub(r"\\([0-7]{3})", lambda match: chr(int(match.group(1), 8)), value)


def _host_scratch_mount_is_present(mountpoint: Path) -> bool:
    """Detect a partial mount after a failed mount command so cleanup can still remove it."""
    try:
        with MOUNTINFO_PATH.open(encoding="utf-8") as mountinfo:
            for line in mountinfo:
                left, separator, _ = line.rstrip("\n").partition(" - ")
                fields = left.split()
                if not separator or len(fields) < 6:
                    raise ValueError("malformed mountinfo record")
                if _decode_mountinfo_field(fields[4]) == str(mountpoint):
                    return True
        return False
    except (OSError, UnicodeError, ValueError, OverflowError):
        raise _fail("ASEGS019", "The host scratch mount state could not be inspected safely.") from None


def _size_option_bytes(value: str) -> int | None:
    match = re.fullmatch(r"([0-9]+)([kKmMgGtT]?)", value)
    if match is None:
        return None
    scales = {"": 1, "k": 1024, "m": 1024**2, "g": 1024**3, "t": 1024**4}
    return int(match.group(1)) * scales[match.group(2).lower()]


def _verify_host_scratch_mount(mountpoint: Path, *, host_uid: int, host_gid: int) -> None:
    """Require the live host mount and statvfs limits to match the fixed contract."""
    try:
        matching_mounts: list[tuple[str, str, list[str], list[str]]] = []
        with MOUNTINFO_PATH.open(encoding="utf-8") as mountinfo:
            for line in mountinfo:
                left, separator, right = line.rstrip("\n").partition(" - ")
                fields = left.split()
                if not separator or len(fields) < 6:
                    raise ValueError("malformed mountinfo record")
                if _decode_mountinfo_field(fields[4]) != str(mountpoint):
                    continue
                filesystem_fields = right.split()
                if len(filesystem_fields) < 3:
                    raise ValueError("malformed mountinfo filesystem fields")
                matching_mounts.append(
                    (
                        filesystem_fields[0],
                        filesystem_fields[1],
                        fields[5].split(","),
                        filesystem_fields[2].split(","),
                    )
                )
        if len(matching_mounts) != 1:
            raise ValueError("the scratch mount is missing or ambiguous")

        filesystem_type, source, mount_options, super_options = matching_mounts[0]
        option_flags = set(mount_options + super_options)
        option_values: dict[str, str] = {}
        for option in super_options:
            key, separator, value = option.partition("=")
            if separator:
                if key in option_values:
                    raise ValueError("duplicate tmpfs option")
                option_values[key] = value

        mount_stat = mountpoint.stat()
        filesystem_stat = os.statvfs(mountpoint)
        mode_value = int(option_values.get("mode", "-1"), 8)
        actual_bytes = filesystem_stat.f_blocks * filesystem_stat.f_frsize
        if (
            filesystem_type != "tmpfs"
            or source != "tmpfs"
            or not {"rw", "nosuid", "nodev"}.issubset(option_flags)
            or {"ro", "suid", "dev", "noswap"}.intersection(option_flags)
            or _size_option_bytes(option_values.get("size", "")) != MAX_PROFILE_SCRATCH_BYTES
            or option_values.get("nr_inodes") != str(MAX_PROFILE_SCRATCH_INODES)
            or mode_value != 0o700
            or option_values.get("uid") != str(host_uid)
            or option_values.get("gid") != str(host_gid)
            or not stat.S_ISDIR(mount_stat.st_mode)
            or stat.S_IMODE(mount_stat.st_mode) != 0o700
            or mount_stat.st_uid != host_uid
            or mount_stat.st_gid != host_gid
            or actual_bytes != MAX_PROFILE_SCRATCH_BYTES
            or filesystem_stat.f_files != MAX_PROFILE_SCRATCH_INODES
        ):
            raise ValueError("the live tmpfs does not match the fixed quota and identity")
    except (OSError, UnicodeError, ValueError, OverflowError):
        raise _fail("ASEGS019", "The live quota-limited host scratch mount could not be verified.") from None


def _verify_container_configuration(
    data: bytes,
    *,
    subject_root: Path,
    source_diff: Path,
    scratch_mountpoint: Path,
    limits: SubjectLimits,
    profile_arguments: Sequence[str],
    owner_token: str,
) -> None:
    container = _one_inspect_object(data)
    config = container.get("Config")
    host = container.get("HostConfig")
    mounts = container.get("Mounts")
    if not isinstance(config, dict) or not isinstance(host, dict) or not isinstance(mounts, list):
        raise _fail("ASEGS012", "The created container omitted required isolation settings.")

    expected_entrypoint = [CONTAINER_ENTRYPOINT]
    expected_command = list(profile_arguments)
    if config.get("Entrypoint") != expected_entrypoint or config.get("Cmd") != expected_command:
        raise _fail("ASEGS012", "The created container command differs from the fixed trusted profile command.")
    if config.get("User") != f"{CONTAINER_UID}:{CONTAINER_GID}" or config.get("WorkingDir") != "/subject":
        raise _fail("ASEGS012", "The created container is not using its fixed non-root identity and work directory.")
    if config.get("Env") != list(CONTAINER_ENVIRONMENT):
        raise _fail("ASEGS012", "The created container environment differs from the fixed credentialless allowlist.")
    labels = config.get("Labels")
    if not isinstance(labels, dict) or labels.get(SUPERVISOR_OWNER_LABEL) != owner_token:
        raise _fail("ASEGS012", "The created container does not carry this run's cleanup ownership label.")
    if host.get("ReadonlyRootfs") is not True:
        raise _fail("ASEGS012", "The created container root filesystem is not read-only.")
    cap_drop = host.get("CapDrop")
    security_options = host.get("SecurityOpt")
    if not isinstance(cap_drop, list) or not all(isinstance(value, str) for value in cap_drop):
        raise _fail("ASEGS012", "The created container omitted capability-drop verification.")
    if not isinstance(security_options, list) or not all(isinstance(value, str) for value in security_options):
        raise _fail("ASEGS012", "The created container omitted no-new-privileges verification.")
    # Rootless Podman 4.9 expands ALL into its default capability names in
    # inspect, while other versions may retain the literal request.
    dropped = set(cap_drop)
    if (
        not (cap_drop == ["ALL"] or (
            len(dropped) == len(cap_drop) and PODMAN_DEFAULT_CAPABILITIES.issubset(dropped)
        ))
    ) or not {
        "no-new-privileges",
        "no-new-privileges:true",
    }.intersection(set(security_options)):
        raise _fail("ASEGS012", "The created container did not drop every capability and enable no-new-privileges.")
    if len(security_options) != 1 or host.get("CapAdd") not in (None, []):
        raise _fail("ASEGS012", "The created container has an additional security option or added capability.")
    if host.get("NetworkMode") != "none":
        raise _fail("ASEGS012", "The created container has network access.")
    if (
        host.get("PidsLimit") != limits.pids
        or host.get("Memory") != limits.memory_mib * 1024 * 1024
        or host.get("NanoCpus") != limits.cpu_millis * 1_000_000
    ):
        raise _fail("ASEGS012", "The created container resource limits do not match the validated profile limits.")
    devices = host.get("Devices")
    if not isinstance(devices, list):
        raise _fail("ASEGS012", "The created container omitted device access verification.")
    if host.get("Privileged") is not False or devices:
        raise _fail("ASEGS012", "The created container has a privileged or device capability.")
    if host.get("Tmpfs") not in (None, {}):
        raise _fail("ASEGS012", "The created container has an undeclared tmpfs mount.")
    if (
        host.get("PortBindings") not in (None, {})
        or host.get("PidMode") != "private"
        or host.get("IpcMode") != "private"
        or host.get("UTSMode") != "private"
        or host.get("CgroupnsMode") not in (None, "private")
        or host.get("UsernsMode") != "private"
    ):
        raise _fail("ASEGS012", "The created container shares a host namespace or publishes a port.")

    if len(mounts) != 3:
        raise _fail("ASEGS012", "The created container has an unexpected host mount.")
    by_destination = {mount.get("Destination"): mount for mount in mounts if isinstance(mount, dict)}
    if set(by_destination) != {"/subject", SOURCE_DIFF_CONTAINER_PATH, "/scratch"}:
        raise _fail("ASEGS012", "The created container has an unexpected host mount.")
    subject_mount = by_destination["/subject"]
    diff_mount = by_destination[SOURCE_DIFF_CONTAINER_PATH]
    scratch_mount = by_destination["/scratch"]
    if (
        subject_mount.get("Type") != "bind"
        or Path(str(subject_mount.get("Source"))).resolve() != subject_root
        or subject_mount.get("RW") is not False
        or subject_mount.get("Propagation") != "rprivate"
        or diff_mount.get("Type") != "bind"
        or Path(str(diff_mount.get("Source"))).resolve() != source_diff
        or diff_mount.get("RW") is not False
        or diff_mount.get("Propagation") != "rprivate"
        or scratch_mount.get("Type") != "bind"
        or Path(str(scratch_mount.get("Source"))).resolve() != scratch_mountpoint
        or scratch_mount.get("RW") is not True
        or scratch_mount.get("Propagation") != "rprivate"
    ):
        raise _fail(
            "ASEGS012", "Subject, source diff, and scratch bind mounts do not match the read-only/private contract."
        )


def _validate_subject_result_content(content: bytes, *, expected_status: str) -> Mapping[str, Any]:
    if not content or len(content) > MAX_SUBJECT_RESULT_BYTES:
        raise _fail("ASEGS018", "The exported subject result is not bounded; no claim may be issued.")
    try:
        value = _parse_json(content, "bounded subject result")
    except SubjectLauncherError:
        raise _fail("ASEGS018", "The exported subject result is malformed; no claim may be issued.") from None
    if not isinstance(value, dict):
        raise _fail("ASEGS018", "The exported subject result does not match the fixed non-claiming record.")
    status = value.get("status")
    expected_keys = {"claimEligible", "profileId", "schemaVersion", "status", "steps"}
    if status == "failed":
        expected_keys.add("diagnostic")
    if (
        set(value) != expected_keys
        or value.get("claimEligible") is not False
        or value.get("profileId") != "code-coverage"
        or type(value.get("schemaVersion")) is not int
        or value.get("schemaVersion") != 1
        or not isinstance(status, str)
        or status != expected_status
        or status not in {"completed", "failed"}
        or not isinstance(value.get("steps"), list)
    ):
        raise _fail("ASEGS018", "The exported subject result does not match the fixed completed non-claiming record.")
    steps = value["steps"]
    if len(steps) > len(SUBJECT_RESULT_STEP_NAMES):
        raise _fail("ASEGS018", "The exported subject result contains too many fixed execution steps.")
    total_output_bytes = 0
    for index, step in enumerate(steps):
        if (
            not isinstance(step, dict)
            or set(step) != {"exitCode", "name", "outputBytes", "stderrSha256", "stdoutSha256"}
            or index >= len(SUBJECT_RESULT_STEP_NAMES)
            or step.get("name") != SUBJECT_RESULT_STEP_NAMES[index]
            or type(step.get("exitCode")) is not int
            or type(step.get("outputBytes")) is not int
            or step["outputBytes"] < 0
            or not isinstance(step.get("stdoutSha256"), str)
            or SUBJECT_STEP_DIGEST_PATTERN.fullmatch(step["stdoutSha256"]) is None
            or not isinstance(step.get("stderrSha256"), str)
            or SUBJECT_STEP_DIGEST_PATTERN.fullmatch(step["stderrSha256"]) is None
        ):
            raise _fail("ASEGS018", "The exported subject result contains malformed or unexpected step proof.")
        total_output_bytes += step["outputBytes"]
        if total_output_bytes > MAX_PROFILE_OUTPUT_BYTES:
            raise _fail("ASEGS018", "The exported subject step proof exceeds its fixed output budget.")
        if index + 1 < len(steps) and step["exitCode"] != 0:
            raise _fail("ASEGS018", "A subject execution step failed before later steps were recorded.")
    if status == "completed" and (
        tuple(step["name"] for step in steps) != SUBJECT_RESULT_STEP_NAMES
        or any(step["exitCode"] != 0 for step in steps)
    ):
        raise _fail("ASEGS018", "The completed subject result omitted a required successful execution step.")
    if status == "failed":
        diagnostic = value.get("diagnostic")
        if (
            not isinstance(diagnostic, dict)
            or set(diagnostic) != {"code", "message"}
            or not isinstance(diagnostic.get("code"), str)
            or not isinstance(diagnostic.get("message"), str)
        ):
            raise _fail("ASEGS018", "The failed subject result omitted its typed diagnostic.")
    try:
        canonical = (json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii")
    except (TypeError, ValueError, RecursionError):
        raise _fail("ASEGS018", "The exported subject result could not be canonicalized safely.") from None
    if content != canonical:
        raise _fail("ASEGS018", "The exported subject result is not canonical; no claim may be issued.")
    return value


def _write_subject_result_export(scratch: Path, content: bytes) -> None:
    result_path = scratch / SUBJECT_RESULT_RELATIVE_PATH
    descriptor = -1
    created = False
    try:
        descriptor = os.open(
            result_path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        created = True
        with os.fdopen(descriptor, "wb") as output:
            descriptor = -1
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
    except OSError:
        if descriptor >= 0:
            try:
                os.close(descriptor)
            except OSError:
                pass
        if created:
            try:
                result_path.unlink()
            except OSError:
                pass
        raise _fail("ASEGS018", "The bounded subject result could not be saved safely; no claim may be issued.") from None


def _export_subject_result(scratch: Path, content: bytes, *, expected_status: str) -> Mapping[str, Any]:
    value = _validate_subject_result_content(content, expected_status=expected_status)
    _write_subject_result_export(scratch, content)
    return value


def _check_cancel(cancel_event: threading.Event) -> None:
    if cancel_event.is_set():
        raise _fail("ASEGS013", "Subject execution was cancelled.")


def _raise_process_error(error: Exception, stage: str) -> None:
    if isinstance(error, _ProcessCancelled):
        raise _fail("ASEGS013", f"Subject execution was cancelled during {stage}.") from None
    if isinstance(error, _ProcessTimedOut):
        raise _fail("ASEGS014", f"The bounded {stage} deadline expired.") from None
    if isinstance(error, _ProcessOutputExceeded):
        raise _fail("ASEGS015", f"The bounded {stage} output limit was exceeded.") from None
    if isinstance(error, _ProcessCouldNotStop):
        raise _fail("ASEGS016", f"The {stage} process could not be stopped safely.") from None
    if isinstance(error, _ProcessStartFailed):
        raise _fail("ASEGS008", f"The trusted process could not start during {stage}.") from None
    raise error


def launch_subject(
    *,
    subject_checkout: str | os.PathLike[str],
    image_digest: str,
    scratch_directory: str | os.PathLike[str],
    profile_id: str,
    source_diff: str | os.PathLike[str],
    source_diff_sha256: str,
    limits: SubjectLimits,
    cancel_event: threading.Event | None = None,
    _environment: Mapping[str, str] | None = None,
    _system_name: str | None = None,
    _effective_uid: int | None = None,
    _engine_path: str | None = None,
    _host_mount_verifier: Callable[..., None] | None = None,
    _command_executor: CommandExecutor = _run_command,
    supervisor_client: CleanupSupervisorClient | None = None,
) -> SubjectRunResult:
    """Run a fixed profile with host-mounted quota-limited scratch and export its result record."""
    _validate_limits(limits)
    profile_arguments = _validate_profile(profile_id)
    diff_path = _validate_source_diff(source_diff, source_diff_sha256)
    profile_arguments = (*profile_arguments, f"--diff-sha256={source_diff_sha256}")
    _validate_image_reference(image_digest)
    subject_root = _validate_subject_root(subject_checkout)
    env = dict(os.environ if _environment is None else _environment)
    system_name = platform.system() if _system_name is None else _system_name
    effective_uid = os.geteuid() if _effective_uid is None else _effective_uid
    runner_temp, home, runtime = _validate_runner_context(
        env,
        system_name=system_name,
        effective_uid=effective_uid,
    )
    scratch = _validate_scratch_path(scratch_directory, runner_temp)
    if scratch == subject_root or scratch in subject_root.parents or subject_root in scratch.parents:
        raise _fail("ASEGS002", "The subject checkout and scratch directory must be disjoint.")
    if (
        diff_path == subject_root
        or subject_root in diff_path.parents
        or diff_path == scratch
        or scratch in diff_path.parents
    ):
        raise _fail(
            "ASEGS020", "The controller source diff must be outside the subject checkout and scratch directory."
        )

    event = cancel_event if cancel_event is not None else threading.Event()
    host_gid = os.getegid()
    _check_cancel(event)
    engine = _resolve_podman() if _engine_path is None else _engine_path
    # Only the trusted engine receives host paths. No GitHub token, workflow
    # variable, subject environment, or socket endpoint is inherited.
    engine_environment = {
        "PATH": ENGINE_PATH,
        "HOME": str(home),
        "XDG_RUNTIME_DIR": str(runtime),
        "TMPDIR": str(runner_temp),
        "REGISTRY_AUTH_FILE": "/dev/null",
    }
    try:
        _preflight_rootless_engine(
            engine,
            environment=engine_environment,
            cancel_event=event,
            executor=_command_executor,
        )
        _check_cancel(event)
        _verify_local_image(
            engine,
            image_digest,
            environment=engine_environment,
            cancel_event=event,
            executor=_command_executor,
        )
    except SubjectLauncherError:
        raise
    except (_ProcessCancelled, _ProcessTimedOut, _ProcessOutputExceeded, _ProcessStartFailed, _ProcessCouldNotStop) as exc:
        _raise_process_error(exc, "OCI preflight")

    try:
        os.mkdir(scratch, 0o700)
        os.chmod(scratch, 0o700)
    except OSError:
        raise _fail("ASEGS002", "The new scratch directory could not be created safely.") from None

    scratch_stat = scratch.stat()
    if not stat.S_ISDIR(scratch_stat.st_mode) or stat.S_IMODE(scratch_stat.st_mode) != 0o700:
        raise _fail("ASEGS002", "The new scratch directory permissions are unsafe.")

    scratch_mountpoint = scratch / f"quota-limited-scratch-{secrets.token_hex(8)}"
    try:
        scratch_mountpoint.mkdir(mode=0o700)
        os.chmod(scratch_mountpoint, 0o700)
    except OSError:
        raise _fail("ASEGS002", "The private scratch mountpoint could not be created safely.") from None

    run_id = env["GITHUB_RUN_ID"]
    attempt = env["GITHUB_RUN_ATTEMPT"]
    container_name = f"ase-subject-{run_id}-{attempt}-{secrets.token_hex(6)}"
    supervisor = supervisor_client or SubjectCleanupSupervisorClient(
        runner_temp=runner_temp,
        home=home,
        runtime=runtime,
        executor=_command_executor,
    )
    try:
        supervisor_session = supervisor.start(
            container_name=container_name,
            scratch_directory=scratch,
            mountpoint=scratch_mountpoint,
            runner_temp=runner_temp,
            home=home,
            runtime=runtime,
            parent_pid=os.getpid(),
        )
        if not isinstance(supervisor_session, SupervisorSession) or supervisor_session.ready is not True:
            raise _fail("ASEGS017", "The cleanup supervisor did not complete its readiness handshake.")
        owner_token = supervisor_session.owner_token
        if (
            not isinstance(owner_token, str)
            or SUPERVISOR_TOKEN_PATTERN.fullmatch(owner_token) is None
            or supervisor_session.owner_label != SUPERVISOR_OWNER_LABEL
        ):
            raise _fail("ASEGS017", "The cleanup supervisor returned an invalid ownership token.")
    except SubjectLauncherError:
        for directory in (scratch_mountpoint, scratch):
            try:
                directory.rmdir()
            except OSError:
                pass
        raise
    except Exception:
        for directory in (scratch_mountpoint, scratch):
            try:
                directory.rmdir()
            except OSError:
                pass
        raise _fail("ASEGS017", "The cleanup supervisor failed before subject setup.") from None
    primary_error: BaseException | None = None
    result: SubjectRunResult | None = None
    try:
        _check_cancel(event)
        supervisor.assert_alive(supervisor_session)
        mount_options = (
            "rw,nosuid,nodev,"
            f"size={MAX_PROFILE_SCRATCH_BYTES},"
            f"nr_inodes={MAX_PROFILE_SCRATCH_INODES},"
            f"mode=0700,uid={effective_uid},gid={host_gid}"
        )
        mount_result = _command_executor(
            [
                SUDO_PATH,
                "-n",
                "mount",
                "-t",
                "tmpfs",
                "-o",
                mount_options,
                "tmpfs",
                str(scratch_mountpoint),
            ],
            timeout_seconds=MOUNT_PHASE_TIMEOUT_SECONDS,
            maximum_output_bytes=4096,
            environment=engine_environment,
            cancel_event=event,
        )
        if mount_result.returncode != 0:
            raise _fail("ASEGS019", "The quota-limited host scratch mount failed.")
        mount_verifier = _verify_host_scratch_mount if _host_mount_verifier is None else _host_mount_verifier
        mount_verifier(scratch_mountpoint, host_uid=effective_uid, host_gid=host_gid)
        _check_cancel(event)
        supervisor.assert_alive(supervisor_session)
        _expect_success(
            _command_executor(
                _container_create_arguments(
                    engine,
                    name=container_name,
                    image=image_digest,
                    owner_token=owner_token,
                    subject_root=subject_root,
                    source_diff=diff_path,
                    scratch_mountpoint=scratch_mountpoint,
                    profile_arguments=profile_arguments,
                    limits=limits,
                ),
                timeout_seconds=ENGINE_PHASE_TIMEOUT_SECONDS,
                maximum_output_bytes=4096,
                environment=engine_environment,
                cancel_event=event,
            ),
            "container creation",
        )
        _check_cancel(event)
        inspect_result = _expect_success(
            _command_executor(
                [engine, "--remote=false", "inspect", "--format=json", container_name],
                timeout_seconds=ENGINE_PHASE_TIMEOUT_SECONDS,
                maximum_output_bytes=MAX_ENGINE_OUTPUT_BYTES,
                environment=engine_environment,
                cancel_event=event,
            ),
            "container isolation verification",
        )
        _verify_container_configuration(
            inspect_result.stdout,
            subject_root=subject_root,
            source_diff=diff_path,
            scratch_mountpoint=scratch_mountpoint,
            limits=limits,
            profile_arguments=profile_arguments,
            owner_token=owner_token,
        )
        _check_cancel(event)
        started = _command_executor(
            [engine, "--remote=false", "start", "--attach", container_name],
            timeout_seconds=limits.timeout_seconds,
            maximum_output_bytes=limits.output_bytes + MAX_SUBJECT_RESULT_BYTES,
            environment=engine_environment,
            cancel_event=event,
        )
        if len(started.stderr) > limits.output_bytes:
            raise _fail("ASEGS015", "The bounded subject execution output limit was exceeded.")
        execution_record: Mapping[str, Any] | None = None
        if started.stdout:
            execution_record = _export_subject_result(
                scratch,
                started.stdout,
                expected_status="completed" if started.returncode == 0 else "failed",
            )
        elif started.returncode == 0:
            raise _fail("ASEGS018", "The completed subject omitted its bounded non-claiming result record.")
        result = SubjectRunResult(
            exit_code=started.returncode,
            stdout=b"",
            stderr=started.stderr,
            scratch_directory=scratch,
            execution_record=execution_record,
        )
    except BaseException as exc:
        primary_error = exc
    try:
        supervisor_record = supervisor.request_and_wait(supervisor_session)
        SubjectCleanupSupervisorClient._validate_attestation(supervisor_record)
    except SubjectLauncherError:
        raise
    except Exception:
        raise _fail("ASEGS017", "The cleanup supervisor did not attest complete cleanup.") from None
    if primary_error is not None:
        if isinstance(primary_error, SubjectLauncherError):
            raise primary_error
        if isinstance(primary_error, (_ProcessCancelled, _ProcessTimedOut, _ProcessOutputExceeded, _ProcessStartFailed, _ProcessCouldNotStop)):
            _raise_process_error(primary_error, "subject execution")
        if isinstance(primary_error, KeyboardInterrupt):
            raise primary_error
        raise _fail("ASEGS008", "The trusted OCI execution phase failed.") from None
    if result is None:
        raise _fail("ASEGS008", "The trusted OCI execution phase returned no process result.")
    return result


def _positive_cli_integer(value: str) -> int:
    if not value.isascii() or not value.isdecimal() or value.startswith("0"):
        raise argparse.ArgumentTypeError("must be a positive decimal integer")
    try:
        return int(value, 10)
    except ValueError:
        raise argparse.ArgumentTypeError("must be a positive decimal integer") from None


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--subject-checkout", required=True)
    parser.add_argument("--image-digest", required=True)
    parser.add_argument("--scratch-directory", required=True)
    parser.add_argument("--profile-id", required=True)
    parser.add_argument("--source-diff", required=True)
    parser.add_argument("--source-diff-sha256", required=True)
    parser.add_argument("--timeout-seconds", required=True, type=_positive_cli_integer)
    parser.add_argument("--memory-mib", required=True, type=_positive_cli_integer)
    parser.add_argument("--cpu-millis", required=True, type=_positive_cli_integer)
    parser.add_argument("--pids", required=True, type=_positive_cli_integer)
    parser.add_argument("--output-bytes", required=True, type=_positive_cli_integer)
    args = parser.parse_args(argv)
    try:
        result = launch_subject(
            subject_checkout=args.subject_checkout,
            image_digest=args.image_digest,
            scratch_directory=args.scratch_directory,
            profile_id=args.profile_id,
            source_diff=args.source_diff,
            source_diff_sha256=args.source_diff_sha256,
            limits=SubjectLimits(
                timeout_seconds=args.timeout_seconds,
                memory_mib=args.memory_mib,
                cpu_millis=args.cpu_millis,
                pids=args.pids,
                output_bytes=args.output_bytes,
            ),
        )
    except SubjectLauncherError as exc:
        print(f"{exc.code}: {exc.message}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("ASEGS013: Subject execution was cancelled.", file=sys.stderr)
        return 130
    print(
        json.dumps(
            {
                "claimEligible": False,
                "exitCode": result.exit_code,
                "outputBytes": len(result.stdout) + len(result.stderr),
                "profileId": args.profile_id,
            },
            separators=(",", ":"),
            sort_keys=True,
        )
    )
    # The standalone command is deliberately non-green: only a later trusted
    # verifier may convert this non-claiming execution into a CI verdict.
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
