"""Private, non-authoritative VSTest trace retention after authenticated owned exit.

The caller supplies borrowed, already selected directory FDs, exactly one recorded
results token, and actual subject identity. This module neither discovers a job nor
establishes its exit. ``expected_root_uid`` labels portable data controls only; the
runtime caller uses its default zero. No caller URL, command, trace basename or
destination basename is accepted. See the private qualification README.
"""

import datetime
import hashlib
import json
import math
import os
import re
import stat
import time


TRACE_SUFFIX = "-qualification-vstest.log"
DESTINATION_NAME = "vstest-diagnostics"
ROLES = ("runner", "collector", "host")
MAX_ENTRIES = 64
WINDOW_BYTES = 64 * 1024
MAX_ROLE_BYTES = 2 * WINDOW_BYTES
MAX_INDEX_BYTES = 4096
MAX_TOTAL_BYTES = 400 * 1024
MAX_SECONDS = 5.0
TOKEN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}\Z", re.ASCII)
COMPANION = re.compile(
    r"(datacollector|host)\."
    r"(\d{2}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_\d{5})_([1-9][0-9]{0,9})\.log\Z",
    re.ASCII,
)
DIRECTORY_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC


class _Rejected(Exception):
    """An internal data rejection; never serialized or echoed."""


def _require(value):
    if not value:
        raise _Rejected()


def _remaining(deadline):
    _require(time.monotonic() < deadline)


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _directory(info, uid, gid, mode=None):
    _require(stat.S_ISDIR(info.st_mode) and info.st_uid == uid and info.st_gid == gid
             and info.st_nlink > 0)
    permissions = stat.S_IMODE(info.st_mode)
    if mode is None:
        _require(permissions & 0o700 == 0o700 and permissions & 0o7022 == 0)
    else:
        _require(permissions == mode)


def _source_file(info, uid, gid):
    permissions = stat.S_IMODE(info.st_mode)
    _require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1
             and info.st_uid == uid and info.st_gid == gid
             and permissions in (0o400, 0o440, 0o444, 0o600, 0o640, 0o644)
             and 0 <= info.st_size <= (1 << 63) - 1)


def _role(name, basename):
    if name == basename:
        return "runner"
    stem = basename[:-4]
    match = COMPANION.fullmatch(name[len(stem) + 1:]) if name.startswith(stem + ".") else None
    if match is not None:
        _require(int(match[3]) <= 2147483647)
        try:
            datetime.datetime.strptime(match[2], "%y-%m-%d_%H-%M-%S_%f")
        except ValueError:
            raise _Rejected() from None
        return "collector" if match[1] == "datacollector" else "host"
    # Rolling .bak and GUID fallbacks are excluded, never selected as authority.
    return None


def _names(fd, deadline):
    names = []
    with os.scandir(fd) as entries:
        for entry in entries:
            _remaining(deadline)
            _require(len(names) < MAX_ENTRIES)
            _require(len(os.fsencode(entry.name)) <= 255)
            names.append(entry.name)
    _remaining(deadline)
    return tuple(sorted(names))


def _read_region(fd, offset, length, deadline):
    blocks = []
    read = 0
    while read < length:
        _remaining(deadline)
        block = os.pread(fd, min(WINDOW_BYTES, length - read), offset + read)
        _require(bool(block))
        blocks.append(block)
        read += len(block)
    _remaining(deadline)
    return b"".join(blocks)


def _read_file(fd, length, deadline):
    if length <= MAX_ROLE_BYTES:
        content = _read_region(fd, 0, length, deadline)
    else:
        content = (_read_region(fd, 0, WINDOW_BYTES, deadline)
                   + _read_region(fd, length - WINDOW_BYTES, WINDOW_BYTES, deadline))
    _remaining(deadline)
    _require(os.pread(fd, 1, length) == b"")
    return content


def _write(fd, content, deadline):
    offset = 0
    while offset < len(content):
        _remaining(deadline)
        count = os.write(fd, content[offset:offset + WINDOW_BYTES])
        _require(count > 0)
        offset += count
    _remaining(deadline)


def _recheck_sources(parent_fd, parent_stat, token, token_fd, token_stat, names, files, deadline):
    _remaining(deadline)
    _require(_identity(os.fstat(parent_fd)) == _identity(parent_stat))
    _require(_identity(os.fstat(token_fd)) == _identity(token_stat))
    _require(_identity(os.stat(token, dir_fd=parent_fd, follow_symlinks=False)) == _identity(token_stat))
    _require(_names(parent_fd, deadline) == names)
    for name, fd, info in files:
        _remaining(deadline)
        _require(_identity(os.fstat(fd)) == _identity(info))
        _require(_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)) == _identity(info))


def _cleanup(parent_fd, child_fd, child_stat, created):
    """Remove only our exclusive, still-identical files; never replace foreign data."""
    try:
        if child_fd < 0 or child_stat is None:
            return
        named = os.stat(DESTINATION_NAME, dir_fd=parent_fd, follow_symlinks=False)
        if (named.st_dev, named.st_ino) != (child_stat.st_dev, child_stat.st_ino):
            return
        for name, dev, ino in reversed(created):
            info = os.stat(name, dir_fd=child_fd, follow_symlinks=False)
            if (info.st_dev, info.st_ino) == (dev, ino):
                os.unlink(name, dir_fd=child_fd)
        os.rmdir(DESTINATION_NAME, dir_fd=parent_fd)
    except OSError:
        # Partial diagnostic quarantine remains private. Cleanup never upgrades it.
        pass


def capture_traces(result_parent_fd, result_tokens, destination_fd, subject_uid, results_gid,
                   *, deadline, expected_root_uid=0) -> bool:
    """Retain raw private trace windows, returning availability only.

    Both input FDs remain open and their offsets unchanged. Results parent/token
    must be subject UID/results GID directories with mode 2770. Source files are
    single-link ordinary owner-readable files (400/440/444/600/640/644). Traces
    are direct siblings of the recorded token, with its exact basename prefix.
    Unrecognized and rolling names are not selected; the parent listing is capped
    at 64. Exactly one safe token is accepted. Missing roles become empty files
    with null source length in the closed index; all roles missing returns False.

    The existing destination parent must have expected root ownership and no
    special or group/other-write bits. The new exclusive child is 700, its four
    fixed files 600. Each trace retains at most 64 KiB prefix + 64 KiB suffix;
    index SHA256 covers retained bytes, not omitted bytes or trace authenticity.
    This call clamps its monotonic budget to five seconds. Read errors, identity
    changes, ambiguity, expiry or cleanup errors return False without raw echo.
    The caller must preserve its original consumer result regardless of return.
    """
    owned = []
    created = []
    parent_fd = token_fd = output_fd = child_fd = -1
    child_stat = None
    success = False
    try:
        _require(type(deadline) in (int, float) and math.isfinite(deadline))
        deadline = min(float(deadline), time.monotonic() + MAX_SECONDS)
        _remaining(deadline)
        _require(all(type(value) is int and 0 <= value <= 0xffffffff
                     for value in (subject_uid, results_gid, expected_root_uid)))
        _require(os.geteuid() == expected_root_uid)
        root_gid = 0 if expected_root_uid == 0 else os.getegid()
        _require(os.getegid() == root_gid)
        _require(type(result_tokens) in (tuple, list) and len(result_tokens) == 1)
        token = result_tokens[0]
        _require(type(token) is str and TOKEN.fullmatch(token) is not None)
        _require(type(result_parent_fd) is int and type(destination_fd) is int
                 and result_parent_fd >= 0 and destination_fd >= 0)
        parent_fd = os.open(".", DIRECTORY_FLAGS, dir_fd=result_parent_fd)
        owned.append(parent_fd)
        output_fd = os.open(".", DIRECTORY_FLAGS, dir_fd=destination_fd)
        owned.append(output_fd)
        parent_stat = os.fstat(parent_fd)
        output_stat = os.fstat(output_fd)
        _directory(parent_stat, subject_uid, results_gid, 0o2770)
        _directory(output_stat, expected_root_uid, root_gid)
        _require((parent_stat.st_dev, parent_stat.st_ino) != (output_stat.st_dev, output_stat.st_ino))
        _remaining(deadline)
        token_fd = os.open(token, DIRECTORY_FLAGS, dir_fd=parent_fd)
        owned.append(token_fd)
        token_stat = os.fstat(token_fd)
        _directory(token_stat, subject_uid, results_gid, 0o2770)
        _require(_identity(os.stat(token, dir_fd=parent_fd, follow_symlinks=False)) == _identity(token_stat))
        names = _names(parent_fd, deadline)
        basename = token + TRACE_SUFFIX
        selected = {}
        for name in names:
            _remaining(deadline)
            role = _role(name, basename)
            if role is not None:
                _require(role not in selected)
                selected[role] = name
        _require(bool(selected))
        files = []
        content = {}
        lengths = {}
        for role in ROLES:
            _remaining(deadline)
            if role not in selected:
                content[role], lengths[role] = b"", None
                continue
            name = selected[role]
            before = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
            _source_file(before, subject_uid, results_gid)
            fd = os.open(name, FILE_FLAGS, dir_fd=parent_fd)
            owned.append(fd)
            info = os.fstat(fd)
            _source_file(info, subject_uid, results_gid)
            _require(_identity(info) == _identity(before))
            files.append((name, fd, info))
            content[role] = _read_file(fd, info.st_size, deadline)
            lengths[role] = info.st_size
        _recheck_sources(parent_fd, parent_stat, token, token_fd, token_stat, names, files, deadline)
        index = json.dumps({"roles": [
            {"role": role, "length": lengths[role], "retained_length": len(content[role]),
             "truncated": lengths[role] is not None and lengths[role] > MAX_ROLE_BYTES,
             "sha256": hashlib.sha256(content[role]).hexdigest()} for role in ROLES
        ]}, sort_keys=True, separators=(",", ":")).encode("ascii") + b"\n"
        _require(len(index) <= MAX_INDEX_BYTES
                 and len(index) + sum(map(len, content.values())) <= MAX_TOTAL_BYTES)
        _require(_identity(os.fstat(output_fd)) == _identity(output_stat))
        _remaining(deadline)
        os.mkdir(DESTINATION_NAME, 0o700, dir_fd=output_fd)
        child_fd = os.open(DESTINATION_NAME, DIRECTORY_FLAGS, dir_fd=output_fd)
        owned.append(child_fd)
        os.fchmod(child_fd, 0o700)
        child_stat = os.fstat(child_fd)
        _directory(child_stat, expected_root_uid, root_gid, 0o700)
        _require(_identity(os.stat(DESTINATION_NAME, dir_fd=output_fd, follow_symlinks=False)) == _identity(child_stat))
        for name, data in tuple((role + ".log", content[role]) for role in ROLES) + (("index.json", index),):
            _remaining(deadline)
            fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW
                         | os.O_NONBLOCK | os.O_CLOEXEC, 0o600, dir_fd=child_fd)
            owned.append(fd)
            initial = os.fstat(fd)
            created.append((name, initial.st_dev, initial.st_ino))
            os.fchmod(fd, 0o600)
            _write(fd, data, deadline)
            info = os.fstat(fd)
            _require(stat.S_ISREG(info.st_mode) and info.st_uid == expected_root_uid
                     and info.st_gid == root_gid and stat.S_IMODE(info.st_mode) == 0o600
                     and info.st_nlink == 1 and info.st_size == len(data))
            _require(_identity(os.stat(name, dir_fd=child_fd, follow_symlinks=False)) == _identity(info))
        _recheck_sources(parent_fd, parent_stat, token, token_fd, token_stat, names, files, deadline)
        _directory(os.fstat(output_fd), expected_root_uid, root_gid)
        _require((os.fstat(output_fd).st_dev, os.fstat(output_fd).st_ino)
                 == (output_stat.st_dev, output_stat.st_ino))
        named = os.stat(DESTINATION_NAME, dir_fd=output_fd, follow_symlinks=False)
        final_child = os.fstat(child_fd)
        _directory(final_child, expected_root_uid, root_gid, 0o700)
        _require(_identity(named) == _identity(final_child)
                 and (named.st_dev, named.st_ino) == (child_stat.st_dev, child_stat.st_ino))
        _remaining(deadline)
        success = True
    except Exception:
        success = False
    finally:
        if not success:
            _cleanup(output_fd, child_fd, child_stat, created)
        # Every duplicate/open is attempted once; never close either borrowed FD.
        for fd in reversed(owned):
            try:
                os.close(fd)
            except OSError:
                success = False
    # Descriptor cleanup is part of the original five-second budget. It cannot
    # turn a timely pre-close snapshot into a successful late return.
    if success:
        try:
            _remaining(deadline)
        except Exception:
            success = False
    return success
