#!/usr/bin/env python3
"""Restore validated coverage project timings from a recent successful build."""

from __future__ import annotations

import argparse
import io
import json
import os
import re
import subprocess
import sys
import time
import zipfile
import zlib
from datetime import datetime
from pathlib import Path
from urllib.parse import urlencode


MAX_JSON_BYTES = 2 * 1024 * 1024
MAX_ZIP_BYTES = MAX_JSON_BYTES + 64 * 1024
MAX_PROJECTS = 1000
MAX_SECONDS = 2_147_483_647
MAX_API_TIMEOUT_SECONDS = 30
MAX_RESTORE_SECONDS = 45


class InvalidData(Exception):
    """Raised when GitHub data or a timings artifact is malformed."""


class ApiFailure(Exception):
    """Raised when a GitHub CLI request cannot provide usable data."""


def _reject_duplicate_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise InvalidData("duplicate JSON object key")
        result[key] = value
    return result


def _reject_non_json_constant(value: str) -> None:
    raise InvalidData(f"invalid JSON constant: {value}")


def _decode_json(data: bytes) -> object:
    if len(data) > MAX_JSON_BYTES:
        raise InvalidData("JSON exceeds the 2 MiB limit")
    try:
        return json.loads(
            data.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_non_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError) as exc:
        raise InvalidData("invalid JSON") from exc


def _run_gh_api(endpoint: str, *, max_bytes: int, deadline: float) -> bytes:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ApiFailure("GitHub API time budget exhausted")
    try:
        result = subprocess.run(
            ["gh", "api", "--method", "GET", endpoint],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            check=False,
            timeout=min(MAX_API_TIMEOUT_SECONDS, remaining),
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise ApiFailure("gh api request failed") from exc
    if result.returncode != 0:
        raise ApiFailure("gh api request failed")
    if not isinstance(result.stdout, bytes) or len(result.stdout) > max_bytes:
        raise ApiFailure("gh api response exceeded its size limit")
    return result.stdout


def _api_json(endpoint: str, *, deadline: float) -> object:
    try:
        raw = _run_gh_api(endpoint, max_bytes=MAX_JSON_BYTES, deadline=deadline)
        return _decode_json(raw)
    except (ApiFailure, InvalidData) as exc:
        raise ApiFailure("GitHub API response was unavailable or invalid") from exc


def _valid_repository(repository: str) -> bool:
    parts = repository.split("/")
    return (
        len(parts) == 2
        and all(re.fullmatch(r"[A-Za-z0-9_.-]+", part) for part in parts)
        and all(part not in {".", ".."} for part in parts)
    )


def _valid_project_path(value: object) -> bool:
    if not isinstance(value, str) or not value or len(value) > 4096:
        return False
    if value != value.strip():
        return False
    if "\\" in value or ":" in value or any(ord(char) < 32 or ord(char) == 127 for char in value):
        return False
    if value.startswith("/") or not value.endswith(".csproj"):
        return False
    return all(part not in {"", ".", ".."} for part in value.split("/"))


def _validate_timings(raw: bytes) -> bytes:
    value = _decode_json(raw)
    if not isinstance(value, dict):
        raise InvalidData("timings must be a JSON object")
    projects = value.get("projects")
    if not isinstance(projects, list) or not projects or len(projects) > MAX_PROJECTS:
        raise InvalidData("projects must be a nonempty array of at most 1000 entries")

    seen: set[str] = set()
    for item in projects:
        if not isinstance(item, dict):
            raise InvalidData("each project timing must be an object")
        project = item.get("project")
        seconds = item.get("seconds")
        if not _valid_project_path(project):
            raise InvalidData("project must be a safe relative .csproj path")
        normalized_project = project.casefold()
        if normalized_project in seen:
            raise InvalidData("duplicate project path ignoring case")
        seen.add(normalized_project)
        if type(seconds) is not int or not 0 <= seconds <= MAX_SECONDS:
            raise InvalidData("seconds must be a bounded nonnegative integer")
    return raw


def _extract_timings(archive_bytes: bytes) -> bytes:
    if len(archive_bytes) > MAX_ZIP_BYTES:
        raise InvalidData("ZIP archive exceeds its size limit")
    try:
        archive = zipfile.ZipFile(io.BytesIO(archive_bytes))
    except (OSError, zipfile.BadZipFile, ValueError) as exc:
        raise InvalidData("invalid ZIP archive") from exc

    with archive:
        entries = archive.infolist()
        if any(info.file_size < 0 or info.file_size > MAX_JSON_BYTES for info in entries):
            raise InvalidData("ZIP entry exceeds the 2 MiB limit")
        matches = [info for info in entries if info.filename == "timings.json" and not info.is_dir()]
        if len(matches) != 1:
            raise InvalidData("ZIP must contain exactly one root timings.json")
        entry = matches[0]
        try:
            with archive.open(entry, "r") as stream:
                raw = stream.read(MAX_JSON_BYTES + 1)
        except (
            OSError,
            RuntimeError,
            zipfile.BadZipFile,
            EOFError,
            NotImplementedError,
            zlib.error,
        ) as exc:
            raise InvalidData("could not read timings.json from ZIP") from exc
        if len(raw) > MAX_JSON_BYTES or len(raw) != entry.file_size:
            raise InvalidData("timings.json exceeds its size limit or is incomplete")
        return _validate_timings(raw)


def _created_at(run: dict[str, object]) -> float | None:
    value = run.get("created_at")
    if not isinstance(value, str):
        return None
    try:
        timestamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
        if timestamp.tzinfo is None:
            return None
        return timestamp.timestamp()
    except (OverflowError, OSError, ValueError):
        return None


def _workflow_runs(
    repository: str, default_branch: str, *, deadline: float
) -> list[dict[str, object]]:
    query = urlencode(
        {
            "branch": default_branch,
            "event": "push",
            "status": "success",
            "per_page": "10",
        }
    )
    payload = _api_json(
        f"repos/{repository}/actions/workflows/build.yml/runs?{query}",
        deadline=deadline,
    )
    if not isinstance(payload, dict) or not isinstance(payload.get("workflow_runs"), list):
        raise ApiFailure("workflow runs response had an invalid shape")

    eligible: list[tuple[float, int, dict[str, object]]] = []
    for run in payload["workflow_runs"]:
        if not isinstance(run, dict):
            continue
        run_id = run.get("id")
        created_at = _created_at(run)
        if (
            type(run_id) is int
            and run_id > 0
            and run.get("head_branch") == default_branch
            and run.get("event") == "push"
            and isinstance(run.get("head_repository"), dict)
            and isinstance(run["head_repository"].get("full_name"), str)
            and run["head_repository"]["full_name"].casefold() == repository.casefold()
            and run.get("status") == "completed"
            and run.get("conclusion") == "success"
            and created_at is not None
        ):
            eligible.append((created_at, run_id, run))
    eligible.sort(key=lambda item: (item[0], item[1]), reverse=True)
    return [run for _, _, run in eligible]


def _run_artifacts(
    repository: str, run_id: int, *, deadline: float
) -> list[dict[str, object]]:
    endpoint = f"repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100"
    payload = _api_json(endpoint, deadline=deadline)
    if not isinstance(payload, dict) or not isinstance(payload.get("artifacts"), list):
        raise ApiFailure("artifacts response had an invalid shape")
    return [item for item in payload["artifacts"] if isinstance(item, dict)]


def _restore_bytes(repository: str, default_branch: str) -> tuple[bytes | None, str]:
    deadline = time.monotonic() + MAX_RESTORE_SECONDS
    try:
        runs = _workflow_runs(repository, default_branch, deadline=deadline)
    except ApiFailure:
        return None, "GitHub workflow history is unavailable"
    if not runs:
        return None, "no successful completed build runs found on the default branch"

    for run in runs:
        run_id = run["id"]
        try:
            artifacts = _run_artifacts(repository, run_id, deadline=deadline)
        except ApiFailure:
            return None, "GitHub artifact history is unavailable"
        for artifact in artifacts:
            if artifact.get("name") != "coverage-schedule-timings" or artifact.get("expired") is not False:
                continue
            size_in_bytes = artifact.get("size_in_bytes")
            if (
                type(size_in_bytes) is not int
                or size_in_bytes < 0
                or size_in_bytes > MAX_ZIP_BYTES
            ):
                continue
            artifact_id = artifact.get("id")
            if type(artifact_id) is not int or artifact_id <= 0:
                continue
            try:
                archive_bytes = _run_gh_api(
                    f"repos/{repository}/actions/artifacts/{artifact_id}/zip",
                    max_bytes=MAX_ZIP_BYTES,
                    deadline=deadline,
                )
            except ApiFailure:
                return None, "GitHub artifact download is unavailable"
            try:
                return _extract_timings(archive_bytes), ""
            except InvalidData:
                continue
    return None, "no valid coverage schedule timings artifact found"


def _required_environment(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"{name} is required")
    return value


def _write_new_file(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o666)
    try:
        with os.fdopen(descriptor, "wb") as output:
            output.write(content)
    except BaseException:
        try:
            path.unlink()
        except OSError:
            pass
        raise


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    restore = subparsers.add_parser("restore", help="restore recent validated project timings")
    restore.add_argument("--output", required=True, help="absolute path to create when history is valid")
    args = parser.parse_args(argv)

    output = Path(args.output)
    if not output.is_absolute() or "\x00" in args.output:
        parser.error("--output must be an absolute path without NUL characters")
    if os.path.lexists(output):
        parser.error(f"--output already exists: {output}")

    try:
        repository = _required_environment("GITHUB_REPOSITORY")
        default_branch = _required_environment("GITHUB_DEFAULT_BRANCH")
        if not _valid_repository(repository):
            raise ValueError("GITHUB_REPOSITORY must be an owner/repository pair")
        if len(default_branch) > 255 or any(ord(char) < 32 or ord(char) == 127 for char in default_branch):
            raise ValueError("GITHUB_DEFAULT_BRANCH is invalid")
    except ValueError as exc:
        parser.error(str(exc))

    content, warning = _restore_bytes(repository, default_branch)
    if content is None:
        print(f"warning: {warning}", file=sys.stderr)
        return 0

    try:
        _write_new_file(output, content)
    except OSError as exc:
        parser.error(f"could not create --output: {exc}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
