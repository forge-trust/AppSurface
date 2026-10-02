#!/usr/bin/env python3
"""Build a verified, flat offline NuGet feed from a trusted base checkout.

This build-time utility parses one trusted repository-relative ``.slnx`` file,
then resolves the exact ``packages*.lock.json`` file each listed project uses
for the fixed ``linux-x64`` subject runtime. This excludes unrelated fixtures
and lock variants for other operating systems. It reads exact ``.nupkg`` files
in the supplied NuGet global-packages cache and
invokes the fixed ``dotnet nuget verify --all`` command on each copied archive
to obtain NuGet's authoritative ``Content hash:`` value. It never invokes
Git, a shell, or any build/restore command. The checkout must be a trusted
base revision; do not point it at a pull-request/head checkout. Every selected
path component and file must be physical (not a symbolic link).

The accepted lock format is NuGet's JSON lock format with an integer version
of 1 or 2 and a ``dependencies`` object. ``Direct``, ``Transitive``, and
``CentralTransitive`` entries must have safe normalized package IDs/versions
and a canonical base64 SHA-512 ``contentHash``. ``Project`` entries are
validated as project references and do not become feed packages. Repeated
case-insensitive package ID/version pairs are emitted once; conflicting
content hashes fail closed. Package archives are located at the exact NuGet
global-packages layout ``<id-lower>/<version-lower>/<id-lower>.<version-lower>.nupkg``.

The destination must not exist. It is created with exclusive directory/file
creation and contains only flat ``<id>.<version>.nupkg`` files. The default
hard limits are 4 MiB per solution, 10,000 solution projects, 1 MiB per project
file, 32 MiB total project XML, 100,000 entries in selected project directories,
2,048 lock files, 4 MiB per lock, 32 MiB of
lock JSON, 10,000 distinct packages, 512 MiB per archive, 4 GiB total archive
bytes, 250,000 inspected cache entries, 30 seconds per NuGet verification, 64
KiB verifier output, and 30 minutes total verification time. Callers of the
Python API may only tighten those limits. Tests may inject a content-hash
verifier; the CLI exposes no verifier override. A failed build removes the
private partial output when it can do so safely.

CLI::

    python3 scripts/evidence-gate-build-offline-feed.py \
      --checkout /trusted/base/checkout \
      --solution ForgeTrust.AppSurface.slnx \
      --global-packages /runner/cache/nuget/packages \
      --output /runner/temp/locked-nuget-feed

Python API::

    result = build_offline_feed(checkout, solution_relative_path, global_packages, output_directory)

Use the result counts for build logs only. A successful feed proves that its
bytes have the NuGet-reported content hashes selected by the checked-in locks;
this utility does not authenticate the checkout, image, or later feed use.
The digest-pinned image build must call it only with a trusted base checkout
and must keep the resulting feed inside the reviewed subject image. NuGet
verification is a fixed, bounded subprocess: no user command, package source,
build, or restore is run.
"""

from __future__ import annotations

import argparse
import base64
from dataclasses import dataclass
import json
import os
from pathlib import Path
from pathlib import PurePosixPath
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Callable, Sequence
import xml.etree.ElementTree as ElementTree


COPY_CHUNK_BYTES = 1024 * 1024
DOTNET_EXECUTABLES = (Path("/usr/share/dotnet/dotnet"), Path("/usr/local/share/dotnet/dotnet"))
DOTNET_PATH = "/usr/local/bin:/usr/bin:/bin:/usr/share/dotnet"
MAX_VERIFIER_OUTPUT_BYTES = 64 * 1024
SDK_RUNTIME_IDENTIFIER = "linux-x64"
SDK_RUNTIME_IDENTIFIER_PROPERTY = "$(NETCoreSdkRuntimeIdentifier)"
PACKAGE_ENTRY_TYPES = frozenset({"Direct", "Transitive", "CentralTransitive"})
PACKAGE_ID_PATTERN = re.compile(r"[A-Za-z0-9][A-Za-z0-9._-]{0,99}\Z")
PACKAGE_VERSION_PATTERN = re.compile(
    r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
    r"(?:\.(?:0|[1-9][0-9]*))?"
    r"(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\Z"
)


class OfflineFeedError(Exception):
    """A bounded, path-free diagnostic for an invalid offline-feed build."""


@dataclass(frozen=True)
class FeedLimits:
    """Hard ceilings; a build may lower but never raise any ceiling."""

    max_solution_bytes: int = 4 * 1024 * 1024
    max_projects: int = 10_000
    max_project_file_bytes: int = 1024 * 1024
    max_total_project_bytes: int = 32 * 1024 * 1024
    max_selected_directory_entries: int = 100_000
    max_lock_files: int = 2_048
    max_lock_file_bytes: int = 4 * 1024 * 1024
    max_total_lock_bytes: int = 32 * 1024 * 1024
    max_packages: int = 10_000
    max_package_bytes: int = 512 * 1024 * 1024
    max_total_package_bytes: int = 4 * 1024 * 1024 * 1024
    max_cache_entries: int = 250_000
    max_verifier_seconds_per_archive: int = 30
    max_total_verifier_seconds: int = 30 * 60
    max_verifier_output_bytes: int = MAX_VERIFIER_OUTPUT_BYTES


DEFAULT_LIMITS = FeedLimits()


@dataclass(frozen=True)
class FeedBuildResult:
    """Counts from a completed feed; no source or cache paths are exposed."""

    lock_file_count: int
    package_count: int
    package_bytes: int


@dataclass(frozen=True)
class _LockedPackage:
    package_id: str
    version: str
    content_hash: bytes

    @property
    def identity(self) -> tuple[str, str]:
        return self.package_id.lower(), self.version.lower()

    @property
    def archive_name(self) -> str:
        package_id, version = self.identity
        return f"{package_id}.{version}.nupkg"


@dataclass(frozen=True)
class _Archive:
    package: _LockedPackage
    source: Path
    size: int


def _fail(message: str) -> OfflineFeedError:
    return OfflineFeedError(message)


def _validate_limits(limits: FeedLimits) -> None:
    if not isinstance(limits, FeedLimits):
        raise _fail("Feed limits must use the FeedLimits contract.")
    for name, hard_maximum in vars(DEFAULT_LIMITS).items():
        value = getattr(limits, name)
        if type(value) is not int or value < 1 or value > hard_maximum:
            raise _fail("A feed limit is outside its reviewed hard ceiling.")


def _path_argument(value: str | os.PathLike[str], name: str) -> Path:
    try:
        raw = os.fspath(value)
    except TypeError:
        raise _fail(f"{name} must be an absolute physical path.") from None
    if not isinstance(raw, str) or not raw or "\x00" in raw or not os.path.isabs(raw):
        raise _fail(f"{name} must be an absolute physical path.")
    if any(component in {".", ".."} for component in raw.split(os.sep)):
        raise _fail(f"{name} contains a dot path component.")
    return Path(raw)


def _physical_directory(value: str | os.PathLike[str], name: str) -> Path:
    path = _path_argument(value, name)
    try:
        current = Path(path.anchor)
        if stat.S_ISLNK(current.lstat().st_mode):
            raise _fail(f"{name} traverses a symbolic link.")
        for index, component in enumerate(path.parts[1:], start=1):
            current = current / component
            item_stat = current.lstat()
            if stat.S_ISLNK(item_stat.st_mode):
                raise _fail(f"{name} traverses a symbolic link.")
            if index < len(path.parts) - 1 and not stat.S_ISDIR(item_stat.st_mode):
                raise _fail(f"{name} contains a non-directory path component.")
        final_stat = path.lstat()
        if not stat.S_ISDIR(final_stat.st_mode):
            raise _fail(f"{name} is not a physical directory.")
        if path.resolve(strict=True) != path:
            raise _fail(f"{name} is not a canonical physical path.")
    except OfflineFeedError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise _fail(f"{name} is unavailable or unsafe.") from None
    return path


def _paths_overlap(first: Path, second: Path) -> bool:
    return first == second or first in second.parents or second in first.parents


def _new_output_location(
    value: str | os.PathLike[str], checkout: Path, cache: Path
) -> tuple[Path, Path]:
    output = _path_argument(value, "The output directory")
    if output == Path(output.anchor) or output.name in {"", ".", ".."}:
        raise _fail("The output directory must be a new child of a physical parent directory.")
    parent = _physical_directory(output.parent, "The output parent")
    output = parent / output.name
    if _paths_overlap(output, checkout) or _paths_overlap(output, cache):
        raise _fail("The output directory must be separate from the checkout and global cache.")
    try:
        output.lstat()
    except FileNotFoundError:
        return parent, output
    except OSError:
        raise _fail("The output location cannot be inspected safely.") from None
    raise _fail("The output directory must not already exist.")


def _is_lock_filename(name: str) -> bool:
    return name.startswith("packages") and name.endswith(".lock.json")


def _selected_project_lock_name(project_bytes: bytes) -> str:
    if b"<!DOCTYPE" in project_bytes.upper() or b"<!ENTITY" in project_bytes.upper():
        raise _fail("A selected project contains unsupported XML declarations.")
    try:
        document = ElementTree.fromstring(project_bytes)
    except (ElementTree.ParseError, RecursionError, ValueError):
        raise _fail("A selected project is malformed XML.") from None
    if document.tag.rsplit("}", 1)[-1] != "Project":
        raise _fail("A selected project has an unsupported root element.")
    configured_paths = [
        element.text
        for element in document.iter()
        if element.tag.rsplit("}", 1)[-1] == "NuGetLockFilePath"
    ]
    if not configured_paths:
        return "packages.lock.json"
    if len(configured_paths) != 1 or configured_paths[0] is None:
        raise _fail("A selected project has an ambiguous NuGet lock path.")
    configured = configured_paths[0].strip()
    selected = configured.replace(SDK_RUNTIME_IDENTIFIER_PROPERTY, SDK_RUNTIME_IDENTIFIER)
    if "$" in selected:
        raise _fail("A selected project has an unsupported NuGet lock path expression.")
    relative = _relative_checkout_path(selected, "A selected project lock path")
    if len(relative.parts) != 1 or not _is_lock_filename(relative.name):
        raise _fail("A selected project lock must be a packages*.lock.json file beside the project.")
    return relative.name


def _relative_checkout_path(value: Any, name: str, required_suffix: str | None = None) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value or "\\" in value:
        raise _fail(f"{name} must be a safe repository-relative path.")
    posix_path = PurePosixPath(value)
    if posix_path.is_absolute() or any(part in {"", ".", ".."} for part in value.split("/")):
        raise _fail(f"{name} must be a safe repository-relative path.")
    if ":" in value.split("/", 1)[0] or any(ord(character) < 32 or ord(character) == 127 for character in value):
        raise _fail(f"{name} contains an unsafe path character.")
    if required_suffix is not None and posix_path.suffix != required_suffix:
        raise _fail(f"{name} has an unsupported file type.")
    return Path(*posix_path.parts)


def _physical_checkout_file(
    checkout: Path,
    relative_path: str | Path,
    name: str,
    maximum_bytes: int,
) -> tuple[Path, int]:
    relative = _relative_checkout_path(os.fspath(relative_path), name)
    path = checkout / relative
    try:
        current = checkout
        for index, component in enumerate(relative.parts):
            current = current / component
            item_stat = current.lstat()
            if stat.S_ISLNK(item_stat.st_mode):
                raise _fail(f"{name} traverses a symbolic link.")
            if index < len(relative.parts) - 1:
                if not stat.S_ISDIR(item_stat.st_mode):
                    raise _fail(f"{name} contains a non-directory path component.")
            elif not stat.S_ISREG(item_stat.st_mode):
                raise _fail(f"{name} is not a regular physical file.")
        item_stat = path.lstat()
        if item_stat.st_size < 1 or item_stat.st_size > maximum_bytes:
            raise _fail(f"{name} is empty or exceeds its byte limit.")
        if path.resolve(strict=True) != path or path.relative_to(checkout) != relative:
            raise _fail(f"{name} is not a canonical file below the trusted checkout.")
    except OfflineFeedError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise _fail(f"{name} is unavailable or unsafe.") from None
    return path, item_stat.st_size


def _read_physical_file(path: Path, expected_size: int, maximum_bytes: int, name: str) -> bytes:
    flags = os.O_RDONLY | _nofollow_flag()
    try:
        descriptor = os.open(path, flags)
        with os.fdopen(descriptor, "rb") as source_file:
            item_stat = os.fstat(source_file.fileno())
            if not stat.S_ISREG(item_stat.st_mode) or item_stat.st_size != expected_size:
                raise _fail(f"{name} changed or is not a regular physical file.")
            if item_stat.st_size > maximum_bytes:
                raise _fail(f"{name} exceeds its byte limit.")
            raw = source_file.read(maximum_bytes + 1)
    except OfflineFeedError:
        raise
    except OSError:
        raise _fail(f"{name} cannot be opened safely.") from None
    if len(raw) != expected_size or len(raw) > maximum_bytes:
        raise _fail(f"{name} changed while being read or exceeds its byte limit.")
    return raw


def _select_lock_files(
    checkout: Path, solution_relative_path: str | os.PathLike[str], limits: FeedLimits
) -> list[tuple[Path, int]]:
    solution_relative = _relative_checkout_path(os.fspath(solution_relative_path), "The solution path", ".slnx")
    solution_path, solution_size = _physical_checkout_file(
        checkout,
        solution_relative,
        "The trusted solution",
        limits.max_solution_bytes,
    )
    solution_bytes = _read_physical_file(
        solution_path,
        solution_size,
        limits.max_solution_bytes,
        "The trusted solution",
    )
    if b"<!DOCTYPE" in solution_bytes.upper() or b"<!ENTITY" in solution_bytes.upper():
        raise _fail("The trusted solution contains unsupported XML declarations.")
    try:
        document = ElementTree.fromstring(solution_bytes)
    except (ElementTree.ParseError, RecursionError, ValueError):
        raise _fail("The trusted solution is malformed XML.") from None
    if document.tag.rsplit("}", 1)[-1] != "Solution":
        raise _fail("The trusted solution has an unsupported root element.")

    project_paths: list[str] = []
    for element in document.iter():
        if element.tag.rsplit("}", 1)[-1] != "Project":
            continue
        project_path = element.attrib.get("Path")
        if project_path is None:
            raise _fail("The trusted solution contains a project without a Path.")
        project_paths.append(project_path)
        if len(project_paths) > limits.max_projects:
            raise _fail("The trusted solution exceeds the project-count limit.")
    if not project_paths:
        raise _fail("The trusted solution contains no projects.")
    if len(set(project_paths)) != len(project_paths):
        raise _fail("The trusted solution lists a project more than once.")

    selected_directories: dict[Path, set[str]] = {}
    total_project_bytes = 0
    for project_relative in project_paths:
        project_path, project_size = _physical_checkout_file(
            checkout,
            project_relative,
            "A selected project",
            limits.max_project_file_bytes,
        )
        total_project_bytes += project_size
        if total_project_bytes > limits.max_total_project_bytes:
            raise _fail("Selected project XML exceeds its total byte limit.")
        project_bytes = _read_physical_file(
            project_path,
            project_size,
            limits.max_project_file_bytes,
            "A selected project",
        )
        selected_directories.setdefault(project_path.parent, set()).add(
            _selected_project_lock_name(project_bytes)
        )

    lock_files: dict[Path, int] = {}
    selected_entry_count = 0
    for directory, selected_names in sorted(selected_directories.items()):
        found_names: set[str] = set()
        try:
            with os.scandir(directory) as entries:
                for entry in entries:
                    selected_entry_count += 1
                    if selected_entry_count > limits.max_selected_directory_entries:
                        raise _fail("Selected project directories exceed the entry-count limit.")
                    if entry.name not in selected_names:
                        continue
                    item_stat = entry.stat(follow_symlinks=False)
                    if stat.S_ISLNK(item_stat.st_mode):
                        raise _fail("A selected lock file is a symbolic link.")
                    if not stat.S_ISREG(item_stat.st_mode):
                        raise _fail("A selected lock input is not a regular file.")
                    lock_path = Path(entry.path)
                    if lock_path.resolve(strict=True) != lock_path:
                        raise _fail("A selected lock file is not physically canonical.")
                    if item_stat.st_size < 1 or item_stat.st_size > limits.max_lock_file_bytes:
                        raise _fail("A selected lock file is empty or exceeds its byte limit.")
                    lock_files[lock_path] = item_stat.st_size
                    if len(lock_files) > limits.max_lock_files:
                        raise _fail("Selected projects exceed the lock-file-count limit.")
                    found_names.add(entry.name)
        except OfflineFeedError:
            raise
        except (OSError, RuntimeError, ValueError):
            raise _fail("A selected project directory cannot be inspected safely.") from None

        if found_names != selected_names:
            raise _fail("A selected project has no regular Linux dependency lock file.")

    if not lock_files:
        raise _fail("The trusted solution's project directories contain no packages*.lock.json inputs.")
    return sorted(lock_files.items(), key=lambda item: item[0].relative_to(checkout).as_posix())


def _read_lock_file(path: Path, expected_size: int, limits: FeedLimits) -> bytes:
    return _read_physical_file(path, expected_size, limits.max_lock_file_bytes, "A selected lock file")


def _unique_json_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def _reject_json_constant(_value: str) -> None:
    raise ValueError("non-standard JSON constant")


def _validate_package_id(package_id: Any) -> str:
    if not isinstance(package_id, str) or PACKAGE_ID_PATTERN.fullmatch(package_id) is None:
        raise _fail("A lock entry contains an unsafe or malformed package ID.")
    return package_id


def _validate_version(version: Any) -> str:
    if not isinstance(version, str) or len(version) > 128 or PACKAGE_VERSION_PATTERN.fullmatch(version) is None:
        raise _fail("A lock entry contains an unsafe or non-normalized package version.")
    if version.endswith(".0") and version.split("-", 1)[0].count(".") == 3:
        raise _fail("A lock entry contains a non-normalized four-part package version.")
    return version


def _decode_content_hash(value: Any) -> bytes:
    if not isinstance(value, str):
        raise _fail("A package lock entry is missing its SHA-512 contentHash.")
    try:
        digest = base64.b64decode(value, validate=True)
    except (ValueError, base64.binascii.Error):
        raise _fail("A package lock entry has a malformed base64 contentHash.") from None
    if len(digest) != 64 or base64.b64encode(digest).decode("ascii") != value:
        raise _fail("A package lock entry has a non-canonical SHA-512 contentHash.")
    return digest


def _parse_lock(raw: bytes) -> list[_LockedPackage]:
    try:
        document = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=_unique_json_object,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, ValueError, RecursionError):
        raise _fail("A lock file is malformed UTF-8 JSON or contains duplicate properties.") from None
    if not isinstance(document, dict) or type(document.get("version")) is not int or document["version"] not in {1, 2}:
        raise _fail("A lock file has an unsupported NuGet lock format version.")
    dependencies = document.get("dependencies")
    if not isinstance(dependencies, dict):
        raise _fail("A lock file is missing its dependencies object.")

    packages: list[_LockedPackage] = []
    for target, entries in dependencies.items():
        if not isinstance(target, str) or not target or len(target) > 512 or not isinstance(entries, dict):
            raise _fail("A lock file contains a malformed target-framework dependency group.")
        for package_id_value, entry in entries.items():
            package_id = _validate_package_id(package_id_value)
            if not isinstance(entry, dict):
                raise _fail("A lock file contains a malformed dependency entry.")
            entry_type = entry.get("type")
            if entry_type == "Project":
                project_dependencies = entry.get("dependencies", {})
                if not isinstance(project_dependencies, dict):
                    raise _fail("A lock file contains a malformed project-reference entry.")
                for dependency_id, requested in project_dependencies.items():
                    _validate_package_id(dependency_id)
                    if not isinstance(requested, str) or len(requested) > 512:
                        raise _fail("A lock file contains a malformed project-reference constraint.")
                continue
            if entry_type not in PACKAGE_ENTRY_TYPES:
                raise _fail("A lock file contains an unsupported dependency entry type.")
            version = _validate_version(entry.get("resolved"))
            content_hash = _decode_content_hash(entry.get("contentHash"))
            packages.append(_LockedPackage(package_id.lower(), version.lower(), content_hash))
    return packages


def _collect_locked_packages(
    lock_files: list[tuple[Path, int]], limits: FeedLimits
) -> dict[tuple[str, str], _LockedPackage]:
    total_lock_bytes = 0
    packages: dict[tuple[str, str], _LockedPackage] = {}
    for path, size in lock_files:
        total_lock_bytes += size
        if total_lock_bytes > limits.max_total_lock_bytes:
            raise _fail("Lock files exceed the total lock-byte limit.")
        for package in _parse_lock(_read_lock_file(path, size, limits)):
            previous = packages.get(package.identity)
            if previous is not None and previous.content_hash != package.content_hash:
                raise _fail("Lock files disagree on a package ID/version contentHash.")
            packages[package.identity] = previous or package
            if len(packages) > limits.max_packages:
                raise _fail("Lock files exceed the distinct-package-count limit.")
    if not packages:
        raise _fail("The lock files contain no NuGet package archives.")
    return packages


class _CacheEntryBudget:
    def __init__(self, maximum: int) -> None:
        self.maximum = maximum
        self.count = 0

    def consume(self) -> None:
        self.count += 1
        if self.count > self.maximum:
            raise _fail("The NuGet cache exceeds the inspected-entry limit.")


def _nofollow_flag() -> int:
    if not hasattr(os, "O_NOFOLLOW"):
        raise _fail("This platform does not provide the required no-follow file operations.")
    return os.O_NOFOLLOW


def _find_children(
    directory: Path,
    wanted_names: set[str],
    *,
    expected_directory: bool,
    budget: _CacheEntryBudget,
) -> dict[str, Path]:
    found: dict[str, Path] = {}
    try:
        with os.scandir(directory) as iterator:
            for entry in iterator:
                budget.consume()
                if entry.name not in wanted_names:
                    continue
                item_stat = entry.stat(follow_symlinks=False)
                if stat.S_ISLNK(item_stat.st_mode):
                    raise _fail("A required NuGet cache path contains a symbolic link.")
                correct_type = (
                    stat.S_ISDIR(item_stat.st_mode) if expected_directory else stat.S_ISREG(item_stat.st_mode)
                )
                if not correct_type:
                    raise _fail("A required NuGet cache path has an unsupported physical file type.")
                path = Path(entry.path)
                if path.resolve(strict=True) != path:
                    raise _fail("A required NuGet cache path is not physically canonical.")
                found[entry.name] = path
    except OfflineFeedError:
        raise
    except (OSError, RuntimeError, ValueError):
        raise _fail("The NuGet cache cannot be inspected safely.") from None
    return found


def _locate_archives(
    cache: Path, packages: dict[tuple[str, str], _LockedPackage], limits: FeedLimits
) -> list[_Archive]:
    budget = _CacheEntryBudget(limits.max_cache_entries)
    ids = {package_id for package_id, _version in packages}
    package_directories = _find_children(cache, ids, expected_directory=True, budget=budget)
    missing_ids = ids - package_directories.keys()
    if missing_ids:
        raise _fail("A locked package is missing from the supplied NuGet global cache.")

    versions_by_id: dict[str, set[str]] = {}
    for package_id, version in packages:
        versions_by_id.setdefault(package_id, set()).add(version)
    version_directories: dict[tuple[str, str], Path] = {}
    for package_id, versions in sorted(versions_by_id.items()):
        found = _find_children(
            package_directories[package_id],
            versions,
            expected_directory=True,
            budget=budget,
        )
        for version in versions:
            if version not in found:
                raise _fail("A locked package version is missing from the supplied NuGet global cache.")
            version_directories[(package_id, version)] = found[version]

    archive_paths: dict[tuple[str, str], Path] = {}
    for identity, package in sorted(packages.items()):
        version_directory = version_directories[identity]
        found = _find_children(
            version_directory,
            {package.archive_name},
            expected_directory=False,
            budget=budget,
        )
        source = found.get(package.archive_name)
        if source is None:
            raise _fail("A locked .nupkg archive is missing from the supplied NuGet global cache.")
        archive_paths[identity] = source

    archives: list[_Archive] = []
    total_bytes = 0
    flags = os.O_RDONLY | _nofollow_flag()
    for identity, package in sorted(packages.items()):
        try:
            descriptor = os.open(archive_paths[identity], flags)
            try:
                item_stat = os.fstat(descriptor)
            finally:
                os.close(descriptor)
        except OSError:
            raise _fail("A locked .nupkg archive cannot be opened safely.") from None
        if not stat.S_ISREG(item_stat.st_mode) or item_stat.st_size < 1:
            raise _fail("A locked .nupkg archive is not a non-empty regular file.")
        if item_stat.st_size > limits.max_package_bytes:
            raise _fail("A locked .nupkg archive exceeds the per-file byte limit.")
        total_bytes += item_stat.st_size
        if total_bytes > limits.max_total_package_bytes:
            raise _fail("The locked package archives exceed the total byte limit.")
        archives.append(_Archive(package, archive_paths[identity], item_stat.st_size))
    return archives


def _open_directory_descriptor(path: Path) -> int:
    required_flags = ("O_DIRECTORY", "O_NOFOLLOW")
    if any(not hasattr(os, name) for name in required_flags):
        raise _fail("This platform does not provide the required no-follow directory operations.")
    try:
        return os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    except OSError:
        raise _fail("A physical directory cannot be opened safely.") from None


def _dotnet_executable() -> Path:
    for candidate in DOTNET_EXECUTABLES:
        try:
            current = Path(candidate.anchor)
            for index, component in enumerate(candidate.parts[1:], start=1):
                current = current / component
                item_stat = current.lstat()
                if stat.S_ISLNK(item_stat.st_mode):
                    raise _fail("The fixed .NET verifier path contains a symbolic link.")
                if index < len(candidate.parts) - 1 and not stat.S_ISDIR(item_stat.st_mode):
                    raise _fail("The fixed .NET verifier path is not a physical file path.")
            item_stat = candidate.lstat()
            if not stat.S_ISREG(item_stat.st_mode) or not os.access(candidate, os.X_OK):
                raise _fail("The fixed .NET verifier is not an executable file.")
            if candidate.resolve(strict=True) != candidate:
                raise _fail("The fixed .NET verifier path is not canonical.")
            return candidate
        except FileNotFoundError:
            continue
        except OfflineFeedError:
            raise
        except (OSError, RuntimeError, ValueError):
            raise _fail("The fixed .NET verifier is unavailable or unsafe.") from None
    raise _fail("The fixed .NET verifier was not found in the pinned image.")


def _parse_nuget_content_hash_output(output: bytes) -> str:
    try:
        lines = output.decode("utf-8", errors="strict").splitlines()
    except UnicodeDecodeError:
        raise _fail("NuGet verification returned non-UTF-8 output.") from None
    hashes: list[str] = []
    for line in lines:
        if line.startswith("Content hash:"):
            match = re.fullmatch(r"Content hash: ([A-Za-z0-9+/]+={0,2})", line)
            if match is None:
                raise _fail("NuGet verification returned a malformed Content hash line.")
            _decode_content_hash(match.group(1))
            hashes.append(match.group(1))
    if len(hashes) != 1:
        raise _fail("NuGet verification did not return exactly one Content hash line.")
    return hashes[0]


def _nuget_content_hash_verifier(archive_path: Path, timeout_seconds: float, output_limit: int) -> str:
    executable = _dotnet_executable()
    output = bytearray()
    error_output = bytearray()
    output_bytes = 0
    output_lock = threading.Lock()
    output_exceeded = threading.Event()
    process: subprocess.Popen[bytes] | None = None

    try:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-nuget-verify-") as working_directory:
            verifier_environment = {
                "PATH": DOTNET_PATH,
                "HOME": working_directory,
                "DOTNET_CLI_HOME": working_directory,
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                "DOTNET_NOLOGO": "1",
                "DOTNET_CLI_UI_LANGUAGE": "en",
                "LANG": "C",
                "LC_ALL": "C",
            }
            process = subprocess.Popen(
                [str(executable), "nuget", "verify", str(archive_path), "--all", "--verbosity", "minimal"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                cwd=working_directory,
                env=verifier_environment,
                close_fds=True,
            )

            def drain(stream: Any, destination: bytearray) -> None:
                nonlocal output_bytes
                while True:
                    chunk = stream.read(8192)
                    if not chunk:
                        return
                    with output_lock:
                        remaining = output_limit - output_bytes
                        if remaining > 0:
                            destination.extend(chunk[:remaining])
                        output_bytes += len(chunk)
                        if output_bytes > output_limit:
                            output_exceeded.set()

            assert process.stdout is not None and process.stderr is not None
            readers = [
                threading.Thread(target=drain, args=(process.stdout, output), daemon=True),
                threading.Thread(target=drain, args=(process.stderr, error_output), daemon=True),
            ]
            for reader in readers:
                reader.start()

            deadline = time.monotonic() + timeout_seconds
            timed_out = False
            while process.poll() is None:
                if output_exceeded.is_set():
                    process.kill()
                    break
                if time.monotonic() >= deadline:
                    timed_out = True
                    process.kill()
                    break
                time.sleep(0.01)
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            for reader in readers:
                reader.join(timeout=1)
            if any(reader.is_alive() for reader in readers):
                raise _fail("NuGet verification output could not be drained safely.")
            if output_exceeded.is_set():
                raise _fail("NuGet verification exceeded its output-byte limit.")
            if timed_out:
                raise _fail("NuGet verification exceeded its time limit.")
            if process.returncode != 0:
                raise _fail("NuGet verification failed for a copied package archive.")
            return _parse_nuget_content_hash_output(bytes(output) + b"\n" + bytes(error_output))
    except OfflineFeedError:
        raise
    except (OSError, subprocess.SubprocessError):
        if process is not None and process.poll() is None:
            process.kill()
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                pass
        raise _fail("NuGet verification could not be started or completed safely.") from None
    finally:
        if process is not None:
            if process.stdout is not None:
                process.stdout.close()
            if process.stderr is not None:
                process.stderr.close()


def _copy_archive(archive: _Archive, staging_name: str, output_fd: int, limits: FeedLimits) -> int:
    source_fd = -1
    destination_fd = -1
    created = False
    try:
        source_fd = os.open(archive.source, os.O_RDONLY | _nofollow_flag())
        source_stat = os.fstat(source_fd)
        if not stat.S_ISREG(source_stat.st_mode) or source_stat.st_size != archive.size:
            raise _fail("A locked .nupkg archive changed after cache validation.")
        destination_fd = os.open(
            staging_name,
            os.O_WRONLY | os.O_CREAT | os.O_EXCL | _nofollow_flag(),
            0o644,
            dir_fd=output_fd,
        )
        created = True
        copied_bytes = 0
        with os.fdopen(source_fd, "rb") as source_file, os.fdopen(destination_fd, "wb") as destination_file:
            source_fd = destination_fd = -1
            while True:
                chunk = source_file.read(COPY_CHUNK_BYTES)
                if not chunk:
                    break
                copied_bytes += len(chunk)
                if copied_bytes > limits.max_package_bytes or copied_bytes > archive.size:
                    raise _fail("A locked .nupkg archive grew beyond its validated byte limit.")
                destination_file.write(chunk)
            destination_file.flush()
        if copied_bytes != archive.size:
            raise _fail("A locked .nupkg archive changed size while being copied.")
        return copied_bytes
    except OfflineFeedError:
        if created:
            try:
                os.unlink(staging_name, dir_fd=output_fd)
            except OSError:
                pass
        raise
    except OSError as error:
        if created:
            try:
                os.unlink(staging_name, dir_fd=output_fd)
            except OSError:
                pass
        errno = error.errno if isinstance(error.errno, int) else "unknown"
        raise _fail(f"A locked .nupkg archive could not be copied safely (errno {errno}).") from None
    finally:
        if source_fd >= 0:
            os.close(source_fd)
        if destination_fd >= 0:
            os.close(destination_fd)


def _verify_copied_archive(
    archive: _Archive,
    copied_path: Path,
    limits: FeedLimits,
    started_at: float,
    injected_verifier: Callable[[Path], str] | None,
) -> None:
    if injected_verifier is None:
        remaining_total = limits.max_total_verifier_seconds - (time.monotonic() - started_at)
        if remaining_total <= 0:
            raise _fail("NuGet verification exceeded the total time limit.")
        actual_hash = _nuget_content_hash_verifier(
            copied_path,
            min(limits.max_verifier_seconds_per_archive, remaining_total),
            limits.max_verifier_output_bytes,
        )
    else:
        actual_hash = injected_verifier(copied_path)
    actual_digest = _decode_content_hash(actual_hash)
    if actual_digest != archive.package.content_hash:
        raise _fail("A copied .nupkg archive does not match NuGet's lock-file contentHash.")


def _promote_verified_archive(staging_name: str, archive_name: str, output_fd: int) -> None:
    try:
        os.link(
            staging_name,
            archive_name,
            src_dir_fd=output_fd,
            dst_dir_fd=output_fd,
            follow_symlinks=False,
        )
        os.unlink(staging_name, dir_fd=output_fd)
    except OSError:
        raise _fail("A NuGet-verified archive could not be promoted exclusively into the feed.") from None


def _remove_partial_output(parent_fd: int, output_name: str, output_fd: int, created_names: list[str]) -> None:
    for name in reversed(created_names):
        try:
            os.unlink(name, dir_fd=output_fd)
        except OSError:
            pass
    try:
        os.close(output_fd)
    except OSError:
        pass
    try:
        os.rmdir(output_name, dir_fd=parent_fd)
    except OSError:
        pass


def build_offline_feed(
    checkout: str | os.PathLike[str],
    solution_relative_path: str | os.PathLike[str],
    global_packages: str | os.PathLike[str],
    output_directory: str | os.PathLike[str],
    *,
    limits: FeedLimits = DEFAULT_LIMITS,
    _content_hash_verifier: Callable[[Path], str] | None = None,
) -> FeedBuildResult:
    """Create a flat feed from solution-selected locks and NuGet-verified archives.

    ``checkout`` must be the trusted base checkout. ``solution_relative_path``
    must identify a trusted ``.slnx`` below it. ``global_packages`` must be a
    separate physical NuGet global-packages cache, and ``output_directory``
    must be a nonexistent directory whose parent already exists. ``limits``
    can lower the reviewed hard ceilings but cannot raise them. The private
    ``_content_hash_verifier`` seam exists only for synthetic tests; production
    CLI calls always use the bounded fixed .NET verifier.
    """

    _validate_limits(limits)
    _nofollow_flag()
    checkout_root = _physical_directory(checkout, "The trusted checkout")
    cache_root = _physical_directory(global_packages, "The NuGet global cache")
    if _paths_overlap(checkout_root, cache_root):
        raise _fail("The trusted checkout and NuGet global cache must be separate physical trees.")
    output_parent, output_path = _new_output_location(output_directory, checkout_root, cache_root)

    lock_files = _select_lock_files(checkout_root, solution_relative_path, limits)
    packages = _collect_locked_packages(lock_files, limits)
    archives = _locate_archives(cache_root, packages, limits)

    parent_fd = _open_directory_descriptor(output_parent)
    output_fd = -1
    created_names: list[str] = []
    output_created = False
    try:
        try:
            os.mkdir(output_path.name, mode=0o700, dir_fd=parent_fd)
            output_created = True
        except FileExistsError:
            raise _fail("The output directory must not already exist.") from None
        except OSError:
            raise _fail("The new output directory cannot be created safely.") from None
        try:
            output_fd = os.open(
                output_path.name,
                os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                dir_fd=parent_fd,
            )
        except OSError:
            raise _fail("The new output directory cannot be opened safely.") from None

        total_bytes = 0
        verification_started = time.monotonic()
        for index, archive in enumerate(archives):
            staging_name = f".offline-feed-{index:05d}.tmp"
            created_names.append(staging_name)
            copied_bytes = _copy_archive(archive, staging_name, output_fd, limits)
            _verify_copied_archive(
                archive,
                output_path / staging_name,
                limits,
                verification_started,
                _content_hash_verifier,
            )
            created_names.append(archive.package.archive_name)
            _promote_verified_archive(staging_name, archive.package.archive_name, output_fd)
            total_bytes += copied_bytes
        if total_bytes > limits.max_total_package_bytes:
            raise _fail("The copied package archives exceed the total byte limit.")
        return FeedBuildResult(len(lock_files), len(archives), total_bytes)
    except BaseException:
        if output_created:
            if output_fd < 0:
                try:
                    output_fd = os.open(
                        output_path.name,
                        os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW,
                        dir_fd=parent_fd,
                    )
                except OSError:
                    output_fd = -1
            if output_fd >= 0:
                _remove_partial_output(parent_fd, output_path.name, output_fd, created_names)
                output_fd = -1
            else:
                try:
                    os.rmdir(output_path.name, dir_fd=parent_fd)
                except OSError:
                    pass
        raise
    finally:
        if output_fd >= 0:
            try:
                os.close(output_fd)
            except OSError:
                pass
        try:
            os.close(parent_fd)
        except OSError:
            pass


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--checkout", required=True, help="Absolute trusted base checkout root.")
    parser.add_argument("--solution", required=True, help="Trusted repository-relative .slnx project selection.")
    parser.add_argument("--global-packages", required=True, help="Absolute physical NuGet global-packages cache root.")
    parser.add_argument("--output", required=True, help="Absolute path to a new output feed directory.")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_arguments(argv)
    try:
        result = build_offline_feed(args.checkout, args.solution, args.global_packages, args.output)
    except OfflineFeedError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    print(
        f"verified offline feed: {result.package_count} packages from "
        f"{result.lock_file_count} lock files ({result.package_bytes} bytes)"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
