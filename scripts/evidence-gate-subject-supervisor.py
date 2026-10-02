#!/usr/bin/env python3
"""Independently reap one fixed AppSurface OCI subject run.

The launcher starts this process before creating its container.  ``start``
creates a private run directory and manifest, then starts a detached supervisor
which watches the launcher's PID and Linux process start time.  On normal
completion the launcher invokes ``request``; if it is killed, the supervisor
detects that the exact parent process has exited.  The supervisor has no shell
or caller-supplied command support.  It removes only a container bearing this
run's random ownership label and exact scratch bind, then unmounts only the
matching fixed-quota tmpfs.  It writes a small, non-claiming attestation outside
the tmpfs.

This helper does not establish a complete isolation envelope or an eligible
Evidence claim.  Its detached process is independent of launcher SIGKILL, but
cannot survive destruction of the runner VM or an external job cgroup kill.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import signal
import stat
import subprocess
import sys
import time
from typing import Any, Callable, Mapping, Sequence


PODMAN_PATH = "/usr/bin/podman"
SUDO_PATH = "/usr/bin/sudo"
ENGINE_PATH = "/usr/bin:/usr/local/bin:/bin"
MOUNTINFO_PATH = Path("/proc/self/mountinfo")
PROC_ROOT = Path("/proc")
TOKEN_PATTERN = re.compile(r"[0-9a-f]{32}\Z")
RUN_DIRECTORY_PATTERN = re.compile(r"appsurface-subject-supervisor-([0-9a-f]{32})\Z")
CONTAINER_PATTERN = re.compile(r"ase-subject-([1-9][0-9]{0,18})-([1-9][0-9]{0,8})-([0-9a-f]{12})\Z")
MOUNTPOINT_PATTERN = re.compile(r"quota-limited-scratch-[0-9a-f]{16}\Z")
OWNER_LABEL = "io.forge-trust.appsurface.cleanup-token"
MANIFEST_NAME = "supervisor.json"
REQUEST_NAME = "cleanup-request.json"
ATTESTATION_NAME = "cleanup-attestation.json"
SUBJECT_RESULT_NAME = "evidence-subject-result.json"
MAX_SUBJECT_RESULT_BYTES = 16 * 1024
MAX_MANIFEST_BYTES = 4096
MAX_REQUEST_BYTES = 512
MAX_ATTESTATION_BYTES = 4096
MAX_COMMAND_OUTPUT_BYTES = 256 * 1024
MAX_COMMAND_SECONDS = 30
SUPERVISOR_STARTUP_SECONDS = 10
MAX_PARENT_LIFETIME_SECONDS = 30 * 60
CLEANUP_DEADLINE_SECONDS = 10 * 60
PARENT_POLL_SECONDS = 0.2
TMPFS_BYTES = 4 * 1024**3
TMPFS_INODES = 262_144


class SupervisorError(Exception):
    """A fixed, safe diagnostic for malformed supervisor input."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


class CommandError(Exception):
    """A bounded trusted-command failure identified by a stable code."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


@dataclass(frozen=True)
class CommandResult:
    returncode: int
    stdout: bytes
    stderr: bytes


@dataclass(frozen=True)
class Manifest:
    token: str
    container_name: str
    scratch_directory: Path
    mountpoint: Path
    parent_pid: int
    parent_start_time: int
    scratch_device: int
    scratch_inode: int
    mountpoint_device: int
    mountpoint_inode: int


@dataclass(frozen=True)
class _Mount:
    filesystem: str
    source: str
    mount_options: tuple[str, ...]
    super_options: tuple[str, ...]


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON key")
        value[key] = item
    return value


def _decode_json(data: bytes, *, maximum_bytes: int, code: str) -> Any:
    if not data or len(data) > maximum_bytes:
        raise SupervisorError(code)
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=_unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise SupervisorError(code) from None


def _canonical_path(value: str | os.PathLike[str], code: str) -> Path:
    try:
        raw = os.fspath(value)
    except TypeError:
        raise SupervisorError(code) from None
    if (
        not isinstance(raw, str)
        or not raw
        or "\x00" in raw
        or any(character in raw for character in ",\r\n")
        or not Path(raw).is_absolute()
        or raw != str(Path(raw))
        or any(part in {".", ".."} for part in raw.split("/"))
    ):
        raise SupervisorError(code)
    path = Path(raw)
    current = Path(path.anchor)
    try:
        for part in path.parts[1:]:
            current = current / part
            info = current.lstat()
            if stat.S_ISLNK(info.st_mode):
                raise SupervisorError(code)
        if path.resolve(strict=True) != path:
            raise SupervisorError(code)
    except SupervisorError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise SupervisorError(code) from None
    return path


def _check_directory(path: Path, *, uid: int, mode: int | None, code: str) -> os.stat_result:
    try:
        info = path.lstat()
    except OSError:
        raise SupervisorError(code) from None
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != uid:
        raise SupervisorError(code)
    if mode is not None and stat.S_IMODE(info.st_mode) != mode:
        raise SupervisorError(code)
    return info


def _check_owned_regular_file(path: Path, *, uid: int, maximum_bytes: int, code: str) -> bytes:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(path, flags)
    except OSError:
        raise SupervisorError(code) from None
    try:
        info = os.fstat(fd)
        if (
            not stat.S_ISREG(info.st_mode)
            or info.st_uid != uid
            or stat.S_IMODE(info.st_mode) != 0o600
            or info.st_nlink != 1
            or info.st_size > maximum_bytes
        ):
            raise SupervisorError(code)
        data = bytearray()
        while len(data) <= maximum_bytes:
            chunk = os.read(fd, min(4096, maximum_bytes + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        if len(data) > maximum_bytes:
            raise SupervisorError(code)
        return bytes(data)
    finally:
        os.close(fd)


def _validate_context(environment: Mapping[str, str], *, uid: int) -> tuple[Path, Path, Path]:
    runner_temp_value = environment.get("RUNNER_TEMP", "")
    home_value = environment.get("HOME", "")
    runtime_value = environment.get("XDG_RUNTIME_DIR") or f"/run/user/{uid}"
    runner_temp = _canonical_path(runner_temp_value, "invalid-runner-temp")
    home = _canonical_path(home_value, "invalid-home")
    runtime = _canonical_path(runtime_value, "invalid-runtime-directory")
    for path, mode in ((runner_temp, None), (home, None), (runtime, None)):
        _check_directory(path, uid=uid, mode=mode, code="invalid-runner-context")
    if stat.S_IMODE(runner_temp.stat().st_mode) & 0o022:
        raise SupervisorError("invalid-runner-temp")
    runtime_info = runtime.stat()
    if stat.S_IMODE(runtime_info.st_mode) & 0o077:
        raise SupervisorError("invalid-runtime-directory")
    return runner_temp, home, runtime


def _validate_manifest_path(value: str | os.PathLike[str], runner_temp: Path, uid: int) -> tuple[Path, Path]:
    manifest_path = _canonical_path(value, "invalid-manifest-path")
    state_directory = manifest_path.parent
    match = RUN_DIRECTORY_PATTERN.fullmatch(state_directory.name)
    if (
        manifest_path.name != MANIFEST_NAME
        or state_directory.parent != runner_temp
        or match is None
    ):
        raise SupervisorError("invalid-manifest-path")
    _check_directory(state_directory, uid=uid, mode=0o700, code="invalid-state-directory")
    return state_directory, manifest_path


def _read_manifest(
    value: str | os.PathLike[str],
    *,
    environment: Mapping[str, str],
    uid: int,
    gid: int,
) -> tuple[Manifest, Path, Path]:
    runner_temp, home, runtime = _validate_context(environment, uid=uid)
    state_directory, manifest_path = _validate_manifest_path(value, runner_temp, uid)
    raw = _check_owned_regular_file(
        manifest_path,
        uid=uid,
        maximum_bytes=MAX_MANIFEST_BYTES,
        code="invalid-manifest",
    )
    document = _decode_json(raw, maximum_bytes=MAX_MANIFEST_BYTES, code="invalid-manifest")
    expected_keys = {
        "containerName", "mountpoint", "mountpointDevice", "mountpointInode", "ownerToken",
        "parentPid", "parentStartTime", "schemaVersion", "scratchDevice", "scratchDirectory",
        "scratchInode",
    }
    if not isinstance(document, dict) or set(document) != expected_keys:
        raise SupervisorError("invalid-manifest")
    token = document.get("ownerToken")
    name = document.get("containerName")
    scratch_value = document.get("scratchDirectory")
    mountpoint_value = document.get("mountpoint")
    parent_pid = document.get("parentPid")
    parent_start = document.get("parentStartTime")
    scratch_device = document.get("scratchDevice")
    scratch_inode = document.get("scratchInode")
    mountpoint_device = document.get("mountpointDevice")
    mountpoint_inode = document.get("mountpointInode")
    if (
        type(document.get("schemaVersion")) is not int
        or document["schemaVersion"] != 1
        or not isinstance(token, str)
        or TOKEN_PATTERN.fullmatch(token) is None
        or state_directory.name != f"appsurface-subject-supervisor-{token}"
        or not isinstance(name, str)
        or CONTAINER_PATTERN.fullmatch(name) is None
        or type(parent_pid) is not int
        or parent_pid <= 1
        or type(parent_start) is not int
        or parent_start <= 0
        or any(type(value) is not int or value < 0 for value in (
            scratch_device, scratch_inode, mountpoint_device, mountpoint_inode
        ))
    ):
        raise SupervisorError("invalid-manifest")

    scratch = _canonical_path(scratch_value, "invalid-scratch-path")
    mountpoint = _canonical_path(mountpoint_value, "invalid-mountpoint")
    if (
        scratch.parent != runner_temp
        or mountpoint.parent != scratch
        or MOUNTPOINT_PATTERN.fullmatch(mountpoint.name) is None
        or scratch == state_directory
        or scratch in state_directory.parents
        or state_directory in scratch.parents
    ):
        raise SupervisorError("invalid-scratch-path")
    scratch_info = _check_directory(scratch, uid=uid, mode=0o700, code="invalid-scratch-directory")
    _check_directory(mountpoint, uid=uid, mode=0o700, code="invalid-mountpoint")
    if (scratch_info.st_dev, scratch_info.st_ino) != (scratch_device, scratch_inode):
        raise SupervisorError("invalid-scratch-directory")
    if (
        not stat.S_ISDIR(mountpoint.stat().st_mode)
        or mountpoint.stat().st_uid != uid
        or mountpoint.stat().st_gid != gid
        or stat.S_IMODE(mountpoint.stat().st_mode) != 0o700
    ):
        raise SupervisorError("invalid-mountpoint")
    # Validate the current process identity context now; command execution uses
    # this narrow allowlist rather than inheriting runner credentials.
    del home, runtime
    manifest = Manifest(
        token, name, scratch, mountpoint, parent_pid, parent_start,
        scratch_device, scratch_inode, mountpoint_device, mountpoint_inode,
    )
    return manifest, state_directory, runner_temp


def _write_new_file(directory: Path, name: str, content: bytes, *, mode: int = 0o600) -> None:
    if len(content) > MAX_MANIFEST_BYTES:
        raise SupervisorError("record-too-large")
    directory_fd = os.open(directory, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0))
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        fd = os.open(name, flags, mode, dir_fd=directory_fd)
        try:
            view = memoryview(content)
            while view:
                written = os.write(fd, view)
                view = view[written:]
            os.fsync(fd)
        finally:
            os.close(fd)
        os.fsync(directory_fd)
    finally:
        os.close(directory_fd)


def _atomic_attestation(directory: Path, document: Mapping[str, Any]) -> None:
    payload = (json.dumps(document, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    if len(payload) > MAX_ATTESTATION_BYTES:
        raise SupervisorError("attestation-too-large")
    directory_fd = os.open(directory, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0))
    temporary_name = ".cleanup-attestation.tmp"
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    created_temporary = False
    try:
        fd = os.open(temporary_name, flags, 0o600, dir_fd=directory_fd)
        created_temporary = True
        try:
            view = memoryview(payload)
            while view:
                written = os.write(fd, view)
                view = view[written:]
            os.fsync(fd)
        finally:
            os.close(fd)
        os.link(
            temporary_name,
            ATTESTATION_NAME,
            src_dir_fd=directory_fd,
            dst_dir_fd=directory_fd,
            follow_symlinks=False,
        )
        os.unlink(temporary_name, dir_fd=directory_fd)
        created_temporary = False
        os.fsync(directory_fd)
    finally:
        if created_temporary:
            try:
                os.unlink(temporary_name, dir_fd=directory_fd)
            except OSError:
                pass
        os.close(directory_fd)


def read_process_identity(pid: int, *, proc_root: Path = PROC_ROOT) -> tuple[int, str] | None:
    """Return Linux ``starttime`` ticks and process state for a PID, if readable."""
    try:
        raw = (proc_root / str(pid) / "stat").read_text(encoding="ascii")
        closing_paren = raw.rfind(")")
        if closing_paren < 0:
            return None
        fields = raw[closing_paren + 1 :].split()
        # The substring starts at stat field 3 (state); starttime is field 22.
        if len(fields) <= 19 or len(fields[0]) != 1:
            return None
        return int(fields[19]), fields[0]
    except (OSError, UnicodeError, ValueError):
        return None


def _parent_state(
    manifest: Manifest,
    *,
    proc_root: Path,
    identity_reader: Callable[[int], tuple[int, str] | None] = read_process_identity,
) -> bool | None:
    """True means the exact launcher lives; False means it exited or PID changed."""
    pid_path = proc_root / str(manifest.parent_pid)
    try:
        info = pid_path.stat()
    except FileNotFoundError:
        return False
    except OSError:
        return None
    if info.st_uid != os.geteuid():
        return False
    identity = identity_reader(manifest.parent_pid)
    if identity is None:
        return None
    start_time, state = identity
    if start_time != manifest.parent_start_time or state in {"Z", "X"}:
        return False
    return True


def _read_request(state_directory: Path, token: str, *, uid: int) -> bool | None:
    request_path = state_directory / REQUEST_NAME
    try:
        raw = _check_owned_regular_file(
            request_path,
            uid=uid,
            maximum_bytes=MAX_REQUEST_BYTES,
            code="invalid-request",
        )
    except SupervisorError as exc:
        if exc.code == "invalid-request":
            try:
                request_path.lstat()
            except FileNotFoundError:
                return False
            except OSError:
                return None
        return None
    try:
        document = _decode_json(raw, maximum_bytes=MAX_REQUEST_BYTES, code="invalid-request")
    except SupervisorError:
        return None
    if document != {"ownerToken": token, "schemaVersion": 1}:
        return None
    return True


def _request_cleanup(manifest_path: str | os.PathLike[str], token: str, *, environment: Mapping[str, str]) -> None:
    uid = os.geteuid()
    manifest, state_directory, _ = _read_manifest(
        manifest_path, environment=environment, uid=uid, gid=os.getegid()
    )
    if not isinstance(token, str) or token != manifest.token:
        raise SupervisorError("owner-token-mismatch")
    content = (json.dumps({"ownerToken": token, "schemaVersion": 1}, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    try:
        _write_new_file(state_directory, REQUEST_NAME, content, mode=0o600)
    except FileExistsError:
        if _read_request(state_directory, token, uid=uid) is not True:
            raise SupervisorError("invalid-request") from None


def _parse_mountinfo(text: str, mountpoint: Path) -> _Mount | None:
    matches: list[_Mount] = []
    for line in text.splitlines():
        left, separator, right = line.partition(" - ")
        left_fields = left.split()
        right_fields = right.split()
        if not separator or len(left_fields) < 6 or len(right_fields) < 3:
            raise ValueError("malformed mountinfo")
        decoded_path = re.sub(r"\\([0-7]{3})", lambda match: chr(int(match.group(1), 8)), left_fields[4])
        if decoded_path != str(mountpoint):
            continue
        matches.append(
            _Mount(
                right_fields[0],
                right_fields[1],
                tuple(left_fields[5].split(",")),
                tuple(right_fields[2].split(",")),
            )
        )
    if len(matches) > 1:
        raise ValueError("ambiguous mountpoint")
    return matches[0] if matches else None


def _size_bytes(value: str) -> int | None:
    match = re.fullmatch(r"([0-9]+)([kKmMgGtT]?)", value)
    if match is None:
        return None
    scale = {"": 1, "k": 1024, "m": 1024**2, "g": 1024**3, "t": 1024**4}
    return int(match.group(1)) * scale[match.group(2).lower()]


def _matching_owned_mount(
    mount: _Mount,
    mountpoint: Path,
    *,
    uid: int,
    gid: int,
    statvfs: Callable[[str | os.PathLike[str]], Any],
) -> bool:
    options: dict[str, str] = {}
    for item in mount.super_options:
        key, separator, value = item.partition("=")
        if separator:
            if key in options:
                return False
            options[key] = value
    try:
        info = mountpoint.stat()
        filesystem = statvfs(mountpoint)
        mode = int(options.get("mode", "-1"), 8)
        actual_bytes = filesystem.f_blocks * filesystem.f_frsize
        return (
            mount.filesystem == "tmpfs"
            and mount.source == "tmpfs"
            and {"rw", "nosuid", "nodev"}.issubset(set(mount.mount_options + mount.super_options))
            and {"ro", "suid", "dev", "noswap"}.isdisjoint(set(mount.mount_options + mount.super_options))
            and _size_bytes(options.get("size", "")) == TMPFS_BYTES
            and options.get("nr_inodes") == str(TMPFS_INODES)
            and mode == 0o700
            and int(options.get("uid", "-1")) == uid
            and int(options.get("gid", "-1")) == gid
            and stat.S_ISDIR(info.st_mode)
            and stat.S_IMODE(info.st_mode) == 0o700
            and info.st_uid == uid
            and info.st_gid == gid
            and actual_bytes == TMPFS_BYTES
            and filesystem.f_files == TMPFS_INODES
        )
    except (OSError, ValueError, OverflowError, AttributeError):
        return False


def _remove_owned_scratch(manifest: Manifest, *, runner_temp: Path, uid: int, gid: int) -> str:
    """Remove only the fixed result record and exact, identity-checked scratch directories."""
    runner_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    scratch_flags = os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        runner_fd = os.open(runner_temp, runner_flags)
    except OSError:
        return "unsafe"
    try:
        try:
            scratch_fd = os.open(manifest.scratch_directory.name, scratch_flags, dir_fd=runner_fd)
        except OSError:
            return "unsafe"
        try:
            scratch_info = os.fstat(scratch_fd)
            if (
                not stat.S_ISDIR(scratch_info.st_mode)
                or scratch_info.st_uid != uid
                or stat.S_IMODE(scratch_info.st_mode) != 0o700
                or (scratch_info.st_dev, scratch_info.st_ino)
                != (manifest.scratch_device, manifest.scratch_inode)
            ):
                return "unsafe"
            try:
                mount_info = os.stat(
                    manifest.mountpoint.name,
                    dir_fd=scratch_fd,
                    follow_symlinks=False,
                )
            except FileNotFoundError:
                mount_info = None
            except OSError:
                return "unsafe"
            if mount_info is not None:
                if (
                    not stat.S_ISDIR(mount_info.st_mode)
                    or mount_info.st_uid != uid
                    or mount_info.st_gid != gid
                    or stat.S_IMODE(mount_info.st_mode) != 0o700
                    or (mount_info.st_dev, mount_info.st_ino)
                    != (manifest.mountpoint_device, manifest.mountpoint_inode)
                ):
                    return "unsafe"
                try:
                    os.rmdir(manifest.mountpoint.name, dir_fd=scratch_fd)
                except OSError:
                    return "failed"

            try:
                with os.scandir(scratch_fd) as directory_entries:
                    entries: list[str] = []
                    for entry in directory_entries:
                        entries.append(entry.name)
                        if len(entries) == 2:
                            break
            except OSError:
                return "failed"
            if len(entries) > 1 or any(name != SUBJECT_RESULT_NAME for name in entries):
                return "preserved"
            if entries:
                result_fd = -1
                try:
                    result_fd = os.open(
                        SUBJECT_RESULT_NAME,
                        os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
                        dir_fd=scratch_fd,
                    )
                    result_info = os.fstat(result_fd)
                    if (
                        not stat.S_ISREG(result_info.st_mode)
                        or result_info.st_uid != uid
                        or stat.S_IMODE(result_info.st_mode) != 0o600
                        or result_info.st_nlink != 1
                        or result_info.st_size > MAX_SUBJECT_RESULT_BYTES
                    ):
                        return "unsafe"
                except OSError:
                    return "unsafe"
                finally:
                    if result_fd >= 0:
                        os.close(result_fd)
                try:
                    current_result = os.stat(
                        SUBJECT_RESULT_NAME,
                        dir_fd=scratch_fd,
                        follow_symlinks=False,
                    )
                    if (current_result.st_dev, current_result.st_ino) != (
                        result_info.st_dev, result_info.st_ino
                    ):
                        return "unsafe"
                except OSError:
                    return "unsafe"
                try:
                    os.unlink(SUBJECT_RESULT_NAME, dir_fd=scratch_fd)
                except OSError:
                    return "failed"

            try:
                current = os.stat(
                    manifest.scratch_directory.name,
                    dir_fd=runner_fd,
                    follow_symlinks=False,
                )
                if (current.st_dev, current.st_ino) != (manifest.scratch_device, manifest.scratch_inode):
                    return "unsafe"
                os.rmdir(manifest.scratch_directory.name, dir_fd=runner_fd)
            except OSError:
                return "failed"
            return "removed"
        finally:
            os.close(scratch_fd)
    finally:
        os.close(runner_fd)


def _container_names(item: Any) -> set[str] | None:
    if not isinstance(item, dict):
        return None
    names = item.get("Names", item.get("Name"))
    if isinstance(names, str):
        values = [names]
    elif isinstance(names, list) and all(isinstance(name, str) for name in names):
        values = names
    else:
        return None
    normalized = {value[1:] if value.startswith("/") else value for value in values}
    return normalized


def _list_target_container(
    manifest: Manifest,
    *,
    executor: Callable[..., CommandResult],
    environment: Mapping[str, str],
    timeout_seconds: float,
) -> tuple[str, Any | None]:
    result = executor(
        [
            PODMAN_PATH,
            "--remote=false",
            "ps",
            "--all",
            "--no-trunc",
            "--format=json",
        ],
        timeout_seconds=timeout_seconds,
        maximum_output_bytes=MAX_COMMAND_OUTPUT_BYTES,
        environment=environment,
    )
    if result.returncode != 0:
        return "unverified", None
    value = _decode_json(result.stdout, maximum_bytes=MAX_COMMAND_OUTPUT_BYTES, code="invalid-container-list")
    if not isinstance(value, list) or len(value) > 4096:
        return "unverified", None
    exact: list[Any] = []
    for item in value:
        names = _container_names(item)
        if names is None:
            return "unverified", None
        if manifest.container_name in names:
            exact.append(item)
    if not exact:
        return "absent", None
    if len(exact) != 1:
        return "unverified", None
    return "present", exact[0]


def _confirm_container_absent(
    manifest: Manifest,
    *,
    executor: Callable[..., CommandResult],
    environment: Mapping[str, str],
    timeout_seconds: Callable[[], float],
    sleep: Callable[[float], None],
    observations: int = 5,
) -> bool:
    """Require several separated full inventory reads before treating absence as stable."""
    for index in range(observations):
        status, _ = _list_target_container(
            manifest,
            executor=executor,
            environment=environment,
            timeout_seconds=timeout_seconds(),
        )
        if status != "absent":
            return False
        if index + 1 < observations:
            sleep(0.25)
    return True


def _inspect_owned_container(
    manifest: Manifest,
    *,
    executor: Callable[..., CommandResult],
    environment: Mapping[str, str],
    timeout_seconds: float,
) -> tuple[str, str | None]:
    listing_status, _ = _list_target_container(
        manifest, executor=executor, environment=environment, timeout_seconds=timeout_seconds
    )
    if listing_status != "present":
        return listing_status, None
    inspected = executor(
        [PODMAN_PATH, "--remote=false", "inspect", "--format=json", manifest.container_name],
        timeout_seconds=timeout_seconds,
        maximum_output_bytes=MAX_COMMAND_OUTPUT_BYTES,
        environment=environment,
    )
    if inspected.returncode != 0:
        return "unverified", None
    document = _decode_json(inspected.stdout, maximum_bytes=MAX_COMMAND_OUTPUT_BYTES, code="invalid-container-inspect")
    if not isinstance(document, list) or len(document) != 1 or not isinstance(document[0], dict):
        return "unverified", None
    item = document[0]
    name = item.get("Name")
    if isinstance(name, str) and name.startswith("/"):
        name = name[1:]
    config = item.get("Config")
    labels = config.get("Labels") if isinstance(config, dict) else None
    mounts = item.get("Mounts")
    if (
        name != manifest.container_name
        or not isinstance(labels, dict)
        or labels.get(OWNER_LABEL) != manifest.token
        or not isinstance(mounts, list)
    ):
        return "unverified", None
    expected_mounts = [
        mount
        for mount in mounts
        if isinstance(mount, dict)
        and mount.get("Destination") == "/scratch"
        and mount.get("Type") == "bind"
        and mount.get("Source") == str(manifest.mountpoint)
    ]
    if len(expected_mounts) != 1:
        return "unverified", None
    container_id = item.get("Id", item.get("ID"))
    if not isinstance(container_id, str) or re.fullmatch(r"[0-9a-f]{12,64}", container_id) is None:
        return "unverified", None
    return "owned", container_id


def _safe_command_environment(home: Path, runtime: Path, runner_temp: Path) -> dict[str, str]:
    return {
        "PATH": ENGINE_PATH,
        "HOME": str(home),
        "XDG_RUNTIME_DIR": str(runtime),
        "TMPDIR": str(runner_temp),
        "REGISTRY_AUTH_FILE": "/dev/null",
    }


def _run_command(
    arguments: Sequence[str],
    *,
    timeout_seconds: float,
    maximum_output_bytes: int,
    environment: Mapping[str, str],
) -> CommandResult:
    if (
        not arguments
        or any(not isinstance(value, str) or not value or "\x00" in value for value in arguments)
        or timeout_seconds <= 0
        or maximum_output_bytes <= 0
    ):
        raise CommandError("invalid-command")
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
        raise CommandError("command-start-failed") from None

    selector = selectors.DefaultSelector()
    stdout = bytearray()
    stderr = bytearray()
    deadline = time.monotonic() + timeout_seconds
    try:
        if process.stdout is None or process.stderr is None:
            raise CommandError("command-start-failed")
        for pipe, label in ((process.stdout, "stdout"), (process.stderr, "stderr")):
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ, label)
        while selector.get_map() or process.poll() is None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise CommandError("command-timeout")
            for key, _ in selector.select(min(0.1, remaining)):
                target = stdout if key.data == "stdout" else stderr
                available = maximum_output_bytes - len(stdout) - len(stderr)
                try:
                    chunk = os.read(key.fileobj.fileno(), min(64 * 1024, available + 1))
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                    continue
                target.extend(chunk)
                if len(stdout) + len(stderr) > maximum_output_bytes:
                    raise CommandError("command-output-exceeded")
        return CommandResult(process.wait(), bytes(stdout), bytes(stderr))
    except CommandError:
        _terminate_process_group(process)
        raise
    except (OSError, ValueError, selectors.SelectorError):
        _terminate_process_group(process)
        raise CommandError("command-io-failed") from None
    finally:
        selector.close()
        for pipe in (process.stdout, process.stderr):
            if pipe is not None and not pipe.closed:
                pipe.close()


def _terminate_process_group(process: subprocess.Popen[bytes]) -> None:
    try:
        os.killpg(process.pid, signal.SIGTERM)
    except ProcessLookupError:
        pass
    except OSError:
        try:
            process.terminate()
        except OSError:
            pass
    try:
        process.wait(timeout=1)
    except subprocess.TimeoutExpired:
        pass
    # The CLI leader may exit while a descendant retains a captured pipe;
    # terminate the private session even if that leader has already exited.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except OSError:
        try:
            process.kill()
        except OSError:
            pass
    try:
        process.wait(timeout=1)
    except subprocess.TimeoutExpired:
        pass


def _cleanup(
    manifest: Manifest,
    *,
    home: Path,
    runtime: Path,
    runner_temp: Path,
    uid: int,
    gid: int,
    executor: Callable[..., CommandResult],
    mountinfo_reader: Callable[[], str],
    statvfs: Callable[[str | os.PathLike[str]], Any],
    monotonic: Callable[[], float],
    sleep: Callable[[float], None],
    cleanup_deadline: float,
) -> dict[str, str]:
    statuses = {
        "containerStatus": "unverified",
        "mountStatus": "unverified",
        "scratchStatus": "preserved",
    }
    command_environment = _safe_command_environment(home, runtime, runner_temp)

    def command_timeout() -> float:
        remaining = cleanup_deadline - monotonic()
        if remaining <= 0:
            raise CommandError("cleanup-deadline-expired")
        return min(MAX_COMMAND_SECONDS, remaining)

    # Revalidate the path tree immediately before acting.  Cleanup uses only
    # fixed-name unlink/rmdir operations anchored to directory descriptors.
    try:
        current_scratch = _canonical_path(manifest.scratch_directory, "invalid-scratch-path")
        current_mountpoint = _canonical_path(manifest.mountpoint, "invalid-mountpoint")
        if current_scratch != manifest.scratch_directory or current_mountpoint != manifest.mountpoint:
            raise SupervisorError("invalid-scratch-path")
        scratch_info = _check_directory(current_scratch, uid=uid, mode=0o700, code="invalid-scratch-directory")
        if (scratch_info.st_dev, scratch_info.st_ino) != (manifest.scratch_device, manifest.scratch_inode):
            raise SupervisorError("invalid-scratch-directory")
        _check_directory(current_mountpoint, uid=uid, mode=0o700, code="invalid-mountpoint")
    except SupervisorError:
        return statuses

    try:
        container_status, container_id = _inspect_owned_container(
            manifest,
            executor=executor,
            environment=command_environment,
            timeout_seconds=command_timeout(),
        )
    except (CommandError, SupervisorError):
        container_status, container_id = "unverified", None

    if container_status == "absent":
        try:
            stable_absence = _confirm_container_absent(
                manifest,
                executor=executor,
                environment=command_environment,
                timeout_seconds=command_timeout,
                sleep=sleep,
            )
        except (CommandError, SupervisorError):
            stable_absence = False
        statuses["containerStatus"] = "absent" if stable_absence else "unverified"
    elif container_status == "owned" and container_id is not None:
        try:
            removed = executor(
                [PODMAN_PATH, "--remote=false", "rm", "--force", "--ignore", container_id],
                timeout_seconds=command_timeout(),
                maximum_output_bytes=4096,
                environment=command_environment,
            )
            if removed.returncode == 0:
                post_status, _ = _list_target_container(
                    manifest,
                    executor=executor,
                    environment=command_environment,
                    timeout_seconds=command_timeout(),
                )
                stable_absence = (
                    _confirm_container_absent(
                        manifest,
                        executor=executor,
                        environment=command_environment,
                        timeout_seconds=command_timeout,
                        sleep=sleep,
                    )
                    if post_status == "absent"
                    else False
                )
                statuses["containerStatus"] = "removed" if stable_absence else "failed"
            else:
                statuses["containerStatus"] = "failed"
        except (CommandError, SupervisorError):
            statuses["containerStatus"] = "failed"
    else:
        statuses["containerStatus"] = container_status

    if statuses["containerStatus"] not in {"absent", "removed"}:
        return statuses

    try:
        mount = _parse_mountinfo(mountinfo_reader(), manifest.mountpoint)
    except (OSError, UnicodeError, ValueError, OverflowError):
        return statuses

    if mount is None:
        statuses["mountStatus"] = "absent"
        statuses["scratchStatus"] = _remove_owned_scratch(
            manifest, runner_temp=runner_temp, uid=uid, gid=gid
        )
        return statuses

    if not _matching_owned_mount(mount, manifest.mountpoint, uid=uid, gid=gid, statvfs=statvfs):
        return statuses
    try:
        unmounted = executor(
            [SUDO_PATH, "-n", "umount", "--", str(manifest.mountpoint)],
            timeout_seconds=command_timeout(),
            maximum_output_bytes=4096,
            environment=command_environment,
        )
        if unmounted.returncode != 0:
            statuses["mountStatus"] = "failed"
            return statuses
        if _parse_mountinfo(mountinfo_reader(), manifest.mountpoint) is not None:
            statuses["mountStatus"] = "failed"
            return statuses
        statuses["mountStatus"] = "unmounted"
        statuses["scratchStatus"] = _remove_owned_scratch(
            manifest, runner_temp=runner_temp, uid=uid, gid=gid
        )
    except CommandError as exc:
        statuses["mountStatus"] = "failed" if exc.code != "cleanup-deadline-expired" else "deadline-expired"
    except (OSError, UnicodeError, ValueError, OverflowError):
        statuses["mountStatus"] = "failed"
    return statuses


def _attestation(
    *,
    trigger: str,
    statuses: Mapping[str, str],
    failure_code: str,
) -> dict[str, Any]:
    complete = (
        statuses.get("containerStatus") in {"removed", "absent"}
        and statuses.get("mountStatus") in {"unmounted", "absent"}
        and statuses.get("scratchStatus") in {"removed", "absent"}
        and failure_code == "none"
    )
    return {
        "schemaVersion": 1,
        "claimEligible": False,
        "published": False,
        "status": "complete" if complete else "incomplete",
        "trigger": trigger,
        "containerStatus": statuses.get("containerStatus", "not-attempted"),
        "mountStatus": statuses.get("mountStatus", "not-attempted"),
        "scratchStatus": statuses.get("scratchStatus", "not-attempted"),
        "failureCode": failure_code,
        "cleanupDeadlineSeconds": CLEANUP_DEADLINE_SECONDS,
    }


def run_supervisor(
    manifest_path: str | os.PathLike[str],
    *,
    environment: Mapping[str, str] | None = None,
    effective_uid: int | None = None,
    effective_gid: int | None = None,
    proc_root: Path = PROC_ROOT,
    proc_identity_reader: Callable[[int], tuple[int, str] | None] | None = None,
    mountinfo_reader: Callable[[], str] | None = None,
    executor: Callable[..., CommandResult] = _run_command,
    statvfs: Callable[[str | os.PathLike[str]], Any] = os.statvfs,
    monotonic: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
    max_parent_lifetime_seconds: float = MAX_PARENT_LIFETIME_SECONDS,
    cleanup_deadline_seconds: float = CLEANUP_DEADLINE_SECONDS,
    ready_fd: int | None = None,
) -> int:
    env = dict(os.environ if environment is None else environment)
    uid = os.geteuid() if effective_uid is None else effective_uid
    gid = os.getegid() if effective_gid is None else effective_gid
    manifest, state_directory, runner_temp = _read_manifest(
        manifest_path, environment=env, uid=uid, gid=gid
    )
    _, home, runtime = _validate_context(env, uid=uid)
    if ready_fd is not None:
        try:
            os.write(ready_fd, b"READY\n")
        finally:
            os.close(ready_fd)
    read_mountinfo = mountinfo_reader or (lambda: MOUNTINFO_PATH.read_text(encoding="utf-8"))
    started_at = monotonic()
    trigger: str | None = None
    failure_code = "none"
    invalid_request_seen = False
    while trigger is None:
        requested = _read_request(state_directory, manifest.token, uid=uid)
        if requested is None:
            invalid_request_seen = True
        if requested:
            trigger = "request"
            break
        parent_state = _parent_state(
            manifest,
            proc_root=proc_root,
            identity_reader=proc_identity_reader
            or (lambda pid: read_process_identity(pid, proc_root=proc_root)),
        )
        if parent_state is False:
            trigger = "parent-exit"
            if invalid_request_seen:
                failure_code = "invalid-request"
            break
        if parent_state is None:
            # Never unmount resources while the parent might still be alive.
            if monotonic() - started_at >= max_parent_lifetime_seconds:
                trigger = "parent-unverifiable"
                failure_code = "parent-unverifiable"
                break
        elif monotonic() - started_at >= max_parent_lifetime_seconds:
            trigger = "supervisor-lease-expired"
            failure_code = "supervisor-lease-expired"
            break
        sleep(min(PARENT_POLL_SECONDS, max(0.0, started_at + max_parent_lifetime_seconds - monotonic())))

    statuses = {
        "containerStatus": "not-attempted",
        "mountStatus": "not-attempted",
        "scratchStatus": "not-attempted",
    }
    if trigger in {"request", "parent-exit"}:
        cleanup_started = monotonic()
        try:
            statuses = _cleanup(
                manifest,
                home=home,
                runtime=runtime,
                runner_temp=runner_temp,
                uid=uid,
                gid=gid,
                executor=executor,
                mountinfo_reader=read_mountinfo,
            statvfs=statvfs,
            monotonic=monotonic,
            sleep=sleep,
            cleanup_deadline=cleanup_started + cleanup_deadline_seconds,
            )
        except Exception:
            statuses = {
                "containerStatus": "unverified",
                "mountStatus": "unverified",
                "scratchStatus": "preserved",
            }
            failure_code = "cleanup-internal-error"
        if any(value in {"failed", "unverified", "unsafe", "deadline-expired", "preserved"} for value in statuses.values()):
            failure_code = "resource-cleanup-incomplete"
    record = _attestation(trigger=trigger or "unknown", statuses=statuses, failure_code=failure_code)
    try:
        _atomic_attestation(state_directory, record)
    except (OSError, SupervisorError):
        return 2
    return 0 if record["status"] == "complete" else 1


def _prepare_and_start(
    *,
    container_name: str,
    scratch_directory_value: str,
    mountpoint_value: str,
    parent_pid: int,
    parent_start_time: int,
    environment: Mapping[str, str],
) -> dict[str, Any]:
    uid = os.geteuid()
    gid = os.getegid()
    runner_temp, home, runtime = _validate_context(environment, uid=uid)
    if not isinstance(container_name, str) or CONTAINER_PATTERN.fullmatch(container_name) is None:
        raise SupervisorError("invalid-container-name")
    scratch = _canonical_path(scratch_directory_value, "invalid-scratch-path")
    mountpoint = _canonical_path(mountpoint_value, "invalid-mountpoint")
    if (
        scratch.parent != runner_temp
        or mountpoint.parent != scratch
        or MOUNTPOINT_PATTERN.fullmatch(mountpoint.name) is None
    ):
        raise SupervisorError("invalid-scratch-path")
    scratch_info = _check_directory(scratch, uid=uid, mode=0o700, code="invalid-scratch-directory")
    mount_info = _check_directory(mountpoint, uid=uid, mode=0o700, code="invalid-mountpoint")
    if mount_info.st_gid != gid:
        raise SupervisorError("invalid-mountpoint")

    if type(parent_pid) is not int or parent_pid <= 1 or type(parent_start_time) is not int or parent_start_time <= 0:
        raise SupervisorError("invalid-parent-identity")
    parent_identity = read_process_identity(parent_pid)
    try:
        parent_uid = (PROC_ROOT / str(parent_pid)).stat().st_uid
    except OSError:
        raise SupervisorError("parent-identity-unavailable") from None
    if (
        parent_identity is None
        or parent_uid != uid
        or parent_identity[0] != parent_start_time
        or parent_identity[1] in {"Z", "X"}
    ):
        raise SupervisorError("parent-identity-unavailable")

    token = os.urandom(16).hex()
    state_directory = runner_temp / f"appsurface-subject-supervisor-{token}"
    try:
        os.mkdir(state_directory, 0o700)
    except OSError:
        raise SupervisorError("state-directory-create-failed") from None
    state_info = state_directory.lstat()
    if state_info.st_uid != uid or stat.S_IMODE(state_info.st_mode) != 0o700:
        try:
            state_directory.rmdir()
        except OSError:
            pass
        raise SupervisorError("state-directory-create-failed")
    document = {
        "schemaVersion": 1,
        "ownerToken": token,
        "containerName": container_name,
        "scratchDirectory": str(scratch),
        "mountpoint": str(mountpoint),
        "parentPid": parent_pid,
        "parentStartTime": parent_start_time,
        "scratchDevice": scratch_info.st_dev,
        "scratchInode": scratch_info.st_ino,
        "mountpointDevice": mount_info.st_dev,
        "mountpointInode": mount_info.st_ino,
    }
    manifest_path = state_directory / MANIFEST_NAME
    payload = (json.dumps(document, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    try:
        _write_new_file(state_directory, MANIFEST_NAME, payload)
        child_environment = {
            "PATH": ENGINE_PATH,
            "HOME": str(home),
            "XDG_RUNTIME_DIR": str(runtime),
            "RUNNER_TEMP": str(runner_temp),
        }
        ready_read_fd, ready_write_fd = os.pipe()
        try:
            child = subprocess.Popen(
                [
                    sys.executable,
                    str(Path(__file__).resolve()),
                    "supervise",
                    "--manifest",
                    str(manifest_path),
                    "--ready-fd",
                    str(ready_write_fd),
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                shell=False,
                close_fds=True,
                pass_fds=(ready_write_fd,),
                start_new_session=True,
                cwd="/",
                env=child_environment,
            )
        finally:
            os.close(ready_write_fd)

        try:
            selector = selectors.DefaultSelector()
            try:
                selector.register(ready_read_fd, selectors.EVENT_READ)
                events = selector.select(SUPERVISOR_STARTUP_SECONDS)
                ready = os.read(ready_read_fd, 16) if events else b""
            finally:
                selector.close()
                os.close(ready_read_fd)
            child_identity = read_process_identity(child.pid)
            if (
                ready != b"READY\n"
                or child.poll() is not None
                or child_identity is None
                or child_identity[1] in {"Z", "X"}
            ):
                raise SupervisorError("supervisor-not-ready")
        except BaseException:
            try:
                os.killpg(child.pid, signal.SIGTERM)
            except OSError:
                pass
            try:
                child.wait(timeout=1)
            except subprocess.TimeoutExpired:
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except OSError:
                    pass
                try:
                    child.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    pass
            raise
    except (OSError, ValueError):
        raise SupervisorError("supervisor-start-failed") from None
    return {
        "schemaVersion": 1,
        "claimEligible": False,
        "ownerToken": token,
        "stateDirectory": str(state_directory),
        "manifestPath": str(manifest_path),
        "supervisorPid": child.pid,
        "supervisorStartTime": child_identity[0],
        "supervisorReady": True,
        "ownerLabel": OWNER_LABEL,
    }


def _main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Bounded non-claiming OCI subject cleanup supervisor")
    subparsers = parser.add_subparsers(dest="operation", required=True)
    start_parser = subparsers.add_parser("start", help="start an independent cleanup supervisor")
    start_parser.add_argument("--container-name", required=True)
    start_parser.add_argument("--scratch-directory", required=True)
    start_parser.add_argument("--mountpoint", required=True)
    start_parser.add_argument("--parent-pid", required=True, type=int)
    start_parser.add_argument("--parent-start-time", required=True, type=int)
    request_parser = subparsers.add_parser("request", help="request cleanup after normal completion")
    request_parser.add_argument("--manifest", required=True)
    request_parser.add_argument("--owner-token", required=True)
    supervise_parser = subparsers.add_parser("supervise", help=argparse.SUPPRESS)
    supervise_parser.add_argument("--manifest", required=True)
    supervise_parser.add_argument("--ready-fd", required=True, type=int)
    arguments = parser.parse_args(argv)
    environment = dict(os.environ)
    try:
        if arguments.operation == "start":
            response = _prepare_and_start(
                container_name=arguments.container_name,
                scratch_directory_value=arguments.scratch_directory,
                mountpoint_value=arguments.mountpoint,
                parent_pid=arguments.parent_pid,
                parent_start_time=arguments.parent_start_time,
                environment=environment,
            )
            print(json.dumps(response, sort_keys=True, separators=(",", ":")))
            return 0
        if arguments.operation == "request":
            if TOKEN_PATTERN.fullmatch(arguments.owner_token) is None:
                raise SupervisorError("invalid-owner-token")
            _request_cleanup(arguments.manifest, arguments.owner_token, environment=environment)
            return 0
        if arguments.operation == "supervise":
            return run_supervisor(arguments.manifest, environment=environment, ready_fd=arguments.ready_fd)
    except SupervisorError as exc:
        print(f"subject supervisor failed: {exc.code}", file=sys.stderr)
        return 2
    except (OSError, ValueError, CommandError):
        print("subject supervisor failed: operation-error", file=sys.stderr)
        return 2
    return 2


if __name__ == "__main__":
    raise SystemExit(_main())
