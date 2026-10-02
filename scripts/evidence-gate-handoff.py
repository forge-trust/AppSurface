#!/usr/bin/env python3
"""Create and consume the private, bounded controller-to-subject handoff.

The controller uses ``create`` after exact PR capture and base-owned v2 policy
planning. It archives only the captured head commit from the retained trusted
Git store and binds the archive, source diff, plan, and run identity. The
credentialless subject job uses ``execute`` to validate that bundle. An
explicitly empty documentation profile needs no OCI run; the supported code
profile materializes a regular-file-only snapshot and invokes the fixed offline
OCI launcher. The subject result is always non-claiming.

No GitHub token, trusted object store, executable policy authority, or verifier
input is included in the subject artifact. The bounded plan carries a policy
snapshot as data; the trusted verifier independently resolves it from its own
base-owned policy. The artifact is private to one workflow run and attempt;
its contents remain untrusted after crossing the job boundary.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import selectors
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
from typing import Any, Mapping, Sequence


MAX_SOURCE_DIFF_BYTES = 20 * 1024 * 1024
MAX_EVIDENCE_PLAN_BYTES = 4 * 1024 * 1024
MAX_SNAPSHOT_ARCHIVE_BYTES = 256 * 1024 * 1024
MAX_SNAPSHOT_EXPANDED_BYTES = 256 * 1024 * 1024
MAX_SNAPSHOT_FILES = 250_000
MAX_GIT_TREE_INVENTORY_BYTES = 64 * 1024 * 1024
MAX_GIT_ARCHIVE_SECONDS = 120
MAX_EXTRACT_SECONDS = 300
MAX_TRUSTED_SCRIPT_BYTES = 128 * 1024
MAX_RESULT_BYTES = 16 * 1024
MAX_TOTAL_HANDOFF_BYTES = MAX_SOURCE_DIFF_BYTES + MAX_EVIDENCE_PLAN_BYTES + MAX_SNAPSHOT_ARCHIVE_BYTES + 512 * 1024
MAX_GIT_STDERR_BYTES = 4096
SHA_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
POSITIVE_DECIMAL_PATTERN = re.compile(r"[1-9][0-9]{0,18}\Z")
SUBJECT_RESULT_STEP_NAMES = (
    "dotnet-sdk-version",
    "dotnet-runtime-list",
    "offline-locked-restore",
    "coverage-run",
    "coverage-gate",
)
TRUSTED_SUBJECT_FILES = (
    "evidence-gate-handoff.py",
    "evidence-gate-subject.py",
    "evidence-gate-subject-supervisor.py",
    "evidence-gate-cleanup-attestation-check.py",
    "evidence-gate-subject-entrypoint.py",
)
SUBJECT_IMAGE_ENVIRONMENT_VARIABLE = "EVIDENCE_GATE_SUBJECT_IMAGE"
SUBJECT_LIMITS = {
    "timeout_seconds": 1200,
    "memory_mib": 4096,
    "cpu_millis": 2000,
    "pids": 256,
    "output_bytes": 1024 * 1024,
}


class HandoffError(Exception):
    """A stable bounded failure code suitable for an untrusted subject job."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


def _canonical_json(value: Mapping[str, Any]) -> bytes:
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode("ascii")


def _validate_subject_execution_record(
    value: Any,
    *,
    require_complete: bool,
) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise HandoffError("ASEHB001", "The fixed subject execution record is missing or malformed.")
    status = value.get("status")
    expected_keys = {"claimEligible", "profileId", "schemaVersion", "status", "steps"}
    if status == "failed":
        expected_keys.add("diagnostic")
    if (
        set(value) != expected_keys
        or value.get("claimEligible") is not False
        or value.get("profileId") != "code-coverage"
        or type(value.get("schemaVersion")) is not int
        or value["schemaVersion"] != 1
        or not isinstance(status, str)
        or status not in {"completed", "failed"}
        or (require_complete and status != "completed")
        or not isinstance(value.get("steps"), list)
    ):
        raise HandoffError("ASEHB001", "The fixed subject execution record has an unexpected shape.")

    steps = value["steps"]
    if len(steps) > len(SUBJECT_RESULT_STEP_NAMES):
        raise HandoffError("ASEHB001", "The fixed subject execution record has too many steps.")
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
            or SHA256_PATTERN.fullmatch(step["stdoutSha256"]) is None
            or not isinstance(step.get("stderrSha256"), str)
            or SHA256_PATTERN.fullmatch(step["stderrSha256"]) is None
        ):
            raise HandoffError("ASEHB001", "The fixed subject execution record contains malformed step proof.")
        total_output_bytes += step["outputBytes"]
        if total_output_bytes > SUBJECT_LIMITS["output_bytes"]:
            raise HandoffError("ASEHB001", "The fixed subject execution record exceeds its output budget.")
        if index + 1 < len(steps) and step["exitCode"] != 0:
            raise HandoffError("ASEHB001", "A subject execution step failed before later steps were recorded.")

    if status == "completed" and (
        tuple(step["name"] for step in steps) != SUBJECT_RESULT_STEP_NAMES
        or any(step["exitCode"] != 0 for step in steps)
    ):
        raise HandoffError("ASEHB001", "The completed subject execution omitted a required successful step.")
    if status == "failed":
        diagnostic = value.get("diagnostic")
        if (
            not isinstance(diagnostic, dict)
            or set(diagnostic) != {"code", "message"}
            or not isinstance(diagnostic.get("code"), str)
            or not isinstance(diagnostic.get("message"), str)
        ):
            raise HandoffError("ASEHB001", "The failed subject execution omitted its typed diagnostic.")
    return value


def _make_execution_receipt(record: Mapping[str, Any], binding: Mapping[str, str]) -> dict[str, Any]:
    validated_record = _validate_subject_execution_record(
        record,
        require_complete=record.get("status") == "completed",
    )
    payload = {"binding": dict(binding), "record": dict(validated_record), "schemaVersion": 1}
    digest = hashlib.sha256(_canonical_json(payload)).hexdigest()
    return {**payload, "sha256": digest}


def _execution_receipt_binding(
    identity: Mapping[str, Any],
    manifest: Mapping[str, Any],
    profile_id: str,
) -> dict[str, str]:
    return {
        "baseRevision": str(identity["BaseRevision"]),
        "headRevision": str(identity["HeadRevision"]),
        "profileId": profile_id,
        "snapshotSha256": str(manifest["SnapshotArchiveSha256"]),
        "workflowRunAttempt": str(identity["WorkflowRunAttempt"]),
        "workflowRunId": str(identity["WorkflowRunId"]),
    }


def _validate_execution_receipt(
    value: Any,
    *,
    expected_binding: Mapping[str, str],
) -> None:
    if not isinstance(value, dict) or set(value) != {"binding", "record", "schemaVersion", "sha256"}:
        raise HandoffError("ASEHB001", "The bounded execution receipt has an unexpected shape.")
    if type(value.get("schemaVersion")) is not int or value["schemaVersion"] != 1:
        raise HandoffError("ASEHB001", "The bounded execution receipt schema version is unsupported.")
    binding = value.get("binding")
    if not isinstance(binding, dict) or binding != dict(expected_binding):
        raise HandoffError("ASEHB001", "The execution receipt is bound to a different run, revision, profile, or snapshot.")
    if not isinstance(value.get("sha256"), str) or SHA256_PATTERN.fullmatch(value["sha256"]) is None:
        raise HandoffError("ASEHB001", "The execution receipt digest is malformed.")
    record = _validate_subject_execution_record(value.get("record"), require_complete=True)
    try:
        digest = hashlib.sha256(
            _canonical_json({"binding": binding, "record": record, "schemaVersion": value["schemaVersion"]})
        ).hexdigest()
    except (TypeError, ValueError, RecursionError):
        raise HandoffError("ASEHB001", "The bounded execution receipt could not be canonicalized.") from None
    if value["sha256"] != digest:
        raise HandoffError("ASEHB001", "The bounded execution receipt digest does not match its contents.")


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key")
        result[key] = value
    return result


def _reject_json_constant(_value: str) -> None:
    raise ValueError("non-standard JSON constant")


def _read_json(path: Path, maximum_bytes: int, description: str) -> tuple[Mapping[str, Any], bytes]:
    raw = _read_regular_file(path, maximum_bytes, description)
    try:
        parsed = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=_reject_json_constant)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise HandoffError("ASEHB001", f"{description} is malformed JSON.") from None
    if not isinstance(parsed, dict) or _canonical_json(parsed) != raw:
        raise HandoffError("ASEHB001", f"{description} is not a canonical JSON object.")
    return parsed, raw


def _canonical_evidence_json(value: Mapping[str, Any]) -> bytes:
    """Match EvidenceCanonicalJson's compact, ordinal-property UTF-8 representation."""
    encoded = _canonical_json(value)
    escaped_ascii = {
        ord("+"): b"\\u002B",
        ord("<"): b"\\u003C",
        ord(">"): b"\\u003E",
        ord("&"): b"\\u0026",
        ord("'"): b"\\u0027",
        ord("`"): b"\\u0060",
    }
    output = bytearray()
    in_string = False
    index = 0
    while index < len(encoded):
        character = encoded[index]
        if character == ord('"'):
            in_string = not in_string
            output.append(character)
        elif in_string and character == ord("\\") and index + 1 < len(encoded):
            next_character = encoded[index + 1]
            if next_character == ord('"'):
                output.extend(b"\\u0022")
            elif next_character == ord("u") and index + 5 < len(encoded):
                output.extend(b"\\u" + encoded[index + 2 : index + 6].upper())
                index += 4
            else:
                output.extend(encoded[index : index + 2])
            index += 1
        elif in_string and character in escaped_ascii:
            output.extend(escaped_ascii[character])
        else:
            output.append(character)
        index += 1
    return bytes(output)


def _read_evidence_plan(path: Path) -> tuple[Mapping[str, Any], bytes]:
    raw = _read_regular_file(path, MAX_EVIDENCE_PLAN_BYTES, "The base-owned EvidencePlan")
    try:
        parsed = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise HandoffError("ASEHB001", "The base-owned EvidencePlan is malformed JSON.") from None
    try:
        canonical = _canonical_evidence_json(parsed) if isinstance(parsed, dict) else b""
    except (TypeError, ValueError, RecursionError):
        raise HandoffError("ASEHB001", "The base-owned EvidencePlan is not a bounded canonical JSON object.") from None
    if not isinstance(parsed, dict) or canonical != raw:
        raise HandoffError("ASEHB001", "The base-owned EvidencePlan is not a canonical JSON object.")
    return parsed, raw


def _validate_evidence_plan(
    plan: Mapping[str, Any],
    identity: Mapping[str, Any],
    source_diff_digest: str,
) -> None:
    required = {
        "BaseRevision",
        "ChangedPaths",
        "ContractVersion",
        "DiffDigest",
        "HeadRevision",
        "MatchedRuleIds",
        "NameStatusDigest",
        "PlanDigest",
        "PolicyDigest",
        "PolicyId",
        "PolicySnapshot",
        "Profile",
        "PullRequestRunIdentity",
        "SourceDiffDigest",
    }
    if not required.issubset(plan) or plan.get("ContractVersion") != "2.0":
        raise HandoffError("ASEHB001", "The handoff requires a revision-bound EvidencePlan contract version 2.0.")
    for key in ("BaseRevision", "HeadRevision"):
        value = plan.get(key)
        if not isinstance(value, str) or SHA_PATTERN.fullmatch(value) is None or value != identity[key]:
            raise HandoffError("ASEHB001", f"The EvidencePlan {key} does not match the captured pull-request identity.")
    for key in ("PlanDigest", "PolicyDigest", "DiffDigest", "NameStatusDigest"):
        if not isinstance(plan.get(key), str) or re.fullmatch(r"[0-9a-f]{64}", plan[key]) is None:
            raise HandoffError("ASEHB001", f"The EvidencePlan {key} is not a lower-case SHA-256 digest.")
    if plan.get("SourceDiffDigest") != source_diff_digest:
        raise HandoffError("ASEHB001", "The EvidencePlan source diff digest does not match the captured source diff.")
    if not isinstance(plan.get("ChangedPaths"), list) or not isinstance(plan.get("MatchedRuleIds"), list):
        raise HandoffError("ASEHB001", "The EvidencePlan change inventory is malformed.")
    if not isinstance(plan.get("PolicyId"), str) or not plan["PolicyId"] or len(plan["PolicyId"]) > 128:
        raise HandoffError("ASEHB001", "The EvidencePlan policy identifier is malformed.")

    plan_identity = plan.get("PullRequestRunIdentity")
    identity_fields = {
        "RepositoryId",
        "HeadRepositoryId",
        "PullRequestNumber",
        "TargetBranch",
        "WorkflowRunId",
        "WorkflowRunAttempt",
    }
    expected_identity = {key: identity[key] for key in identity_fields}
    if not isinstance(plan_identity, dict) or set(plan_identity) != identity_fields:
        raise HandoffError("ASEHB001", "The EvidencePlan pull-request run identity has an unexpected shape.")
    for key, expected in expected_identity.items():
        actual = plan_identity[key]
        if type(actual) is not type(expected) or actual != expected:
            raise HandoffError("ASEHB001", "The EvidencePlan pull-request run identity does not match the controller capture.")

    profile = plan.get("Profile")
    if not isinstance(profile, dict):
        raise HandoffError("ASEHB001", "The EvidencePlan selected profile is malformed.")
    profile_id = profile.get("Id")
    if (
        not isinstance(profile_id, str)
        or len(profile_id) > 64
        or re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", profile_id) is None
    ):
        raise HandoffError("ASEHB001", "The EvidencePlan profile identifier is not bounded.")
    if profile.get("Scope") != "Targeted" or any(
        not isinstance(profile.get(key), list) for key in ("Resources", "Producers", "Obligations")
    ):
        raise HandoffError("ASEHB001", "The EvidencePlan profile is not a well-formed targeted profile.")

    policy = plan.get("PolicySnapshot")
    profiles = policy.get("Profiles") if isinstance(policy, dict) else None
    if not isinstance(profiles, list):
        raise HandoffError("ASEHB001", "The EvidencePlan policy snapshot has no closed profile set.")
    matching_profiles = [item for item in profiles if isinstance(item, dict) and item.get("Id") == profile_id]
    if len(matching_profiles) != 1 or matching_profiles[0] != profile:
        raise HandoffError("ASEHB001", "The EvidencePlan profile is not closed by its policy snapshot.")
    if profile_id == "documentation-only" and any(profile[key] for key in ("Resources", "Producers", "Obligations")):
        raise HandoffError("ASEHB001", "The documentation-only EvidencePlan profile must be explicitly empty.")


def _read_regular_file(path: Path, maximum_bytes: int, description: str) -> bytes:
    try:
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
    except (OSError, ValueError):
        raise HandoffError("ASEHB001", f"{description} is unavailable or unsafe.") from None
    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > maximum_bytes:
            raise HandoffError("ASEHB001", f"{description} is not a bounded regular file.")
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, min(1024 * 1024, maximum_bytes + 1 - total))
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
            if total > maximum_bytes:
                raise HandoffError("ASEHB001", f"{description} exceeds its byte limit.")
        return b"".join(chunks)
    except OSError:
        raise HandoffError("ASEHB001", f"{description} could not be read safely.") from None
    finally:
        os.close(descriptor)


def _write_regular_file(path: Path, content: bytes, mode: int = 0o600) -> None:
    try:
        descriptor = os.open(
            path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            mode,
        )
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
    except OSError:
        raise HandoffError("ASEHB002", "The private handoff could not be written safely.") from None


def _positive_integer(value: Any, name: str) -> int:
    if type(value) is not int or value <= 0 or value > (1 << 63) - 1:
        raise HandoffError("ASEHB001", f"{name} is not a positive bounded integer.")
    return value


def _validate_identity(identity: Mapping[str, Any]) -> None:
    required = {
        "BaseRevision",
        "HeadRevision",
        "HeadRepositoryId",
        "PullRequestNumber",
        "RepositoryId",
        "TargetBranch",
        "WorkflowRunAttempt",
        "WorkflowRunId",
    }
    if set(identity) != required:
        raise HandoffError("ASEHB001", "The captured pull-request identity has an unexpected shape.")
    if identity["HeadRepositoryId"] != identity["RepositoryId"]:
        raise HandoffError("ASEHB001", "A credentialless subject snapshot requires a same-repository pull request.")
    for key in ("BaseRevision", "HeadRevision"):
        if not isinstance(identity[key], str) or SHA_PATTERN.fullmatch(identity[key]) is None:
            raise HandoffError("ASEHB001", f"The captured {key} is not a full lower-case Git object ID.")
    if len(identity["BaseRevision"]) != len(identity["HeadRevision"]):
        raise HandoffError("ASEHB001", "The captured commit IDs use different Git object formats.")
    for key in ("HeadRepositoryId", "PullRequestNumber", "RepositoryId", "WorkflowRunAttempt", "WorkflowRunId"):
        _positive_integer(identity[key], key)
    branch = identity["TargetBranch"]
    if not isinstance(branch, str) or not branch or len(branch) > 128 or any(c in branch for c in "\x00\r\n"):
        raise HandoffError("ASEHB001", "The captured target branch is malformed.")


def _new_path(value: str | os.PathLike[str], name: str) -> Path:
    raw = os.fspath(value)
    if not raw or "\x00" in raw or ".." in Path(raw).parts:
        raise HandoffError("ASEHB002", f"The {name} path is malformed.")
    path = Path(raw).absolute()
    current = Path(path.anchor)
    try:
        for component in path.parts[1:]:
            current = current / component
            metadata = current.lstat()
            if stat.S_ISLNK(metadata.st_mode):
                raise HandoffError("ASEHB002", f"The {name} path traverses a symbolic link.")
    except HandoffError:
        raise
    except OSError:
        raise HandoffError("ASEHB002", f"The {name} path is unavailable or unsafe.") from None
    return path


def _new_output_directory(value: str | os.PathLike[str], name: str) -> Path:
    raw = os.fspath(value)
    if not raw or "\x00" in raw or ".." in Path(raw).parts:
        raise HandoffError("ASEHB002", f"The {name} path is malformed.")
    path = Path(raw).absolute()
    parent = _new_path(path.parent, f"{name} parent")
    try:
        if not parent.is_dir() or path.exists() or path.is_symlink():
            raise HandoffError("ASEHB002", f"The {name} directory must be new under an existing parent.")
    except OSError:
        raise HandoffError("ASEHB002", f"The {name} directory is unavailable or unsafe.") from None
    return path


def _git_environment() -> dict[str, str]:
    return {
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "LC_ALL": "C",
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_CONFIG_GLOBAL": os.devnull,
        "GIT_CONFIG_COUNT": "1",
        "GIT_CONFIG_KEY_0": "core.attributesFile",
        "GIT_CONFIG_VALUE_0": os.devnull,
        "GIT_CONFIG_PARAMETERS": "",
        "GIT_NO_REPLACE_OBJECTS": "1",
        "GIT_ATTR_NOSYSTEM": "1",
        "GIT_TERMINAL_PROMPT": "0",
        "GIT_PAGER": "cat",
    }


def _run_git_tree_inventory(repository: Path, revision: str) -> dict[str, tuple[str, str]]:
    command = [
        "git",
        "--no-pager",
        "--no-replace-objects",
        f"--git-dir={repository}",
        "ls-tree",
        "-r",
        "-z",
        "--full-tree",
        "--no-abbrev",
        revision,
    ]
    try:
        process = subprocess.Popen(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            close_fds=True,
            env=_git_environment(),
        )
    except (OSError, ValueError):
        raise HandoffError("ASEHB003", "The captured Git tree inventory process could not start.") from None

    assert process.stdout is not None and process.stderr is not None
    selector = selectors.DefaultSelector()
    output = bytearray()
    stderr_bytes = 0
    try:
        os.set_blocking(process.stdout.fileno(), False)
        os.set_blocking(process.stderr.fileno(), False)
        selector.register(process.stdout, selectors.EVENT_READ, "inventory")
        selector.register(process.stderr, selectors.EVENT_READ, "stderr")
        deadline = time.monotonic() + MAX_GIT_ARCHIVE_SECONDS
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise HandoffError("ASEHB003", "The bounded Git tree inventory deadline expired.")
            ready = selector.select(min(remaining, 0.25))
            if not ready and process.poll() is not None:
                continue
            for key, _ in ready:
                try:
                    chunk = os.read(key.fileobj.fileno(), 64 * 1024)
                except OSError:
                    raise HandoffError("ASEHB003", "The captured Git tree inventory could not be read.") from None
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                    continue
                if key.data == "inventory":
                    if len(output) + len(chunk) > MAX_GIT_TREE_INVENTORY_BYTES:
                        raise HandoffError("ASEHB003", "The captured Git tree inventory exceeds its byte limit.")
                    output.extend(chunk)
                else:
                    stderr_bytes += len(chunk)
                    if stderr_bytes > MAX_GIT_STDERR_BYTES:
                        raise HandoffError("ASEHB003", "The captured Git tree diagnostics exceed their byte limit.")
        try:
            return_code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise HandoffError("ASEHB003", "The bounded Git tree inventory deadline expired.") from None
        if return_code != 0:
            raise HandoffError("ASEHB003", "Git could not inventory the captured head commit.")
    except BaseException:
        if process.poll() is None:
            try:
                process.kill()
                process.wait(timeout=2)
            except (OSError, subprocess.TimeoutExpired):
                pass
        raise
    finally:
        selector.close()
        for pipe in (process.stdout, process.stderr):
            if not pipe.closed:
                pipe.close()

    inventory: dict[str, tuple[str, str]] = {}
    folded_paths: set[str] = set()
    records = bytes(output).split(b"\0")
    if records[-1] != b"":
        raise HandoffError("ASEHB004", "The captured Git tree inventory is malformed.")
    for record in records[:-1]:
        try:
            header, raw_path = record.split(b"\t", 1)
            mode_bytes, object_type_bytes, object_id_bytes = header.split(b" ")
            path = raw_path.decode("utf-8", errors="strict")
            normalized = "/".join(_member_path(path))
            mode = mode_bytes.decode("ascii")
            object_type = object_type_bytes.decode("ascii")
            object_id = object_id_bytes.decode("ascii")
        except (UnicodeDecodeError, ValueError):
            raise HandoffError("ASEHB004", "The captured Git tree inventory contains an unsafe entry.") from None
        if mode not in {"100644", "100755"} or object_type != "blob":
            raise HandoffError("ASEHB004", "The captured Git tree contains a symlink, submodule, or unsupported entry.")
        if re.fullmatch(r"(?:[0-9a-f]{40}|[0-9a-f]{64})", object_id) is None:
            raise HandoffError("ASEHB004", "The captured Git tree contains an invalid blob object ID.")
        folded = normalized.casefold()
        if normalized in inventory or folded in folded_paths:
            raise HandoffError("ASEHB004", "The captured Git tree contains duplicate or case-colliding paths.")
        if len(inventory) >= MAX_SNAPSHOT_FILES:
            raise HandoffError("ASEHB004", "The captured Git tree exceeds its file-count limit.")
        inventory[normalized] = (mode, object_id)
        folded_paths.add(folded)
    if not inventory:
        raise HandoffError("ASEHB004", "The captured Git tree contains no regular files.")
    return inventory


def _run_git_archive(repository: Path, revision: str, destination: Path) -> int:
    command = ["git", "--no-pager", f"--git-dir={repository}", "archive", "--format=tar", revision]
    environment = _git_environment()
    try:
        process = subprocess.Popen(
            command,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            close_fds=True,
            env=environment,
        )
    except (OSError, ValueError):
        raise HandoffError("ASEHB003", "The trusted Git archive process could not start.") from None

    assert process.stdout is not None and process.stderr is not None
    selector = selectors.DefaultSelector()
    try:
        os.set_blocking(process.stdout.fileno(), False)
        os.set_blocking(process.stderr.fileno(), False)
        selector.register(process.stdout, selectors.EVENT_READ, "archive")
        selector.register(process.stderr, selectors.EVENT_READ, "stderr")
        deadline = time.monotonic() + MAX_GIT_ARCHIVE_SECONDS
        archive_bytes = 0
        stderr_bytes = 0
        descriptor = os.open(
            destination,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        with os.fdopen(descriptor, "wb") as output:
            while selector.get_map():
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise HandoffError("ASEHB003", "The bounded Git archive deadline expired.")
                ready = selector.select(min(remaining, 0.25))
                if not ready and process.poll() is not None:
                    # Drain both pipes to EOF before checking the final status.
                    continue
                for key, _ in ready:
                    try:
                        chunk = os.read(key.fileobj.fileno(), 64 * 1024)
                    except OSError:
                        raise HandoffError("ASEHB003", "The trusted Git archive output could not be read.") from None
                    if not chunk:
                        selector.unregister(key.fileobj)
                        key.fileobj.close()
                        continue
                    if key.data == "archive":
                        archive_bytes += len(chunk)
                        if archive_bytes > MAX_SNAPSHOT_ARCHIVE_BYTES:
                            raise HandoffError("ASEHB003", "The subject source snapshot exceeds its archive byte limit.")
                        output.write(chunk)
                    else:
                        stderr_bytes += len(chunk)
                        if stderr_bytes > MAX_GIT_STDERR_BYTES:
                            raise HandoffError("ASEHB003", "The trusted Git archive diagnostics exceed their byte limit.")
            output.flush()
            os.fsync(output.fileno())
        try:
            return_code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
        except subprocess.TimeoutExpired:
            raise HandoffError("ASEHB003", "The bounded Git archive deadline expired.") from None
        if return_code != 0:
            raise HandoffError("ASEHB003", "Git could not archive the captured head commit.")
        if archive_bytes <= 0:
            raise HandoffError("ASEHB003", "Git produced an empty subject source snapshot.")
        return archive_bytes
    except BaseException:
        if process.poll() is None:
            try:
                process.kill()
                process.wait(timeout=2)
            except (OSError, subprocess.TimeoutExpired):
                pass
        try:
            destination.unlink(missing_ok=True)
        except OSError:
            pass
        raise
    finally:
        selector.close()
        for pipe in (process.stdout, process.stderr):
            if not pipe.closed:
                pipe.close()


def _member_path(name: str) -> tuple[str, ...]:
    if not isinstance(name, str) or not name or "\x00" in name or "\\" in name or name.startswith("/"):
        raise HandoffError("ASEHB004", "The subject source archive contains an unsafe path.")
    normalized = name[:-1] if name.endswith("/") else name
    parts = normalized.split("/")
    if not parts or any(part in {"", ".", ".."} for part in parts):
        raise HandoffError("ASEHB004", "The subject source archive contains an unsafe path.")
    try:
        normalized.encode("utf-8", errors="strict")
    except UnicodeEncodeError:
        raise HandoffError("ASEHB004", "The subject source archive path is not valid UTF-8.") from None
    return tuple(parts)


def _scan_archive(
    path: Path,
    expected_blobs: Mapping[str, tuple[str, str]] | None = None,
) -> tuple[int, int, set[str]]:
    metadata = path.lstat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_size <= 0 or metadata.st_size > MAX_SNAPSHOT_ARCHIVE_BYTES:
        raise HandoffError("ASEHB004", "The subject source archive is not a bounded regular file.")
    count = 0
    expanded_bytes = 0
    seen: set[str] = set()
    folded_seen: set[str] = set()
    names: set[str] = set()
    regular_files: set[str] = set()
    directories: set[str] = set()
    deadline = time.monotonic() + MAX_EXTRACT_SECONDS
    expected_directories: set[str] | None = None
    if expected_blobs is not None:
        expected_directories = set()
        for file_path in expected_blobs:
            parts = file_path.split("/")
            expected_directories.update("/".join(parts[:index]) for index in range(1, len(parts)))
    try:
        with tarfile.open(path, mode="r:") as archive:
            for member in archive:
                if time.monotonic() > deadline:
                    raise HandoffError("ASEHB004", "The bounded subject archive inspection deadline expired.")
                parts = _member_path(member.name)
                normalized = "/".join(parts)
                folded = normalized.casefold()
                if normalized in seen or folded in folded_seen:
                    raise HandoffError("ASEHB004", "The subject source archive contains duplicate or case-colliding paths.")
                seen.add(normalized)
                folded_seen.add(folded)
                names.add(normalized)
                count += 1
                if count > MAX_SNAPSHOT_FILES:
                    raise HandoffError("ASEHB004", "The subject source archive exceeds its file-count limit.")
                if member.isdir():
                    directories.add(normalized)
                    continue
                if not member.isreg() or member.issym() or member.islnk() or member.size < 0:
                    raise HandoffError("ASEHB004", "The subject source archive contains a link or special file.")
                regular_files.add(normalized)
                expected_blob = None if expected_blobs is None else expected_blobs.get(normalized)
                if expected_blobs is not None and expected_blob is None:
                    raise HandoffError("ASEHB004", "The subject source archive contains a path absent from the captured Git tree.")
                if expected_blob is not None:
                    expected_mode, object_id = expected_blob
                    if bool(member.mode & 0o111) != (expected_mode == "100755"):
                        raise HandoffError("ASEHB004", "The subject source archive changes a captured Git file mode.")
                    digest = hashlib.sha1() if len(object_id) == 40 else hashlib.sha256()
                    digest.update(b"blob " + str(member.size).encode("ascii") + b"\0")
                    source = archive.extractfile(member)
                    if source is None:
                        raise HandoffError("ASEHB004", "A subject source archive file could not be read.")
                    bytes_read = 0
                    with source:
                        while bytes_read < member.size:
                            if time.monotonic() > deadline:
                                raise HandoffError("ASEHB004", "The bounded subject archive inspection deadline expired.")
                            chunk = source.read(min(1024 * 1024, member.size - bytes_read))
                            if not chunk:
                                raise HandoffError("ASEHB004", "A subject source archive file was truncated.")
                            digest.update(chunk)
                            bytes_read += len(chunk)
                        if source.read(1):
                            raise HandoffError("ASEHB004", "A subject source archive file exceeded its declared size.")
                    if digest.hexdigest() != object_id:
                        raise HandoffError("ASEHB004", "A subject source archive file differs from its captured Git blob.")
                expanded_bytes += member.size
                if expanded_bytes > MAX_SNAPSHOT_EXPANDED_BYTES:
                    raise HandoffError("ASEHB004", "The expanded subject source snapshot exceeds its byte limit.")
    except HandoffError:
        raise
    except (OSError, tarfile.TarError, EOFError, ValueError):
        raise HandoffError("ASEHB004", "The subject source archive is malformed or unreadable.") from None
    if count == 0:
        raise HandoffError("ASEHB004", "The subject source archive contains no entries.")
    for normalized in seen:
        parts = normalized.split("/")
        parents = ("/".join(parts[:index]) for index in range(1, len(parts)))
        if any(parent not in directories for parent in parents):
            raise HandoffError("ASEHB004", "The subject source archive omits or conflicts with a parent directory entry.")
    if expected_blobs is not None:
        if regular_files != set(expected_blobs):
            raise HandoffError("ASEHB004", "The subject source archive omits or adds a regular file from the captured Git tree.")
        if directories != expected_directories:
            raise HandoffError("ASEHB004", "The subject source archive directory paths differ from the captured Git tree.")
    return count, expanded_bytes, names


def _sha256_file(path: Path, maximum_bytes: int) -> tuple[str, int]:
    try:
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
    except OSError:
        raise HandoffError("ASEHB001", "A handoff digest input is unavailable or unsafe.") from None
    digest = hashlib.sha256()
    total = 0
    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode):
            raise HandoffError("ASEHB001", "A handoff digest input is not a regular file.")
        while True:
            chunk = os.read(descriptor, 1024 * 1024)
            if not chunk:
                break
            total += len(chunk)
            if total > maximum_bytes:
                raise HandoffError("ASEHB001", "A handoff digest input exceeds its byte limit.")
            digest.update(chunk)
    except OSError:
        raise HandoffError("ASEHB001", "A handoff digest input could not be read safely.") from None
    finally:
        os.close(descriptor)
    return digest.hexdigest(), total


def _safe_regular_children(root: Path) -> dict[str, Path]:
    try:
        root_info = root.lstat()
        if not stat.S_ISDIR(root_info.st_mode):
            raise HandoffError("ASEHB001", "The downloaded handoff root is not a physical directory.")
        entries = list(root.iterdir())
    except HandoffError:
        raise
    except OSError:
        raise HandoffError("ASEHB001", "The downloaded handoff directory is unavailable.") from None
    result: dict[str, Path] = {}
    total = 0
    for entry in entries:
        try:
            metadata = entry.lstat()
        except OSError:
            raise HandoffError("ASEHB001", "A downloaded handoff entry is unavailable.") from None
        if entry.name in result or not stat.S_ISREG(metadata.st_mode):
            raise HandoffError("ASEHB001", "The downloaded handoff contains an unexpected entry type or duplicate.")
        total += metadata.st_size
        if total > MAX_TOTAL_HANDOFF_BYTES:
            raise HandoffError("ASEHB001", "The downloaded handoff exceeds its aggregate byte limit.")
        result[entry.name] = entry
    return result


def create_handoff(
    *,
    capture_directory: str | os.PathLike[str],
    trusted_scripts_directory: str | os.PathLike[str],
    output_directory: str | os.PathLike[str],
    plan_file: str | os.PathLike[str] | None = None,
) -> str:
    capture = _new_path(capture_directory, "capture")
    scripts = _new_path(trusted_scripts_directory, "trusted scripts")
    output = _new_output_directory(output_directory, "handoff output")
    if not stat.S_ISDIR(capture.stat().st_mode) or not stat.S_ISDIR(scripts.stat().st_mode):
        raise HandoffError("ASEHB002", "The trusted capture or script directory is unavailable.")

    try:
        entries = {path.name: path for path in capture.iterdir()}
    except OSError:
        raise HandoffError("ASEHB002", "The trusted capture directory is unavailable.") from None
    stage: Path | None = None
    try:
        stage = Path(tempfile.mkdtemp(prefix=f".{output.name}.stage-", dir=output.parent))
        os.chmod(stage, 0o700)
        trusted_hashes: dict[str, str] = {}
        if "observation.json" in entries:
            if plan_file is not None:
                raise HandoffError("ASEHB002", "A fork observation handoff cannot include an EvidencePlan.")
            if set(entries) != {"observation.json"}:
                raise HandoffError("ASEHB002", "The fork observation capture contains an unexpected entry.")
            observation, _ = _read_json(entries["observation.json"], 64 * 1024, "The fork observation")
            if observation.get("Mode") != "ObservationOnly":
                raise HandoffError("ASEHB002", "The fork capture is not observation-only.")
            _copy_trusted_files(scripts, stage, ("evidence-gate-handoff.py",), trusted_hashes)
            observation_bytes = _canonical_json(observation)
            _write_regular_file(stage / "observation.json", observation_bytes)
            manifest = {
                "Mode": "ObservationOnly",
                "SchemaVersion": 1,
                "TrustedFiles": trusted_hashes,
                "WorkflowRunAttempt": observation.get("WorkflowRunAttempt"),
                "WorkflowRunId": observation.get("WorkflowRunId"),
            }
            _write_regular_file(stage / "handoff.json", _canonical_json(manifest))
            _publish_directory(stage, output)
            stage = None
            return "observation"

        if set(entries) != {"source.diff", "pull-request-run-identity.json", "repository.git"}:
            raise HandoffError("ASEHB002", "The captured pull-request inputs have an unexpected file set.")
        identity, _ = _read_json(entries["pull-request-run-identity.json"], 64 * 1024, "The pull-request run identity")
        _validate_identity(identity)
        if plan_file is None:
            raise HandoffError("ASEHB001", "A base-owned EvidencePlan is required for a same-repository capture.")
        plan_path = _new_path(plan_file, "EvidencePlan")
        if not entries["repository.git"].is_dir() or entries["repository.git"].is_symlink():
            raise HandoffError("ASEHB002", "The retained trusted Git object store is unavailable.")
        source_diff = _read_regular_file(entries["source.diff"], MAX_SOURCE_DIFF_BYTES, "The captured source diff")
        diff_digest = hashlib.sha256(source_diff).hexdigest()
        evidence_plan, evidence_plan_bytes = _read_evidence_plan(plan_path)
        _validate_evidence_plan(evidence_plan, identity, diff_digest)
        _write_regular_file(stage / "source.diff", source_diff)
        _write_regular_file(stage / "pull-request-run-identity.json", _canonical_json(identity))
        _write_regular_file(stage / "evidence-plan.json", evidence_plan_bytes)

        archive_path = stage / "subject.snapshot.tar"
        git_inventory = _run_git_tree_inventory(entries["repository.git"], identity["HeadRevision"])
        _run_git_archive(entries["repository.git"], identity["HeadRevision"], archive_path)
        file_count, expanded_bytes, _ = _scan_archive(archive_path, git_inventory)
        archive_digest, archive_bytes = _sha256_file(archive_path, MAX_SNAPSHOT_ARCHIVE_BYTES)
        _copy_trusted_files(scripts, stage, TRUSTED_SUBJECT_FILES, trusted_hashes)
        manifest = {
            "BaseRevision": identity["BaseRevision"],
            "EvidencePlanBytes": len(evidence_plan_bytes),
            "EvidencePlanSha256": hashlib.sha256(evidence_plan_bytes).hexdigest(),
            "HeadRevision": identity["HeadRevision"],
            "Mode": "SubjectSnapshot",
            "PullRequestNumber": identity["PullRequestNumber"],
            "RepositoryId": identity["RepositoryId"],
            "SchemaVersion": 1,
            "SourceDiffBytes": len(source_diff),
            "SourceDiffSha256": diff_digest,
            "SnapshotArchiveBytes": archive_bytes,
            "SnapshotArchiveEntryCount": file_count,
            "SnapshotArchiveExpandedBytes": expanded_bytes,
            "SnapshotArchiveSha256": archive_digest,
            "TargetBranch": identity["TargetBranch"],
            "TrustedFiles": trusted_hashes,
            "WorkflowRunAttempt": identity["WorkflowRunAttempt"],
            "WorkflowRunId": identity["WorkflowRunId"],
        }
        _write_regular_file(stage / "handoff.json", _canonical_json(manifest))
        _validate_bundle_directory(stage, manifest)
        _publish_directory(stage, output)
        stage = None
        return "captured"
    except OSError:
        raise HandoffError("ASEHB002", "The private controller handoff failed closed.") from None
    finally:
        if stage is not None:
            _remove_private_directory(stage)


def _copy_trusted_files(source: Path, destination: Path, names: Sequence[str], hashes: dict[str, str]) -> None:
    for name in names:
        content = _read_regular_file(source / name, MAX_TRUSTED_SCRIPT_BYTES, f"Trusted subject helper {name}")
        _write_regular_file(destination / name, content)
        hashes[name] = hashlib.sha256(content).hexdigest()


def _publish_directory(stage: Path, output: Path) -> None:
    if output.exists() or output.is_symlink():
        raise HandoffError("ASEHB002", "The private handoff output changed during preparation.")
    try:
        os.rename(stage, output)
    except OSError:
        raise HandoffError("ASEHB002", "The private handoff could not be published atomically.") from None


def _remove_private_directory(path: Path) -> None:
    try:
        if path.is_dir() and not path.is_symlink():
            shutil.rmtree(path)
    except OSError:
        pass


def _validate_bundle_directory(
    root: Path,
    manifest: Mapping[str, Any],
) -> tuple[Mapping[str, Any] | None, Path | None, Mapping[str, Any] | None]:
    entries = _safe_regular_children(root)
    mode = manifest.get("Mode")
    if type(manifest.get("SchemaVersion")) is not int or manifest["SchemaVersion"] != 1:
        raise HandoffError("ASEHB001", "The private handoff schema version is unsupported.")
    trusted_files = manifest.get("TrustedFiles")
    if not isinstance(trusted_files, dict):
        raise HandoffError("ASEHB001", "The private handoff trusted-file inventory is malformed.")
    expected_script_names = ("evidence-gate-handoff.py",) if mode == "ObservationOnly" else TRUSTED_SUBJECT_FILES
    expected_names = {"handoff.json", *expected_script_names}
    identity: Mapping[str, Any] | None = None
    archive_path: Path | None = None
    evidence_plan: Mapping[str, Any] | None = None
    if mode == "SubjectSnapshot":
        expected_names.update({"evidence-plan.json", "source.diff", "pull-request-run-identity.json", "subject.snapshot.tar"})
        if set(entries) != expected_names or set(trusted_files) != set(expected_script_names):
            raise HandoffError("ASEHB001", "The downloaded private handoff file set is invalid.")
        identity, _ = _read_json(entries.get("pull-request-run-identity.json", root / "missing"), 64 * 1024, "The pull-request run identity")
        _validate_identity(identity)
        if (
            manifest.get("BaseRevision") != identity["BaseRevision"]
            or manifest.get("HeadRevision") != identity["HeadRevision"]
            or manifest.get("PullRequestNumber") != identity["PullRequestNumber"]
            or manifest.get("RepositoryId") != identity["RepositoryId"]
            or manifest.get("TargetBranch") != identity["TargetBranch"]
            or manifest.get("WorkflowRunId") != identity["WorkflowRunId"]
            or manifest.get("WorkflowRunAttempt") != identity["WorkflowRunAttempt"]
        ):
            raise HandoffError("ASEHB001", "The controller handoff does not match its captured pull-request identity.")
        if not isinstance(manifest.get("SourceDiffSha256"), str) or re.fullmatch(
            r"[0-9a-f]{64}", manifest["SourceDiffSha256"]
        ) is None:
            raise HandoffError("ASEHB001", "The captured source diff digest is malformed.")
        diff_digest, diff_bytes = _sha256_file(entries["source.diff"], MAX_SOURCE_DIFF_BYTES)
        if diff_digest != manifest.get("SourceDiffSha256") or diff_bytes != manifest.get("SourceDiffBytes"):
            raise HandoffError("ASEHB001", "The captured source diff does not match the controller handoff digest.")
        if (
            type(manifest.get("EvidencePlanBytes")) is not int
            or manifest["EvidencePlanBytes"] <= 0
            or manifest["EvidencePlanBytes"] > MAX_EVIDENCE_PLAN_BYTES
            or not isinstance(manifest.get("EvidencePlanSha256"), str)
            or re.fullmatch(r"[0-9a-f]{64}", manifest["EvidencePlanSha256"]) is None
        ):
            raise HandoffError("ASEHB001", "The controller EvidencePlan digest inventory is malformed.")
        plan_digest, plan_bytes = _sha256_file(entries["evidence-plan.json"], MAX_EVIDENCE_PLAN_BYTES)
        if plan_digest != manifest["EvidencePlanSha256"] or plan_bytes != manifest["EvidencePlanBytes"]:
            raise HandoffError("ASEHB001", "The EvidencePlan does not match the controller handoff digest.")
        evidence_plan, _ = _read_evidence_plan(entries["evidence-plan.json"])
        _validate_evidence_plan(evidence_plan, identity, diff_digest)
        archive_path = entries.get("subject.snapshot.tar")
        archive_digest, archive_bytes = _sha256_file(archive_path, MAX_SNAPSHOT_ARCHIVE_BYTES)
        if archive_digest != manifest.get("SnapshotArchiveSha256") or archive_bytes != manifest.get("SnapshotArchiveBytes"):
            raise HandoffError("ASEHB001", "The subject snapshot does not match the controller handoff digest.")
        entry_count, expanded_bytes, _ = _scan_archive(archive_path)
        if entry_count != manifest.get("SnapshotArchiveEntryCount") or expanded_bytes != manifest.get("SnapshotArchiveExpandedBytes"):
            raise HandoffError("ASEHB001", "The subject snapshot inventory does not match the controller handoff.")
    elif mode == "ObservationOnly":
        expected_names.add("observation.json")
        if set(entries) != expected_names or set(trusted_files) != set(expected_script_names):
            raise HandoffError("ASEHB001", "The downloaded private handoff file set is invalid.")
        observation, _ = _read_json(entries.get("observation.json", root / "missing"), 64 * 1024, "The fork observation")
        if observation.get("Mode") != "ObservationOnly":
            raise HandoffError("ASEHB001", "The downloaded fork handoff is not observation-only.")
        if (
            observation.get("WorkflowRunId") != manifest.get("WorkflowRunId")
            or observation.get("WorkflowRunAttempt") != manifest.get("WorkflowRunAttempt")
        ):
            raise HandoffError("ASEHB001", "The fork observation does not match its handoff identity.")
    else:
        raise HandoffError("ASEHB001", "The private handoff mode is unsupported.")
    for name in expected_script_names:
        expected_digest = trusted_files.get(name)
        if not isinstance(expected_digest, str) or re.fullmatch(r"[0-9a-f]{64}", expected_digest) is None:
            raise HandoffError("ASEHB001", "The trusted subject helper digest is malformed.")
        actual_digest, _ = _sha256_file(entries[name], MAX_TRUSTED_SCRIPT_BYTES)
        if actual_digest != expected_digest:
            raise HandoffError("ASEHB001", "A trusted subject helper does not match the controller handoff.")
    return identity, archive_path, evidence_plan


def _extract_snapshot(archive_path: Path, destination: Path) -> tuple[int, int]:
    output = _new_output_directory(destination, "subject checkout")
    started = time.monotonic()
    try:
        os.mkdir(output, 0o700)
        with tarfile.open(archive_path, mode="r:") as archive:
            for member in archive:
                if time.monotonic() - started > MAX_EXTRACT_SECONDS:
                    raise HandoffError("ASEHB005", "The bounded subject snapshot extraction deadline expired.")
                parts = _member_path(member.name)
                target = output.joinpath(*parts)
                if member.isdir():
                    try:
                        target.mkdir(mode=0o755, parents=False, exist_ok=True)
                    except FileExistsError:
                        if not target.is_dir() or target.is_symlink():
                            raise HandoffError("ASEHB005", "A subject snapshot parent path has a conflicting file type.") from None
                    continue
                parent = target.parent
                missing: list[Path] = []
                current = parent
                while current != output and not current.exists():
                    missing.append(current)
                    current = current.parent
                if current != output and (current.is_symlink() or not current.is_dir()):
                    raise HandoffError("ASEHB005", "A subject snapshot parent path is unsafe.")
                for directory in reversed(missing):
                    directory.mkdir(mode=0o755)
                if not member.isreg():
                    raise HandoffError("ASEHB005", "The subject snapshot contains a link or special file.")
                source = archive.extractfile(member)
                if source is None:
                    raise HandoffError("ASEHB005", "A subject snapshot file could not be read.")
                descriptor = os.open(
                    target,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
                    0o600,
                )
                total = 0
                try:
                    with os.fdopen(descriptor, "wb") as output_file, source:
                        while total < member.size:
                            if time.monotonic() - started > MAX_EXTRACT_SECONDS:
                                raise HandoffError("ASEHB005", "The bounded subject snapshot extraction deadline expired.")
                            chunk = source.read(min(1024 * 1024, member.size - total))
                            if not chunk:
                                raise HandoffError("ASEHB005", "A subject snapshot file was truncated.")
                            output_file.write(chunk)
                            total += len(chunk)
                        if source.read(1):
                            raise HandoffError("ASEHB005", "A subject snapshot file exceeded its declared size.")
                        output_file.flush()
                        os.fsync(output_file.fileno())
                    os.chmod(target, 0o755 if member.mode & 0o111 else 0o644, follow_symlinks=False)
                except BaseException:
                    try:
                        target.unlink(missing_ok=True)
                    except OSError:
                        pass
                    raise
        return _scan_extracted_tree(output)
    except HandoffError:
        _remove_private_directory(output)
        raise
    except (OSError, tarfile.TarError, EOFError, ValueError):
        _remove_private_directory(output)
        raise HandoffError("ASEHB005", "The subject snapshot could not be materialized safely.") from None


def _scan_extracted_tree(root: Path) -> tuple[int, int]:
    file_count = 0
    total_bytes = 0
    stack = [root]
    while stack:
        current = stack.pop()
        with os.scandir(current) as entries:
            for entry in entries:
                metadata = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(metadata.st_mode):
                    raise HandoffError("ASEHB005", "The materialized subject snapshot contains a symbolic link.")
                if stat.S_ISDIR(metadata.st_mode):
                    stack.append(Path(entry.path))
                elif stat.S_ISREG(metadata.st_mode):
                    file_count += 1
                    total_bytes += metadata.st_size
                    if file_count > MAX_SNAPSHOT_FILES or total_bytes > MAX_SNAPSHOT_EXPANDED_BYTES:
                        raise HandoffError("ASEHB005", "The materialized subject snapshot exceeds its fixed limits.")
                else:
                    raise HandoffError("ASEHB005", "The materialized subject snapshot contains a special file.")
    return file_count, total_bytes


def _write_subject_result(path: Path, value: Mapping[str, Any]) -> None:
    encoded = _canonical_json(value) + b"\n"
    if len(encoded) > MAX_RESULT_BYTES:
        encoded = _canonical_json(
            {
                "claimEligible": False,
                "execution": "failed",
                "schemaVersion": 1,
                "diagnostic": {"code": "ASEHB006", "message": "The bounded subject result exceeded its byte limit."},
            }
        ) + b"\n"
    parent = _new_path(path.parent, "subject result parent")
    if not parent.is_dir():
        raise HandoffError("ASEHB006", "The subject result directory is unavailable.")
    _write_regular_file(parent / path.name, encoded)


def verify_handoff(
    *,
    handoff_directory: str | os.PathLike[str],
    fresh_capture_directory: str | os.PathLike[str],
    subject_result_file: str | os.PathLike[str],
    output_plan: str | os.PathLike[str],
) -> None:
    """Copy a downloaded plan only after binding its bundle to fresh trusted Git state.

    This runs in the base-owned verifier job, not the credentialless subject. It
    validates the entire bounded handoff, compares the captured identity and
    diff with an independent current-PR recapture, and checks every archive
    blob and mode against that fresh Git tree. The copied plan is still
    independently resolved by the .NET gate verifier before any claim.
    """
    handoff = _new_path(handoff_directory, "downloaded handoff")
    capture = _new_path(fresh_capture_directory, "fresh verifier capture")
    output = Path(output_plan).absolute()
    _new_path(output.parent, "verified plan output parent")
    if not capture.is_dir() or capture.is_symlink():
        raise HandoffError("ASEHB001", "The fresh verifier capture is not a physical directory.")

    entries = _safe_regular_children(handoff)
    manifest, _ = _read_json(entries.get("handoff.json", handoff / "missing"), 128 * 1024, "The controller handoff manifest")
    identity, archive_path, plan = _validate_bundle_directory(handoff, manifest)
    if manifest.get("Mode") != "SubjectSnapshot" or identity is None or archive_path is None or plan is None:
        raise HandoffError("ASEHB001", "The verifier requires a same-repository subject snapshot.")

    fresh_identity, _ = _read_json(
        capture / "pull-request-run-identity.json", 64 * 1024, "The fresh verifier pull-request identity"
    )
    _validate_identity(fresh_identity)
    if fresh_identity != identity:
        raise HandoffError("ASEHB001", "The downloaded handoff differs from the fresh pull-request identity.")
    fresh_diff = _read_regular_file(capture / "source.diff", MAX_SOURCE_DIFF_BYTES, "The fresh verifier source diff")
    if hashlib.sha256(fresh_diff).hexdigest() != manifest["SourceDiffSha256"]:
        raise HandoffError("ASEHB001", "The downloaded handoff differs from the fresh source diff.")
    repository = capture / "repository.git"
    if not repository.is_dir() or repository.is_symlink():
        raise HandoffError("ASEHB003", "The fresh trusted Git object store is unavailable.")
    inventory = _run_git_tree_inventory(repository, identity["HeadRevision"])
    _scan_archive(archive_path, inventory)

    subject_raw = _read_regular_file(Path(subject_result_file), MAX_RESULT_BYTES, "The credentialless subject result")
    if not subject_raw.endswith(b"\n"):
        raise HandoffError("ASEHB001", "The credentialless subject result is not newline-terminated canonical JSON.")
    try:
        subject_result = json.loads(
            subject_raw[:-1], object_pairs_hook=_unique_object, parse_constant=_reject_json_constant
        )
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise HandoffError("ASEHB001", "The credentialless subject result is malformed JSON.") from None
    if not isinstance(subject_result, dict) or _canonical_json(subject_result) + b"\n" != subject_raw:
        raise HandoffError("ASEHB001", "The credentialless subject result is not canonical JSON.")
    diagnostic = subject_result.get("diagnostic")
    if (
        type(subject_result.get("schemaVersion")) is not int
        or subject_result["schemaVersion"] != 1
        or subject_result.get("claimEligible") is not False
        or subject_result.get("mode") != "SubjectSnapshot"
        or subject_result.get("workflowRunId") != str(identity["WorkflowRunId"])
        or subject_result.get("workflowRunAttempt") != str(identity["WorkflowRunAttempt"])
        or subject_result.get("headRevision") != identity["HeadRevision"]
        or subject_result.get("snapshotSha256") != manifest["SnapshotArchiveSha256"]
        or subject_result.get("profileId") != plan["Profile"]["Id"]
        or subject_result.get("execution") != "completed"
        or type(subject_result.get("exitCode")) is not int
        or subject_result["exitCode"] != 0
        or not isinstance(diagnostic, dict)
        or diagnostic.get("code") != "ASEHB010"
    ):
        raise HandoffError("ASEHB001", "The credentialless subject result does not match this successful handoff.")
    if plan["Profile"]["Id"] == "code-coverage":
        _validate_execution_receipt(
            subject_result.get("executionReceipt"),
            expected_binding=_execution_receipt_binding(identity, manifest, plan["Profile"]["Id"]),
        )

    plan_bytes = _read_regular_file(entries["evidence-plan.json"], MAX_EVIDENCE_PLAN_BYTES, "The verified EvidencePlan")
    _write_regular_file(output, plan_bytes)


def execute_handoff(
    *,
    handoff_directory: str | os.PathLike[str],
    subject_checkout: str | os.PathLike[str],
    scratch_directory: str | os.PathLike[str],
    result_path: str | os.PathLike[str],
    image_digest: str | None,
    environment: Mapping[str, str] | None = None,
) -> int:
    result_destination = Path(result_path).absolute()
    result: dict[str, Any] = {
        "claimEligible": False,
        "execution": "not-run",
        "schemaVersion": 1,
    }
    try:
        handoff = _new_path(handoff_directory, "downloaded handoff")
        entries = _safe_regular_children(handoff)
        manifest, _ = _read_json(entries.get("handoff.json", handoff / "missing"), 128 * 1024, "The controller handoff manifest")
        identity, archive_path, evidence_plan = _validate_bundle_directory(handoff, manifest)
        env = os.environ if environment is None else environment
        run_id = env.get("GITHUB_RUN_ID", "")
        attempt = env.get("GITHUB_RUN_ATTEMPT", "")
        if POSITIVE_DECIMAL_PATTERN.fullmatch(run_id) is None or POSITIVE_DECIMAL_PATTERN.fullmatch(attempt) is None:
            raise HandoffError("ASEHB001", "The trusted workflow run identity is missing or malformed.")
        if str(manifest.get("WorkflowRunId")) != run_id or str(manifest.get("WorkflowRunAttempt")) != attempt:
            raise HandoffError("ASEHB001", "The downloaded handoff belongs to a different workflow run attempt.")
        repository_id = env.get("GITHUB_REPOSITORY_ID", "")
        if repository_id and str(manifest.get("RepositoryId")) != repository_id:
            raise HandoffError("ASEHB001", "The downloaded handoff belongs to a different repository.")
        result["mode"] = manifest["Mode"]
        result["workflowRunId"] = run_id
        result["workflowRunAttempt"] = attempt
        if manifest["Mode"] == "ObservationOnly":
            result["execution"] = "observation-only"
            result["diagnostic"] = {"code": "ASEHB007", "message": "Untrusted fork input is observation-only."}
            _write_subject_result(result_destination, result)
            return 2
        assert identity is not None and archive_path is not None and evidence_plan is not None
        result["headRevision"] = identity["HeadRevision"]
        result["snapshotSha256"] = manifest["SnapshotArchiveSha256"]
        profile_id = evidence_plan["Profile"]["Id"]
        result["profileId"] = profile_id
        if profile_id == "documentation-only":
            result.update(
                {
                    "execution": "completed",
                    "exitCode": 0,
                    "diagnostic": {
                        "code": "ASEHB010",
                        "message": "The explicit empty documentation-only profile completed without OCI execution or a trusted evidence verifier.",
                    },
                }
            )
            _write_subject_result(result_destination, result)
            return 0
        if profile_id != "code-coverage":
            raise HandoffError("ASEHB008", "The selected EvidencePlan profile is unsupported by the bounded subject handoff.")
        if not isinstance(image_digest, str) or not image_digest:
            raise HandoffError("ASEHB008", "The digest-pinned offline subject image is not configured.")

        checkout = _new_output_directory(subject_checkout, "subject checkout")
        scratch = _new_output_directory(scratch_directory, "subject scratch")
        file_count, tree_bytes = _extract_snapshot(archive_path, checkout)
        result["snapshotFileCount"] = file_count
        result["snapshotExpandedBytes"] = tree_bytes

        import importlib.util

        launcher_path = handoff / "evidence-gate-subject.py"
        spec = importlib.util.spec_from_file_location("evidence_gate_subject_handoff", launcher_path)
        if spec is None or spec.loader is None:
            raise HandoffError("ASEHB008", "The trusted subject launcher could not be loaded.")
        launcher = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = launcher
        previous_dont_write_bytecode = sys.dont_write_bytecode
        sys.dont_write_bytecode = True
        try:
            spec.loader.exec_module(launcher)
        finally:
            sys.dont_write_bytecode = previous_dont_write_bytecode
        limits = launcher.SubjectLimits(**SUBJECT_LIMITS)
        subject_result = launcher.launch_subject(
            subject_checkout=checkout,
            image_digest=image_digest,
            scratch_directory=scratch,
            profile_id=profile_id,
            limits=limits,
            _environment=env,
        )
        execution_record = subject_result.execution_record
        if type(subject_result.exit_code) is not int:
            raise HandoffError("ASEHB009", "The bounded subject launcher returned a malformed exit code.")
        if subject_result.exit_code == 0 and not isinstance(execution_record, Mapping):
            raise HandoffError("ASEHB009", "A successful subject run omitted its fixed execution record.")
        if execution_record is not None:
            if not isinstance(execution_record, Mapping):
                raise HandoffError("ASEHB009", "The bounded subject launcher returned a malformed execution record.")
            try:
                result["executionReceipt"] = _make_execution_receipt(
                    execution_record,
                    _execution_receipt_binding(identity, manifest, profile_id),
                )
            except HandoffError:
                raise HandoffError("ASEHB009", "The bounded subject launcher returned invalid step proof.") from None
        result.update(
            {
                "execution": "completed" if subject_result.exit_code == 0 else "failed",
                "exitCode": subject_result.exit_code,
                "profileId": profile_id,
                "stdoutBytes": len(subject_result.stdout),
                "stdoutSha256": hashlib.sha256(subject_result.stdout).hexdigest(),
                "stderrBytes": len(subject_result.stderr),
                "stderrSha256": hashlib.sha256(subject_result.stderr).hexdigest(),
            }
        )
        if subject_result.exit_code != 0:
            result["diagnostic"] = {"code": "ASEHB009", "message": "The credentialless subject profile exited unsuccessfully."}
        else:
            result["diagnostic"] = {"code": "ASEHB010", "message": "Subject execution completed without a trusted evidence verifier."}
    except HandoffError as failure:
        result["execution"] = "failed"
        result["diagnostic"] = {"code": failure.code, "message": failure.message}
    except Exception:
        result["execution"] = "failed"
        result["diagnostic"] = {"code": "ASEHB011", "message": "The credentialless subject handoff failed closed."}
    try:
        _write_subject_result(result_destination, result)
    except HandoffError as failure:
        print(f"{failure.code}: {failure.message}", file=sys.stderr)
        return 2
    return 0 if result.get("execution") == "completed" else 2


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    create = subparsers.add_parser("create", help="Build the private controller handoff from a capture directory.")
    create.add_argument("--capture-directory", required=True)
    create.add_argument("--trusted-scripts-directory", required=True)
    create.add_argument("--output-directory", required=True)
    create.add_argument("--plan-file", default=None)
    execute = subparsers.add_parser("execute", help="Validate and execute the handoff on the credentialless subject job.")
    execute.add_argument("--handoff-directory", required=True)
    execute.add_argument("--subject-checkout", required=True)
    execute.add_argument("--scratch-directory", required=True)
    execute.add_argument("--result", required=True)
    execute.add_argument("--image-digest", default=None)
    verify = subparsers.add_parser("verify", help="Bind the downloaded handoff to a fresh trusted PR recapture.")
    verify.add_argument("--handoff-directory", required=True)
    verify.add_argument("--fresh-capture-directory", required=True)
    verify.add_argument("--subject-result", required=True)
    verify.add_argument("--output-plan", required=True)
    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "create":
            outcome = create_handoff(
                capture_directory=arguments.capture_directory,
                trusted_scripts_directory=arguments.trusted_scripts_directory,
                output_directory=arguments.output_directory,
                plan_file=arguments.plan_file,
            )
            print(f"evidence-gate-handoff: {outcome} controller handoff prepared.")
            return 0
        if arguments.command == "verify":
            verify_handoff(
                handoff_directory=arguments.handoff_directory,
                fresh_capture_directory=arguments.fresh_capture_directory,
                subject_result_file=arguments.subject_result,
                output_plan=arguments.output_plan,
            )
            print("evidence-gate-handoff: current-revision handoff verified for trusted planning.")
            return 0
        environment: dict[str, str] = dict(os.environ)
        if arguments.image_digest is None:
            arguments.image_digest = environment.get(SUBJECT_IMAGE_ENVIRONMENT_VARIABLE)
        return execute_handoff(
            handoff_directory=arguments.handoff_directory,
            subject_checkout=arguments.subject_checkout,
            scratch_directory=arguments.scratch_directory,
            result_path=arguments.result,
            image_digest=arguments.image_digest,
            environment=environment,
        )
    except HandoffError as failure:
        print(f"{failure.code}: {failure.message}", file=sys.stderr)
        return 2
    except (OSError, ValueError):
        print("ASEHB011: The credentialless subject handoff failed closed.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
