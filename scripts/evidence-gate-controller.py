#!/usr/bin/env python3
"""Capture trusted pull-request revision evidence for the base-owned CI controller.

The caller must run this from a trusted pull_request_target job with read-only
pull-request API access. The event path, repository identity, target branch,
workflow event, run ID, attempt, and token come from trusted workflow context.
The event payload is data only; no head code is checked out or executed.

Same-repository output contains source.diff and the canonical
pull-request-run-identity.json contract object plus a retained bare Git store
at repository.git by default. Pass that path to the trusted CLI as --repository.
An optional external store must be a new path; the controller creates and
retains it on success, removes it on failure, and leaves successful-store
cleanup to the caller after the CLI has finished. Fork output contains only
observation.json and can never represent a gate-capable plan. A downstream
verifier must independently verify the plan/diff and reread current PR state
immediately before issuing a verdict.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import selectors
import signal
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Callable, Mapping, Sequence
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import HTTPRedirectHandler, Request, build_opener


MAX_EVENT_BYTES = 1024 * 1024
MAX_API_RESPONSE_BYTES = 64 * 1024
MAX_SOURCE_DIFF_BYTES = 20 * 1024 * 1024
MAX_CAPTURE_SECONDS = 120
API_TIMEOUT_SECONDS = 10
MAX_GIT_STDERR_BYTES = 4096
MAX_TARGET_BRANCH_CHARS = 128
ALLOWED_ACTIONS = frozenset({"opened", "synchronize", "reopened", "edited", "ready_for_review"})
SHA_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
REPOSITORY_COMPONENT_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\Z")
TARGET_BRANCH_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._/-]{0,127}\Z")
NUMERIC_ID_PATTERN = re.compile(r"[1-9][0-9]{0,18}\Z")
GIT_DIFF_OPTIONS = (
    "-c",
    "diff.external=",
    "-c",
    "diff.noprefix=false",
    "-c",
    "diff.mnemonicprefix=false",
    "-c",
    "diff.algorithm=myers",
    "diff",
    "--no-ext-diff",
    "--no-textconv",
    "--no-color",
    "--binary",
    "--full-index",
    "--unified=3",
    "--find-renames=50%",
    "--find-copies=50%",
    "--submodule=short",
    "--ignore-submodules=none",
    "--no-relative",
    "--src-prefix=a/",
    "--dst-prefix=b/",
)


class ControllerError(Exception):
    """A fixed, bounded diagnostic safe for controller logs."""


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ControllerError("JSON contains duplicate object keys.")
        result[key] = value
    return result


def _reject_json_constant(_value: str) -> None:
    raise ControllerError("JSON contains a non-standard numeric value.")


def _decode_json(data: bytes, maximum_bytes: int, description: str) -> Any:
    if len(data) > maximum_bytes:
        raise ControllerError(f"{description} exceeds its byte limit.")
    try:
        return json.loads(
            data,
            object_pairs_hook=_reject_duplicate_keys,
            parse_constant=_reject_json_constant,
        )
    except ControllerError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise ControllerError(f"{description} is malformed JSON.") from None


def _read_event(path_value: str | os.PathLike[str]) -> Mapping[str, Any]:
    raw_path = os.fspath(path_value)
    if not raw_path or "\x00" in raw_path or ".." in Path(raw_path).parts:
        raise ControllerError("The event path is malformed.")
    path = Path(raw_path)
    try:
        file_descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
    except (OSError, ValueError):
        raise ControllerError("The event file is unavailable or unsafe.") from None

    try:
        file_stat = os.fstat(file_descriptor)
        if not stat.S_ISREG(file_stat.st_mode) or file_stat.st_size > MAX_EVENT_BYTES:
            raise ControllerError("The event file is not a bounded regular file.")
        with os.fdopen(file_descriptor, "rb", closefd=False) as event_file:
            event_bytes = event_file.read(MAX_EVENT_BYTES + 1)
        if len(event_bytes) > MAX_EVENT_BYTES:
            raise ControllerError("The event file exceeds its byte limit.")
    finally:
        os.close(file_descriptor)

    event = _decode_json(event_bytes, MAX_EVENT_BYTES, "The event file")
    if not isinstance(event, dict):
        raise ControllerError("The event payload must be a JSON object.")
    return event


def _positive_integer(value: Any, name: str, maximum: int = (1 << 63) - 1) -> int:
    if type(value) is not int or value <= 0 or value > maximum:
        raise ControllerError(f"{name} must be a positive bounded integer.")
    return value


def _environment_integer(value: str | None, name: str, maximum: int = (1 << 63) - 1) -> int:
    if value is None or NUMERIC_ID_PATTERN.fullmatch(value) is None:
        raise ControllerError(f"{name} must be a positive decimal integer.")
    try:
        parsed = int(value, 10)
    except ValueError:
        raise ControllerError(f"{name} must be a positive decimal integer.") from None
    return _positive_integer(parsed, name, maximum)


def _full_sha(value: Any, name: str) -> str:
    if not isinstance(value, str) or SHA_PATTERN.fullmatch(value) is None:
        raise ControllerError(f"{name} must be a full lower-case 40- or 64-character Git object ID.")
    return value


def _repository(value: Any, name: str) -> str:
    if not isinstance(value, str) or value.count("/") != 1:
        raise ControllerError(f"{name} must be an owner/repository name.")
    owner, repository = value.split("/", 1)
    if (
        REPOSITORY_COMPONENT_PATTERN.fullmatch(owner) is None
        or REPOSITORY_COMPONENT_PATTERN.fullmatch(repository) is None
        or owner in {".", ".."}
        or repository in {".", ".."}
    ):
        raise ControllerError(f"{name} must be an owner/repository name.")
    return value


def _target_branch(value: Any, name: str) -> str:
    if (
        not isinstance(value, str)
        or len(value) > MAX_TARGET_BRANCH_CHARS
        or TARGET_BRANCH_PATTERN.fullmatch(value) is None
        or value.endswith(("/", ".", ".lock"))
        or "//" in value
        or ".." in value
        or "@{" in value
        or any(part.startswith(".") or part.endswith(".lock") for part in value.split("/"))
    ):
        raise ControllerError(f"{name} is not a safe target branch name.")
    return value


def _object_member(value: Any, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict):
        raise ControllerError(f"{name} is missing or malformed.")
    return value


def _event_snapshot(
    event: Mapping[str, Any],
    repository: str,
    repository_id: int,
    target_branch: str,
) -> dict[str, Any]:
    action = event.get("action")
    if not isinstance(action, str) or action not in ALLOWED_ACTIONS:
        raise ControllerError("The pull-request event action is not allowed.")

    pull_request = _object_member(event.get("pull_request"), "The pull request")
    base = _object_member(pull_request.get("base"), "The event base")
    head = _object_member(pull_request.get("head"), "The event head")
    base_repository = _object_member(base.get("repo"), "The event base repository")
    head_repository = _object_member(head.get("repo"), "The event head repository")
    event_repository = _object_member(event.get("repository"), "The event repository")

    event_number = _positive_integer(event.get("number"), "The event pull-request number", (1 << 31) - 1)
    pull_request_number = _positive_integer(
        pull_request.get("number"), "The pull-request number", (1 << 31) - 1
    )
    if event_number != pull_request_number:
        raise ControllerError("The event pull-request numbers do not match.")

    event_repo_id = _positive_integer(event_repository.get("id"), "The event repository ID")
    event_base_repo_id = _positive_integer(base_repository.get("id"), "The event base repository ID")
    event_head_repo_id = _positive_integer(head_repository.get("id"), "The event head repository ID")
    if event_repo_id != repository_id or event_base_repo_id != repository_id:
        raise ControllerError("The event repository identity does not match the trusted repository.")
    event_repository_name = _repository(
        event_repository.get("full_name"),
        "The event repository name",
    )
    if event_repository_name.casefold() != repository.casefold():
        raise ControllerError("The event repository name does not match the trusted repository.")
    event_base_repository_name = _repository(
        base_repository.get("full_name"),
        "The event base repository name",
    )
    if event_base_repository_name.casefold() != repository.casefold():
        raise ControllerError("The event base repository name does not match the trusted repository.")

    event_base_branch = _target_branch(base.get("ref"), "The event target branch")
    if event_base_branch != target_branch:
        raise ControllerError("The pull request targets an unexpected branch.")

    return {
        "action": action,
        "base_revision": _full_sha(base.get("sha"), "The event base revision"),
        "head_revision": _full_sha(head.get("sha"), "The event head revision"),
        "head_repository_id": event_head_repo_id,
        "head_repository": _repository(head_repository.get("full_name"), "The event head repository name"),
        "number": event_number,
        "repository_id": event_repo_id,
        "repository": repository,
        "target_branch": event_base_branch,
    }


def read_current_pull_request(repository: str, number: int, token: str) -> Mapping[str, Any]:
    """Read the bounded authoritative PR API response without following redirects."""

    encoded_repository = quote(repository, safe="/")
    url = f"https://api.github.com/repos/{encoded_repository}/pulls/{number}"
    request = Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": f"Bearer {token}",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "AppSurface-evidence-gate-controller",
        },
        method="GET",
    )

    class NoRedirectHandler(HTTPRedirectHandler):
        def redirect_request(
            self,
            _request: Any,
            _fp: Any,
            _code: int,
            _message: str,
            _headers: Any,
            _url: str,
        ) -> None:
            return None

    try:
        opener = build_opener(NoRedirectHandler)
        with opener.open(request, timeout=API_TIMEOUT_SECONDS) as response:
            if response.status != 200:
                raise ControllerError("The authoritative pull-request API read failed.")
            content_length = response.headers.get("Content-Length")
            if content_length is not None:
                if not content_length.isascii() or not content_length.isdecimal():
                    raise ControllerError("The pull-request API response length is malformed.")
                if int(content_length, 10) > MAX_API_RESPONSE_BYTES:
                    raise ControllerError("The pull-request API response exceeds its byte limit.")
            body = response.read(MAX_API_RESPONSE_BYTES + 1)
    except ControllerError:
        raise
    except (HTTPError, URLError, TimeoutError, OSError, ValueError):
        raise ControllerError("The authoritative pull-request API read failed.") from None

    result = _decode_json(body, MAX_API_RESPONSE_BYTES, "The pull-request API response")
    if not isinstance(result, dict):
        raise ControllerError("The pull-request API response is malformed.")
    return result


def _api_snapshot(
    response: Mapping[str, Any],
    repository: str,
    repository_id: int,
    expected_number: int,
    target_branch: str,
) -> dict[str, Any]:
    base = _object_member(response.get("base"), "The API base")
    head = _object_member(response.get("head"), "The API head")
    base_repository = _object_member(base.get("repo"), "The API base repository")
    head_repository = _object_member(head.get("repo"), "The API head repository")
    number = _positive_integer(response.get("number"), "The API pull-request number", (1 << 31) - 1)
    if number != expected_number or response.get("state") != "open":
        raise ControllerError("The authoritative pull request is not the open event pull request.")

    api_repository_id = _positive_integer(base_repository.get("id"), "The API base repository ID")
    api_head_repository_id = _positive_integer(head_repository.get("id"), "The API head repository ID")
    if api_repository_id != repository_id:
        raise ControllerError("The API base repository identity does not match the trusted repository.")
    api_base_repository_name = _repository(
        base_repository.get("full_name"),
        "The API base repository name",
    )
    if api_base_repository_name.casefold() != repository.casefold():
        raise ControllerError("The API base repository name does not match the trusted repository.")

    branch = _target_branch(base.get("ref"), "The API target branch")
    if branch != target_branch:
        raise ControllerError("The current pull request targets an unexpected branch.")
    return {
        "base_revision": _full_sha(base.get("sha"), "The API base revision"),
        "head_revision": _full_sha(head.get("sha"), "The API head revision"),
        "head_repository_id": api_head_repository_id,
        "head_repository": _repository(head_repository.get("full_name"), "The API head repository name"),
        "number": number,
        "repository_id": api_repository_id,
        "repository": repository,
        "target_branch": branch,
    }


def _require_current_match(event_snapshot: Mapping[str, Any], api_snapshot: Mapping[str, Any]) -> None:
    for field in (
        "base_revision",
        "head_revision",
        "head_repository_id",
        "head_repository",
        "number",
        "repository_id",
        "target_branch",
    ):
        event_value = event_snapshot[field]
        api_value = api_snapshot[field]
        if field == "head_repository":
            matches = event_value.casefold() == api_value.casefold()
        else:
            matches = event_value == api_value
        if not matches:
            raise ControllerError("The event pull request is stale or does not match current GitHub state.")


def _git_environment() -> dict[str, str]:
    inherited = os.environ
    allowed = {
        key: value
        for key, value in inherited.items()
        if key in {
            "PATH",
            "SystemRoot",
            "WINDIR",
            "TEMP",
            "TMP",
            "TMPDIR",
            "SSL_CERT_FILE",
            "SSL_CERT_DIR",
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "NO_PROXY",
            "http_proxy",
            "https_proxy",
            "no_proxy",
        }
    }
    allowed.update(
        {
            "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_CONFIG_GLOBAL": "NUL" if os.name == "nt" else os.devnull,
            "GIT_CONFIG_COUNT": "0",
            "GIT_CONFIG_PARAMETERS": "",
            "GIT_EXTERNAL_DIFF": "",
            "GIT_DIFF_OPTS": "",
            "GIT_PAGER": "cat",
            "GIT_TERMINAL_PROMPT": "0",
            "GIT_ASKPASS": os.devnull,
            "LC_ALL": "C",
        }
    )
    return allowed


def run_git_command(
    arguments: Sequence[str],
    *,
    deadline: float,
    maximum_output_bytes: int,
) -> bytes:
    """Run a fixed Git argument vector while bounding time and both output streams."""

    if not isinstance(arguments, (list, tuple)) or not arguments or any(not isinstance(arg, str) for arg in arguments):
        raise ControllerError("A Git command was not supplied as a fixed argument array.")
    argv = list(arguments)
    if argv[0] != "git":
        raise ControllerError("The controller may invoke only Git.")
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ControllerError("Git capture exceeded its deadline.")
    try:
        process = subprocess.Popen(
            argv,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            close_fds=True,
            env=_git_environment(),
        )
    except (OSError, ValueError):
        raise ControllerError("The trusted Git process could not start.") from None

    assert process.stdout is not None
    assert process.stderr is not None
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ, "stdout")
    selector.register(process.stderr, selectors.EVENT_READ, "stderr")
    output = bytearray()
    stderr_count = 0
    try:
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ControllerError("Git capture exceeded its deadline.")
            ready = selector.select(remaining)
            if not ready:
                raise ControllerError("Git capture exceeded its deadline.")
            for key, _ in ready:
                try:
                    chunk = os.read(key.fd, 64 * 1024)
                except OSError:
                    raise ControllerError("The trusted Git process output could not be read.") from None
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                    continue
                if key.data == "stdout":
                    if len(output) + len(chunk) > maximum_output_bytes:
                        raise ControllerError("Git output exceeds its reviewed byte limit.")
                    output.extend(chunk)
                else:
                    stderr_count += len(chunk)
                    if stderr_count > MAX_GIT_STDERR_BYTES:
                        raise ControllerError("Git diagnostics exceed their reviewed byte limit.")

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise ControllerError("Git capture exceeded its deadline.")
        try:
            return_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            raise ControllerError("Git capture exceeded its deadline.") from None
        if return_code != 0:
            raise ControllerError("Git could not read the exact trusted commit pair.")
        return bytes(output)
    except BaseException:
        if process.poll() is None:
            try:
                process.kill()
                process.wait(timeout=1)
            except (OSError, subprocess.TimeoutExpired):
                pass
        raise
    finally:
        selector.close()
        for pipe in (process.stdout, process.stderr):
            if not pipe.closed:
                pipe.close()


GitRunner = Callable[..., bytes]
ApiClient = Callable[[str, int, str], Mapping[str, Any]]


def _invoke_api(
    api_client: ApiClient,
    repository: str,
    number: int,
    token: str,
    deadline: float,
    *,
    enforce_alarm: bool,
) -> Mapping[str, Any]:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ControllerError("Pull-request capture exceeded its deadline.")

    if not enforce_alarm:
        result = api_client(repository, number, token)
    else:
        # urllib's socket timeout is an idle timeout. A response that trickles bytes
        # can otherwise hold the controller beyond the capture deadline.
        if not hasattr(signal, "setitimer") or threading.current_thread() is not threading.main_thread():
            raise ControllerError("The controller cannot enforce the API deadline on this runner.")
        previous_handler = signal.getsignal(signal.SIGALRM)
        if signal.getitimer(signal.ITIMER_REAL)[0] > 0:
            raise ControllerError("The controller cannot own the API deadline alarm.")

        def deadline_reached(_signal_number: int, _frame: Any) -> None:
            raise ControllerError("Pull-request capture exceeded its deadline.")

        signal.signal(signal.SIGALRM, deadline_reached)
        try:
            signal.setitimer(signal.ITIMER_REAL, min(API_TIMEOUT_SECONDS, remaining))
            result = api_client(repository, number, token)
        finally:
            try:
                signal.setitimer(signal.ITIMER_REAL, 0)
            finally:
                signal.signal(signal.SIGALRM, previous_handler)

    if time.monotonic() > deadline:
        raise ControllerError("Pull-request capture exceeded its deadline.")
    return result


def _invoke_git(
    git_runner: GitRunner,
    arguments: Sequence[str],
    deadline: float,
    maximum_output_bytes: int,
) -> bytes:
    result = git_runner(arguments, deadline=deadline, maximum_output_bytes=maximum_output_bytes)
    if not isinstance(result, bytes):
        raise ControllerError("The trusted Git process returned malformed output.")
    if len(result) > maximum_output_bytes:
        raise ControllerError("Git output exceeds its reviewed byte limit.")
    return result


def _git_prefix(object_store: Path) -> list[str]:
    return ["git", "--no-pager", f"--git-dir={object_store}"]


def _capture_diff(
    repository: str,
    repository_id: int,
    target_branch: str,
    run_id: int,
    run_attempt: int,
    token: str,
    event_path: str | os.PathLike[str],
    output_path: str | os.PathLike[str],
    *,
    object_store_path: str | os.PathLike[str] | None = None,
    api_client: ApiClient = read_current_pull_request,
    git_runner: GitRunner = run_git_command,
) -> str:
    if not isinstance(token, str) or not token or len(token) > 4096 or any(char.isspace() for char in token):
        raise ControllerError("The read-only GitHub token is missing or malformed.")
    repository = _repository(repository, "The trusted repository")
    repository_id = _positive_integer(repository_id, "The trusted repository ID")
    target_branch = _target_branch(target_branch, "The trusted target branch")
    run_id = _positive_integer(run_id, "The workflow run ID")
    run_attempt = _positive_integer(run_attempt, "The workflow run attempt", (1 << 31) - 1)
    output = _validate_output_destination(output_path)
    object_store, internal_store_path = _resolve_object_store(output, object_store_path)

    event = _read_event(event_path)
    event_snapshot = _event_snapshot(event, repository, repository_id, target_branch)
    deadline = time.monotonic() + MAX_CAPTURE_SECONDS

    try:
        current = _invoke_api(
            api_client, repository, event_snapshot["number"], token, deadline,
            enforce_alarm=api_client is read_current_pull_request,
        )
    except ControllerError:
        raise
    except Exception:
        raise ControllerError("The authoritative pull-request API read failed.") from None
    api_snapshot = _api_snapshot(
        current, repository, repository_id, event_snapshot["number"], target_branch
    )
    _require_current_match(event_snapshot, api_snapshot)

    identity = {
        "HeadRepositoryId": api_snapshot["head_repository_id"],
        "PullRequestNumber": api_snapshot["number"],
        "RepositoryId": repository_id,
        "TargetBranch": target_branch,
        "WorkflowRunAttempt": run_attempt,
        "WorkflowRunId": run_id,
    }

    if api_snapshot["head_repository_id"] != repository_id:
        observation = {
            "Action": event_snapshot["action"],
            "BaseRevision": event_snapshot["base_revision"],
            "HeadRepositoryId": api_snapshot["head_repository_id"],
            "HeadRevision": event_snapshot["head_revision"],
            "Mode": "ObservationOnly",
            "PullRequestNumber": event_snapshot["number"],
            "RepositoryId": repository_id,
            "TargetBranch": target_branch,
            "WorkflowRunAttempt": run_attempt,
            "WorkflowRunId": run_id,
        }
        _publish_output(output, {"observation.json": _canonical_json(observation)})
        return "observation"

    if api_snapshot["head_repository"].casefold() != repository.casefold():
        raise ControllerError("The same-repository head name does not match the trusted repository.")
    if len(event_snapshot["base_revision"]) != len(event_snapshot["head_revision"]):
        raise ControllerError("The pull-request Git object IDs use different formats.")

    stage: Path | None = None
    owned_external_store: Path | None = None
    try:
        if internal_store_path is None:
            try:
                os.mkdir(object_store, 0o700)
            except OSError:
                raise ControllerError("The object-store destination could not be created safely.") from None
            owned_external_store = object_store
        else:
            stage = _new_output_stage(output)
            object_store = stage / internal_store_path
            object_store.parent.mkdir(mode=0o700, parents=True, exist_ok=True)

        prefix = _git_prefix(object_store)
        _invoke_git(
            git_runner,
            ["git", "--no-pager", "init", "--bare", "--quiet", str(object_store)],
            deadline,
            4096,
        )
        repository_url = f"https://github.com/{repository}.git"
        fetch_arguments = [
            *prefix,
            "fetch",
            "--depth=1",
            "--no-tags",
            "--no-recurse-submodules",
            "--no-write-fetch-head",
            repository_url,
            f"+refs/heads/{target_branch}:refs/evidence/base",
            f"+refs/pull/{event_snapshot['number']}/head:refs/evidence/head",
        ]
        _invoke_git(git_runner, fetch_arguments, deadline, 64 * 1024)

        fetched_base = _invoke_git(
            git_runner,
            [*prefix, "rev-parse", "--verify", "refs/evidence/base^{commit}"],
            deadline,
            256,
        )
        fetched_head = _invoke_git(
            git_runner,
            [*prefix, "rev-parse", "--verify", "refs/evidence/head^{commit}"],
            deadline,
            256,
        )
        fetched_base_id = _parse_rev_parse_output(fetched_base, "The fetched base revision")
        fetched_head_id = _parse_rev_parse_output(fetched_head, "The fetched head revision")
        if (
            _full_sha(fetched_base_id, "The fetched base revision") != event_snapshot["base_revision"]
            or _full_sha(fetched_head_id, "The fetched head revision") != event_snapshot["head_revision"]
        ):
            raise ControllerError("The fetched Git refs do not match the event revisions.")

        diff_arguments = [
            *prefix,
            *GIT_DIFF_OPTIONS,
            event_snapshot["base_revision"],
            event_snapshot["head_revision"],
            "--",
        ]
        source_diff = _invoke_git(git_runner, diff_arguments, deadline, MAX_SOURCE_DIFF_BYTES)

        try:
            current = _invoke_api(
                api_client, repository, event_snapshot["number"], token, deadline,
                enforce_alarm=api_client is read_current_pull_request,
            )
        except ControllerError:
            raise
        except Exception:
            raise ControllerError("The authoritative pull-request API read failed.") from None
        api_snapshot = _api_snapshot(
            current, repository, repository_id, event_snapshot["number"], target_branch
        )
        _require_current_match(event_snapshot, api_snapshot)
        if api_snapshot["head_repository_id"] != repository_id:
            raise ControllerError("The pull request changed repository trust class during capture.")

        if stage is None:
            stage = _new_output_stage(output)
        _write_output_file(stage, "source.diff", source_diff)
        _write_output_file(stage, "pull-request-run-identity.json", _canonical_json(identity))
        _publish_staged_output(output, stage)
        stage = None
        owned_external_store = None
        return "captured"
    finally:
        if stage is not None:
            _remove_owned_directory(stage)
        if owned_external_store is not None:
            _remove_owned_directory(owned_external_store)


def _canonical_json(value: Mapping[str, Any]) -> bytes:
    """Match EvidenceCanonicalJson: compact JSON with ordinally sorted properties."""
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True).encode("utf-8")


def _parse_rev_parse_output(output: bytes, name: str) -> str:
    if len(output) not in {41, 65} or not output.endswith(b"\n") or output.count(b"\n") != 1:
        raise ControllerError("Git returned malformed full commit IDs.")
    try:
        return _full_sha(output[:-1].decode("ascii"), name)
    except UnicodeDecodeError:
        raise ControllerError("Git returned malformed full commit IDs.") from None


def _new_destination_path(path_value: str | os.PathLike[str], name: str) -> Path:
    raw_path = os.fspath(path_value)
    if not raw_path or "\x00" in raw_path or ".." in Path(raw_path).parts:
        raise ControllerError(f"The {name} path is malformed.")
    path = Path(raw_path).absolute()
    if path.name in {"", ".", ".."}:
        raise ControllerError(f"The {name} path is malformed.")
    return path


def _validate_output_destination(path_value: str | os.PathLike[str]) -> Path:
    path = _new_destination_path(path_value, "output")
    try:
        if path.is_symlink() or path.exists():
            raise ControllerError("The output destination must not already exist.")
        if not path.parent.is_dir():
            raise ControllerError("The output parent directory is unavailable.")
    except OSError:
        raise ControllerError("The output destination is unavailable or unsafe.") from None
    return path


def _resolve_object_store(
    output: Path,
    object_store_path: str | os.PathLike[str] | None,
) -> tuple[Path, Path | None]:
    if object_store_path is None:
        return output / "repository.git", Path("repository.git")

    object_store = _new_destination_path(object_store_path, "object-store")
    if object_store == output:
        raise ControllerError("The object-store path must be distinct from the output directory.")
    if output in object_store.parents:
        relative = object_store.relative_to(output)
        if object_store.exists() or object_store.is_symlink():
            raise ControllerError("The object-store destination must not already exist.")
        if any(part in {"", ".", ".."} for part in relative.parts):
            raise ControllerError("The object-store path is malformed.")
        return object_store, relative
    if object_store in output.parents:
        raise ControllerError("The external object-store path must not contain the output directory.")
    try:
        if object_store.exists() or object_store.is_symlink():
            raise ControllerError("The object-store destination must not already exist.")
        if not object_store.parent.is_dir():
            raise ControllerError("The object-store parent directory is unavailable.")
    except OSError:
        raise ControllerError("The object-store destination is unavailable or unsafe.") from None
    return object_store, None


def _new_output_stage(destination: Path) -> Path:
    try:
        stage = Path(tempfile.mkdtemp(prefix=f".{destination.name}.stage-", dir=destination.parent))
        os.chmod(stage, 0o700)
        return stage
    except OSError:
        raise ControllerError("The trusted capture staging directory could not be created.") from None


def _write_output_file(stage: Path, filename: str, content: bytes) -> None:
    if filename not in {
        "source.diff",
        "pull-request-run-identity.json",
        "observation.json",
    }:
        raise ControllerError("The controller attempted to publish an unexpected file.")
    file_path = stage / filename
    try:
        descriptor = os.open(
            file_path,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_CLOEXEC", 0),
            0o600,
        )
        with os.fdopen(descriptor, "wb") as output_file:
            output_file.write(content)
            output_file.flush()
            os.fsync(output_file.fileno())
    except OSError:
        raise ControllerError("The trusted capture output could not be written.") from None


def _publish_staged_output(destination: Path, stage: Path) -> None:
    try:
        directory_descriptor = os.open(stage, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(directory_descriptor)
        finally:
            os.close(directory_descriptor)
        if destination.exists() or destination.is_symlink():
            raise ControllerError("The output destination changed during capture.")
        os.rename(stage, destination)
        parent_descriptor = os.open(destination.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
        try:
            os.fsync(parent_descriptor)
        finally:
            os.close(parent_descriptor)
    except ControllerError:
        raise
    except OSError:
        raise ControllerError("The trusted capture output could not be published atomically.") from None


def _remove_owned_directory(path: Path) -> None:
    try:
        if path.is_dir() and not path.is_symlink():
            shutil.rmtree(path)
    except OSError:
        # Keep the primary capture failure; the job remains failed and the directory
        # is still at a controller-generated, caller-selected path for operator cleanup.
        pass


def _publish_output(destination: Path, files: Mapping[str, bytes]) -> None:
    stage: Path | None = None
    try:
        stage = _new_output_stage(destination)
        for filename, content in files.items():
            _write_output_file(stage, filename, content)
        _publish_staged_output(destination, stage)
        stage = None
    finally:
        if stage is not None:
            _remove_owned_directory(stage)


def _read_context(environment: Mapping[str, str]) -> dict[str, Any]:
    event_name = environment.get("GITHUB_EVENT_NAME")
    if event_name != "pull_request_target":
        raise ControllerError("This controller accepts only pull_request_target events.")
    token = environment.get("GITHUB_TOKEN")
    if token is None:
        raise ControllerError("The read-only GitHub token is missing.")
    return {
        "repository": _repository(environment.get("GITHUB_REPOSITORY"), "The trusted repository"),
        "repository_id": _environment_integer(environment.get("GITHUB_REPOSITORY_ID"), "GITHUB_REPOSITORY_ID"),
        "event_name": event_name,
        "run_id": _environment_integer(environment.get("GITHUB_RUN_ID"), "GITHUB_RUN_ID"),
        "run_attempt": _environment_integer(
            environment.get("GITHUB_RUN_ATTEMPT"), "GITHUB_RUN_ATTEMPT", (1 << 31) - 1
        ),
        "token": token,
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--event", required=True, help="Path to the bounded GitHub event JSON file.")
    parser.add_argument("--output", required=True, help="New output directory under an existing trusted parent.")
    parser.add_argument("--target-branch", required=True, help="Trusted allowed target branch, such as main.")
    parser.add_argument(
        "--object-store",
        help=(
            "Optional new bare Git object-store path. If omitted, the retained store is OUTPUT/repository.git. "
            "The caller owns cleanup after the trusted CLI has finished using it."
        ),
    )
    arguments = parser.parse_args(argv)
    try:
        context = _read_context(os.environ)
        result = _capture_diff(
            repository=context["repository"],
            repository_id=context["repository_id"],
            target_branch=arguments.target_branch,
            run_id=context["run_id"],
            run_attempt=context["run_attempt"],
            token=context["token"],
            event_path=arguments.event,
            output_path=arguments.output,
            object_store_path=arguments.object_store,
        )
    except ControllerError as failure:
        print(f"evidence-gate-controller: {failure}", file=sys.stderr)
        return 2
    except (OSError, ValueError):
        print("evidence-gate-controller: trusted capture failed closed.", file=sys.stderr)
        return 2
    if result == "observation":
        print("evidence-gate-controller: fork observation metadata captured; no gate claim is available.")
    else:
        print("evidence-gate-controller: exact same-repository source diff captured.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
