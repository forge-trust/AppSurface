#!/usr/bin/env python3
"""Fixed N15 live observer. It has no signal retry, fallback signal, or deadline renewal."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import pwd
import grp
import re
import signal
import stat
import subprocess
import sys
import time

class Reject(ValueError):
    """Fixed private rejection; raw source and input data are never echoed."""


class InitialIdentityPending(Exception):
    """An activating/start unit has not yet supplied a matching live worker sample."""


class TerminalIdentityPending(Exception):
    """A post-signal process is Z/X or its proc name disappeared; never an exit receipt."""


def require(condition, reason):
    if not condition:
        raise Reject(reason)


DIAGNOSTIC_STAGE = "preflight"
DIAGNOSTIC_STAGES = ('preflight', 'phase-read', 'admission-window', 'process-identities', 'account-snapshot', 'worker-membership', 'pre-signal-recheck', 'pidfd-signal', 'owner-terminal', 'worker-terminal', 'consumed-guard', 'result')
DIAGNOSTIC_REASONS = ("unit-reference-binding", "unit-reference-open", "unit-reference-timeout", "unit-reference-call", "reference-marker-custody", "reference-marker-write", "reference-marker-release", "phase-generation-syntax", "phase-record-byte-bound", "phase-record-json", "phase-phase-shape", "phase-phase-binding", "phase-worker-unit-generation", "phase-job-path-shape", "phase-parser-rejected", 'cgroup-node', 'cgroup-path', 'cgroup-pid-syntax', 'cgroup-procs-bound', 'cgroup-root', 'cgroup-symlink', 'deadline-order', 'explicit-execution-flag-required', 'fixed-case-unit', 'generated-account-identity', 'generated-account-membership', 'generated-account-nss-missing', 'generated-worker-account-name', 'generation-syntax', 'guard-ancestor-custody', 'guard-armed-remains', 'guard-consumed-bytes', 'guard-consumed-custody', 'guard-directory-custody', 'identity-window-expired', 'original-deadline-expired', 'owner-kill-terminal', 'owner-process-binding', 'parser-custody', 'parser-loader', 'parser-pin', 'phase-file-custody', 'phase-open-substitution', 'phase-read-substitution', 'pidfd-owner-recheck', 'pre-signal-identity-changed', 'proc-cgroup-shape', 'proc-credential-fields', 'proc-credentials', 'proc-file-bound', 'proc-file-read', 'proc-process-state', 'proc-stat-shape', 'root-linux-required', 'systemd-output-encoding', 'systemd-output-fields', 'systemd-output-key', 'systemd-output-shape', 'systemd-query-failed', 'unit-account-lookup', 'unit-account-range', 'unit-account-shape', 'unit-control-group', 'unit-main-pid', 'unit-restart-policy', 'unit-syntax', 'worker-account-contract', 'worker-cgroup-membership', 'worker-cgroup-not-empty', 'worker-not-started', 'worker-process-binding', 'worker-replacement', 'worker-terminal-fields', 'worker-terminal-not-observed')


def failure_diagnostic(stage, error):
    """Bounded fixed vocabulary only; diagnostic output cannot change rejection."""
    stage = stage if type(stage) is str and stage in DIAGNOSTIC_STAGES else "unknown"
    reason = "unclassified"
    if isinstance(error, Reject) and len(error.args) == 1 and type(error.args[0]) is str and error.args[0] in DIAGNOSTIC_REASONS:
        reason = error.args[0]
    category = "system-error" if isinstance(error, OSError) else "subprocess-error" if isinstance(error, subprocess.SubprocessError) else "validation-error"
    return "N15_OBSERVER_DIAGNOSTIC:stage=" + stage + ":reason=" + reason + ":category=" + category


def identity(st):
    return (st.st_dev, st.st_ino, st.st_mode, st.st_uid, st.st_gid, st.st_nlink,
            st.st_size, st.st_mtime_ns, st.st_ctime_ns)


PARSER_SHA256 = "fa1fc74882dd5018d5e04128adf3ef30ae19570f4e1eee075eca2c9b57cfa877"


def load_pinned_parser():
    path = Path(__file__).parent / "check_n15_records.py"
    before = os.lstat(path)
    require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and before.st_gid == 0
            and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o444,
            "parser-custody")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        raw = os.read(fd, 131073)
        require(len(raw) <= 131072 and not os.read(fd, 1)
                and hashlib.sha256(raw).hexdigest() == PARSER_SHA256
                and identity(before) == identity(os.fstat(fd)) == identity(os.lstat(path)),
                "parser-pin")
    finally:
        os.close(fd)
    spec = importlib.util.spec_from_file_location("issue779_n15_records", path)
    require(spec is not None and spec.loader is not None, "parser-loader")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


_record_parser = None


def record_parser():
    global _record_parser
    if _record_parser is None:
        _record_parser = load_pinned_parser()
    return _record_parser


PHASE_REJECTIONS = ("generation-syntax", "record-byte-bound", "record-json", "phase-shape", "phase-binding", "worker-unit-generation", "job-path-shape")


def validate_phase(raw, generation):
    """Keep the strict pinned parser; translate only its closed diagnostic reason."""
    parser = record_parser()
    try:
        return parser.validate_phase(raw, generation)
    except parser.Reject as error:
        reason = error.args[0] if len(error.args) == 1 and type(error.args[0]) is str else None
        raise Reject("phase-" + reason if reason in PHASE_REJECTIONS else "phase-parser-rejected") from None

SYSTEMCTL = "/usr/bin/systemctl"
WORKER_PREFIX = "appsurface-evidence-worker-"
OWNER_PREFIX = "appsurface-evidence-owner-"


def boot_ms():
    return time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1_000_000


def remaining(deadline):
    left = deadline - boot_ms()
    require(left > 0, "original-deadline-expired")
    return left


def read_fd(path, cap):
    before = os.lstat(path)
    require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
            and stat.S_IMODE(before.st_mode) == 0o600 and before.st_uid == 0
            and before.st_gid == 0 and 0 <= before.st_size <= cap, "phase-file-custody")
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        opened = os.fstat(fd)
        require(identity(before) == identity(opened), "phase-open-substitution")
        data = os.read(fd, cap + 1)
        require(len(data) == before.st_size and len(data) <= cap and not os.read(fd, 1)
                and identity(before) == identity(os.fstat(fd)) == identity(os.lstat(path)),
                "phase-read-substitution")
        return data
    finally:
        os.close(fd)


def systemd(unit, properties, deadline):
    require(re.fullmatch(r"appsurface-evidence-(?:owner|worker)-[0-9a-f]{32}\.service", unit)
            is not None, "unit-syntax")
    left = remaining(deadline)
    try:
        result = subprocess.run([SYSTEMCTL, "show", "--no-pager", "--property=" + ",".join(properties), unit],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/bin:/usr/sbin", "LANG": "C", "LC_ALL": "C"},
            timeout=min(left / 1000, 1.0), check=False)
    except (OSError, subprocess.TimeoutExpired):
        raise Reject("systemd-query-failed") from None
    require(result.returncode == 0 and len(result.stdout) <= 4096, "systemd-query-failed")
    try:
        text = result.stdout.decode("ascii", "strict")
    except UnicodeError:
        raise Reject("systemd-output-encoding") from None
    values = {}
    for line in text.splitlines():
        require("=" in line, "systemd-output-shape")
        key, value = line.split("=", 1)
        require(key in properties and key not in values, "systemd-output-key")
        values[key] = value
    require(set(values) == set(properties), "systemd-output-fields")
    remaining(deadline)
    return values


def account_id(value, kind):
    require(type(value) is str and value and len(value) <= 128, "unit-account-shape")
    try:
        if value.isascii() and value.isdecimal():
            result = int(value)
        elif kind == "uid":
            result = pwd.getpwnam(value).pw_uid
        else:
            result = grp.getgrnam(value).gr_gid
    except (KeyError, ValueError, OverflowError):
        raise Reject("unit-account-lookup") from None
    require(0 < result < 2**32 - 1, "unit-account-range")
    return result


def account_snapshot(worker_values, worker):
    """Capture all five actual NSS identities while the selected worker is still pending."""
    # systemd stores the production unit's explicitly numeric User/Group values.
    # Names come only from the generated kernel-bound unit, never from caller data.
    match=re.fullmatch(r"0::/system.slice/appsurface-evidence-worker-([0-9a-f]{32})\.service",
        "0::"+worker["control_group"])
    require(match is not None,"generated-worker-account-name")
    suffix=match.group(1)[:28]
    names={"worker":"evw"+suffix,"subject":"evs"+suffix,"results":"evr"+suffix}
    require(account_id(worker_values["User"],"uid")==worker["uid"]
        and account_id(worker_values["Group"],"gid")==worker["gid"],
        "generated-account-identity")
    try:
        worker_passwd=pwd.getpwnam(names["worker"])
        subject_passwd=pwd.getpwnam(names["subject"])
        worker_grp=grp.getgrnam(names["worker"])
        subject_grp=grp.getgrnam(names["subject"])
        results_grp=grp.getgrnam(names["results"])
    except KeyError:
        raise Reject("generated-account-nss-missing") from None
    ids={"worker_uid":worker_passwd.pw_uid,"worker_gid":worker_grp.gr_gid,
         "subject_uid":subject_passwd.pw_uid,"subject_gid":subject_grp.gr_gid,
         "results_gid":results_grp.gr_gid}
    require(ids["worker_uid"]==worker["uid"] and ids["worker_gid"]==worker["gid"]
        and all(type(value) is int and 0<value<2**32-1 for value in ids.values())
        and len(set(ids.values()))==5,"generated-account-identity")
    require(worker_passwd.pw_gid==ids["worker_gid"]
        and subject_passwd.pw_gid==ids["subject_gid"]
        and not worker_grp.gr_mem and not subject_grp.gr_mem and not results_grp.gr_mem,
        "generated-account-membership")
    try:
        require(pwd.getpwuid(ids["worker_uid"])==worker_passwd
            and pwd.getpwuid(ids["subject_uid"])==subject_passwd
            and grp.getgrgid(ids["worker_gid"])==worker_grp
            and grp.getgrgid(ids["subject_gid"])==subject_grp
            and grp.getgrgid(ids["results_gid"])==results_grp,
            "generated-account-identity")
    except KeyError:
        raise Reject("generated-account-nss-missing") from None
    return {"names":names,"ids":ids}


def proc_file(pid, name, cap=8192):
    path = f"/proc/{pid}/{name}"
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        data = os.read(fd, cap + 1)
        require(len(data) <= cap and not os.read(fd, 1), "proc-file-bound")
        return data.decode("ascii", "strict")
    except (UnicodeError, OSError):
        raise Reject("proc-file-read") from None
    finally:
        os.close(fd)


def proc_identity(pid, *, include_terminal=False):
    """Read an actual live identity; optional post-signal Z/X raises pending before credentials.

    Default authentication rejects terminal states. Pending data never establishes exit.
    """
    statline = proc_file(pid, "stat")
    close = statline.rfind(")")
    require(close > 0, "proc-stat-shape")
    tail = statline[close + 2:].split()
    require(len(tail) >= 20, "proc-stat-shape")
    if tail[0] in ("Z", "X"):
        if include_terminal:
            raise TerminalIdentityPending()
        raise Reject("proc-process-state")
    starttime = int(tail[19])
    rows = {}
    for line in proc_file(pid, "status").splitlines():
        if ":" in line:
            key, value = line.split(":", 1)
            rows[key] = value.strip()
    try:
        uids = [int(x) for x in rows["Uid"].split()]
        gids = [int(x) for x in rows["Gid"].split()]
    except (KeyError, ValueError):
        raise Reject("proc-credentials") from None
    require(len(uids) == 4 and len(gids) == 4, "proc-credential-fields")
    cgroups = proc_file(pid, "cgroup").splitlines()
    unified = [row[3:] for row in cgroups if row.startswith("0::")]
    require(len(unified) == 1, "proc-cgroup-shape")
    return {"starttime_ticks": starttime, "uids": uids, "gids": gids,
            "cgroup": unified[0]}


def unit_identity(unit, expected_kind, deadline, *, include_terminal=False, terminal_pid=None, pending_start=False):
    """Bind live process facts to exact unit policy within the original bound.

    Post-signal mode requires an original positive terminal_pid, permits no replacement,
    and raises TerminalIdentityPending for Z/X or missing proc names. Only MainPID0
    returns None; the caller separately requires original terminal metadata and empty groups.
    """
    fields = ["MainPID", "ControlGroup", "User", "Group", "Restart", "ActiveState", "SubState",
              "ExecMainPID", "ExecMainCode", "ExecMainStatus", "Result"]
    values = systemd(unit, fields, deadline)
    try:
        pid = int(values["MainPID"])
    except ValueError:
        raise Reject("unit-main-pid") from None
    require(values["Restart"] == "no", "unit-restart-policy")
    if include_terminal:
        require(type(terminal_pid) is int and terminal_pid > 0
            and pid in (0, terminal_pid), "unit-main-pid")
    if pid == 0:
        return values, None
    expected_cgroup = "/system.slice/" + unit
    require(values["ControlGroup"] == expected_cgroup, "unit-control-group")
    try:
        proc = proc_identity(pid, include_terminal=include_terminal)
    except (FileNotFoundError, ProcessLookupError):
        if include_terminal:
            raise TerminalIdentityPending() from None
        raise
    if expected_kind == "owner":
        require(proc["uids"] == [0] * 4 and proc["gids"] == [0] * 4
                and proc["cgroup"] == expected_cgroup, "owner-process-binding")
    else:
        uid = account_id(values["User"], "uid")
        gid = account_id(values["Group"], "gid")
        matched = (proc["uids"] == [uid] * 4 and proc["gids"] == [gid] * 4
                and proc["cgroup"] == expected_cgroup)
        if not matched and pending_start and values["ActiveState"] == "activating" \
                and values["SubState"] in ("start", "start-pre", "start-post"):
            raise InitialIdentityPending()
        if not matched:
            # Fixed private diagnostic only. A mismatch still rejects; no raw path or text.
            allowed_states = ("activating", "active", "inactive", "failed")
            allowed_substates = ("start", "start-pre", "start-post", "running", "dead", "failed")
            projection = {"schema":"issue779-n15-worker-binding-diagnostic-v1",
                "expected_uid":uid, "expected_gid":gid,
                "uid4":proc["uids"], "gid4":proc["gids"],
                "cgroup_matches":proc["cgroup"] == expected_cgroup,
                "active_state":values["ActiveState"] if values["ActiveState"] in allowed_states else "other",
                "sub_state":values["SubState"] if values["SubState"] in allowed_substates else "other"}
            encoded = json.dumps(projection,sort_keys=True,separators=(",",":"))
            require(len(encoded)<=1024,"proc-process-state")
            print(encoded,file=sys.stderr,flush=True)
            raise Reject("worker-process-binding")
        proc["uid"] = uid
        proc["gid"] = gid
    proc["pid"] = pid
    proc["control_group"] = expected_cgroup
    return values, proc


def wait_phase(path, generation, deadline):
    while True:
        remaining(deadline)
        try:
            raw = read_fd(path, 8192)
        except FileNotFoundError:
            time.sleep(min(0.02, remaining(deadline) / 1000))
            continue
        # The C# hook writes JSON and newline separately on the original pipe.
        if not raw or b"\n" not in raw:
            time.sleep(min(0.02, remaining(deadline) / 1000))
            continue
        parsed = validate_phase(raw, generation)
        return raw, parsed


def cgroup_members(root, group, deadline):
    root = Path(root)
    require(root == Path("/sys/fs/cgroup") and not root.is_symlink(), "cgroup-root")
    target = root / group.lstrip("/")
    require(group.startswith("/system.slice/") and not target.is_symlink(), "cgroup-path")
    if not target.exists():
        return []
    members = []
    pending = [target]
    while pending:
        remaining(deadline)
        current = pending.pop()
        require(not current.is_symlink() and current.is_dir(), "cgroup-node")
        for entry in os.scandir(current):
            remaining(deadline)
            if entry.is_symlink():
                raise Reject("cgroup-symlink")
            if entry.is_dir(follow_symlinks=False):
                pending.append(Path(entry.path))
            elif entry.name == "cgroup.procs":
                fd = os.open(entry.path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
                try:
                    raw = os.read(fd, 65537)
                    require(len(raw) <= 65536 and not os.read(fd, 1), "cgroup-procs-bound")
                finally:
                    os.close(fd)
                for line in raw.splitlines():
                    require(line.isdigit(), "cgroup-pid-syntax")
                    members.append(int(line))
    return sorted(set(members))


def terminal_failure_diagnostic(values, original_pid, sample_count, proc_observed):
    """Project only the last already-read terminal poll; never queries or establishes exit."""
    def number(key, maximum):
        value = values.get(key)
        if type(value) is str and re.fullmatch(r"[0-9]{1,10}", value):
            parsed = int(value)
            if parsed <= maximum:
                return parsed
        return None
    active = values.get("ActiveState")
    sub = values.get("SubState")
    data = {"schema":"issue779-n15-terminal-poll-diagnostic-v1",
        "original_pid":original_pid if type(original_pid) is int and 0<original_pid<=2147483647 else None,
        "main_pid":number("MainPID",2147483647),"exec_main_pid":number("ExecMainPID",2147483647),
        "exec_main_code":number("ExecMainCode",255),"exec_main_status":number("ExecMainStatus",255),
        "active_state":active if active in ("active","inactive","activating","deactivating","failed","reloading","maintenance") else "other",
        "sub_state":sub if sub in ("running","exited","dead","failed","start","start-pre","start-post","stop","stop-sigterm","stop-sigkill","final-sigterm","final-sigkill") else "other",
        "restart_policy":"no" if values.get("Restart")=="no" else "other",
        "sample_count":sample_count if type(sample_count) is int and 0<=sample_count<=10000 else None,
        "main_proc_observed":proc_observed if type(proc_observed) is bool else None}
    encoded = json.dumps(data,sort_keys=True,separators=(",",":"))
    require(len(encoded)<=1024,"proc-process-state")
    return encoded


def wait_worker_terminal(unit, original_pid, group, deadline):
    last = None
    diagnostic_values = {}
    diagnostic_samples = 0
    diagnostic_proc = False
    try:
        while boot_ms() < deadline:
            remaining(deadline)
            try:
                values, proc = unit_identity(unit, "worker", deadline, include_terminal=True, terminal_pid=original_pid)
            except TerminalIdentityPending:
                time.sleep(min(0.025, remaining(deadline) / 1000))
                continue
            diagnostic_values = dict(values)
            diagnostic_samples += 1
            diagnostic_proc = proc is not None
            if proc is not None:
                require(proc["pid"] == original_pid, "worker-replacement")
            else:
                try:
                    exec_pid = int(values["ExecMainPID"])
                    exec_code = int(values["ExecMainCode"])
                    exec_status = int(values["ExecMainStatus"])
                except ValueError:
                    raise Reject("worker-terminal-fields") from None
                if (values["ActiveState"] in ("inactive", "failed")
                        and values["SubState"] in ("dead", "failed") and exec_pid == original_pid
                        and exec_code in (1, 2) and exec_status > 0):
                    first = cgroup_members("/sys/fs/cgroup", group, deadline)
                    time.sleep(min(0.05, remaining(deadline) / 1000))
                    second = cgroup_members("/sys/fs/cgroup", group, deadline)
                    require(not first and not second, "worker-cgroup-not-empty")
                    last = {"main_pid": 0, "active_state": values["ActiveState"],
                        "sub_state": values["SubState"], "exec_main_pid": exec_pid,
                        "exec_main_code": exec_code, "exec_main_status": exec_status,
                        "restart_policy": values["Restart"], "cgroup_populated": False,
                        "cgroup_tree_empty_twice": True, "no_successful_completion": True,
                        "same_unit_no_replacement": True}
                    break
            time.sleep(min(0.025, remaining(deadline) / 1000))
    finally:
        if last is None:
            try:
                print(terminal_failure_diagnostic(diagnostic_values, original_pid,
                    diagnostic_samples, diagnostic_proc),file=sys.stderr,flush=True)
            except Exception:
                pass  # Diagnostic failure never replaces the original polling failure.
    require(last is not None, "worker-terminal-not-observed")
    return last


def consumed_guard(generation):
    base = Path("/run/appsurface-evidence-owners")
    target = base / generation
    for path in (Path("/run"), Path("/run/appsurface-evidence-owners")):
        st = os.lstat(path)
        require(stat.S_ISDIR(st.st_mode) and st.st_uid == 0 and st.st_gid == 0
                and (stat.S_IMODE(st.st_mode) & 0o022) == 0, "guard-ancestor-custody")
    st = os.lstat(target)
    require(stat.S_ISDIR(st.st_mode) and st.st_uid == 0 and st.st_gid == 0
            and stat.S_IMODE(st.st_mode) == 0o700, "guard-directory-custody")
    armed = target / "armed"
    consumed = target / "consumed"
    require(not armed.exists() and not armed.is_symlink(), "guard-armed-remains")
    before = os.lstat(consumed)
    require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and before.st_gid == 0
            and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o600
            and before.st_size == 33, "guard-consumed-custody")
    fd = os.open(consumed, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        raw = os.read(fd, 34)
        require(raw == (generation + "\n").encode("ascii") and not os.read(fd, 1)
                and identity(before) == identity(os.fstat(fd)) == identity(os.lstat(consumed)),
                "guard-consumed-bytes")
    finally:
        os.close(fd)
    return {"armed_absent": True, "consumed_root_owned_exact_marker": True,
            "consumed_marker_same_generation": True}


class UnitReference:
    """Keep only selected worker-unit metadata loaded; never starts/stops a unit."""
    def __init__(self):
        self.lib = None
        self.bus = ctypes.c_void_p()
        self.unit = None

    def acquire(self, unit, deadline):
        require(self.lib is None and re.fullmatch(r"appsurface-evidence-worker-[0-9a-f]{32}\.service", unit) is not None, "unit-reference-binding")
        remaining(deadline)
        self.lib = ctypes.CDLL("/usr/lib/x86_64-linux-gnu/libsystemd.so.0")
        self.lib.sd_bus_open_system.argtypes = [ctypes.POINTER(ctypes.c_void_p)]
        self.lib.sd_bus_open_system.restype = ctypes.c_int
        self.lib.sd_bus_set_method_call_timeout.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
        self.lib.sd_bus_set_method_call_timeout.restype = ctypes.c_int
        self.lib.sd_bus_call_method.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p), ctypes.c_char_p]
        self.lib.sd_bus_call_method.restype = ctypes.c_int
        self.lib.sd_bus_message_unref.argtypes = [ctypes.c_void_p]
        self.lib.sd_bus_message_unref.restype = ctypes.c_void_p
        self.lib.sd_bus_close_unref.argtypes = [ctypes.c_void_p]
        self.lib.sd_bus_close_unref.restype = ctypes.c_void_p
        try:
            require(self.lib.sd_bus_open_system(ctypes.byref(self.bus)) >= 0 and self.bus.value is not None, "unit-reference-open")
            self._call(b"RefUnit", unit, deadline)
            self.unit = unit
        except BaseException:
            self.close_now()
            raise

    def _call(self, method, unit, deadline):
        require(method in (b"RefUnit", b"UnrefUnit") and self.bus.value is not None, "unit-reference-binding")
        left = remaining(deadline)
        require(self.lib.sd_bus_set_method_call_timeout(self.bus, min(left, 1000) * 1000) >= 0, "unit-reference-timeout")
        reply = ctypes.c_void_p()
        try:
            code = self.lib.sd_bus_call_method(self.bus, b"org.freedesktop.systemd1", b"/org/freedesktop/systemd1", b"org.freedesktop.systemd1.Manager", method, None, ctypes.byref(reply), b"s", ctypes.c_char_p(unit.encode("ascii")))
            require(code >= 0, "unit-reference-call")
            remaining(deadline)
        finally:
            if reply.value is not None:
                self.lib.sd_bus_message_unref(reply)

    def close(self, deadline):
        try:
            if self.unit is not None:
                self._call(b"UnrefUnit", self.unit, deadline)
        finally:
            self.close_now()

    def close_now(self):
        if self.lib is not None and self.bus.value is not None:
            self.lib.sd_bus_close_unref(self.bus)
        self.bus = ctypes.c_void_p()
        self.unit = None


def reference_markers(generation):
    require(re.fullmatch(r"[0-9a-f]{32}", generation) is not None, "generation-syntax")
    root = Path("/run/appsurface-evidence-fixture") / generation
    st = root.lstat()
    require(stat.S_ISDIR(st.st_mode) and st.st_uid == st.st_gid == 0 and stat.S_IMODE(st.st_mode) == 0o700, "reference-marker-custody")
    return root / "unit-reference.ready", root / "unit-reference.release"


def hold_reference_until_release(generation, deadline):
    """Announce complete partial JSON, then keep the bus connection through replay/finalization."""
    ready, release = reference_markers(generation)
    remaining(deadline)
    fd = os.open(ready, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        require(os.write(fd, b"ready\n") == 6, "reference-marker-write")
        os.fsync(fd)
    finally:
        os.close(fd)
    while True:
        remaining(deadline)
        try:
            raw = read_fd(release, 8)
        except FileNotFoundError:
            time.sleep(min(.02, remaining(deadline) / 1000))
            continue
        require(raw == b"release\n", "reference-marker-release")
        remaining(deadline)
        return


def observe_and_kill(args, reference):
    global DIAGNOSTIC_STAGE
    DIAGNOSTIC_STAGE = "preflight"
    require(os.geteuid() == 0 and sys.platform == "linux", "root-linux-required")
    require(re.fullmatch(r"[0-9a-f]{32}", args.case_generation) is not None, "generation-syntax")
    require(args.startup_deadline_boot_ms <= args.job_deadline_boot_ms
            and args.admission_deadline_boot_ms <= args.job_deadline_boot_ms
            and args.work_deadline_boot_ms < args.job_deadline_boot_ms, "deadline-order")
    phase_end = min(args.startup_deadline_boot_ms, args.admission_deadline_boot_ms,
                    args.job_deadline_boot_ms, args.work_deadline_boot_ms)
    DIAGNOSTIC_STAGE = 'phase-read'
    raw, phase = wait_phase(args.phase_stderr, args.case_generation, phase_end)
    reference.acquire(phase["unit"], phase_end)
    owner = OWNER_PREFIX + args.case_generation + ".service"
    require(phase["unit"] == WORKER_PREFIX + args.case_generation + ".service", "fixed-case-unit")
    DIAGNOSTIC_STAGE = 'admission-window'
    trigger_ms = boot_ms()
    admission_remaining_ms = record_parser().admission_window_remaining(
        args.admission_deadline_boot_ms, trigger_ms)
    identity_end = min(trigger_ms + 5000, args.startup_deadline_boot_ms,
                       args.admission_deadline_boot_ms, args.job_deadline_boot_ms,
                       args.work_deadline_boot_ms)
    DIAGNOSTIC_STAGE = 'process-identities'
    worker_values = worker = owner_values = owner_proc = None
    while boot_ms() < identity_end:
        remaining(identity_end)
        try:
            worker_values, worker = unit_identity(phase["unit"], "worker", identity_end, pending_start=True)
        except InitialIdentityPending:
            time.sleep(min(0.02, remaining(identity_end) / 1000))
            continue
        owner_values, owner_proc = unit_identity(owner, "owner", identity_end)
        if worker is not None and owner_proc is not None:
            require(worker_values["ActiveState"] in ("activating", "active")
                    and worker_values["SubState"] in ("start", "start-pre", "start-post", "running"),
                    "worker-not-started")
            break
        time.sleep(min(0.02, remaining(identity_end) / 1000))
    require(worker is not None and owner_proc is not None, "identity-window-expired")
    require(worker_values["User"] and worker_values["Group"], "worker-account-contract")
    DIAGNOSTIC_STAGE = 'account-snapshot'
    account_data=account_snapshot(worker_values,worker)
    DIAGNOSTIC_STAGE = 'worker-membership'
    members = cgroup_members("/sys/fs/cgroup", worker["control_group"], identity_end)
    require(worker["pid"] in members, "worker-cgroup-membership")
    # Recheck both units and the process starttime immediately before acquiring the pidfd.
    DIAGNOSTIC_STAGE = 'pre-signal-recheck'
    worker_values_2, worker_2 = unit_identity(phase["unit"], "worker", identity_end)
    owner_values_2, owner_2 = unit_identity(owner, "owner", identity_end)
    require(worker_2 == worker and owner_2 == owner_proc, "pre-signal-identity-changed")
    DIAGNOSTIC_STAGE = 'pidfd-signal'
    pidfd = os.pidfd_open(owner_proc["pid"], 0)
    try:
        owner_values_3, owner_3 = unit_identity(owner, "owner", identity_end)
        require(owner_3 == owner_proc, "pidfd-owner-recheck")
        signal.pidfd_send_signal(pidfd, signal.SIGKILL, None, 0)
    finally:
        os.close(pidfd)

    # The helper waits only under the original work deadline supplied by the caller.
    DIAGNOSTIC_STAGE = 'owner-terminal'
    owner_terminal = None
    while boot_ms() < args.work_deadline_boot_ms:
        remaining(args.work_deadline_boot_ms)
        try:
            values, proc = unit_identity(owner, "owner", args.work_deadline_boot_ms, include_terminal=True, terminal_pid=owner_proc["pid"])
        except TerminalIdentityPending:
            time.sleep(min(0.025, remaining(args.work_deadline_boot_ms) / 1000))
            continue
        if proc is not None:
            require(proc == owner_proc, "owner-process-binding")
        if proc is None:
            owner_terminal = values
            if values["ActiveState"] in ("inactive", "failed") and values["SubState"] in ("dead", "failed"):
                break
        time.sleep(min(0.025, remaining(args.work_deadline_boot_ms) / 1000))
    require(owner_terminal is not None and owner_terminal["ExecMainPID"] == str(owner_proc["pid"])
            and owner_terminal["ExecMainCode"] == "2" and owner_terminal["ExecMainStatus"] == "9",
            "owner-kill-terminal")
    DIAGNOSTIC_STAGE = 'worker-terminal'
    worker_terminal = wait_worker_terminal(phase["unit"], worker["pid"],
        worker["control_group"], args.work_deadline_boot_ms)
    DIAGNOSTIC_STAGE = 'consumed-guard'
    guard = consumed_guard(args.case_generation)
    DIAGNOSTIC_STAGE = 'result'
    return {
        "schema": record_parser().PARTIAL_SCHEMA,
        "case_generation": args.case_generation,
        "admission_remaining_ms_at_trigger": admission_remaining_ms,
        "phase_sha256": phase["phase_sha256"],
        "job_path": phase["job_path"],
        "worker": {"unit": phase["unit"], "main_pid": worker["pid"],
            "starttime_ticks": worker["starttime_ticks"], "uid": worker["uid"], "gid": worker["gid"],
            "control_group": worker["control_group"], "cgroup_membership_matched": True,
            "identity_rechecked_before_signal": True},
        "owner": {"unit": owner, "main_pid": owner_proc["pid"],
            "starttime_ticks": owner_proc["starttime_ticks"], "uid": 0, "gid": 0,
            "control_group": owner_proc["control_group"], "pidfd_opened": True,
            "identity_rechecked_before_signal": True},
        "pidfd_signal": {"signal": 9, "syscall_succeeded": True},
        "owner_terminal": {"main_pid": 0, "active_state": owner_terminal["ActiveState"],
            "sub_state": owner_terminal["SubState"], "exec_main_pid": int(owner_terminal["ExecMainPID"]),
            "exec_main_code": int(owner_terminal["ExecMainCode"]),
            "exec_main_status": int(owner_terminal["ExecMainStatus"]),
            "restart_policy": owner_terminal["Restart"], "same_unit_no_replacement": True},
        "worker_terminal": worker_terminal,
        "guard": guard,
        "account_snapshot": account_data,
        "observation_only": True, "native_authority": False,
        "native_acceptance": False, "native_credit": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--case-generation", required=True)
    parser.add_argument("--phase-stderr", required=True)
    parser.add_argument("--startup-deadline-boot-ms", required=True, type=int)
    parser.add_argument("--admission-deadline-boot-ms", required=True, type=int)
    parser.add_argument("--job-deadline-boot-ms", required=True, type=int)
    parser.add_argument("--work-deadline-boot-ms", required=True, type=int)
    args = parser.parse_args()
    require(args.execute, "explicit-execution-flag-required")
    reference = UnitReference()
    try:
        result = observe_and_kill(args, reference)
        print(json.dumps(result, sort_keys=True, separators=(",", ":")), flush=True)
        hold_reference_until_release(args.case_generation, args.job_deadline_boot_ms)
        reference.close(args.job_deadline_boot_ms)
    except (OSError, ValueError, KeyError, Reject, subprocess.SubprocessError, OverflowError) as error:
        print(failure_diagnostic(DIAGNOSTIC_STAGE, error), file=sys.stderr)
        print("N15_OBSERVER_REJECTED", file=sys.stderr)
        return 2
    finally:
        reference.close_now()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
