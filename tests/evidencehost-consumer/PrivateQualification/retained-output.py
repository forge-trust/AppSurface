"""Private read-only retained-output collector; raw bytes confer no execution or gate authority.

The root controller must require the exact completion class from its actual imported
launcher before calling collect. This module does not load a second launcher, accept
JSON as a completion, close its accounts, parse a manifest, or issue an accepted claim.
See retained-output-README.md for the fixed output grammar and ownership ordering.
"""
from __future__ import annotations

import math
import os
from pathlib import Path
import stat
import time


WORKER_UID = 65010
WORKER_GID = 65011
OUTPUT_SLOT = "qualification"
EXPECTED_FILES = frozenset({
    "evidence-plan.json", "evidence-manifest.json", "evidence-summary.json",
    "qual-coverage/merged/coverage.cobertura.xml",
})
HOST_FILES = frozenset({"manifest.json", "qual-coverage/merged/coverage.cobertura.xml"})
EXPECTED_DIRECTORIES = frozenset({"qual-coverage", "qual-coverage/merged"})
MAX_FILES = 64
MAX_DEPTH = 8
MAX_FILE_BYTES = 20 * 1024 * 1024
MAX_TOTAL_BYTES = 20 * 1024 * 1024
READ_CHUNK_BYTES = 64 * 1024
_IDENTITY_KEYS = frozenset({"device_major", "device_minor", "inode", "uid", "gid"})


class RetainedOutputError(RuntimeError):
    """Fixed local collection failure; never retain paths, exception bytes or hostile content."""
    def __init__(self):
        super().__init__("retained-output-failed")


def _require(value):
    if not value:
        raise RetainedOutputError()


def _clock(deadline):
    _require(type(deadline) in (int, float) and math.isfinite(deadline) and time.monotonic() < deadline)


def _receipt(info):
    return {"device_major": os.major(info.st_dev), "device_minor": os.minor(info.st_dev),
            "inode": info.st_ino, "uid": info.st_uid, "gid": info.st_gid}


def _snapshot(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid, info.st_nlink,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _identity(value, uid, gid):
    _require(type(value) is dict and set(value) == _IDENTITY_KEYS
             and all(type(number) is int and number >= 0 for number in value.values())
             and value["inode"] > 0 and (value["uid"], value["gid"]) == (uid, gid))


def _close_all(fds):
    failed = False
    for fd in reversed(fds):
        try:
            os.close(fd)
        except OSError:
            failed = True
    if failed:
        raise RetainedOutputError()


def _open_absolute_directory(path, deadline):
    """Pin every current absolute directory component without following any symlink."""
    _clock(deadline)
    _require(type(path) is str and path.startswith("/") and os.path.normpath(path) == path
             and "\0" not in path and ".." not in Path(path).parts)
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for component in Path(path).parts[1:]:
            _clock(deadline)
            child = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            old, fd = fd, child
            os.close(old)
        _clock(deadline)
        result, fd = fd, -1
        return result
    finally:
        if fd >= 0:
            os.close(fd)


def _collect_fds(output_fd, parent_fd, parent_path, output_identity, parent_identity,
                 expected_plan_bytes, deadline, *, entry="cli", expected_uid=WORKER_UID, expected_gid=WORKER_GID):
    """Read-only data/FD seam; owner overrides cannot create a completion, lease or authority.

    The two incoming descriptors are borrowed. Every child/file/name descriptor
    opened here is retained through final identity rechecks and always closed.
    """
    owned = []
    directories, files, result = [], [], {}
    try:
        _clock(deadline)
        _require(type(entry) is str and entry in ("cli", "host"))
        expected_files = EXPECTED_FILES if entry == "cli" else HOST_FILES
        _require(type(expected_uid) is int and expected_uid >= 0 and type(expected_gid) is int and expected_gid >= 0
                 and type(expected_plan_bytes) is bytes and len(expected_plan_bytes) <= MAX_FILE_BYTES
                 and type(output_fd) is int and output_fd >= 0 and type(parent_fd) is int and parent_fd >= 0
                 and output_fd != parent_fd)
        _identity(output_identity, expected_uid, expected_gid)
        _identity(parent_identity, expected_uid, expected_gid)
        parent, output = os.fstat(parent_fd), os.fstat(output_fd)
        for info, expected in ((parent, parent_identity), (output, output_identity)):
            _require(stat.S_ISDIR(info.st_mode) and stat.S_IMODE(info.st_mode) == 0o700 and _receipt(info) == expected)
        named_parent = _open_absolute_directory(parent_path, deadline)
        owned.append(named_parent)
        _require(_snapshot(parent) == _snapshot(os.fstat(named_parent))
                 and _snapshot(output) == _snapshot(os.stat(OUTPUT_SLOT, dir_fd=parent_fd, follow_symlinks=False)))
        directories.append((output_fd, parent_fd, OUTPUT_SLOT, _snapshot(output)))
        total, count = 0, 0

        def visit(directory_fd, prefix, depth):
            nonlocal total, count
            _clock(deadline)
            _require(depth <= MAX_DEPTH)
            names = []
            with os.scandir(directory_fd) as entries:
                for entry in entries:
                    _clock(deadline)
                    _require(len(names) < MAX_FILES + len(EXPECTED_DIRECTORIES))
                    names.append(entry.name)
            for name in sorted(names):
                _clock(deadline)
                _require(name not in ("", ".", "..") and "/" not in name and "\0" not in name)
                relative = prefix + "/" + name if prefix else name
                named = os.stat(name, dir_fd=directory_fd, follow_symlinks=False)
                if stat.S_ISDIR(named.st_mode):
                    _require(relative in EXPECTED_DIRECTORIES and stat.S_IMODE(named.st_mode) == 0o700
                             and (named.st_uid, named.st_gid) == (expected_uid, expected_gid))
                    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory_fd)
                    owned.append(fd)
                    info = os.fstat(fd)
                    _require(_snapshot(info) == _snapshot(named))
                    directories.append((fd, directory_fd, name, _snapshot(info)))
                    visit(fd, relative, depth + 1)
                    continue
                _require(relative in expected_files and stat.S_ISREG(named.st_mode)
                         and stat.S_IMODE(named.st_mode) == 0o600 and named.st_nlink == 1
                         and (named.st_uid, named.st_gid) == (expected_uid, expected_gid)
                         and 0 <= named.st_size <= MAX_FILE_BYTES)
                count += 1; total += named.st_size
                _require(count <= MAX_FILES and total <= MAX_TOTAL_BYTES and relative not in result)
                fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=directory_fd)
                owned.append(fd)
                info = os.fstat(fd)
                _require(_snapshot(info) == _snapshot(named))
                before = _snapshot(info)
                files.append((fd, directory_fd, name, before))
                chunks, remaining = [], info.st_size
                while remaining:
                    _clock(deadline)
                    data = os.read(fd, min(READ_CHUNK_BYTES, remaining))
                    _require(data and len(data) <= remaining)
                    chunks.append(data); remaining -= len(data)
                _clock(deadline)
                _require(not os.read(fd, 1) and before == _snapshot(os.fstat(fd))
                         and before == _snapshot(os.stat(name, dir_fd=directory_fd, follow_symlinks=False)))
                result[relative] = b"".join(chunks)

        visit(output_fd, "", 0)
        _require(set(result) == expected_files
                 and (entry == "host" or result["evidence-plan.json"] == expected_plan_bytes))
        for fd, containing_fd, name, before in files + directories:
            _clock(deadline)
            _require(before == _snapshot(os.fstat(fd))
                     and before == _snapshot(os.stat(name, dir_fd=containing_fd, follow_symlinks=False)))
        _clock(deadline)
        final_named_parent = _open_absolute_directory(parent_path, deadline)
        owned.append(final_named_parent)
        _require(_snapshot(parent) == _snapshot(os.fstat(parent_fd))
                 and _snapshot(parent) == _snapshot(os.fstat(named_parent))
                 and _snapshot(parent) == _snapshot(os.fstat(final_named_parent)))
        _clock(deadline)
        return result
    except Exception:
        raise RetainedOutputError() from None
    finally:
        _close_all(owned)


def collect(completion, expected_plan_bytes: bytes, deadline: float, *, entry: str = "cli") -> dict[str, bytes]:
    """Return only actual closed cli/host files before caller closes launcher completion.

    The controller MUST assert type(completion) is its actual launcher._LaunchCompletion.
    This module validates data and filesystem bindings, not that external type authority.
    It closes only its duplicates; never completion.close() or retained accounts.
    CLI checks actual plan bytes exactly. Host emits no plan; the caller supplies
    its frozen expected plan separately to structural verification, never this result.
    """
    duplicated = []
    try:
        _clock(deadline)
        _require(type(entry) is str and entry in ("cli", "host"))
        descriptor = completion.descriptor
        output_identity, parent_identity = completion.output_identity, completion.output_parent_identity
        _require(type(descriptor) is dict and type(descriptor.get("worker_uid")) is int
                 and type(descriptor.get("worker_gid")) is int
                 and (descriptor["worker_uid"], descriptor["worker_gid"]) == (WORKER_UID, WORKER_GID)
                 and descriptor.get("output_slot") == OUTPUT_SLOT
                 and descriptor.get("output_parent_identity") == parent_identity
                 and type(expected_plan_bytes) is bytes and len(expected_plan_bytes) <= MAX_FILE_BYTES)
        _identity(output_identity, WORKER_UID, WORKER_GID)
        _identity(parent_identity, WORKER_UID, WORKER_GID)
        parent_path = descriptor.get("output_parent")
        _require(type(parent_path) is str and completion.output == Path(parent_path) / OUTPUT_SLOT)
        for duplicate in (completion.duplicate_output_directory, completion.duplicate_output_parent):
            _clock(deadline)
            fd = duplicate()
            _require(type(fd) is int and fd >= 0 and fd not in duplicated)
            duplicated.append(fd)
        return _collect_fds(duplicated[0], duplicated[1], parent_path, output_identity, parent_identity,
                            expected_plan_bytes, deadline, entry=entry)
    except Exception:
        raise RetainedOutputError() from None
    finally:
        _close_all(duplicated)
