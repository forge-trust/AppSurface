#!/usr/bin/env python3
"""Run the fixed, offline code-coverage subject profile.

This executable is intended to be installed in the digest-pinned subject image
at ``/usr/local/libexec/appsurface-subject-runner``.  It accepts only the
controller's fixed profile arguments; it never accepts a command or image from
the subject.  The subject checkout is validated as a read-only mount and copied
without following links into private scratch, where .NET may write build output.

The result is an execution record, not an Evidence claim.  The fixed profile
checks aggregate and patch coverage against the controller-captured diff, but
the current full solution also needs container-backed tests that this offline
envelope cannot run. A later trusted verifier must independently validate any
artifacts and the execution envelope before a complete producer can exist.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import selectors
import signal
import stat
import subprocess
import sys
import time
from typing import Callable, Mapping, Sequence
import xml.etree.ElementTree as ElementTree


SUBJECT_ROOT = Path("/subject")
SCRATCH_ROOT = Path("/scratch")
DEPENDENCY_SOURCE = Path("/opt/appsurface/locked-dependencies")
PATCH_DIFF_PATH = Path("/source.diff")
SOLUTION_RELATIVE_PATH = Path("ForgeTrust.AppSurface.slnx")
CLI_RELATIVE_PATH = Path("Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj")
RESULT_RELATIVE_PATH = Path("evidence-subject-result.json")
RESTORE_CONFIG_RELATIVE_PATH = Path("offline-nuget.config")
STAGED_SUBJECT_RELATIVE_PATH = Path("source")
OUTPUT_RELATIVE_PATH = Path("coverage")

MAX_EXECUTION_SECONDS = 1200
MAX_OUTPUT_BYTES = 1024 * 1024
MAX_RESULT_BYTES = 16 * 1024
MAX_SUBJECT_FILES = 250_000
MAX_SUBJECT_BYTES = 2 * 1024 * 1024 * 1024
MAX_SOURCE_DIFF_BYTES = 20 * 1024 * 1024
MAX_COBERTURA_ARTIFACT_BYTES = 20 * 1024 * 1024
MAX_GATE_MARKDOWN_ARTIFACT_BYTES = 1 * 1024 * 1024
MAX_GATE_JSON_ARTIFACT_BYTES = 4 * 1024 * 1024
COPY_CHUNK_BYTES = 1024 * 1024
DOTNET_PATH = "/usr/local/bin:/usr/bin:/bin"
DOTNET_MAJOR_VERSION = 10
SDK_RUNTIME_IDENTIFIER = "linux-x64"
SDK_RUNTIME_IDENTIFIER_PROPERTY = "$(NETCoreSdkRuntimeIdentifier)"
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}\Z")
REQUIRED_ARTIFACTS = (
    ("cobertura", "coverage/coverage-merged/coverage.cobertura.xml", MAX_COBERTURA_ARTIFACT_BYTES),
    ("gate-report", "coverage/coverage-gate/coverage-gate.md", MAX_GATE_MARKDOWN_ARTIFACT_BYTES),
    # Policy marks diagnostics optional. This fixed profile requires its gate JSON report as a conservative extra check.
    ("diagnostics", "coverage/coverage-gate/coverage-gate.json", MAX_GATE_JSON_ARTIFACT_BYTES),
)

FIXED_PROFILE_ID = "code-coverage"
FIXED_SUBJECT_ARGUMENT = "--subject-root=/subject"
FIXED_SCRATCH_ARGUMENT = "--scratch-root=/scratch"
FIXED_DEPENDENCY_ARGUMENT = "--dependency-source=/opt/appsurface/locked-dependencies"
FIXED_OFFLINE_ARGUMENT = "--offline"

OFFLINE_UNSUPPORTED_TEST_PROJECTS = (
    "Cli/ForgeTrust.AppSurface.Cli.Tests/ForgeTrust.AppSurface.Cli.Tests.csproj",
    "Durable/ForgeTrust.AppSurface.Durable.PostgreSql.Tests/ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj",
)


class EntrypointError(Exception):
    """A stable failure suitable for a bounded machine result."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


@dataclass(frozen=True)
class Invocation:
    profile_id: str
    subject_root: Path
    scratch_root: Path
    dependency_source: Path
    diff_file: Path
    diff_sha256: str
    offline: bool


@dataclass(frozen=True)
class ProcessResult:
    exit_code: int
    stdout: bytes
    stderr: bytes


@dataclass(frozen=True)
class StepResult:
    name: str
    exit_code: int
    output_bytes: int
    stdout_sha256: str
    stderr_sha256: str


@dataclass
class OutputBudget:
    maximum_bytes: int
    used_bytes: int = 0

    def consume(self, count: int) -> None:
        if count < 0 or self.used_bytes + count > self.maximum_bytes:
            raise EntrypointError("ASESE006", "Subject process output exceeded the fixed byte limit.")
        self.used_bytes += count


ProcessRunner = Callable[..., ProcessResult]


def parse_invocation(argv: Sequence[str] | None = None) -> Invocation:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile-id", required=True, choices=(FIXED_PROFILE_ID,))
    parser.add_argument("--subject-root", required=True, choices=(str(SUBJECT_ROOT),))
    parser.add_argument("--scratch-root", required=True, choices=(str(SCRATCH_ROOT),))
    parser.add_argument("--dependency-source", required=True, choices=(str(DEPENDENCY_SOURCE),))
    parser.add_argument("--diff-file", required=True, choices=(str(PATCH_DIFF_PATH),))
    parser.add_argument("--diff-sha256", required=True)
    parser.add_argument("--offline", action="store_true", required=True)
    args = parser.parse_args(argv)
    if SHA256_PATTERN.fullmatch(args.diff_sha256) is None:
        raise EntrypointError("ASESE012", "The controller source diff digest is malformed.")
    return Invocation(
        profile_id=args.profile_id,
        subject_root=Path(args.subject_root),
        scratch_root=Path(args.scratch_root),
        dependency_source=Path(args.dependency_source),
        diff_file=Path(args.diff_file),
        diff_sha256=args.diff_sha256,
        offline=args.offline,
    )


def _decode_mount_field(value: str) -> str:
    return value.replace("\\040", " ").replace("\\011", "\t").replace("\\012", "\n").replace("\\134", "\\")


def read_mounts(mountinfo_text: str) -> dict[Path, frozenset[str]]:
    mounts: dict[Path, frozenset[str]] = {}
    for line in mountinfo_text.splitlines():
        left, separator, _right = line.partition(" - ")
        fields = left.split()
        if not separator or len(fields) < 6:
            raise EntrypointError("ASESE001", "The container mount table is malformed.")
        mount_path = Path(_decode_mount_field(fields[4]))
        options = frozenset(fields[5].split(","))
        mounts[mount_path] = options
    return mounts


def _effective_mount(path: Path, mounts: Mapping[Path, frozenset[str]]) -> tuple[Path, frozenset[str]]:
    candidates = [mount for mount in mounts if path == mount or mount in path.parents]
    if not candidates:
        raise EntrypointError("ASESE001", "A required container mount is unavailable.")
    mount = max(candidates, key=lambda item: len(item.parts))
    return mount, mounts[mount]


def _canonical_directory(path: Path, name: str) -> Path:
    try:
        info = path.lstat()
        if stat.S_ISLNK(info.st_mode) or not stat.S_ISDIR(info.st_mode):
            raise EntrypointError("ASESE001", f"{name} is not a physical directory.")
        resolved = path.resolve(strict=True)
    except EntrypointError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise EntrypointError("ASESE001", f"{name} is unavailable.") from None
    if resolved != path:
        raise EntrypointError("ASESE001", f"{name} is not canonical.")
    return resolved


def _validate_source_diff(path: Path, expected_sha256: str) -> None:
    if path != PATCH_DIFF_PATH or SHA256_PATTERN.fullmatch(expected_sha256) is None:
        raise EntrypointError("ASESE012", "The controller source diff configuration is invalid.")
    descriptor = -1
    try:
        metadata = path.lstat()
        if stat.S_ISLNK(metadata.st_mode) or not stat.S_ISREG(metadata.st_mode):
            raise EntrypointError("ASESE012", "The controller source diff is not a physical regular file.")
        if path.resolve(strict=True) != path or metadata.st_size > MAX_SOURCE_DIFF_BYTES:
            raise EntrypointError("ASESE012", "The controller source diff is noncanonical or exceeds its byte limit.")
        descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0))
        opened = os.fstat(descriptor)
        if (
            not stat.S_ISREG(opened.st_mode)
            or (opened.st_dev, opened.st_ino) != (metadata.st_dev, metadata.st_ino)
            or opened.st_size > MAX_SOURCE_DIFF_BYTES
        ):
            raise EntrypointError("ASESE012", "The controller source diff changed or exceeds its byte limit.")
        digest = hashlib.sha256()
        total = 0
        while True:
            chunk = os.read(descriptor, min(COPY_CHUNK_BYTES, MAX_SOURCE_DIFF_BYTES + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > MAX_SOURCE_DIFF_BYTES:
                raise EntrypointError("ASESE012", "The controller source diff exceeds its byte limit.")
            digest.update(chunk)
        after = os.fstat(descriptor)
        if (
            total != opened.st_size
            or (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
            != (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns)
            or digest.hexdigest() != expected_sha256
        ):
            raise EntrypointError(
                "ASESE012", "The mounted source diff is missing, changed, or does not match its controller digest."
            )
    except EntrypointError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise EntrypointError("ASESE012", "The controller source diff is unavailable or unsafe.") from None
    finally:
        if descriptor >= 0:
            try:
                os.close(descriptor)
            except OSError:
                raise EntrypointError("ASESE012", "The controller source diff could not be closed safely.") from None


def validate_mounts(
    subject_root: Path,
    scratch_root: Path,
    dependency_source: Path,
    diff_file: Path,
    diff_sha256: str,
    *,
    mountinfo_text: str,
    effective_uid: int,
) -> None:
    subject = _canonical_directory(subject_root, "The subject root")
    scratch = _canonical_directory(scratch_root, "The scratch root")
    dependency = _canonical_directory(dependency_source, "The locked dependency source")
    _validate_source_diff(diff_file, diff_sha256)
    mounts = read_mounts(mountinfo_text)

    if (
        subject != SUBJECT_ROOT
        or scratch != SCRATCH_ROOT
        or dependency != DEPENDENCY_SOURCE
        or diff_file != PATCH_DIFF_PATH
    ):
        raise EntrypointError(
            "ASESE001", "The subject, scratch, dependency, and source diff must use their fixed paths."
        )
    if mounts.get(SUBJECT_ROOT) is None or "ro" not in mounts[SUBJECT_ROOT]:
        raise EntrypointError("ASESE001", "The subject mount is not read-only.")
    if mounts.get(SCRATCH_ROOT) is None or "rw" not in mounts[SCRATCH_ROOT]:
        raise EntrypointError("ASESE001", "The scratch mount is not writable.")
    diff_options = mounts.get(PATCH_DIFF_PATH)
    if diff_options is None or "ro" not in diff_options or "rw" in diff_options:
        raise EntrypointError("ASESE001", "The controller source diff mount is not read-only.")

    scratch_info = scratch.stat()
    if scratch_info.st_uid != effective_uid or stat.S_IMODE(scratch_info.st_mode) != 0o700:
        raise EntrypointError("ASESE001", "The scratch directory is not private to the subject user.")
    try:
        if next(scratch.iterdir(), None) is not None:
            raise EntrypointError("ASESE001", "The scratch directory must be empty at entry.")
    except OSError:
        raise EntrypointError("ASESE001", "The scratch directory cannot be inspected safely.") from None

    dependency_mount, dependency_options = _effective_mount(dependency, mounts)
    if "ro" not in dependency_options or SCRATCH_ROOT == dependency_mount or SCRATCH_ROOT in dependency.parents:
        raise EntrypointError("ASESE001", "The locked dependency source is not on a read-only image mount.")


def _validate_dependency_feed(root: Path) -> None:
    """Scan every feed entry for physical files; locked restore verifies package hashes."""
    try:
        root_info = root.stat()
        if not stat.S_ISDIR(root_info.st_mode):
            raise EntrypointError("ASESE002", "The locked dependency source is unavailable.")
        stack = [root]
        entries_seen = 0
        package_seen = False
        while stack:
            current = stack.pop()
            with os.scandir(current) as entries:
                for entry in entries:
                    entries_seen += 1
                    if entries_seen > MAX_SUBJECT_FILES:
                        raise EntrypointError("ASESE002", "The locked dependency source exceeds its fixed entry limit.")
                    info = entry.stat(follow_symlinks=False)
                    if stat.S_ISLNK(info.st_mode):
                        raise EntrypointError("ASESE002", "The locked dependency source contains a symbolic link.")
                    if stat.S_ISDIR(info.st_mode):
                        stack.append(Path(entry.path))
                    elif stat.S_ISREG(info.st_mode) and entry.name.endswith(".nupkg"):
                        package_seen = True
                    elif not stat.S_ISREG(info.st_mode):
                        raise EntrypointError("ASESE002", "The locked dependency source contains an unsupported file type.")
        if not package_seen:
            raise EntrypointError("ASESE002", "The locked dependency source contains no offline NuGet packages.")
    except EntrypointError:
        raise
    except OSError:
        raise EntrypointError("ASESE002", "The locked dependency source is unavailable or unreadable.") from None


def _process_environment(scratch_root: Path) -> dict[str, str]:
    home = scratch_root / "dotnet-home"
    temp = scratch_root / "tmp"
    packages = scratch_root / "nuget-packages"
    for directory in (home, temp, packages):
        directory.mkdir(mode=0o700)
    return {
        "DOTNET_CLI_HOME": str(home),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "HOME": str(home),
        "NUGET_PACKAGES": str(packages),
        "PATH": DOTNET_PATH,
        "TEMP": str(temp),
        "TMP": str(temp),
        "TMPDIR": str(temp),
    }


def _terminate_process_group(process: subprocess.Popen[bytes]) -> None:
    # A command can exit while a descendant still owns its redirected pipes.
    # Signal the private session before reaping its leader so its group ID cannot
    # be reused between the lookup and cleanup.
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    except OSError:
        raise EntrypointError("ASESE011", "The subject process group could not be stopped safely.") from None
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        raise EntrypointError("ASESE011", "The subject process group could not be reaped after termination.") from None


def run_bounded_process(
    arguments: Sequence[str],
    *,
    working_directory: Path,
    environment: Mapping[str, str],
    deadline: float,
    output_budget: OutputBudget,
) -> ProcessResult:
    if not arguments or any(not isinstance(argument, str) or "\x00" in argument for argument in arguments):
        raise EntrypointError("ASESE003", "A fixed subject process argument is malformed.")
    try:
        process = subprocess.Popen(
            list(arguments),
            cwd=working_directory,
            env=dict(environment),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            close_fds=True,
            start_new_session=True,
        )
    except OSError:
        raise EntrypointError("ASESE003", "The required offline runtime or command is unavailable.") from None

    assert process.stdout is not None and process.stderr is not None
    stdout = bytearray()
    stderr = bytearray()
    selector = selectors.DefaultSelector()
    streams = {process.stdout.fileno(): stdout, process.stderr.fileno(): stderr}
    try:
        for stream in (process.stdout, process.stderr):
            os.set_blocking(stream.fileno(), False)
            selector.register(stream, selectors.EVENT_READ)
        while selector.get_map():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise EntrypointError("ASESE005", "The fixed subject execution deadline expired.")
            for key, _ in selector.select(min(remaining, 0.25)):
                descriptor = key.fileobj.fileno()
                target = streams[descriptor]
                allowance = min(COPY_CHUNK_BYTES, output_budget.maximum_bytes - output_budget.used_bytes + 1)
                try:
                    chunk = os.read(descriptor, max(1, allowance))
                except BlockingIOError:
                    continue
                if not chunk:
                    selector.unregister(key.fileobj)
                    key.fileobj.close()
                    continue
                output_budget.consume(len(chunk))
                target.extend(chunk)
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise EntrypointError("ASESE005", "The fixed subject execution deadline expired.")
        try:
            exit_code = process.wait(timeout=remaining)
        except subprocess.TimeoutExpired:
            raise EntrypointError("ASESE005", "The fixed subject execution deadline expired.") from None
        return ProcessResult(exit_code, bytes(stdout), bytes(stderr))
    except BaseException:
        _terminate_process_group(process)
        raise
    finally:
        selector.close()
        for stream in (process.stdout, process.stderr):
            if not stream.closed:
                stream.close()


def _check_deadline(deadline: float) -> None:
    if time.monotonic() >= deadline:
        raise EntrypointError("ASESE005", "The fixed subject execution deadline expired.")


def _copy_subject_tree(source: Path, destination: Path, *, deadline: float) -> tuple[int, int]:
    """Copy regular files without following links or accepting concurrent changes."""
    try:
        source_info = source.stat()
        if not stat.S_ISDIR(source_info.st_mode):
            raise EntrypointError("ASESE001", "The subject root is not a directory.")
        destination.mkdir(mode=0o700)
    except EntrypointError:
        raise
    except OSError:
        raise EntrypointError("ASESE003", "A private subject staging directory could not be created.") from None

    copied_files = 0
    copied_bytes = 0
    pending: list[tuple[Path, Path, bool]] = [(source, destination, True)]
    while pending:
        _check_deadline(deadline)
        source_directory, target_directory, is_root = pending.pop()
        try:
            with os.scandir(source_directory) as iterator:
                entries = sorted(iterator, key=lambda item: item.name)
        except OSError:
            raise EntrypointError("ASESE003", "The read-only subject tree could not be enumerated.") from None
        for entry in entries:
            _check_deadline(deadline)
            source_path = source_directory / entry.name
            target_path = target_directory / entry.name
            try:
                before = entry.stat(follow_symlinks=False)
            except OSError:
                raise EntrypointError("ASESE003", "A subject entry changed during staging.") from None
            if stat.S_ISLNK(before.st_mode):
                raise EntrypointError("ASESE003", "The subject tree contains a symbolic link.")
            if stat.S_ISDIR(before.st_mode):
                try:
                    target_path.mkdir(mode=0o700)
                except OSError:
                    raise EntrypointError("ASESE003", "A private subject directory could not be staged.") from None
                pending.append((source_path, target_path, False))
                continue
            if not stat.S_ISREG(before.st_mode):
                raise EntrypointError("ASESE003", "The subject tree contains an unsupported file type.")
            if entry.name.casefold() == "nuget.config":
                raise EntrypointError("ASESE003", "Subject-controlled NuGet configuration is unsupported.")
            if before.st_nlink != 1:
                raise EntrypointError("ASESE003", "The subject tree contains a hard-linked file.")
            copied_files += 1
            if copied_files > MAX_SUBJECT_FILES or copied_bytes + before.st_size > MAX_SUBJECT_BYTES:
                raise EntrypointError("ASESE003", "The subject tree exceeds its fixed staging limits.")
            flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
            source_descriptor = -1
            target_descriptor = -1
            try:
                source_descriptor = os.open(source_path, flags)
                target_mode = 0o600 | (0o100 if before.st_mode & 0o111 else 0)
                target_descriptor = os.open(
                    target_path,
                    os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0),
                    target_mode,
                )
            except OSError:
                if source_descriptor >= 0:
                    os.close(source_descriptor)
                if target_descriptor >= 0:
                    os.close(target_descriptor)
                raise EntrypointError("ASESE003", "A subject file could not be opened safely for staging.") from None
            try:
                opened = os.fstat(source_descriptor)
                if (
                    not stat.S_ISREG(opened.st_mode)
                    or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino)
                    or opened.st_nlink != before.st_nlink
                ):
                    raise EntrypointError("ASESE003", "A subject file changed during staging.")
                file_bytes = 0
                while True:
                    _check_deadline(deadline)
                    chunk = os.read(source_descriptor, COPY_CHUNK_BYTES)
                    if not chunk:
                        break
                    file_bytes += len(chunk)
                    copied_bytes += len(chunk)
                    if copied_bytes > MAX_SUBJECT_BYTES or file_bytes > before.st_size:
                        raise EntrypointError("ASESE003", "The subject tree exceeds its fixed staging limits.")
                    view = memoryview(chunk)
                    while view:
                        written = os.write(target_descriptor, view)
                        view = view[written:]
                after = os.fstat(source_descriptor)
                if (
                    file_bytes != before.st_size
                    or (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns)
                    != (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns)
                ):
                    raise EntrypointError("ASESE003", "A subject file changed during staging.")
            except OSError:
                raise EntrypointError("ASESE003", "A subject file could not be copied safely.") from None
            finally:
                os.close(source_descriptor)
                os.close(target_descriptor)
    return copied_files, copied_bytes


def _project_lock_path(project: Path) -> Path:
    try:
        document = ElementTree.parse(project)
        configured_paths = [
            element.text
            for element in document.iter()
            if element.tag.split("}")[-1] == "NuGetLockFilePath"
        ]
    except (OSError, UnicodeError, ElementTree.ParseError):
        raise EntrypointError("ASESE002", "A fixed solution project file is unavailable or malformed.") from None

    if not configured_paths:
        return project.with_name("packages.lock.json")
    if len(configured_paths) != 1 or configured_paths[0] is None:
        raise EntrypointError("ASESE002", "A fixed solution project has an ambiguous dependency lock path.")

    configured_path = configured_paths[0].strip()
    relative_path = configured_path.replace(SDK_RUNTIME_IDENTIFIER_PROPERTY, SDK_RUNTIME_IDENTIFIER)
    if (
        not relative_path
        or "$" in relative_path
        or "\\" in relative_path
        or ":" in relative_path
        or relative_path.startswith("/")
        or any(part in {"", ".", ".."} for part in relative_path.split("/"))
    ):
        raise EntrypointError("ASESE002", "A fixed solution project has an unsafe or unsupported dependency lock path.")
    return project.parent.joinpath(*PurePosixPath(relative_path).parts)


def _validate_solution_locks(staged_root: Path) -> None:
    solution = staged_root / SOLUTION_RELATIVE_PATH
    try:
        document = ElementTree.parse(solution)
        project_paths = [element.attrib.get("Path") for element in document.iter() if element.tag == "Project"]
        if not project_paths:
            raise EntrypointError("ASESE002", "The fixed solution contains no projects.")
        for relative in project_paths:
            if not relative or Path(relative).is_absolute() or ".." in Path(relative).parts:
                raise EntrypointError("ASESE002", "The fixed solution contains an unsafe project path.")
            project = staged_root / relative
            project_info = project.lstat()
            if not stat.S_ISREG(project_info.st_mode):
                raise EntrypointError("ASESE002", "A fixed solution project is unavailable.")
            lock_path = _project_lock_path(project)
            try:
                lock_info = lock_path.lstat()
            except OSError:
                raise EntrypointError("ASESE002", "A fixed solution project has no regular dependency lock file.") from None
            if not stat.S_ISREG(lock_info.st_mode):
                raise EntrypointError("ASESE002", "A fixed solution project has no regular dependency lock file.")
            lock_value = json.loads(lock_path.read_text(encoding="utf-8"))
            if not isinstance(lock_value, dict) or not isinstance(lock_value.get("dependencies"), dict):
                raise EntrypointError("ASESE002", "A fixed solution dependency lock file is malformed.")
    except EntrypointError:
        raise
    except (OSError, UnicodeError, ValueError, ElementTree.ParseError):
        raise EntrypointError("ASESE002", "The fixed solution or one of its dependency locks is unavailable or malformed.") from None


def _write_offline_config(path: Path) -> None:
    content = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<configuration><packageSources><clear/>"
        f'<add key="locked-dependencies" value="{DEPENDENCY_SOURCE}"/>'
        "</packageSources></configuration>\n"
    ).encode("utf-8")
    try:
        descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
    except OSError:
        raise EntrypointError("ASESE003", "The fixed offline package-source configuration could not be written.") from None


def _step(name: str, result: ProcessResult) -> StepResult:
    return StepResult(
        name=name,
        exit_code=result.exit_code,
        output_bytes=len(result.stdout) + len(result.stderr),
        stdout_sha256=hashlib.sha256(result.stdout).hexdigest(),
        stderr_sha256=hashlib.sha256(result.stderr).hexdigest(),
    )


def _require_success(name: str, result: ProcessResult) -> StepResult:
    step = _step(name, result)
    if result.exit_code != 0:
        raise StepFailure(step)
    return step


class StepFailure(EntrypointError):
    def __init__(self, step: StepResult) -> None:
        super().__init__("ASESE008", "A fixed subject command failed.")
        self.step = step


def _dotnet_version(
    *,
    runner: ProcessRunner,
    working_directory: Path,
    environment: Mapping[str, str],
    deadline: float,
    output_budget: OutputBudget,
) -> list[StepResult]:
    version_result = runner(
        ["dotnet", "--version"],
        working_directory=working_directory,
        environment=environment,
        deadline=deadline,
        output_budget=output_budget,
    )
    version_step = _require_success("dotnet-sdk-version", version_result)
    version_text = version_result.stdout.decode("ascii", errors="strict").strip()
    version_parts = version_text.split(".")
    if not version_parts or not version_parts[0].isdecimal() or int(version_parts[0]) != DOTNET_MAJOR_VERSION:
        raise EntrypointError("ASESE004", "The required .NET 10 SDK is unavailable.")

    runtime_result = runner(
        ["dotnet", "--list-runtimes"],
        working_directory=working_directory,
        environment=environment,
        deadline=deadline,
        output_budget=output_budget,
    )
    runtime_step = _require_success("dotnet-runtime-list", runtime_result)
    if not any(line.startswith("Microsoft.NETCore.App 10.") for line in runtime_result.stdout.decode("ascii", errors="strict").splitlines()):
        raise EntrypointError("ASESE004", "The required .NET 10 runtime is unavailable.")
    return [version_step, runtime_step]


def _coverage_commands(staged_root: Path, scratch_root: Path, diff_file: Path) -> tuple[list[str], list[str]]:
    cli_project = str(staged_root / CLI_RELATIVE_PATH)
    solution = str(staged_root / SOLUTION_RELATIVE_PATH)
    coverage_output = str(scratch_root / OUTPUT_RELATIVE_PATH / "coverage-merged")
    gate_output = str(scratch_root / OUTPUT_RELATIVE_PATH / "coverage-gate")
    coverage_run = [
        "dotnet",
        "run",
        "--project",
        cli_project,
        "--configuration",
        "Debug",
        "--no-restore",
        "--",
        "coverage",
        "run",
        "--solution",
        solution,
        "--output",
        coverage_output,
        "--configuration",
        "Debug",
        "--parallelism",
        "1",
        "--exclusive-test-project",
        "ForgeTrust.AppSurface.Config.Tests.csproj",
        "--exclusive-test-project",
        "AuthAspNetCoreDevAuthExample.Tests.csproj",
        "--exclusive-test-project",
        "AuthWebRazorWireProofExample.Tests.csproj",
        "--exclusive-test-project",
        "ForgeTrust.AppSurface.Durable.PostgreSql.Tests.csproj",
        "--exclusive-test-project",
        "ForgeTrust.AppSurface.DurableExternalActivationExample.Tests.csproj",
        "--exclusive-test-project",
        "ForgeTrust.RazorWire.Cli.Tests.csproj",
        "--exclusive-test-project",
        "ForgeTrust.AppSurface.Web.Tailwind.Tests.csproj",
        "--test-results",
        "junit",
        "--slow-test-diagnostics",
        "--no-restore",
    ]
    coverage_gate = [
        "dotnet",
        "run",
        "--project",
        cli_project,
        "--configuration",
        "Debug",
        "--no-restore",
        "--",
        "coverage",
        "gate",
        "--coverage",
        str(Path(coverage_output) / "coverage.cobertura.xml"),
        "--min-line",
        "95",
        "--min-branch",
        "85",
        "--min-patch-line",
        "95",
        "--min-patch-branch",
        "85",
        "--patch-line-mode",
        "codecov",
        "--diff-file",
        str(diff_file),
        "--output",
        gate_output,
    ]
    return coverage_run, coverage_gate


def _make_result(
    status: str,
    steps: Sequence[StepResult],
    *,
    artifacts: Sequence[Mapping[str, object]] | None = None,
    diagnostic: tuple[str, str] | None = None,
) -> bytes:
    value: dict[str, object] = {
        "claimEligible": False,
        "profileId": FIXED_PROFILE_ID,
        "schemaVersion": 1,
        "status": status,
        "steps": [
            {
                "exitCode": step.exit_code,
                "name": step.name,
                "outputBytes": step.output_bytes,
                "stderrSha256": step.stderr_sha256,
                "stdoutSha256": step.stdout_sha256,
            }
            for step in steps
        ],
    }
    if status == "completed":
        value["artifacts"] = _validate_artifact_index(artifacts)
    elif artifacts is not None:
        raise EntrypointError("ASESE013", "A failed subject result cannot claim successful coverage artifacts.")
    if diagnostic is not None:
        value["diagnostic"] = {"code": diagnostic[0], "message": diagnostic[1]}
    encoded = (json.dumps(value, ensure_ascii=True, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii")
    if len(encoded) > MAX_RESULT_BYTES:
        raise EntrypointError("ASESE009", "The canonical subject result exceeds its fixed byte limit.")
    return encoded


def _validate_artifact_index(value: object) -> list[dict[str, object]]:
    if not isinstance(value, (list, tuple)) or len(value) != len(REQUIRED_ARTIFACTS):
        raise EntrypointError("ASESE013", "The completed coverage run omitted its fixed artifact index.")
    normalized: list[dict[str, object]] = []
    for item, (logical_name, relative_path, maximum_bytes) in zip(value, REQUIRED_ARTIFACTS, strict=True):
        if (
            not isinstance(item, Mapping)
            or set(item) != {"logicalName", "relativePath", "byteCount", "sha256"}
            or item.get("logicalName") != logical_name
            or item.get("relativePath") != relative_path
            or type(item.get("byteCount")) is not int
            or not 0 < item["byteCount"] <= maximum_bytes
            or not isinstance(item.get("sha256"), str)
            or SHA256_PATTERN.fullmatch(item["sha256"]) is None
        ):
            raise EntrypointError("ASESE013", "The completed coverage run has an invalid fixed artifact index.")
        normalized.append(
            {
                "logicalName": logical_name,
                "relativePath": relative_path,
                "byteCount": item["byteCount"],
                "sha256": item["sha256"],
            }
        )
    return normalized


def _hash_required_artifact(
    scratch_root: Path,
    relative_path: str,
    maximum_bytes: int,
    *,
    deadline: float,
) -> tuple[int, str]:
    parts = PurePosixPath(relative_path).parts
    if not parts or PurePosixPath(relative_path).is_absolute() or any(part in {".", ".."} for part in parts):
        raise EntrypointError("ASESE013", "A required coverage artifact path is unsafe.")
    directory_descriptor = -1
    file_descriptor = -1
    try:
        directory_descriptor = os.open(
            scratch_root,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        for part in parts[:-1]:
            child = os.open(
                part,
                os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0),
                dir_fd=directory_descriptor,
            )
            os.close(directory_descriptor)
            directory_descriptor = child
        name = parts[-1]
        before = os.stat(name, dir_fd=directory_descriptor, follow_symlinks=False)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or not 0 < before.st_size <= maximum_bytes:
            raise EntrypointError("ASESE013", "A required coverage artifact is not a bounded single-link regular file.")
        file_descriptor = os.open(
            name,
            os.O_RDONLY | getattr(os, "O_CLOEXEC", 0) | getattr(os, "O_NOFOLLOW", 0),
            dir_fd=directory_descriptor,
        )
        opened = os.fstat(file_descriptor)
        if (
            not stat.S_ISREG(opened.st_mode)
            or opened.st_nlink != 1
            or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino)
            or not 0 < opened.st_size <= maximum_bytes
        ):
            raise EntrypointError("ASESE013", "A required coverage artifact changed or exceeds its byte limit.")
        digest = hashlib.sha256()
        total = 0
        while True:
            _check_deadline(deadline)
            chunk = os.read(file_descriptor, min(COPY_CHUNK_BYTES, maximum_bytes + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            if total > maximum_bytes:
                raise EntrypointError("ASESE013", "A required coverage artifact exceeds its byte limit.")
            digest.update(chunk)
        after = os.fstat(file_descriptor)
        named_after = os.stat(name, dir_fd=directory_descriptor, follow_symlinks=False)
        opened_identity = (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns, opened.st_ctime_ns)
        if (
            total != opened.st_size
            or (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns) != opened_identity
            or (named_after.st_dev, named_after.st_ino, named_after.st_size, named_after.st_mtime_ns, named_after.st_ctime_ns)
            != opened_identity
            or not stat.S_ISREG(named_after.st_mode)
            or named_after.st_nlink != 1
        ):
            raise EntrypointError("ASESE013", "A required coverage artifact changed while it was being hashed.")
        return total, digest.hexdigest()
    except EntrypointError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise EntrypointError("ASESE013", "A required coverage artifact is unavailable or unsafe.") from None
    finally:
        for descriptor in (file_descriptor, directory_descriptor):
            if descriptor >= 0:
                try:
                    os.close(descriptor)
                except OSError:
                    pass


def _build_artifact_index(scratch_root: Path, *, deadline: float) -> list[dict[str, object]]:
    artifacts: list[dict[str, object]] = []
    for logical_name, relative_path, maximum_bytes in REQUIRED_ARTIFACTS:
        byte_count, digest = _hash_required_artifact(
            scratch_root,
            relative_path,
            maximum_bytes,
            deadline=deadline,
        )
        artifacts.append(
            {
                "logicalName": logical_name,
                "relativePath": relative_path,
                "byteCount": byte_count,
                "sha256": digest,
            }
        )
    return _validate_artifact_index(artifacts)


def _write_result(scratch_root: Path, content: bytes) -> None:
    destination = scratch_root / RESULT_RELATIVE_PATH
    try:
        descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(descriptor, "wb") as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
    except OSError:
        raise EntrypointError("ASESE009", "The canonical subject result could not be written safely.") from None


def _result_for_transport(scratch_root: Path) -> bytes:
    result_path = scratch_root / RESULT_RELATIVE_PATH
    descriptor = -1
    try:
        descriptor = os.open(result_path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
        metadata = os.fstat(descriptor)
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_nlink != 1 or metadata.st_size > MAX_RESULT_BYTES:
            raise EntrypointError("ASESE009", "The canonical subject result is not a bounded regular file.")
        with os.fdopen(descriptor, "rb") as result_file:
            descriptor = -1
            content = result_file.read(MAX_RESULT_BYTES + 1)
        if len(content) != metadata.st_size or not content or len(content) > MAX_RESULT_BYTES:
            raise EntrypointError("ASESE009", "The canonical subject result is not bounded.")
        return content
    except EntrypointError:
        raise
    except OSError:
        raise EntrypointError("ASESE009", "The canonical subject result could not be read safely.") from None
    finally:
        if descriptor >= 0:
            try:
                os.close(descriptor)
            except OSError:
                raise EntrypointError("ASESE009", "The canonical subject result could not be closed safely.") from None


def execute(
    invocation: Invocation,
    *,
    mountinfo_text: str | None = None,
    effective_uid: int | None = None,
    runner: ProcessRunner = run_bounded_process,
) -> int:
    scratch = invocation.scratch_root
    steps: list[StepResult] = []
    try:
        mounts_text = Path("/proc/self/mountinfo").read_text(encoding="utf-8") if mountinfo_text is None else mountinfo_text
        uid = os.geteuid() if effective_uid is None else effective_uid
        validate_mounts(
            invocation.subject_root,
            scratch,
            invocation.dependency_source,
            invocation.diff_file,
            invocation.diff_sha256,
            mountinfo_text=mounts_text,
            effective_uid=uid,
        )
    except EntrypointError:
        raise
    except OSError:
        raise EntrypointError("ASESE001", "The container mount table is unavailable.") from None

    deadline = time.monotonic() + MAX_EXECUTION_SECONDS
    output_budget = OutputBudget(MAX_OUTPUT_BYTES)
    result_path = scratch / RESULT_RELATIVE_PATH
    try:
        _validate_dependency_feed(invocation.dependency_source)
        environment = _process_environment(scratch)
        steps.extend(
            _dotnet_version(
                runner=runner,
                working_directory=scratch,
                environment=environment,
                deadline=deadline,
                output_budget=output_budget,
            )
        )
        staged_root = scratch / STAGED_SUBJECT_RELATIVE_PATH
        _copy_subject_tree(invocation.subject_root, staged_root, deadline=deadline)
        _validate_solution_locks(staged_root)
        _write_offline_config(scratch / RESTORE_CONFIG_RELATIVE_PATH)
        try:
            user_config_directory = Path(environment["HOME"]) / ".nuget" / "NuGet"
            user_config_directory.mkdir(parents=True, mode=0o700)
        except OSError:
            raise EntrypointError("ASESE003", "The private NuGet SDK resolver configuration directory could not be created.") from None
        # Project SDK resolution happens during MSBuild evaluation, before the
        # restore command applies --configfile. Give that resolver the same
        # fixed offline feed; no subject NuGet.Config may override it.
        _write_offline_config(user_config_directory / "NuGet.Config")

        restore_result = runner(
            [
                "dotnet",
                "restore",
                str(staged_root / SOLUTION_RELATIVE_PATH),
                "--locked-mode",
                "--disable-parallel",
                "--configfile",
                str(scratch / RESTORE_CONFIG_RELATIVE_PATH),
                "--source",
                str(invocation.dependency_source),
                "--packages",
                environment["NUGET_PACKAGES"],
            ],
            working_directory=staged_root,
            environment=environment,
            deadline=deadline,
            output_budget=output_budget,
        )
        steps.append(_require_success("offline-locked-restore", restore_result))

        unsupported = [
            relative
            for relative in OFFLINE_UNSUPPORTED_TEST_PROJECTS
            if (staged_root / relative).is_file()
        ]
        if unsupported:
            raise EntrypointError(
                "ASESE010",
                "The fixed full-solution coverage profile includes container-dependent test projects "
                "without a declared service or network envelope; coverage execution is unavailable.",
            )

        (scratch / OUTPUT_RELATIVE_PATH).mkdir(mode=0o700)
        coverage_run, coverage_gate = _coverage_commands(staged_root, scratch, invocation.diff_file)
        coverage_result = runner(
            coverage_run,
            working_directory=staged_root,
            environment=environment,
            deadline=deadline,
            output_budget=output_budget,
        )
        steps.append(_require_success("coverage-run", coverage_result))
        gate_result = runner(
            coverage_gate,
            working_directory=staged_root,
            environment=environment,
            deadline=deadline,
            output_budget=output_budget,
        )
        steps.append(_require_success("coverage-gate", gate_result))
        _check_deadline(deadline)
        artifacts = _build_artifact_index(scratch, deadline=deadline)
        _write_result(scratch, _make_result("completed", steps, artifacts=artifacts))
        return 0
    except StepFailure as exc:
        steps.append(exc.step)
        _write_result(scratch, _make_result("failed", steps, diagnostic=(exc.code, exc.message)))
        return 2
    except EntrypointError as exc:
        _write_result(scratch, _make_result("failed", steps, diagnostic=(exc.code, exc.message)))
        return 2


def main(argv: Sequence[str] | None = None) -> int:
    try:
        invocation = parse_invocation(argv)
        exit_code = execute(invocation)
        content = _result_for_transport(invocation.scratch_root)
        try:
            sys.stdout.buffer.write(content)
            sys.stdout.buffer.flush()
        except OSError:
            raise EntrypointError("ASESE009", "The canonical subject result could not be streamed safely.") from None
        return exit_code
    except EntrypointError as exc:
        print(f"{exc.code}: {exc.message}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
