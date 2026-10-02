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

The caller owns the new scratch directory after return and must treat every
byte written there and all process output as untrusted.  Even exit code zero
does not create complete Evidence or authorize a CI verdict.

Normal completion and handled failures remove the container before unmounting
the private host tmpfs. This process-local cleanup cannot run after SIGKILL or
VM loss; an independent supervisor is still required before this primitive can
support an eligible claim.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
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
from typing import Any, Callable, Mapping, Sequence


ENGINE_PATH = "/usr/bin:/usr/local/bin:/bin"
SUDO_PATH = "/usr/bin/sudo"
MOUNTINFO_PATH = Path("/proc/self/mountinfo")
CONTAINER_ENTRYPOINT = "/usr/local/libexec/appsurface-subject-runner"
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
MIN_OUTPUT_BYTES = 4096
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


CommandExecutor = Callable[..., CommandResult]


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
    subject_root: Path,
    scratch_mountpoint: Path,
    profile_arguments: Sequence[str],
    limits: SubjectLimits,
) -> list[str]:
    subject_mount = f"type=bind,src={subject_root},dst=/subject,ro=true,bind-propagation=rprivate"
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
    scratch_mountpoint: Path,
    limits: SubjectLimits,
    profile_arguments: Sequence[str],
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

    if len(mounts) != 2:
        raise _fail("ASEGS012", "The created container has an unexpected host mount.")
    by_destination = {mount.get("Destination"): mount for mount in mounts if isinstance(mount, dict)}
    if set(by_destination) != {"/subject", "/scratch"}:
        raise _fail("ASEGS012", "The created container has an unexpected host mount.")
    subject_mount = by_destination["/subject"]
    scratch_mount = by_destination["/scratch"]
    if (
        subject_mount.get("Type") != "bind"
        or Path(str(subject_mount.get("Source"))).resolve() != subject_root
        or subject_mount.get("RW") is not False
        or subject_mount.get("Propagation") != "rprivate"
        or scratch_mount.get("Type") != "bind"
        or Path(str(scratch_mount.get("Source"))).resolve() != scratch_mountpoint
        or scratch_mount.get("RW") is not True
        or scratch_mount.get("Propagation") != "rprivate"
    ):
        raise _fail("ASEGS012", "Subject and scratch bind mount access does not match the read-only/private contract.")


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
    limits: SubjectLimits,
    cancel_event: threading.Event | None = None,
    _environment: Mapping[str, str] | None = None,
    _system_name: str | None = None,
    _effective_uid: int | None = None,
    _engine_path: str | None = None,
    _host_mount_verifier: Callable[..., None] | None = None,
    _command_executor: CommandExecutor = _run_command,
) -> SubjectRunResult:
    """Run a fixed profile with host-mounted quota-limited scratch and export its result record."""
    _validate_limits(limits)
    profile_arguments = _validate_profile(profile_id)
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
    create_attempted = False
    mount_attempted = False
    mount_cleanup_required = False
    primary_error: BaseException | None = None
    result: SubjectRunResult | None = None
    try:
        _check_cancel(event)
        mount_attempted = True
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
        mount_cleanup_required = True
        mount_verifier = _verify_host_scratch_mount if _host_mount_verifier is None else _host_mount_verifier
        mount_verifier(scratch_mountpoint, host_uid=effective_uid, host_gid=host_gid)
        _check_cancel(event)
        create_attempted = True
        _expect_success(
            _command_executor(
                _container_create_arguments(
                    engine,
                    name=container_name,
                    image=image_digest,
                    subject_root=subject_root,
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
            scratch_mountpoint=scratch_mountpoint,
            limits=limits,
            profile_arguments=profile_arguments,
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
        if mount_attempted and not mount_cleanup_required:
            try:
                mount_cleanup_required = _host_scratch_mount_is_present(scratch_mountpoint)
            except BaseException:
                # An unreadable mount table cannot prove that the failed mount
                # left no mount behind, so make a best-effort unmount attempt.
                mount_cleanup_required = True

    cleanup_error: BaseException | None = None
    if create_attempted:
        try:
            cleanup_result = _command_executor(
                [engine, "--remote=false", "rm", "--force", "--ignore", container_name],
                timeout_seconds=CLEANUP_TIMEOUT_SECONDS,
                maximum_output_bytes=4096,
                environment=engine_environment,
                cancel_event=threading.Event(),
            )
            if cleanup_result.returncode != 0:
                cleanup_error = _fail("ASEGS017", "The subject container cleanup failed; no claim may be issued.")
        except BaseException as exc:
            cleanup_error = exc

    mount_unmounted = not mount_cleanup_required
    # A failed container removal may leave a running bind-mount user. Preserve
    # its host mount for external cleanup rather than detaching it underneath
    # that process.
    if mount_cleanup_required and (not create_attempted or cleanup_error is None):
        try:
            unmount_result = _command_executor(
                [SUDO_PATH, "-n", "umount", "--", str(scratch_mountpoint)],
                timeout_seconds=CLEANUP_TIMEOUT_SECONDS,
                maximum_output_bytes=4096,
                environment=engine_environment,
                cancel_event=threading.Event(),
            )
            if unmount_result.returncode == 0:
                mount_unmounted = True
            elif cleanup_error is None:
                cleanup_error = _fail("ASEGS017", "The host scratch tmpfs cleanup failed; no claim may be issued.")
        except BaseException as exc:
            if cleanup_error is None:
                cleanup_error = exc

    if mount_unmounted and cleanup_error is None:
        try:
            scratch_mountpoint.rmdir()
        except OSError as exc:
            if cleanup_error is None:
                cleanup_error = exc

    if cleanup_error is not None:
        raise _fail("ASEGS017", "The subject container or host scratch cleanup failed; no claim may be issued.") from None
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
