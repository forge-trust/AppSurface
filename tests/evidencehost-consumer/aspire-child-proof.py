#!/usr/bin/env python3
"""Provisional native mechanism proof; never returns Evidence admission or trust."""
import argparse
import hashlib
import json
import multiprocessing
import os
from pathlib import Path
import pwd
import re
import shutil
import socket
import stat
import struct
import subprocess
import sys
import threading
import time
import uuid

CASES = ("normal", "readiness-failure", "factory-stall", "cancel", "stuck-descendant")
RESOURCE = "native-http"
OUTPUT_LIMIT = 1024 * 1024
JOB_SECONDS = 45
READINESS_SECONDS = 20
CLEANUP_SECONDS = 10
COOPERATIVE_SECONDS = 5
WATCHDOG_ACK_SECONDS = 3
IDENTITY_FAILURE = "application-identity-rejected"
IDENTITY_STAGE = "startup-identity-validation"
IDENTITY_DIAGNOSTIC_LIMIT = 4096
BUDGET_DIAGNOSTIC_LIMIT = 4096
BUDGET_DIAGNOSTIC_CATEGORY = "cgroup-task-memory-counters"
STARTUP_FAILURE = "application-exec-startup-failed"
STARTUP_STAGE = "startup-unit-query"
STARTUP_DIAGNOSTIC_LIMIT = 4096
LOAD_STATES = frozenset(("stub", "loaded", "not-found", "bad-setting", "error", "merged", "masked", "unknown"))
SERVICE_TYPES = frozenset(("simple", "exec", "forking", "oneshot", "dbus", "notify", "notify-reload", "idle", "unset", "unknown"))
SUB_STATES = frozenset(("dead", "condition", "start-pre", "start", "start-post", "running", "exited", "reload",
                        "reload-signal", "reload-notify", "stop", "stop-watchdog", "stop-sigterm", "stop-sigkill",
                        "stop-post", "final-watchdog", "final-sigterm", "final-sigkill", "failed", "auto-restart",
                        "auto-restart-queued", "cleaning", "unknown"))
UNIT_PROPERTY_NAMES = ("LoadState", "ControlGroup", "MainPID", "Type", "ActiveState", "SubState")
STARTUP_DIAGNOSTIC_PROPERTY_NAMES = ("Result", "ExecMainPID", "ExecMainCode", "ExecMainStatus", "Job")
SERVICE_RESULTS = frozenset(("success", "resources", "protocol", "timeout", "exit-code", "signal", "core-dump",
                             "watchdog", "start-limit-hit", "oom-kill", "exec-condition", "unknown"))
ACTIVE_STATES = frozenset(("active", "reloading", "inactive", "failed", "activating", "deactivating",
                           "maintenance", "refreshing", "unknown"))


class WatchdogFailure(RuntimeError):
    """Fixed-category failure of the independent root owner."""


class StartupFailure(RuntimeError):
    """The selected exec unit did not establish completed process setup."""


def command(argv, timeout=5, check=True):
    return subprocess.run(argv, capture_output=True, timeout=timeout, check=check)


def selected_group(unit):
    """A generated service has exactly one eligible root-selected cgroup."""
    if not re.fullmatch(r"issue779-child-[0-9a-f]{32}\.service", unit):
        raise ValueError("invalid-selected-unit")
    return "/system.slice/" + unit


def unit_properties(unit, timeout=5):
    """Query only the generated unit; retain status without exposing raw stderr."""
    selected_group(unit)
    result = command(["systemctl", "show", "--all", unit,
                      "--property=" + ",".join(UNIT_PROPERTY_NAMES + STARTUP_DIAGNOSTIC_PROPERTY_NAMES)],
                     timeout=timeout, check=False)
    properties = {"_query_exit_status": result.returncode}
    if len(result.stdout) > STARTUP_DIAGNOSTIC_LIMIT:
        properties["_malformed"] = True
        return properties
    for line in result.stdout.splitlines():
        key, separator, value = line.partition(b"=")
        key = key.decode("ascii")
        if key in STARTUP_DIAGNOSTIC_PROPERTY_NAMES:
            # Optional diagnostics cannot change the six required startup checks.
            # Duplicate/non-ASCII values are unknown, never authoritative defaults.
            try:
                value = value.decode("ascii")
            except UnicodeDecodeError:
                value = None
            properties[key] = None if key in properties or not separator else value
        elif not separator or key in properties or key not in UNIT_PROPERTY_NAMES:
            properties["_malformed"] = True
        else:
            properties[key] = value.decode("ascii")
    return properties


def unit_main_pid(properties):
    value = properties.get("MainPID", "")
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]{1,10}", value) or int(value) > 0xffffffff:
        raise StartupFailure(STARTUP_FAILURE)
    return int(value)


def diagnostic_integer(value, minimum, maximum):
    """Parse bounded observed decimal metadata; absence or malformed data is null."""
    if not isinstance(value, str) or not re.fullmatch(r"-?[0-9]{1,10}", value):
        return None
    parsed = int(value)
    return parsed if minimum <= parsed <= maximum else None


def startup_execution_diagnostics(properties):
    """Closed process/job facts only; no startup, identity or cleanup authority."""
    result = properties.get("Result")
    job = properties.get("Job")
    return {"result": result if isinstance(result, str) and result in SERVICE_RESULTS else "unknown",
            "exec_main_pid": diagnostic_integer(properties.get("ExecMainPID"), 0, 0xffffffff),
            "exec_main_code": diagnostic_integer(properties.get("ExecMainCode"), -0x80000000, 0x7fffffff),
            "exec_main_status": diagnostic_integer(properties.get("ExecMainStatus"), -0x80000000, 0x7fffffff),
            # Pinned v255 systemctl prints the Job tuple's ID, or blank for no job.
            "job_id": 0 if job == "" else diagnostic_integer(job, 1, 0xffffffff)}


def exec_startup_complete(properties, deadline, loaded_exec_seen=False, startup_progress_seen=False):
    """Only Type=exec active/running permits subsequent exact identity checks."""
    if (time.monotonic() >= deadline or type(properties.get("_query_exit_status")) is not int or
            properties.get("_query_exit_status") != 0 or properties.get("_malformed") or
            not set(UNIT_PROPERTY_NAMES).issubset(properties) or
            properties.get("LoadState") not in LOAD_STATES - {"unknown"} or
            (properties.get("Type") != "" and properties.get("Type") not in SERVICE_TYPES - {"unknown", "unset"}) or
            properties.get("ActiveState") not in ACTIVE_STATES - {"unknown"} or
            properties.get("SubState") not in SUB_STATES - {"unknown"}):
        raise StartupFailure(STARTUP_FAILURE)
    pid = unit_main_pid(properties)
    state = properties.get("ActiveState"), properties.get("SubState")
    if properties["LoadState"] == "not-found":
        if (not loaded_exec_seen and properties["Type"] == "" and state == ("inactive", "dead") and
                pid == 0 and properties.get("ControlGroup") == ""):
            return False
        raise StartupFailure(STARTUP_FAILURE)
    if properties["LoadState"] != "loaded" or properties["Type"] != "exec":
        raise StartupFailure(STARTUP_FAILURE)
    # A freshly installed start job may not have reached unit_start() yet.
    # This tuple grants no execution authority; expiry/launcher exit still fails.
    # Once activation is observed, returning here is terminal, not a new grace.
    if state == ("inactive", "dead") and pid == 0 and not startup_progress_seen:
        return False
    if state == ("active", "running"):
        if pid == 0 or not properties.get("ControlGroup"):
            raise StartupFailure(STARTUP_FAILURE)
        return True
    if state[0] == "activating" and state[1] in ("condition", "start-pre", "start", "start-post"):
        return False
    raise StartupFailure(STARTUP_FAILURE)


def record_startup_failure(receipt, control, properties, started):
    """Latch the first startup cause and retain only closed bounded query facts."""
    if receipt.get("failure_stage") in (IDENTITY_STAGE, STARTUP_STAGE):
        return
    receipt.update(failure=STARTUP_FAILURE, failure_stage=STARTUP_STAGE, startup_diagnostic_written=False)
    try:
        status = properties.get("_query_exit_status")
        status = status if type(status) is int and -255 <= status <= 255 else None
        try:
            pid = unit_main_pid(properties)
        except StartupFailure:
            pid = None
        data = {"load_state": properties.get("LoadState") if properties.get("LoadState") in LOAD_STATES else "unknown",
                "type": "unset" if properties.get("Type") == "" else properties.get("Type") if properties.get("Type") in SERVICE_TYPES else "unknown",
                "active_state": properties.get("ActiveState") if properties.get("ActiveState") in ACTIVE_STATES else "unknown",
                "sub_state": properties.get("SubState") if properties.get("SubState") in SUB_STATES else "unknown",
                "main_pid": pid, "query_exit_status": status,
                "elapsed_seconds": round(max(0, time.monotonic() - started), 6)}
        data.update(startup_execution_diagnostics(properties))
        encoded = json.dumps(data, allow_nan=False, separators=(",", ":")).encode() + b"\n"
        if len(encoded) > STARTUP_DIAGNOSTIC_LIMIT:
            return
        with (control / "startup-diagnostic.json").open("xb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(encoded)
        receipt["startup_diagnostic_written"] = True
    except Exception:
        pass


def query_exec_startup(unit, deadline, loaded_exec_seen, receipt, control, started, startup_progress_seen=False):
    """Only recognized initial/activating tuples are pending within one deadline."""
    properties = {}
    try:
        properties = unit_properties(unit, timeout=max(0.001, min(5, deadline - time.monotonic())))
        complete = exec_startup_complete(properties, deadline, loaded_exec_seen, startup_progress_seen)
        group = properties.get("ControlGroup", "")
        if group and group != selected_group(unit):
            raise StartupFailure(STARTUP_FAILURE)
        return properties, complete
    except (OSError, subprocess.SubprocessError, ValueError, StartupFailure):
        record_startup_failure(receipt, control, properties, started)
        raise StartupFailure(STARTUP_FAILURE) from None


def refresh_unconfirmed_startup(unit, deadline, properties):
    """One diagnostic-only query before capture; a failed read retains prior facts."""
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        return properties
    try:
        latest = unit_properties(unit, timeout=min(5, remaining))
        if (type(latest.get("_query_exit_status")) is int and latest.get("_query_exit_status") == 0 and
                not latest.get("_malformed") and set(UNIT_PROPERTY_NAMES).issubset(latest)):
            return latest
    except (OSError, subprocess.SubprocessError, ValueError):
        pass
    return properties


def observe_unit_processes(properties, uid, group, receipt, control, started, deadline):
    """Exec completion precedes the unchanged UID/cgroup guard for every PID."""
    if not exec_startup_complete(properties, deadline):
        return None
    observed = {}
    for pid in cgroup_pids(group):
        try:
            facts = {}
            if not identity_matches(pid, uid, group, facts):
                record_identity_rejection(receipt, control, pid, uid, facts, group, properties, started)
                raise RuntimeError("Unexpected application process identity.")
            text = Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" ").decode(errors="replace")[:1024]
            observed[str(pid)] = ("descendant" if "--descendant" in text else "resource" if "NativeHttpResource.dll" in text
                                  else "apphost" if "AspireChild.dll" in text else "dcp" if "dcp" in text else "other")
        except FileNotFoundError:
            pass
    return observed


def read_cgroup_counter(directory_fd, name):
    """Read one fixed cgroup file without following links, within the byte bound."""
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=directory_fd)
    with os.fdopen(fd, "rb") as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ValueError("invalid-cgroup-counter")
        raw = stream.read(BUDGET_DIAGNOSTIC_LIMIT + 1)
    if len(raw) > BUDGET_DIAGNOSTIC_LIMIT:
        raise ValueError("invalid-cgroup-counter")
    return raw.decode("ascii").strip()


def counter_number(value, unlimited=False):
    """Kernel unsigned counters are numeric; an unlimited maximum becomes null."""
    if unlimited and value == "max":
        return None
    if not re.fullmatch(r"[0-9]{1,20}", value) or int(value) > 0xffffffffffffffff:
        raise ValueError("invalid-cgroup-counter")
    return int(value)


def counter_events(raw, required, optional=()):
    values = {}
    for line in raw.splitlines():
        key, value = line.split()
        if key not in (*required, *optional) or key in values:
            raise ValueError("invalid-cgroup-counter")
        values[key] = counter_number(value)
    if not set(required).issubset(values):
        raise ValueError("invalid-cgroup-counter")
    return {key: values.get(key) for key in (*required, *optional)}


def record_budget_diagnostic(receipt, control, group, started):
    """Best-effort private counters before stop; never reclassify the run failure."""
    receipt.update(budget_diagnostic_category=BUDGET_DIAGNOSTIC_CATEGORY, budget_diagnostic_written=False)
    directory_fd = None
    try:
        if not re.fullmatch(r"/system.slice/issue779-child-[0-9a-f]{32}\.service", group):
            return
        directory_fd = os.open(Path("/sys/fs/cgroup") / group.lstrip("/"),
                               os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        read = lambda name: read_cgroup_counter(directory_fd, name)
        data = {"pids_current": counter_number(read("pids.current")),
                "pids_max": counter_number(read("pids.max"), unlimited=True),
                "pids_events_max": counter_events(read("pids.events"), ("max",))["max"],
                "memory_current": counter_number(read("memory.current")),
                "memory_max": counter_number(read("memory.max"), unlimited=True),
                "memory_events": counter_events(read("memory.events"), ("low", "high", "max", "oom", "oom_kill"),
                                                ("oom_group_kill", "sock_throttled")),
                "elapsed_seconds": round(max(0, time.monotonic() - started), 6)}
        encoded = json.dumps(data, allow_nan=False, separators=(",", ":")).encode() + b"\n"
        if len(encoded) > BUDGET_DIAGNOSTIC_LIMIT:
            return
        with (control / "budget-diagnostic.json").open("xb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(encoded)
        receipt["budget_diagnostic_written"] = True
    except Exception:
        # Diagnostics cannot erase the first cause or change physical cleanup.
        pass
    finally:
        if directory_fd is not None:
            try:
                os.close(directory_fd)
            except OSError:
                pass


def cgroup_pids(group):
    if not re.fullmatch(r"/system.slice/issue779-child-[0-9a-f]{32}\.service", group):
        raise ValueError("Unexpected root-selected unit cgroup.")
    directory = Path("/sys/fs/cgroup") / group.lstrip("/")
    if not directory.exists():
        return set()
    result = set()
    for path in directory.rglob("cgroup.procs"):
        result.update(int(pid) for pid in path.read_text().split())
    return result


def belongs_to_group(pid, group):
    return any(line.split(":", 2)[-1] == group or line.split(":", 2)[-1].startswith(group + "/")
               for line in Path(f"/proc/{pid}/cgroup").read_text().splitlines())


def identity_matches(pid, uid, group, facts=None):
    status = Path(f"/proc/{pid}/status").read_text().splitlines()
    identities = [int(value) for value in next(line.split()[1:] for line in status if line.startswith("Uid:"))]
    if facts is not None:
        facts["uid"] = identities
    if not all(value == uid for value in identities):
        return False
    matches = belongs_to_group(pid, group)
    if facts is not None:
        facts["cgroup_matches"] = matches
    return matches


def preserve_identity_rejection(receipt):
    """The first startup or identity rejection survives later cleanup failures."""
    if receipt.get("failure_stage") == IDENTITY_STAGE:
        receipt["failure"] = IDENTITY_FAILURE
    elif receipt.get("failure_stage") == STARTUP_STAGE:
        receipt["failure"] = STARTUP_FAILURE


def record_control_failure(receipt, error, control, properties, started):
    """The orchestration catch also captures expiry between query and observation."""
    if isinstance(error, StartupFailure):
        record_startup_failure(receipt, control, properties, started)
    receipt["failure"] = ("watchdog-unavailable" if isinstance(error, WatchdogFailure) else
                          STARTUP_FAILURE if isinstance(error, StartupFailure) else
                          "control-or-readiness-failure")
    preserve_identity_rejection(receipt)
    receipt["error_class"] = type(error).__name__


def record_identity_rejection(receipt, control, pid, target_uid, facts, group, properties, started):
    """Latch a fixed safe category; capture bounded numeric facts without changing rejection."""
    receipt.update(failure=IDENTITY_FAILURE, failure_stage=IDENTITY_STAGE, identity_diagnostic_written=False)
    try:
        identities = facts["uid"]
        main_pid = int(properties.get("MainPID", "0"))
        numbers = [pid, target_uid, main_pid, *identities]
        if len(identities) != 4 or any(type(value) is not int or not 0 <= value <= 0xffffffff for value in numbers):
            return
        matches = facts["cgroup_matches"] if "cgroup_matches" in facts else belongs_to_group(pid, group)
        if type(matches) is not bool:
            return
        active = properties.get("ActiveState", "unknown")
        data = {"candidate_pid": pid, "target_uid": target_uid, "uid": identities,
                "cgroup_matches": matches, "main_pid": main_pid,
                "active_state": active if active in ACTIVE_STATES else "unknown",
                "elapsed_seconds": round(max(0, time.monotonic() - started), 6)}
        encoded = json.dumps(data, allow_nan=False, separators=(",", ":")).encode() + b"\n"
        if len(encoded) > IDENTITY_DIAGNOSTIC_LIMIT:
            return
        with (control / "identity-rejection.json").open("xb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            stream.write(encoded)
        receipt["identity_diagnostic_written"] = True
    except Exception:
        # The original rejection is authoritative even when its private capture fails.
        pass


def ready_request(path, uid, group, deadline):
    """Independent root request: kernel peer identity plus bounded HTTP response."""
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as stream:
        def bound_operation():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("readiness-deadline-expired")
            stream.settimeout(min(1, remaining))

        bound_operation()
        stream.connect(str(path))
        pid, peer_uid, _ = struct.unpack("3i", stream.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if peer_uid != uid or not belongs_to_group(pid, group):
            raise RuntimeError("Readiness socket peer escaped the selected UID/cgroup.")
        bound_operation()
        stream.sendall(b"GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
        data = bytearray()
        while True:
            bound_operation()
            part = stream.recv(1024)
            if not part:
                break
            data.extend(part)
            if len(data) > 8192:
                raise RuntimeError("Readiness response exceeded its independent byte bound.")
        header, body = bytes(data).split(b"\r\n\r\n", 1)
        status = header.split(b"\r\n", 1)[0]
        bound_operation()
        return status == b"HTTP/1.1 200 OK" and body == b"native-http-ready", pid, status == b"HTTP/1.1 503 Service Unavailable"


def stop_unit(unit, force=False):
    if force:
        try:
            command(["systemctl", "stop", "--no-block", unit], check=False)
        finally:
            command(["systemctl", "kill", "--kill-whom=all", "--signal=KILL", unit], check=False)
    else:
        # Do not begin PID 1's service-stop timeout or TERM DCP/resources here.
        command(["systemctl", "kill", "--kill-whom=main", "--signal=TERM", unit], check=False)


def stop_and_join(unit, process, group, pumps, guard=None):
    """Discover exact ownership during a finite stop; unknown emptiness is no proof."""
    expected = selected_group(unit)
    rejected_group = bool(group and group != expected)
    group = group if group == expected else ""

    def discover(deadline):
        nonlocal group, rejected_group
        if group or time.monotonic() >= deadline:
            return
        try:
            properties = unit_properties(unit, timeout=max(0.001, min(1, deadline - time.monotonic())))
            candidate = properties.get("ControlGroup", "")
            if candidate and candidate != expected:
                rejected_group = True
                return
            if (type(properties.get("_query_exit_status")) is int and properties.get("_query_exit_status") == 0 and
                    not properties.get("_malformed") and set(UNIT_PROPERTY_NAMES).issubset(properties) and
                    properties.get("LoadState") == "loaded" and properties.get("Type") == "exec" and
                    properties.get("ActiveState") in ACTIVE_STATES - {"unknown"} and
                    properties.get("SubState") in SUB_STATES - {"unknown"}):
                unit_main_pid(properties)
                if candidate == expected:
                    group = candidate
        except (OSError, subprocess.SubprocessError, ValueError, StartupFailure):
            pass

    stop_unit(unit)
    cooperative_deadline = time.monotonic() + COOPERATIVE_SECONDS
    cleanup_deadline = cooperative_deadline + CLEANUP_SECONDS - COOPERATIVE_SECONDS
    while time.monotonic() < cooperative_deadline:
        if guard is not None:
            guard()
        discover(cooperative_deadline)
        if process.poll() is not None and group and not cgroup_pids(group):
            break
        time.sleep(0.05)
    escalated = rejected_group or process.poll() is None or not group or bool(cgroup_pids(group))
    if escalated:
        stop_unit(unit, force=True)
    process.wait(timeout=max(0.001, cleanup_deadline - time.monotonic()))
    while time.monotonic() < cleanup_deadline:
        if guard is not None:
            guard()
        discover(cleanup_deadline)
        if group and not cgroup_pids(group):
            break
        time.sleep(0.05)
    for thread in pumps:
        thread.join(timeout=max(0, min(2, cleanup_deadline - time.monotonic())))
        if guard is not None:
            guard()
    return (not rejected_group and bool(group) and not cgroup_pids(group) and
            all(not thread.is_alive() for thread in pumps)), escalated


def final_output_receipt(receipt, budget, states=None):
    """Call after all output pumps join so teardown cannot upgrade a quota failure."""
    with budget.lock:
        receipt["output_bytes"] = budget.count
        receipt["output_quota_exceeded"] = budget.exceeded.is_set()
    if states is not None:
        snapshots = [state.snapshot() for state in states]
        receipt["output_pumps"] = snapshots
        receipt["output_complete"] = (len(snapshots) == 2 and
                                      all(state["finished"] and state["eof"] and not state["failed"] for state in snapshots) and
                                      sum(state["bytes"] for state in snapshots) == receipt["output_bytes"])
        if not receipt["output_complete"]:
            receipt["failure"] = "output-pump-incomplete"
    if budget.exceeded.is_set():
        receipt["failure"] = "output-quota-exceeded"


def record_stop_result(receipt, case, exit_code):
    """An empty cgroup alone cannot establish a successful cooperative stop."""
    receipt["process_exit_code"] = exit_code
    if case in ("normal", "cancel"):
        if receipt.get("stop_escalated"):
            receipt["failure"] = "unexpected-stop-escalation"
        elif exit_code != 0:
            receipt["failure"] = "cooperative-stop-nonzero-exit"
    if case == "factory-stall" and not receipt.get("stop_escalated"):
        receipt["failure"] = "missing-factory-stall-escalation"


def require_watchdog_alive(monitor):
    if not monitor.is_alive() or monitor.exitcode is not None:
        raise WatchdogFailure("watchdog-unavailable")


def await_watchdog_ack(monitor, reader, timeout=WATCHDOG_ACK_SECONDS):
    """The private inherited pipe must identify the selected live watchdog PID."""
    try:
        if not reader.poll(timeout) or reader.recv_bytes(64) != struct.pack("!I", monitor.pid):
            raise WatchdogFailure("watchdog-ack-invalid")
        require_watchdog_alive(monitor)
    except (OSError, EOFError) as error:
        raise WatchdogFailure("watchdog-ack-unavailable") from error


def disarm_watchdog(monitor, done, reader, physical_exit=True, ownership_lost=False):
    """Require live ownership until disarm, the disarm ACK and a clean joined exit."""
    require_watchdog_alive(monitor)
    if ownership_lost or not physical_exit:
        raise WatchdogFailure("watchdog-disarm-unconfirmed")
    done.set()
    try:
        if not reader.poll(2) or reader.recv_bytes(64) != b"disarmed":
            raise WatchdogFailure("watchdog-disarm-invalid")
    except (OSError, EOFError) as error:
        raise WatchdogFailure("watchdog-disarm-unavailable") from error
    monitor.join(timeout=2)
    if monitor.is_alive() or monitor.exitcode != 0:
        raise WatchdogFailure("watchdog-exit-invalid")


def watchdog(unit, deadline, done, control, ack):
    """Separate root process: a stalled controller cannot extend the child lease."""
    # A caller's process-group cancellation must not cancel the external owner.
    try:
        os.setsid()
        ack.send_bytes(struct.pack("!I", os.getpid()))
        while not done.wait(0.1):
            if time.monotonic() >= deadline:
                (control / "quarantine").write_text("external-watchdog-deadline\n")
                stop_unit(unit, force=True)
                return
        if time.monotonic() >= deadline:
            (control / "quarantine").write_text("external-watchdog-deadline\n")
            stop_unit(unit, force=True)
            return
        ack.send_bytes(b"disarmed")
    except Exception:
        (control / "quarantine").write_text("watchdog-failed\n")
        raise SystemExit(1) from None
    finally:
        ack.close()


class OutputBudget:
    def __init__(self, maximum=OUTPUT_LIMIT):
        self.maximum = maximum
        self.count = 0
        self.exceeded = threading.Event()
        self.lock = threading.Lock()

    def consume(self, size):
        with self.lock:
            self.count += size
            if self.count > self.maximum:
                self.exceeded.set()
            return not self.exceeded.is_set()


class PumpState:
    """Joined thread termination is accepted only with actual EOF and no failure."""
    def __init__(self, name):
        self.name = name
        self.bytes = 0
        self.eof = False
        self.failed = False
        self.finished = False
        self.error_class = None
        self.lock = threading.Lock()

    def snapshot(self):
        with self.lock:
            return {"name": self.name, "bytes": self.bytes, "eof": self.eof, "failed": self.failed,
                    "finished": self.finished, "error_class": self.error_class}

    def fail(self, error):
        with self.lock:
            self.failed = True
            self.error_class = type(error).__name__


def pump(stream, destination, budget, state):
    try:
        with destination.open("wb") as log:
            while True:
                part = stream.read(4096)
                if not part:
                    with state.lock:
                        state.eof = True
                    break
                with state.lock:
                    state.bytes += len(part)
                if budget.consume(len(part)) and log.write(part) != len(part):
                    raise OSError("short-log-write")
    except Exception as error:
        state.fail(error)
    finally:
        try:
            stream.close()
        except Exception as error:
            state.fail(error)
        with state.lock:
            state.finished = True


def prepare_payload(source, destination):
    """Stage a root-owned read-only bundle; no worker verifier/registration assemblies."""
    required = ("AspireChild.dll", "AspireChild.deps.json", "AspireChild.runtimeconfig.json",
                "dcp/dcp", "resource/NativeHttpResource.dll", "resource/NativeHttpResource.runtimeconfig.json")
    for name in required:
        if not (source / name).is_file():
            raise ValueError(f"Missing real SDK payload: {name}")
    if any("Evidence" in path.name or "ForgeTrust" in path.name for path in source.rglob("*.dll")):
        raise ValueError("Evidence or ForgeTrust assemblies are forbidden in the child bundle.")
    if any(path.is_symlink() for path in source.rglob("*")):
        raise ValueError("Bundle symlinks are forbidden.")
    shutil.copytree(source, destination)
    hashes = {}
    for path in destination.rglob("*"):
        os.chown(path, 0, 0)
        if path.is_dir():
            path.chmod(0o555)
        elif path.is_file():
            hashes[str(path.relative_to(destination))] = hashlib.sha256(path.read_bytes()).hexdigest()
            path.chmod(0o555 if path.stat().st_mode & 0o111 else 0o444)
    destination.chmod(0o555)
    return hashes


def service_command(unit, payload, scratch, dotnet, uid, gid, tools, output, control, case):
    allowed_input = payload / "proof-input" / "declared.txt"
    properties = ["Type=exec", f"User={uid}", f"Group={gid}", "KillMode=control-group", "TimeoutStopSec=10s",
                  f"RuntimeMaxSec={JOB_SECONDS}s", "SendSIGKILL=yes", "NoNewPrivileges=yes",
                  "CapabilityBoundingSet=", "AmbientCapabilities=", "ProtectControlGroups=yes",
                  "ProtectSystem=strict", "ProtectHome=yes", "PrivateNetwork=yes", "RestrictSUIDSGID=yes",
                  "TasksMax=128", "MemoryMax=1G", "UMask=0077", f"WorkingDirectory={scratch}",
                  f"ReadWritePaths={scratch}", f"ReadOnlyPaths={payload} {dotnet.parent} {allowed_input}",
                  f"InaccessiblePaths={tools} {output} {control}"]
    argv = ["systemd-run", "--quiet", "--wait", "--pipe", f"--unit={unit}"]
    argv += [f"--property={value}" for value in properties]
    # env -i prevents inherited root credentials and tool/cache paths crossing the boundary.
    argv += ["/usr/bin/env", "-i", f"HOME={scratch}", f"TMPDIR={scratch}",
             f"ASPIRE__STORE__PATH={scratch / '.aspire-store'}",
             "DOTNET_NOLOGO=1", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
             f"PROOF_PROTECTED_TOOLS={tools}", f"PROOF_PROTECTED_OUTPUT={output}",
             f"PROOF_ALLOWED_INPUT={allowed_input}",
             str(dotnet), str(payload / "AspireChild.dll"), "--scratch", str(scratch), "--case", case]
    return argv


def prove(args):
    if sys.platform != "linux" or os.geteuid() != 0 or not Path("/sys/fs/cgroup/cgroup.controllers").exists():
        raise RuntimeError("Native proof requires root Linux with unified cgroup v2 and systemd; no fallback.")
    if args.subject_uid == 0 or args.subject_gid == 0:
        raise ValueError("The application UID/GID must be separate from the root controller.")
    pwd.getpwuid(args.subject_uid)
    for path in (args.bundle, args.dotnet, args.protected_tools, args.protected_output):
        if not path.is_absolute() or not path.exists() or any(char.isspace() for char in str(path)):
            raise ValueError("Root-selected existing absolute paths without whitespace are required.")
    if not args.protected_tools.is_dir() or not args.protected_output.is_dir():
        raise ValueError("Protected probe roots must be directories.")
    protected_tool = args.protected_tools / "protected-tool.dll"
    if protected_tool.stat().st_size > 64 * 1024 * 1024:
        raise ValueError("The fixed managed tool exceeds the fixture input bound.")
    tool_bytes = protected_tool.read_bytes()
    if not tool_bytes.startswith(b"MZ") or b"BSJB" not in tool_bytes:
        raise ValueError("The fixed protected-tool.dll must be a real managed PE binary.")
    output_probe = args.protected_output / "native-resource-output-probe"
    if output_probe.exists():
        raise ValueError("The fixed output probe must be absent in a fresh output fixture.")
    for protected in (args.protected_tools.resolve(), args.protected_output.resolve()):
        if args.dotnet.resolve().is_relative_to(protected):
            raise ValueError("The platform runtime cannot be inside a denied protected-tool root.")
    base = Path("/run") / ("issue779-child-" + uuid.uuid4().hex)
    base.mkdir(mode=0o711)
    base.chmod(0o711)
    control = base / "control"
    control.mkdir(mode=0o700)
    scratch = base / "scratch"
    scratch.mkdir(mode=0o700)
    os.chown(scratch, args.subject_uid, args.subject_gid)
    unit = base.name + ".service"
    receipt = {"provisional": True, "trust_claim": False, "case": args.case, "unit": unit,
               "resource": RESOURCE, "ready": False, "owned_exit": False, "cleanup": False}
    done = multiprocessing.Event()
    ack_reader, ack_writer = multiprocessing.Pipe(duplex=False)
    monitor = None
    process = None
    pumps = []
    pump_states = []
    watchdog_lost = False
    physical_exit = False
    group = ""
    properties = {}
    budget = OutputBudget()
    deadline = time.monotonic() + JOB_SECONDS

    def observe_watchdog():
        nonlocal watchdog_lost
        try:
            require_watchdog_alive(monitor)
        except WatchdogFailure:
            watchdog_lost = True

    try:
        receipt["payload_sha256"] = prepare_payload(args.bundle, base / "payload")
        inputs = base / "payload" / "proof-input"
        inputs.mkdir(mode=0o555)
        inputs.chmod(0o555)
        declared = inputs / "declared.txt"
        declared.write_text("declared-native-input\n")
        declared.chmod(0o444)
        receipt["payload_sha256"]["proof-input/declared.txt"] = hashlib.sha256(declared.read_bytes()).hexdigest()
        receipt["protected_tool_sha256"] = hashlib.sha256(tool_bytes).hexdigest()
        monitor = multiprocessing.Process(target=watchdog, args=(unit, deadline, done, control, ack_writer))
        monitor.start()
        ack_writer.close()
        await_watchdog_ack(monitor, ack_reader)
        if time.monotonic() >= deadline:
            raise WatchdogFailure("watchdog-deadline-expired")
        receipt["watchdog_ack"] = True
        receipt["watchdog_pid"] = monitor.pid
        receipt["armed_monotonic"] = time.monotonic()
        (scratch / "armed").write_text("root-watchdog-armed\n")
        argv = service_command(unit, base / "payload", scratch, args.dotnet, args.subject_uid,
                               args.subject_gid, args.protected_tools, args.protected_output, control, args.case)
        require_watchdog_alive(monitor)
        process = subprocess.Popen(argv, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        for name, stream in (("stdout", process.stdout), ("stderr", process.stderr)):
            state = PumpState(name)
            pump_states.append(state)
            thread = threading.Thread(target=pump, args=(stream, control / (name + ".log"), budget, state), daemon=True)
            thread.start()
            pumps.append(thread)
        ready_deadline = min(deadline, time.monotonic() + READINESS_SECONDS)
        observed = {}
        startup_confirmed = False
        loaded_exec_seen = False
        startup_progress_seen = False
        while time.monotonic() < ready_deadline and process.poll() is None and not budget.exceeded.is_set():
            require_watchdog_alive(monitor)
            properties, complete = query_exec_startup(unit, ready_deadline, loaded_exec_seen, receipt, control,
                                                       deadline - JOB_SECONDS, startup_progress_seen)
            loaded_exec_seen = loaded_exec_seen or (properties["LoadState"] == "loaded" and properties["Type"] == "exec")
            startup_progress_seen = startup_progress_seen or properties["ActiveState"] in ("activating", "active")
            group = properties.get("ControlGroup", "") or group
            if not complete:
                time.sleep(0.05)
                continue
            current = observe_unit_processes(properties, args.subject_uid, group, receipt, control,
                                             deadline - JOB_SECONDS, ready_deadline)
            if current is None:
                time.sleep(0.05)
                continue
            startup_confirmed = True
            observed.update(current)
            if group:
                try:
                    ready, pid, unhealthy = ready_request(scratch / "http.sock", args.subject_uid, group, ready_deadline)
                    if time.monotonic() >= ready_deadline:
                        raise TimeoutError("readiness-deadline-expired")
                    if unhealthy:
                        receipt["unhealthy_peer_pid"] = pid
                    if ready:
                        receipt.update(ready=True, resource_pid=pid)
                        if args.case == "stuck-descendant":
                            marker = scratch / "descendant-armed"
                            try:
                                descendant = int(marker.read_text())
                                if marker.stat().st_uid != args.subject_uid or observed.get(str(descendant)) != "descendant" or not identity_matches(descendant, args.subject_uid, group):
                                    time.sleep(0.1)
                                    continue
                            except (FileNotFoundError, ValueError):
                                time.sleep(0.1)
                                continue
                        break
                except (OSError, ValueError):
                    pass
            time.sleep(0.1)
        require_watchdog_alive(monitor)
        if not startup_confirmed:
            properties = refresh_unconfirmed_startup(unit, ready_deadline, properties)
            record_startup_failure(receipt, control, properties, deadline - JOB_SECONDS)
            raise StartupFailure(STARTUP_FAILURE)
        receipt["observed_processes"] = observed
        receipt["output_bytes"] = budget.count
        receipt["output_quota_exceeded"] = budget.exceeded.is_set()
        if args.case in ("normal", "cancel", "stuck-descendant") and not receipt["ready"]:
            raise RuntimeError("Independent native readiness never succeeded.")
        if args.case in ("readiness-failure", "factory-stall"):
            if receipt["ready"]:
                raise RuntimeError("Negative control unexpectedly became ready.")
            if process.poll() is not None or not group or not cgroup_pids(group):
                raise RuntimeError("Negative control exited early instead of exercising its bounded failure.")
            live = {role for pid, role in observed.items() if int(pid) in cgroup_pids(group) and identity_matches(int(pid), args.subject_uid, group)}
            if args.case == "readiness-failure":
                peer = receipt.get("unhealthy_peer_pid")
                if not {"apphost", "dcp", "resource"}.issubset(live) or peer is None or not identity_matches(peer, args.subject_uid, group):
                    raise RuntimeError("Readiness-failure lacks actual live topology and authenticated HTTP 503.")
            if args.case == "factory-stall":
                marker = scratch / "factory-entered"
                if "apphost" not in live or {"dcp", "resource"}.intersection(observed.values()) or not marker.is_file() or marker.stat().st_uid != args.subject_uid or marker.read_text() != "factory-entered\n":
                    raise RuntimeError("Factory-stall lacks an actual armed factory or launched extra topology.")
        if receipt["ready"]:
            if "dcp" not in observed.values() or "apphost" not in observed.values():
                raise RuntimeError("AppHost and real DCP process evidence was not observed.")
            if args.case == "stuck-descendant" and "descendant" not in observed.values():
                raise RuntimeError("Stuck-descendant did not launch its actual signal-resistant child.")
            if args.case == "stuck-descendant":
                marker = scratch / "descendant-armed"
                pid = int(marker.read_text())
                if marker.stat().st_uid != args.subject_uid or observed.get(str(pid)) != "descendant" or not identity_matches(pid, args.subject_uid, group):
                    raise RuntimeError("Stuck-descendant lacks armed signal-handler kernel identity.")
        if budget.exceeded.is_set():
            raise RuntimeError("Combined child output quota exceeded.")
        receipt["control_result"] = "cancel-requested" if args.case == "cancel" else "bounded-control-complete"
    except Exception as error:
        record_control_failure(receipt, error, control, properties, deadline - JOB_SECONDS)
    finally:
        if process is not None:
            record_budget_diagnostic(receipt, control, group, deadline - JOB_SECONDS)
            try:
                physical_exit, receipt["stop_escalated"] = stop_and_join(unit, process, group, pumps, guard=observe_watchdog)
                receipt["owned_exit"] = physical_exit
                record_stop_result(receipt, args.case, process.returncode)
            except (OSError, subprocess.SubprocessError, ValueError) as error:
                receipt["cleanup_failure"] = "stop-or-join-failure"
                receipt["cleanup_error_class"] = type(error).__name__
            final_output_receipt(receipt, budget, pump_states)
            if not receipt.get("output_complete"):
                receipt["owned_exit"] = False
            receipt["protected_output_probe_absent"] = not output_probe.exists()
            if output_probe.exists():
                receipt["failure"] = "protected-output-probe-created"
        else:
            receipt["owned_exit"] = True
            physical_exit = True
        if monitor is not None:
            try:
                require_watchdog_alive(monitor)
                if watchdog_lost or not physical_exit:
                    raise WatchdogFailure("watchdog-disarm-unconfirmed")
                disarm_watchdog(monitor, done, ack_reader, physical_exit=physical_exit, ownership_lost=watchdog_lost)
                receipt["watchdog_clean_exit"] = True
                receipt["watchdog_exit_code"] = monitor.exitcode
            except WatchdogFailure:
                receipt["failure"] = "watchdog-unavailable"
                receipt["watchdog_failure"] = "watchdog-unavailable"
                receipt["watchdog_clean_exit"] = False
                receipt["watchdog_exit_code"] = monitor.exitcode
                receipt["owned_exit"] = False
                if monitor.pid is not None:
                    monitor.join(timeout=2)
        ack_reader.close()
        ack_writer.close()
        if receipt["owned_exit"] and not (control / "quarantine").exists():
            shutil.rmtree(scratch)
            shutil.rmtree(base / "payload", ignore_errors=True)
            receipt["cleanup"] = True
        else:
            (control / "quarantine").write_text("unconfirmed-exit; retain all paths\n")
        preserve_identity_rejection(receipt)
        receipt["receipt_path"] = str(control / "receipt.json")
        (control / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps(receipt))
    return 0 if receipt["owned_exit"] and receipt["cleanup"] and "failure" not in receipt else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--dotnet", type=Path, required=True)
    parser.add_argument("--subject-uid", type=int, required=True)
    parser.add_argument("--subject-gid", type=int, required=True)
    parser.add_argument("--protected-tools", type=Path, required=True)
    parser.add_argument("--protected-output", type=Path, required=True)
    parser.add_argument("--case", choices=CASES, default="normal")
    try:
        return prove(parser.parse_args())
    except Exception as error:
        print(json.dumps({"provisional": True, "trust_claim": False, "failure": "preparation-failure",
                          "error_class": type(error).__name__}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
