#!/usr/bin/env python3
"""Require a bounded completed subject cleanup record before trusted PR verification.

The trusted workflow requires the subject cleanup step and same-run upload to
succeed.  The record remains non-claiming: its JSON does not independently
attest the subject checkout, execution envelope, or producer outputs.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import stat
import sys
from typing import Any


MAX_PLAN_BYTES = 1_048_576
MAX_ATTESTATION_BYTES = 4_096
EXPECTED_KEYS = frozenset({
    "schemaVersion", "claimEligible", "published", "status", "trigger",
    "containerStatus", "mountStatus", "scratchStatus", "failureCode",
    "cleanupDeadlineSeconds",
})


class CleanupVerificationError(Exception):
    """A stable, non-sensitive reason to reject the downloaded cleanup record."""


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _read_regular(path: Path, maximum_bytes: int, code: str) -> bytes:
    """Read through no-follow directory descriptors, rejecting unsafe entries."""
    if not path.is_absolute() or any(part in (".", "..") for part in path.parts):
        raise CleanupVerificationError(code)
    directory_flags = os.O_RDONLY | os.O_DIRECTORY | getattr(os, "O_NOFOLLOW", 0)
    # A malicious FIFO must fail fstat instead of blocking the verifier at open.
    file_flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | os.O_NONBLOCK
    descriptor = -1
    try:
        descriptor = os.open("/", directory_flags)
        for part in path.parts[1:-1]:
            next_descriptor = os.open(part, directory_flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = next_descriptor
        file_descriptor = os.open(path.name, file_flags, dir_fd=descriptor)
        try:
            metadata = os.fstat(file_descriptor)
            if (
                not stat.S_ISREG(metadata.st_mode)
                or metadata.st_nlink != 1
                or metadata.st_size <= 0
                or metadata.st_size > maximum_bytes
            ):
                raise CleanupVerificationError(code)
            chunks: list[bytes] = []
            remaining = metadata.st_size
            while remaining:
                chunk = os.read(file_descriptor, remaining)
                if not chunk:
                    raise CleanupVerificationError(code)
                chunks.append(chunk)
                remaining -= len(chunk)
            if os.read(file_descriptor, 1):
                raise CleanupVerificationError(code)
            return b"".join(chunks)
        finally:
            os.close(file_descriptor)
    except (OSError, OverflowError, ValueError):
        raise CleanupVerificationError(code) from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _parse_json(payload: bytes, code: str) -> dict[str, Any]:
    try:
        document = json.loads(
            payload.decode("ascii"),
            object_pairs_hook=_unique_object,
        )
    except (UnicodeError, ValueError, RecursionError):
        raise CleanupVerificationError(code) from None
    if not isinstance(document, dict):
        raise CleanupVerificationError(code)
    return document


def verify_cleanup(plan_path: Path, attestation_path: Path) -> None:
    """Require completion for coverage; reject an unexpected record for empty work."""
    plan = _parse_json(_read_regular(plan_path, MAX_PLAN_BYTES, "ASEGC001"), "ASEGC001")
    profile = plan.get("Profile")
    profile_id = profile.get("Id") if isinstance(profile, dict) else None
    if profile_id == "documentation-only":
        if os.path.lexists(attestation_path):
            raise CleanupVerificationError("ASEGC004")
        return
    if profile_id != "code-coverage":
        raise CleanupVerificationError("ASEGC004")

    payload = _read_regular(attestation_path, MAX_ATTESTATION_BYTES, "ASEGC002")
    record = _parse_json(payload, "ASEGC002")
    if set(record) != EXPECTED_KEYS:
        raise CleanupVerificationError("ASEGC002")
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
        or record["cleanupDeadlineSeconds"] != 600
    ):
        raise CleanupVerificationError("ASEGC003")
    expected = (json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")
    if payload != expected:
        raise CleanupVerificationError("ASEGC002")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--attestation", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        verify_cleanup(arguments.plan, arguments.attestation)
    except CleanupVerificationError as error:
        print(f"{error}: Trusted cleanup verification failed closed.", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
