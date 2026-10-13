#!/usr/bin/env python3
"""Verify and stage one pinned integrated16 build image artifact.

This module only reads a supplied ZIP and writes a fully verified private
handoff tree. It never downloads artifacts, launches processes, or performs
build/native work. ``deadline`` is an absolute ``time.monotonic()`` deadline.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile
import zlib


SOURCE_COMMIT = "fc59bb1254294d624f6779f928020edfe988dd5e"
SOURCE_TREE = "c9ed8adcc08d050dc70af6885d31171038a36f86"
SOURCE_COUNT = 2872
CAPTURE_SHA256 = "6e46183742801e864802ab8a7f3e64cd320139a500b7b215b17befe96616d2c1"
CAPTURE_PROJECTION_SHA256 = "8457d006fa5061f7249afd6038107907557732e6a51de3d8ba151923007e3ea0"
BUILD_CONTRACT_SHA256 = "4e1e774f57d1b878a0985ac218a27490ae4de8da1f21f81cd696dbe6e9c399f8"
ARTIFACT_METADATA_SHA256 = "338d59c0a386e3b28ecae0d5af534a3e006f8b6339c950096279d7910ab0b39b"
ARTIFACT_RUN_ID = 38441424522

SELECTORS = ("None", *(f"N{i:02d}" for i in range(4, 17)))
MAX_ZIP_BYTES = 256 * 1024 * 1024
MAX_MEMBER_BYTES = 256 * 1024 * 1024
MAX_MEMBERS = 50_000
MAX_EXPANDED_BYTES = 4 * 1024 * 1024 * 1024
MAX_ROLE_BYTES = 1024 * 1024 * 1024
MAX_TOOL_RUNTIME_BYTES = 1024 * 1024 * 1024
MAX_LOG_BYTES = 8 * 1024 * 1024
MAX_ALL_LOG_BYTES = 16 * 1024 * 1024
MAX_CONTROL_BYTES = 16 * 1024 * 1024
MAX_BUILD_RECEIPT_BYTES = 4 * 1024 * 1024
MAX_NODE_BYTES = 4 * 1024 * 1024
MAX_TSV_BYTES = 1024 * 1024
MAX_TRX_BYTES = 2 * 1024 * 1024
CHUNK = 64 * 1024
HEX_SHA = re.compile(r"[0-9a-f]{64}\Z")
OCTAL_MODE = re.compile(r"0[0-7]{3}\Z")


class ImageArtifactError(ValueError):
    """A closed rejection category; messages never include artifact data."""

    def __init__(self, category: str):
        super().__init__(category)
        self.category = category


def _need(ok: bool, category: str) -> None:
    if not ok:
        raise ImageArtifactError(category)


def _check_deadline(deadline: float) -> None:
    _need(time.monotonic() < deadline, "original-deadline")


def _sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def _canonical(value: object) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def _unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        _need(key not in result, "duplicate-json-key")
        result[key] = value
    return result


def _decode_json(raw: bytes, category: str) -> dict[str, object]:
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=_unique_object)
    except ImageArtifactError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError):
        raise ImageArtifactError(category) from None
    _need(type(value) is dict, category)
    return value


def _identity(fact: os.stat_result) -> tuple[int, ...]:
    return (fact.st_dev, fact.st_ino, fact.st_mode, fact.st_uid, fact.st_gid,
            fact.st_nlink, fact.st_size, fact.st_mtime_ns, fact.st_ctime_ns)


def _read_pinned(path: Path, expected_sha: str, cap: int, deadline: float) -> bytes:
    _check_deadline(deadline)
    try:
        before = path.lstat()
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and 0 < before.st_size <= cap,
              "pinned-input-shape")
        fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    except ImageArtifactError:
        raise
    except OSError:
        raise ImageArtifactError("pinned-input-open") from None
    try:
        _need(_identity(before) == _identity(os.fstat(fd)), "pinned-input-substitution")
        chunks: list[bytes] = []
        digest = hashlib.sha256()
        size = 0
        while True:
            _check_deadline(deadline)
            block = os.read(fd, min(CHUNK, cap + 1 - size))
            _check_deadline(deadline)
            if not block:
                break
            size += len(block)
            _need(size <= cap and size <= before.st_size, "pinned-input-cap")
            digest.update(block)
            chunks.append(block)
        _need(size == before.st_size and _identity(before) == _identity(os.fstat(fd))
              == _identity(path.lstat()), "pinned-input-changed")
        _need(digest.hexdigest() == expected_sha, "pinned-input-sha256")
        return b"".join(chunks)
    finally:
        os.close(fd)


def _source_tuple(capture: dict[str, object], capture_sha: str, deadline: float) -> dict[str, object]:
    _need(capture.get("schema") == "issue779-csharp-common-source-capture-v4"
          and capture.get("validation_commit") == SOURCE_COMMIT
          and capture.get("previous_validation_commit") == "1794a99f9f9857e923fdb2cc8cc81fe0bd5c8c38"
          and capture.get("source_tree") == SOURCE_TREE
          and type(capture.get("source_count")) is int and capture["source_count"] == SOURCE_COUNT
          and capture.get("native_credit") is False
          and type(capture.get("source")) is dict, "capture-source-binding")
    source = capture["source"]
    _need(set(source) == {"sha256", "modes"}
          and type(source.get("sha256")) is dict and type(source.get("modes")) is dict
          and len(source["sha256"]) == SOURCE_COUNT and len(source["modes"]) == SOURCE_COUNT,
          "capture-source-map-shape")
    hashes = source["sha256"]
    modes = source["modes"]
    _need(set(hashes) == set(modes), "capture-source-map-set")
    for name, digest in hashes.items():
        _check_deadline(deadline)
        _need(type(name) is str and _safe_relative(name) and type(digest) is str
              and HEX_SHA.fullmatch(digest) is not None, "capture-source-map-row")
        _need(type(modes[name]) is str and OCTAL_MODE.fullmatch(modes[name]) is not None,
              "capture-source-mode")
    projection = {"validation_commit": capture["validation_commit"],
                  "previous_validation_commit": capture["previous_validation_commit"],
                  "source_count": capture["source_count"], "tree": capture["source_tree"],
                  "source": source}
    projection_sha = _sha(_canonical(projection))
    _need(projection_sha == CAPTURE_PROJECTION_SHA256, "capture-projection-pin")
    map_sha = _sha(_canonical(source))
    raw_map = {name: {"sha256": hashes[name], "mode": modes[name]} for name in hashes}
    raw_map_sha = _sha(_canonical(raw_map))
    return {"commit": SOURCE_COMMIT, "parent": capture["previous_validation_commit"],
            "tree": SOURCE_TREE, "count": SOURCE_COUNT, "capture": capture_sha,
            "projection": projection_sha, "map": map_sha, "raw_map": raw_map_sha}


def _load_pins(deadline: float) -> tuple[dict[str, object], dict[str, object], dict[str, object]]:
    root = Path(__file__).resolve().parent
    capture_raw = _read_pinned(root / "capture-receipt.json", CAPTURE_SHA256,
                               2 * 1024 * 1024, deadline)
    contract_raw = _read_pinned(root / "integrated16_build_contract.py", BUILD_CONTRACT_SHA256,
                                 64 * 1024, deadline)
    metadata_raw = _read_pinned(root / "image-artifact-metadata.json", ARTIFACT_METADATA_SHA256,
                                128 * 1024, deadline)
    capture = _decode_json(capture_raw, "capture-json")
    source = _source_tuple(capture, _sha(capture_raw), deadline)
    metadata = _decode_json(metadata_raw, "artifact-metadata-json")
    _need(metadata.get("run_id") == ARTIFACT_RUN_ID and metadata.get("total_count") == 14
          and metadata.get("downloaded") is False and type(metadata.get("artifacts")) is list
          and len(metadata["artifacts"]) == 14, "artifact-metadata-shape")
    by_selector: dict[str, dict[str, object]] = {}
    for row in metadata["artifacts"]:
        _need(type(row) is dict, "artifact-metadata-row")
        name = row.get("name")
        match = re.fullmatch(
            rf"issue779-integrated-image-(None|N\d{{2}})-{ARTIFACT_RUN_ID}-1", name or "")
        _need(match is not None, "artifact-metadata-name")
        selector = match.group(1)
        _need(selector in SELECTORS and selector not in by_selector
              and type(row.get("id")) is int and row["id"] > 0
              and type(row.get("size_in_bytes")) is int
              and 0 < row["size_in_bytes"] <= MAX_ZIP_BYTES
              and type(row.get("digest")) is str
              and re.fullmatch(r"sha256:[0-9a-f]{64}", row["digest"]) is not None
              and row.get("expired") is False, "artifact-metadata-fields")
        by_selector[selector] = row
    _need(set(by_selector) == set(SELECTORS), "artifact-metadata-selector-set")
    namespace: dict[str, object] = {"__name__": "_pinned_integrated16_build_contract"}
    try:
        exec(compile(contract_raw, "<pinned-integrated16-build-contract>", "exec"), namespace)
    except Exception:
        raise ImageArtifactError("build-contract-load") from None
    _need(namespace.get("SCHEMA") == "issue779-integrated16-build-v1"
          and callable(namespace.get("validate_build_receipt"))
          and callable(namespace.get("expected_regressions")), "build-contract-api")
    return source, by_selector, {"contract": namespace, "capture": capture}


def _safe_relative(name: str) -> bool:
    if type(name) is not str or not name or "\x00" in name or "\\" in name:
        return False
    if name.startswith("/") or name.endswith("/") or ":" in name.split("/", 1)[0]:
        return False
    parts = name.split("/")
    return all(part not in ("", ".", "..") for part in parts)


def _safe_member_name(name: str, is_dir: bool) -> tuple[str, bool]:
    _need(type(name) is str and len(name.encode("utf-8", "strict")) <= 4096
          and "\x00" not in name and "\\" not in name, "archive-member-name")
    trailing = name.endswith("/")
    _need(not trailing or is_dir, "archive-member-type-name")
    normalized = name[:-1] if trailing else name
    _need(_safe_relative(normalized), "archive-member-path")
    return normalized, is_dir or trailing


def _zip_mode(info: zipfile.ZipInfo, is_dir: bool) -> tuple[int, int]:
    mode = (info.external_attr >> 16) & 0xFFFF
    kind = stat.S_IFMT(mode)
    if is_dir:
        _need(kind in (0, stat.S_IFDIR), "archive-special-file-type")
    else:
        _need(kind in (0, stat.S_IFREG), "archive-special-file-type")
    _need(not info.flag_bits & 1, "archive-encrypted-member")
    return kind, stat.S_IMODE(mode)


def _scan_archive(zf: zipfile.ZipFile, deadline: float) -> tuple[dict[str, zipfile.ZipInfo], dict[str, bool], int]:
    _check_deadline(deadline)
    try:
        rows = zf.infolist()
    except (OSError, zipfile.BadZipFile, RuntimeError):
        raise ImageArtifactError("archive-directory-read") from None
    _check_deadline(deadline)
    _need(0 < len(rows) <= MAX_MEMBERS, "archive-member-count")
    infos: dict[str, zipfile.ZipInfo] = {}
    dirs: dict[str, bool] = {}
    expanded = 0
    for info in rows:
        _check_deadline(deadline)
        original_name = getattr(info, "orig_filename", info.filename)
        _need(original_name == info.filename, "archive-member-name-truncation")
        normalized, is_dir = _safe_member_name(original_name, info.is_dir())
        _need(normalized not in infos, "archive-duplicate-normalized-name")
        _zip_mode(info, is_dir)
        _need(type(info.file_size) is int and type(info.compress_size) is int
              and 0 <= info.file_size <= MAX_MEMBER_BYTES
              and 0 <= info.compress_size <= MAX_ZIP_BYTES, "archive-member-size")
        expanded += info.file_size
        _need(expanded <= MAX_EXPANDED_BYTES, "archive-expanded-size")
        infos[normalized] = info
        dirs[normalized] = is_dir
    return infos, dirs, expanded


def _read_member(zf: zipfile.ZipFile, info: zipfile.ZipInfo, cap: int, deadline: float) -> bytes:
    _need(info.file_size <= cap, "selected-member-cap")
    result = bytearray()
    try:
        with zf.open(info, "r") as source:
            while True:
                _check_deadline(deadline)
                block = source.read(min(CHUNK, cap + 1 - len(result)))
                _check_deadline(deadline)
                if not block:
                    break
                result.extend(block)
                _need(len(result) <= cap and len(result) <= info.file_size, "selected-member-growth")
    except ImageArtifactError:
        raise
    except (OSError, EOFError, RuntimeError, zipfile.BadZipFile, zlib.error):
        raise ImageArtifactError("selected-member-read") from None
    _need(len(result) == info.file_size, "selected-member-length")
    return bytes(result)


def _validate_node_map(role: str, nodes_raw: bytes, tsv_raw: bytes,
                       capture: dict[str, object] | None, expected_summary: dict[str, object],
                       deadline: float) -> dict[str, object]:
    _need(_sha(nodes_raw) == expected_summary.get("nodes_sha256")
          and _sha(tsv_raw) == expected_summary.get("tsv_sha256"), "handoff-map-sha256")
    nodes = _decode_json(nodes_raw, "handoff-node-json")
    _need(set(nodes) == {"schema", "root_name", "files", "directories"}
          and nodes.get("schema") == "issue779-build-node-inventory-v1"
          and nodes.get("root_name") == role
          and type(nodes.get("files")) is dict and type(nodes.get("directories")) is dict,
          "handoff-node-schema")
    files = nodes["files"]
    directories = nodes["directories"]
    _need(type(expected_summary.get("file_count")) is int
          and type(expected_summary.get("node_count")) is int
          and type(expected_summary.get("total_bytes")) is int
          and len(files) == expected_summary["file_count"]
          and len(files) + len(directories) == expected_summary["node_count"], "handoff-node-count")
    total = 0
    for path, fact in files.items():
        _check_deadline(deadline)
        _need(_safe_relative(path) and type(fact) is dict
              and set(fact) == {"bytes", "mode", "sha256"}
              and type(fact.get("bytes")) is int and 0 <= fact["bytes"] <= MAX_MEMBER_BYTES
              and type(fact.get("mode")) is str and OCTAL_MODE.fullmatch(fact["mode"]) is not None
              and type(fact.get("sha256")) is str and HEX_SHA.fullmatch(fact["sha256"]) is not None,
              "handoff-node-file-row")
        total += fact["bytes"]
        _need(total <= MAX_ROLE_BYTES, "handoff-role-size")
    for path, fact in directories.items():
        _check_deadline(deadline)
        _need(type(path) is str and (path == "." or _safe_relative(path))
              and type(fact) is dict and set(fact) == {"mode"}
              and type(fact.get("mode")) is str and OCTAL_MODE.fullmatch(fact["mode"]) is not None,
              "handoff-node-directory-row")
    _need("." in directories, "handoff-node-root-directory")
    expected_dir_mode = "0700" if role == "source" else "0555"
    _need(all(fact["mode"] == expected_dir_mode for fact in directories.values()),
          "handoff-directory-mode")
    for path in files:
        parts = PurePosixPath(path).parts
        _need(all("/".join(parts[:index]) in directories for index in range(1, len(parts))),
              "handoff-parent-directory-missing")
    tsv_map: dict[str, tuple[str, str]] = {}
    try:
        text = tsv_raw.decode("utf-8")
    except UnicodeDecodeError:
        raise ImageArtifactError("handoff-tsv-utf8") from None
    _need(bool(text) and text.endswith("\n") and "\r" not in text, "handoff-tsv-newline")
    previous = None
    for line in text[:-1].split("\n"):
        _check_deadline(deadline)
        fields = line.split("\t")
        _need(len(fields) == 3, "handoff-tsv-row-width")
        mode, digest, path = fields
        _need(_safe_relative(path) and OCTAL_MODE.fullmatch(mode) is not None
              and HEX_SHA.fullmatch(digest) is not None and path not in tsv_map,
              "handoff-tsv-row")
        _need(previous is None or previous < path, "handoff-tsv-order")
        previous = path
        tsv_map[path] = (mode, digest)
    _need(set(tsv_map) == set(files) and all(tsv_map[path] == (fact["mode"], fact["sha256"])
          for path, fact in files.items()), "handoff-tsv-node-mismatch")
    _need(total == expected_summary["total_bytes"], "handoff-node-total-bytes")
    if role == "source":
        _need(capture is not None and set(files) == set(capture["source"]["sha256"])
              and all(files[path]["sha256"] == capture["source"]["sha256"][path]
                      and files[path]["mode"] == capture["source"]["modes"][path]
                      for path in files), "handoff-source-capture-map")
        _need(len(files) == SOURCE_COUNT, "handoff-source-count")
    return nodes


def _validate_trx(raw: bytes, fact: dict[str, object], expected_class: str,
                  expected_method: str, deadline: float) -> None:
    _need(_sha(raw) == fact.get("trx_sha256"), "selected-trx-sha256")
    _check_deadline(deadline)
    try:
        root = ET.fromstring(raw)
    except (ET.ParseError, ValueError):
        raise ImageArtifactError("selected-trx-xml") from None
    _check_deadline(deadline)
    counters = next((node for node in root.iter() if node.tag.endswith("Counters")), None)
    _need(counters is not None, "selected-trx-counters")
    for key, wanted in (("total", "1"), ("executed", "1"), ("passed", "1"),
                        ("failed", "0"), ("error", "0"), ("notExecuted", "0"),
                        ("timeout", "0"), ("aborted", "0")):
        _need(counters.attrib.get(key) == wanted, "selected-trx-outcome")
    expected_test_name = expected_class + "." + expected_method
    definitions_sections = [node for node in root.iter()
                           if node.tag.rsplit("}", 1)[-1] == "TestDefinitions"]
    _need(len(definitions_sections) == 1, "selected-trx-definitions")
    definitions = list(definitions_sections[0])
    _need(len(definitions) == 1
          and definitions[0].tag.rsplit("}", 1)[-1] == "UnitTest",
          "selected-trx-definition")
    definition = definitions[0]
    definition_id = definition.attrib.get("id")
    _need(type(definition_id) is str and bool(definition_id.strip()),
          "selected-trx-definition-id")
    methods = [node for node in definition.iter()
               if node.tag.rsplit("}", 1)[-1] == "TestMethod"]
    _need(len(methods) == 1
          and methods[0].attrib.get("className") == expected_class
          and methods[0].attrib.get("name") == expected_method,
          "selected-trx-definition-method")

    result_sections = [node for node in root.iter()
                       if node.tag.rsplit("}", 1)[-1] == "Results"]
    _need(len(result_sections) == 1, "selected-trx-results")
    results = list(result_sections[0])
    _need(len(results) == 1
          and results[0].tag.rsplit("}", 1)[-1] == "UnitTestResult",
          "selected-trx-result")
    result = results[0]
    _need(result.attrib.get("testId") == definition_id,
          "selected-trx-result-definition-id")
    _need(result.attrib.get("testName") == expected_test_name
          and result.attrib.get("outcome") == "Passed", "selected-trx-test")


def _member_set_for_role(role: str, infos: dict[str, zipfile.ZipInfo],
                         kinds: dict[str, bool], nodes: dict[str, object],
                         deadline: float) -> None:
    prefix = f"image/handoff/{role}/"
    actual_files = {name[len(prefix):] for name, is_dir in kinds.items()
                    if name.startswith(prefix) and not is_dir}
    _need(actual_files == set(nodes["files"]), f"archive-node-file-set-{role}")
    for name, is_dir in kinds.items():
        _check_deadline(deadline)
        if not name.startswith(prefix) or not is_dir:
            continue
        relative = name[len(prefix):]
        relative = relative[:-1] if relative.endswith("/") else relative
        node_path = relative or "."
        fact = nodes["directories"].get(node_path)
        _need(type(fact) is dict, f"archive-node-directory-set-{role}")
        _, mode = _zip_mode(infos[name], True)
        _need(mode == int(fact["mode"], 8), f"archive-node-directory-mode-{role}")


def _validate_zip_node_mode(info: zipfile.ZipInfo, expected_mode: str) -> None:
    kind, actual_mode = _zip_mode(info, False)
    _need(kind in (0, stat.S_IFREG) and actual_mode == int(expected_mode, 8),
          "archive-node-file-mode")


def _receipt_logs(receipt: dict[str, object], prefix: str, kind: str,
                  infos: dict[str, zipfile.ZipInfo], deadline: float) -> list[tuple[str, dict[str, object], zipfile.ZipInfo]]:
    commands = receipt.get("commands")
    _need(type(commands) is list and commands, f"{kind}-commands-shape")
    result = []
    seen = set()
    total = 0
    for command in commands:
        _check_deadline(deadline)
        _need(type(command) is dict and type(command.get("logs")) is list,
              f"{kind}-command-logs")
        _need(len(command["logs"]) == 2, f"{kind}-command-log-count")
        for log in command["logs"]:
            _need(type(log) is dict and set(log) == {"name", "bytes", "sha256"}
                  and type(log.get("name")) is str and re.fullmatch(r"[0-9]{2}-(?:stdout|stderr)\.log", log["name"])
                  and type(log.get("bytes")) is int and 0 <= log["bytes"] <= MAX_LOG_BYTES
                  and type(log.get("sha256")) is str and HEX_SHA.fullmatch(log["sha256"]) is not None,
                  f"{kind}-log-record")
            _need(log["name"] not in seen, f"{kind}-log-duplicate")
            seen.add(log["name"])
            total += log["bytes"]
            _need(total <= MAX_ALL_LOG_BYTES, "all-command-logs-cap")
            path = prefix + log["name"]
            info = infos.get(path)
            _need(info is not None, "command-log-member-missing")
            _need(info.file_size == log["bytes"], "command-log-size")
            result.append((path, log, info))
    return result


def _image_receipt_path(selector: str) -> str:
    _need(type(selector) is str and selector in SELECTORS, "selector-closed-set")
    return ("orchestration/receipts/ordinary.json" if selector == "None"
            else f"orchestration/receipts/{selector.lower()}.json")


def _image_receipt_cases_match(image_receipt: object, selector: str) -> bool:
    """Bind the selected image to its exact fixture-case list.

    The ordinary image selector is named ``None`` in the build matrix, while
    its receipt records the three ordinary fixture cases it contains.
    Individual N04-N16 images continue to bind exactly one matching case.
    """
    if type(selector) is not str or selector not in SELECTORS or type(image_receipt) is not dict:
        return False
    expected = ["N01", "N02", "N03"] if selector == "None" else [selector]
    return image_receipt.get("cases") == expected


def _validate_runtime_receipt(runtime: dict[str, object], runtime_nodes: dict[str, object],
                              deadline: float) -> None:
    phases = ("ubuntu-signed-package-index", "ubuntu-runtime-candidate-metadata",
              "fixed-ubuntu-runtime-install", "installed-package-data", "installed-package-data")
    _need(runtime.get("schema") == "issue779-ubuntu-runtime-prerequisite-v1"
          and type(runtime.get("exit")) is int and runtime["exit"] == 0
          and runtime.get("native_csharp_dispatched") is False
          and runtime.get("runtime_root") == "/usr/lib/dotnet"
          and runtime.get("runtime_host") == "/usr/lib/dotnet/dotnet"
          and type(runtime.get("runtime_host_sha256")) is str
          and HEX_SHA.fullmatch(runtime["runtime_host_sha256"]) is not None
          and type(runtime.get("elapsed_seconds")) in (int, float)
          and math.isfinite(runtime["elapsed_seconds"]) and runtime["elapsed_seconds"] >= 0,
          "runtime-receipt-terminal")
    runtime_files = runtime_nodes.get("files")
    _need(type(runtime_files) is dict and "dotnet" in runtime_files
          and runtime.get("runtime_host_sha256") == runtime_files["dotnet"].get("sha256"),
          "runtime-host-node-binding")
    package_names = {"aspnetcore-runtime-10.0", "dotnet-host-10.0",
                     "dotnet-hostfxr-10.0", "dotnet-runtime-10.0"}
    packages = runtime.get("packages")
    package_version = runtime.get("package_version")
    _need(type(package_version) is str and package_version
          and type(packages) is dict and set(packages) == package_names,
          "runtime-package-set")
    for name, package in packages.items():
        _check_deadline(deadline)
        _need(type(package) is dict and set(package) == {"architecture", "status", "version"}
              and package.get("architecture") == "amd64" and package.get("status") == "ii "
              and package.get("version") == package_version, "runtime-package-row")
    for key in ("prerequisite_script_sha256", "runner_sha256"):
        _need(type(runtime.get(key)) is str and HEX_SHA.fullmatch(runtime[key]) is not None,
              "runtime-input-binding")
    commands = runtime.get("commands")
    _need(type(commands) is list and len(commands) == len(phases), "runtime-command-count")
    for ordinal, (command, phase) in enumerate(zip(commands, phases)):
        _check_deadline(deadline)
        _need(type(command) is dict and type(command.get("ordinal")) is int
              and command["ordinal"] == ordinal and command.get("phase") == phase
              and type(command.get("exit")) is int and command["exit"] == 0
              and command.get("failure") is None and command.get("waited") is True
              and command.get("group_absent") is True and command.get("timed_out") is False
              and command.get("forced_cleanup") is False and type(command.get("logs")) is list
              and len(command["logs"]) == 2, "runtime-command-terminal")
        root_guard = ordinal in (0, 2)
        for field in ("root_start_guard_closed", "root_start_guard_created",
                      "root_utility_cgroup_empty"):
            _need(command.get(field) is (True if root_guard else None),
                  "runtime-command-root-guard")
        unit = command.get("root_utility_unit")
        if root_guard:
            expected_suffix = f"-{ordinal:02d}.service"
            _need(type(unit) is str and re.fullmatch(
                r"issue779-native-tool-[0-9a-f]{32}" + re.escape(expected_suffix), unit) is not None,
                "runtime-command-root-unit")
        else:
            _need(unit is None, "runtime-command-root-unit")


def _validate_output_parent(root: Path) -> Path:
    _need(root.is_absolute() and root.name not in ("", ".", "..")
          and ".." not in root.parts, "output-root-absolute")
    try:
        root.lstat()
    except FileNotFoundError:
        pass
    except OSError:
        raise ImageArtifactError("output-root-stat") from None
    else:
        raise ImageArtifactError("output-root-not-fresh")
    try:
        canonical = root.resolve(strict=False)
    except OSError:
        raise ImageArtifactError("output-root-canonicalize") from None
    try:
        parent_st = canonical.parent.lstat()
    except FileNotFoundError:
        raise ImageArtifactError("output-parent-missing") from None
    except OSError:
        raise ImageArtifactError("output-root-stat") from None
    _need(stat.S_ISDIR(parent_st.st_mode)
          and not stat.S_ISLNK(parent_st.st_mode) and parent_st.st_uid == os.geteuid()
          and stat.S_IMODE(parent_st.st_mode) & 0o022 == 0, "output-parent-private-directory")
    _need(not os.path.lexists(canonical), "output-root-not-fresh")
    return canonical


def _mkdirs(root: Path, relative: str, directories: dict[str, object]) -> None:
    path = root / relative
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    st = path.lstat()
    _need(stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode),
          "staging-directory-shape")


def _write_bytes(path: Path, raw: bytes, mode: int, deadline: float) -> str:
    _check_deadline(deadline)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC
    try:
        fd = os.open(path, flags, 0o600)
    except OSError:
        raise ImageArtifactError("staging-file-create") from None
    digest = hashlib.sha256()
    written = 0
    try:
        view = memoryview(raw)
        while written < len(raw):
            _check_deadline(deadline)
            count = os.write(fd, view[written:written + CHUNK])
            _check_deadline(deadline)
            _need(count > 0, "staging-file-write")
            digest.update(view[written:written + count])
            written += count
        os.fchmod(fd, mode)
        os.fsync(fd)
        st = os.fstat(fd)
        _need(stat.S_ISREG(st.st_mode) and st.st_nlink == 1 and st.st_size == len(raw)
              and stat.S_IMODE(st.st_mode) == mode, "staging-file-terminal")
    finally:
        os.close(fd)
    _check_deadline(deadline)
    return digest.hexdigest()


def _stream_member(zf: zipfile.ZipFile, info: zipfile.ZipInfo, path: Path,
                   expected_bytes: int, expected_sha: str, mode: int,
                   deadline: float) -> None:
    _need(info.file_size == expected_bytes, "archive-node-file-length")
    digest = hashlib.sha256()
    size = 0
    try:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    except OSError:
        raise ImageArtifactError("staging-file-create") from None
    try:
        with zf.open(info, "r") as source:
            while True:
                _check_deadline(deadline)
                block = source.read(CHUNK)
                _check_deadline(deadline)
                if not block:
                    break
                size += len(block)
                _need(size <= expected_bytes and size <= MAX_MEMBER_BYTES, "archive-node-file-growth")
                digest.update(block)
                view = memoryview(block)
                offset = 0
                while offset < len(block):
                    _check_deadline(deadline)
                    count = os.write(fd, view[offset:])
                    _check_deadline(deadline)
                    _need(count > 0, "staging-file-write")
                    offset += count
        _need(size == expected_bytes and digest.hexdigest() == expected_sha,
              "archive-node-file-content")
        _check_deadline(deadline)
        os.fchmod(fd, mode)
        os.fsync(fd)
        _check_deadline(deadline)
        st = os.fstat(fd)
        _need(stat.S_ISREG(st.st_mode) and st.st_nlink == 1 and st.st_size == expected_bytes
              and stat.S_IMODE(st.st_mode) == mode, "staging-file-terminal")
    except ImageArtifactError:
        raise
    except (OSError, EOFError, RuntimeError, zipfile.BadZipFile, zlib.error):
        raise ImageArtifactError("archive-node-file-read") from None
    finally:
        os.close(fd)


def _cleanup_tree(path: Path) -> None:
    try:
        if path.is_symlink() or not path.exists():
            return
        for current, dirs, files in os.walk(path, topdown=True, followlinks=False):
            base = Path(current)
            for name in dirs:
                child = base / name
                if child.is_symlink():
                    child.unlink()
                else:
                    os.chmod(child, 0o700, follow_symlinks=False)
            for name in files:
                child = base / name
                if child.is_symlink() or stat.S_ISREG(child.lstat().st_mode):
                    child.unlink()
        os.chmod(path, 0o700, follow_symlinks=False)
        shutil.rmtree(path)
    except OSError:
        # The caller still receives the original closed failure. A cleanup
        # failure is observable because the stage directory remains on disk.
        raise ImageArtifactError("staging-cleanup-failed") from None


def _verify_staged_tree(root: Path, expected: dict[str, tuple[int, str, int]],
                        expected_dirs: dict[str, int], deadline: float) -> None:
    _check_deadline(deadline)
    actual_files: set[str] = set()
    actual_dirs: set[str] = set()
    for current, dirs, files in os.walk(root, topdown=True, followlinks=False):
        _check_deadline(deadline)
        base = Path(current)
        for name in dirs:
            child = base / name
            st = child.lstat()
            _need(stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode),
                  "staged-directory-shape")
            actual_dirs.add(child.relative_to(root).as_posix())
        for name in files:
            child = base / name
            st = child.lstat()
            _need(stat.S_ISREG(st.st_mode) and not stat.S_ISLNK(st.st_mode) and st.st_nlink == 1,
                  "staged-file-shape")
            rel = child.relative_to(root).as_posix()
            actual_files.add(rel)
            _check_deadline(deadline)
            digest = hashlib.sha256()
            size = 0
            try:
                fd = os.open(child, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
            except OSError:
                raise ImageArtifactError("staged-file-open") from None
            try:
                opened = os.fstat(fd)
                _need(_identity(opened) == _identity(st), "staged-file-substitution")
                while True:
                    _check_deadline(deadline)
                    block = os.read(fd, CHUNK)
                    _check_deadline(deadline)
                    if not block:
                        break
                    size += len(block)
                    digest.update(block)
                _need(_identity(os.fstat(fd)) == _identity(st), "staged-file-changed")
            finally:
                os.close(fd)
            _need(rel in expected and size == expected[rel][0]
                  and digest.hexdigest() == expected[rel][1]
                  and stat.S_IMODE(st.st_mode) == expected[rel][2],
                  "staged-file-verify")
    _need(actual_files == set(expected), "staged-file-set")
    _need(actual_dirs == set(expected_dirs) - {"."}, "staged-directory-set")
    for rel in actual_dirs:
        _check_deadline(deadline)
        st = (root / rel).lstat()
        _need(stat.S_IMODE(st.st_mode) == expected_dirs[rel], "staged-directory-mode")


def stage_image(zip_path: str | os.PathLike[str], expected_size: int, expected_digest: str,
                selector: str, output_root: str | os.PathLike[str], deadline: float) -> dict[str, object]:
    """Validate and atomically stage one image artifact for the native reader.

    The receipt and handoff data are materialized only after the archive's
    pinned digest, build contract, source capture, maps, mode records, logs,
    regression TRX files, and every source/tool/runtime payload byte pass.
    On any failure the requested output path remains absent.
    """
    _need(type(selector) is str and selector in SELECTORS, "selector-closed-set")
    _need(type(expected_size) is int and 0 < expected_size <= MAX_ZIP_BYTES,
          "artifact-size-argument")
    _need(type(expected_digest) is str and re.fullmatch(r"sha256:[0-9a-f]{64}", expected_digest) is not None,
          "artifact-digest-argument")
    _need(type(deadline) in (int, float) and deadline > time.monotonic(), "deadline-argument")
    deadline = float(deadline)
    _check_deadline(deadline)
    source_tuple, artifacts_by_selector, pins = _load_pins(deadline)
    metadata = artifacts_by_selector[selector]
    _need(metadata["size_in_bytes"] == expected_size and metadata["digest"] == expected_digest,
          "artifact-metadata-argument-binding")
    root = _validate_output_parent(Path(output_root))

    archive = Path(zip_path)
    try:
        before = archive.lstat()
        _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
              and before.st_size == expected_size and before.st_size <= MAX_ZIP_BYTES,
              "artifact-zip-shape")
        fd = os.open(archive, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    except ImageArtifactError:
        raise
    except OSError:
        raise ImageArtifactError("artifact-zip-open") from None

    staging: Path | None = None
    published = False
    try:
        _need(_identity(before) == _identity(os.fstat(fd)), "artifact-zip-substitution")
        digest = hashlib.sha256()
        size = 0
        while True:
            _check_deadline(deadline)
            block = os.read(fd, CHUNK)
            _check_deadline(deadline)
            if not block:
                break
            size += len(block)
            _need(size <= expected_size, "artifact-zip-growth")
            digest.update(block)
        _need(size == expected_size and digest.hexdigest() == expected_digest[7:],
              "artifact-zip-sha256")
        _need(_identity(before) == _identity(os.fstat(fd)) == _identity(archive.lstat()),
              "artifact-zip-changed")
        os.lseek(fd, 0, os.SEEK_SET)
        zip_file = os.fdopen(os.dup(fd), "rb", closefd=True)
        try:
            with zipfile.ZipFile(zip_file, "r") as zf:
                infos, kinds, _expanded = _scan_archive(zf, deadline)

                matrix_path = "orchestration/matrix-receipt.json"
                image_path = _image_receipt_path(selector)
                build_path = "image/receipts/build-receipt.json"
                runtime_path = "issue779-csharp-ubuntu-runtime/receipt.json"
                role_paths = {role: (f"image/handoff/{role}-nodes.json", f"image/handoff/{role}.tsv")
                              for role in ("source", "tool", "runtime")}
                fixed_paths = [matrix_path, image_path, build_path, runtime_path]
                for path in fixed_paths:
                    _need(path in infos and not kinds[path], "required-image-receipt-missing")
                build_raw = _read_member(zf, infos[build_path], MAX_BUILD_RECEIPT_BYTES, deadline)
                build = _decode_json(build_raw, "build-receipt-json")
                matrix_raw = _read_member(zf, infos[matrix_path], 2 * 1024 * 1024, deadline)
                matrix = _decode_json(matrix_raw, "matrix-receipt-json")
                image_raw = _read_member(zf, infos[image_path], 4 * 1024 * 1024, deadline)
                image_receipt = _decode_json(image_raw, "image-receipt-json")
                runtime_raw = _read_member(zf, infos[runtime_path], 2 * 1024 * 1024, deadline)
                runtime = _decode_json(runtime_raw, "runtime-receipt-json")
                contract = pins["contract"]
                try:
                    contract["validate_build_receipt"](build, selector, source_tuple)
                except Exception:
                    raise ImageArtifactError("build-receipt-contract") from None
                build_sha = _sha(build_raw)
                _need(matrix.get("schema") == "issue779-integrated16-matrix-receipt-v1"
                      and matrix.get("status") == "build_prerequisites_complete"
                      and matrix.get("failed_selector") is None
                      and matrix.get("selectors") == list(SELECTORS)
                      and matrix.get("source_commit") == SOURCE_COMMIT
                      and matrix.get("capture_sha256") == CAPTURE_SHA256
                      and matrix.get("native_execution") is False
                      and matrix.get("native_coverage_claim") is False
                      and type(matrix.get("images")) is dict
                      and set(matrix["images"]) == {selector}, "matrix-receipt-binding")
                image_binding = matrix["images"][selector]
                _need(type(image_binding) is dict
                      and image_binding.get("status") == "build_prerequisite_complete"
                      and image_binding.get("native_execution") is False
                      and image_binding.get("receipt_sha256") == _sha(image_raw)
                      and image_binding.get("image_sha256") == image_receipt.get("image_sha256"),
                      "matrix-image-receipt-binding")
                _need(image_receipt.get("schema") == "issue779-integrated16-image-receipt-v1"
                      and image_receipt.get("selector") == selector
                      and _image_receipt_cases_match(image_receipt, selector)
                      and image_receipt.get("status") == "build_prerequisite_complete"
                      and image_receipt.get("source_commit") == SOURCE_COMMIT
                      and image_receipt.get("capture_sha256") == CAPTURE_SHA256
                      and image_receipt.get("native_execution") is False
                      and image_receipt.get("native_coverage_claim") is False
                      and type(image_receipt.get("process")) is dict
                      and type(image_receipt["process"].get("exit")) is int
                      and image_receipt["process"]["exit"] == 0
                      and image_receipt["process"].get("waited") is True
                      and image_receipt["process"].get("group_absent") is True
                      and image_receipt.get("child_build_receipt_sha256") == build_sha
                      and image_receipt.get("child_build_receipt_artifacts") == build.get("artifacts"),
                      "image-receipt-binding")
                small_controls: dict[str, bytes] = {
                    "receipts/build-receipt.json": build_raw,
                    "receipts/matrix-receipt.json": matrix_raw,
                    "receipts/image-receipt.json": image_raw,
                    "receipts/runtime-receipt.json": runtime_raw,
                }
                nonlog_total = sum(map(len, (build_raw, matrix_raw, image_raw, runtime_raw)))
                node_docs: dict[str, dict[str, object]] = {}
                for role, (nodes_path, tsv_path) in role_paths.items():
                    _need(nodes_path in infos and tsv_path in infos
                          and not kinds[nodes_path] and not kinds[tsv_path], "handoff-map-missing")
                    nodes_raw = _read_member(zf, infos[nodes_path], MAX_NODE_BYTES, deadline)
                    tsv_raw = _read_member(zf, infos[tsv_path], MAX_TSV_BYTES, deadline)
                    nonlog_total += len(nodes_raw) + len(tsv_raw)
                    nodes = _validate_node_map(role, nodes_raw, tsv_raw,
                                               pins["capture"] if role == "source" else None,
                                               build["artifacts"][role], deadline)
                    node_docs[role] = nodes
                    small_controls[f"handoff/{role}-nodes.json"] = nodes_raw
                    small_controls[f"handoff/{role}.tsv"] = tsv_raw
                _need(nonlog_total <= MAX_CONTROL_BYTES, "selected-control-bytes-cap")

                for role in ("source", "tool", "runtime"):
                    _member_set_for_role(role, infos, kinds, node_docs[role], deadline)
                _validate_runtime_receipt(runtime, node_docs["runtime"], deadline)
                tool_runtime_bytes = (build["artifacts"]["tool"]["total_bytes"]
                                      + build["artifacts"]["runtime"]["total_bytes"])
                _need(tool_runtime_bytes <= MAX_TOOL_RUNTIME_BYTES, "tool-runtime-total-cap")

                artifact_rows = image_receipt.get("artifact_manifest")
                _need(type(artifact_rows) is list and len(artifact_rows) ==
                      len(node_docs["tool"]["files"]) + len(node_docs["runtime"]["files"]),
                      "image-artifact-manifest-count")
                expected_manifest = []
                for role in ("runtime", "tool"):
                    for rel, fact in node_docs[role]["files"].items():
                        expected_manifest.append({"bytes": fact["bytes"], "mode": fact["mode"],
                                                  "path": f"{role}/{rel}", "sha256": fact["sha256"]})
                expected_manifest.sort(key=lambda item: item["path"])
                _need(artifact_rows == expected_manifest, "image-artifact-manifest-map")
                regressions = contract["expected_regressions"](selector)
                trx_records = []
                for key, class_name, method in regressions:
                    trx_path = f"image/receipts/{key}/selected.trx"
                    _need(trx_path in infos and not kinds[trx_path], "selected-trx-missing")
                    raw = _read_member(zf, infos[trx_path], MAX_TRX_BYTES, deadline)
                    _validate_trx(raw, build[key], class_name, method, deadline)
                    small_controls[f"receipts/trx/{key}/selected.trx"] = raw
                    nonlog_total += len(raw)
                    trx_records.append((trx_path, raw))
                _need(nonlog_total <= MAX_CONTROL_BYTES, "selected-control-bytes-cap")

                logs: list[tuple[str, dict[str, object], zipfile.ZipInfo]] = []
                logs.extend(_receipt_logs(build, "image/receipts/", "build", infos, deadline))
                logs.extend(_receipt_logs(runtime, "issue779-csharp-ubuntu-runtime/", "runtime", infos, deadline))
                _need(sum(log[1]["bytes"] for log in logs) <= MAX_ALL_LOG_BYTES,
                      "all-command-logs-cap")
                log_data: dict[str, bytes] = {}
                for member, fact, info in logs:
                    raw = _read_member(zf, info, MAX_LOG_BYTES, deadline)
                    _need(_sha(raw) == fact["sha256"] and len(raw) == fact["bytes"],
                          "command-log-binding")
                    group = "build" if member.startswith("image/receipts/") else "runtime"
                    log_data[f"receipts/logs/{group}/{fact['name']}"] = raw

                # The published image ZIP must carry every file committed by
                # its own node maps. This exact-set check catches omitted hidden
                # files before creating any output path.
                stage_items = dict(small_controls)
                stage_items.update(log_data)
                for role in ("source", "tool", "runtime"):
                    nodes = node_docs[role]
                    prefix = f"image/handoff/{role}/"
                    for rel, fact in nodes["files"].items():
                        info = infos[prefix + rel]
                        _validate_zip_node_mode(info, fact["mode"])
                _need(_identity(before) == _identity(os.fstat(fd)) == _identity(archive.lstat()),
                      "artifact-zip-changed")

                # No destination or staging directory exists until all fixed
                # receipts, contracts, maps, modes, payload sets and selected
                # receipt logs have passed validation.
                _check_deadline(deadline)
                staging = Path(tempfile.mkdtemp(prefix=".integrated16-image-stage-", dir=root.parent))
                os.chmod(staging, 0o700)
                expected_files: dict[str, tuple[int, str, int]] = {}
                expected_dirs: dict[str, int] = {}
                for role in ("source", "tool", "runtime"):
                    nodes = node_docs[role]
                    role_root = staging / "handoff" / role
                    role_root.mkdir(mode=0o700, parents=True)
                    for rel, fact in nodes["directories"].items():
                        _check_deadline(deadline)
                        rel_path = "" if rel == "." else rel
                        target = role_root if not rel_path else role_root / rel_path
                        target.mkdir(mode=0o700, parents=True, exist_ok=True)
                        out_rel = target.relative_to(staging).as_posix()
                        expected_dirs[out_rel] = int(fact["mode"], 8)
                    prefix = f"image/handoff/{role}/"
                    for rel, fact in nodes["files"].items():
                        _check_deadline(deadline)
                        target = role_root / rel
                        target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                        info = infos[prefix + rel]
                        _stream_member(zf, info, target, fact["bytes"], fact["sha256"],
                                       int(fact["mode"], 8), deadline)
                        expected_files[target.relative_to(staging).as_posix()] = (
                            fact["bytes"], fact["sha256"], int(fact["mode"], 8))
                    for rel, fact in nodes["directories"].items():
                        target = role_root if rel == "." else role_root / rel
                        out_rel = target.relative_to(staging).as_posix()
                        if out_rel != ".":
                            expected_dirs[out_rel] = int(fact["mode"], 8)
                for relative, raw in stage_items.items():
                    _check_deadline(deadline)
                    target = staging / relative
                    target.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                    digest = _write_bytes(target, raw, 0o600, deadline)
                    expected_files[relative] = (len(raw), digest, 0o600)
                    expected_dirs.setdefault(target.parent.relative_to(staging).as_posix(), 0o700)
                    parent = target.parent
                    while parent != staging:
                        expected_dirs.setdefault(parent.relative_to(staging).as_posix(), 0o700)
                        parent = parent.parent
                marker = {
                    "schema": "issue779-integrated16-staged-image-v1",
                    "status": "verified-and-staged",
                    "artifact_id": metadata["id"],
                    "artifact_sha256": expected_digest[7:],
                    "source_commit": SOURCE_COMMIT,
                    "source_tree": SOURCE_TREE,
                    "source_count": SOURCE_COUNT,
                    "capture_sha256": CAPTURE_SHA256,
                    "selector": selector,
                    "build_receipt_sha256": build_sha,
                    "image_receipt_sha256": _sha(image_raw),
                    "native_execution": False,
                    "native_credit": False,
                    "role_file_counts": {name: len(node_docs[name]["files"])
                                         for name in ("source", "tool", "runtime")},
                    "role_total_bytes": {name: build["artifacts"][name]["total_bytes"]
                                         for name in ("source", "tool", "runtime")},
                }
                marker_raw = _canonical(marker) + b"\n"
                marker_path = staging / "stage-image-receipt.json"
                marker_sha = _write_bytes(marker_path, marker_raw, 0o600, deadline)
                expected_files["stage-image-receipt.json"] = (len(marker_raw), marker_sha, 0o600)
                expected_dirs["."] = 0o700
                # Set all directory modes bottom-up after materialization.
                for rel in sorted((p for p in expected_dirs if p != "."),
                                  key=lambda p: (p.count("/"), p), reverse=True):
                    _check_deadline(deadline)
                    os.chmod(staging / rel, expected_dirs[rel], follow_symlinks=False)
                os.chmod(staging, 0o700, follow_symlinks=False)
                _verify_staged_tree(staging, expected_files, expected_dirs, deadline)
                _check_deadline(deadline)
                os.rename(staging, root)
                staging = None
                published = True
                _check_deadline(deadline)
                root_st = root.lstat()
                _need(stat.S_ISDIR(root_st.st_mode) and not stat.S_ISLNK(root_st.st_mode)
                      and stat.S_IMODE(root_st.st_mode) == 0o700,
                      "output-root-terminal")
                return marker
        finally:
            zip_file.close()
    except ImageArtifactError:
        if staging is not None:
            _cleanup_tree(staging)
        if published:
            _cleanup_tree(root)
        raise
    except (OSError, EOFError, RuntimeError, zipfile.BadZipFile, zipfile.LargeZipFile,
            json.JSONDecodeError, ValueError):
        if staging is not None:
            _cleanup_tree(staging)
        if published:
            _cleanup_tree(root)
        raise ImageArtifactError("image-artifact-rejected") from None
    finally:
        os.close(fd)
