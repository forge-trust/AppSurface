"""Private kernel/process snapshots, never admission or owned-exit authority.

The importing root launcher validates selected unit properties before observe().
All per-observation FDs close before returning; finish returns memory-only bytes. No subprocess, thread, socket, signal,
unit query, command execution, environment read, or execution-result mutation exists.
The root-path/owner keyword seam is portable fixture data only. See the private
collector-sampling README for ordering and incomplete-observation limits.
"""
import json
import math
import os
import re
import stat
import time

UNIT = re.compile(r"evidencehost-[0-9a-f]{12}-s-(0|[1-9][0-9]{0,2})\.service\Z", re.ASCII)
MAX_SUBJECT_UNITS = 128
DIRECTORY_FLAGS = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
FILE_FLAGS = os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC
MAX_PIDS = 16
MAX_GROUPS = 16
MAX_SAMPLES = 16
MIN_INTERVAL = 0.33
WINDOW_SECONDS = 5.0
OBSERVATION_SECONDS = 0.1
MAX_OUTPUT = 64 * 1024
MAX_NUMBER = (1 << 63) - 1
SDK_RELATIVE = "sdk/10.0.401/"
ROLES = ("dotnet", "vstest.console", "datacollector", "testhost", "unknown")
MEMORY_EVENTS = ("low", "high", "max", "oom", "oom_kill", "oom_group_kill")


class _Rejected(Exception):
    pass


def _need(value):
    if not value:
        raise _Rejected()


def _remaining(deadline):
    _need(time.monotonic() < deadline)


def _identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _directory_identity(info):
    """Pin directory identity and access metadata, not mutable child statistics."""
    return info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid


def _directory(info, uid, gid, exact_mode=None):
    mode = stat.S_IMODE(info.st_mode)
    _need(stat.S_ISDIR(info.st_mode) and info.st_uid == uid and info.st_gid == gid
          and info.st_nlink > 0 and mode & 0o7022 == 0)
    if exact_mode is not None:
        _need(mode == exact_mode)


def _number(raw):
    """Intentionally pure bounded ASCII counter parser; absence is not zero."""
    _need(isinstance(raw, bytes) and re.fullmatch(rb"[0-9]{1,19}", raw))
    value = int(raw)
    _need(value <= MAX_NUMBER)
    return value


def parse_limit(raw):
    """Return a closed nullable value/unbounded pair; malformed input rejects."""
    if raw.strip() == b"max":
        return {"value": None, "unbounded": True}
    return {"value": _number(raw.strip()), "unbounded": False}


def parse_events(raw, keys):
    """Pure event projection: missing keys stay null; duplicate/unknown keys reject."""
    values = dict.fromkeys(keys)
    for line in raw.splitlines():
        words = line.split()
        _need(len(words) == 2)
        name = words[0].decode("ascii")
        _need(name in values and values[name] is None)
        values[name] = _number(words[1])
    return values


def parse_status(raw, uid, gid):
    """Project exact four UID/GID entries and bounded actual supplementary groups."""
    selected = {}
    for line in raw.splitlines():
        key, sep, rest = line.partition(b":")
        if key in (b"Uid", b"Gid", b"Groups"):
            _need(sep and key not in selected)
            selected[key] = tuple(_number(part) for part in rest.split())
    _need(set(selected) == {b"Uid", b"Gid", b"Groups"})
    _need(selected[b"Uid"] == (uid,) * 4 and selected[b"Gid"] == (gid,) * 4
          and len(selected[b"Groups"]) <= MAX_GROUPS
          and all(value <= 0xffffffff for value in selected[b"Groups"]))
    return {"uid": selected[b"Uid"], "gid": selected[b"Gid"], "groups": selected[b"Groups"]}


def parse_start_time(raw, pid):
    """Parse kernel stat field 22 without exporting the process comm string."""
    prefix, sep, tail = raw.rpartition(b")")
    _need(sep and prefix.startswith(str(pid).encode() + b" (")
          and b"\n" not in prefix)
    fields = tail.split()
    _need(len(fields) >= 20 and len(fields[0]) == 1)
    value = _number(fields[19])
    _need(value > 0)
    return value


def _read_at(parent_fd, name, uid, gid, cap, deadline):
    _remaining(deadline)
    before = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
    _need(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
          and before.st_uid == uid and before.st_gid == gid
          and stat.S_IMODE(before.st_mode) & 0o7022 == 0
          and 0 <= before.st_size <= cap)
    fd = os.open(name, FILE_FLAGS, dir_fd=parent_fd)
    try:
        _need(_identity(os.fstat(fd)) == _identity(before))
        chunks = []
        length = 0
        while True:
            _remaining(deadline)
            block = os.read(fd, min(4096, cap - length + 1))
            if not block:
                break
            length += len(block)
            _need(length <= cap)
            chunks.append(block)
        _remaining(deadline)
        _need(_identity(os.fstat(fd)) == _identity(before)
              == _identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)))
        return b"".join(chunks)
    finally:
        os.close(fd)


def _open_directory(parent_fd, name, uid, gid, deadline):
    _remaining(deadline)
    before = os.stat(name, dir_fd=parent_fd, follow_symlinks=False)
    _directory(before, uid, gid)
    fd = os.open(name, DIRECTORY_FLAGS, dir_fd=parent_fd)
    try:
        _need(_directory_identity(os.fstat(fd)) == _directory_identity(before))
        return fd, before
    except BaseException:
        os.close(fd)
        raise


def _open_root(path, uid, gid, deadline):
    _remaining(deadline)
    _need(os.path.realpath(path) == path)
    before = os.stat(path, follow_symlinks=False)
    _directory(before, uid, gid)
    fd = os.open(path, DIRECTORY_FLAGS)
    try:
        _need(_directory_identity(os.fstat(fd)) == _directory_identity(before))
        return fd, before
    except BaseException:
        os.close(fd)
        raise


def _same_directory(fd, info, parent_fd=None, name=None):
    _need(_directory_identity(os.fstat(fd)) == _directory_identity(info))
    if parent_fd is not None:
        _need(_directory_identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)) == _directory_identity(info))


def _kernel_group(raw, expected):
    _need(raw == b"0::" + expected.encode("ascii") + b"\n")


def _role(raw, executable, dotnet_host, sdk_root):
    _need(len(raw) <= 8192 and raw.endswith(b"\0"))
    args = raw[:-1].split(b"\0")
    _need(1 <= len(args) <= 64 and all(len(arg) <= 4096 for arg in args))
    if executable != dotnet_host or args[0] not in (dotnet_host.encode(), b"dotnet"):
        return "unknown"
    rest = args[1:]
    if rest and rest[0] == b"exec":
        rest = rest[1:]
        paired = {b"--depsfile", b"--runtimeconfig", b"--additionalprobingpath",
                  b"--additional-deps", b"--fx-version", b"--roll-forward"}
        while rest and rest[0].startswith(b"-"):
            if rest[0] not in paired or len(rest) < 2:
                return "dotnet"
            rest = rest[2:]
    names = {b"vstest.console.dll": "vstest.console", b"datacollector.dll": "datacollector",
             b"testhost.dll": "testhost"}
    if rest:
        for name, role in names.items():
            if rest[0] == (sdk_root + "/" + SDK_RELATIVE).encode() + name:
                return role
    return "dotnet"


class CollectorStartupSampler:
    """Diagnostic-only synchronous sampler; no transport or authority is provided.

    Constructor records selection only. observe() returns None, including rejection.
    finish() returns bounded memory-only bytes or None, once; no FD persists.
    Root calls use default paths/owner zero. Overrides label ordinary fixture data.
    """
    def __init__(self, unit, cgroup_path, subject_uid, subject_gid, dotnet_host,
                 sdk_root, job_deadline, *,
                 expected_root_uid=0, expected_root_gid=0, proc_root="/proc",
                 cgroup_root="/sys/fs/cgroup"):
        self._valid = False
        self._finished = False
        self._failed = False
        self._samples = []
        self._trigger = None
        self._next = 0.0
        try:
            _need(_selected_unit(unit, cgroup_path))
            _need(all(type(v) is int and 0 < v <= 0xffffffff for v in (subject_uid, subject_gid)))
            _need(all(type(v) is int and 0 <= v <= 0xffffffff for v in (expected_root_uid, expected_root_gid)))
            _need(type(job_deadline) in (int, float) and math.isfinite(job_deadline))
            for path in (sdk_root, dotnet_host, proc_root, cgroup_root):
                _need(isinstance(path, str) and path.startswith("/") and "\0" not in path
                      and os.path.normpath(path) == path)
            _need(dotnet_host == sdk_root + "/dotnet")
            self._unit, self._group = unit, cgroup_path
            self._uid, self._gid = subject_uid, subject_gid
            self._root_uid, self._root_gid = expected_root_uid, expected_root_gid
            self._host, self._sdk = dotnet_host, sdk_root
            self._proc, self._cgroup = proc_root, cgroup_root
            self._deadline = float(job_deadline)
            self._valid = True
        except Exception:
            self._failed = True

    def _process(self, proc_fd, pid, deadline):
        fd = -1
        try:
            fd, info = _open_directory(proc_fd, str(pid), self._uid, self._gid, deadline)
            before_stat = _read_at(fd, "stat", self._uid, self._gid, 4096, deadline)
            start = parse_start_time(before_stat, pid)
            identity = parse_status(_read_at(fd, "status", self._uid, self._gid, 8192, deadline), self._uid, self._gid)
            group = _read_at(fd, "cgroup", self._uid, self._gid, 1024, deadline)
            _kernel_group(group, self._group)
            command = _read_at(fd, "cmdline", self._uid, self._gid, 8192, deadline)
            _remaining(deadline)
            exe = os.readlink("exe", dir_fd=fd)
            role = _role(command, exe, self._host, self._sdk)
            _need(parse_start_time(_read_at(fd, "stat", self._uid, self._gid, 4096, deadline), pid) == start)
            _need(parse_status(_read_at(fd, "status", self._uid, self._gid, 8192, deadline), self._uid, self._gid) == identity)
            _need(_read_at(fd, "cgroup", self._uid, self._gid, 1024, deadline) == group)
            _need(_read_at(fd, "cmdline", self._uid, self._gid, 8192, deadline) == command)
            _remaining(deadline)
            _need(os.readlink("exe", dir_fd=fd) == exe)
            _same_directory(fd, info, proc_fd, str(pid))
            _remaining(deadline)
            return dict(identity, pid=pid, start_time=start, role=role)
        except Exception:
            return None
        finally:
            if fd >= 0:
                os.close(fd)

    def _counter(self, fd, name, parser, deadline):
        try:
            return parser(_read_at(fd, name, self._root_uid, self._root_gid, 4096, deadline))
        except Exception:
            return None

    def _sample(self, deadline):
        owned = []
        try:
            cg, cg_info = _open_root(self._cgroup, self._root_uid, self._root_gid, deadline)
            owned.append(cg)
            system, system_info = _open_directory(cg, "system.slice", self._root_uid, self._root_gid, deadline)
            owned.append(system)
            group, group_info = _open_directory(system, self._unit, self._root_uid, self._root_gid, deadline)
            owned.append(group)
            proc, proc_info = _open_root(self._proc, self._root_uid, self._root_gid, deadline)
            owned.append(proc)
            raw = _read_at(group, "cgroup.procs", self._root_uid, self._root_gid, 4096, deadline)
            pids = tuple(_number(line) for line in raw.splitlines())
            _need(len(pids) <= MAX_PIDS and len(set(pids)) == len(pids)
                  and all(0 < pid <= 2147483647 for pid in pids))
            facts = []
            unavailable = 0
            for pid in pids:
                _remaining(deadline)
                fact = self._process(proc, pid, deadline)
                if fact is None:
                    unavailable += 1
                else:
                    facts.append(fact)
            _remaining(deadline)
            now = time.monotonic()
            if self._trigger is None:
                if not any(fact["role"] == "datacollector" for fact in facts):
                    return None
                self._trigger = now
            _remaining(min(deadline, self._trigger + WINDOW_SECONDS))
            counters = {
                "pids_current": self._counter(group, "pids.current", lambda r: _number(r.strip()), deadline),
                "pids_max": self._counter(group, "pids.max", parse_limit, deadline),
                "pids_events": self._counter(group, "pids.events", lambda r: parse_events(r, ("max",)), deadline),
                "memory_current": self._counter(group, "memory.current", lambda r: _number(r.strip()), deadline),
                "memory_max": self._counter(group, "memory.max", parse_limit, deadline),
                "memory_events": self._counter(group, "memory.events", lambda r: parse_events(r, MEMORY_EVENTS), deadline),
            }
            _same_directory(group, group_info, system, self._unit)
            _same_directory(system, system_info, cg, "system.slice")
            _same_directory(cg, cg_info)
            _same_directory(proc, proc_info)
            _remaining(min(deadline, self._trigger + WINDOW_SECONDS))
            return {"elapsed_ms": int((time.monotonic() - self._trigger) * 1000),
                    "processes": facts, "unavailable_pids": unavailable, "counters": counters}
        finally:
            first_error = None
            for fd in reversed(owned):
                try:
                    os.close(fd)
                except OSError as error:
                    first_error = first_error or error
            if first_error is not None:
                raise first_error

    def observe(self):
        """Read at most one snapshot, only when due; exceptions never reach root _run."""
        try:
            if not self._valid or self._finished or self._failed:
                return None
            now = time.monotonic()
            if now >= self._deadline or now < self._next or len(self._samples) >= MAX_SAMPLES:
                return None
            if self._trigger is not None and now >= self._trigger + WINDOW_SECONDS:
                return None
            self._next = now + MIN_INTERVAL
            limit = min(self._deadline, now + OBSERVATION_SECONDS)
            if self._trigger is not None:
                limit = min(limit, self._trigger + WINDOW_SECONDS)
            sample = self._sample(limit)
            if sample is not None:
                _remaining(limit)
                self._samples.append(sample)
                if len(self._json()) > MAX_OUTPUT:
                    self._samples.pop()
                    self._failed = True
        except Exception:
            self._failed = True
        return None

    def _json(self):
        return (json.dumps({"schema": "issue779-private-collector-startup-v1",
                           "unit": self._unit, "cgroup": self._group,
                           "subject_uid": self._uid, "subject_gid": self._gid,
                           "collector_observed": self._trigger is not None,
                           "capture_incomplete": self._failed,
                           "sample_count": len(self._samples), "samples": self._samples,
                           "network_observed": False, "authority": False},
                          sort_keys=True, separators=(",", ":")) + "\n").encode("ascii")

    def finish(self):
        """Freeze once; bytes mean diagnostic availability, never execution success.

        No write, no retained FD, and no observer after this call. Parent alone
        validates/binds these bytes and writes after its owned-exit/idle guards.
        """
        if self._finished:
            return None
        self._finished = True
        try:
            if not self._valid or not self._samples:
                return None
            _remaining(self._deadline)
            raw = self._json()
            _need(validate_snapshot(raw))
            _remaining(self._deadline)
            return raw
        except Exception:
            return None


def _selected_unit(unit, group):
    if not isinstance(unit, str):
        return False
    match = UNIT.fullmatch(unit)
    return match is not None and int(match[1]) < MAX_SUBJECT_UNITS and group == "/system.slice/" + unit


def _integer(value, low=0, high=MAX_NUMBER):
    return type(value) is int and low <= value <= high


def _nullable(value):
    return value is None or _integer(value)


def _unique(pairs):
    result = {}
    for key, value in pairs:
        _need(key not in result)
        result[key] = value
    return result


def validate_snapshot(payload):
    """Pure closed/canonical schema validation, without origin or authority claims.

    Parent MUST additionally bind unit/cgroup/UID/GID to its recorded selection.
    This function does not establish that kernel sampling or ownership occurred.
    """
    try:
        _need(type(payload) is bytes and 0 < len(payload) <= MAX_OUTPUT)
        data = json.loads(payload, object_pairs_hook=_unique)
        _need(type(data) is dict and set(data) == {"schema", "unit", "cgroup", "subject_uid",
              "subject_gid", "collector_observed", "capture_incomplete", "sample_count", "samples", "network_observed", "authority"})
        _need(data["schema"] == "issue779-private-collector-startup-v1"
              and _selected_unit(data["unit"], data["cgroup"])
              and _integer(data["subject_uid"], 1, 0xffffffff)
              and _integer(data["subject_gid"], 1, 0xffffffff)
              and data["collector_observed"] is True
              and type(data["capture_incomplete"]) is bool
              and data["network_observed"] is False and data["authority"] is False)
        samples = data["samples"]
        _need(type(samples) is list and 1 <= len(samples) <= MAX_SAMPLES
              and _integer(data["sample_count"], 1, MAX_SAMPLES) and data["sample_count"] == len(samples))
        previous = -1
        collector = False
        for sample in samples:
            _need(type(sample) is dict and set(sample) == {"elapsed_ms", "processes", "unavailable_pids", "counters"}
                  and _integer(sample["elapsed_ms"], 0, 5000) and sample["elapsed_ms"] >= previous)
            previous = sample["elapsed_ms"]
            processes = sample["processes"]
            _need(type(processes) is list and len(processes) <= MAX_PIDS
                  and _integer(sample["unavailable_pids"], 0, MAX_PIDS)
                  and len(processes) + sample["unavailable_pids"] <= MAX_PIDS)
            pids = set()
            for process in processes:
                _need(type(process) is dict and set(process) == {"pid", "start_time", "uid", "gid", "groups", "role"}
                      and _integer(process["pid"], 1, 2147483647) and process["pid"] not in pids
                      and _integer(process["start_time"], 1) and process["role"] in ROLES)
                pids.add(process["pid"])
                _need(type(process["uid"]) is list and process["uid"] == [data["subject_uid"]] * 4
                      and all(type(v) is int for v in process["uid"])
                      and type(process["gid"]) is list and process["gid"] == [data["subject_gid"]] * 4
                      and all(type(v) is int for v in process["gid"])
                      and type(process["groups"]) is list and len(process["groups"]) <= MAX_GROUPS
                      and all(_integer(v, 0, 0xffffffff) for v in process["groups"]))
                collector = collector or process["role"] == "datacollector"
            counters = sample["counters"]
            _need(type(counters) is dict and set(counters) == {"pids_current", "pids_max", "pids_events",
                  "memory_current", "memory_max", "memory_events"})
            _need(_nullable(counters["pids_current"]) and _nullable(counters["memory_current"]))
            for key in ("pids_max", "memory_max"):
                value = counters[key]
                if value is not None:
                    _need(type(value) is dict and set(value) == {"value", "unbounded"}
                          and ((value["unbounded"] is True and value["value"] is None)
                               or (value["unbounded"] is False and _integer(value["value"]))))
            for key, keys in (("pids_events", ("max",)), ("memory_events", MEMORY_EVENTS)):
                value = counters[key]
                if value is not None:
                    _need(type(value) is dict and set(value) == set(keys)
                          and all(_nullable(v) for v in value.values()))
        _need(collector)
        _need(payload == (json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n").encode("ascii"))
        return True
    except Exception:
        return False
