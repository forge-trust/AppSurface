#!/usr/bin/env python3
"""Provisional root-owned Linux EvidenceHost launcher/broker; this grants no admission.

Parent contract: invoke as root with ``--tool-root`` (immutable root-owned published
CLI build), ``--subject-root`` (checkout), protected ``--policy-file``, bounded
``--job-seconds``, ``--mode`` (``trusted`` or ``observation``), and a fresh
``--output-parent``/``--output-slot``, plus explicit ``--base-revision``,
``--subject-revision``, ``--workflow-identity``, ``--run-id`` (exact CI run/attempt),
``--solution``, repeatable ``--path`` and repeatable root-selected
``--observation-profile``. The worker command is
``dotnet ForgeTrust.AppSurface.Cli.dll evidence worker --control <socket_path>``.
Before callbacks it sends ``{"op":"ready"}\n`` and receives
``{"ok":true,"descriptor":{...},"job_remaining_seconds":...}\n``. The
remaining allowance is positive, derived from the frozen monotonic deadline, and
never exceeds the launch allowance. The 0440 root-owned descriptor schema is
``evidence-worker-linux-v1`` and includes run/worker/subject identities, unit and
cgroup, deadline, protected paths, mode, socket, ``policy_sha256`` (SHA-256 of
the exact policy-file bytes), output-parent ``device_major``, ``device_minor``,
``inode``, ``uid`` and ``gid``, ``dotnet_path``, ``test_output_root``, provider,
platform, proof digest, observation IDs, entry hash, revisions, workflow
identity, and optional ``diff_file``/``diff_sha256`` for one immutable protected
20 MiB diff snapshot. The diff file must already live inside the protected tool
root; the launcher never copies a subject-provided patch into it. Its output_parent is a new worker-owned 0700 run-tag anchor under the
root-owned outer output parent; output_slot is absent until the worker's own
admission-time allocator creates it. Provider is ``github-actions`` and platform
is ``linux-x64``; the compiled Trusted proof-digest allowlist is empty, so
Trusted mode always fails closed. Requests are newline-delimited JSON, at most 64 KiB.
``run`` has fields ``op``, ``executable``, ``arguments`` and
``working_directory``; a confirmed response also reports ``received_bytes`` for
all stdout/stderr bytes, including bytes discarded beyond the bounded prefixes.
It accepts only tokenized dotnet build/restore/test/msbuild and a working directory
below subject_root. A test command must select one single-token results directory
below ``test_output_root``. ``artifacts`` lists at most 64 regular files, each at
most 20 MiB and 256 MiB total, with paths and byte lengths only. ``artifact`` reads
the retained descriptor sequentially in chunks of at most 128 KiB. Artifact
content is hostile data: the broker validates filesystem shape and descriptor
identity, but does not interpret or approve its contents. Paths use Linux x86-64
``openat2`` with BENEATH, NO_SYMLINKS, NO_MAGICLINKS and NO_XDEV; no fallback is
available. The worker cannot directly access ``test_output_root``. The subject is
copied to a fresh scratch tree, rejecting links and special files; the worker gets
read-only source-map access while test output is inaccessible in its namespace.
The copy remains writable by the subject identity. This subject lane does
not execute arbitrary assemblies or the protected reporter DLL; a registered
reporter runner belongs in a separate lane.
``stop``, ``wait`` and ``exit`` use separate concurrent connections;
The run exchange remains owned through its bounded response after caller
cancellation; cancellation uses a separate ``stop`` connection. ``wait`` closes
the run gate and waits on ownership notifications, with bounded cgroup polling
until the stopping grace, cleanup reserve or frozen job deadline expires. Every
descriptor contains all five stage budgets. There is no Aspire start lane in this
prototype, so the minimum overall job deadline is admission + collection + cleanup;
stopping is reserved within cleanup and is not added again. ``start_seconds`` is
still carried for the C# worker contract. Successful
``wait`` is exactly ``{"ok":true,"owned_exit":true}`` and requires
empty child cgroups and joined output pumps. The broker PID is in the descriptor
so the C# client can pin SO_PEERCRED across connections. No
shell is involved. This is internal/provisional code, not a public API. Execution
requires disposable Linux, systemd 255+, cgroup v2, and root.
"""
from __future__ import annotations

import argparse
import base64
import ctypes
import grp
import hashlib
import json
import os
import platform
import pwd
import re
import stat
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

SCHEMA = "evidence-worker-linux-v1"
FAILURE_DIAGNOSTIC_SCHEMA = "evidence-launcher-failure-v1"
FAILURE_DIAGNOSTIC_FILE = "launcher-failure.json"
FAILURE_DIAGNOSTIC_LIMIT = 4096
FAILURE_DIAGNOSTIC_CAUSES = frozenset({
    "unclassified-host-failure", "requires-root-systemd-linux", "requires-cgroup-v2",
    "requires-systemd-255", "openat2-x86-64-required", "systemd-operation-failed", "host-command-start-failed",
    "identity-separation-failed", "prepared-subject-overlaps-protected-root",
    "trusted-cli-missing", "worker-start-failed", "worker-timeout",
    "broker-handler-not-joined", "worker-protocol-incomplete", "worker-unsuccessful",
    "worker-exit-unconfirmed", "subject-exit-unconfirmed", "output-slot-not-allocated",
    "output-slot-invalid", "output-parent-identity-changed", "output-anchor-not-fresh",
    "tool-root-path-not-canonical", "tool-root-symlink-component", "tool-root-path-missing",
    "tool-root-owner-changed", "tool-root-entry-invalid", "trusted-proof-not-allowlisted",
    "subject-entry-invalid", "subject-root-symlink", "subject-copy-path-invalid",
    "subject-copy-path-overlap", "subject-copy-depth-limit", "subject-copy-entry-limit",
    "subject-copy-byte-limit", "subject-changed-during-copy",
})
FAILURE_DIAGNOSTIC_OPERATIONS = frozenset({"systemctl", "systemd-run", "useradd", "groupadd", "userdel", "groupdel", "worker-exit"})
TRUSTED_PROOF_DIGEST_ALLOWLIST: frozenset[str] = frozenset()
MAX_REQUEST = 64 * 1024
MAX_PREFIX = 1024 * 1024
MAX_JOB_OUTPUT = 16 * 1024 * 1024
MAX_ARTIFACT_FILES = 64
MAX_ARTIFACT_FILE_BYTES = 20 * 1024 * 1024
MAX_ARTIFACT_TOTAL_BYTES = 256 * 1024 * 1024
MAX_ARTIFACT_CHUNK_BYTES = 128 * 1024
MAX_ARTIFACT_TREE_ENTRIES = 4096
MAX_ARTIFACT_TREE_DEPTH = 32
MAX_ARTIFACT_PATH_BYTES = 4095
MAX_PROTECTED_DIFF_BYTES = 20 * 1024 * 1024
MAX_SUBJECT_COPY_ENTRIES = 250_000
MAX_SUBJECT_COPY_BYTES = 8 * 1024 * 1024 * 1024
MAX_SUBJECT_COPY_DEPTH = 128
MAX_SUBJECT_UNITS = 128
STAGE_LIMITS = {"admission_seconds": 30, "start_seconds": 120,
                "collection_seconds": 60, "cleanup_seconds": 600,
                "stopping_seconds": 30}
ALLOWED_VERBS = {"build", "restore", "test", "msbuild"}
SAFE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}$")
WINDOWS_ABSOLUTE = re.compile(r"^[A-Za-z]:")
ENV = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent", "LANG": "C.UTF-8", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
WORKER_ENV = {**ENV, "DOTNET_CLI_HOME": "/tmp"}
RUN_ID = re.compile(r"^[0-9]+/[0-9]+$")
SYS_OPENAT2_X86_64 = 437
AT_FDCWD = -100
RESOLVE_NO_XDEV = 0x01
RESOLVE_NO_MAGICLINKS = 0x02
RESOLVE_NO_SYMLINKS = 0x04
RESOLVE_BENEATH = 0x08
OPENAT2_RESOLVE = RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV
O_PATH = getattr(os, "O_PATH", 0o10000000)
_LIBC = ctypes.CDLL(None, use_errno=True)
_LIBC.syscall.restype = ctypes.c_long
_LIBC.syscall.restype = ctypes.c_long


class LauncherError(RuntimeError):
    """Safe, non-sensitive operational failure category."""

    def __init__(self, cause: str, *, operation: str | None = None, exit_code: int | None = None,
                 errno: int | None = None):
        super().__init__(cause)
        self.operation = operation
        self.exit_code = exit_code
        self.errno = errno
        self.worker_main_code = None
        self.worker_main_status = None


def failure_diagnostic(error: Exception) -> dict:
    """Reduce a host failure to closed literal categories, never exception text or child output."""
    kind = ("LauncherError" if isinstance(error, LauncherError) else
            "OSError" if isinstance(error, OSError) else
            "SubprocessError" if isinstance(error, subprocess.SubprocessError) else "ValueError")
    cause = str(error) if isinstance(error, LauncherError) else "unclassified-host-failure"
    record = {"schema": FAILURE_DIAGNOSTIC_SCHEMA, "error_class": kind,
              "cause": cause if cause in FAILURE_DIAGNOSTIC_CAUSES else "unclassified-host-failure"}
    if (isinstance(error, LauncherError) and isinstance(error.operation, str)
            and error.operation in FAILURE_DIAGNOSTIC_OPERATIONS):
        record["operation"] = error.operation
        if type(error.exit_code) is int and -128 <= error.exit_code <= 255:
            record["exit_code"] = error.exit_code
    if isinstance(error, (LauncherError, OSError)) and type(error.errno) is int and 0 <= error.errno <= 4095:
        record["errno"] = error.errno
    if isinstance(error, LauncherError) and error.operation == "worker-exit":
        for name, maximum in (("worker_main_code", 6), ("worker_main_status", 255)):
            value = getattr(error, name)
            if type(value) is int and 0 <= value <= maximum:
                record[name] = value
    return record


def worker_exit_failure(cause: str, properties: dict[str, str]) -> LauncherError:
    """Attach only bounded numeric systemd main-process status to the original host cause."""
    error = LauncherError(cause, operation="worker-exit")
    for key, name, maximum in (("ExecMainCode", "worker_main_code", 6),
                              ("ExecMainStatus", "worker_main_status", 255)):
        value = properties.get(key, "")
        if isinstance(value, str) and re.fullmatch(r"[0-9]{1,3}", value) and int(value) <= maximum:
            setattr(error, name, int(value))
    return error


def validate_failure_diagnostic(record: object) -> dict:
    """Validate the bounded private record before a driver publishes its safe categories."""
    required = {"schema", "error_class", "cause"}
    if (not isinstance(record, dict) or not required <= record.keys()
            or record.keys() - required - {"operation", "exit_code", "errno", "worker_main_code", "worker_main_status"}
            or record["schema"] != FAILURE_DIAGNOSTIC_SCHEMA
            or record["error_class"] not in ("LauncherError", "OSError", "SubprocessError", "ValueError")
            or not isinstance(record["cause"], str) or record["cause"] not in FAILURE_DIAGNOSTIC_CAUSES):
        raise LauncherError("invalid-private-diagnostic")
    if "operation" in record and (not isinstance(record["operation"], str)
                                  or record["operation"] not in FAILURE_DIAGNOSTIC_OPERATIONS):
        raise LauncherError("invalid-private-diagnostic")
    if "exit_code" in record and ("operation" not in record or type(record["exit_code"]) is not int
                                   or not -128 <= record["exit_code"] <= 255):
        raise LauncherError("invalid-private-diagnostic")
    if "errno" in record and (type(record["errno"]) is not int or not 0 <= record["errno"] <= 4095):
        raise LauncherError("invalid-private-diagnostic")
    for name, maximum in (("worker_main_code", 6), ("worker_main_status", 255)):
        if name in record and (record.get("operation") != "worker-exit" or type(record[name]) is not int
                               or not 0 <= record[name] <= maximum):
            raise LauncherError("invalid-private-diagnostic")
    return dict(record)


def open_diagnostic_directory(directory: Path, *, expected_owner_uid: int = 0) -> int:
    """Pin an existing protected directory; the owner override is a portable test seam only."""
    if not directory.is_absolute():
        raise LauncherError("diagnostic-directory-not-protected")
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    info = os.fstat(fd)
    if info.st_uid != expected_owner_uid or info.st_mode & 0o022:
        os.close(fd)
        raise LauncherError("diagnostic-directory-not-protected")
    return fd


def write_failure_diagnostic(directory_fd: int, error: Exception) -> None:
    """Exclusively create one 0600 no-follow record beneath the retained protected directory."""
    data = json.dumps(failure_diagnostic(error), separators=(",", ":")).encode() + b"\n"
    fd = os.open(FAILURE_DIAGNOSTIC_FILE, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                 0o600, dir_fd=directory_fd)
    with os.fdopen(fd, "wb") as output:
        output.write(data)


def read_failure_diagnostic(directory: Path, *, expected_owner_uid: int = 0) -> dict:
    """Read only a protected regular 0600 file within the fixed byte limit, with no links."""
    directory_fd = open_diagnostic_directory(directory, expected_owner_uid=expected_owner_uid)
    try:
        fd = os.open(FAILURE_DIAGNOSTIC_FILE, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK,
                     dir_fd=directory_fd)
        with os.fdopen(fd, "rb") as source:
            info = os.fstat(source.fileno())
            if (not stat.S_ISREG(info.st_mode) or info.st_uid != expected_owner_uid
                    or stat.S_IMODE(info.st_mode) != 0o600 or info.st_size > FAILURE_DIAGNOSTIC_LIMIT):
                raise LauncherError("invalid-private-diagnostic")
            data = source.read(FAILURE_DIAGNOSTIC_LIMIT + 1)
        if len(data) > FAILURE_DIAGNOSTIC_LIMIT:
            raise LauncherError("invalid-private-diagnostic")
        return validate_failure_diagnostic(json.loads(data))
    finally:
        os.close(directory_fd)


class OpenHow(ctypes.Structure):
    """Linux x86-64 ``open_how`` layout used by the required ``openat2`` syscall."""
    _fields_ = [("flags", ctypes.c_uint64), ("mode", ctypes.c_uint64), ("resolve", ctypes.c_uint64)]


class ArtifactHandle:
    """Broker-owned pinned regular-file descriptor and immutable advertised identity."""
    def __init__(self, fd: int, relative_root: str, relative_path: str,
                 length_bytes: int, identity: tuple[int, ...], root_identity: tuple[int, ...]):
        self.fd = fd
        self.relative_root = relative_root
        self.relative_path = relative_path
        self.length_bytes = length_bytes
        self.identity = identity
        self.root_identity = root_identity
        self.next_offset = 0
        self.ended = False


def ensure_openat2_supported() -> None:
    """Require the native x86-64 Linux ``openat2`` ABI; there is deliberately no fallback."""
    if sys.platform != "linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        raise LauncherError("openat2-x86-64-required")


def _native_openat2(dir_fd: int, path: bytes, how: OpenHow) -> int:
    """Invoke syscall 437 through libc and preserve its errno for the caller."""
    ctypes.set_errno(0)
    result = _LIBC.syscall(SYS_OPENAT2_X86_64, ctypes.c_int(dir_fd), ctypes.c_char_p(path),
                           ctypes.byref(how), ctypes.c_size_t(ctypes.sizeof(how)))
    if result < 0:
        error_number = ctypes.get_errno()
        raise OSError(error_number, os.strerror(error_number), os.fsdecode(path))
    return int(result)


def openat2(dir_fd: int, path: str, flags: int,
            resolve_flags: int = OPENAT2_RESOLVE) -> int:
    """Open one relative path with explicit ``openat2`` resolution flags, never a fallback."""
    ensure_openat2_supported()
    if not isinstance(path, str) or not path or path.startswith("/") or "\0" in path:
        raise LauncherError("artifact-path-invalid")
    encoded_path = os.fsencode(path)
    how = OpenHow(flags | os.O_CLOEXEC, 0, resolve_flags)
    return _native_openat2(dir_fd, encoded_path, how)


def trusted_proof_admitted(proof_digest: str) -> bool:
    """Return whether a digest is compiled into the internal Trusted allowlist."""
    return bool(proof_digest) and proof_digest in TRUSTED_PROOF_DIGEST_ALLOWLIST


def output_parent_identity(path: Path) -> dict[str, int]:
    """Return the stable C# descriptor identity for a worker-owned output anchor."""
    stat = path.stat()
    return {"device_major": os.major(stat.st_dev), "device_minor": os.minor(stat.st_dev),
            "inode": stat.st_ino, "uid": stat.st_uid, "gid": stat.st_gid}


def policy_sha256(path: Path) -> str:
    """Hash the exact protected policy-file bytes without decoding or reserialization."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def nofollow_absolute_path(path: Path) -> Path:
    """Require a normalized absolute path whose every component is non-symlink."""
    if not path.is_absolute() or ".." in path.parts or os.path.normpath(str(path)) != str(path):
        raise LauncherError("tool-root-path-not-canonical")
    current = Path(path.anchor)
    for component in path.parts[1:]:
        current /= component
        try:
            if current.is_symlink(): raise LauncherError("tool-root-symlink-component")
            current.lstat()
        except FileNotFoundError:
            raise LauncherError("tool-root-path-missing") from None
    return path


def prepare_tool_root(tool: Path, worker_gid: int) -> None:
    """Keep root ownership, grant only the worker group read/search, and deny subject access."""
    if tool.stat().st_uid != 0:
        raise LauncherError("tool-root-owner-changed")
    for root, dirs, files in os.walk(tool, followlinks=False):
        directory = Path(root)
        for name in dirs + files:
            child = directory / name
            stat = child.lstat()
            if stat.st_uid != 0 or child.is_symlink() or not (child.is_dir() or child.is_file()):
                raise LauncherError("tool-root-entry-invalid")
        for name in dirs:
            child = directory / name
            stat = child.stat()
            os.chown(child, 0, worker_gid)
            os.chmod(child, ((stat.st_mode & 0o700) | 0o050) & ~0o022)
        for name in files:
            child = directory / name
            stat = child.stat()
            mode = (stat.st_mode & 0o700) | 0o040
            if stat.st_mode & 0o100: mode |= 0o010
            os.chown(child, 0, worker_gid)
            os.chmod(child, mode & ~0o022)
        stat = directory.stat()
        os.chown(directory, 0, worker_gid)
        os.chmod(directory, ((stat.st_mode & 0o700) | 0o050) & ~0o022)


def declared_subject_inputs(subject: Path, solution: str, paths: list[str]) -> tuple[str, list[str]]:
    """Resolve the solution and retain normalized repo-relative diff names, including deleted files."""
    subject = subject.resolve(strict=True)
    solution_path = Path(solution)
    if not solution_path.is_absolute(): solution_path = subject / solution_path
    try:
        solution_path = solution_path.resolve(strict=True)
    except OSError:
        raise LauncherError("solution-outside-subject") from None
    if not solution_path.is_relative_to(subject) or not solution_path.is_file():
        raise LauncherError("solution-outside-subject")
    declared = []
    for item in paths:
        if (not isinstance(item, str) or not item or "\0" in item or "\\" in item
                or item.startswith("/") or WINDOWS_ABSOLUTE.match(item)):
            raise LauncherError("declared-path-outside-subject")
        parts = item.split("/")
        if any(part == ".." for part in parts):
            raise LauncherError("declared-path-outside-subject")
        normalized = "/".join(part for part in parts if part not in ("", "."))
        if not normalized:
            raise LauncherError("declared-path-outside-subject")
        path = subject / normalized
        try:
            resolved = path.resolve(strict=False)
        except (OSError, RuntimeError):
            raise LauncherError("declared-path-outside-subject") from None
        if not resolved.is_relative_to(subject):
            raise LauncherError("declared-path-outside-subject")
        declared.append(normalized)
    return str(solution_path), declared


def roots_overlap(first: Path, second: Path) -> bool:
    """Return whether either canonical directory contains the other."""
    return first == second or first in second.parents or second in first.parents


def validate_test_results_path(scratch: Path, value: str) -> str:
    """Return one canonical results directory below scratch/test-output, rejecting lexical escapes."""
    root = scratch / "test-output"
    if (not isinstance(value, str) or not value or "\0" in value
            or not Path(value).is_absolute() or os.path.normpath(value) != value
            or ".." in Path(value).parts):
        raise LauncherError("test-results-outside-output-root")
    candidate = Path(value)
    try:
        relative = candidate.relative_to(root)
    except ValueError:
        raise LauncherError("test-results-outside-output-root") from None
    if not relative.parts or any(part in ("", ".", "..") for part in relative.parts):
        raise LauncherError("test-results-outside-output-root")
    if len(relative.parts) != 1 or not SAFE_NAME.fullmatch(relative.name):
        raise LauncherError("test-results-outside-output-root")
    return relative.as_posix()


def dotnet_test_results_argument(arguments: list[str]) -> str:
    """Require exactly one explicit ``dotnet test --results-directory`` token/value pair."""
    matches = []
    index = 1
    while index < len(arguments):
        argument = arguments[index]
        lowered = argument.lower()
        if lowered == "--results-directory":
            if index + 1 >= len(arguments):
                raise LauncherError("test-results-directory-missing")
            matches.append(arguments[index + 1])
            index += 2
            continue
        for separator in ("=", ":"):
            prefix = "--results-directory" + separator
            if lowered.startswith(prefix):
                matches.append(argument[len(prefix):])
                break
        else:
            index += 1
            continue
        index += 1
    if len(matches) != 1 or not matches[0]:
        raise LauncherError("test-results-directory-required")
    return matches[0]


def open_test_output_root(scratch: Path, subject_uid: int, results_gid: int) -> int:
    """Pin scratch/test-output through openat2 before starting any worker or subject process."""
    root_fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    scratch_fd = -1
    test_output_fd = -1
    try:
        relative = scratch.resolve(strict=True).relative_to(Path("/")).as_posix()
        scratch_fd = openat2(root_fd, relative, os.O_RDONLY | os.O_DIRECTORY,
                             RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS)
        test_output_fd = openat2(scratch_fd, "test-output", os.O_RDONLY | os.O_DIRECTORY)
        info = os.fstat(test_output_fd)
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != subject_uid or info.st_gid != results_gid:
            raise LauncherError("test-output-root-invalid")
        result = test_output_fd
        test_output_fd = -1
        return result
    finally:
        for fd in (test_output_fd, scratch_fd, root_fd):
            if fd >= 0:
                os.close(fd)


def _filesystem_identity(entry: os.stat_result) -> tuple[int, ...]:
    """Capture metadata needed to detect replacement, linking, ownership or content changes."""
    return (entry.st_dev, entry.st_ino, stat.S_IFMT(entry.st_mode), entry.st_nlink,
            entry.st_uid, entry.st_gid, entry.st_size, entry.st_mtime_ns, entry.st_ctime_ns)


def protected_diff_snapshot(tool_root: Path, requested_path: str | None,
                            root_uid: int = 0) -> tuple[str | None, str | None]:
    """Bind an optional root-selected immutable diff already contained by the protected tool tree."""
    if requested_path is None:
        return None, None
    if not isinstance(requested_path, str) or not requested_path or any(c.isspace() for c in requested_path):
        raise LauncherError("protected-diff-path-invalid")
    candidate = Path(requested_path)
    if (not candidate.is_absolute() or ".." in candidate.parts
            or os.path.normpath(requested_path) != requested_path):
        raise LauncherError("protected-diff-path-invalid")
    try:
        nofollow_absolute_path(candidate)
        resolved_tool = tool_root.resolve(strict=True)
        resolved_file = candidate.resolve(strict=True)
    except (OSError, LauncherError):
        raise LauncherError("protected-diff-path-invalid") from None
    if resolved_file == resolved_tool or not resolved_file.is_relative_to(resolved_tool):
        raise LauncherError("protected-diff-outside-tool-root")
    tool_info = resolved_tool.stat()
    if (not stat.S_ISDIR(tool_info.st_mode) or tool_info.st_uid != root_uid
            or tool_info.st_mode & 0o022):
        raise LauncherError("protected-diff-root-invalid")
    relative = resolved_file.relative_to(resolved_tool).as_posix()
    current = resolved_tool
    for component in relative.split("/")[:-1]:
        current /= component
        info = current.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != root_uid or info.st_mode & 0o022:
            raise LauncherError("protected-diff-parent-invalid")
    root_fd = os.open(resolved_tool, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
    file_fd = -1
    named_fd = -1
    try:
        file_fd = openat2(root_fd, relative, os.O_RDONLY | os.O_NONBLOCK)
        before = os.fstat(file_fd)
        if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_uid != root_uid
                or before.st_mode & 0o222 or before.st_size < 0
                or before.st_size > MAX_PROTECTED_DIFF_BYTES):
            raise LauncherError("protected-diff-file-invalid")
        named_fd = openat2(root_fd, relative, O_PATH)
        if _filesystem_identity(os.fstat(named_fd)) != _filesystem_identity(before):
            raise LauncherError("protected-diff-name-changed")
        digest = hashlib.sha256()
        offset = 0
        while offset < before.st_size:
            block = os.pread(file_fd, min(128 * 1024, before.st_size - offset), offset)
            if not block:
                raise LauncherError("protected-diff-changed")
            digest.update(block)
            offset += len(block)
        after = os.fstat(file_fd)
        named_after_fd = openat2(root_fd, relative, O_PATH)
        try:
            if (_filesystem_identity(after) != _filesystem_identity(before)
                    or _filesystem_identity(os.fstat(named_after_fd)) != _filesystem_identity(before)):
                raise LauncherError("protected-diff-changed")
        finally:
            os.close(named_after_fd)
        return str(resolved_file), digest.hexdigest()
    finally:
        for fd in (named_fd, file_fd, root_fd):
            if fd >= 0:
                os.close(fd)


def _copy_subject_tree(source: Path, destination: Path, subject_uid: int,
                       subject_gid: int, worker_gid: int) -> Path:
    """Copy a checkout into a fresh tree that is readable by the worker and writable by its subject owner.

    Source traversal cannot follow symlinks or magic links and cannot cross a mount beneath the pinned
    checkout root. The root itself may live on a mounted checkout filesystem. Directory and file count,
    total regular-file bytes, and nesting are capped before data is materialized. Destination ownership
    and ownership/modes are assigned only to the fresh copy; the caller's checkout is untouched. The
    worker group receives read/search access to parse source inputs; its systemd unit mounts the copy
    read-only, and no other user receives those permissions.
    """
    if not source.is_absolute() or not destination.is_absolute():
        raise LauncherError("subject-copy-path-invalid")
    if roots_overlap(source.resolve(strict=True), destination.resolve(strict=False)):
        raise LauncherError("subject-copy-path-overlap")
    destination.mkdir(mode=0o700)
    destination_fd = -1
    root_fd = -1
    source_fd = -1
    state = {"entries": 0, "bytes": 0}

    def copy_contents(source_dir_fd: int, destination_dir_fd: int, depth: int) -> None:
        if depth > MAX_SUBJECT_COPY_DEPTH:
            raise LauncherError("subject-copy-depth-limit")
        source_dir_before = os.fstat(source_dir_fd)
        if not stat.S_ISDIR(source_dir_before.st_mode):
            raise LauncherError("subject-entry-invalid")
        for name in sorted(os.listdir(source_dir_fd)):
            state["entries"] += 1
            if state["entries"] > MAX_SUBJECT_COPY_ENTRIES:
                raise LauncherError("subject-copy-entry-limit")
            entry = os.stat(name, dir_fd=source_dir_fd, follow_symlinks=False)
            if stat.S_ISDIR(entry.st_mode):
                if depth >= MAX_SUBJECT_COPY_DEPTH:
                    raise LauncherError("subject-copy-depth-limit")
                child_source_fd = openat2(source_dir_fd, name, os.O_RDONLY | os.O_DIRECTORY)
                child_destination_fd = -1
                try:
                    if _filesystem_identity(os.fstat(child_source_fd)) != _filesystem_identity(entry):
                        raise LauncherError("subject-changed-during-copy")
                    os.mkdir(name, 0o700, dir_fd=destination_dir_fd)
                    child_destination_fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW,
                                                   dir_fd=destination_dir_fd)
                    copy_contents(child_source_fd, child_destination_fd, depth + 1)
                    if _filesystem_identity(os.fstat(child_source_fd)) != _filesystem_identity(entry):
                        raise LauncherError("subject-changed-during-copy")
                    os.fchown(child_destination_fd, subject_uid, worker_gid)
                    os.fchmod(child_destination_fd, 0o750)
                finally:
                    os.close(child_source_fd)
                    if child_destination_fd >= 0:
                        os.close(child_destination_fd)
            elif stat.S_ISREG(entry.st_mode):
                source_file_fd = openat2(source_dir_fd, name, os.O_RDONLY | os.O_NONBLOCK)
                destination_file_fd = -1
                try:
                    before = os.fstat(source_file_fd)
                    if (not stat.S_ISREG(before.st_mode)
                            or _filesystem_identity(before) != _filesystem_identity(entry)):
                        raise LauncherError("subject-changed-during-copy")
                    if before.st_size < 0 or state["bytes"] + before.st_size > MAX_SUBJECT_COPY_BYTES:
                        raise LauncherError("subject-copy-byte-limit")
                    destination_file_fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW,
                                                  0o600, dir_fd=destination_dir_fd)
                    copied = 0
                    while copied < before.st_size:
                        block = os.pread(source_file_fd, min(1024 * 1024, before.st_size - copied), copied)
                        if not block:
                            raise LauncherError("subject-changed-during-copy")
                        view = memoryview(block)
                        while view:
                            written = os.write(destination_file_fd, view)
                            if written <= 0:
                                raise OSError("subject-copy-write-failed")
                            view = view[written:]
                        copied += len(block)
                    after = os.fstat(source_file_fd)
                    named_after = os.stat(name, dir_fd=source_dir_fd, follow_symlinks=False)
                    if (_filesystem_identity(after) != _filesystem_identity(before)
                            or _filesystem_identity(named_after) != _filesystem_identity(before)):
                        raise LauncherError("subject-changed-during-copy")
                    os.fchown(destination_file_fd, subject_uid, worker_gid)
                    os.fchmod(destination_file_fd, 0o750 if before.st_mode & 0o111 else 0o640)
                    state["bytes"] += copied
                finally:
                    os.close(source_file_fd)
                    if destination_file_fd >= 0:
                        os.close(destination_file_fd)
            else:
                raise LauncherError("subject-entry-invalid")
        if _filesystem_identity(os.fstat(source_dir_fd)) != _filesystem_identity(source_dir_before):
            raise LauncherError("subject-changed-during-copy")

    try:
        root_fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        source_relative = source.resolve(strict=True).relative_to(Path("/")).as_posix()
        source_fd = openat2(root_fd, source_relative, os.O_RDONLY | os.O_DIRECTORY,
                            RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS)
        destination_fd = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC | os.O_NOFOLLOW)
        copy_contents(source_fd, destination_fd, 0)
        os.fchown(destination_fd, subject_uid, worker_gid)
        os.fchmod(destination_fd, 0o750)
        return destination
    except BaseException:
        if destination_fd >= 0:
            os.close(destination_fd)
            destination_fd = -1
        shutil.rmtree(destination, ignore_errors=True)
        raise
    finally:
        for fd in (source_fd, root_fd, destination_fd):
            if fd >= 0:
                try:
                    os.close(fd)
                except OSError:
                    pass


def validate_budgets(args: argparse.Namespace) -> dict[str, int]:
    """Validate stage ceilings; stopping is reserved within cleanup, not added twice."""
    budgets = {name: getattr(args, name) for name in STAGE_LIMITS}
    for name, maximum in STAGE_LIMITS.items():
        if type(budgets[name]) is not int or not 1 <= budgets[name] <= maximum:
            raise LauncherError("invalid-stage-budget")
    if budgets["stopping_seconds"] > budgets["cleanup_seconds"]:
        raise LauncherError("stopping-budget-exceeds-cleanup")
    minimum = budgets["admission_seconds"] + budgets["collection_seconds"] + budgets["cleanup_seconds"]
    if args.job_seconds < minimum:
        raise LauncherError("job-deadline-below-stage-budget-sum")
    return budgets


def job_remaining_seconds(deadline: float, original_seconds: int, now: float | None = None) -> float:
    """Return a positive frozen-deadline allowance that can never exceed the launch allowance."""
    if type(original_seconds) is not int or original_seconds <= 0:
        raise LauncherError("invalid-job-deadline")
    current = time.monotonic() if now is None else now
    remaining = deadline - current
    if remaining <= 0:
        raise LauncherError("job-deadline-expired")
    return min(float(original_seconds), remaining)


def ready_response(descriptor: dict, deadline: float, original_seconds: int,
                   now: float | None = None) -> dict:
    """Build ready facts with a conservative live allowance outside the immutable descriptor."""
    return {"ok": True, "descriptor": descriptor,
            "job_remaining_seconds": job_remaining_seconds(deadline, original_seconds, now)}


def validate_args(args: argparse.Namespace, *, root_uid: int = 0) -> tuple[Path, Path, Path, Path]:
    """Validate launcher inputs without following worker-controlled paths."""
    if args.mode not in ("trusted", "observation"):
        raise LauncherError("invalid-mode")
    if not 1 <= args.job_seconds <= 3600:
        raise LauncherError("invalid-job-deadline")
    if not SAFE_NAME.fullmatch(args.output_slot):
        raise LauncherError("invalid-output-slot")
    tool = nofollow_absolute_path(Path(args.tool_root))
    subject = Path(args.subject_root)
    policy = Path(args.policy_file)
    parent = Path(args.output_parent)
    for p in (tool, subject, policy, parent):
        if not p.is_absolute() or ".." in p.parts or any(c.isspace() for c in str(p)):
            raise LauncherError("path-must-be-absolute-and-normalized")
    tool = tool.resolve(strict=True)
    subject = subject.resolve(strict=True)
    policy = policy.resolve(strict=True)
    parent = parent.resolve(strict=True)
    if not policy.is_relative_to(tool):
        raise LauncherError("policy-outside-protected-tool-root")
    if not tool.is_dir() or tool.stat().st_uid != root_uid or tool.stat().st_mode & 0o022:
        raise LauncherError("tool-root-not-protected")
    if not subject.is_dir() or not policy.is_file() or policy.stat().st_uid != root_uid or policy.stat().st_mode & 0o022:
        raise LauncherError("subject-or-policy-invalid")
    if not parent.is_dir() or parent.stat().st_uid != root_uid or parent.stat().st_mode & 0o022:
        raise LauncherError("output-parent-not-protected")
    for index, first in enumerate((tool, subject, parent)):
        if any(roots_overlap(first, second) for second in (tool, subject, parent)[index + 1:]):
            raise LauncherError("protected-roots-overlap")
    return tool, subject, policy, parent


def validate_request(raw: bytes) -> dict:
    """Decode bounded JSONL, reject duplicate/case-colliding keys, and type-check before dispatch."""
    if not raw or len(raw) > MAX_REQUEST or not raw.endswith(b"\n"):
        raise LauncherError("invalid-request-size")

    def unique_fields(pairs):
        result = {}
        folded = set()
        for key, item in pairs:
            casefolded = key.casefold()
            if key in result or casefolded in folded:
                raise LauncherError("duplicate-request-field")
            result[key] = item
            folded.add(casefolded)
        return result

    try:
        value = json.loads(raw[:-1], object_pairs_hook=unique_fields)
    except LauncherError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise LauncherError("invalid-request-json") from None
    if not isinstance(value, dict) or not isinstance(value.get("op"), str):
        raise LauncherError("invalid-request-shape")
    op = value["op"]
    fields = {"ready": {"op"}, "stop": {"op"}, "wait": {"op"}, "exit": {"op"},
              "run": {"op", "executable", "arguments", "working_directory"},
              "artifacts": {"op", "relative_root"},
              "artifact": {"op", "relative_root", "relative_path", "offset"}}
    if op not in fields or set(value) != fields[op]:
        raise LauncherError("invalid-request-fields")
    if op == "run":
        if (not isinstance(value["executable"], str) or "\0" in value["executable"]
                or not isinstance(value["arguments"], list) or len(value["arguments"]) > 4096):
            raise LauncherError("invalid-run-command")
        if not value["arguments"] or any(not isinstance(x, str) or "\0" in x for x in value["arguments"]):
            raise LauncherError("invalid-run-arguments")
        if (not isinstance(value["working_directory"], str)
                or "\0" in value["working_directory"]):
            raise LauncherError("invalid-working-directory")
    if op == "artifacts":
        if not isinstance(value["relative_root"], str) or not SAFE_NAME.fullmatch(value["relative_root"]):
            raise LauncherError("invalid-artifact-root")
    if op == "artifact":
        if not isinstance(value["relative_root"], str) or not SAFE_NAME.fullmatch(value["relative_root"]):
            raise LauncherError("invalid-artifact-root")
        if not isinstance(value["relative_path"], str):
            raise LauncherError("invalid-artifact-path")
        validate_artifact_relative_path(value["relative_path"])
        if type(value["offset"]) is not int or not 0 <= value["offset"] <= MAX_ARTIFACT_FILE_BYTES:
            raise LauncherError("invalid-artifact-offset")
    return value


def validate_artifact_relative_path(value: str) -> str:
    """Require a normalized UTF-8 relative file path with no empty, dot, or parent segments."""
    if not isinstance(value, str) or not value or value.startswith("/") or "\\" in value or "\0" in value:
        raise LauncherError("invalid-artifact-path")
    try:
        if len(value.encode("utf-8", "strict")) > MAX_ARTIFACT_PATH_BYTES:
            raise LauncherError("invalid-artifact-path")
    except UnicodeEncodeError:
        raise LauncherError("invalid-artifact-path") from None
    parts = value.split("/")
    if any(not part or part in (".", "..") or len(os.fsencode(part)) > 255 for part in parts):
        raise LauncherError("invalid-artifact-path")
    return value


def peer_credentials(conn: socket.socket) -> tuple[int, int, int]:
    """Return Linux SO_PEERCRED (pid, uid, gid), isolated for unit testing."""
    return parse_peer_credentials(conn.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i")))


def parse_peer_credentials(raw: bytes) -> tuple[int, int, int]:
    """Decode the fixed Linux ``ucred`` payload returned by SO_PEERCRED."""
    if len(raw) != struct.calcsize("3i"):
        raise LauncherError("invalid-peer-credentials")
    return struct.unpack("3i", raw)


def peer_is_worker(raw: bytes, worker_pid: int, worker_uid: int, worker_gid: int) -> bool:
    """Authorize the exact pinned worker PID, UID and GID on every connection."""
    pid, uid, gid = parse_peer_credentials(raw)
    return pid == worker_pid and uid == worker_uid and gid == worker_gid


def capture_job_deadline(job_seconds: int) -> tuple[str, float]:
    """Freeze wall and monotonic deadlines once, before starting the worker unit."""
    wall_started = time.time()
    monotonic_started = time.monotonic()
    return (time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(wall_started + job_seconds)),
            monotonic_started + job_seconds)


def worker_unit_properties(worker_name: str, tool_root: Path, subject_root: Path,
                           test_output_root: Path, output_parent: Path,
                           job_seconds: int) -> dict[str, str]:
    """Expose tooling and the source map read-only without granting raw artifact access."""
    return {
        "User": worker_name, "Group": worker_name, "Type": "exec",
        "KillMode": "control-group", "RuntimeMaxSec": str(job_seconds), "TimeoutStopSec": "2",
        "SendSIGKILL": "yes", "NoNewPrivileges": "yes", "CapabilityBoundingSet": "",
        "AmbientCapabilities": "", "ProtectControlGroups": "yes", "RestrictSUIDSGID": "yes",
        "PrivateTmp": "yes", "ProtectSystem": "strict", "ProtectHome": "yes", "LimitCORE": "0",
        "TasksMax": "64", "MemoryMax": "1G", "Restart": "no", "RemainAfterExit": "yes",
        "ReadOnlyPaths": f"{tool_root} {subject_root}", "ReadWritePaths": str(output_parent),
        "InaccessiblePaths": str(test_output_root),
    }


def prepare_scratch_layout(scratch: Path, subject_uid: int, subject_gid: int,
                           worker_gid: int, results_gid: int) -> None:
    """Set separate subject caches and results permissions beneath a worker-traversable scratch root."""
    for child in ("dotnet", "nuget", "test-output"):
        path = scratch / child
        path.mkdir(mode=0o700)
        os.chown(path, subject_uid, results_gid if child == "test-output" else subject_gid)
        os.chmod(path, 0o2770 if child == "test-output" else 0o700)
    os.chown(scratch, subject_uid, worker_gid)
    os.chmod(scratch, 0o710)


def encode_response(obj: dict) -> bytes:
    """Serialize a single JSON line within the parent protocol's 3 MiB cap."""
    limit = 3 * 1024 * 1024
    response = dict(obj)
    data = json.dumps(response, separators=(",", ":")).encode() + b"\n"
    while len(data) > limit and ("stdout" in response or "stderr" in response):
        for name in ("stdout", "stderr"):
            value = response.get(name)
            if isinstance(value, str): response[name] = value[:len(value) // 2]
        response["output_truncated"] = True
        data = json.dumps(response, separators=(",", ":")).encode() + b"\n"
    if len(data) > limit:
        return b'{"ok":false,"error":"broker-response-too-large"}\n'
    return data


class OutputQuota:
    """One received-byte quota shared by every subject command in a job."""
    def __init__(self):
        self.total = 0
        self.exceeded = threading.Event()
        self._lock = threading.Lock()

    def count(self, size: int) -> None:
        with self._lock:
            self.total += size
            if self.total > MAX_JOB_OUTPUT:
                self.exceeded.set()


class OutputBudget:
    """Per-command prefixes backed by the broker's job-wide received-byte quota."""
    def __init__(self, quota: OutputQuota | None = None):
        self.quota = quota or OutputQuota()
        self.total = 0
        self.stdout = bytearray()
        self.stderr = bytearray()
        self._lock = threading.Lock()

    def add(self, stream: str, chunk: bytes) -> None:
        with self._lock:
            self.total += len(chunk)
            self.quota.count(len(chunk))
            target = self.stdout if stream == "stdout" else self.stderr
            target.extend(chunk[:max(0, MAX_PREFIX - len(target))])

    @property
    def truncated(self) -> bool:
        return self.total > len(self.stdout) + len(self.stderr)

    @property
    def exceeded(self):
        return self.quota.exceeded


class Broker:
    def __init__(self, descriptor: dict, worker_uid: int, worker_pid: int, worker_gid: int, subject_uid: int,
                 subject_gid: int, results_gid: int, subject_root: Path, protected_paths: tuple[Path, ...],
                 scratch: Path, dotnet: Path, unit_prefix: str, deadline: float,
                 job_seconds: int, stopping_seconds: int, cleanup_seconds: int,
                 test_output_fd: int):
        self.descriptor = descriptor
        self.worker_uid = worker_uid
        self.worker_pid = worker_pid
        self.worker_gid = worker_gid
        self.subject_uid = subject_uid
        self.subject_gid = subject_gid
        self.results_gid = results_gid
        self.subject_root = subject_root
        self.protected_paths = protected_paths
        self.scratch = scratch
        self.dotnet = dotnet
        self.unit_prefix = unit_prefix
        self.deadline = deadline
        self.job_seconds = job_seconds
        self.stopping_seconds = stopping_seconds
        self.cleanup_seconds = cleanup_seconds
        self.test_output_fd = test_output_fd
        self.units: list[tuple[str, str]] = []
        self.output_quota = OutputQuota()
        self.lock = threading.Lock()
        self.condition = threading.Condition(self.lock)
        self.exited = False
        self.ready_seen = False
        self.active_runs = 0
        self.run_change_generation = 0
        self.active_artifact_operations = 0
        self.active_handlers = 0
        self.work_closed = False
        self.wait_completed = False
        self.artifact_handles: dict[tuple[str, str], ArtifactHandle] = {}
        self.artifact_handles_closing = False
        self.artifact_transfer_lock = threading.Lock()
        self.artifact_count = 0
        self.artifact_bytes = 0
        self.artifact_roots: set[str] = set()
        self.allowed_results_roots: set[str] = set()

    def _send(self, conn: socket.socket, obj: dict) -> None:
        conn.sendall(encode_response(obj))

    def _unit_properties(self, unit: str, timeout: float = 5) -> dict[str, str]:
        keys = ("ActiveState", "ControlGroup", "User", "Group", "KillMode", "Result", "ExecMainCode", "ExecMainStatus")
        result = subprocess.run(["systemctl", "show", unit, "--no-pager", *[f"--property={k}" for k in keys]],
                                capture_output=True, env=ENV, timeout=timeout, check=False)
        if result.returncode:
            raise LauncherError("unit-inspection-failed")
        return dict(line.split("=", 1) for line in result.stdout.decode(errors="replace").splitlines() if "=" in line)

    def _group_empty(self, group: str) -> bool:
        if not group.startswith("/system.slice/") or ".." in group:
            raise LauncherError("unexpected-control-group")
        cg = Path("/sys/fs/cgroup") / group.lstrip("/")
        if not cg.exists():
            return True
        events = dict(line.split() for line in (cg / "cgroup.events").read_text().splitlines())
        return events.get("populated") == "0"

    def _validate_results_root(self, arguments: list[str]) -> str:
        """Require a single existing results directory, securely opened below test-output."""
        relative_root = validate_test_results_path(self.scratch, dotnet_test_results_argument(arguments))
        try:
            os.mkdir(relative_root, 0o2770, dir_fd=self.test_output_fd)
        except FileExistsError:
            pass
        root_fd = openat2(self.test_output_fd, relative_root, os.O_RDONLY | os.O_DIRECTORY)
        try:
            root_stat = os.fstat(root_fd)
            if (not stat.S_ISDIR(root_stat.st_mode) or root_stat.st_uid not in (self.worker_uid, self.subject_uid)
                    or root_stat.st_gid != self.results_gid):
                raise LauncherError("test-results-root-invalid")
            os.fchown(root_fd, self.subject_uid, self.results_gid)
            os.fchmod(root_fd, 0o2770)
        finally:
            os.close(root_fd)
        return relative_root

    def _root_descriptor(self, relative_root: str) -> tuple[int, os.stat_result]:
        """Pin a launched test result directory beneath the broker-created test-output root."""
        if not SAFE_NAME.fullmatch(relative_root):
            raise LauncherError("invalid-artifact-root")
        with self.lock:
            if relative_root not in self.allowed_results_roots:
                raise LauncherError("artifact-root-not-launched")
        root_fd = openat2(self.test_output_fd, relative_root, os.O_RDONLY | os.O_DIRECTORY)
        try:
            root_stat = os.fstat(root_fd)
            if (not stat.S_ISDIR(root_stat.st_mode) or root_stat.st_uid != self.subject_uid
                    or root_stat.st_gid != self.results_gid):
                raise LauncherError("artifact-root-invalid")
            return root_fd, root_stat
        except BaseException:
            os.close(root_fd)
            raise

    def _enumerate_artifacts(self, root_fd: int, relative_root: str,
                             root_stat: os.stat_result) -> list[ArtifactHandle]:
        """Open every bounded regular artifact without following links and retain each descriptor."""
        handles: list[ArtifactHandle] = []
        total_bytes = 0
        entries_seen = 0

        def visit(directory_fd: int, prefix: str, depth: int) -> None:
            nonlocal entries_seen, total_bytes
            if depth > MAX_ARTIFACT_TREE_DEPTH:
                raise LauncherError("artifact-tree-depth-limit")
            directory_before = os.fstat(directory_fd)
            for name in sorted(os.listdir(directory_fd)):
                entries_seen += 1
                if entries_seen > MAX_ARTIFACT_TREE_ENTRIES:
                    raise LauncherError("artifact-tree-entry-limit")
                relative_path = f"{prefix}/{name}" if prefix else name
                validate_artifact_relative_path(relative_path)
                entry = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
                if stat.S_ISDIR(entry.st_mode):
                    child_fd = openat2(directory_fd, name, os.O_RDONLY | os.O_DIRECTORY)
                    named_child_fd = -1
                    try:
                        opened = os.fstat(child_fd)
                        if (_filesystem_identity(opened) != _filesystem_identity(entry)
                                or opened.st_uid != self.subject_uid or opened.st_gid != self.results_gid):
                            raise LauncherError("artifact-directory-invalid")
                        named_child_fd = openat2(root_fd, relative_path, O_PATH | os.O_DIRECTORY)
                        if _filesystem_identity(os.fstat(named_child_fd)) != _filesystem_identity(opened):
                            raise LauncherError("artifact-directory-name-changed")
                        visit(child_fd, relative_path, depth + 1)
                        if _filesystem_identity(os.fstat(child_fd)) != _filesystem_identity(entry):
                            raise LauncherError("artifact-directory-changed")
                    finally:
                        os.close(child_fd)
                        if named_child_fd >= 0:
                            os.close(named_child_fd)
                    continue
                if not stat.S_ISREG(entry.st_mode):
                    raise LauncherError("artifact-file-not-regular")
                fd = openat2(root_fd, relative_path, os.O_RDONLY | os.O_NONBLOCK)
                try:
                    opened = os.fstat(fd)
                    if (not stat.S_ISREG(opened.st_mode) or opened.st_nlink != 1
                            or opened.st_uid != self.subject_uid or opened.st_gid != self.results_gid
                            or _filesystem_identity(opened) != _filesystem_identity(entry)):
                        raise LauncherError("artifact-file-invalid")
                    if opened.st_size < 0 or opened.st_size > MAX_ARTIFACT_FILE_BYTES:
                        raise LauncherError("artifact-file-size-limit")
                    if len(handles) >= MAX_ARTIFACT_FILES or total_bytes + opened.st_size > MAX_ARTIFACT_TOTAL_BYTES:
                        raise LauncherError("artifact-total-limit")
                    handles.append(ArtifactHandle(fd, relative_root, relative_path, opened.st_size,
                                                  _filesystem_identity(opened), _filesystem_identity(root_stat)))
                    fd = -1
                    total_bytes += opened.st_size
                finally:
                    if fd >= 0:
                        os.close(fd)
            if _filesystem_identity(os.fstat(directory_fd)) != _filesystem_identity(directory_before):
                raise LauncherError("artifact-directory-changed")

        try:
            visit(root_fd, "", 0)
            if _filesystem_identity(os.fstat(root_fd)) != _filesystem_identity(root_stat):
                raise LauncherError("artifact-root-changed")
            named_root_fd = openat2(self.test_output_fd, relative_root, O_PATH | os.O_DIRECTORY)
            try:
                if _filesystem_identity(os.fstat(named_root_fd)) != _filesystem_identity(root_stat):
                    raise LauncherError("artifact-root-name-changed")
            finally:
                os.close(named_root_fd)
            return handles
        except BaseException:
            for handle in handles:
                os.close(handle.fd)
            raise

    def _list_artifacts(self, relative_root: str) -> dict:
        """Publish bounded metadata only after owned work and every subject cgroup are empty."""
        self._begin_artifact_operation()
        try:
            with self.artifact_transfer_lock:
                root_fd, root_stat = self._root_descriptor(relative_root)
                try:
                    handles = self._enumerate_artifacts(root_fd, relative_root, root_stat)
                    with self.lock:
                        if (self.artifact_handles_closing or self.artifact_count + len(handles) > MAX_ARTIFACT_FILES
                                or self.artifact_bytes + sum(handle.length_bytes for handle in handles) > MAX_ARTIFACT_TOTAL_BYTES
                                or relative_root in self.artifact_roots):
                            raise LauncherError("artifact-total-limit")
                        self.artifact_count += len(handles)
                        self.artifact_bytes += sum(handle.length_bytes for handle in handles)
                        self.artifact_roots.add(relative_root)
                        for handle in handles:
                            self.artifact_handles[(relative_root, handle.relative_path)] = handle
                    return {"ok": True, "artifacts": [
                        {"path": handle.relative_path, "length_bytes": handle.length_bytes}
                        for handle in handles]}
                except BaseException:
                    # Handles transferred into the broker table remain owned there.
                    with self.lock:
                        retained = {id(handle) for handle in self.artifact_handles.values()}
                    for handle in locals().get("handles", []):
                        if id(handle) not in retained:
                            os.close(handle.fd)
                    raise
                finally:
                    os.close(root_fd)
        finally:
            self._end_artifact_operation()

    def _artifact_identity_unchanged(self, handle: ArtifactHandle) -> None:
        """Reopen only the pathname under its pinned root and compare it with the held descriptor."""
        current = os.fstat(handle.fd)
        if (_filesystem_identity(current) != handle.identity or current.st_nlink != 1
                or current.st_uid != self.subject_uid or current.st_gid != self.results_gid):
            raise LauncherError("artifact-descriptor-changed")
        root_fd, root_stat = self._root_descriptor(handle.relative_root)
        path_fd = -1
        try:
            if _filesystem_identity(root_stat) != handle.root_identity:
                raise LauncherError("artifact-root-changed")
            path_fd = openat2(root_fd, handle.relative_path, O_PATH)
            named = os.fstat(path_fd)
            if (_filesystem_identity(named) != handle.identity or named.st_nlink != 1
                    or named.st_uid != self.subject_uid or named.st_gid != self.results_gid):
                raise LauncherError("artifact-name-changed")
        finally:
            if path_fd >= 0:
                os.close(path_fd)
            os.close(root_fd)

    def _read_artifact(self, relative_root: str, relative_path: str, offset: int) -> dict:
        """Return the next sequential base64 chunk after before/after identity checks."""
        self._begin_artifact_operation()
        try:
            with self.artifact_transfer_lock:
                key = (relative_root, relative_path)
                with self.lock:
                    handle = self.artifact_handles.get(key)
                    closing = self.artifact_handles_closing
                if handle is None or closing or handle.ended or offset != handle.next_offset:
                    raise LauncherError("artifact-read-state-invalid")
                if offset > handle.length_bytes:
                    raise LauncherError("artifact-offset-out-of-range")
                self._artifact_identity_unchanged(handle)
                count = min(MAX_ARTIFACT_CHUNK_BYTES, handle.length_bytes - offset)
                data = os.pread(handle.fd, count, offset)
                if len(data) != count:
                    raise LauncherError("artifact-short-read")
                self._artifact_identity_unchanged(handle)
                handle.next_offset += len(data)
                end = handle.next_offset == handle.length_bytes
                handle.ended = end
                return {"ok": True, "bytes_base64": base64.b64encode(data).decode("ascii"), "end": end}
        finally:
            self._end_artifact_operation()

    def close_artifact_handles(self) -> None:
        """Close retained descriptors only after all accepted broker handlers have completed."""
        with self.condition:
            self.artifact_handles_closing = True
            while self.active_handlers or self.active_artifact_operations:
                self.condition.wait(timeout=0.1)
        with self.artifact_transfer_lock:
            with self.condition:
                handles = list(self.artifact_handles.values())
                self.artifact_handles.clear()
                test_output_fd = self.test_output_fd
                self.test_output_fd = -1
            for handle in handles:
                os.close(handle.fd)
            if test_output_fd >= 0:
                os.close(test_output_fd)

    def _run(self, request: dict) -> dict:
        executable = Path(request["executable"])
        if not executable.is_absolute() or executable.resolve() != self.dotnet:
            raise LauncherError("executable-not-approved")
        if request["arguments"][0] not in ALLOWED_VERBS:
            raise LauncherError("dotnet-verb-not-approved")
        result_root = (self._validate_results_root(request["arguments"])
                       if request["arguments"][0] == "test" else None)
        cwd = Path(request["working_directory"]).resolve(strict=True)
        if not cwd.is_dir() or not cwd.is_relative_to(self.subject_root):
            raise LauncherError("working-directory-outside-subject")
        with self.lock:
            if time.monotonic() >= self.deadline or self.exited or self.work_closed:
                raise LauncherError("job-not-active")
            if len(self.units) >= MAX_SUBJECT_UNITS:
                raise LauncherError("subject-command-limit")
            if self.active_runs > 1:
                raise LauncherError("concurrent-subject-run-not-supported")
            if self.active_artifact_operations:
                raise LauncherError("artifact-transfer-active")
            unit = f"{self.unit_prefix}-s-{len(self.units)}.service"
            self.units.append((unit, ""))
        argv = ["systemd-run", "--quiet", "--wait", "--pipe", "--expand-environment=no",
                f"--unit={unit}", *[f"--property={k}={v}" for k, v in {
                    "User": str(self.subject_uid), "Group": str(self.subject_gid),
                    "SupplementaryGroups": str(self.results_gid), "Type": "exec",
                    "KillMode": "control-group", "RuntimeMaxSec": str(max(1, int(self.deadline-time.monotonic()))),
                    "TimeoutStopSec": "1", "SendSIGKILL": "yes", "NoNewPrivileges": "yes",
                    "CapabilityBoundingSet": "", "AmbientCapabilities": "", "ProtectControlGroups": "yes",
                    "RestrictSUIDSGID": "yes", "PrivateTmp": "yes", "ProtectSystem": "strict",
                    "ProtectHome": "yes", "LimitCORE": "0", "TasksMax": "64", "MemoryMax": "1G",
                    "Restart": "no", "RemainAfterExit": "yes", "WorkingDirectory": str(cwd),
                    "InaccessiblePaths": " ".join(str(path) for path in self.protected_paths),
                    "ReadWritePaths": f"{self.subject_root} {self.scratch}"}.items()],
                "/usr/bin/env", "-i", *[f"{key}={value}" for key, value in {
                    **ENV, "DOTNET_CLI_HOME": str(self.scratch / "dotnet"),
                    "NUGET_PACKAGES": str(self.scratch / "nuget"),
                    "EVIDENCE_TEST_OUTPUT_ROOT": str(self.scratch / "test-output")}.items()],
                str(self.dotnet), *request["arguments"]]
        with self.lock:
            if self.work_closed:
                raise LauncherError("job-not-active")
            proc = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                    env=ENV, close_fds=True)
        budget = OutputBudget(self.output_quota)
        def pump(name: str, stream):
            while True:
                block = stream.read(65536)
                if not block:
                    break
                budget.add(name, block)
                if budget.exceeded.is_set():
                    self.stop()
        pumps = [threading.Thread(target=pump, args=("stdout", proc.stdout), daemon=True),
                 threading.Thread(target=pump, args=("stderr", proc.stderr), daemon=True)]
        for thread in pumps: thread.start()
        try:
            proc.wait(timeout=max(1, self.deadline-time.monotonic()) + 3)
        except subprocess.TimeoutExpired:
            self.stop()
            try:
                proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait(timeout=1)
        for thread in pumps: thread.join(timeout=3)
        props = self._unit_properties(unit)
        group = props.get("ControlGroup", "")
        with self.lock:
            self.units = [(name, group if name == unit else known) for name, known in self.units]
        if (props.get("User") != str(self.subject_uid) or props.get("KillMode") != "control-group"
                or any(thread.is_alive() for thread in pumps) or not self._group_empty(group)):
            self.stop()
            raise LauncherError("subject-exit-unconfirmed")
        if budget.exceeded.is_set():
            raise LauncherError("ASEVD420")
        if result_root is not None:
            with self.lock:
                self.allowed_results_roots.add(result_root)
        return {"exit_code": proc.returncode, "stdout": bytes(budget.stdout).decode("utf-8", "replace"),
                "stderr": bytes(budget.stderr).decode("utf-8", "replace"),
                "output_truncated": budget.truncated, "received_bytes": budget.total}

    def stop(self) -> None:
        with self.lock:
            self.work_closed = True
            units = [unit for unit, _ in self.units]
        if units:
            subprocess.run(["systemctl", "stop", *units], capture_output=True, env=ENV, timeout=5, check=False)

    def _all_subject_groups_empty(self, inspection_deadline: float | None = None) -> bool:
        with self.lock:
            units = list(self.units)
            if self.active_runs:
                return False
        for unit, group in units:
            if not group:
                timeout = 5
                if inspection_deadline is not None:
                    timeout = min(timeout, inspection_deadline - time.monotonic())
                    if timeout <= 0:
                        raise LauncherError("owned-exit-inspection-deadline")
                props = self._unit_properties(unit, timeout=timeout)
                group = props.get("ControlGroup", "")
                if not group:
                    if props.get("ActiveState") in ("active", "activating", "deactivating"):
                        return False
                    continue
                with self.lock:
                    self.units = [(name, group if name == unit else known) for name, known in self.units]
            if not self._group_empty(group):
                return False
        return True

    def _wait_for_owned_exit(self) -> bool:
        """Wait for run handlers and all owned cgroups within stopping, cleanup, and job reserves."""
        started = time.monotonic()
        grace = min(self.stopping_seconds, self.cleanup_seconds)
        wait_deadline = min(self.deadline, started + grace)
        while True:
            with self.condition:
                generation = self.run_change_generation
                active = self.active_runs
            if not active:
                try:
                    if (self._all_subject_groups_empty(inspection_deadline=wait_deadline)
                            and time.monotonic() <= wait_deadline):
                        return True
                except (LauncherError, OSError, subprocess.SubprocessError):
                    # Inspection failures consume the same finite ownership grace.
                    pass
            remaining = wait_deadline - time.monotonic()
            if remaining <= 0:
                return False
            with self.condition:
                if self.run_change_generation != generation:
                    continue
                self.condition.wait(timeout=min(0.1, remaining))

    def _begin_artifact_operation(self) -> None:
        """Reserve an idle interval so no new subject run can race artifact enumeration or reading."""
        with self.condition:
            if self.artifact_handles_closing or self.active_runs:
                raise LauncherError("subject-work-active")
            self.active_artifact_operations += 1
        if not self._all_subject_groups_empty():
            self._end_artifact_operation()
            raise LauncherError("subject-exit-unconfirmed")

    def _end_artifact_operation(self) -> None:
        with self.condition:
            self.active_artifact_operations -= 1
            self.condition.notify_all()

    def handle(self, conn: socket.socket) -> None:
        with self.condition:
            self.active_handlers += 1
        try:
            credentials = conn.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, struct.calcsize("3i"))
            if not peer_is_worker(credentials, self.worker_pid, self.worker_uid, self.worker_gid):
                raise LauncherError("peer-identity-mismatch")
            conn.settimeout(5)
            raw = bytearray()
            while len(raw) <= MAX_REQUEST:
                part = conn.recv(min(8192, MAX_REQUEST + 1 - len(raw)))
                if not part: break
                raw.extend(part)
                if b"\n" in raw: break
            req = validate_request(bytes(raw))
            conn.settimeout(None)
            op = req["op"]
            if op == "ready":
                with self.condition:
                    if self.work_closed or self.exited:
                        raise LauncherError("worker-lease-closed")
                    response = ready_response(self.descriptor, self.deadline, self.job_seconds)
                    self.ready_seen = True
            elif op == "run":
                with self.condition:
                    if self.work_closed or self.exited:
                        raise LauncherError("subject-work-closed")
                    if self.active_runs:
                        raise LauncherError("concurrent-subject-run-not-supported")
                    if self.active_artifact_operations:
                        raise LauncherError("artifact-transfer-active")
                    self.active_runs += 1
                    self.run_change_generation += 1
                try: response = {"ok": True, **self._run(req)}
                finally:
                    with self.condition:
                        self.active_runs -= 1
                        self.run_change_generation += 1
                        self.condition.notify_all()
            elif op == "stop":
                with self.condition:
                    self.work_closed = True
                    self.condition.notify_all()
                self.stop()
                response = {"ok": True}
            elif op == "wait":
                with self.condition:
                    self.work_closed = True
                    self.condition.notify_all()
                owned_exit = self._wait_for_owned_exit()
                if owned_exit:
                    self.wait_completed = True
                response = {"ok": True, "owned_exit": owned_exit}
            elif op == "artifacts":
                response = self._list_artifacts(req["relative_root"])
            elif op == "artifact":
                response = self._read_artifact(req["relative_root"], req["relative_path"], req["offset"])
            else:  # exit
                with self.lock:
                    self.work_closed = True
                    wait_completed = self.wait_completed
                if not wait_completed or not self._all_subject_groups_empty():
                    raise LauncherError("subject-exit-unconfirmed")
                self.exited = True
                response = {"ok": True}
            self._send(conn, response)
        except (LauncherError, OSError, subprocess.SubprocessError, ValueError) as error:
            try: self._send(conn, {"ok": False, "code": "ASEVD420"} if str(error) == "ASEVD420"
                            else {"ok": False, "error": "broker-request-failed"})
            except OSError: pass
        finally:
            try:
                conn.close()
            except OSError:
                pass
            finally:
                with self.condition:
                    self.active_handlers -= 1
                    self.condition.notify_all()


def _systemd(argv: list[str], timeout: int = 8, *, check: bool = True) -> subprocess.CompletedProcess:
    """Run a fixed host command; spawning failures expose only a known operation and bounded errno."""
    operation = Path(argv[0]).name
    if operation not in FAILURE_DIAGNOSTIC_OPERATIONS:
        operation = None
    try:
        result = subprocess.run(argv, capture_output=True, env=ENV, timeout=timeout, check=False)
    except OSError as error:
        raise LauncherError("host-command-start-failed", operation=operation, errno=error.errno) from None
    if check and result.returncode:
        raise LauncherError("systemd-operation-failed", operation=Path(argv[0]).name, exit_code=result.returncode)
    return result


def _create_run_accounts(worker_name: str, subject_name: str, results_group: str,
                         users: list[str], groups: list[str]) -> None:
    """Use trusted absolute utilities and track each account only after successful creation."""
    for name in (worker_name, subject_name):
        _systemd(["/usr/sbin/useradd", "--system", "--user-group", "--no-create-home",
                  "--shell", "/usr/sbin/nologin", name])
        users.append(name)
    _systemd(["/usr/sbin/groupadd", "--system", results_group])
    groups.append(results_group)


def _delete_run_accounts(users: list[str], groups: list[str]) -> None:
    """Delete only this run's tracked accounts in reverse order, preserving best-effort exit handling."""
    for name in reversed(users):
        _systemd(["/usr/sbin/userdel", name], timeout=5, check=False)
    for name in reversed(groups):
        _systemd(["/usr/sbin/groupdel", name], timeout=5, check=False)


def launch(args: argparse.Namespace) -> Path:
    if sys.platform != "linux" or os.geteuid() != 0 or Path("/proc/1/comm").read_text().strip() != "systemd":
        raise LauncherError("requires-root-systemd-linux")
    ensure_openat2_supported()
    if not Path("/sys/fs/cgroup/cgroup.controllers").is_file():
        raise LauncherError("requires-cgroup-v2")
    version = _systemd(["systemctl", "--version"]).stdout.decode().splitlines()[0]
    m = re.search(r"systemd (\d+)", version)
    if not m or int(m.group(1)) < 255:
        raise LauncherError("requires-systemd-255")
    tool, subject, policy, parent = validate_args(args)
    diff_file, diff_sha256 = protected_diff_snapshot(tool, getattr(args, "diff_file", None))
    if not RUN_ID.fullmatch(args.run_id):
        raise LauncherError("invalid-ci-run-attempt")
    budgets = validate_budgets(args)
    if (not args.base_revision or not args.subject_revision or len(args.base_revision) > 256
            or len(args.subject_revision) > 256 or "\0" in args.base_revision + args.subject_revision):
        raise LauncherError("invalid-revision-input")
    if not args.workflow_identity or len(args.workflow_identity) > 256 or "\0" in args.workflow_identity:
        raise LauncherError("invalid-workflow-identity")
    if any(not SAFE_NAME.fullmatch(value) for value in args.observation_profile + args.observation_producer):
        raise LauncherError("invalid-observation-identifier")
    source_subject = subject
    solution, declared_paths = declared_subject_inputs(source_subject, args.solution, args.path)
    solution_relative = Path(solution).relative_to(source_subject)
    tag = uuid.uuid4().hex[:12]
    if args.mode == "trusted" and not trusted_proof_admitted(""):
        raise LauncherError("trusted-proof-not-allowlisted")
    output_parent = parent / f"run-{tag}"
    if output_parent.exists() or output_parent.is_symlink():
        raise LauncherError("output-anchor-not-fresh")
    output_parent.mkdir(mode=0o700)
    worker_name, subject_name = f"evw{tag}", f"evs{tag}"
    results_group = f"evr{tag}"
    units: list[str] = []
    root = Path(tempfile.mkdtemp(prefix=f"evidencehost-{tag}-", dir="/run"))
    os.chmod(root, 0o710)
    worker_socket_dir = root / "broker"
    worker_socket_dir.mkdir(mode=0o710)
    scratch = Path(tempfile.mkdtemp(prefix=f"evidencehost-subject-{tag}-", dir="/run"))
    os.chmod(scratch, 0o700)
    users: list[str] = []
    groups: list[str] = []
    worker_pid = 0
    exposed = False
    listener: socket.socket | None = None
    broker: Broker | None = None
    test_output_fd = -1
    handlers: list[threading.Thread] = []
    try:
        _create_run_accounts(worker_name, subject_name, results_group, users, groups)
        wu, su = pwd.getpwnam(worker_name), pwd.getpwnam(subject_name)
        rgid = grp.getgrnam(results_group).gr_gid
        if wu.pw_uid == su.pw_uid or not wu.pw_uid or not su.pw_uid: raise LauncherError("identity-separation-failed")
        os.chown(output_parent, wu.pw_uid, wu.pw_gid)
        os.chmod(output_parent, 0o700)
        prepare_scratch_layout(scratch, su.pw_uid, su.pw_gid, wu.pw_gid, rgid)
        prepared_subject = (scratch / "subject").resolve(strict=False)
        if any(roots_overlap(prepared_subject, protected)
               for protected in (tool, source_subject, output_parent)):
            raise LauncherError("prepared-subject-overlaps-protected-root")
        subject = _copy_subject_tree(source_subject, prepared_subject, su.pw_uid, su.pw_gid, wu.pw_gid)
        solution = str(subject / solution_relative)
        test_output_fd = open_test_output_root(scratch, su.pw_uid, rgid)
        os.chown(root, 0, wu.pw_gid)
        os.chmod(root, 0o710)
        os.chown(worker_socket_dir, 0, wu.pw_gid)
        prepare_tool_root(tool, wu.pw_gid)
        sock_path = worker_socket_dir / "control.sock"
        listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        listener.bind(str(sock_path)); os.chown(sock_path, 0, wu.pw_gid); os.chmod(sock_path, 0o660); listener.listen(8); listener.settimeout(0.25)
        dotnet = Path(shutil.which("dotnet", path=ENV["PATH"] ) or "").resolve(strict=True)
        cli = tool / "ForgeTrust.AppSurface.Cli.dll"
        if not cli.is_file(): raise LauncherError("trusted-cli-missing")
        worker_command = ["/usr/bin/env", "-i", *[f"{key}={value}" for key, value in WORKER_ENV.items()],
                          str(dotnet), str(cli), "evidence", "worker", "--control", str(sock_path)]
        worker_unit = f"evidencehost-{tag}-worker.service"; units.append(worker_unit)
        job_deadline_utc, job_deadline_monotonic = capture_job_deadline(args.job_seconds)
        worker_argv = ["systemd-run", "--quiet", "--unit="+worker_unit, "--expand-environment=no",
                       *[f"--property={k}={v}" for k,v in worker_unit_properties(
                           worker_name, tool, subject, scratch / "test-output", output_parent,
                           args.job_seconds).items()], *worker_command]
        _systemd(worker_argv)
        props = {}
        deadline = job_deadline_monotonic
        while time.monotonic() < deadline:
            props = dict(line.split("=",1) for line in _systemd(["systemctl","show",worker_unit,"--no-pager","--property=MainPID","--property=ControlGroup","--property=ActiveState","--property=KillMode","--property=User","--property=RuntimeMaxUSec"]).stdout.decode().splitlines() if "=" in line)
            if int(props.get("MainPID", "0")) > 0: break
            time.sleep(.03)
        worker_pid = int(props.get("MainPID", "0"))
        if not worker_pid: raise LauncherError("worker-start-failed")
        desc = {"schema": SCHEMA, "run_id": args.run_id, "worker_pid": worker_pid,
                "broker_pid": os.getpid(), "worker_uid": wu.pw_uid,
                "worker_gid": wu.pw_gid, "subject_uid": su.pw_uid, "subject_gid": su.pw_gid,
                "unit": worker_unit, "cgroup": props.get("ControlGroup", ""), "job_deadline_utc": job_deadline_utc,
                "tool_root": str(tool), "subject_root": str(subject), "output_parent": str(output_parent),
                "dotnet_path": str(dotnet), "test_output_root": str(scratch / "test-output"), "solution": solution,
                "diff_file": diff_file, "diff_sha256": diff_sha256,
                "paths": declared_paths,
                "output_slot": args.output_slot, "policy_file": str(policy), "mode": args.mode,
                "socket_path": str(sock_path), "descriptor_path": str(root / "worker-control.json"),
                "policy_sha256": policy_sha256(policy),
                "output_parent_identity": output_parent_identity(output_parent),
                "provider": "github-actions", "platform": "linux-x64",
                "proof_digest": "", "observation_profile_ids": list(args.observation_profile),
                "observation_producer_ids": list(args.observation_producer),
                "entry_sha256": hashlib.sha256(cli.read_bytes()).hexdigest(),
                "base_revision": args.base_revision, "subject_revision": args.subject_revision,
                "workflow_identity": args.workflow_identity}
        desc.update(budgets)
        descriptor_path = root / "worker-control.json"
        descriptor_fd = os.open(descriptor_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o440)
        with os.fdopen(descriptor_fd, "w", encoding="utf-8") as descriptor_file:
            json.dump(desc, descriptor_file, separators=(",", ":"))
            descriptor_file.write("\n")
        os.chown(descriptor_path, 0, wu.pw_gid)
        os.chmod(descriptor_path, 0o440)
        broker = Broker(desc,wu.pw_uid,worker_pid,wu.pw_gid,su.pw_uid,su.pw_gid,rgid,subject,
                        (tool, output_parent, root, policy), scratch, dotnet,
                        f"evidencehost-{tag}", deadline, args.job_seconds,
                        budgets["stopping_seconds"], budgets["cleanup_seconds"], test_output_fd)
        test_output_fd = -1  # Broker owns this pinned directory descriptor until all handlers have joined.
        # The socket exists before worker start; ready returns this complete root-created descriptor.
        while time.monotonic() < deadline:
            try:
                conn,_ = listener.accept()
                handler = threading.Thread(target=broker.handle, args=(conn,), daemon=True)
                handlers.append(handler)
                handler.start()
            except socket.timeout:
                pass
            state = _systemd(["systemctl", "show", worker_unit, "--no-pager",
                              "--property=ActiveState", "--property=MainPID"]).stdout.decode()
            state_properties = dict(line.split("=", 1) for line in state.splitlines() if "=" in line)
            if any(state_properties.get("ActiveState") == value
                   for value in ("inactive", "failed", "exited")):
                break
            # RemainAfterExit can keep a successful service Active after its worker
            # process exits, so protocol exit plus MainPID=0 is the terminal signal.
            if broker.exited and state_properties.get("MainPID") == "0":
                break
        if time.monotonic() >= deadline:
            broker.stop()
            _systemd(["systemctl","stop",worker_unit], timeout=8)
        if time.monotonic() >= deadline: raise LauncherError("worker-timeout")
        for handler in handlers: handler.join(timeout=3)
        if any(handler.is_alive() for handler in handlers): raise LauncherError("broker-handler-not-joined")
        if not broker.ready_seen or not broker.exited:
            try:
                failed_properties = broker._unit_properties(worker_unit)
            except (LauncherError, OSError, subprocess.SubprocessError, ValueError):
                failed_properties = {}
            raise worker_exit_failure("worker-protocol-incomplete", failed_properties)
        wprops=broker._unit_properties(worker_unit)
        if (wprops.get("Result") != "success" or wprops.get("User") != worker_name
                or wprops.get("KillMode") != "control-group"):
            raise worker_exit_failure("worker-unsuccessful", wprops)
        if not broker._group_empty(wprops.get("ControlGroup", desc["cgroup"])): raise LauncherError("worker-exit-unconfirmed")
        if any(not broker._group_empty(g) for _,g in broker.units if g): raise LauncherError("subject-exit-unconfirmed")
        output = output_parent / args.output_slot
        try:
            output_stat = output.lstat()
        except FileNotFoundError:
            raise LauncherError("output-slot-not-allocated") from None
        if (not output.is_dir() or output.is_symlink() or output_stat.st_uid != wu.pw_uid
                or output_stat.st_mode & 0o077):
            raise LauncherError("output-slot-invalid")
        if (not output_parent_identity(output_parent) == desc["output_parent_identity"]
                or output_parent.stat().st_mode & 0o777 != 0o700):
            raise LauncherError("output-parent-identity-changed")
        _systemd(["systemctl", "stop", worker_unit, *[unit for unit,_ in broker.units]], timeout=5)
        _systemd(["systemctl","reset-failed",worker_unit])
        for unit,_ in broker.units: _systemd(["systemctl","reset-failed",unit])
        exposed = True
        return output
    finally:
        if not exposed:
            if broker is not None:
                broker.stop()
            if units:
                subprocess.run(["systemctl", "stop", *reversed(units)], capture_output=True, env=ENV,
                               timeout=5, check=False)
        if listener is not None:
            try: listener.close()
            except OSError: pass
        for handler in handlers:
            handler.join()
        if broker is not None:
            broker.close_artifact_handles()
        if test_output_fd >= 0:
            os.close(test_output_fd)
        # Preserve/quarantine the worker-owned output anchor whenever termination is uncertain.
        if exposed:
            _delete_run_accounts(users, groups)
            shutil.rmtree(root,ignore_errors=True)
            shutil.rmtree(scratch, ignore_errors=True)


def parser() -> argparse.ArgumentParser:
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument("--tool-root",required=True); p.add_argument("--subject-root",required=True)
    p.add_argument("--policy-file",required=True); p.add_argument("--job-seconds",required=True,type=int)
    p.add_argument("--mode",required=True,choices=("trusted","observation")); p.add_argument("--output-parent",required=True)
    p.add_argument("--output-slot",required=True)
    p.add_argument("--base-revision",required=True); p.add_argument("--subject-revision",required=True)
    p.add_argument("--workflow-identity",required=True); p.add_argument("--run-id",required=True)
    p.add_argument("--solution",required=True); p.add_argument("--path",action="append",default=[])
    p.add_argument("--diff-file")
    p.add_argument("--diagnostic-directory", help="Internal: existing root-owned protected directory for one safe failure record.")
    p.add_argument("--observation-profile",action="append",default=[])
    p.add_argument("--observation-producer",action="append",default=[])
    for name, maximum in STAGE_LIMITS.items():
        p.add_argument("--" + name.replace("_", "-"), type=int, default=maximum)
    return p


def main(argv: list[str] | None = None) -> int:
    diagnostic_fd = None
    try:
        args = parser().parse_args(argv)
        if args.diagnostic_directory is not None:
            diagnostic_fd = open_diagnostic_directory(Path(args.diagnostic_directory))
            try:
                os.stat(FAILURE_DIAGNOSTIC_FILE, dir_fd=diagnostic_fd, follow_symlinks=False)
            except FileNotFoundError:
                pass
            else:
                raise LauncherError("diagnostic-slot-not-fresh")
        output=launch(args)
        print(json.dumps({"status":"completed","output_slot":output.name},separators=(",",":")))
        return 0
    except LauncherError as error:
        if diagnostic_fd is not None:
            try: write_failure_diagnostic(diagnostic_fd, error)
            except (OSError, ValueError): pass
        # Only this exact host-selected cause has a public admission diagnostic. Never echo
        # arbitrary exception text, paths, commands, or supplied descriptor values.
        diagnostic = "ASEVD407" if str(error) == "trusted-proof-not-allowlisted" else "launcher-failed"
        print(json.dumps({"status":"failed","diagnostic":diagnostic},separators=(",",":")),file=sys.stderr)
        return 1
    except (OSError, subprocess.SubprocessError, ValueError) as error:
        if diagnostic_fd is not None:
            try: write_failure_diagnostic(diagnostic_fd, error)
            except (OSError, ValueError): pass
        print(json.dumps({"status":"failed","diagnostic":"launcher-failed"},separators=(",",":")),file=sys.stderr)
        return 1
    finally:
        if diagnostic_fd is not None:
            os.close(diagnostic_fd)


if __name__ == "__main__":
    sys.exit(main())
