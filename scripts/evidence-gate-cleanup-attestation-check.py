#!/usr/bin/env python3
"""Validate and preserve one non-claiming subject cleanup attestation.

The credentialless workflow runs this after the subject launcher, including
failure and cancellation paths.  This helper accepts only the runner's private
per-run supervisor directory and its bounded, fixed-schema completion record.
It does not authenticate the subject checkout or issue an Evidence claim.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
from typing import Any, Callable


STATE_PREFIX = "appsurface-subject-supervisor-"
STATE_PATTERN = re.compile(r"appsurface-subject-supervisor-[0-9a-f]{32}\Z")
ATTESTATION_NAME = "cleanup-attestation.json"
OUTPUT_NAME = "evidence-gate-cleanup-attestation.json"
MAX_ATTESTATION_BYTES = 4096
WAIT_SECONDS = 600
EXPECTED_KEYS = frozenset({
    "schemaVersion", "claimEligible", "published", "status", "trigger",
    "containerStatus", "mountStatus", "scratchStatus", "failureCode",
    "cleanupDeadlineSeconds",
})


class CleanupCheckError(Exception):
    """A stable, non-sensitive failure of the cleanup-record check."""


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _owned_directory(path: Path, *, uid: int, mode: int | None = None) -> bool:
    try:
        info = path.lstat()
    except OSError:
        return False
    return (
        stat.S_ISDIR(info.st_mode)
        and info.st_uid == uid
        and (mode is None or stat.S_IMODE(info.st_mode) == mode)
    )


def _read_attestation(path: Path, *, uid: int) -> bytes:
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(path, flags)
    except OSError:
        raise CleanupCheckError("cleanup-attestation-unavailable") from None
    try:
        info = os.fstat(descriptor)
        if (
            not stat.S_ISREG(info.st_mode)
            or info.st_uid != uid
            or stat.S_IMODE(info.st_mode) != 0o600
            or info.st_nlink != 1
            or info.st_size <= 0
            or info.st_size > MAX_ATTESTATION_BYTES
        ):
            raise CleanupCheckError("cleanup-attestation-unsafe")
        payload = os.read(descriptor, MAX_ATTESTATION_BYTES + 1)
        if len(payload) != info.st_size:
            raise CleanupCheckError("cleanup-attestation-unbounded")
        return payload
    finally:
        os.close(descriptor)


def _validate_attestation(payload: bytes) -> None:
    try:
        record = json.loads(payload.decode("ascii"), object_pairs_hook=_unique_object)
    except (UnicodeError, ValueError, RecursionError):
        raise CleanupCheckError("cleanup-attestation-malformed") from None
    if not isinstance(record, dict) or set(record) != EXPECTED_KEYS:
        raise CleanupCheckError("cleanup-attestation-malformed")
    if (
        type(record["schemaVersion"]) is not int
        or record["schemaVersion"] != 1
        or record["claimEligible"] is not False
        or record["published"] is not False
        or record["status"] != "complete"
        or record["trigger"] not in ("request", "parent-exit")
        or record["containerStatus"] not in ("removed", "absent")
        or record["mountStatus"] not in ("unmounted", "absent")
        or record["scratchStatus"] not in ("removed", "absent")
        or record["failureCode"] != "none"
        or type(record["cleanupDeadlineSeconds"]) is not int
        or record["cleanupDeadlineSeconds"] != WAIT_SECONDS
    ):
        raise CleanupCheckError("cleanup-incomplete")


def check_cleanup(
    runner_temp: Path,
    output: Path,
    *,
    uid: int | None = None,
    wait_seconds: float = WAIT_SECONDS,
    monotonic: Callable[[], float] = time.monotonic,
    sleep: Callable[[float], None] = time.sleep,
) -> None:
    """Wait for exactly one safe completion record and save its exact bytes."""
    runner_uid = os.geteuid() if uid is None else uid
    if not runner_temp.is_absolute() or not _owned_directory(runner_temp, uid=runner_uid):
        raise CleanupCheckError("runner-temp-unsafe")
    if output != runner_temp / OUTPUT_NAME or output.exists() or output.is_symlink():
        raise CleanupCheckError("cleanup-output-unsafe")
    try:
        states = [item for item in runner_temp.iterdir() if item.name.startswith(STATE_PREFIX)]
    except OSError:
        raise CleanupCheckError("cleanup-state-unavailable") from None
    if len(states) != 1 or STATE_PATTERN.fullmatch(states[0].name) is None:
        raise CleanupCheckError("cleanup-state-ambiguous")
    state = states[0]
    if not _owned_directory(state, uid=runner_uid, mode=0o700):
        raise CleanupCheckError("cleanup-state-unsafe")

    attestation = state / ATTESTATION_NAME
    deadline = monotonic() + wait_seconds
    while True:
        try:
            attestation.lstat()
            break
        except FileNotFoundError:
            if monotonic() >= deadline:
                raise CleanupCheckError("cleanup-attestation-timeout") from None
            sleep(min(0.25, max(0.0, deadline - monotonic())))
        except OSError:
            raise CleanupCheckError("cleanup-attestation-unavailable") from None
    payload = _read_attestation(attestation, uid=runner_uid)
    _validate_attestation(payload)

    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0)
    try:
        descriptor = os.open(output, flags, 0o600)
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
    except OSError:
        raise CleanupCheckError("cleanup-output-unavailable") from None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runner-temp", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    try:
        check_cleanup(args.runner_temp, args.output)
    except CleanupCheckError as error:
        print(f"subject cleanup check failed: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
