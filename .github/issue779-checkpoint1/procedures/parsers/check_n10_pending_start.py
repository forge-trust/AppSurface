#!/usr/bin/env python3
"""Strict N10 start-reply/joined-observation parser. Parses bounded data only; proves no native acceptance."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

MAX_FRAME = 4096
MAX_STDERR = 114688
GEN = re.compile(r"^[0-9a-f]{32}$")
UNIT = re.compile(r"^appsurface-evidence-worker-([0-9a-f]{32})\.service$")
JOB = re.compile(r"^/org/freedesktop/systemd1/job/[1-9][0-9]{0,18}$")


class Reject(ValueError):
    def __init__(self, category):
        super().__init__("N10 record rejected")
        self.category = category


def require(ok, category):
    if not ok:
        raise Reject(category)


def exact_int(value, minimum, maximum):
    return type(value) is int and minimum <= value <= maximum


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate-json-member")
        result[key] = value
    return result


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read_bounded(path):
    path = Path(path)
    before = path.lstat()
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_size <= MAX_STDERR,
            "stderr-file-shape")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        opened = os.fstat(fd)
        require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) ==
                (opened.st_dev, opened.st_ino, opened.st_size, opened.st_mtime_ns), "stderr-open-race")
        chunks = []
        total = 0
        while True:
            chunk = os.read(fd, min(16384, MAX_STDERR + 1 - total))
            if not chunk:
                break
            total += len(chunk)
            require(total <= MAX_STDERR, "stderr-byte-bound")
            chunks.append(chunk)
        after = os.fstat(fd)
        named = path.lstat()
        require(total == before.st_size and
                (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) ==
                (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns) ==
                (named.st_dev, named.st_ino, named.st_size, named.st_mtime_ns), "stderr-changed")
        return b"".join(chunks)
    finally:
        os.close(fd)


def extract(raw):
    require(type(raw) is bytes and len(raw) <= MAX_STDERR, "stderr-byte-bound")
    rows = []
    for number, line in enumerate(raw.splitlines(), 1):
        if len(line) > MAX_FRAME:
            require(b"issue779-n10-pending-start-" not in line, "n10-frame-byte-bound")
            continue
        if not line.startswith(b"{"):
            continue
        try:
            row = json.loads(line.decode("utf-8", "strict"), object_pairs_hook=unique)
        except (UnicodeError, json.JSONDecodeError):
            require(b"issue779-n10-pending-start-" not in line, "n10-frame-json")
            continue
        if isinstance(row, dict) and row.get("case") == "N10":
            require(row.get("schema") in ("issue779-n10-pending-start-phase-v1",
                                          "issue779-n10-pending-start-observation-v1"),
                    "unknown-n10-frame")
            rows.append((number, row))
    return rows


def phase(row):
    require(set(row) == {"schema", "case", "phase", "unit", "job_path", "native_authority", "native_acceptance"},
            "start-frame-keys")
    require(row["schema"] == "issue779-n10-pending-start-phase-v1" and row["case"] == "N10" and
            row["phase"] == "start-transient-unit-reply", "start-frame-identity")
    match = UNIT.fullmatch(row["unit"] if isinstance(row["unit"], str) else "")
    require(match is not None and GEN.fullmatch(match.group(1)) is not None, "worker-unit")
    require(JOB.fullmatch(row["job_path"] if isinstance(row["job_path"], str) else "") is not None,
            "systemd-job-path")
    require(row["native_authority"] is False and row["native_acceptance"] is False, "start-authority")
    return match.group(1)


def joined(row, generation, start):
    keys = {"schema", "case", "phase", "generation", "unit", "job_path", "start_reply_observed",
            "pending_start", "lifetime", "original_stop_delegate_calls", "final_unit",
            "final_group_after_pumps", "pumps", "root_custody_completed", "native_authority", "native_acceptance"}
    require(set(row) == keys, "joined-frame-keys")
    require(row["schema"] == "issue779-n10-pending-start-observation-v1" and row["case"] == "N10" and
            row["phase"] == "original-start-stop-and-pumps-joined-before-custody", "joined-frame-identity")
    require(row["generation"] == generation and row["unit"] == start["unit"] and
            row["job_path"] == start["job_path"] and row["start_reply_observed"] is True, "joined-frame-binding")
    pending = row["pending_start"]
    require(type(pending) is dict and set(pending) == {"start_reserved", "started", "start_joined", "closed", "stop_joined", "first_failure"} and
            pending["start_reserved"] is True and pending["started"] is False and
            pending["start_joined"] is True and pending["closed"] is True and
            pending["stop_joined"] is True and pending["first_failure"] == "StartCancelled",
            "original-pending-start")
    life = row["lifetime"]
    require(type(life) is dict and set(life) == {"startup_joined", "startup_failed", "stop_joined", "physically_settled"} and
            life["startup_joined"] is True and life["startup_failed"] is True and life["stop_joined"] is True and
            life["physically_settled"] is False, "original-lifetime-failure")
    require(type(row["original_stop_delegate_calls"]) is int and row["original_stop_delegate_calls"] == 2,
            "two-original-stops")
    final = row["final_unit"]
    require(type(final) is dict and set(final) == {"id", "active_state", "sub_state", "main_pid", "exec_main_pid",
            "exec_main_code", "exec_main_status", "stopped"} and final["id"] == start["unit"] and
            final["stopped"] is True and
            ((final["active_state"] == "inactive" and final["sub_state"] == "dead") or
             (final["active_state"] == "failed" and final["sub_state"] == "failed")) and
            exact_int(final["main_pid"], 0, 0) and
            exact_int(final["exec_main_pid"], 1, 2147483647) and
            exact_int(final["exec_main_code"], 1, 3) and
            ((final["exec_main_code"] == 1 and exact_int(final["exec_main_status"], 0, 255)) or
             (final["exec_main_code"] in (2, 3) and exact_int(final["exec_main_status"], 1, 64))),
            "final-selected-unit")
    group = row["final_group_after_pumps"]
    require(type(group) is dict and set(group) == {"sample_taken", "empty", "exists", "populated", "frozen",
            "device_major", "device_minor", "inode"} and group["sample_taken"] is True and
            group["empty"] is True and type(group["exists"]) is bool and
            ((group["exists"] is False and all(group[key] is None for key in
              ("populated", "frozen", "device_major", "device_minor", "inode"))) or
             (group["exists"] is True and group["populated"] is False and group["frozen"] is False and
              exact_int(group["device_major"], 0, 4294967295) and
              exact_int(group["device_minor"], 0, 4294967295) and
              exact_int(group["inode"], 1, 18446744073709551615))), "final-cgroup-after-pumps")
    pumps = row["pumps"]
    require(type(pumps) is dict and set(pumps) == {"joined", "received_bytes", "discarded_bytes", "failure",
            "stdout_eof", "stdout_failure", "stderr_eof", "stderr_failure"} and pumps["joined"] is True and
            pumps["stdout_eof"] is True and pumps["stderr_eof"] is True and pumps["failure"] == "None" and
            pumps["stdout_failure"] == "None" and pumps["stderr_failure"] == "None" and
            type(pumps["received_bytes"]) is int and pumps["received_bytes"] >= 0 and
            type(pumps["discarded_bytes"]) is int and pumps["discarded_bytes"] == 0, "joined-pumps")
    require(row["root_custody_completed"] is False and row["native_authority"] is False and
            row["native_acceptance"] is False, "joined-authority")
    return {"generation": generation, "worker_unit": start["unit"], "job_path": start["job_path"],
            "pending_start_failure": pending["first_failure"], "original_stop_delegate_calls": 2,
            "startup_joined": True, "startup_failed": True, "stop_joined": True, "physically_settled": False,
            "unit_stopped": True, "final_group_sampled_after_pumps": True, "final_group_empty": True,
            "pumps_joined": True, "stdout_eof": True, "stderr_eof": True, "discarded_bytes": 0,
            "root_custody_completed": False, "native_authority": False, "native_acceptance": False}


def validate_bytes(raw, mode="joined"):
    rows = extract(raw)
    starts = [(line, row) for line, row in rows if row.get("schema") == "issue779-n10-pending-start-phase-v1"]
    ends = [(line, row) for line, row in rows if row.get("schema") == "issue779-n10-pending-start-observation-v1"]
    require(len(starts) == 1, "start-frame-count")
    start_line, start_row = starts[0]
    generation = phase(start_row)
    if mode == "phase":
        require(len(ends) == 0, "joined-frame-before-cancel")
        return {"generation": generation, "worker_unit": start_row["unit"], "job_path": start_row["job_path"],
                "phase_observed": True, "native_authority": False, "native_acceptance": False,
                "stderr_sha256": digest(raw)}
    require(mode == "joined" and len(ends) == 1, "joined-frame-count")
    end_line, end_row = ends[0]
    require(start_line < end_line, "reply-before-joined-observation")
    result = joined(end_row, generation, start_row)
    result["stderr_sha256"] = digest(raw)
    return result


def main():
    parser = argparse.ArgumentParser(allow_abbrev=False)
    parser.add_argument("--mode", choices=("phase", "joined"), required=True)
    parser.add_argument("stderr")
    args = parser.parse_args()
    try:
        print(json.dumps(validate_bytes(read_bounded(args.stderr), args.mode), sort_keys=True, separators=(",", ":")))
        return 0
    except (Reject, OSError) as error:
        category = error.category if isinstance(error, Reject) else "stderr-read"
        print("N10_RECORD_REJECTED:" + category, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
