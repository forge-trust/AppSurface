"""Validate detached N11 failure data; perform no OS operation or qualification.

The original root holder supplies the failure-only record. Independent source,
credential, unit, deadline, quarantine and retained-byte provenance must be checked
by the native fixture. This parser cannot establish any of those facts.
"""
import base64
import binascii
import hashlib
import json
import re

MAX_JSON_BYTES = 65536
MAX_RAW_BYTES = 32768
ENTERED = b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n"
TOP = {"schema", "generation", "worker_unit", "process", "ready", "lifetime",
       "pending_start", "monitor", "terminal", "cgroup", "pumps", "observation_only",
       "native_authority", "native_acceptance"}
STOP_TIMING_SCHEMA = "issue779-original-worker-stop-timing-v1"
STOP_TIMING_FIELDS = frozenset(("schema", "job_remaining_ms", "stopping_ms", "worker_runtime_us",
    "containment_started_ms", "stop_1_started_ms", "stop_1_replied_ms", "stop_2_started_ms",
    "stop_2_replied_ms", "unit_and_group_stopped_ms", "monitor_joined_ms", "pumps_joined_ms",
    "final_unit_and_group_sampled_ms", "settlement_published_ms", "stop_joined_ms"))
STOP_TIMING_EVENTS = ("containment_started_ms", "stop_1_started_ms", "stop_1_replied_ms",
    "stop_2_started_ms", "stop_2_replied_ms", "unit_and_group_stopped_ms", "monitor_joined_ms",
    "pumps_joined_ms", "final_unit_and_group_sampled_ms", "settlement_published_ms", "stop_joined_ms")
STOP_TIMING_REQUIRED_EVENTS = ("containment_started_ms", "stop_1_started_ms", "stop_1_replied_ms",
    "unit_and_group_stopped_ms", "monitor_joined_ms", "pumps_joined_ms",
    "final_unit_and_group_sampled_ms", "settlement_published_ms", "stop_joined_ms")
FAILED_STOP_REQUIRED_EVENTS = ("containment_started_ms", "stop_1_started_ms", "stop_1_replied_ms",
    "unit_and_group_stopped_ms", "monitor_joined_ms", "pumps_joined_ms",
    "final_unit_and_group_sampled_ms", "stop_joined_ms")


def check_stop_timing(value, *, expected_stopping_ms):
    """Separate successful-settlement timing from the failed N11 stop control."""
    if value is None:
        return {"present": False, "proof": False, "schema": None,
                "missing_fields": ["stop_timing"], "clock_origin": "LinuxWorkerStopTiming.Begin",
                "bounded_failed_stop": False, "failed_stop_missing_fields": ["stop_timing"]}
    _object(value, STOP_TIMING_FIELDS)
    _require(value["schema"] == STOP_TIMING_SCHEMA)
    _integer(value["job_remaining_ms"], -3_600_000, 3_600_000)
    _integer(value["stopping_ms"], 1, 3_600_000)
    runtime = value["worker_runtime_us"]
    if runtime is not None:
        _integer(runtime, 1, 3_600_000_000)
    for name in STOP_TIMING_EVENTS:
        event = value[name]
        if event is not None:
            _integer(event, 0, 3_600_000)
    second_start, second_reply = value["stop_2_started_ms"], value["stop_2_replied_ms"]
    _require((second_start is None) == (second_reply is None))
    observed = [value[name] for name in STOP_TIMING_EVENTS if value[name] is not None]
    _require(observed == sorted(observed))
    _require(expected_stopping_ms is None or value["stopping_ms"] == expected_stopping_ms)
    missing = [name for name in STOP_TIMING_REQUIRED_EVENTS if value[name] is None]
    if value["job_remaining_ms"] <= 0:
        missing.append("unexpired_job_remaining_ms")
    if runtime is None:
        missing.append("worker_runtime_us")
    failed_stop_missing = [name for name in FAILED_STOP_REQUIRED_EVENTS if value[name] is None]
    if value["job_remaining_ms"] <= 0:
        failed_stop_missing.append("unexpired_job_remaining_ms")
    if value["stop_joined_ms"] is not None and value["stop_joined_ms"] > value["stopping_ms"]:
        failed_stop_missing.append("stop_joined_within_stopping_ms")
    if value["stop_joined_ms"] is not None and value["stop_joined_ms"] > value["job_remaining_ms"]:
        failed_stop_missing.append("stop_joined_within_original_job_ms")
    # A second stop is optional only as an absent pair. A partial pair is malformed above.
    return {"present": True, "proof": not missing, "schema": STOP_TIMING_SCHEMA,
            "missing_fields": missing, "clock_origin": "LinuxWorkerStopTiming.Begin",
            "bounded_failed_stop": not failed_stop_missing,
            "failed_stop_missing_fields": failed_stop_missing}


class StallRecordRejected(ValueError):
    """One fixed diagnostic; supplied values and chained errors are not retained."""


def _require(value):
    if not value:
        raise StallRecordRejected("stall-data-rejected")


def _object(value, keys):
    _require(type(value) is dict and set(value) == set(keys))


def _integer(value, low, high):
    _require(type(value) is int and low <= value <= high)


def _pairs(pairs):
    result, folded = {}, set()
    for key, value in pairs:
        _require(key.casefold() not in folded)
        result[key] = value
        folded.add(key.casefold())
    return result


def _digest(value):
    _require(type(value) is str and re.fullmatch("[0-9a-f]{64}", value) is not None)


def _stream(value, raw):
    _object(value, ("received_bytes", "retained_bytes", "discarded_bytes", "eof",
                    "failure", "sha256", "raw_base64"))
    for name in ("received_bytes", "retained_bytes"):
        _integer(value[name], 0, MAX_RAW_BYTES)
        _require(value[name] == len(raw))
    _integer(value["discarded_bytes"], 0, 0)
    _require(value["eof"] is True and value["failure"] == "None")
    _digest(value["sha256"])
    _require(value["sha256"] == hashlib.sha256(raw).hexdigest())
    encoded = value["raw_base64"]
    _require(type(encoded) is str and len(encoded) <= 4 * ((MAX_RAW_BYTES + 2) // 3))
    decoded = base64.b64decode(encoded, validate=True)
    _require(decoded == raw and base64.b64encode(decoded).decode("ascii") == encoded)


def check_stall_record(record, *, worker_stdout, worker_stderr, expected_generation,
                       expected_pid, expected_starttime_ticks, expected_uid, expected_gid,
                       expected_descriptor_sha256, expected_stopping_ms=None):
    """Check supplied records against independently retained original full bytes.

    An accepted result is data consistency only. It requires original joined tasks,
    a committed READY record, a genuinely nonzero terminal and empty final cgroup
    data sampled after the pumps. It preserves unsuccessful lifetime/monitor state
    rather than converting those fields into successful completion. The expected
    identity values are caller data, not credentials or capabilities.
    """
    try:
        _require(type(record) is bytes and 0 < len(record) <= MAX_JSON_BYTES + 1)
        body = record[:-1] if record.endswith(b"\n") else record
        _require(0 < len(body) <= MAX_JSON_BYTES and body.startswith(b"{")
                 and body.endswith(b"}"))
        _require(type(worker_stdout) is bytes and worker_stdout == b"")
        _require(type(worker_stderr) is bytes and worker_stderr.startswith(ENTERED)
                 and worker_stderr.count(ENTERED) == 1 and len(worker_stderr) <= MAX_RAW_BYTES)
        _require(type(expected_generation) is str and expected_generation != "0" * 32
                 and re.fullmatch("[0-9a-f]{32}", expected_generation) is not None)
        _integer(expected_pid, 1, (1 << 31) - 1)
        _integer(expected_starttime_ticks, 1, (1 << 64) - 1)
        _integer(expected_uid, 1, (1 << 32) - 2)
        _integer(expected_gid, 1, (1 << 32) - 2)
        _digest(expected_descriptor_sha256)
        value = json.loads(body.decode("utf-8", errors="strict"), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _require(False))
        _require(type(value) is dict and set(value) == TOP | {"stop_timing"})
        stop_timing = check_stop_timing(value.get("stop_timing"), expected_stopping_ms=expected_stopping_ms)
        # N11 is a failed-path record: preserve false settlement while requiring the
        # original bounded stop, terminal/group observations, joined monitor/pumps and EOF.
        _require(stop_timing["bounded_failed_stop"] is True)
        unit = "appsurface-evidence-worker-" + expected_generation + ".service"
        _require(value["schema"] == "issue779-n11-original-failed-settlement-v1"
                 and value["generation"] == expected_generation and value["worker_unit"] == unit)
        process = value["process"]
        _object(process, ("pid", "starttime_ticks", "uid4", "gid4", "control_group"))
        _integer(process["pid"], 1, (1 << 31) - 1)
        _integer(process["starttime_ticks"], 1, (1 << 64) - 1)
        _require(process["pid"] == expected_pid and process["starttime_ticks"] == expected_starttime_ticks
                 and process["control_group"] == "/system.slice/" + unit)
        for name, identity in (("uid4", expected_uid), ("gid4", expected_gid)):
            _require(type(process[name]) is list and len(process[name]) == 4)
            for item in process[name]:
                _integer(item, 1, (1 << 32) - 2)
                _require(item == identity)
        _object(value["ready"], ("committed", "descriptor_sha256"))
        _require(value["ready"]["committed"] is True
                 and value["ready"]["descriptor_sha256"] == expected_descriptor_sha256)
        lifetime = value["lifetime"]
        _object(lifetime, ("startup_joined", "stop_joined", "failed", "physically_settled"))
        _require(lifetime["startup_joined"] is True and lifetime["stop_joined"] is True
                 and lifetime["failed"] is True and lifetime["physically_settled"] is False
                 and value["stop_timing"]["settlement_published_ms"] is None)
        pending = value["pending_start"]
        _object(pending, ("start_reserved", "started", "start_joined", "closed", "stop_joined",
                          "first_failure"))
        _require(all(pending[name] is True for name in
                     ("start_reserved", "started", "start_joined", "closed", "stop_joined")))
        _require(pending["first_failure"] in
                 ("None", "StartFailed", "StartCancelled", "StopFailed", "StopCancelled"))
        _require(value["monitor"] in ("Completed", "Faulted", "Cancelled"))
        terminal = value["terminal"]
        _object(terminal, ("exec_main_pid", "exec_main_code", "exec_main_status"))
        _integer(terminal["exec_main_pid"], 1, (1 << 31) - 1)
        _integer(terminal["exec_main_code"], 1, 6)
        _integer(terminal["exec_main_status"], 1, 255)
        _require(terminal["exec_main_pid"] == expected_pid)
        group = value["cgroup"]
        _object(group, ("exists", "populated", "frozen", "device_major", "device_minor", "inode",
                        "after_pumps"))
        _require(type(group["exists"]) is bool and group["after_pumps"] is True)
        if group["exists"]:
            _require(group["populated"] is False and group["frozen"] is False)
            _integer(group["device_major"], 0, (1 << 32) - 1)
            _integer(group["device_minor"], 0, (1 << 32) - 1)
            _integer(group["inode"], 1, (1 << 64) - 1)
        else:
            _require(all(group[name] is None for name in
                         ("populated", "frozen", "device_major", "device_minor", "inode")))
        pumps = value["pumps"]
        _object(pumps, ("joined", "stdout", "stderr", "received_bytes", "received_byte_limit",
                        "failure", "discarded_bytes", "quota_exceeded", "stop_signal_failed",
                        "export_complete"))
        _require(pumps["joined"] is True and pumps["export_complete"] is True
                 and pumps["quota_exceeded"] is False and pumps["stop_signal_failed"] is False
                 and pumps["failure"] == "None")
        _stream(pumps["stdout"], worker_stdout)
        _stream(pumps["stderr"], worker_stderr)
        _integer(pumps["received_bytes"], 0, MAX_RAW_BYTES)
        _integer(pumps["received_byte_limit"], 1, 16 * 1024 * 1024)
        _integer(pumps["discarded_bytes"], 0, 0)
        _require(pumps["received_bytes"] == len(worker_stdout) + len(worker_stderr)
                 and pumps["received_bytes"] <= pumps["received_byte_limit"])
        _require(value["observation_only"] is True and value["native_authority"] is False
                 and value["native_acceptance"] is False)
        return {"schema": "issue779-n11-detached-data-consistency-v1", "streams_matched": True,
                "monitor": value["monitor"], "physically_settled": lifetime["physically_settled"],
                "stop_timing": stop_timing, "native_authority": False, "native_acceptance": False}
    except (ValueError, TypeError, KeyError, UnicodeError, OverflowError, RecursionError,
            binascii.Error):
        raise StallRecordRejected("stall-data-rejected") from None
def check_launch_timing(record, *, expected_generation, expected_source, expected_request_sha256,
                        expected_job_deadline_utc):
    """Validate retained launch arithmetic as detached data, never as a physical-exit receipt.

    The root fixture captures the exact UTC, BOOTTIME and fixture remainder passed to its
    unchanged runtime selector. A later parser binds the root-owned record to the original
    request bytes and checks the 5s observer, 5s stop and 5s dispatch reserves. It performs
    no OS operations, clock reads, waits, timer resets or admission decisions.
    """
    try:
        _require(type(record) is bytes and 0 < len(record) <= 1024)
        value = json.loads(record.decode("utf-8", errors="strict"), object_pairs_hook=_pairs,
                           parse_constant=lambda _: _require(False))
        fields = {"schema", "generation", "source", "owner_unit", "request_sha256",
                  "utc_sample_epoch_seconds", "utc_deadline_epoch_seconds", "boottime_sample_seconds",
                  "boottime_deadline_seconds", "utc_remaining_seconds", "boottime_remaining_seconds",
                  "fixture_remaining_seconds", "selected_runtime_seconds", "observer_reserve_seconds",
                  "stop_reserve_seconds", "dispatch_reserve_seconds", "timeout_stop_seconds",
                  "native_authority", "native_acceptance"}
        _object(value, fields)
        _require(type(expected_generation) is str and re.fullmatch("[0-9a-f]{32}", expected_generation)
                 and expected_generation != "0" * 32)
        _require(type(expected_source) is str and re.fullmatch("[0-9a-f]{40}", expected_source))
        _digest(expected_request_sha256)
        _require(value["schema"] == "issue779-n11-launch-timing-v1"
                 and value["generation"] == expected_generation and value["source"] == expected_source
                 and value["owner_unit"] == "appsurface-evidence-owner-" + expected_generation + ".service"
                 and value["request_sha256"] == expected_request_sha256
                 and value["native_authority"] is False and value["native_acceptance"] is False)
        for name in ("utc_sample_epoch_seconds", "utc_deadline_epoch_seconds", "boottime_sample_seconds",
                     "boottime_deadline_seconds"):
            _integer(value[name], 1, (1 << 53) - 1)
        for name, maximum in (("utc_remaining_seconds", 240), ("boottime_remaining_seconds", 240),
                              ("fixture_remaining_seconds", 570)):
            _integer(value[name], 1, maximum)
        for name in ("observer_reserve_seconds", "stop_reserve_seconds", "dispatch_reserve_seconds",
                     "timeout_stop_seconds"):
            _integer(value[name], 5, 5)
        _require(value["utc_deadline_epoch_seconds"] - value["utc_sample_epoch_seconds"]
                 == value["utc_remaining_seconds"])
        _require(value["boottime_deadline_seconds"] - value["boottime_sample_seconds"]
                 == value["boottime_remaining_seconds"])
        # The fixture renders the original deadline from integral UTC epoch seconds.
        import datetime
        _require(type(expected_job_deadline_utc) is str
                 and re.fullmatch(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", expected_job_deadline_utc))
        expected_epoch = int(datetime.datetime.strptime(expected_job_deadline_utc, "%Y-%m-%dT%H:%M:%SZ")
                             .replace(tzinfo=datetime.timezone.utc).timestamp())
        _require(value["utc_deadline_epoch_seconds"] == expected_epoch)
        expected = min(value["utc_remaining_seconds"], value["boottime_remaining_seconds"],
                       value["fixture_remaining_seconds"] - 5) - 10
        _integer(value["selected_runtime_seconds"], 105, 240)
        _require(value["selected_runtime_seconds"] == expected)
        return {"selected_runtime_seconds": expected, "timing_consistent": True,
                "native_authority": False, "native_acceptance": False}
    except (StallRecordRejected, ValueError, TypeError, KeyError, OverflowError, UnicodeError):
        raise StallRecordRejected("stall-data-rejected") from None

