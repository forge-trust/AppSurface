#!/usr/bin/env python3
"""Build a bounded, non-claiming observation from trusted verifier inputs.

This command records what the base-owned verifier checked. It does not attest
the subject checkout, prove runtime enforcement or producer truth, or create a
gate verdict. Every input remains bounded and is opened without following
symbolic links, including parent directories.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
from typing import Any, Mapping


MAX_OUTPUT_BYTES = 16 * 1024
MAX_PLAN_BYTES = 4 * 1024 * 1024
MAX_IDENTITY_BYTES = 16 * 1024
MAX_SUBJECT_RESULT_BYTES = 16 * 1024
MAX_CLEANUP_BYTES = 4 * 1024
MAX_ARTIFACT_BYTES = {
    "cobertura.xml": 20 * 1024 * 1024,
    "gate-report.md": 1 * 1024 * 1024,
    "diagnostics.json": 4 * 1024 * 1024,
}
ARTIFACT_INDEX = (
    ("cobertura.xml", "cobertura", "coverage/coverage-merged/coverage.cobertura.xml"),
    ("gate-report.md", "gate-report", "coverage/coverage-gate/coverage-gate.md"),
    ("diagnostics.json", "diagnostics", "coverage/coverage-gate/coverage-gate.json"),
)
REVISION_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
DECIMAL_PATTERN = re.compile(r"[1-9][0-9]{0,18}\Z")
REPOSITORY_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\Z")
IMAGE_PATTERN = re.compile(
    r"ghcr\.io/forge-trust/appsurface-subject-native-validation@sha256:[0-9a-f]{64}\Z"
)


class ObservationError(Exception):
    """A fixed, non-sensitive failure for an invalid observation input."""


def _canonical(value: Any) -> bytes:
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode("ascii")


def _canonical_plan(value: Mapping[str, Any]) -> bytes:
    """Match the EvidencePlan serializer's additional JSON string escaping."""
    encoded = _canonical(value)
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


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def _reject_constant(_value: str) -> None:
    raise ValueError("non-standard JSON constant")


def _absolute_parts(path: Path) -> tuple[str, ...]:
    if not path.is_absolute() or ".." in path.parts or "\x00" in os.fspath(path):
        raise ObservationError("unsafe path")
    return path.parts[1:]


def _open_directory(path: Path) -> int:
    parts = _absolute_parts(path)
    flags = os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0)
    descriptor = -1
    try:
        descriptor = os.open("/", flags)
        for component in parts:
            next_descriptor = os.open(component, flags, dir_fd=descriptor)
            os.close(descriptor)
            descriptor = next_descriptor
        metadata = os.fstat(descriptor)
        if not stat.S_ISDIR(metadata.st_mode):
            raise ObservationError("unsafe directory")
        return descriptor
    except (OSError, ValueError):
        if descriptor >= 0:
            os.close(descriptor)
        raise ObservationError("unsafe directory") from None


def _read_at(directory_fd: int, name: str, maximum_bytes: int) -> bytes:
    descriptor = -1
    try:
        descriptor = os.open(
            name,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_NONBLOCK", 0),
            dir_fd=directory_fd,
        )
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or before.st_nlink != 1
            or before.st_size <= 0
            or before.st_size > maximum_bytes
        ):
            raise ObservationError("unsafe file")
        chunks: list[bytes] = []
        total = 0
        while total <= maximum_bytes:
            chunk = os.read(descriptor, min(64 * 1024, maximum_bytes + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > maximum_bytes:
                raise ObservationError("oversized file")
            chunks.append(chunk)
        after = os.fstat(descriptor)
        if (
            total != before.st_size
            or (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns)
            != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
        ):
            raise ObservationError("changed file")
        return b"".join(chunks)
    except ObservationError:
        raise
    except (OSError, ValueError):
        raise ObservationError("unavailable file") from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)


def _read_file(path: Path, maximum_bytes: int) -> bytes:
    parts = _absolute_parts(path)
    if not parts:
        raise ObservationError("invalid file")
    directory_fd = _open_directory(path.parent)
    try:
        return _read_at(directory_fd, parts[-1], maximum_bytes)
    finally:
        os.close(directory_fd)


def _read_json(raw: bytes) -> dict[str, Any]:
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=_reject_constant)
    except (UnicodeError, ValueError, RecursionError):
        raise ObservationError("malformed JSON") from None
    if not isinstance(value, dict):
        raise ObservationError("unexpected JSON shape")
    return value


def _require_decimal(value: Any) -> str:
    if not isinstance(value, str) or DECIMAL_PATTERN.fullmatch(value) is None:
        raise ObservationError("invalid identity")
    return value


def _require_sha(value: Any, pattern: re.Pattern[str] = SHA256_PATTERN) -> str:
    if not isinstance(value, str) or pattern.fullmatch(value) is None:
        raise ObservationError("invalid digest")
    return value


def _validate_identity(value: Mapping[str, Any]) -> dict[str, Any]:
    if set(value) != {"EventName", "Repository", "RunIdentity", "SubjectJobId", "WorkflowId"}:
        raise ObservationError("invalid verifier identity")
    repository = value.get("Repository")
    if not isinstance(repository, str) or REPOSITORY_PATTERN.fullmatch(repository) is None:
        raise ObservationError("invalid verifier identity")
    run = value.get("RunIdentity")
    run_keys = {
        "HeadRepositoryId", "PullRequestNumber", "RepositoryId", "TargetBranch", "WorkflowRunAttempt", "WorkflowRunId",
    }
    if not isinstance(run, dict) or set(run) != run_keys or value.get("EventName") != "pull_request_target":
        raise ObservationError("invalid verifier identity")
    if _require_decimal(value.get("WorkflowId")) != value["WorkflowId"]:
        raise ObservationError("invalid verifier identity")
    _require_decimal(value.get("SubjectJobId"))
    for key in ("RepositoryId", "HeadRepositoryId", "PullRequestNumber", "WorkflowRunId", "WorkflowRunAttempt"):
        if type(run.get(key)) is not int or not 0 < run[key] <= (1 << 63) - 1:
            raise ObservationError("invalid verifier identity")
    if run["RepositoryId"] != run["HeadRepositoryId"]:
        raise ObservationError("invalid verifier identity")
    branch = run.get("TargetBranch")
    if not isinstance(branch, str) or not branch or len(branch) > 128 or any(char in branch for char in "\x00\r\n"):
        raise ObservationError("invalid verifier identity")
    return dict(value)


def _validate_plan(value: Mapping[str, Any], identity: Mapping[str, Any]) -> tuple[str, dict[str, str]]:
    run = identity["RunIdentity"]
    base_revision = _require_sha(value.get("BaseRevision"), REVISION_PATTERN)
    head_revision = _require_sha(value.get("HeadRevision"), REVISION_PATTERN)
    if len(base_revision) != len(head_revision):
        raise ObservationError("plan revisions use different Git object formats")
    plan_identity = value.get("PullRequestRunIdentity")
    expected_plan_identity = {
        key: run[key]
        for key in ("RepositoryId", "HeadRepositoryId", "PullRequestNumber", "TargetBranch", "WorkflowRunId", "WorkflowRunAttempt")
    }
    if (
        value.get("ContractVersion") != "2.0"
        or not isinstance(plan_identity, dict)
        or set(plan_identity) != set(expected_plan_identity)
        or any(type(plan_identity[key]) is not type(expected) or plan_identity[key] != expected for key, expected in expected_plan_identity.items())
    ):
        raise ObservationError("plan binding mismatch")
    profile = value.get("Profile")
    if not isinstance(profile, dict) or profile.get("Scope") != "Targeted":
        raise ObservationError("invalid profile")
    profile_id = profile.get("Id")
    if not isinstance(profile_id, str) or profile_id not in {"documentation-only", "code-coverage"}:
        raise ObservationError("unsupported profile")
    if any(not isinstance(profile.get(key), list) for key in ("Resources", "Producers", "Obligations")):
        raise ObservationError("invalid profile")
    if profile_id == "documentation-only" and any(profile[key] for key in ("Resources", "Producers", "Obligations")):
        raise ObservationError("nonempty documentation profile")
    policy = value.get("PolicySnapshot")
    profiles = policy.get("Profiles") if isinstance(policy, dict) else None
    matches = [candidate for candidate in profiles if isinstance(candidate, dict) and candidate.get("Id") == profile_id] if isinstance(profiles, list) else []
    if len(matches) != 1 or matches[0] != profile:
        raise ObservationError("profile is not closed by policy snapshot")
    digests = {
        key: _require_sha(value.get(key))
        for key in ("PlanDigest", "PolicyDigest", "DiffDigest", "NameStatusDigest", "SourceDiffDigest")
    }
    if not isinstance(value.get("PolicyId"), str) or not value["PolicyId"] or len(value["PolicyId"]) > 128:
        raise ObservationError("invalid plan")
    return profile_id, digests


def _expected_receipt_binding(
    identity: Mapping[str, Any],
    plan: Mapping[str, Any],
    profile_id: str,
    snapshot_sha256: str,
) -> dict[str, str]:
    run = identity["RunIdentity"]
    return {
        "baseRevision": plan["BaseRevision"],
        "headRevision": plan["HeadRevision"],
        "profileId": profile_id,
        "snapshotSha256": snapshot_sha256,
        "workflowRunAttempt": str(run["WorkflowRunAttempt"]),
        "workflowRunId": str(run["WorkflowRunId"]),
    }


def _validate_receipt(value: Any, binding: Mapping[str, str], member: str) -> Mapping[str, Any]:
    if (
        not isinstance(value, dict)
        or set(value) != {"binding", member, "schemaVersion", "sha256"}
        or type(value.get("schemaVersion")) is not int
        or value["schemaVersion"] != 1
        or value.get("binding") != dict(binding)
    ):
        raise ObservationError("invalid receipt")
    digest = _require_sha(value.get("sha256"))
    payload = {"binding": value["binding"], member: value[member], "schemaVersion": 1}
    try:
        actual_digest = hashlib.sha256(_canonical(payload)).hexdigest()
    except (TypeError, ValueError, RecursionError):
        raise ObservationError("invalid receipt") from None
    if actual_digest != digest:
        raise ObservationError("invalid receipt digest")
    record = value[member]
    if not isinstance(record, dict):
        raise ObservationError("invalid receipt record")
    return record


def _read_artifacts(directory: Path, indexed: Any) -> list[dict[str, Any]]:
    if not isinstance(indexed, list) or len(indexed) != len(ARTIFACT_INDEX):
        raise ObservationError("invalid artifact index")
    directory_fd = _open_directory(directory)
    try:
        if set(os.listdir(directory_fd)) != set(MAX_ARTIFACT_BYTES):
            raise ObservationError("unexpected artifact inventory")
        results: list[dict[str, Any]] = []
        for entry, (basename, logical_name, relative_path) in zip(indexed, ARTIFACT_INDEX, strict=True):
            if (
                not isinstance(entry, dict)
                or set(entry) != {"logicalName", "relativePath", "byteCount", "sha256"}
                or entry.get("logicalName") != logical_name
                or entry.get("relativePath") != relative_path
                or type(entry.get("byteCount")) is not int
                or not 0 < entry["byteCount"] <= MAX_ARTIFACT_BYTES[basename]
            ):
                raise ObservationError("invalid artifact index")
            raw = _read_at(directory_fd, basename, MAX_ARTIFACT_BYTES[basename])
            digest = hashlib.sha256(raw).hexdigest()
            if len(raw) != entry["byteCount"] or digest != _require_sha(entry.get("sha256")):
                raise ObservationError("artifact mismatch")
            results.append({"name": basename, "byteCount": len(raw), "sha256": digest})
        if set(os.listdir(directory_fd)) != set(MAX_ARTIFACT_BYTES):
            raise ObservationError("changed artifact inventory")
        return results
    except OSError:
        raise ObservationError("unsafe artifacts") from None
    finally:
        os.close(directory_fd)


def _validate_cleanup(raw: bytes) -> None:
    record = _read_json(raw)
    if set(record) != {
        "schemaVersion", "claimEligible", "published", "status", "trigger", "containerStatus",
        "mountStatus", "scratchStatus", "failureCode", "cleanupDeadlineSeconds",
    }:
        raise ObservationError("invalid cleanup record")
    if (
        type(record.get("schemaVersion")) is not int
        or record["schemaVersion"] != 1
        or record.get("claimEligible") is not False
        or record.get("published") is not False
        or record.get("status") != "complete"
        or record.get("trigger") not in {"request", "parent-exit"}
        or record.get("containerStatus") not in {"removed", "absent"}
        or record.get("mountStatus") not in {"unmounted", "absent"}
        or record.get("scratchStatus") not in {"removed", "absent"}
        or record.get("failureCode") != "none"
        or type(record.get("cleanupDeadlineSeconds")) is not int
        or record["cleanupDeadlineSeconds"] != 600
        or raw != _canonical(record) + b"\n"
    ):
        raise ObservationError("incomplete cleanup record")


def build_observation(
    *,
    verified_plan_path: Path,
    verifier_identity_path: Path,
    subject_result_path: Path,
    cleanup_record_path: Path | None = None,
    subject_artifacts_directory: Path | None = None,
    expected_image_digest: str | None = None,
) -> dict[str, Any]:
    plan_raw = _read_file(verified_plan_path, MAX_PLAN_BYTES)
    identity_raw = _read_file(verifier_identity_path, MAX_IDENTITY_BYTES)
    subject_raw = _read_file(subject_result_path, MAX_SUBJECT_RESULT_BYTES)
    plan = _read_json(plan_raw)
    identity_value = _read_json(identity_raw)
    if _canonical(identity_value) != identity_raw:
        raise ObservationError("noncanonical verifier identity")
    identity = _validate_identity(identity_value)
    if _canonical_plan(plan) != plan_raw:
        raise ObservationError("noncanonical verified plan")
    profile_id, digests = _validate_plan(plan, identity)
    run = identity["RunIdentity"]
    subject = _read_json(subject_raw)
    if not subject_raw.endswith(b"\n") or _canonical(subject) + b"\n" != subject_raw:
        raise ObservationError("noncanonical subject result")
    snapshot_sha256 = _require_sha(subject.get("snapshotSha256"))
    expected_common = {
        "claimEligible": False,
        "execution": "completed",
        "exitCode": 0,
        "headRevision": plan["HeadRevision"],
        "mode": "SubjectSnapshot",
        "profileId": profile_id,
        "schemaVersion": 1,
        "snapshotSha256": snapshot_sha256,
        "workflowRunAttempt": str(run["WorkflowRunAttempt"]),
        "workflowRunId": str(run["WorkflowRunId"]),
    }
    if any(type(subject.get(key)) is not type(expected) or subject.get(key) != expected for key, expected in expected_common.items()):
        raise ObservationError("subject binding mismatch")
    diagnostic = subject.get("diagnostic")
    if not isinstance(diagnostic, dict) or set(diagnostic) != {"code", "message"} or diagnostic.get("code") != "ASEHB010" or not isinstance(diagnostic.get("message"), str):
        raise ObservationError("invalid subject result")

    input_hashes = {
        "verifiedPlan": hashlib.sha256(plan_raw).hexdigest(),
        "verifierIdentity": hashlib.sha256(identity_raw).hexdigest(),
        "subjectResult": hashlib.sha256(subject_raw).hexdigest(),
    }
    artifacts: list[dict[str, Any]] = []
    trusted_preflight: dict[str, str] | None = None
    if profile_id == "documentation-only":
        if cleanup_record_path is not None or subject_artifacts_directory is not None or expected_image_digest is not None:
            raise ObservationError("coverage inputs forbidden for documentation-only")
        if set(subject) != set(expected_common) | {"diagnostic"}:
            raise ObservationError("unexpected documentation result fields")
        if "envelopeReceipt" in subject or "executionReceipt" in subject:
            raise ObservationError("unexpected documentation receipt")
    else:
        if cleanup_record_path is None or subject_artifacts_directory is None or expected_image_digest is None:
            raise ObservationError("coverage inputs required")
        if not isinstance(expected_image_digest, str) or IMAGE_PATTERN.fullmatch(expected_image_digest) is None:
            raise ObservationError("invalid preflight image digest")
        expected_fields = set(expected_common) | {
            "diagnostic", "executionReceipt", "envelopeReceipt", "snapshotFileCount", "snapshotExpandedBytes",
            "stdoutBytes", "stdoutSha256", "stderrBytes", "stderrSha256",
        }
        if set(subject) != expected_fields:
            raise ObservationError("unexpected coverage result fields")
        for field in ("snapshotFileCount", "snapshotExpandedBytes", "stdoutBytes", "stderrBytes"):
            if type(subject.get(field)) is not int or subject[field] < 0:
                raise ObservationError("invalid coverage result counters")
        for field in ("stdoutSha256", "stderrSha256"):
            _require_sha(subject.get(field))
        binding = _expected_receipt_binding(identity, plan, profile_id, snapshot_sha256)
        execution = _validate_receipt(subject.get("executionReceipt"), binding, "record")
        if (
            set(execution) != {"claimEligible", "profileId", "schemaVersion", "status", "steps", "artifacts"}
            or execution.get("claimEligible") is not False
            or execution.get("profileId") != "code-coverage"
            or type(execution.get("schemaVersion")) is not int
            or execution["schemaVersion"] != 1
            or execution.get("status") != "completed"
            or not isinstance(execution.get("steps"), list)
            or len(execution["steps"]) != 5
        ):
            raise ObservationError("invalid execution receipt")
        envelope = _validate_receipt(subject.get("envelopeReceipt"), binding, "observation")
        expected_envelope_fields = {
            "schemaVersion", "imageDigest", "profileId", "runnerEnvironment", "runtime", "networkMode",
            "rootfsReadOnlyConfigured", "subjectAndDiffReadOnlyConfigured", "scratchTmpfsQuotaVerified",
            "capabilityDropConfigured", "noNewPrivilegesConfigured", "pidIpcUtsUserNamespacesConfiguredPrivate",
            "resourceLimitsConfigured",
        }
        if (
            set(envelope) != expected_envelope_fields
            or type(envelope.get("schemaVersion")) is not int
            or envelope["schemaVersion"] != 1
            or envelope.get("imageDigest") != expected_image_digest
            or envelope.get("profileId") != "code-coverage"
            or envelope.get("runnerEnvironment") != "github-hosted"
            or envelope.get("runtime") != "rootless-podman"
            or envelope.get("networkMode") != "none"
            or any(envelope.get(key) is not True for key in expected_envelope_fields if key.endswith("Configured") or key == "scratchTmpfsQuotaVerified")
        ):
            raise ObservationError("preflight image or envelope mismatch")
        artifacts = _read_artifacts(subject_artifacts_directory, execution.get("artifacts"))
        cleanup_raw = _read_file(cleanup_record_path, MAX_CLEANUP_BYTES)
        _validate_cleanup(cleanup_raw)
        input_hashes["cleanupRecord"] = hashlib.sha256(cleanup_raw).hexdigest()
        input_hashes["trustedPreflightImageDigest"] = hashlib.sha256(expected_image_digest.encode("ascii")).hexdigest()
        trusted_preflight = {"subjectImageDigest": expected_image_digest}

    repository = identity["Repository"]
    run_identity = {
        "repositoryId": str(run["RepositoryId"]),
        "headRepositoryId": str(run["HeadRepositoryId"]),
        "pullRequestNumber": str(run["PullRequestNumber"]),
        "targetBranch": run["TargetBranch"],
        "baseRevision": plan["BaseRevision"],
        "headRevision": plan["HeadRevision"],
    }
    return {
        "claimEligible": False,
        "inputSha256": input_hashes,
        "profile": {
            "id": profile_id,
            "planDigest": digests["PlanDigest"],
            "policyDigest": digests["PolicyDigest"],
            "diffDigest": digests["DiffDigest"],
            "sourceDiffDigest": digests["SourceDiffDigest"],
        },
        "run": {
            "eventName": identity["EventName"],
            "repository": repository,
            "workflowId": identity["WorkflowId"],
            "runId": str(run["WorkflowRunId"]),
            "runAttempt": str(run["WorkflowRunAttempt"]),
            "subjectJobId": identity["SubjectJobId"],
            "pullRequestIdentity": run_identity,
        },
        "schemaVersion": 1,
        "semantics": "Observation only; does not attest subject checkout, isolation enforcement, producer truth, or gate eligibility.",
        "snapshotSha256": snapshot_sha256,
        "subjectArtifacts": artifacts,
        "trustedPreflight": trusted_preflight,
    }


def _write_new_output(path: Path, value: Mapping[str, Any]) -> None:
    raw = _canonical(value) + b"\n"
    if len(raw) > MAX_OUTPUT_BYTES:
        raise ObservationError("observation exceeds output limit")
    parts = _absolute_parts(path)
    if not parts:
        raise ObservationError("invalid output path")
    directory_fd = _open_directory(path.parent)
    descriptor = -1
    try:
        descriptor = os.open(
            parts[-1],
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            0o600,
            dir_fd=directory_fd,
        )
        with os.fdopen(descriptor, "wb") as output:
            descriptor = -1
            output.write(raw)
            output.flush()
            os.fsync(output.fileno())
    except (OSError, ValueError):
        raise ObservationError("observation output unavailable") from None
    finally:
        if descriptor >= 0:
            os.close(descriptor)
        os.close(directory_fd)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--verified-plan", required=True, type=Path)
    parser.add_argument("--verifier-identity", required=True, type=Path)
    parser.add_argument("--subject-result", required=True, type=Path)
    parser.add_argument("--cleanup-record", type=Path)
    parser.add_argument("--subject-artifacts-directory", type=Path)
    parser.add_argument("--expected-image-digest")
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args(argv)
    try:
        observation = build_observation(
            verified_plan_path=arguments.verified_plan,
            verifier_identity_path=arguments.verifier_identity,
            subject_result_path=arguments.subject_result,
            cleanup_record_path=arguments.cleanup_record,
            subject_artifacts_directory=arguments.subject_artifacts_directory,
            expected_image_digest=arguments.expected_image_digest,
        )
        _write_new_output(arguments.output, observation)
    except ObservationError:
        print("evidence-gate-observation: ASEGO001: trusted observation inputs were invalid or unsafe.", file=sys.stderr)
        return 2
    print("evidence-gate-observation: bounded non-claiming observation recorded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
