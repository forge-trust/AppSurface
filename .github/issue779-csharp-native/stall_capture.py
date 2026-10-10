"""Private N11 original-holder capture, with no admission or runtime authority.

The source-bound native fixture supplies its generated generation, original
fixture observer BOOTTIME cutoff and independently audited image revisions. This module only reads
the original root's completed frame/descriptor, checks quarantine and fresh
generated cgroup/NSS state, and retains bounded evidence. It never signals,
starts a workload, changes account ownership, deletes evidence or frees IDs.
"""
from __future__ import annotations

import base64
import datetime
import grp
import hashlib
import json
import os
import pwd
import re
import stat
import time

from check_stall_record import check_stall_record
from root_stall_frame import parse_root_stall_frame

MAX_FRAME = 70 * 1024 - 1
DESCRIPTOR_KEYS = frozenset((
    "schema", "run_id", "worker_pid", "broker_pid", "worker_uid", "worker_gid",
    "subject_uid", "subject_gid", "unit", "cgroup", "job_deadline_utc", "tool_root",
    "subject_root", "output_parent", "output_slot", "dotnet_path", "test_output_root",
    "policy_file", "mode", "socket_path", "descriptor_path", "entry_sha256",
    "policy_sha256", "base_revision", "subject_revision", "workflow_identity",
    "provider", "platform", "proof_digest", "output_parent_identity",
    "observation_profile_ids", "observation_producer_ids", "paths", "admission_seconds",
    "start_seconds", "collection_seconds", "cleanup_seconds", "stopping_seconds",
    "diff_file", "diff_sha256", "solution",
))
FAILURE_KEYS = frozenset(("schema", "phase", "error_kind", "diagnostic_code",
                         "account_failure", "control_failure", "custody_failure"))


def require(value):
    if not value:
        raise ValueError("stall-capture-rejected")


def digest(raw):
    return hashlib.sha256(raw).hexdigest()


def pairs(items):
    result = {}
    folded = set()
    for name, value in items:
        require(type(name) is str and name.casefold() not in folded)
        folded.add(name.casefold())
        result[name] = value
    return result


def decode(raw, maximum):
    require(type(raw) is bytes and 0 < len(raw) <= maximum)
    return json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=pairs,
                      parse_constant=lambda _: require(False))


def uint(value, minimum=1, maximum=(1 << 32) - 2):
    require(type(value) is int and minimum <= value <= maximum)
    return value


REQUEST_KEYS = frozenset(("schema", "mode", "tool_root", "runtime_root", "runtime_host",
    "entry_path", "policy_file", "subject_root", "base_revision", "subject_revision",
    "workflow_identity", "paths", "observation_profile_ids", "observation_producer_ids",
    "job_deadline_utc", "admission_seconds", "start_seconds", "collection_seconds",
    "cleanup_seconds", "stopping_seconds"))

# Closed on-disk evidence contract. The request is an original root-owned input,
# retained byte-for-byte after the parser has compared its deadline to the worker
# descriptor. The final observation is generated only after filesystem/NSS checks.
ORIGINAL_RECORD_NAMES = frozenset((
    "root.stderr", "original-failed-settlement.json", "root-failure.json",
    "worker-control.json", "worker.stdout", "worker.stderr", "original-request.json",
))
RETAINED_RECORD_NAMES = ORIGINAL_RECORD_NAMES | frozenset(("filesystem-nss-observation.json",))


def utc_ticks(value):
    """Compare UTC instants without discarding .NET's seventh fractional digit."""
    require(type(value) is str)
    match = re.fullmatch(r"([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|\+00:00)", value)
    require(match is not None)
    parts = tuple(int(match.group(i)) for i in range(1, 7))
    datetime.datetime(*parts, tzinfo=datetime.timezone.utc)
    return (*parts, int((match.group(7) or "").ljust(7, "0")))


def requested_deadline(request, descriptor_deadline):
    """Validate retained request data; this comparison grants no native authority."""
    value = decode(request, 65536)
    require(type(value) is dict and set(value) == REQUEST_KEYS
            and value["schema"] == "evidence-supervisor-linux-v1")
    require(utc_ticks(value["job_deadline_utc"]) == utc_ticks(descriptor_deadline))
    return value["job_deadline_utc"]


def _parse_original(frame, descriptor, *, request, generation, source, base, entry_sha, policy_sha,
                    runtime_relative_host, workflow):
    require(type(generation) is str and re.fullmatch("[0-9a-f]{32}", generation)
            and generation != "0" * 32)
    require(all(type(v) is str and re.fullmatch("[0-9a-f]{40}", v) for v in (source, base)))
    require(all(type(v) is str and re.fullmatch("[0-9a-f]{64}", v) for v in (entry_sha, policy_sha)))
    require(type(runtime_relative_host) is str and len(runtime_relative_host.encode()) <= 4096
            and all(c not in ("", ".", "..") for c in runtime_relative_host.split("/"))
            and not any(ord(c) < 32 or ord(c) == 127 for c in runtime_relative_host))
    require(type(workflow) is str and 0 < len(workflow.encode()) <= 4096
            and not any(ord(c) < 32 or ord(c) == 127 for c in workflow))
    record, failure = parse_root_stall_frame(frame)
    require(set(failure) == FAILURE_KEYS
            and failure["phase"] in ("ServerRun", "ServerCompletion", "WorkerExit", "WorkerCompletion")
            and failure["error_kind"] in ("Admission", "ControlLine", "Cancelled", "InvalidOperation")
            and failure["diagnostic_code"] in (None, "ASEVD410", "ASEVD420")
            and failure["account_failure"] is None and failure["custody_failure"] is None)
    control = failure["control_failure"]
    if control is not None:
        require(type(control) is dict and set(control) == {"stage", "operation", "error_kind", "diagnostic_code"}
                and control["stage"] in ("RequestRead", "RequestClassify", "RequestLifetime", "AcceptLoop",
                    "Accept", "AcceptJoin", "HandlerJoin", "CapacityWait", "HandlerDispatch",
                    "ControlRegistration", "HandlerFailureCommit", "WorkerTerminalTaskCompleted", "ProtocolIncomplete")
                and control["operation"] in (None, "Ready", "Stop", "Wait", "Exit")
                and control["error_kind"] in ("Unknown", "Admission", "ControlLine", "Cancelled", "InvalidOperation")
                and control["diagnostic_code"] in (None, "ASEVD402", "ASEVD410", "ASEVD420"))
    d = decode(descriptor, 65536)
    require(type(d) is dict and set(d) == DESCRIPTOR_KEYS)
    root = "/run/appsurface-evidence-" + generation
    unit = "appsurface-evidence-worker-" + generation + ".service"
    deployment = "/var/lib/appsurface-evidence-fixture/" + generation
    require(d["schema"] == "evidence-worker-linux-v1" and d["run_id"] == "csharp/" + generation
            and d["unit"] == unit and d["cgroup"] == "/system.slice/" + unit
            and d["descriptor_path"] == root + "/worker/worker-control.json"
            and d["socket_path"] == root + "/worker/broker/control.sock"
            and d["output_parent"] == root + "/output" and d["output_slot"] == "evidence"
            and d["test_output_root"] == root + "/raw-results"
            and d["tool_root"] == deployment + "/tool" and d["subject_root"] == deployment + "/subject"
            and d["policy_file"] == deployment + "/tool/fixture-policy.json"
            and d["dotnet_path"] == deployment + "/runtime/" + runtime_relative_host
            and d["workflow_identity"] == workflow
            and d["mode"] == "observation" and d["provider"] == "github-actions"
            and d["platform"] == "linux-x64" and d["proof_digest"] == ""
            and d["base_revision"] == base and d["subject_revision"] == source
            and d["entry_sha256"] == entry_sha and d["policy_sha256"] == policy_sha
            and d["observation_profile_ids"] == ["empty"] and d["observation_producer_ids"] == []
            and d["paths"] == ["docs/designs/issue-779-csharp-supervision-core.md"]
            and all(d[k] is None for k in ("diff_file", "diff_sha256", "solution")))
    requested_deadline(request, d["job_deadline_utc"])
    for key, expected in (("admission_seconds", 10), ("start_seconds", 30),
                          ("collection_seconds", 10), ("cleanup_seconds", 60), ("stopping_seconds", 5)):
        require(type(d[key]) is int and d[key] == expected)
    output_identity = d["output_parent_identity"]
    require(type(output_identity) is dict
            and set(output_identity) == {"device_major", "device_minor", "inode", "uid", "gid"})
    for key in ("device_major", "device_minor", "uid", "gid"):
        uint(output_identity[key], 0, (1 << 32) - 1)
    uint(output_identity["inode"], 1, (1 << 64) - 1)
    for name in ("worker_uid", "worker_gid", "subject_uid", "subject_gid"):
        uint(d[name])
    uint(d["worker_pid"], 1, (1 << 31) - 1)
    uint(d["broker_pid"], 1, (1 << 31) - 1)
    require(d["worker_uid"] != d["subject_uid"] and d["worker_gid"] != d["subject_gid"]
            and d["worker_pid"] != d["broker_pid"])
    # Full exported bytes come from the original collector. They are checked
    # again by the existing detached consistency API; no prefix substitutes.
    raw = {}
    for name in ("stdout", "stderr"):
        encoded = record["pumps"][name]["raw_base64"]
        require(type(encoded) is str and len(encoded) <= 43692)
        raw[name] = base64.b64decode(encoded, validate=True)
        require(base64.b64encode(raw[name]).decode("ascii") == encoded)
    record_bytes = frame.split(b"\n", 1)[0] + b"\n"
    consistency = check_stall_record(record_bytes, worker_stdout=raw["stdout"], worker_stderr=raw["stderr"],
        expected_generation=generation, expected_pid=d["worker_pid"],
        expected_starttime_ticks=record["process"]["starttime_ticks"], expected_uid=d["worker_uid"],
        expected_gid=d["worker_gid"], expected_descriptor_sha256=digest(descriptor))
    original_records = {
        "root.stderr": frame, "original-failed-settlement.json": record_bytes,
        "root-failure.json": frame.split(b"\n")[1] + b"\n", "worker-control.json": descriptor,
        "worker.stdout": raw["stdout"], "worker.stderr": raw["stderr"],
        "original-request.json": request,
    }
    require(set(original_records) == ORIGINAL_RECORD_NAMES)
    return d, consistency, original_records


def parse_original(frame, descriptor, **expected):
    """Detached consistency only: supplied JSON cannot establish native identity."""
    try:
        return _parse_original(frame, descriptor, **expected)
    except (ValueError, TypeError, KeyError, UnicodeError, OverflowError, RecursionError):
        raise ValueError("stall-capture-rejected") from None


def identity(s):
    return (s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid, s.st_nlink,
            s.st_size, s.st_mtime_ns, s.st_ctime_ns)


class NativeCapture:
    """Root-selected retained read descriptors under the fixed fixture observer cutoff."""
    def __init__(self, end_ms):
        require(type(end_ms) is int and end_ms > 0)
        self.end = end_ms / 1000
        self.fds = []

    def check(self):
        require(time.clock_gettime(time.CLOCK_BOOTTIME) < self.end)

    def directory(self, parent, name, uid, gid, mode):
        self.check()
        before = os.stat(name, dir_fd=parent, follow_symlinks=False)
        require(stat.S_ISDIR(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and stat.S_IMODE(before.st_mode) == mode)
        fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        self.fds.append(fd)
        require(identity(os.fstat(fd)) == identity(before))
        self.check()
        return fd, before

    def read(self, parent, name, uid, gid, mode, maximum):
        self.check()
        before = os.stat(name, dir_fd=parent, follow_symlinks=False)
        require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                and stat.S_IMODE(before.st_mode) == mode and before.st_nlink == 1 and before.st_size <= maximum)
        fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        try:
            require(identity(os.fstat(fd)) == identity(before))
            chunks = []
            count = 0
            while True:
                self.check()
                chunk = os.read(fd, min(65536, maximum + 1 - count))
                if not chunk:
                    break
                count += len(chunk)
                require(count <= maximum)
                chunks.append(chunk)
            require(count == before.st_size and identity(os.fstat(fd)) == identity(before)
                    and identity(os.stat(name, dir_fd=parent, follow_symlinks=False)) == identity(before))
            self.check()
            return b"".join(chunks)
        finally:
            os.close(fd)

    def write(self, parent, name, raw):
        self.check()
        require(type(raw) is bytes and len(raw) <= MAX_FRAME and re.fullmatch("[a-z.-]+", name))
        fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=parent)
        try:
            os.fchmod(fd, 0o600)
            offset = 0
            while offset < len(raw):
                self.check()
                wrote = os.write(fd, raw[offset:])
                require(wrote > 0)
                offset += wrote
            os.fsync(fd)
            self.check()
        finally:
            os.close(fd)

    def close(self):
        failed = False
        for fd in reversed(self.fds):
            try:
                os.close(fd)
            except OSError:
                failed = True
        self.fds.clear()
        require(not failed)

    def group_empty(self, root, unit):
        """Fresh exact generated group; a missing group is recorded as pruned."""
        parent = root
        for name in ("sys", "fs", "cgroup", "system.slice"):
            self.check()
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
            self.fds.append(fd)
            s = os.fstat(fd)
            require(s.st_uid == s.st_gid == 0 and not stat.S_IMODE(s.st_mode) & 0o022)
            parent = fd
        self.check()
        try:
            before = os.stat(unit, dir_fd=parent, follow_symlinks=False)
        except FileNotFoundError:
            self.check()
            return {"exists": False, "populated": None, "frozen": None, "inode": None}
        require(stat.S_ISDIR(before.st_mode) and before.st_uid == before.st_gid == 0
                and not stat.S_IMODE(before.st_mode) & 0o022)
        fd = os.open(unit, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=parent)
        self.fds.append(fd)
        require(identity(os.fstat(fd)) == identity(before))
        events = os.open("cgroup.events", os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
        try:
            s = os.fstat(events)
            require(stat.S_ISREG(s.st_mode) and s.st_uid == s.st_gid == 0)
            raw = b""
            while True:
                self.check()
                chunk = os.read(events, 1025 - len(raw))
                if not chunk:
                    break
                raw += chunk
                require(len(raw) <= 1024)
            values = {}
            for line in raw.decode("ascii").splitlines():
                name, value = line.split()
                require(name not in values and name in ("populated", "frozen") and value in ("0", "1"))
                values[name] = value
            require(values == {"populated": "0", "frozen": "0"} and identity(os.fstat(events)) == identity(s))
        finally:
            os.close(events)
        require(identity(os.fstat(fd)) == identity(before)
                and identity(os.stat(unit, dir_fd=parent, follow_symlinks=False)) == identity(before))
        self.check()
        return {"exists": True, "populated": False, "frozen": False, "inode": before.st_ino}


def capture(private, generation, source, base, entry_sha, policy_sha,
            runtime_relative_host, workflow, end_ms):
    """Retain original complete evidence and fixed generated quarantine observations.

    Native qualification additionally requires the fixture's independently
    verified build/deployment and original root launch/exit/time/cleanup receipt.
    Expected starttime here comes from the source-bound original retained holder;
    it is not advertised as a second independent pre-exit process sample.
    """
    require(os.geteuid() == 0 and type(generation) is str
            and re.fullmatch("[0-9a-f]{32}", generation) and generation != "0" * 32
            and private == "/run/appsurface-evidence-fixture/" + generation)
    capture_owner = NativeCapture(end_ms)
    result = None
    try:
        c = capture_owner
        c.check()
        root = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        c.fds.append(root)
        s = os.fstat(root)
        require(s.st_uid == s.st_gid == 0 and not stat.S_IMODE(s.st_mode) & 0o022)
        run = os.open("run", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=root)
        c.fds.append(run)
        s = os.fstat(run)
        require(s.st_uid == s.st_gid == 0 and not stat.S_IMODE(s.st_mode) & 0o022)
        fixtures, _ = c.directory(run, "appsurface-evidence-fixture", 0, 0, 0o700)
        private_fd, _ = c.directory(fixtures, generation, 0, 0, 0o700)
        log, _ = c.directory(private_fd, "logs", 0, 0, 0o700)
        frame = c.read(log, "n01.stderr", 0, 0, 0o600, MAX_FRAME)
        require(c.read(log, "n01.stdout", 0, 0, 0o600, 0) == b"")
        workspace_name = "appsurface-evidence-" + generation
        s = os.stat(workspace_name, dir_fd=run, follow_symlinks=False)
        require(stat.S_ISDIR(s.st_mode) and s.st_uid == 0 and s.st_gid > 0
                and stat.S_IMODE(s.st_mode) == 0o750)
        work, before_work = c.directory(run, workspace_name, 0, s.st_gid, 0o750)
        control, _ = c.directory(work, "worker", 0, s.st_gid, 0o710)
        descriptor = c.read(control, "worker-control.json", 0, s.st_gid, 0o440, 65536)
        request = c.read(private_fd, "request.json", 0, 0, 0o600, 65536)
        d, consistency, records = parse_original(frame, descriptor, request=request, generation=generation,
            source=source, base=base, entry_sha=entry_sha, policy_sha=policy_sha,
            runtime_relative_host=runtime_relative_host, workflow=workflow)
        require(d["worker_gid"] == s.st_gid)
        out, before_out = c.directory(work, "output", d["worker_uid"], d["worker_gid"], 0o700)
        require(d["output_parent_identity"] == {
            "device_major": os.major(before_out.st_dev), "device_minor": os.minor(before_out.st_dev),
            "inode": before_out.st_ino, "uid": before_out.st_uid, "gid": before_out.st_gid})
        require(os.listdir(out) == [])
        raw_s = os.stat("raw-results", dir_fd=work, follow_symlinks=False)
        require(stat.S_ISDIR(raw_s.st_mode) and raw_s.st_uid == d["subject_uid"] and raw_s.st_gid > 0
                and raw_s.st_gid not in (d["worker_gid"], d["subject_gid"])
                and stat.S_IMODE(raw_s.st_mode) == 0o710)
        raw, _ = c.directory(work, "raw-results", d["subject_uid"], raw_s.st_gid, 0o710)
        require(os.listdir(raw) == [])
        require(not os.path.lexists("/proc/" + str(d["worker_pid"]))
                and not os.path.lexists("/proc/" + str(d["broker_pid"])))
        worker_group = c.group_empty(root, d["unit"])
        owner_group = c.group_empty(root, "appsurface-evidence-owner-" + generation + ".service")
        c.check()
        users = []
        for name, uid, gid in (("evw" + generation[:28], d["worker_uid"], d["worker_gid"]),
                               ("evs" + generation[:28], d["subject_uid"], d["subject_gid"])):
            by_name = pwd.getpwnam(name)
            by_id = pwd.getpwuid(uid)
            c.check()
            require(by_name == by_id and by_name.pw_name == name
                    and by_name.pw_uid == uid and by_name.pw_gid == gid)
            users.append({"name": name, "uid": uid, "gid": gid, "retained": True})
        groups = []
        for name, gid in (("evw" + generation[:28], d["worker_gid"]),
                          ("evs" + generation[:28], d["subject_gid"]), ("evr" + generation[:28], raw_s.st_gid)):
            by_name = grp.getgrnam(name)
            by_id = grp.getgrgid(gid)
            c.check()
            require(by_name == by_id and by_name.gr_name == name
                    and by_name.gr_gid == gid and by_name.gr_mem == [])
            groups.append({"name": name, "gid": gid, "retained": True})
        require(identity(os.fstat(out)) == identity(before_out)
                and identity(os.stat("output", dir_fd=work, follow_symlinks=False)) == identity(before_out)
                and identity(os.fstat(work)) == identity(before_work)
                and identity(os.stat(workspace_name, dir_fd=run, follow_symlinks=False)) == identity(before_work))
        observation = {"schema": "issue779-n11-filesystem-nss-v1", "generation": generation,
            "output_parent_descriptor_identity_match": True, "output_slot_present": False,
            "raw_results_empty": True, "account_disposition": "preserved-quarantined",
            "users": users, "groups": groups, "original_worker_pid_absent": True,
            "original_broker_pid_absent": True, "fresh_generated_worker_group": worker_group,
            "fresh_generated_owner_group": owner_group,
            "native_acceptance": False, "native_authority": False}
        records["filesystem-nss-observation.json"] = (json.dumps(observation,
            sort_keys=True, separators=(",", ":")) + "\n").encode()
        require(set(records) == RETAINED_RECORD_NAMES)
        for name, raw in records.items():
            c.write(private_fd, name, raw)
        c.check()
        result = {"schema": "issue779-n11-private-capture-v1", "generation": generation,
            "source": source, "entry_sha256": entry_sha, "policy_sha256": policy_sha,
            "record_files": {name: {"bytes": len(raw), "sha256": digest(raw)} for name, raw in records.items()},
            "job_deadline_utc": requested_deadline(request, d["job_deadline_utc"]),
            "observer_deadline_kind": "fixture-observer-boottime",
            "observer_end_boottime_ms": end_ms,
            "consistency": consistency, "observation_only": True,
            "native_acceptance": False, "native_authority": False}
    except (ValueError, TypeError, OSError, KeyError, UnicodeError, OverflowError, RecursionError):
        raise ValueError("stall-capture-rejected") from None
    finally:
        capture_owner.close()
    capture_owner.check()
    return result
