"""Pure validation adapter for the N03/N07 helper handoff receipts.

This module reads only caller-supplied data. It performs no filesystem, process,
build, native, or admission operation. The expected source hashes and command
contract are copied from the frozen helper recipes.
"""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import PurePosixPath

MAX_LOG_BYTES = 8 * 1024 * 1024
MAX_HELPER_BYTES = 1024 * 1024 * 1024
MAX_HELPER_NODES = 8192
OPERATIONS = ("sdk-version", "locked-restore", "fdd-publish")
COMMAND_FIELDS = frozenset({
    "operation", "exit", "waited", "leader_exit_observed", "descendants_settled",
    "forced_cleanup", "log", "log_bytes", "overflow", "discarded_bytes",
    "read_error", "write_error", "pipe_eof", "deadline_ok", "failure", "error",
})
PRIVATE_COMMAND_FIELDS = frozenset({"operation", "argv", "resources_before", "resources_after"})
TOP_FIELDS = frozenset({
    "authority", "commands", "exit", "helper_bytes", "helper_directories",
    "helper_entry_sha256", "helper_files", "helper_nodes_sha256", "helper_root",
    "helper_tsv_sha256", "native_execution", "private_diagnostics_sha256",
    "recipe_sha256", "runtime_required", "schema", "sdk_required", "sdk_sha256",
    "source_pins",
})

SOURCE_PINS = {
    "N03": {
        "Program.cs": "02a28424a2d57c56213fae8618d50df1019986cadf89246e2ce11411a23c967a",
        "NativePeerBroker.csproj": "92da6c96d4c88fae754ece54af775a00c1d713c7e44b9a76bda7cc768773514e",
        "packages.lock.json": "a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb",
    },
    "N07": {
        "Program.cs": "059246aa8acb0feb5c1172990661fc83cd1631f07ff34a1bfe8de736414a4a5d",
        "PossibleStopRegistration.cs": "3c1e57f7030c2e2a1a54bfa2853744248bf3bad4cc71af5b94ca912a87d0d3af",
        "N07CoordinatorData.cs": "6edf029c097f1e6bf902936d7418a7622d2654ad97483a88ea5f2ec10323679d",
        "NativeRootCoordinator.csproj": "9ea64016a8932db2b62910d08a579236a8e3a2b20e2cd438a0b27bc92f5269d1",
        "packages.lock.json": "a29c6aa8cfb81874ff8bb78dc369d7416f28c9b8cc47e99592bfc019b20c41eb",
    },
}
RECIPE_SHA256 = {
    "N03": "a0188f106b75e1320aefac501c1057e66da973bda8c9e08a862bfebddd65390e",
    "N07": "cd470cbac05a4913e46828344ad54ef477926c63ec5ceea55921386ba00fa334",
}


class HelperReceiptError(ValueError):
    """Closed helper receipt rejection; messages contain no caller data."""


def _need(condition: bool, category: str) -> None:
    if not condition:
        raise HelperReceiptError(category)


def _digest(value: object) -> bool:
    return type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        _need(key not in result, "duplicate-json-key")
        result[key] = value
    return result


def decode_json(raw: bytes) -> dict:
    _need(type(raw) is bytes and 0 < len(raw) <= 1024 * 1024 and raw.endswith(b"\n"),
          "json-size-or-terminator")
    try:
        value = json.loads(raw, object_pairs_hook=_unique_pairs,
                           parse_constant=lambda _value: (_ for _ in ()).throw(HelperReceiptError("json-constant")))
    except (UnicodeError, json.JSONDecodeError) as error:
        raise HelperReceiptError("json-invalid") from error
    _need(type(value) is dict, "json-object")
    return value


def validate_command_rows(rows) -> bool:
    """Validate the exact 16-field public rows produced by the shared runner."""
    _need(type(rows) is list and len(rows) == len(OPERATIONS), "command-count")
    for index, (row, operation) in enumerate(zip(rows, OPERATIONS)):
        _need(type(row) is dict and set(row) == COMMAND_FIELDS, "command-fields")
        _need(type(row["operation"]) is str and row["operation"] == operation,
              "command-operation-order")
        _need(type(row["exit"]) is int and row["exit"] == 0, "command-exit")
        for name, expected in (
            ("waited", True), ("leader_exit_observed", True), ("descendants_settled", True),
            ("forced_cleanup", False), ("overflow", False), ("read_error", False),
            ("write_error", False), ("pipe_eof", True), ("deadline_ok", True), ("error", False),
        ):
            _need(type(row[name]) is bool and row[name] is expected, "command-settlement")
        _need(row["failure"] is None, "command-failure")
        _need(type(row["discarded_bytes"]) is int and row["discarded_bytes"] == 0,
              "command-discarded-output")
        _need(row["log"] == f"build-{index:02d}.log", "command-log-name")
        _need(type(row["log_bytes"]) is int and 0 <= row["log_bytes"] <= MAX_LOG_BYTES,
              "command-log-bound")
    return True


def _expected_argv(case: str, sdk_path: str, output_root: str) -> list[list[str]]:
    _need(case in SOURCE_PINS, "helper-case")
    sdk = PurePosixPath(sdk_path)
    root = PurePosixPath(output_root)
    _need(type(sdk_path) is str and sdk.is_absolute() and ".." not in sdk.parts
          and sdk.name == "dotnet" and "\x00" not in sdk_path, "sdk-path")
    _need(type(output_root) is str and root.is_absolute() and ".." not in root.parts
          and "\x00" not in output_root, "output-path")
    project_name = "NativePeerBroker.csproj" if case == "N03" else "NativeRootCoordinator.csproj"
    project = output_root + "/project/" + project_name
    flags = ["-p:UseAppHost=false", "-p:SelfContained=false", "-p:UseSharedCompilation=false",
             "-p:RuntimeFrameworkVersion=10.0.12", "-p:NuGetAudit=true"]
    if case == "N07":
        flags += ["-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false"]
    return [
        [sdk_path, "--version"],
        [sdk_path, "restore", project, "--locked-mode", *flags],
        [sdk_path, "publish", project, "--no-restore", "--configuration", "Release",
         "--output", output_root + "/helper", *flags],
    ]


def _validate_resource_snapshot(snapshot) -> bool:
    _need(type(snapshot) is dict and set(snapshot) == {
        "rlimits", "system_mem_available_bytes", "cgroup_memory"
    }, "diagnostics-resource-shape")
    limits = snapshot["rlimits"]
    _need(type(limits) is dict and set(limits) == {"RLIMIT_AS", "RLIMIT_DATA", "RLIMIT_FSIZE"},
          "diagnostics-rlimits")
    for limit in limits.values():
        if limit is None:
            continue
        _need(type(limit) is dict and set(limit) == {"soft_bytes", "hard_bytes"},
              "diagnostics-rlimit-shape")
        for value in limit.values():
            _need(value is None or (type(value) is int and value >= 0), "diagnostics-rlimit-value")
    available = snapshot["system_mem_available_bytes"]
    _need(available is None or (type(available) is int and available >= 0),
          "diagnostics-system-memory")
    group = snapshot["cgroup_memory"]
    if group is not None:
        _need(type(group) is dict and set(group) == {
            "path", "current_bytes", "max_bytes", "max_unlimited", "events"
        }, "diagnostics-cgroup-shape")
        _need(type(group["path"]) is str and group["path"].startswith("/")
              and ".." not in PurePosixPath(group["path"]).parts, "diagnostics-cgroup-path")
        _need(type(group["current_bytes"]) is int and group["current_bytes"] >= 0,
              "diagnostics-cgroup-current")
        _need(group["max_bytes"] is None or (type(group["max_bytes"]) is int and group["max_bytes"] >= 0),
              "diagnostics-cgroup-max")
        _need(group["max_unlimited"] is None or type(group["max_unlimited"]) is bool,
              "diagnostics-cgroup-unlimited")
        events = group["events"]
        _need(type(events) is dict and all(type(k) is str and re.fullmatch(r"[a-z_]{1,32}", k)
                                           and type(v) is int and v >= 0
                                           for k, v in events.items()), "diagnostics-cgroup-events")
    return True


def validate_private_diagnostics(raw: bytes, case: str, output_root: str,
                                 sdk_path: str | None = None) -> bool:
    """Bind the retained private argv/resource record to the three public phases."""
    value = decode_json(raw)
    expected_schema = f"issue779-{case.lower()}-helper-private-diagnostics-v1"
    _need(set(value) == {"schema", "commands"} and value["schema"] == expected_schema,
          "diagnostics-schema")
    rows = value["commands"]
    _need(type(rows) is list and len(rows) == len(OPERATIONS), "diagnostics-count")
    if sdk_path is None and type(rows[0]) is dict:
        argv0 = rows[0].get("argv")
        sdk_path = argv0[0] if type(argv0) is list and argv0 and type(argv0[0]) is str else ""
    expected_argv = _expected_argv(case, sdk_path, output_root)
    for row, operation, argv in zip(rows, OPERATIONS, expected_argv):
        _need(type(row) is dict and set(row) == PRIVATE_COMMAND_FIELDS,
              "diagnostics-command-fields")
        _need(row["operation"] == operation and row["argv"] == argv,
              "diagnostics-command-argv")
        _validate_resource_snapshot(row["resources_before"])
        if row["resources_after"] is not None:
            _validate_resource_snapshot(row["resources_after"])
    return True


def validate_helper_receipt(value: dict, case: str, recipe_sha256: str,
                            private_diagnostics_raw: bytes) -> bool:
    """Validate metadata and command lifecycle without conferring native credit.

    Existing consumers must continue checking helper.tsv/helper-nodes.json bytes,
    file membership, entry DLL hash, output custody and root transport separately.
    """
    _need(case in SOURCE_PINS, "helper-case")
    _need(type(value) is dict and set(value) == TOP_FIELDS, "receipt-fields")
    _need(value["schema"] == f"issue779-{case.lower()}-helper-build-handoff-v1",
          "receipt-schema")
    _need(type(value["exit"]) is int and value["exit"] == 0, "receipt-exit")
    _need(value["authority"] is False and value["native_execution"] is False,
          "receipt-authority")
    _need(type(recipe_sha256) is str and recipe_sha256 == RECIPE_SHA256[case]
          and value["recipe_sha256"] == recipe_sha256, "recipe-pin")
    _need(value["source_pins"] == SOURCE_PINS[case], "source-pins")
    _need(value["sdk_required"] == "10.0.401" and value["runtime_required"] == "10.0.12",
          "sdk-runtime-version")
    _need(type(value["helper_files"]) is int and value["helper_files"] > 0
          and type(value["helper_directories"]) is int and value["helper_directories"] > 0
          and value["helper_files"] + value["helper_directories"] <= MAX_HELPER_NODES,
          "helper-node-count")
    _need(type(value["helper_bytes"]) is int and 0 <= value["helper_bytes"] <= MAX_HELPER_BYTES,
          "helper-byte-count")
    helper_root = PurePosixPath(value["helper_root"]) if type(value["helper_root"]) is str else PurePosixPath(".")
    _need(helper_root.is_absolute() and helper_root.name == "helper" and ".." not in helper_root.parts,
          "helper-root")
    for name in ("helper_entry_sha256", "helper_nodes_sha256", "helper_tsv_sha256",
                 "private_diagnostics_sha256", "sdk_sha256"):
        _need(_digest(value[name]), "receipt-digest")
    validate_command_rows(value["commands"])
    _need(type(private_diagnostics_raw) is bytes and 0 < len(private_diagnostics_raw) <= 1024 * 1024
          and private_diagnostics_raw.endswith(b"\n"), "diagnostics-size")
    _need(hashlib.sha256(private_diagnostics_raw).hexdigest() == value["private_diagnostics_sha256"],
          "diagnostics-digest")
    validate_private_diagnostics(private_diagnostics_raw, case, str(helper_root.parent))
    return True
