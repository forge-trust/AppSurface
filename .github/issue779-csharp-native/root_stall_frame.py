"""Parse the fixed, source-bound three-line N11 root-stderr frame.

The result is a pair ``(record, diagnostic)`` of detached JSON data dictionaries.
It carries no native acceptance or authority; callers must separately bind source,
root/unit identity, deadline, and retained worker streams, then use the existing
``check_stall_record`` API for worker-byte and expected-identity validation.

Input is exactly one UTF-8 JSON N11 record (64 KiB maximum), one UTF-8 JSON
``evidence-native-observation-failure-v4`` diagnostic (4096 bytes maximum), and
the fixed ASEVD410 terminal line (1024 bytes maximum), separated by LF with at
most one final LF. CR, NUL, malformed UTF-8/JSON, duplicate or case-aliased
properties, schema drift, and extra/missing lines fail with the constant
``fixedFrameRejected`` message. This pure parser uses no paths, hashes, environment,
process, clock, callback, admission, or native operation.
"""
from __future__ import annotations

import json
from typing import Any

MAX_RECORD_JSON = 64 * 1024
MAX_DIAGNOSTIC_JSON = 4096
MAX_TERMINAL_LINE = 1024
MAX_FRAME_BYTES_EXCLUSIVE = 70 * 1024
RECORD_SCHEMA = "issue779-n11-original-failed-settlement-v1"
DIAGNOSTIC_SCHEMA = "evidence-native-observation-failure-v4"
TERMINAL_LINE = (
    b"ASEVD410: The protected empty Observation execution or final cleanup could not be established. "
    b"Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md."
)
_RECORD_KEYS = frozenset({
    "schema", "generation", "worker_unit", "process", "ready", "lifetime",
    "pending_start", "monitor", "terminal", "cgroup", "pumps",
    "observation_only", "native_authority", "native_acceptance",
})
_NESTED_KEYS = {
    "process": frozenset({"pid", "starttime_ticks", "uid4", "gid4", "control_group"}),
    "ready": frozenset({"committed", "descriptor_sha256"}),
    "lifetime": frozenset({"startup_joined", "stop_joined", "failed", "physically_settled"}),
    "pending_start": frozenset({"start_reserved", "started", "start_joined", "closed", "stop_joined", "first_failure"}),
    "terminal": frozenset({"exec_main_pid", "exec_main_code", "exec_main_status"}),
    "cgroup": frozenset({"exists", "populated", "frozen", "device_major", "device_minor", "inode", "after_pumps"}),
    "pumps": frozenset({"joined", "stdout", "stderr", "received_bytes", "received_byte_limit", "failure", "discarded_bytes", "quota_exceeded", "stop_signal_failed", "export_complete"}),
    "stream": frozenset({"received_bytes", "retained_bytes", "discarded_bytes", "eof", "failure", "sha256", "raw_base64"}),
}

def _reject() -> None:
    raise ValueError("fixedFrameRejected")

def _pairs_without_aliases(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    folded: set[str] = set()
    for key, value in pairs:
        alias = key.casefold()
        if key in result or alias in folded:
            _reject()
        result[key] = value
        folded.add(alias)
    return result

def _no_non_json_constant(value: str) -> None:
    _reject()

def _decode_object(line: bytes) -> dict[str, Any]:
    try:
        decoded = line.decode("utf-8", errors="strict")
        value = json.loads(
            decoded,
            object_pairs_hook=_pairs_without_aliases,
            parse_constant=_no_non_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, RecursionError, ValueError):
        _reject()
    if type(value) is not dict:
        _reject()
    return value

def _require_shape(value: dict[str, Any], name: str) -> None:
    expected = _NESTED_KEYS[name]
    if type(value) is not dict or frozenset(value) != expected:
        _reject()

def _validate_record(record: dict[str, Any]) -> None:
    if frozenset(record) != _RECORD_KEYS or record.get("schema") != RECORD_SCHEMA:
        _reject()
    if record.get("observation_only") is not True or record.get("native_authority") is not False or record.get("native_acceptance") is not False:
        _reject()
    for name in ("process", "ready", "lifetime", "pending_start", "cgroup", "pumps"):
        _require_shape(record[name], name)
    if record["terminal"] is not None:
        _require_shape(record["terminal"], "terminal")
    pumps = record["pumps"]
    _require_shape(pumps["stdout"], "stream")
    _require_shape(pumps["stderr"], "stream")

def parse_root_stall_frame(frame: bytes) -> tuple[dict[str, Any], dict[str, Any]]:
    """Return the N11 record and v4 diagnostic from an exact three-line frame.

    A single trailing LF is accepted because the source emits each line with
    WriteLine semantics. All rejection paths raise only ``fixedFrameRejected``.
    """
    if type(frame) is not bytes or not frame or len(frame) >= MAX_FRAME_BYTES_EXCLUSIVE:
        _reject()
    if b"\x00" in frame or b"\r" in frame:
        _reject()
    lines = frame.split(b"\n")
    if lines and lines[-1] == b"":
        lines.pop()
    if len(lines) != 3 or any(not line for line in lines):
        _reject()
    record_line, diagnostic_line, terminal_line = lines
    if (len(record_line) > MAX_RECORD_JSON or len(diagnostic_line) > MAX_DIAGNOSTIC_JSON
            or len(terminal_line) > MAX_TERMINAL_LINE or terminal_line != TERMINAL_LINE):
        _reject()
    record = _decode_object(record_line)
    diagnostic = _decode_object(diagnostic_line)
    _validate_record(record)
    if diagnostic.get("schema") != DIAGNOSTIC_SCHEMA:
        _reject()
    return record, diagnostic
