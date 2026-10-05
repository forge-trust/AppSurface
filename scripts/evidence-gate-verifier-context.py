#!/usr/bin/env python3
"""Resolve a trusted verifier identity for the current pull-request workflow run.

Run only from the base-owned verifier job after a fresh controller recapture. The
captured identity is trusted input; the workflow run and subject-job IDs come
from GitHub's attempt-specific API, never from a downloaded subject artifact.
This command writes no verdict and does not attest the subject checkout.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import stat
import sys
from typing import Any, Callable, Mapping
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener


MAX_IDENTITY_BYTES = 16 * 1024
MAX_API_BYTES = 512 * 1024
API_TIMEOUT_SECONDS = 10
SUBJECT_JOB_NAME = "Evidence gate subject (credentialless, non-claiming)"
REPOSITORY_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\Z")
DECIMAL_PATTERN = re.compile(r"[1-9][0-9]{0,18}\Z")
RUN_IDENTITY_KEYS = frozenset(
    {"BaseRevision", "HeadRevision", "HeadRepositoryId", "PullRequestNumber", "RepositoryId", "TargetBranch", "WorkflowRunAttempt", "WorkflowRunId"}
)
EXPECTED_RUN_IDENTITY_KEYS = RUN_IDENTITY_KEYS - {"BaseRevision", "HeadRevision"}
REVISION_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")


class VerifierContextError(Exception):
    """Fixed diagnostic for an unavailable or inconsistent trusted identity."""


def _positive(value: Any, name: str) -> int:
    if type(value) is not int or value <= 0 or value > (1 << 63) - 1:
        raise VerifierContextError(f"{name} is not a positive bounded integer.")
    return value


def _environment_positive(environment: Mapping[str, str], name: str) -> int:
    raw = environment.get(name, "")
    if DECIMAL_PATTERN.fullmatch(raw) is None:
        raise VerifierContextError(f"{name} is missing or malformed.")
    return _positive(int(raw, 10), name)


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def _reject_constant(_value: str) -> None:
    raise ValueError("nonstandard JSON number")


def _parse_object(raw: bytes, maximum: int, description: str) -> dict[str, Any]:
    if len(raw) > maximum:
        raise VerifierContextError(f"{description} exceeds its byte limit.")
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object, parse_constant=_reject_constant)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError, RecursionError):
        raise VerifierContextError(f"{description} is malformed JSON.") from None
    if not isinstance(value, dict):
        raise VerifierContextError(f"{description} is not a JSON object.")
    return value


def _canonical(value: Mapping[str, Any]) -> bytes:
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode("ascii")


def _read_identity(path: Path) -> dict[str, Any]:
    try:
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
    except (OSError, ValueError):
        raise VerifierContextError("The fresh controller identity is unavailable or unsafe.") from None
    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > MAX_IDENTITY_BYTES:
            raise VerifierContextError("The fresh controller identity is not a bounded regular file.")
        raw = os.read(descriptor, MAX_IDENTITY_BYTES + 1)
    finally:
        os.close(descriptor)
    identity = _parse_object(raw, MAX_IDENTITY_BYTES, "The fresh controller identity")
    if set(identity) != RUN_IDENTITY_KEYS or _canonical(identity) != raw:
        raise VerifierContextError("The fresh controller identity is not a canonical run identity.")
    for key in RUN_IDENTITY_KEYS - {"BaseRevision", "HeadRevision", "TargetBranch"}:
        _positive(identity[key], f"The {key} value")
    base_revision = identity["BaseRevision"]
    head_revision = identity["HeadRevision"]
    if (
        not isinstance(base_revision, str)
        or not isinstance(head_revision, str)
        or REVISION_PATTERN.fullmatch(base_revision) is None
        or REVISION_PATTERN.fullmatch(head_revision) is None
        or len(base_revision) != len(head_revision)
    ):
        raise VerifierContextError("The fresh controller revisions are malformed.")
    branch = identity["TargetBranch"]
    if not isinstance(branch, str) or not branch or len(branch) > 128 or any(char.isspace() for char in branch):
        raise VerifierContextError("The fresh target branch is malformed.")
    return identity


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, _request: Any, _fp: Any, _code: int, _message: str, _headers: Any, _url: str) -> None:
        return None


def _read_api(path: str, *, api_base: str, token: str) -> dict[str, Any]:
    request = Request(
        api_base.rstrip("/") + "/" + path,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2026-03-10",
            "User-Agent": "AppSurface-evidence-gate-verifier-context",
        },
        method="GET",
    )
    try:
        with build_opener(_NoRedirect).open(request, timeout=API_TIMEOUT_SECONDS) as response:
            if response.status != 200:
                raise VerifierContextError("The attempt-specific GitHub API read failed.")
            length = response.headers.get("Content-Length")
            if length is not None and (not length.isascii() or not length.isdecimal() or int(length) > MAX_API_BYTES):
                raise VerifierContextError("The attempt-specific GitHub API response length is invalid.")
            raw = response.read(MAX_API_BYTES + 1)
    except VerifierContextError:
        raise
    except (HTTPError, URLError, TimeoutError, OSError, ValueError):
        raise VerifierContextError("The attempt-specific GitHub API read failed.") from None
    return _parse_object(raw, MAX_API_BYTES, "The attempt-specific GitHub API response")


def resolve_identity(
    captured: Mapping[str, Any],
    environment: Mapping[str, str],
    api_get: Callable[[str], Mapping[str, Any]],
) -> dict[str, Any]:
    """Bind one captured PR identity to the current base-owned run and unique subject job."""
    repository = environment.get("GITHUB_REPOSITORY", "")
    if REPOSITORY_PATTERN.fullmatch(repository) is None or any(
        component in {".", ".."} for component in repository.split("/")
    ):
        raise VerifierContextError("The trusted repository name is malformed.")
    if environment.get("GITHUB_EVENT_NAME") != "pull_request_target":
        raise VerifierContextError("The trusted workflow event is not pull_request_target.")
    repository_id = _environment_positive(environment, "GITHUB_REPOSITORY_ID")
    run_id = _environment_positive(environment, "GITHUB_RUN_ID")
    attempt = _environment_positive(environment, "GITHUB_RUN_ATTEMPT")
    if (
        captured.get("RepositoryId") != repository_id
        or captured.get("WorkflowRunId") != run_id
        or captured.get("WorkflowRunAttempt") != attempt
        or captured.get("HeadRepositoryId") != repository_id
    ):
        raise VerifierContextError("The fresh controller identity differs from the trusted workflow context.")

    encoded_repository = quote(repository, safe="/")
    prefix = f"repos/{encoded_repository}/actions/runs/{run_id}/attempts/{attempt}"
    workflow = api_get(prefix)
    workflow_repository = workflow.get("repository")
    if (
        not isinstance(workflow_repository, dict)
        or _positive(workflow.get("id"), "The API run ID") != run_id
        or _positive(workflow.get("run_attempt"), "The API run attempt") != attempt
        or workflow.get("event") != "pull_request_target"
        or _positive(workflow_repository.get("id"), "The API repository ID") != repository_id
        or not isinstance(workflow_repository.get("full_name"), str)
        or workflow_repository["full_name"].casefold() != repository.casefold()
    ):
        raise VerifierContextError("The GitHub workflow attempt does not match the trusted controller context.")
    workflow_id = _positive(workflow.get("workflow_id"), "The workflow ID")

    jobs_response = api_get(prefix + "/jobs?per_page=100")
    jobs = jobs_response.get("jobs")
    if (
        not isinstance(jobs, list)
        or len(jobs) > 100
        or type(jobs_response.get("total_count")) is not int
        or jobs_response.get("total_count") != len(jobs)
    ):
        raise VerifierContextError("The attempt-specific subject-job list is incomplete or malformed.")
    matches = [job for job in jobs if isinstance(job, dict) and job.get("name") == SUBJECT_JOB_NAME]
    if len(matches) != 1:
        raise VerifierContextError("The attempt-specific subject job is missing or ambiguous.")
    job = matches[0]
    job_id = _positive(job.get("id"), "The subject job ID")
    if (
        _positive(job.get("run_id"), "The API subject run ID") != run_id
        or job.get("status") != "completed"
        or job.get("conclusion") != "success"
        or ("run_attempt" in job and _positive(job["run_attempt"], "The API subject run attempt") != attempt)
    ):
        raise VerifierContextError("The subject job is not a successful job from this run attempt.")
    return {
        "EventName": "pull_request_target",
        "Repository": repository,
        # The captured revision-bound identity has two more fields than the
        # canonical EvidencePullRequestRunIdentity consumed by the .NET host.
        # The verified plan carries base/head revisions separately.
        "RunIdentity": {key: captured[key] for key in EXPECTED_RUN_IDENTITY_KEYS},
        "SubjectJobId": str(job_id),
        "WorkflowId": str(workflow_id),
    }


def write_new_identity(path: Path, value: Mapping[str, Any]) -> None:
    try:
        descriptor = os.open(
            path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        with os.fdopen(descriptor, "wb") as output:
            output.write(_canonical(value))
            output.flush()
            os.fsync(output.fileno())
    except (OSError, ValueError):
        raise VerifierContextError("The trusted verifier identity output could not be written safely.") from None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fresh-identity", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args(argv)
    try:
        identity = _read_identity(Path(arguments.fresh_identity))
        api_base = os.environ.get("GITHUB_API_URL", "https://api.github.com")
        try:
            url = urlsplit(api_base)
        except ValueError:
            raise VerifierContextError("The trusted GitHub API endpoint is malformed.") from None
        if url.scheme != "https" or not url.netloc or url.username or url.password or url.query or url.fragment:
            raise VerifierContextError("The trusted GitHub API endpoint is malformed.")
        token = os.environ.get("GITHUB_TOKEN", "")
        if not token or len(token) > 4096 or any(char.isspace() or ord(char) < 32 for char in token):
            raise VerifierContextError("The trusted GitHub API token is unavailable.")
        expected = resolve_identity(identity, os.environ, lambda path: _read_api(path, api_base=api_base, token=token))
        write_new_identity(Path(arguments.output), expected)
        print("evidence-gate-verifier-context: current workflow and subject-job identity captured.")
        return 0
    except VerifierContextError as error:
        print(f"evidence-gate-verifier-context: ASEVC001: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
