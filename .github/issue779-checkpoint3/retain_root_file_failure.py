"""Retain bounded diagnostics from a failed root-file collector attempt."""

from __future__ import annotations

import hashlib
import json
import os
import stat
from pathlib import Path
from typing import Any


MAX_DRIVER_SOURCE = 32 * 1024 * 1024
MAX_DRIVER_TAIL = 256 * 1024
MAX_TRX = 1024 * 1024
MAX_SOURCE = 1024 * 1024
DIR_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
OUT_FLAGS = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC


class RetentionError(ValueError):
    """Fixed diagnostic failure category with no source contents in the message."""


def preserve(source_root: str | os.PathLike[str], destination_parent: str | os.PathLike[str],
             destination_name: str, *, destination_uid: int, destination_gid: int) -> dict[str, Any]:
    """Copy only the fixed root collector log tail, optional TRX, and source capture.

    The collector log is streamed under a 32 MiB source bound and only its final 256 KiB are
    retained. The TRX and source capture are copied byte-for-byte under their own limits.
    This helper records diagnostics only; it does not interpret test outcomes or reports.
    """
    _validate_owner(destination_uid, "destination-uid")
    _validate_owner(destination_gid, "destination-gid")
    if (not isinstance(destination_name, str) or not destination_name or destination_name in (".", "..") or
            len(destination_name) > 96 or any(
                character not in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-"
                for character in destination_name
            )):
        raise RetentionError("destination-name-invalid")

    source_fd = _open_directory_path(source_root)
    parent_fd: int | None = None
    created_fd: int | None = None
    try:
        parent_fd = _open_directory_path(destination_parent)
        os.mkdir(destination_name, 0o700, dir_fd=parent_fd)
        created_fd = os.open(destination_name, DIR_FLAGS, dir_fd=parent_fd)
        _set_owner_mode(created_fd, destination_uid, destination_gid, 0o700)

        records: dict[str, dict[str, Any]] = {}
        driver, driver_record = _read_driver_tail(source_fd)
        if driver is not None:
            _write_exclusive(created_fd, "root-file-driver.tail.log", driver,
                             destination_uid, destination_gid)
        records["root-file-driver.log"] = driver_record

        trx, trx_record = _read_fixed(source_fd, ("root-file-tests", "root-files.trx"), MAX_TRX, required=False)
        if trx is not None:
            subdir = _make_private_directory(created_fd, "root-file-tests", destination_uid, destination_gid)
            try:
                _write_exclusive(subdir, "root-files.trx", trx, destination_uid, destination_gid)
            finally:
                os.close(subdir)
        records["root-file-tests/root-files.trx"] = trx_record

        source, source_record = _read_fixed(source_fd, ("source.json",), MAX_SOURCE, required=True)
        assert source is not None
        _write_exclusive(created_fd, "source.json", source, destination_uid, destination_gid)
        records["source.json"] = source_record

        summary: dict[str, Any] = {
            "schema": "issue779-root-file-failure-retention-v1",
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
    descriptor = os.open("/", DIR_FLAGS)
    try:
        for component in value.split("/")[1:]:
            if not component:
                continue
            if component in (".", ".."):
                raise RetentionError("root-path-invalid")
            child = os.open(component, DIR_FLAGS, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = child
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def _open_source(root_fd: int, parts: tuple[str, ...], required: bool) -> tuple[int | None, int]:
    parent = os.dup(root_fd)
    try:
        if len(parts) == 2:
            try:
                child = os.open(parts[0], DIR_FLAGS, dir_fd=parent)
            except FileNotFoundError:
                if required:
                    raise RetentionError("required-source-missing") from None
                return None, parent
            os.close(parent)
            parent = child
        try:
            descriptor = os.open(parts[-1], FILE_FLAGS, dir_fd=parent)
        except FileNotFoundError:
            if required:
                raise RetentionError("required-source-missing") from None
            return None, parent
        except OSError as error:
            raise RetentionError("source-open-rejected") from error
        return descriptor, parent
    except BaseException:
        os.close(parent)
        raise


def _signature(metadata: os.stat_result) -> tuple[int, ...]:
    return (metadata.st_dev, metadata.st_ino, metadata.st_mode, metadata.st_nlink, metadata.st_uid,
            metadata.st_gid, metadata.st_size, metadata.st_mtime_ns, metadata.st_ctime_ns)


def _require_regular(metadata: os.stat_result, maximum: int) -> None:
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or metadata.st_size < 0:
        raise RetentionError("source-file-type-or-links-rejected")
    if metadata.st_size > maximum:
        raise RetentionError("source-size-limit")


def _read_fixed(
    root_fd: int, parts: tuple[str, ...], maximum: int, *, required: bool
) -> tuple[bytes | None, dict[str, Any]]:
    descriptor, parent = _open_source(root_fd, parts, required)
    try:
        if descriptor is None:
            return None, {"present": False, "bytes": 0, "sha256": None}
        try:
            before = os.fstat(descriptor)
            _require_regular(before, maximum)
            data = bytearray()
            while len(data) <= maximum:
                chunk = os.read(descriptor, min(64 * 1024, maximum + 1 - len(data)))
                if not chunk:
                    break
                data.extend(chunk)
            if len(data) > maximum:
                raise RetentionError("source-size-limit")
            after = os.fstat(descriptor)
            _require_regular(after, maximum)
            if _signature(before) != _signature(after) or len(data) != before.st_size:
                raise RetentionError("source-changed-during-read")
            current = os.open(parts[-1], FILE_FLAGS, dir_fd=parent)
            try:
                if _signature(os.fstat(current)) != _signature(after):
                    raise RetentionError("source-name-changed-during-read")
            finally:
                os.close(current)
            value = bytes(data)
            return value, {"present": True, "bytes": len(value), "sha256": hashlib.sha256(value).hexdigest()}
        finally:
            os.close(descriptor)
    finally:
        os.close(parent)


def _read_driver_tail(root_fd: int) -> tuple[bytes | None, dict[str, Any]]:
    descriptor, parent = _open_source(root_fd, ("root-file-driver.log",), required=False)
    if descriptor is None:
        os.close(parent)
        return None, {"present": False, "source_bytes": 0, "source_sha256": None,
                      "retained_bytes": 0, "retained_sha256": None, "retained_suffix": True}
    try:
        before = os.fstat(descriptor)
        _require_regular(before, MAX_DRIVER_SOURCE)
        digest = hashlib.sha256()
        tail = bytearray()
        count = 0
        while True:
            chunk = os.read(descriptor, 64 * 1024)
            if not chunk:
                break
            count += len(chunk)
            if count > MAX_DRIVER_SOURCE:
                raise RetentionError("source-size-limit")
            digest.update(chunk)
            tail.extend(chunk)
            if len(tail) > MAX_DRIVER_TAIL:
                del tail[:-MAX_DRIVER_TAIL]
        after = os.fstat(descriptor)
        _require_regular(after, MAX_DRIVER_SOURCE)
        if _signature(before) != _signature(after) or count != before.st_size:
            raise RetentionError("source-changed-during-read")
        current = os.open("root-file-driver.log", FILE_FLAGS, dir_fd=parent)
        try:
            if _signature(os.fstat(current)) != _signature(after):
                raise RetentionError("source-name-changed-during-read")
        finally:
            os.close(current)
        value = bytes(tail)
        return value, {
            "present": True,
            "source_bytes": count,
            "source_sha256": digest.hexdigest(),
            "retained_bytes": len(value),
            "retained_sha256": hashlib.sha256(value).hexdigest(),
            "retained_suffix": True,
        }
    finally:
        os.close(descriptor)
        os.close(parent)


def _make_private_directory(root_fd: int, name: str, uid: int, gid: int) -> int:
    os.mkdir(name, 0o700, dir_fd=root_fd)
    descriptor = os.open(name, DIR_FLAGS, dir_fd=root_fd)
    try:
        _set_owner_mode(descriptor, uid, gid, 0o700)
        return descriptor
    except BaseException:
        os.close(descriptor)
        raise


def _set_owner_mode(descriptor: int, uid: int, gid: int, mode: int) -> None:
    current = os.fstat(descriptor)
    if (current.st_uid, current.st_gid) != (uid, gid):
        os.fchown(descriptor, uid, gid)
    os.fchmod(descriptor, mode)
    final = os.fstat(descriptor)
    if (final.st_uid, final.st_gid, stat.S_IMODE(final.st_mode)) != (uid, gid, mode):
        raise RetentionError("destination-owner-or-mode-rejected")
    if stat.S_ISREG(final.st_mode) and final.st_nlink != 1:
        raise RetentionError("destination-link-count-rejected")


def _write_exclusive(parent_fd: int, name: str, data: bytes, uid: int, gid: int) -> None:
    descriptor = os.open(name, OUT_FLAGS, 0o600, dir_fd=parent_fd)
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
