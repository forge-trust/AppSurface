#!/usr/bin/env python3
"""Publish the trusted PR verifier's one bounded GitHub and terminal verdict.

This runs only in the base-owned verifier job. It never consumes subject output
directly: the C# verifier has already checked the plan, manifest, GitHub state,
and any selected artifacts before writing these three create-new result files.
An absent, inconsistent, or ineligible result fails closed.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys


MAX_RESULT_BYTES = 4 * 1024 * 1024
MAX_SUMMARY_BYTES = 128 * 1024
CODE_PATTERN = re.compile(r"ASE[A-Z]{2,4}[0-9]{3}\Z")
FALLBACK_CODE = "ASEGH003"
FALLBACK_MARKDOWN = (
    b"## AppSurface evidence gate\n\n"
    b"- Verdict: **ineligible** (`ASEGH003`)\n"
    b"- Verified claim: `None`\n"
    b"- Diagnostic: A trusted gate result is unavailable or incomplete.\n"
)


class VerdictError(Exception):
    """A missing or inconsistent trusted verifier result."""


def _read_regular(path: Path, maximum_bytes: int) -> bytes:
    descriptor = -1
    try:
        descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0),
        )
        before = os.fstat(descriptor)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= maximum_bytes:
            raise VerdictError("The trusted verifier output is missing or unsafe.")
        data = bytearray()
        while len(data) <= maximum_bytes:
            chunk = os.read(descriptor, min(64 * 1024, maximum_bytes + 1 - len(data)))
            if not chunk:
                break
            data.extend(chunk)
        after = os.fstat(descriptor)
        if (
            len(data) != before.st_size
            or len(data) > maximum_bytes
            or (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns)
            != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
        ):
            raise VerdictError("The trusted verifier output changed or exceeded its bound.")
        return bytes(data)
    except (OSError, ValueError) as error:
        raise VerdictError("The trusted verifier output is unavailable.") from error
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _json_object(data: bytes) -> dict[str, object]:
    try:
        value = json.loads(
            data,
            object_pairs_hook=_unique_object,
            parse_constant=_reject_non_json_number,
        )
    except (UnicodeDecodeError, ValueError, RecursionError) as error:
        raise VerdictError("The trusted verifier JSON is malformed.") from error
    if not isinstance(value, dict):
        raise VerdictError("The trusted verifier JSON has no object result.")
    return value


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON property")
        result[key] = value
    return result


def _reject_non_json_number(_: str) -> None:
    raise ValueError("Non-JSON numeric value")


def _verified_verdict(directory: Path) -> tuple[bool, str, bytes]:
    machine = _json_object(_read_regular(directory / "evidence-gate-verification.json", MAX_RESULT_BYTES))
    summary = _json_object(_read_regular(directory / "evidence-gate-summary.json", MAX_SUMMARY_BYTES))
    markdown = _read_regular(directory / "evidence-gate-summary.md", MAX_SUMMARY_BYTES)
    try:
        markdown.decode("utf-8")
    except UnicodeDecodeError as error:
        raise VerdictError("The trusted verifier Markdown is malformed.") from error

    eligible = machine.get("IsEligible")
    code = machine.get("Code")
    diagnostic = machine.get("Diagnostic")
    plan_summary = machine.get("Summary")
    if (
        set(machine) != {"IsEligible", "Code", "Diagnostic", "Summary"}
        or set(summary) != {"eligible", "code", "diagnostic", "summary"}
        or type(eligible) is not bool
        or not isinstance(code, str)
        or CODE_PATTERN.fullmatch(code) is None
        or not isinstance(diagnostic, str)
        or not 0 < len(diagnostic) <= 1024
        or (eligible and (code != "ASEVG000" or not isinstance(plan_summary, dict)))
        or (not eligible and code == "ASEVG000")
        or (plan_summary is not None and not isinstance(plan_summary, dict))
        or summary.get("eligible") is not eligible
        or summary.get("code") != code
        or summary.get("diagnostic") != diagnostic
        or summary.get("summary") != plan_summary
    ):
        raise VerdictError("The trusted verifier outputs disagree.")

    has_evidence = plan_summary.get("ProfileHasEvidence") if isinstance(plan_summary, dict) else None
    if eligible and type(has_evidence) is not bool:
        raise VerdictError("The trusted verifier omitted the claim shape.")
    claim = "None" if not eligible else "TargetedComplete" if has_evidence else "NoEvidenceRequired"
    expected_header = (
        f"## AppSurface evidence gate\n\n"
        f"- Verdict: **{'eligible' if eligible else 'ineligible'}** (`{code}`)\n"
        f"- Verified claim: `{claim}`\n"
    ).encode("utf-8")
    if not markdown.startswith(expected_header):
        raise VerdictError("The trusted verifier Markdown disagrees with the machine result.")
    return eligible, code, markdown


def _append_summary(path: Path, content: bytes) -> None:
    descriptor = -1
    try:
        descriptor = os.open(
            path,
            os.O_WRONLY | os.O_APPEND | os.O_CREAT | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1:
            raise VerdictError("The GitHub step summary path is unsafe.")
        with os.fdopen(descriptor, "ab", closefd=True) as stream:
            descriptor = -1
            stream.write(content)
            stream.flush()
    except OSError as error:
        raise VerdictError("The GitHub step summary could not be written.") from error
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def finalize(
    verdict_directory: Path,
    step_summary: Path,
    controller_outcome: str,
    subject_outcome: str,
    verifier_outcome: str,
) -> int:
    upstream_success = all(
        outcome == "success" for outcome in (controller_outcome, subject_outcome, verifier_outcome)
    )
    try:
        eligible, code, markdown = _verified_verdict(verdict_directory)
    except VerdictError:
        eligible, code, markdown = False, FALLBACK_CODE, FALLBACK_MARKDOWN

    if not upstream_success:
        eligible = False
        if code == "ASEVG000":
            code, markdown = FALLBACK_CODE, FALLBACK_MARKDOWN

    try:
        _append_summary(step_summary, markdown)
    except VerdictError:
        print("gate=ineligible; code=ASEGH003; claim=None")
        print("ASEGH003: The trusted GitHub summary could not be published; no gate claim was issued.", file=sys.stderr)
        return 2

    claim = "None"
    if eligible:
        claim = "TargetedComplete" if b"- Verified claim: `TargetedComplete`" in markdown else "NoEvidenceRequired"
    print(f"gate={'eligible' if eligible else 'ineligible'}; code={code}; claim={claim}")
    if not eligible:
        print(f"{code}: A complete trusted gate claim is unavailable.", file=sys.stderr)
        return 2
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verdict-directory", type=Path, required=True)
    parser.add_argument("--step-summary", type=Path, required=True)
    parser.add_argument("--controller-outcome", required=True)
    parser.add_argument("--subject-outcome", required=True)
    parser.add_argument("--verifier-outcome", required=True)
    arguments = parser.parse_args()
    return finalize(
        arguments.verdict_directory,
        arguments.step_summary,
        arguments.controller_outcome,
        arguments.subject_outcome,
        arguments.verifier_outcome,
    )


if __name__ == "__main__":
    raise SystemExit(main())
