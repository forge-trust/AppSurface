#!/usr/bin/env python3
"""Prepare fixed candidate policy-shadow inputs from a captured Git commit.

This helper is intended for a base-owned workflow. It reads only the captured
HeadRevision's policy and fixture blobs from the retained bare Git store. It
does not check out the candidate tree or execute candidate-controlled code.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import selectors
import stat
import subprocess
import sys
import time
from typing import Any


MAX_FILE_BYTES = 1024 * 1024
MAX_TREE_OUTPUT_BYTES = 64 * 1024
MAX_GIT_STDERR_BYTES = 4096
MAX_GIT_SECONDS = 20
GIT_SHA_PATTERN = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
POLICY_PATH = ".appsurface/evidence/evidence.policy.json"
FIXTURES_PATH = "docs/fixtures/issue-777-policy-shadow/fixtures.json"
FIXED_CANDIDATE_PATHS = (POLICY_PATH, FIXTURES_PATH)
IDENTITY_KEYS = frozenset(
    {
        "BaseRevision",
        "HeadRepositoryId",
        "HeadRevision",
        "PullRequestNumber",
        "RepositoryId",
        "TargetBranch",
        "WorkflowRunAttempt",
        "WorkflowRunId",
    }
)


class PolicyShadowPreparerError(Exception):
    """A bounded diagnostic safe to print in trusted workflow logs."""


def _git_environment() -> dict[str, str]:
    path = os.environ.get("PATH")
    if not path:
        raise PolicyShadowPreparerError("The trusted Git executable is unavailable.")
    return {
        "PATH": path,
        "LC_ALL": "C",
        "GIT_CONFIG_NOSYSTEM": "1",
        "GIT_CONFIG_GLOBAL": os.devnull,
        "GIT_CONFIG_COUNT": "0",
        "GIT_CONFIG_PARAMETERS": "",
        "GIT_NO_REPLACE_OBJECTS": "1",
        "GIT_ATTR_NOSYSTEM": "1",
        "GIT_TERMINAL_PROMPT": "0",
        "GIT_PAGER": "cat",
    }


def _run_git(arguments: list[str], *, deadline: float, maximum_output_bytes: int) -> bytes:
    if not arguments or arguments[0] != "git" or any(not isinstance(argument, str) for argument in arguments):
        raise PolicyShadowPreparerError("A fixed Git argument vector is required.")
    if maximum_output_bytes < 0:
        raise PolicyShadowPreparerError("The Git output limit is invalid.")
    if deadline <= time.monotonic():
        raise PolicyShadowPreparerError("The bounded Git deadline expired.")

    try:
        process = subprocess.Popen(
            arguments,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            shell=False,
            close_fds=True,
            env=_git_environment(),
        )
    except (OSError, ValueError):
        raise PolicyShadowPreparerError("The trusted Git process could not start.") from None

    assert process.stdout is not None and process.stderr is not None
    selector = selectors.DefaultSelector()
    output = bytearray()
    stderr_count = 0
    try:
        os.set_blocking(process.stdout.fileno(), False)
        os.set_blocking(process.stderr.fileno(), False)
        selector.register(process.stdout, selectors.EVENT_READ, "stdout")
        selector.register(process.stderr, selectors.EVENT_READ, "stderr")
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise PolicyShadowPreparerError("The bounded Git deadline expired.")
            ready = selector.select(remaining)
            if not ready:
                raise PolicyShadowPreparerError("The bounded Git deadline expired.")
            for key, _ in ready:
                if key.data == "stdout":
                    remaining_bytes = maximum_output_bytes + 1 - len(output)
                else:
                    remaining_bytes = MAX_GIT_STDERR_BYTES + 1 - stderr_count
                try:
                    chunk = os.read(key.fileobj.fileno(), min(64 * 1024, remaining_bytes))
                except OSError:
                    raise PolicyShadowPreparerError("The trusted Git output could not be read.") from None
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                elif key.data == "stdout":
                    output.extend(chunk)
                    if len(output) > maximum_output_bytes:
                        raise PolicyShadowPreparerError("Git output exceeds its reviewed byte limit.")
                else:
                    stderr_count += len(chunk)
                    if stderr_count > MAX_GIT_STDERR_BYTES:
                        raise PolicyShadowPreparerError("Git diagnostics exceed their reviewed byte limit.")

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise PolicyShadowPreparerError("The bounded Git deadline expired.")
        try:
            return_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            raise PolicyShadowPreparerError("The bounded Git deadline expired.") from None
        if return_code != 0:
            raise PolicyShadowPreparerError("Git could not read the exact captured head commit.")
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


def _read_regular_file(path: Path, description: str) -> bytes:
    try:
        descriptor = os.open(
            path,
            os.O_RDONLY
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_NOFOLLOW", 0)
            | getattr(os, "O_NONBLOCK", 0),
        )
    except (OSError, ValueError):
        raise PolicyShadowPreparerError(f"{description} is unavailable or unsafe.") from None

    try:
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > MAX_FILE_BYTES:
            raise PolicyShadowPreparerError(f"{description} is not a bounded regular file.")
        content = bytearray()
        while True:
            chunk = os.read(descriptor, min(64 * 1024, MAX_FILE_BYTES + 1 - len(content)))
            if not chunk:
                return bytes(content)
            content.extend(chunk)
            if len(content) > MAX_FILE_BYTES:
                raise PolicyShadowPreparerError(f"{description} exceeds its 1 MiB byte limit.")
    except OSError:
        raise PolicyShadowPreparerError(f"{description} could not be read safely.") from None
    finally:
        os.close(descriptor)


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise PolicyShadowPreparerError("The captured identity contains duplicate keys.")
        result[key] = value
    return result


def _read_identity(capture_directory: Path) -> dict[str, Any]:
    capture_metadata = _lstat(capture_directory, "The controller capture directory")
    if not stat.S_ISDIR(capture_metadata.st_mode):
        raise PolicyShadowPreparerError("The controller capture directory is unavailable or unsafe.")
    identity_path = capture_directory / "pull-request-run-identity.json"
    raw_identity = _read_regular_file(identity_path, "The captured pull-request run identity")
    try:
        identity = json.loads(raw_identity, object_pairs_hook=_reject_duplicate_keys)
    except PolicyShadowPreparerError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise PolicyShadowPreparerError("The captured pull-request run identity is malformed.") from None
    if not isinstance(identity, dict) or set(identity) != IDENTITY_KEYS:
        raise PolicyShadowPreparerError("The captured pull-request run identity has an unexpected shape.")

    for key in ("BaseRevision", "HeadRevision"):
        revision = identity[key]
        if not isinstance(revision, str) or GIT_SHA_PATTERN.fullmatch(revision) is None:
            raise PolicyShadowPreparerError(f"The captured {key} is not a full lower-case Git object ID.")
    if len(identity["BaseRevision"]) != len(identity["HeadRevision"]):
        raise PolicyShadowPreparerError("The captured revisions use different Git object formats.")
    for key in ("HeadRepositoryId", "PullRequestNumber", "RepositoryId", "WorkflowRunAttempt", "WorkflowRunId"):
        value = identity[key]
        if type(value) is not int or value <= 0 or value > (1 << 63) - 1:
            raise PolicyShadowPreparerError(f"The captured {key} is not a positive bounded integer.")
    if identity["HeadRepositoryId"] != identity["RepositoryId"]:
        raise PolicyShadowPreparerError("The captured pull request is not same-repository input.")
    branch = identity["TargetBranch"]
    if not isinstance(branch, str) or not branch or len(branch) > 128 or any(char in branch for char in "\x00\r\n"):
        raise PolicyShadowPreparerError("The captured target branch is malformed.")
    return identity


def _lstat(path: Path, description: str) -> os.stat_result:
    try:
        return os.lstat(path)
    except (OSError, ValueError):
        raise PolicyShadowPreparerError(f"{description} is unavailable or unsafe.") from None


def _read_protected_base_file(protected_base: Path, relative_path: str) -> bytes:
    path = protected_base
    parts = relative_path.split("/")
    for index, part in enumerate(parts):
        path = path / part
        metadata = _lstat(path, "A fixed protected-base input")
        if stat.S_ISLNK(metadata.st_mode):
            raise PolicyShadowPreparerError("A fixed protected-base input is a symbolic link.")
        if index < len(parts) - 1 and not stat.S_ISDIR(metadata.st_mode):
            raise PolicyShadowPreparerError("A fixed protected-base input has an unsafe parent.")
    return _read_regular_file(path, "A fixed protected-base input")


def _captured_entries(repository: Path, head_revision: str, deadline: float) -> dict[str, tuple[str, str]]:
    common_arguments = [
        "git",
        "--no-pager",
        "--no-replace-objects",
        f"--git-dir={repository}",
        "-c",
        f"core.attributesFile={os.devnull}",
    ]
    object_type = _run_git(
        [*common_arguments, "cat-file", "-t", head_revision],
        deadline=deadline,
        maximum_output_bytes=32,
    )
    if object_type != b"commit\n":
        raise PolicyShadowPreparerError("The captured head object is not a commit.")

    tree = _run_git(
        [
            *common_arguments,
            "ls-tree",
            "-z",
            "--full-tree",
            "--no-abbrev",
            head_revision,
            "--",
            *FIXED_CANDIDATE_PATHS,
        ],
        deadline=deadline,
        maximum_output_bytes=MAX_TREE_OUTPUT_BYTES,
    )
    result: dict[str, tuple[str, str]] = {}
    if not tree:
        return result
    if not tree.endswith(b"\0"):
        raise PolicyShadowPreparerError("The captured head tree response is malformed.")
    fixed_paths = {path.encode("ascii") for path in FIXED_CANDIDATE_PATHS}
    for entry in tree[:-1].split(b"\0"):
        try:
            header, raw_path = entry.split(b"\t", 1)
            mode_bytes, object_type_bytes, object_id_bytes = header.split(b" ")
            candidate_path = raw_path.decode("ascii")
            mode = mode_bytes.decode("ascii")
            blob_type = object_type_bytes.decode("ascii")
            object_id = object_id_bytes.decode("ascii")
        except (UnicodeDecodeError, ValueError):
            raise PolicyShadowPreparerError("The captured head tree response is malformed.") from None
        if raw_path not in fixed_paths or candidate_path in result:
            raise PolicyShadowPreparerError("Git returned an unexpected fixed-path entry.")
        if mode != "100644" or blob_type != "blob":
            raise PolicyShadowPreparerError("A captured candidate input uses an unsafe Git file mode.")
        if GIT_SHA_PATTERN.fullmatch(object_id) is None or len(object_id) != len(head_revision):
            raise PolicyShadowPreparerError("A captured candidate blob has an invalid object ID.")
        result[candidate_path] = (mode, object_id)
    return result


def _write_new_file(path: Path, content: bytes) -> None:
    try:
        descriptor = os.open(
            path,
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_CLOEXEC", 0)
            | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        with os.fdopen(descriptor, "wb") as output_file:
            output_file.write(content)
            output_file.flush()
            os.fsync(output_file.fileno())
    except OSError:
        raise PolicyShadowPreparerError("A candidate policy-shadow input could not be written safely.") from None


def prepare(capture_directory: Path, protected_base: Path, output_directory: Path) -> Path:
    """Extract the captured fixed candidate inputs into a new private directory."""

    capture_directory = Path(capture_directory)
    protected_base = Path(protected_base)
    output_directory = Path(output_directory)

    protected_metadata = _lstat(protected_base, "The protected-base checkout")
    if not stat.S_ISDIR(protected_metadata.st_mode):
        raise PolicyShadowPreparerError("The protected-base checkout is unavailable or unsafe.")
    # Confirm the two CLI base inputs are bounded regular files under fixed paths.
    _read_protected_base_file(protected_base, POLICY_PATH)
    _read_protected_base_file(protected_base, FIXTURES_PATH)

    identity = _read_identity(capture_directory)
    repository = capture_directory / "repository.git"
    repository_metadata = _lstat(repository, "The retained controller Git store")
    if not stat.S_ISDIR(repository_metadata.st_mode):
        raise PolicyShadowPreparerError("The retained controller Git store is unavailable or unsafe.")

    output_parent = output_directory.parent
    parent_metadata = _lstat(output_parent, "The candidate output parent")
    if not stat.S_ISDIR(parent_metadata.st_mode):
        raise PolicyShadowPreparerError("The candidate output parent is unavailable or unsafe.")
    if os.path.lexists(output_directory):
        raise PolicyShadowPreparerError("The candidate output directory must be new.")

    deadline = time.monotonic() + MAX_GIT_SECONDS
    entries = _captured_entries(repository, identity["HeadRevision"], deadline)
    if POLICY_PATH not in entries:
        raise PolicyShadowPreparerError("The captured head is missing its required candidate policy.")

    blobs: dict[str, bytes] = {}
    for candidate_path in FIXED_CANDIDATE_PATHS:
        entry = entries.get(candidate_path)
        if entry is None:
            continue
        _mode, object_id = entry
        blobs[candidate_path] = _run_git(
            [
                "git",
                "--no-pager",
                "--no-replace-objects",
                f"--git-dir={repository}",
                "-c",
                f"core.attributesFile={os.devnull}",
                "cat-file",
                "blob",
                object_id,
            ],
            deadline=deadline,
            maximum_output_bytes=MAX_FILE_BYTES,
        )

    try:
        output_directory.mkdir(mode=0o700)
        for candidate_path, content in blobs.items():
            destination = output_directory.joinpath(*candidate_path.split("/"))
            destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            _write_new_file(destination, content)
    except PolicyShadowPreparerError:
        raise
    except OSError:
        raise PolicyShadowPreparerError("The candidate policy-shadow output could not be created safely.") from None
    return output_directory


def _parse_arguments(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture-directory", required=True, type=Path)
    parser.add_argument("--protected-base", required=True, type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    try:
        arguments = _parse_arguments(argv)
        output = prepare(arguments.capture_directory, arguments.protected_base, arguments.output_directory)
    except PolicyShadowPreparerError as error:
        print(f"evidence-gate-policy-shadow: {error}", file=sys.stderr)
        return 2
    print(f"Prepared fixed candidate policy-shadow inputs in {output}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
