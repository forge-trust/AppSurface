#!/usr/bin/env python3
"""Plan or run fourteen source-bound, complete qualification image builds.

The default API call is plan-only. Executing requires ``execute=True`` and is
never done by this sidecar's test suite. The only accepted recipe is the
byte-pinned N11 build-fdd.sh input; specialization changes selector literals
inside a private per-image copy and leaves the pinned source tree untouched.
"""

from __future__ import annotations

import ast
import integrated16_build_contract as contract
import dataclasses
import hashlib
import json
import os
import pathlib
import re
import signal
import stat
import subprocess
import threading
import time
from dataclasses import dataclass
from typing import Any, Callable


SOURCE_COMMIT = '5b4ed381badb91de47d793715d05b6ae7737e39e'
SOURCE_PARENT = '914046e1e998ca89a5f0f5be39a90bbfb88a431d'
SOURCE_TREE = '3bafd0fcd5321f1b29323f46df94b77eb48fa859'
CAPTURE_SHA256 = '4ad23a2fdeccf9abbcd06724b9c97e3a4c1751ecb45e8321cc35bd79c036d540'
CAPTURE_PROJECTION_SHA256 = '488b59a9bd50ef1f05f8eb4291ed3eee164b6d1536cdeec73fe9f6847194100c'
SOURCE_MAP_SHA256 = '758a0f769ca67ce6f1fda2eea3569a373efafb192e2d2c2cce2224809b352a7d'
SOURCE_COUNT = 2872
CAPTURE_BYTES = 688655
CAPTURE_MODE = 0o600
# The current captured source is verified by plan preflight, but final harness
# review and upstream Linux runner/prerequisite pin review remain pending.
SOURCE_BINDING_STATUS = "current_capture_pending_final_harness_review"
BASE_RECIPE_SHA256 = '0927cc47a35be8ed7fd2f3b7c03350ef912f89c37cf38d8aeec6936ad6b18ca4'
BASE_RECIPE_BYTES = 67388
BASE_RECIPE_MODE = 0o644
# The corrected parent recipe is copied locally. Execution stays unset until
# final harness review and upstream Linux runner/prerequisite pins are cleared.
EXECUTION_CLEARED_RECIPE_SHA256: str | None = None

TARGET_CAPTURE = pathlib.Path(
    "/Users/andrew/Projects/forge-trust/runnable__saturn/TestResults/issue779-recovery-20261015/control-continuity-recovery/n07-joined-source-capture/source-capture.json"
)
TARGET_SOURCE_ROOT = pathlib.Path(
    "/Users/andrew/Projects/forge-trust/runnable__saturn/TestResults/issue779-recovery-20261015/control-continuity-recovery/n07-joined-source-capture/repo"
)
BASE_RECIPE = pathlib.Path(__file__).resolve().with_name("build-fdd.sh")

SELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))
CASES_BY_SELECTOR = {
    "None": ("N01", "N02", "N03"),
    **{f"N{i:02d}": (f"N{i:02d}",) for i in range(4, 17)},
}
TOTAL_DEADLINE_SECONDS = 14 * 1200
PER_IMAGE_DEADLINE_SECONDS = 1200
TERM_GRACE_SECONDS = 2.0
POLL_SECONDS = 0.02
CLEANUP_RESERVE_SECONDS = 5.0
MAX_CAPTURE_BYTES = 4 * 1024 * 1024
MAX_RECEIPT_BYTES = 4 * 1024 * 1024
MAX_ARTIFACT_FILES = 8192
MAX_ARTIFACT_BYTES = 1024 * 1024 * 1024
MAX_OUTER_LOG_BYTES = 8 * 1024 * 1024
GENERATED_SOURCE_DIRS = frozenset({"obj", "bin", "node_modules"})
GENERATED_SOURCE_FILES = frozenset(
    {"Web/ForgeTrust.AppSurface.Docs/wwwroot/css/site.gen.css"}
)


class BuildPreparationError(ValueError):
    """Input or output failed a closed preflight check."""


class BuildExecutionError(RuntimeError):
    """A recipe failed or could not prove its process group was joined."""


@dataclass(frozen=True)
class SourceTarget:
    commit: str
    parent: str
    tree: str
    count: int
    capture_path: pathlib.Path
    capture_sha256: str
    capture_bytes: int
    capture_mode: int
    capture_projection_sha256: str
    source_map_sha256: str
    source_root: pathlib.Path


@dataclass(frozen=True)
class BuildConfig:
    source: SourceTarget
    recipe_path: pathlib.Path
    recipe_sha256: str
    recipe_bytes: int
    recipe_mode: int
    total_deadline_seconds: int = TOTAL_DEADLINE_SECONDS
    per_image_deadline_seconds: int = PER_IMAGE_DEADLINE_SECONDS


DEFAULT_CONFIG = BuildConfig(
    source=SourceTarget(
        commit=SOURCE_COMMIT,
        parent=SOURCE_PARENT,
        tree=SOURCE_TREE,
        count=SOURCE_COUNT,
        capture_path=TARGET_CAPTURE,
        capture_sha256=CAPTURE_SHA256,
        capture_bytes=CAPTURE_BYTES,
        capture_mode=CAPTURE_MODE,
        capture_projection_sha256=CAPTURE_PROJECTION_SHA256,
        source_map_sha256=SOURCE_MAP_SHA256,
        source_root=TARGET_SOURCE_ROOT,
    ),
    recipe_path=BASE_RECIPE,
    recipe_sha256=BASE_RECIPE_SHA256,
    recipe_bytes=BASE_RECIPE_BYTES,
    recipe_mode=BASE_RECIPE_MODE,
)


@dataclass(frozen=True)
class ImagePlan:
    selector: str
    cases: tuple[str, ...]
    output_dir: pathlib.Path
    receipt_path: pathlib.Path
    stdout_path: pathlib.Path
    stderr_path: pathlib.Path
    recipe_path: pathlib.Path
    recipe_bytes: bytes
    recipe_sha256: str


@dataclass(frozen=True)
class PreparedPlan:
    config: BuildConfig
    capture: dict[str, Any]
    harness_root: pathlib.Path
    output_root: pathlib.Path
    images: tuple[ImagePlan, ...]
    source_tree_sha256: str


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _json_bytes(value: Any) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")


def _unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise BuildPreparationError("duplicate-json-key")
        result[key] = value
    return result


def _decode_json(data: bytes) -> dict[str, Any]:
    try:
        value = json.loads(data.decode("utf-8", errors="strict"), object_pairs_hook=_unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise BuildPreparationError("invalid-json") from error
    if type(value) is not dict:
        raise BuildPreparationError("json-root-not-object")
    return value


def _identity(st: os.stat_result) -> tuple[int, int, int, int, int, int, int, int, int]:
    return (
        st.st_dev,
        st.st_ino,
        st.st_mode,
        st.st_uid,
        st.st_gid,
        st.st_nlink,
        st.st_size,
        st.st_mtime_ns,
        st.st_ctime_ns,
    )


def _check_deadline(deadline: float | None, clock: Callable[[], float], phase: str) -> None:
    if deadline is not None and clock() >= deadline:
        raise BuildExecutionError(f"{phase}-deadline")


def _read_regular(
    path: pathlib.Path,
    max_bytes: int,
    *,
    deadline: float | None = None,
    clock: Callable[[], float] = time.monotonic,
) -> tuple[bytes, int]:
    _check_deadline(deadline, clock, "postbuild-read")
    try:
        before = path.lstat()
    except OSError as error:
        raise BuildPreparationError(f"missing-input:{path}") from error
    if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1 or before.st_size > max_bytes:
        raise BuildPreparationError(f"input-file-shape:{path}")
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0) | getattr(os, "O_CLOEXEC", 0)
    try:
        fd = os.open(path, flags)
    except OSError as error:
        raise BuildPreparationError(f"input-open:{path}") from error
    try:
        opened = os.fstat(fd)
        if _identity(opened) != _identity(before):
            raise BuildPreparationError(f"input-replaced:{path}")
        chunks: list[bytes] = []
        size = 0
        while True:
            _check_deadline(deadline, clock, "postbuild-read")
            chunk = os.read(fd, min(65536, max_bytes + 1 - size))
            if not chunk:
                break
            chunks.append(chunk)
            size += len(chunk)
            if size > max_bytes:
                raise BuildPreparationError(f"input-size-bound:{path}")
        after_fd = os.fstat(fd)
        after_path = path.lstat()
        _check_deadline(deadline, clock, "postbuild-read")
        if _identity(after_fd) != _identity(before) or _identity(after_path) != _identity(before):
            raise BuildPreparationError(f"input-changed:{path}")
        if size != before.st_size:
            raise BuildPreparationError(f"input-short-read:{path}")
        return b"".join(chunks), stat.S_IMODE(before.st_mode)
    finally:
        os.close(fd)


def _capture_projection(capture: dict[str, Any]) -> dict[str, Any]:
    if "source_tree" not in capture:
        raise BuildPreparationError("capture-tree-missing")
    return {
        "validation_commit": capture.get("validation_commit"),
        "previous_validation_commit": capture.get("previous_validation_commit"),
        "source_count": capture.get("source_count"),
        "tree": capture["source_tree"],
        "source": capture.get("source"),
    }


def _validate_capture(config: BuildConfig) -> dict[str, Any]:
    raw, mode = _read_regular(config.source.capture_path, MAX_CAPTURE_BYTES)
    if len(raw) != config.source.capture_bytes or mode != config.source.capture_mode:
        raise BuildPreparationError("capture-size-or-mode-mismatch")
    if _sha256(raw) != config.source.capture_sha256:
        raise BuildPreparationError("capture-sha256-mismatch")
    capture = _decode_json(raw)
    if capture.get("schema") != "issue779-csharp-common-source-capture-v4":
        raise BuildPreparationError("capture-schema-mismatch")
    if (
        capture.get("validation_commit") != config.source.commit
        or capture.get("previous_validation_commit") != config.source.parent
        or capture.get("source_count") != config.source.count
        or capture.get("source_tree") != config.source.tree
    ):
        raise BuildPreparationError("capture-source-identity-mismatch")
    source = capture.get("source")
    if type(source) is not dict or set(source) != {"sha256", "modes"}:
        raise BuildPreparationError("capture-source-map-shape")
    hashes, modes = source["sha256"], source["modes"]
    if type(hashes) is not dict or type(modes) is not dict:
        raise BuildPreparationError("capture-source-map-types")
    if len(hashes) != config.source.count or len(modes) != config.source.count or set(hashes) != set(modes):
        raise BuildPreparationError("capture-source-map-count")
    if any(not isinstance(name, str) or not isinstance(value, str)
           or not re.fullmatch(r"[0-9a-f]{64}", value)
           for name, value in hashes.items()):
        raise BuildPreparationError("capture-hash-entry-shape")
    if any(not isinstance(name, str) or not isinstance(value, str)
           or not re.fullmatch(r"0[0-7]{3}", value)
           for name, value in modes.items()):
        raise BuildPreparationError("capture-mode-entry-shape")
    projection_sha = _sha256(_json_bytes(_capture_projection(capture)).rstrip(b"\n"))
    if projection_sha != config.source.capture_projection_sha256:
        raise BuildPreparationError("capture-projection-mismatch")
    if _sha256(_json_bytes(source).rstrip(b"\n")) != config.source.source_map_sha256:
        raise BuildPreparationError("capture-source-map-sha256-mismatch")
    return capture


def _validate_relpath(value: str) -> pathlib.PurePosixPath:
    path = pathlib.PurePosixPath(value)
    if path.is_absolute() or not value or any(part in ("", ".", "..") for part in path.parts):
        raise BuildPreparationError("unsafe-capture-path")
    return path


def _validate_source_tree(source_root: pathlib.Path, capture: dict[str, Any]) -> str:
    if not source_root.is_absolute() or source_root != source_root.resolve(strict=True):
        raise BuildPreparationError("source-root-not-canonical")
    try:
        root_stat = source_root.lstat()
    except OSError as error:
        raise BuildPreparationError("source-root-missing") from error
    if not stat.S_ISDIR(root_stat.st_mode):
        raise BuildPreparationError("source-root-not-directory")

    expected_hashes = capture["source"]["sha256"]
    expected_modes = capture["source"]["modes"]
    actual: set[str] = set()
    root_prefix = source_root
    for current, dirnames, filenames in os.walk(root_prefix, topdown=True, followlinks=False):
        current_path = pathlib.Path(current)
        if current_path == root_prefix and ".git" in dirnames:
            dirnames.remove(".git")
        elif current_path == root_prefix and ".git" in filenames:
            filenames.remove(".git")
        for name in list(dirnames):
            item = current_path / name
            st = item.lstat()
            if stat.S_ISLNK(st.st_mode) or not stat.S_ISDIR(st.st_mode):
                raise BuildPreparationError("source-directory-link-or-shape")
            # These are known compiler/package output directories. Their
            # contents are never captured source inputs and are excluded from
            # traversal; every captured source path remains byte/mode checked.
            if name in GENERATED_SOURCE_DIRS:
                dirnames.remove(name)
        for name in filenames:
            item = current_path / name
            rel = item.relative_to(root_prefix).as_posix()
            if rel not in expected_hashes:
                if rel in GENERATED_SOURCE_FILES:
                    # Tailwind writes this generated output during the pinned
                    # asset preparation step; the capture intentionally pins
                    # only its source inputs.
                    _read_regular(item, 256 * 1024 * 1024)
                    continue
                raise BuildPreparationError(f"unexpected-source-file:{rel}")
            raw, mode = _read_regular(item, 256 * 1024 * 1024)
            if _sha256(raw) != expected_hashes[rel]:
                raise BuildPreparationError(f"source-bytes-mismatch:{rel}")
            if f"{mode:04o}" != expected_modes[rel]:
                raise BuildPreparationError(f"source-mode-mismatch:{rel}")
            actual.add(rel)
    if actual != set(expected_hashes):
        missing = sorted(set(expected_hashes) - actual)
        raise BuildPreparationError("source-files-missing:" + ",".join(missing[:5]))
    return _sha256(_json_bytes(capture["source"]).rstrip(b"\n"))


def _embedded_python(source: bytes) -> str:
    marker = b"<<'PY'\n"
    start = source.find(marker)
    if start < 0:
        raise BuildPreparationError("recipe-python-block-missing")
    start += len(marker)
    end = source.find(b"\nPY\n", start)
    if end < 0:
        raise BuildPreparationError("recipe-python-block-unterminated")
    try:
        return source[start:end].decode("utf-8", errors="strict")
    except UnicodeDecodeError as error:
        raise BuildPreparationError("recipe-python-not-utf8") from error


def _specialize_recipe(base: bytes, selector: str) -> bytes:
    if selector not in SELECTORS:
        raise BuildPreparationError("selector-not-in-closed-set")
    literal = b"SELECTED_VARIANT = 'N11'"
    if base.count(literal) != 1:
        raise BuildPreparationError("base-recipe-selector-shape")
    result = base.replace(literal, ("SELECTED_VARIANT = '" + selector + "'").encode("ascii"))
    try:
        ast.parse(_embedded_python(result), filename="derived-build-fdd.py")
    except SyntaxError as error:
        raise BuildPreparationError("derived-recipe-python-syntax") from error
    if result.splitlines()[0] != b"#!/usr/bin/env bash" or b"set -euo pipefail" not in result.splitlines()[:5]:
        raise BuildPreparationError("recipe-shell-prologue-changed")
    if b'[[ $# == 4 && "$1" == --execute ]]' not in result:
        raise BuildPreparationError("recipe-execution-guard-changed")
    return result


def _read_recipe(config: BuildConfig) -> bytes:
    raw, mode = _read_regular(config.recipe_path, 1024 * 1024)
    if len(raw) != config.recipe_bytes or mode != config.recipe_mode:
        raise BuildPreparationError("recipe-size-or-mode-mismatch")
    if _sha256(raw) != config.recipe_sha256:
        raise BuildPreparationError("recipe-sha256-mismatch")
    source = config.source
    text = raw.decode("utf-8", errors="strict")
    pins = {
        "HEAD": source.commit,
        "PARENT": source.parent,
        "HARNESS_PARENT": source.commit,
        "TREE": source.tree,
        "CAPTURE_SHA": source.capture_sha256,
        "CAPTURE_PROJECTION_SHA": source.capture_projection_sha256,
        "COUNT": str(source.count),
    }
    for name, expected in pins.items():
        match = re.search(rf"^{name}\s*=\s*['\"]?([^'\"\n]+)['\"]?\s*$", text, re.MULTILINE)
        if match is None or match.group(1) != expected:
            raise BuildPreparationError(f"recipe-source-pin-mismatch:{name}")
    if text.count("SELECTED_VARIANT = 'N11'") != 1:
        raise BuildPreparationError("recipe-fixed-selector-shape")
    return raw


def _slug(selector: str) -> str:
    return "ordinary" if selector == "None" else selector.lower()


def _path_is_fresh(path: pathlib.Path) -> bool:
    return not os.path.lexists(path)


def _prepare(config: BuildConfig, harness_root: pathlib.Path, output_root: pathlib.Path) -> PreparedPlan:
    expected_selectors = ("None", *(f"N{i:02d}" for i in range(4, 17)))
    expected_cases = {
        "None": ("N01", "N02", "N03"),
        **{f"N{i:02d}": (f"N{i:02d}",) for i in range(4, 17)},
    }
    flattened_cases = [case for cases in CASES_BY_SELECTOR.values() for case in cases]
    if tuple(SELECTORS) != expected_selectors:
        raise BuildPreparationError("selector-set-mutated")
    if CASES_BY_SELECTOR != expected_cases or len(flattened_cases) != 16 or set(flattened_cases) != {
        f"N{i:02d}" for i in range(1, 17)
    }:
        raise BuildPreparationError("case-selector-map-incomplete")
    if config.total_deadline_seconds != len(SELECTORS) * config.per_image_deadline_seconds:
        raise BuildPreparationError("cumulative-deadline-shape")
    if config.per_image_deadline_seconds <= 0 or config.total_deadline_seconds <= 0:
        raise BuildPreparationError("deadline-invalid")
    for path in (config.source.capture_path, config.source.source_root, config.recipe_path):
        if not path.is_absolute() or path != path.resolve(strict=True):
            raise BuildPreparationError(f"pinned-input-path-not-canonical:{path}")
    if not harness_root.is_absolute() or harness_root != harness_root.resolve(strict=True):
        raise BuildPreparationError("harness-root-not-canonical")
    if not stat.S_ISDIR(harness_root.lstat().st_mode):
        raise BuildPreparationError("harness-root-not-directory")
    if not output_root.is_absolute() or output_root != output_root.resolve(strict=False):
        raise BuildPreparationError("output-root-not-canonical")
    if not _path_is_fresh(output_root):
        raise BuildPreparationError("output-root-not-fresh")
    for protected in (config.source.capture_path, config.source.source_root, harness_root, config.recipe_path):
        if output_root == protected or output_root in protected.parents or protected in output_root.parents:
            raise BuildPreparationError("output-overlaps-input")

    capture = _validate_capture(config)
    source_tree_sha = _validate_source_tree(config.source.source_root, capture)
    base = _read_recipe(config)
    images: list[ImagePlan] = []
    for selector in SELECTORS:
        slug = _slug(selector)
        derived = _specialize_recipe(base, selector)
        images.append(
            ImagePlan(
                selector=selector,
                cases=CASES_BY_SELECTOR[selector],
                output_dir=output_root / "images" / slug,
                receipt_path=output_root / "receipts" / f"{slug}.json",
                stdout_path=output_root / "logs" / f"{slug}.stdout.log",
                stderr_path=output_root / "logs" / f"{slug}.stderr.log",
                recipe_path=output_root / "recipes" / f"{slug}.build-fdd.sh",
                recipe_bytes=derived,
                recipe_sha256=_sha256(derived),
            )
        )
    return PreparedPlan(config, capture, harness_root, output_root, tuple(images), source_tree_sha)


def _public_plan(plan: PreparedPlan) -> dict[str, Any]:
    return {
        "schema": "issue779-integrated16-build-plan-v1",
        "source": {
            "binding_status": SOURCE_BINDING_STATUS,
            "commit": plan.config.source.commit,
            "parent": plan.config.source.parent,
            "tree": plan.config.source.tree,
            "count": plan.config.source.count,
            "capture_path": str(plan.config.source.capture_path),
            "capture_sha256": plan.config.source.capture_sha256,
            "capture_bytes": plan.config.source.capture_bytes,
            "capture_projection_sha256": plan.config.source.capture_projection_sha256,
            "source_tree_sha256": plan.source_tree_sha256,
        },
        "base_recipe": {
            "path": str(plan.config.recipe_path),
            "sha256": plan.config.recipe_sha256,
            "bytes": plan.config.recipe_bytes,
            "mode": f"{plan.config.recipe_mode:04o}",
            "specialization": "exact N11 selector-literal rewrite in private per-image copy",
        },
        "harness_root": str(plan.harness_root),
        "output_root": str(plan.output_root),
        "selectors": list(SELECTORS),
        "cases": {selector: list(CASES_BY_SELECTOR[selector]) for selector in SELECTORS},
        "deadline": {
            "cumulative_seconds": plan.config.total_deadline_seconds,
            "per_image_cap_seconds": plan.config.per_image_deadline_seconds,
            "order": "sequential, one monotonic deadline shared by all fourteen images",
        },
        "builds_started": False,
        "native_execution": False,
        "native_coverage_claim": False,
        "images": [
            {
                "selector": image.selector,
                "cases": list(image.cases),
                "output_dir": str(image.output_dir),
                "receipt_path": str(image.receipt_path),
                "stdout_path": str(image.stdout_path),
                "stderr_path": str(image.stderr_path),
                "derived_recipe_sha256": image.recipe_sha256,
                "image_sha256": None,
            }
            for image in plan.images
        ],
    }


def _atomic_json(
    path: pathlib.Path,
    value: dict[str, Any],
    *,
    deadline: float | None = None,
    clock: Callable[[], float] = time.monotonic,
) -> str:
    _check_deadline(deadline, clock, "postbuild-receipt")
    raw = _json_bytes(value)
    _check_deadline(deadline, clock, "postbuild-receipt")
    temp = path.with_name(path.name + f".{os.getpid()}.tmp")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(temp, flags, 0o600)
    try:
        offset = 0
        while offset < len(raw):
            _check_deadline(deadline, clock, "postbuild-receipt")
            written = os.write(fd, raw[offset : offset + 65536])
            if written <= 0:
                raise OSError("receipt-write-made-no-progress")
            offset += written
        os.fsync(fd)
        _check_deadline(deadline, clock, "postbuild-receipt")
    finally:
        os.close(fd)
    os.replace(temp, path)
    dir_fd = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(dir_fd)
    finally:
        os.close(dir_fd)
    _check_deadline(deadline, clock, "postbuild-receipt")
    return _sha256(raw)


def _group_alive(pgid: int) -> bool:
    try:
        os.killpg(pgid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True


def _signal_group(process: Any, sig: int) -> None:
    # A completed wait releases ownership of the numeric PID/PGID. Refuse any
    # later signal even if a caller accidentally attempts a second group join.
    if process.returncode is not None:
        raise BuildExecutionError("process-group-signal-after-leader-reap")
    try:
        os.killpg(process.pid, sig)
    except ProcessLookupError:
        pass


def _leader_exit_observed_unreaped(process: Any) -> bool:
    """Observe child exit without reaping it, preserving PID/PGID ownership."""
    waitid = getattr(os, "waitid", None)
    required = ("P_PID", "WEXITED", "WNOHANG", "WNOWAIT")
    if waitid is None or any(not hasattr(os, name) for name in required):
        raise BuildExecutionError("unreaped-leader-observation-unavailable")
    try:
        result = waitid(os.P_PID, process.pid, os.WEXITED | os.WNOHANG | os.WNOWAIT)
    except (ChildProcessError, OSError) as error:
        raise BuildExecutionError("unreaped-leader-observation-failed") from error
    return result is not None and result.si_pid == process.pid


def _join_group(
    process: Any,
    cleanup_deadline: float,
    *,
    clock: Callable[[], float],
    sleeper: Callable[[float], None],
) -> dict[str, Any]:
    signals: list[str] = []
    pgid = process.pid
    if _group_alive(pgid):
        _signal_group(process, signal.SIGTERM)
        signals.append("TERM")
        term_end = min(cleanup_deadline, clock() + TERM_GRACE_SECONDS)
        while _group_alive(pgid) and clock() < term_end:
            sleeper(min(POLL_SECONDS, max(0.0, term_end - clock())))
    if _group_alive(pgid):
        _signal_group(process, signal.SIGKILL)
        signals.append("KILL")
    waited = False
    try:
        if clock() < cleanup_deadline:
            process.wait(timeout=max(0.001, cleanup_deadline - clock()))
            waited = True
    except subprocess.TimeoutExpired:
        pass
    # Never signal the numeric process-group ID after wait() reaps its leader.
    # Post-reap probes can only fail closed; they cannot trigger a stale signal.
    while waited and _group_alive(pgid) and clock() < cleanup_deadline:
        sleeper(min(POLL_SECONDS, max(0.0, cleanup_deadline - clock())))
    group_absent = waited and not _group_alive(pgid)
    return {"waited": waited, "group_absent": group_absent, "signals": signals}


def _run_child(
    image: ImagePlan,
    harness_root: pathlib.Path,
    capture_path: pathlib.Path,
    image_deadline: float,
    cleanup_deadline: float,
    *,
    popen: Callable[..., Any] | None = None,
    clock: Callable[[], float] | None = None,
    sleeper: Callable[[float], None] | None = None,
) -> dict[str, Any]:
    popen = popen or subprocess.Popen
    clock = clock or time.monotonic
    sleeper = sleeper or time.sleep
    if clock() >= image_deadline:
        raise BuildExecutionError("cumulative-deadline-cleanup-reserve")
    process = None
    stdout = image.stdout_path.open("xb", buffering=0)
    stderr = image.stderr_path.open("xb", buffering=0)
    pipes: list[Any] = []
    readers: list[threading.Thread] = []
    stop_readers = threading.Event()
    output_lock = threading.Lock()
    output_overflow = threading.Event()
    reader_errors: list[str] = []
    retained = 0
    joined: dict[str, Any] | None = None

    def drain(pipe: Any, target: Any) -> None:
        nonlocal retained
        while not stop_readers.is_set():
            try:
                data = os.read(pipe.fileno(), 65536)
            except BlockingIOError:
                stop_readers.wait(0.01)
                continue
            except OSError as error:
                reader_errors.append(type(error).__name__)
                return
            if not data:
                return
            with output_lock:
                available = MAX_OUTER_LOG_BYTES - retained
                keep = data[:available]
                if keep:
                    try:
                        target.write(keep)
                    except OSError as error:
                        reader_errors.append(type(error).__name__)
                        return
                    retained += len(keep)
                if len(data) > available:
                    output_overflow.set()

    try:
        argv = [
            "/bin/bash",
            str(image.recipe_path),
            "--execute",
            str(harness_root),
            str(image.output_dir),
            str(capture_path),
        ]
        process = popen(
            argv,
            cwd=harness_root,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            start_new_session=True,
        )
        if process.stdout is None or process.stderr is None:
            raise BuildExecutionError("outer-recipe-pipe-missing")
        for pipe, target in ((process.stdout, stdout), (process.stderr, stderr)):
            os.set_blocking(pipe.fileno(), False)
            pipes.append(pipe)
            reader = threading.Thread(target=drain, args=(pipe, target), daemon=True)
            readers.append(reader)
            reader.start()
        while True:
            if output_overflow.is_set():
                raise BuildExecutionError("outer-recipe-log-bound")
            if reader_errors:
                raise BuildExecutionError("outer-recipe-pipe-read-failed")
            if _leader_exit_observed_unreaped(process):
                joined = _join_group(
                    process,
                    min(cleanup_deadline, clock() + TERM_GRACE_SECONDS + 1.0),
                    clock=clock,
                    sleeper=sleeper,
                )
                if not joined["waited"] or not joined["group_absent"]:
                    error = BuildExecutionError("child-group-join-unproved")
                    error.join = joined  # type: ignore[attr-defined]
                    raise error
                # Group ownership is now released. Require both bounded pipe
                # readers to observe EOF before accepting the recipe.
                for reader in readers:
                    reader.join(timeout=max(0.0, image_deadline - clock()))
                if any(reader.is_alive() for reader in readers):
                    error = BuildExecutionError("outer-recipe-pipe-eof-unproved")
                    error.join = joined  # type: ignore[attr-defined]
                    raise error
                if output_overflow.is_set():
                    error = BuildExecutionError("outer-recipe-log-bound")
                    error.join = joined  # type: ignore[attr-defined]
                    raise error
                if reader_errors:
                    error = BuildExecutionError("outer-recipe-pipe-read-failed")
                    error.join = joined  # type: ignore[attr-defined]
                    raise error
                status = process.returncode
                if status != 0:
                    error = BuildExecutionError(f"builder-exit-{status}")
                    error.join = joined  # type: ignore[attr-defined]
                    raise error
                return {"exit": status, "waited": True, "group_absent": True, "signals": joined["signals"]}
            if clock() >= image_deadline:
                raise BuildExecutionError("per-image-deadline")
            time.sleep(min(POLL_SECONDS, max(0.0, image_deadline - clock())))
    except BaseException as error:
        error_join = getattr(error, "join", None)
        if process is not None and error_join is None and joined is None:
            joined = _join_group(
                process,
                min(cleanup_deadline,
                    clock() + TERM_GRACE_SECONDS + 1.0),
                clock=clock,
                sleeper=sleeper,
            )
        join = error_join or joined or {
            "waited": process is None, "group_absent": process is None, "signals": []
        }
        stop_readers.set()
        reader_join_deadline = cleanup_deadline
        for reader in readers:
            reader.join(timeout=max(0.0, reader_join_deadline - clock()))
        if any(reader.is_alive() for reader in readers):
            for pipe in pipes:
                try:
                    pipe.close()
                except OSError:
                    pass
            for reader in readers:
                reader.join(timeout=max(0.0, reader_join_deadline - clock()))
        if any(reader.is_alive() for reader in readers):
            join = {**join, "readers_joined": False}
        else:
            join = {**join, "readers_joined": True}
        if not join["waited"] or not join["group_absent"] or not join["readers_joined"]:
            raise BuildExecutionError("error-path-process-group-not-joined") from error
        if isinstance(error, BuildExecutionError):
            error.join = join  # type: ignore[attr-defined]
            raise
        wrapped = BuildExecutionError(type(error).__name__)
        wrapped.join = join  # type: ignore[attr-defined]
        raise wrapped from error
    finally:
        stop_readers.set()
        for pipe in (getattr(process, "stdout", None), getattr(process, "stderr", None)):
            if pipe is not None and not pipe.closed:
                pipe.close()
        stdout.close()
        stderr.close()


def _read_child_receipt(
    image: ImagePlan,
    config: BuildConfig,
    *,
    deadline: float,
    clock: Callable[[], float],
) -> tuple[dict[str, Any], str]:
    path = image.output_dir / "receipts" / "build-receipt.json"
    raw, _mode = _read_regular(path, MAX_RECEIPT_BYTES, deadline=deadline, clock=clock)
    receipt = _decode_json(raw)
    _check_deadline(deadline, clock, "postbuild-receipt")
    try:
        source = config.source
        capture = _validate_capture(config)
        full_rows = {name: {"sha256": digest, "mode": capture["source"]["modes"][name]}
                     for name, digest in capture["source"]["sha256"].items()}
        contract.validate_build_receipt(receipt, image.selector,
            {"commit": source.commit, "parent": source.parent, "tree": source.tree, "count": source.count,
             "capture": source.capture_sha256, "projection": source.capture_projection_sha256,
             "map": source.source_map_sha256, "raw_map": _sha256(_json_bytes(full_rows).rstrip(b"\n"))})
    except (contract.BuildContractError, BuildPreparationError) as error:
        raise BuildExecutionError("child-receipt-binding-mismatch") from error
    _check_deadline(deadline, clock, "postbuild-receipt")
    return receipt, _sha256(raw)


def _artifact_manifest(
    image_dir: pathlib.Path,
    *,
    deadline: float,
    clock: Callable[[], float],
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    total = 0
    for root_name in ("tool", "runtime"):
        _check_deadline(deadline, clock, "postbuild-artifact")
        root = image_dir / "handoff" / root_name
        if not root.is_dir() or root.is_symlink():
            raise BuildExecutionError(f"artifact-root-missing:{root_name}")
        for current, dirnames, filenames in os.walk(root, topdown=True, followlinks=False):
            _check_deadline(deadline, clock, "postbuild-artifact")
            current_path = pathlib.Path(current)
            for name in list(dirnames):
                _check_deadline(deadline, clock, "postbuild-artifact")
                path = current_path / name
                st = path.lstat()
                if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
                    raise BuildExecutionError("artifact-directory-shape")
            for name in filenames:
                _check_deadline(deadline, clock, "postbuild-artifact")
                path = current_path / name
                raw, mode = _read_regular(path, 256 * 1024 * 1024, deadline=deadline, clock=clock)
                total += len(raw)
                if total > MAX_ARTIFACT_BYTES or len(rows) >= MAX_ARTIFACT_FILES:
                    raise BuildExecutionError("artifact-tree-bound")
                rows.append(
                    {
                        "path": path.relative_to(image_dir / "handoff").as_posix(),
                        "bytes": len(raw),
                        "mode": f"{mode:04o}",
                        "sha256": _sha256(raw),
                    }
                )
    _check_deadline(deadline, clock, "postbuild-artifact")
    if not any(row["path"].startswith("tool/") for row in rows) or not any(
        row["path"].startswith("runtime/") for row in rows
    ):
        raise BuildExecutionError("artifact-tree-empty")
    result = sorted(rows, key=lambda row: row["path"])
    _check_deadline(deadline, clock, "postbuild-artifact")
    return result


def _image_binding(
    selector: str,
    config: BuildConfig,
    recipe_sha256: str,
    child_receipt_sha256: str,
    artifacts: list[dict[str, Any]],
) -> str:
    return _sha256(
        _json_bytes(
            {
                "selector": selector,
                "source_commit": config.source.commit,
                "capture_sha256": config.source.capture_sha256,
                "base_recipe_sha256": config.recipe_sha256,
                "derived_recipe_sha256": recipe_sha256,
                "build_receipt_sha256": child_receipt_sha256,
                "artifacts": artifacts,
            }
        ).rstrip(b"\n")
    )


def _make_output_tree(root: pathlib.Path) -> None:
    root.mkdir(mode=0o700)
    for name in ("images", "receipts", "logs", "recipes"):
        (root / name).mkdir(mode=0o700)


def _write_recipe(path: pathlib.Path, data: bytes) -> None:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o700)
    try:
        with os.fdopen(fd, "wb", closefd=False) as stream:
            stream.write(data)
            stream.flush()
            os.fsync(fd)
    finally:
        os.close(fd)


def _execute(plan: PreparedPlan, *, popen=None, clock=None, sleeper=None) -> dict[str, Any]:
    clock = clock or time.monotonic
    sleeper = sleeper or time.sleep
    started = clock()
    cumulative_deadline = started + plan.config.total_deadline_seconds
    work_deadline = cumulative_deadline - CLEANUP_RESERVE_SECONDS
    _check_deadline(work_deadline, clock, "cumulative")
    _make_output_tree(plan.output_root)
    _check_deadline(work_deadline, clock, "cumulative")
    for image in plan.images:
        _check_deadline(work_deadline, clock, "cumulative")
        _write_recipe(image.recipe_path, image.recipe_bytes)
    _check_deadline(work_deadline, clock, "cumulative")
    outcomes: dict[str, dict[str, Any]] = {}
    failed_selector: str | None = None
    for image in plan.images:
        image_started = clock()
        if image_started >= work_deadline:
            failed_selector = image.selector
            outcomes[image.selector] = {
                "status": "not_started",
                "reason": "cumulative-deadline",
                "native_execution": False,
                "image_sha256": None,
            }
            break
        image_deadline = min(work_deadline, image_started + plan.config.per_image_deadline_seconds)
        image_cleanup_deadline = min(cumulative_deadline,
                                     image_started + plan.config.per_image_deadline_seconds)
        image_deadline = min(image_deadline, image_cleanup_deadline - CLEANUP_RESERVE_SECONDS)
        if image_deadline <= image_started:
            failed_selector = image.selector
            outcomes[image.selector] = {
                "status": "not_started",
                "reason": "per-image-deadline-cleanup-reserve",
                "native_execution": False,
                "image_sha256": None,
            }
            break
        running = {
            "schema": "issue779-integrated16-image-receipt-v1",
            "selector": image.selector,
            "cases": list(image.cases),
            "status": "running",
            "source_commit": plan.config.source.commit,
            "capture_sha256": plan.config.source.capture_sha256,
            "base_recipe_sha256": plan.config.recipe_sha256,
            "derived_recipe_sha256": image.recipe_sha256,
            "output_dir": str(image.output_dir),
            "native_execution": False,
            "native_coverage_claim": False,
            "image_sha256": None,
        }
        try:
            _atomic_json(image.receipt_path, running, deadline=image_deadline, clock=clock)
            command = _run_child(
                image,
                plan.harness_root,
                plan.config.source.capture_path,
                image_deadline,
                image_cleanup_deadline,
                popen=popen,
                clock=clock,
                sleeper=sleeper,
            )
            _check_deadline(image_deadline, clock, "postbuild")
            child_receipt, child_sha = _read_child_receipt(
                image, plan.config, deadline=image_deadline, clock=clock
            )
            artifacts = _artifact_manifest(image.output_dir, deadline=image_deadline, clock=clock)
            binding = _image_binding(
                image.selector,
                plan.config,
                image.recipe_sha256,
                child_sha,
                artifacts,
            )
            _check_deadline(image_deadline, clock, "postbuild")
            final = {
                **running,
                "status": "build_prerequisite_complete",
                "process": command,
                "child_build_receipt_sha256": child_sha,
                "child_build_receipt_artifacts": child_receipt.get("artifacts"),
                "artifact_manifest": artifacts,
                "image_sha256": binding,
                "native_execution": False,
                "native_coverage_claim": False,
            }
            _check_deadline(image_deadline, clock, "postbuild")
            receipt_sha = _atomic_json(image.receipt_path, final, deadline=image_deadline, clock=clock)
            outcomes[image.selector] = {"status": final["status"], "receipt_sha256": receipt_sha,
                                        "image_sha256": binding, "native_execution": False}
        except BaseException as error:
            failed_selector = image.selector
            join = getattr(error, "join", None)
            final = {
                **running,
                "status": "failed",
                "failure": str(error),
                "process_group_join": join,
                "native_execution": False,
                "native_coverage_claim": False,
                "image_sha256": None,
            }
            receipt_sha = _atomic_json(image.receipt_path, final, deadline=image_cleanup_deadline, clock=clock)
            outcomes[image.selector] = {"status": "failed", "receipt_sha256": receipt_sha,
                                        "image_sha256": None, "native_execution": False,
                                        "failure": str(error), "process_group_join": join}
            break
    for image in plan.images:
        if image.selector in outcomes:
            continue
        receipt = {
            "schema": "issue779-integrated16-image-receipt-v1",
            "selector": image.selector,
            "cases": list(image.cases),
            "status": "not_started",
            "reason": f"matrix-stopped-after-{failed_selector}",
            "source_commit": plan.config.source.commit,
            "capture_sha256": plan.config.source.capture_sha256,
            "base_recipe_sha256": plan.config.recipe_sha256,
            "derived_recipe_sha256": image.recipe_sha256,
            "output_dir": str(image.output_dir),
            "native_execution": False,
            "native_coverage_claim": False,
            "image_sha256": None,
        }
        receipt_sha = _atomic_json(image.receipt_path, receipt, deadline=cumulative_deadline, clock=clock)
        outcomes[image.selector] = {"status": "not_started", "receipt_sha256": receipt_sha,
                                    "image_sha256": None, "native_execution": False}
    result = {
        "schema": "issue779-integrated16-matrix-receipt-v1",
        "source_commit": plan.config.source.commit,
        "capture_sha256": plan.config.source.capture_sha256,
        "selectors": list(SELECTORS),
        "images": outcomes,
        "status": "failed" if failed_selector is not None else "build_prerequisites_complete",
        "failed_selector": failed_selector,
        "native_execution": False,
        "native_coverage_claim": False,
        "elapsed_seconds": max(0.0, clock() - started),
        "cumulative_deadline_seconds": plan.config.total_deadline_seconds,
    }
    _atomic_json(plan.output_root / "matrix-receipt.json", result, deadline=cumulative_deadline, clock=clock)
    return result


def orchestrate(
    output_root: pathlib.Path,
    harness_root: pathlib.Path,
    *,
    execute: bool = False,
    config: BuildConfig = DEFAULT_CONFIG,
) -> dict[str, Any]:
    """Validate all inputs and return a matrix plan; execute only by explicit opt-in.

    ``execute=True`` runs the fourteen complete publish recipes sequentially. Each
    child is isolated in a new process group, bounded by one shared cumulative
    deadline, and joined on every failure. This function was not called with
    ``execute=True`` during preparation.
    """
    plan = _prepare(config, harness_root, output_root)
    if not execute:
        return _public_plan(plan)
    if EXECUTION_CLEARED_RECIPE_SHA256 is None or config.recipe_sha256 != EXECUTION_CLEARED_RECIPE_SHA256:
        raise BuildExecutionError("pinned-base-recipe-review-pending")
    if SOURCE_BINDING_STATUS != "current_parent_capture_reviewed":
        raise BuildExecutionError("pinned-source-capture-review-pending")
    return _execute(plan)


def main(argv: list[str] | None = None) -> int:
    import argparse

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--harness-root", required=True, type=pathlib.Path)
    parser.add_argument("--output-root", required=True, type=pathlib.Path)
    parser.add_argument("--execute", action="store_true", help="run all fourteen builds; default is plan-only")
    args = parser.parse_args(argv)
    try:
        result = orchestrate(args.output_root, args.harness_root, execute=args.execute)
    except (BuildPreparationError, BuildExecutionError) as error:
        print(json.dumps({"status": "blocked", "failure": str(error)}, sort_keys=True))
        return 1
    print(json.dumps(result, sort_keys=True))
    return 0 if result.get("status") not in ("failed",) else 1


if __name__ == "__main__":
    raise SystemExit(main())
