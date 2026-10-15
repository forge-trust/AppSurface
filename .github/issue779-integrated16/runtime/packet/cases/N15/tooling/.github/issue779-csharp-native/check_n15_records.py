#!/usr/bin/env python3
"""Strict N15 source-owned diagnostic parser; records grant no native credit."""
import hashlib
import json
import re

MAX_PHASE = 8192
SOURCE = '71d004d6cf5a791ddbb8e73e3c538248985a8505'
CAPTURE_SHA256 = '8f5822ab0f5da555ef142a28321631f1960e61463fe14a83b33e83d521934046'
WORKER = re.compile(r"^appsurface-evidence-worker-([0-9a-f]{32})\.service$")
JOB = re.compile(r"^/org/freedesktop/systemd1/job/[1-9][0-9]{0,19}$")


class Reject(ValueError):
    """Fixed private rejection; raw input is never echoed."""


def require(condition, reason):
    if not condition:
        raise Reject(reason)


def unique_pairs(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "duplicate-json-key")
        value[key] = item
    return value


def decode(raw, cap):
    require(type(raw) is bytes and 0 < len(raw) <= cap and raw.endswith(b"\n")
            and b"\x00" not in raw and b"\r" not in raw, "record-byte-bound")
    try:
        value = json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=unique_pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(Reject("nonfinite-json")))
    except (UnicodeError, ValueError, RecursionError):
        raise Reject("record-json") from None
    return value


def validate_phase(raw, expected_generation):
    """Accept only the actual fixed reply-phase frame emitted by the N15 source hook."""
    require(type(expected_generation) is str
            and re.fullmatch(r"[0-9a-f]{32}", expected_generation) is not None,
            "generation-syntax")
    value = decode(raw, MAX_PHASE)
    require(type(value) is dict and set(value) == {"case_name", "phase", "unit", "job_path"},
            "phase-shape")
    require(value["case_name"] == "N15" and value["phase"] == "start-transient-unit-reply",
            "phase-binding")
    match = WORKER.fullmatch(value["unit"]) if type(value["unit"]) is str else None
    require(match is not None and match.group(1) == expected_generation, "worker-unit-generation")
    require(type(value["job_path"]) is str and JOB.fullmatch(value["job_path"]) is not None,
            "job-path-shape")
    return {"unit": value["unit"], "job_path": value["job_path"],
            "phase_sha256": hashlib.sha256(raw).hexdigest(), "phase_bytes": len(raw),
            "native_credit": False}



PARTIAL_SCHEMA = "issue779-n15-native-observer-partial-v1"
COMPLETION_SCHEMA = "issue779-n15-outer-completion-evidence-v1"
FINAL_SCHEMA = "issue779-n15-native-fixture-observation-v1"
FINAL_KEYS = {
    "schema", "case_generation", "source_commit", "source_capture_sha256",
    "phase_sha256", "job_path", "worker", "owner", "pidfd_signal",
    "owner_terminal", "worker_terminal", "guard", "reactivation",
    "observation_only", "native_authority", "native_acceptance", "native_credit",
}
PARTIAL_KEYS = (FINAL_KEYS - {"source_commit", "source_capture_sha256", "reactivation"}) | {
    "admission_remaining_ms_at_trigger", "account_snapshot",
}
COMPLETION_KEYS = {
    "schema", "case_generation", "source_commit", "source_capture_sha256", "reactivation",
}
REACTIVATION_KEYS = {
    "attempted_same_owner_generation", "owner_exit_nonzero", "reply_phase_absent",
    "worker_start_not_repeated", "failure_before_worker_start",
}


def admission_window_remaining(deadline_boot_ms, trigger_boot_ms):
    """Return remaining original 30s Admission time; require the five-second minimum."""
    require(type(deadline_boot_ms) is int and deadline_boot_ms > 0
            and type(trigger_boot_ms) is int and trigger_boot_ms >= 0,
            "admission-clock-fields")
    remaining_ms = deadline_boot_ms - trigger_boot_ms
    require(5000 <= remaining_ms <= 30000, "admission-observation-window")
    return remaining_ms


def _validate_observed_fields(value, expected_generation):
    require(type(value["phase_sha256"]) is str
            and re.fullmatch(r"[0-9a-f]{64}", value["phase_sha256"]) is not None,
            "phase-digest")
    require(type(value["job_path"]) is str and JOB.fullmatch(value["job_path"]) is not None,
            "job-path-shape")
    unit = "appsurface-evidence-worker-" + expected_generation + ".service"
    owner_unit = "appsurface-evidence-owner-" + expected_generation + ".service"
    worker = value["worker"]
    require(type(worker) is dict and set(worker) == {
        "unit", "main_pid", "starttime_ticks", "uid", "gid", "control_group",
        "cgroup_membership_matched", "identity_rechecked_before_signal",
    }, "worker-shape")
    require(worker["unit"] == unit and worker["control_group"] == "/system.slice/" + unit
            and type(worker["main_pid"]) is int and 1 < worker["main_pid"] < 2**31
            and type(worker["starttime_ticks"]) is int and 0 < worker["starttime_ticks"] < 2**64
            and type(worker["uid"]) is int and 0 < worker["uid"] < 2**32 - 1
            and type(worker["gid"]) is int and 0 < worker["gid"] < 2**32 - 1
            and worker["cgroup_membership_matched"] is True
            and worker["identity_rechecked_before_signal"] is True, "worker-identity")
    owner = value["owner"]
    require(type(owner) is dict and set(owner) == {
        "unit", "main_pid", "starttime_ticks", "uid", "gid", "control_group",
        "pidfd_opened", "identity_rechecked_before_signal",
    }, "owner-shape")
    require(owner["unit"] == owner_unit and owner["control_group"] == "/system.slice/" + owner_unit
            and type(owner["main_pid"]) is int and owner["main_pid"] > 1
            and type(owner["starttime_ticks"]) is int and owner["starttime_ticks"] > 0
            and type(owner["uid"]) is int and owner["uid"] == 0
            and type(owner["gid"]) is int and owner["gid"] == 0
            and owner["pidfd_opened"] is True
            and owner["identity_rechecked_before_signal"] is True, "owner-identity")
    signal = value["pidfd_signal"]
    require(type(signal) is dict and set(signal) == {"signal", "syscall_succeeded"}
            and type(signal["signal"]) is int and signal["signal"] == 9
            and signal["syscall_succeeded"] is True, "pidfd-signal")
    terminal = value["owner_terminal"]
    require(type(terminal) is dict and set(terminal) == {
        "main_pid", "active_state", "sub_state", "exec_main_pid", "exec_main_code",
        "exec_main_status", "restart_policy", "same_unit_no_replacement",
    } and type(terminal["main_pid"]) is int and terminal["main_pid"] == 0
        and type(terminal["exec_main_pid"]) is int and terminal["exec_main_pid"] == owner["main_pid"]
        and type(terminal["exec_main_code"]) is int and terminal["exec_main_code"] == 2
        and type(terminal["exec_main_status"]) is int and terminal["exec_main_status"] == 9
        and terminal["restart_policy"] == "no"
        and terminal["active_state"] in ("inactive", "failed")
        and terminal["sub_state"] in ("dead", "failed")
        and terminal["same_unit_no_replacement"] is True, "owner-terminal")
    worker_term = value["worker_terminal"]
    require(type(worker_term) is dict and set(worker_term) == {
        "main_pid", "active_state", "sub_state", "exec_main_pid", "exec_main_code",
        "exec_main_status", "restart_policy", "cgroup_populated", "cgroup_tree_empty_twice",
        "no_successful_completion", "same_unit_no_replacement",
    } and type(worker_term["main_pid"]) is int and worker_term["main_pid"] == 0
        and type(worker_term["exec_main_pid"]) is int and worker_term["exec_main_pid"] == worker["main_pid"]
        and type(worker_term["exec_main_code"]) is int and worker_term["exec_main_code"] in (1, 2)
        and type(worker_term["exec_main_status"]) is int and worker_term["exec_main_status"] > 0
        and worker_term["restart_policy"] == "no"
        and worker_term["active_state"] in ("inactive", "failed")
        and worker_term["sub_state"] in ("dead", "failed")
        and worker_term["cgroup_populated"] is False
        and worker_term["cgroup_tree_empty_twice"] is True
        and worker_term["no_successful_completion"] is True
        and worker_term["same_unit_no_replacement"] is True, "worker-terminal")
    guard = value["guard"]
    require(type(guard) is dict and set(guard) == {
        "armed_absent", "consumed_root_owned_exact_marker", "consumed_marker_same_generation",
    } and all(guard[k] is True for k in guard), "single-use-guard")
    require(value["observation_only"] is True and value["native_authority"] is False
            and value["native_acceptance"] is False and value["native_credit"] is False,
            "authority-or-credit")


def validate_partial_observation(value, expected_generation):
    """Validate the closed live observer result; it is incomplete data, never final proof."""
    require(type(expected_generation) is str
            and re.fullmatch(r"[0-9a-f]{32}", expected_generation) is not None,
            "generation-syntax")
    require(type(value) is dict and set(value) == PARTIAL_KEYS, "partial-shape")
    require(value["schema"] == PARTIAL_SCHEMA
            and value["case_generation"] == expected_generation,
            "partial-generation")
    require(type(value["admission_remaining_ms_at_trigger"]) is int
            and 5000 <= value["admission_remaining_ms_at_trigger"] <= 30000,
            "partial-admission-window")
    _validate_observed_fields(value, expected_generation)
    accounts=value["account_snapshot"]
    require(type(accounts) is dict and set(accounts)=={"names","ids"}
            and type(accounts["names"]) is dict and set(accounts["names"])=={"worker","subject","results"}
            and type(accounts["ids"]) is dict and set(accounts["ids"])=={
                "worker_uid","worker_gid","subject_uid","subject_gid","results_gid"},
            "partial-account-snapshot-shape")
    worker_name=accounts["names"]["worker"]
    match=re.fullmatch(r"evw([0-9a-f]{28})",worker_name) if type(worker_name) is str else None
    require(match is not None and accounts["names"]["subject"]=="evs"+match.group(1)
            and accounts["names"]["results"]=="evr"+match.group(1),"partial-account-name-binding")
    ids=accounts["ids"]
    require(all(type(ids[k]) is int and 0<ids[k]<2**32-1 for k in ids)
            and len(set(ids.values()))==5
            and ids["worker_uid"]==value["worker"]["uid"]
            and ids["worker_gid"]==value["worker"]["gid"],"partial-account-id-binding")
    return {
        "schema": "issue779-n15-observer-partial-summary-v1",
        "case_generation": expected_generation,
        "phase_sha256": value["phase_sha256"],
        "job_path": value["job_path"],
        "admission_remaining_ms_at_trigger": value["admission_remaining_ms_at_trigger"],
        "native_credit": False,
        "meaning": "partial live observation only; source and reactivation evidence remain external",
    }


def validate_completion_evidence(value, expected_generation):
    """Validate separately supplied, externally measured source and reactivation data."""
    require(type(expected_generation) is str
            and re.fullmatch(r"[0-9a-f]{32}", expected_generation) is not None,
            "generation-syntax")
    require(type(value) is dict and set(value) == COMPLETION_KEYS, "completion-evidence-shape")
    require(value["schema"] == COMPLETION_SCHEMA
            and value["case_generation"] == expected_generation
            and value["source_commit"] == SOURCE
            and value["source_capture_sha256"] == CAPTURE_SHA256,
            "completion-evidence-binding")
    activation = value["reactivation"]
    require(type(activation) is dict and set(activation) == REACTIVATION_KEYS
            and all(activation[key] is True for key in REACTIVATION_KEYS),
            "completion-reactivation-measurement")
    return {
        "schema": "issue779-n15-completion-evidence-summary-v1",
        "case_generation": expected_generation,
        "source_commit": value["source_commit"],
        "source_capture_sha256": value["source_capture_sha256"],
        "reactivation": dict(activation),
        "native_credit": False,
        "meaning": "external measurements checked for exact binding; provenance must be established by outer runner",
    }


def validate_result(value, expected_generation):
    """Validate the current exact final observation shape; the record itself is never proof."""
    require(type(value) is dict and set(value) == FINAL_KEYS, "result-shape")
    require(value["schema"] == FINAL_SCHEMA
            and value["case_generation"] == expected_generation
            and value["source_commit"] == SOURCE
            and value["source_capture_sha256"] == CAPTURE_SHA256,
            "result-source-binding")
    _validate_observed_fields(value, expected_generation)
    activation = value["reactivation"]
    require(type(activation) is dict and set(activation) == REACTIVATION_KEYS
            and all(activation[key] is True for key in REACTIVATION_KEYS),
            "reactivation-rejected")
    return {"schema": "issue779-n15-native-record-summary-v1",
            "case_generation": expected_generation,
            "worker_unit": "appsurface-evidence-worker-" + expected_generation + ".service",
            "worker_pid": value["worker"]["main_pid"],
            "owner_pid": value["owner"]["main_pid"],
            "job_path": value["job_path"], "phase_sha256": value["phase_sha256"],
            "native_credit": False,
            "meaning": "source-bound procedure data; independent native review and all other gates remain required"}


def assemble_final_record(partial, completion_evidence, expected_generation):
    """Pure adapter. Copy measured source/reactivation and observer fields; synthesize no claims."""
    validate_partial_observation(partial, expected_generation)
    validate_completion_evidence(completion_evidence, expected_generation)
    record = {key: partial[key] for key in FINAL_KEYS if key in partial}
    record["schema"] = FINAL_SCHEMA
    record["source_commit"] = completion_evidence["source_commit"]
    record["source_capture_sha256"] = completion_evidence["source_capture_sha256"]
    record["reactivation"] = dict(completion_evidence["reactivation"])
    validate_result(record, expected_generation)
    return record
