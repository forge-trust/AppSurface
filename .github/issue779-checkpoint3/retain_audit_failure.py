"""Preserve a small, fixed set of failed audit diagnostics without interpreting them."""

from __future__ import annotations

import hashlib
import json
import os
import re
import stat
from pathlib import Path
from typing import Any


MAX_DRIVER_LOG = 8 * 1024 * 1024
MAX_TRX = 1024 * 1024
MAX_SOURCE_CAPTURE = 1024 * 1024
_DESTINATION_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,95}\Z")
_FILES = (
    ("application-bundle-driver.log", ("application-bundle-driver.log",), MAX_DRIVER_LOG, True),
    ("application-bundle-tests/application-bundle.trx",
     ("application-bundle-tests", "application-bundle.trx"), MAX_TRX, False),
    ("source.json", ("source.json",), MAX_SOURCE_CAPTURE, True),
)
_DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
_FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
_OUT_FLAGS = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC


class RetentionError(ValueError):
    """Fixed diagnostic-retention validation failure; messages contain no file contents."""


def preserve(source_root: str | os.PathLike[str], destination_parent: str | os.PathLike[str],
             destination_name: str, *, destination_uid: int, destination_gid: int) -> dict[str, Any]:
    """Copy only the fixed failure log, optional TRX, and bounded source capture.

    Both roots are opened component by component without following symlinks. The destination
    directory must be new. Raw sources are read from no-follow descriptors, checked for stable
    single-link regular-file identity, and copied unchanged. The returned and persisted summary
    records bytes, hashes, and presence only; it never declares a test, native, gate, or acceptance
    result.
    """
    _validate_owner(destination_uid, "destination-uid")
    _validate_owner(destination_gid, "destination-gid")
    if not isinstance(destination_name, str) or not _DESTINATION_NAME.fullmatch(destination_name):
        raise RetentionError("destination-name-invalid")

    source_fd = _open_directory_path(source_root)
    parent_fd: int | None = None
    created_fd: int | None = None
    try:
        parent_fd = _open_directory_path(destination_parent)
        os.mkdir(destination_name, 0o700, dir_fd=parent_fd)
        created_fd = os.open(destination_name, _DIR_FLAGS, dir_fd=parent_fd)
        _set_owner_mode(created_fd, destination_uid, destination_gid, 0o700)

        records: dict[str, dict[str, Any]] = {}
        for relative, parts, maximum, required in _FILES:
            content = _read_fixed_source(source_fd, parts, maximum, required)
            present = content is not None
            if present:
                if len(parts) == 2:
                    _make_private_directory(created_fd, parts[0], destination_uid, destination_gid)
                    dest_parent = os.open(parts[0], _DIR_FLAGS, dir_fd=created_fd)
                else:
                    dest_parent = os.dup(created_fd)
                try:
                    _write_exclusive(dest_parent, parts[-1], content, destination_uid, destination_gid)
                finally:
                    os.close(dest_parent)
                records[relative] = {
                    "present": True,
                    "bytes": len(content),
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
            else:
                records[relative] = {"present": False, "bytes": 0, "sha256": None}

        summary: dict[str, Any] = {
            "schema": "issue779-audit-failure-retention-v1",
            "files": records,
            "native_claimed": False,
            "gate_pass_claimed": False,
            "accepted": False,
        }
        encoded = (json.dumps(summary, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        _write_exclusive(created_fd, "retention-summary.json", encoded, destination_uid, destination_gid)
        os.fsync(created_fd)
        return summary
    finally:
        os.close(source_fd)
        if parent_fd is not None:
            os.close(parent_fd)
        if created_fd is not None:
            os.close(created_fd)


def _validate_owner(value: int, label: str) -> None:
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= 0xFFFFFFFF:
        raise RetentionError(label + "-invalid")


def _open_directory_path(path: str | os.PathLike[str]) -> int:
    value = os.fspath(path)
    if not isinstance(value, str) or not value.startswith("/"):
        raise RetentionError("root-path-invalid")
    descriptor = os.open("/", _DIR_FLAGS)
    try:
        for component in value.split("/")[1:]:
            if not component:
                continue
            if component in (".", ".."):
                raise RetentionError("root-path-invalid")
            child = os.open(component, _DIR_FLAGS, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def _read_fixed_source(root_fd: int, parts: tuple[str, ...], maximum: int, required: bool) -> bytes | None:
    parent = os.dup(root_fd)
    try:
        if len(parts) == 2:
            try:
                child = os.open(parts[0], _DIR_FLAGS, dir_fd=parent)
            except FileNotFoundError:
                if required:
                    raise RetentionError("required-source-missing") from None
                return None
            os.close(parent)
            parent = child
        try:
            descriptor = os.open(parts[-1], _FILE_FLAGS, dir_fd=parent)
        except FileNotFoundError:
            if required:
                raise RetentionError("required-source-missing") from None
            return None
        except OSError as error:
            raise RetentionError("source-open-rejected") from error
        try:
            before = os.fstat(descriptor)
            _require_regular_single_link(before, maximum)
            data = bytearray()
            while len(data) <= maximum:
                chunk = os.read(descriptor, min(64 * 1024, maximum + 1 - len(data)))
                if not chunk:
                    break
                data.extend(chunk)
            if len(data) > maximum:
                raise RetentionError("source-size-limit")
            after = os.fstat(descriptor)
            _require_regular_single_link(after, maximum)
            if _signature(before) != _signature(after) or len(data) != before.st_size:
                raise RetentionError("source-changed-during-read")
            current = os.open(parts[-1], _FILE_FLAGS, dir_fd=parent)
            try:
                if _signature(os.fstat(current)) != _signature(after):
                    raise RetentionError("source-name-changed-during-read")
            finally:
                os.close(current)
            return bytes(data)
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def _require_regular_single_link(metadata: os.stat_result, maximum: int) -> None:
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or metadata.st_size < 0:
        raise RetentionError("source-file-type-or-links-rejected")
    if metadata.st_size > maximum:
        raise RetentionError("source-size-limit")


def _signature(metadata: os.stat_result) -> tuple[int, ...]:
    return (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_nlink, metadata.st_uid,
            metadata.st_gid, metadata.st_size, metadata.st_mtime_ns, metadata.st_ctime_ns)


def _make_private_directory(root_fd: int, name: str, uid: int, gid: int) -> None:
    os.mkdir(name, 0o700, dir_fd=root_fd)
    descriptor = os.open(name, _DIR_FLAGS, dir_fd=root_fd)
    try:
        _set_owner_mode(descriptor, uid, gid, 0o700)
    finally:
        os.close(descriptor)


def _set_owner_mode(descriptor: int, uid: int, gid: int, mode: int) -> None:
    current = os.fstat(descriptor)
    if (current.st_uid, current.st_gid) != (uid, gid):
        os.fchown(descriptor, uid, gid)
    os.fchmod(descriptor, mode)
    final = os.fstat(descriptor)
    links_valid = not stat.S_ISREG(final.st_mode) or final.st_nlink == 1
    if (final.st_uid, final.st_gid, stat.S_IMODE(final.st_mode)) != (uid, gid, mode) or not links_valid:
        raise RetentionError("destination-owner-or-mode-rejected")


def _write_exclusive(parent_fd: int, name: str, data: bytes, uid: int, gid: int) -> None:
    descriptor = os.open(name, _OUT_FLAGS, 0o600, dir_fd=parent_fd)
    try:
        _set_owner_mode(descriptor, uid, gid, 0o600)
        remaining = memoryview(data)
        while remaining:
            written = os.write(descriptor, remaining)
            if written <= 0:
                raise RetentionError("destination-write-failed")
            remaining = remaining[written:]
        os.fsync(descriptor)
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or metadata.st_size != len(data):
            raise RetentionError("destination-file-verification-failed")
    finally:
        os.close(descriptor)
