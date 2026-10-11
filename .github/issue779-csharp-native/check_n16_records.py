#!/usr/bin/env python3
"""Parse source-owned N16 diagnostics as data; this cannot establish native ownership.

The root fixture separately pins the compiled image/capture, checks its actual Unix
exit, joins the original launch, and verifies generated NSS/unit/workspace absence.
A portable parser pass establishes none of those kernel or execution facts.
"""
import argparse
import hashlib
import json
import os
import re
import stat
import sys

MAX_STDERR = 114688
MAX_OUTPUT = 16 * 1024 * 1024
HEX = re.compile(r"^[0-9a-f]{64}$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
GEN = re.compile(r"^[0-9a-f]{32}$")
FIX = " Fix: use an explicit mode and supported protected worker. See start-here/evidencehost.md."
ROOT_MESSAGE = "ASEVD410: The protected empty Observation execution or final cleanup could not be established." + FIX
WORKER_MARKERS = b"".join(f"ASEVDN16C:{number:02d}\n".encode("ascii") for number in range(1, 8))
WORKER_REJECTION_BYTES = ("ASEVD410: The private N16 control race completed without product admission." + FIX + "\n").encode()
WORKER_BYTES = WORKER_MARKERS + WORKER_REJECTION_BYTES
PHASES = frozenset("Unknown CallerCancellation ProtectedInput JobDeadline BackendConnect OwnerActivation Plan AccountCreate WorkspaceCreate ListenerBind WorkerCreate WorkerStart ServerCreate ServerLifetime ServerRun ServerCompletion WorkerExit WorkerStop WorkerCompletion BeginTeardown Custody FileVerification CleanupBegin ServerCancel ListenerClose ServerJoin WorkerJoin CleanupCustody AccountsClose WorkerClose CustodyClose WorkspaceClose OwnerFinalCheck ServerLifetimeClose JobClose OwnerClose InputClose BackendClose FinalDeadline ResultCheck".split())


class Reject(Exception):
    """Fixed private data rejection; raw input is never included in its reason."""


def need(ok, reason):
    if not ok:
        raise Reject(reason)


def integer(value, low, high):
    return type(value) is int and low <= value <= high


def digest(value):
    return type(value) is str and HEX.fullmatch(value) is not None


def shape(value, keys, reason):
    need(type(value) is dict and set(value) == set(keys.split()), reason)


def pairs(rows):
    out = {}
    for key, value in rows:
        need(key not in out, "duplicate-json-key")
        out[key] = value
    return out


def identity(value):
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid,
            value.st_gid, value.st_nlink, value.st_size, value.st_mtime_ns,
            value.st_ctime_ns)


def read_stderr(path, *, expected_owner_uid=0):
    """Read one retained no-follow FD, with length and path identity rechecks.

    The native command requires root:root/0600. The explicit owner keyword exists
    only for portable real-file controls; those controls issue no root authority.
    """
    need(integer(expected_owner_uid, 0, 2**32 - 2), "stderr-owner-argument")
    before = os.lstat(path)
    need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
         and stat.S_IMODE(before.st_mode) == 0o600
         and before.st_uid == expected_owner_uid
         and (expected_owner_uid != 0 or before.st_gid == 0)
         and 0 < before.st_size <= MAX_STDERR, "stderr-custody-or-size")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        need(identity(before) == identity(os.fstat(fd)), "stderr-open-identity")
        chunks = []
        length = 0
        while True:
            block = os.read(fd, min(65536, MAX_STDERR + 1 - length))
            if not block:
                break
            chunks.append(block)
            length += len(block)
            need(length <= MAX_STDERR, "stderr-read-bound")
        raw = b"".join(chunks)
        need(length == before.st_size and identity(before) == identity(os.fstat(fd))
             and identity(before) == identity(os.lstat(path)), "stderr-read-identity")
        return raw
    finally:
        os.close(fd)


def validate_bytes(raw, generation, root_exit, source_commit, capture_sha, projection_sha):
    """Validate detached bytes only; even a complete record grants no capability."""
    need(type(generation) is str and GEN.fullmatch(generation), "generation-syntax")
    need(type(root_exit) is int and root_exit == 1, "actual-root-exit-not-one")
    need(type(source_commit) is str and COMMIT.fullmatch(source_commit), "source-commit-syntax")
    need(digest(capture_sha) and digest(projection_sha), "source-or-build-receipt-pin")
    need(type(raw) is bytes and 0 < len(raw) <= MAX_STDERR and raw.endswith(b"\n")
         and b"\x00" not in raw and b"\r" not in raw, "stderr-bound-or-eof")
    try:
        lines = raw.decode("utf-8", "strict").split("\n")
    except UnicodeError:
        raise Reject("stderr-utf8") from None
    need(len(lines) == 6 and lines[-1] == "" and lines[4] == ROOT_MESSAGE, "stderr-record-order-or-terminal")
    try:
        accepted, kernel, failure, cleanup = [json.loads(line, object_pairs_hook=pairs,
            parse_constant=lambda _: (_ for _ in ()).throw(Reject("nonfinite-json"))) for line in lines[:4]]
    except (ValueError, RecursionError):
        raise Reject("stderr-json") from None
    shape(accepted, "schema generation case accepted_write_committed request_blocked_during_control_overlap original_body_joined original_request_response_committed stop_write_committed positive_wait_write_committed exit_write_committed handlers_joined active_workloads active_controls native_authority native_acceptance", "accepted-shape")
    need(accepted["schema"] == "issue779-accepted-blocked-work-v1"
         and accepted["generation"] == generation and accepted["case"] == "N16", "accepted-binding")
    for key in ("accepted_write_committed", "request_blocked_during_control_overlap", "original_body_joined",
                "original_request_response_committed", "stop_write_committed", "positive_wait_write_committed",
                "exit_write_committed", "handlers_joined"):
        need(accepted[key] is True, "accepted-event-incomplete")
    need(integer(accepted["active_workloads"], 0, 0) and integer(accepted["active_controls"], 0, 0)
         and accepted["native_authority"] is False and accepted["native_acceptance"] is False,
         "accepted-owner-or-authority-state")
    shape(kernel, "schema generation worker_unit process ready terminal cgroup pumps joins observation_only native_authority native_acceptance", "kernel-shape")
    unit = "appsurface-evidence-worker-" + generation + ".service"
    need(kernel["schema"] == "issue779-negative-kernel-observation-v1"
         and kernel["generation"] == generation and kernel["worker_unit"] == unit, "kernel-binding")
    need(kernel["observation_only"] is True and kernel["native_authority"] is False
         and kernel["native_acceptance"] is False, "kernel-authority-state")
    proc = kernel["process"]
    shape(proc, "pid starttime_ticks uid4 gid4 control_group", "kernel-process-shape")
    need(integer(proc["pid"], 2, 2**31 - 1) and integer(proc["starttime_ticks"], 1, 2**64 - 1), "kernel-process-identity")
    for key in ("uid4", "gid4"):
        value = proc[key]
        need(type(value) is list and len(value) == 4
             and all(integer(item, 1, 2**32 - 2) for item in value)
             and len(set(value)) == 1, "kernel-uid-gid-shape")
    need(proc["control_group"] == "/system.slice/" + unit, "kernel-cgroup-binding")
    ready = kernel["ready"]
    shape(ready, "committed descriptor_sha256", "ready-shape")
    need(ready["committed"] is True and digest(ready["descriptor_sha256"]), "ready-descriptor")
    term = kernel["terminal"]
    shape(term, "exec_main_pid exec_main_code exec_main_status active_state sub_state", "terminal-shape")
    need(integer(term["exec_main_pid"], proc["pid"], proc["pid"])
         and integer(term["exec_main_code"], 1, 1) and integer(term["exec_main_status"], 1, 1), "worker-exit-is-not-unix-one")
    need((term["active_state"], term["sub_state"]) in
         (("active", "exited"), ("inactive", "dead"), ("failed", "failed")), "worker-terminal-state")
    cg = kernel["cgroup"]
    shape(cg, "exists populated frozen device_major device_minor inode", "cgroup-shape")
    need(type(cg["exists"]) is bool and cg["populated"] is False and cg["frozen"] is False,
         "worker-cgroup-not-empty")
    need(all(integer(cg[k], 0, 2**64 - 1) for k in ("device_major", "device_minor", "inode"))
         and (not cg["exists"] or cg["inode"] > 0), "cgroup-kernel-numbers")
    pumps = kernel["pumps"]
    shape(pumps, "stdout stderr received_bytes received_byte_limit failure discarded_bytes", "pumps-shape")
    need(integer(pumps["received_byte_limit"], 1, MAX_OUTPUT)
         and integer(pumps["received_bytes"], 0, pumps["received_byte_limit"])
         and integer(pumps["discarded_bytes"], 0, 0) and pumps["failure"] == "None", "pump-accounting")
    for name, expected in (("stdout", b""), ("stderr", WORKER_BYTES)):
        stream = pumps[name]
        shape(stream, "received_bytes retained_bytes discarded_bytes eof failure sha256", "pump-stream-shape")
        need(integer(stream["received_bytes"], len(expected), len(expected))
             and integer(stream["retained_bytes"], len(expected), len(expected))
             and integer(stream["discarded_bytes"], 0, 0) and stream["eof"] is True
             and stream["failure"] == "None"
             and stream["sha256"] == hashlib.sha256(expected).hexdigest(), "pump-stream-incomplete")
    need(pumps["received_bytes"] == pumps["stdout"]["received_bytes"] + pumps["stderr"]["received_bytes"], "pump-total-mismatch")
    shape(kernel["joins"], "startup pending_stop monitor server pumps", "joins-shape")
    need(all(value is True for value in kernel["joins"].values()), "original-joins-incomplete")
    shape(failure, "schema phase error_kind diagnostic_code account_failure control_failure custody_failure", "failure-shape")
    need(failure["schema"] == "evidence-native-observation-failure-v4"
         and type(failure["phase"]) is str and failure["phase"] in PHASES
         and failure["phase"] != "Unknown" and failure["error_kind"] == "Admission"
         and failure["diagnostic_code"] == "ASEVD410"
         and all(failure[key] is None for key in ("account_failure", "control_failure", "custody_failure")), "failure-packet-binding")
    shape(cleanup, "schema generation results_gid accounts_closed root_custody_closed original_owners_closed observation_only native_authority native_acceptance", "cleanup-shape")
    need(cleanup["schema"] == "issue779-accepted-work-root-cleanup-v1"
         and cleanup["generation"] == generation and integer(cleanup["results_gid"], 1, 2**32 - 2)
         and cleanup["results_gid"] != proc["gid4"][0], "cleanup-binding")
    need(all(cleanup[key] is True for key in ("accounts_closed", "root_custody_closed", "original_owners_closed", "observation_only"))
         and cleanup["native_authority"] is False and cleanup["native_acceptance"] is False,
         "final-cleanup-not-closed")
    return {"schema": "issue779-n16-record-summary-v1", "generation": generation,
            "worker_unit": unit, "worker_pid": proc["pid"], "worker_uid": proc["uid4"][0],
            "worker_gid": proc["gid4"][0], "results_gid": cleanup["results_gid"],
            "stderr_bytes": len(raw), "stderr_sha256": hashlib.sha256(raw).hexdigest(),
            "source_commit": source_commit, "source_capture_sha256": capture_sha,
            "root_projection_sha256": projection_sha, "native_authority": False,
            "native_acceptance": False,
            "meaning": "diagnostic data only; original-process, OS/NSS and custody checks remain separate"}


def validate(path, generation, root_exit, source_commit, capture_sha, projection_sha, *, expected_owner_uid=0):
    return validate_bytes(read_stderr(path, expected_owner_uid=expected_owner_uid), generation,
                          root_exit, source_commit, capture_sha, projection_sha)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("stderr")
    ap.add_argument("--generation", required=True)
    ap.add_argument("--root-exit", required=True, type=int)
    ap.add_argument("--source-commit", required=True)
    ap.add_argument("--capture-sha256", required=True)
    ap.add_argument("--projection-sha256", required=True)
    args = ap.parse_args()
    try:
        result = validate(args.stderr, args.generation, args.root_exit, args.source_commit,
                          args.capture_sha256, args.projection_sha256)
    except (OSError, ValueError, Reject, TypeError, RecursionError):
        print("N16_RECORD_REJECTED", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
