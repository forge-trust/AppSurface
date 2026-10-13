"""Bounded unprivileged native-preparation processes; copied reviewed helper ownership runner."""
from __future__ import annotations

import os
import pathlib
import re
import resource
import selectors
import signal
import stat
import subprocess
import time

OPERATIONS = frozenset({"native-prerequisite", "peer-helper", "coordinator-helper"})
MAX_LOG_BYTES = 8 * 1024 * 1024
COMMAND_FIELDS = frozenset({
    "operation", "exit", "waited", "leader_exit_observed", "descendants_settled",
    "forced_cleanup", "log", "log_bytes", "overflow", "discarded_bytes",
    "read_error", "write_error", "pipe_eof", "deadline_ok", "failure", "error",
})
READ_BYTES = 64 * 1024
GROUP_POLL_SECONDS = 0.05
PS_QUERY_SECONDS = 0.5
PS_REAP_RESERVE_SECONDS = 0.05


def successful_command_records(records) -> bool:
    """Validate the exact public command rows consumed by both helper handoffs."""
    expected = ("sdk-version", "locked-restore", "fdd-publish")
    if type(records) is not list or len(records) != len(expected):
        return False
    for index, (row, operation) in enumerate(zip(records, expected)):
        if type(row) is not dict or set(row) != COMMAND_FIELDS:
            return False
        if (type(row["operation"]) is not str or row["operation"] != operation
                or type(row["exit"]) is not int or row["exit"] != 0):
            return False
        if (row["waited"] is not True or row["leader_exit_observed"] is not True
                or row["descendants_settled"] is not True
                or row["forced_cleanup"] is not False or row["error"] is not False
                or row["overflow"] is not False or type(row["discarded_bytes"]) is not int
                or row["discarded_bytes"] != 0 or row["read_error"] is not False
                or row["write_error"] is not False or row["pipe_eof"] is not True
                or row["deadline_ok"] is not True or row["failure"] is not None
                or type(row["log"]) is not str or row["log"] != f"build-{index:02d}.log"):
            return False
        if type(row["log_bytes"]) is not int or not 0 <= row["log_bytes"] <= MAX_LOG_BYTES:
            return False
    return True


def _bounded_file(path: str, cap: int) -> bytes:
    fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
    try:
        blocks = []
        size = 0
        while True:
            block = os.read(fd, min(4096, cap + 1 - size))
            if not block:
                return b"".join(blocks)
            size += len(block)
            if size > cap:
                return b""
            blocks.append(block)
    finally:
        os.close(fd)


def uptime_ms() -> int:
    raw = _bounded_file("/proc/uptime", 128).split(b" ", 1)[0]
    if not re.fullmatch(rb"[0-9]+\.[0-9]+", raw):
        raise ValueError("invalid-proc-uptime")
    whole, fraction = raw.split(b".")
    return int(whole) * 1000 + int((fraction + b"000")[:3])


def _rlimit_value(value: int) -> int | None:
    return None if value == resource.RLIM_INFINITY else int(value)


def _mem_available_bytes() -> int | None:
    try:
        raw = _bounded_file("/proc/meminfo", 64 * 1024)
        match = re.search(rb"(?m)^MemAvailable:\s+([0-9]+)\s+kB\s*$", raw)
        return int(match.group(1)) * 1024 if match else None
    except (OSError, ValueError):
        return None


def _unescape_mount(value: str) -> str | None:
    out = re.sub(r"\\([0-7]{3})", lambda m: chr(int(m.group(1), 8)), value)
    if re.search(r"\\[^0-7]", out):
        return None
    return out


def _read_cgroup_file(path: pathlib.Path, cap: int = 4096) -> bytes | None:
    try:
        before = path.lstat()
        if not stat.S_ISREG(before.st_mode) or before.st_size > cap:
            return None
        fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
        try:
            after = os.fstat(fd)
            if (before.st_dev, before.st_ino) != (after.st_dev, after.st_ino):
                return None
            blocks = []
            size = 0
            while True:
                block = os.read(fd, min(1024, cap + 1 - size))
                if not block:
                    return b"".join(blocks)
                size += len(block)
                if size > cap:
                    return None
                blocks.append(block)
        finally:
            os.close(fd)
    except (OSError, ValueError):
        return None


def _cgroup_v2_memory() -> dict | None:
    """Read only the current process's safely resolved cgroup-v2 memory files."""
    try:
        cgroup_rows = _bounded_file("/proc/self/cgroup", 64 * 1024).decode("ascii").splitlines()
        mount_rows = _bounded_file("/proc/self/mountinfo", 1024 * 1024).decode("utf-8").splitlines()
    except (OSError, UnicodeError, ValueError):
        return None
    cgroup_path = None
    for row in cgroup_rows:
        fields = row.split(":", 2)
        if len(fields) == 3 and fields[0] == "0" and fields[1] == "":
            cgroup_path = fields[2]
            break
    mount_root = mount_point = None
    for row in mount_rows:
        halves = row.split(" - ", 1)
        if len(halves) != 2:
            continue
        left, right = halves
        rhs = right.split()
        lhs = left.split()
        if rhs and rhs[0] == "cgroup2" and len(lhs) >= 5:
            mount_root = _unescape_mount(lhs[3])
            mount_point = _unescape_mount(lhs[4])
            break
    if not cgroup_path or not mount_root or not mount_point:
        return None
    if not cgroup_path.startswith("/") or ".." in pathlib.PurePosixPath(cgroup_path).parts:
        return None
    if not mount_root.startswith("/") or ".." in pathlib.PurePosixPath(mount_root).parts:
        return None
    if cgroup_path == mount_root:
        relative = ""
    elif mount_root == "/" and cgroup_path.startswith("/"):
        relative = cgroup_path.lstrip("/")
    elif cgroup_path.startswith(mount_root.rstrip("/") + "/"):
        relative = cgroup_path[len(mount_root.rstrip("/")) + 1 :]
    else:
        return None
    try:
        base = pathlib.Path(mount_point).resolve(strict=True)
        selected = (base / relative).resolve(strict=True)
        if not selected.is_dir() or os.path.commonpath((str(base), str(selected))) != str(base):
            return None
    except (OSError, ValueError):
        return None
    current_raw = _read_cgroup_file(selected / "memory.current")
    maximum_raw = _read_cgroup_file(selected / "memory.max")
    events_raw = _read_cgroup_file(selected / "memory.events")
    current = int(current_raw.strip()) if current_raw and re.fullmatch(rb"[0-9]+\s*", current_raw) else None
    maximum_text = maximum_raw.strip() if maximum_raw is not None else None
    max_unlimited = maximum_text == b"max" if maximum_text is not None else None
    maximum = int(maximum_text) if maximum_text and re.fullmatch(rb"[0-9]+", maximum_text) else None
    events = None
    if events_raw is not None:
        parsed = {}
        valid = True
        for line in events_raw.splitlines():
            match = re.fullmatch(rb"([a-z_]{1,32})\s+([0-9]+)", line)
            if not match or match.group(1).decode() in parsed:
                valid = False
                break
            parsed[match.group(1).decode()] = int(match.group(2))
        if valid:
            events = parsed
    if current is None or maximum_raw is None or events is None:
        return None
    return {
        "path": str(selected),
        "current_bytes": current,
        "max_bytes": maximum,
        "max_unlimited": max_unlimited,
        "events": events,
    }


def resource_snapshot() -> dict:
    limits = {}
    for name in ("RLIMIT_AS", "RLIMIT_DATA", "RLIMIT_FSIZE"):
        key = getattr(resource, name, None)
        if key is None:
            limits[name] = None
            continue
        try:
            soft, hard = resource.getrlimit(key)
            limits[name] = {"soft_bytes": _rlimit_value(soft), "hard_bytes": _rlimit_value(hard)}
        except (OSError, ValueError):
            limits[name] = None
    return {
        "rlimits": limits,
        "system_mem_available_bytes": _mem_available_bytes(),
        "cgroup_memory": _cgroup_v2_memory(),
    }


def _bounded_ps_output(
    argv: list[str],
    cap: int,
    *,
    deadline: float | None = None,
    check=None,
) -> tuple[bytes | None, str | None]:
    """Capture a bounded ps-like child without buffering untrusted stdout."""
    started = time.monotonic()
    hard_deadline = started + PS_QUERY_SECONDS
    if deadline is not None:
        hard_deadline = min(hard_deadline, deadline)
    capture_deadline = hard_deadline - PS_REAP_RESERVE_SECONDS
    if cap < 0 or capture_deadline <= started:
        return None, "deadline"

    proc = None
    pipe = None
    selector = selectors.DefaultSelector()
    output = bytearray()
    reason = None
    check_error = None
    waited = False
    try:
        proc = subprocess.Popen(
            argv,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            bufsize=0,
        )
        pipe = proc.stdout
        if pipe is None:
            reason = "stdout-unavailable"
        else:
            os.set_blocking(pipe.fileno(), False)
            selector.register(pipe, selectors.EVENT_READ)
            eof = False
            while reason is None and not eof:
                if check is not None:
                    try:
                        check()
                    except BaseException as exc:
                        check_error = exc
                        reason = "deadline"
                        break
                remaining = capture_deadline - time.monotonic()
                if remaining <= 0:
                    reason = "deadline"
                    break
                ready = selector.select(min(remaining, 0.05))
                if not ready:
                    continue
                room = cap - len(output)
                chunk = os.read(pipe.fileno(), min(READ_BYTES, room + 1))
                if not chunk:
                    eof = True
                    break
                if len(chunk) > room:
                    reason = "output-limit"
                    break
                output.extend(chunk)

            if reason is None:
                remaining = max(0.0, capture_deadline - time.monotonic())
                try:
                    exit_code = proc.wait(timeout=remaining)
                    waited = True
                    if exit_code != 0:
                        reason = "ps-exit"
                except subprocess.TimeoutExpired:
                    reason = "deadline"
    except OSError:
        reason = reason or "ps-process-error"
    finally:
        try:
            selector.close()
        except BaseException:
            reason = reason or "selector-close-error"
        if pipe is not None:
            try:
                pipe.close()
            except OSError:
                reason = reason or "ps-pipe-close-error"
        if proc is not None and not waited:
            # Close the read side, stop only this direct census process, then
            # reap it inside the reserved part of the same query deadline.
            try:
                proc.kill()
            except ProcessLookupError:
                pass
            except OSError:
                reason = reason or "ps-stop-error"
            try:
                proc.wait(timeout=max(0.0, hard_deadline - time.monotonic()))
                waited = True
            except subprocess.TimeoutExpired:
                reason = "ps-reap-timeout"
            except OSError:
                reason = reason or "ps-reap-error"
    if check_error is not None:
        raise check_error
    if not waited:
        return None, reason or "ps-not-reaped"
    if reason is not None:
        return None, reason
    return bytes(output), None


def _leader_exit_observed(
    pid: int,
    *,
    deadline: float | None = None,
    check=None,
) -> tuple[bool, int | None]:
    """Observe exit without reaping, keeping PID/PGID identity reserved."""
    waitid = getattr(os, "waitid", None)
    flags = getattr(os, "WEXITED", 0) | getattr(os, "WNOHANG", 0) | getattr(os, "WNOWAIT", 0)
    if waitid is not None and flags and getattr(os, "WNOWAIT", 0):
        info = waitid(os.P_PID, pid, flags)
        if info is None or info.si_pid == 0:
            return False, None
        code = info.si_code
        if code == getattr(os, "CLD_EXITED", object()):
            return True, int(info.si_status)
        if code in (getattr(os, "CLD_KILLED", object()), getattr(os, "CLD_DUMPED", object())):
            return True, -int(info.si_status)
        return True, None
    # BSD/macOS fallback: a child remains a zombie until Popen.wait() reaps it.
    output, error = _bounded_ps_output(
        ["ps", "-p", str(pid), "-o", "stat="],
        4096,
        deadline=deadline,
        check=check,
    )
    if output is None or error is not None:
        raise ChildProcessError("leader-identity-unobservable")
    state = output.decode("ascii", "strict").strip()
    if not state:
        raise ChildProcessError("leader-identity-lost")
    return state.startswith("Z"), None


def _proc_group_settled(pgid: int, leader_pid: int) -> bool | None:
    try:
        with os.scandir("/proc") as entries:
            for entry in entries:
                if not entry.name.isdecimal():
                    continue
                pid = int(entry.name)
                if pid == leader_pid:
                    continue
                try:
                    raw = _bounded_file(f"/proc/{entry.name}/stat", 4096)
                except (FileNotFoundError, ProcessLookupError):
                    continue
                except (PermissionError, OSError, ValueError):
                    return None
                close = raw.rfind(b")")
                if close < 0:
                    return None
                fields = raw[close + 2 :].split()
                if len(fields) < 3:
                    return None
                state = fields[0]
                try:
                    process_group = int(fields[2])
                except ValueError:
                    return None
                if process_group == pgid and state not in (b"Z", b"X"):
                    return False
        return True
    except OSError:
        return None


def _ps_group_settled(
    pgid: int,
    leader_pid: int,
    *,
    deadline: float | None = None,
    check=None,
) -> bool | None:
    try:
        output, error = _bounded_ps_output(
            ["ps", "-axo", "pid=,pgid=,stat="],
            1024 * 1024,
            deadline=deadline,
            check=check,
        )
        if output is None or error is not None:
            return None
        for row in output.decode("ascii", "strict").splitlines():
            fields = row.split()
            if len(fields) != 3:
                return None
            pid_text, group_text, state = fields
            if not pid_text.isdecimal() or not group_text.isdecimal():
                return None
            if int(pid_text) != leader_pid and int(group_text) == pgid and not state.startswith(("Z", "X")):
                return False
        return True
    except TimeoutError:
        raise
    except (OSError, UnicodeError, subprocess.TimeoutExpired):
        return None


def _group_descendants_settled(
    pgid: int,
    leader_pid: int,
    *,
    deadline: float | None = None,
    check=None,
) -> bool | None:
    if pathlib.Path("/proc/self/stat").exists():
        return _proc_group_settled(pgid, leader_pid)
    return _ps_group_settled(pgid, leader_pid, deadline=deadline, check=check)


def _kill_group_while_identity_held(proc: subprocess.Popen, *, deadline: float | None = None) -> bool:
    """Signal only after observing the leader without reaping it."""
    _leader_exit_observed(proc.pid, deadline=deadline)
    os.killpg(proc.pid, signal.SIGKILL)
    return True


def _record_failure(state: dict, category: str) -> None:
    if state["failure"] is None:
        state["failure"] = category


def _drain(selector, pipe, log_fd: int, state: dict) -> None:
    for key, _ in selector.select(0.02):
        try:
            chunk = os.read(key.fd, READ_BYTES)
        except BlockingIOError:
            continue
        except OSError:
            state["read_error"] = True
            _record_failure(state, "pipe-read-error")
            try:
                selector.unregister(key.fileobj)
            except Exception:
                pass
            continue
        if not chunk:
            state["pipe_eof"] = True
            try:
                selector.unregister(key.fileobj)
            except Exception:
                pass
            continue
        room = max(0, MAX_LOG_BYTES - state["log_bytes"]) if not state["write_error"] else 0
        retained = min(room, len(chunk))
        written = 0
        while written < retained:
            try:
                count = os.write(log_fd, chunk[written:retained])
            except OSError:
                state["write_error"] = True
                _record_failure(state, "log-write-error")
                break
            if count <= 0:
                state["write_error"] = True
                _record_failure(state, "log-write-error")
                break
            written += count
            state["log_bytes"] += count
        state["discarded_bytes"] += len(chunk) - written
        if state["discarded_bytes"]:
            state["overflow"] = True
            _record_failure(state, "output-overflow")


def run_command(
    operation: str,
    argv: list[str],
    *,
    cwd: pathlib.Path,
    log_path: pathlib.Path,
    env: dict[str, str],
    check,
    cleanup_remaining,
) -> tuple[dict, dict]:
    """Run one allowlisted operation and return public and private receipts."""
    if operation not in OPERATIONS or not argv or any(not isinstance(arg, str) or "\x00" in arg for arg in argv):
        raise ValueError("helper-command-rejected")
    private = {"operation": operation, "argv": list(argv), "resources_before": resource_snapshot()}
    public = {
        "operation": operation,
        "exit": None,
        "waited": False,
        "leader_exit_observed": False,
        "descendants_settled": False,
        "forced_cleanup": False,
        "log": log_path.name,
        "log_bytes": 0,
        "overflow": False,
        "discarded_bytes": 0,
        "read_error": False,
        "write_error": False,
        "pipe_eof": False,
        "deadline_ok": True,
        "failure": None,
        "error": False,
    }
    state = {
        "failure": None,
        "log_bytes": 0,
        "discarded_bytes": 0,
        "overflow": False,
        "read_error": False,
        "write_error": False,
        "pipe_eof": False,
    }
    proc = None
    selector = selectors.DefaultSelector()
    log_fd = None
    leader_exited = False
    observed_exit = None
    descendants_settled = False
    last_group_check = 0.0
    signal_attempted = False
    log_closed = False
    try:
        check()
        log_fd = os.open(log_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_CLOEXEC | os.O_NOFOLLOW, 0o600)
        proc = subprocess.Popen(
            argv,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            cwd=cwd,
            start_new_session=True,
            env=env,
            bufsize=0,
        )
        assert proc.stdout is not None
        os.set_blocking(proc.stdout.fileno(), False)
        selector.register(proc.stdout, selectors.EVENT_READ)
        while True:
            try:
                check()
            except BaseException:
                public["deadline_ok"] = False
                _record_failure(state, "work-deadline")
                break
            _drain(selector, proc.stdout, log_fd, state)
            if state["failure"] is not None:
                break
            leader_exited, observed_exit = _leader_exit_observed(proc.pid, check=check)
            public["leader_exit_observed"] = leader_exited
            if leader_exited and observed_exit not in (None, 0):
                _record_failure(state, "command-exit-nonzero")
                break
            if leader_exited and time.monotonic() - last_group_check >= GROUP_POLL_SECONDS:
                group_state = _group_descendants_settled(proc.pid, proc.pid, check=check)
                last_group_check = time.monotonic()
                if group_state is None:
                    _record_failure(state, "group-observation-failed")
                    break
                descendants_settled = group_state
                public["descendants_settled"] = group_state
            if leader_exited and descendants_settled and state["pipe_eof"]:
                check()
                public["exit"] = observed_exit
                if observed_exit is not None and observed_exit != 0:
                    _record_failure(state, "command-exit-nonzero")
                break
    except BaseException as exc:
        if isinstance(exc, (TimeoutError,)):
            public["deadline_ok"] = False
            _record_failure(state, "work-deadline")
        else:
            _record_failure(state, "runner-error")
    finally:
        if proc is not None:
            deadline = time.monotonic() + max(0.0, float(cleanup_remaining()))
            if state["failure"] is not None and not signal_attempted:
                try:
                    still_exited, _ = _leader_exit_observed(proc.pid, deadline=deadline)
                    if still_exited:
                        group_state = _group_descendants_settled(proc.pid, proc.pid, deadline=deadline)
                        need_signal = group_state is not True
                    else:
                        need_signal = True
                    if need_signal:
                        public["forced_cleanup"] = True
                        _kill_group_while_identity_held(proc, deadline=deadline)
                        signal_attempted = True
                except (OSError, ChildProcessError):
                    _record_failure(state, "group-kill-error")
            while time.monotonic() < deadline:
                if not state["pipe_eof"] and proc.stdout is not None:
                    _drain(selector, proc.stdout, log_fd, state)
                try:
                    leader_exited, observed_exit = _leader_exit_observed(proc.pid, deadline=deadline)
                    public["leader_exit_observed"] = leader_exited
                    if leader_exited and time.monotonic() - last_group_check >= GROUP_POLL_SECONDS:
                        group_state = _group_descendants_settled(proc.pid, proc.pid, deadline=deadline)
                        last_group_check = time.monotonic()
                        if group_state is None:
                            _record_failure(state, "group-observation-failed")
                        else:
                            descendants_settled = group_state
                            public["descendants_settled"] = group_state
                except BaseException:
                    leader_exited = False
                if leader_exited and descendants_settled and state["pipe_eof"]:
                    break
                time.sleep(0.01)
            # If EOF never arrived by the original cleanup end, stop observing this
            # pipe and fail closed. No process-group signal is sent after wait().
            if not state["pipe_eof"] and proc.stdout is not None:
                try:
                    selector.unregister(proc.stdout)
                except Exception:
                    pass
                try:
                    proc.stdout.close()
                    log_closed = True
                except OSError:
                    _record_failure(state, "pipe-close-failed")
            try:
                proc.wait(timeout=max(0.0, deadline - time.monotonic()))
                public["waited"] = True
                public["exit"] = proc.returncode
                if proc.returncode != 0:
                    _record_failure(state, "command-exit-nonzero")
            except (subprocess.TimeoutExpired, OSError):
                _record_failure(state, "process-wait-failed")
            # The leader is now reaped. All group observation/signalling is complete.
        if not state["pipe_eof"]:
            _record_failure(state, "pipe-eof-missing")
        try:
            selector.close()
        except BaseException:
            _record_failure(state, "selector-close-failed")
        if proc is not None and proc.stdout is not None and not log_closed:
            try:
                proc.stdout.close()
            except BaseException:
                _record_failure(state, "pipe-close-failed")
        if log_fd is not None:
            try:
                os.fsync(log_fd)
                os.close(log_fd)
            except OSError:
                _record_failure(state, "log-close-failed")
        public.update({
            "log_bytes": state["log_bytes"],
            "overflow": state["overflow"],
            "discarded_bytes": state["discarded_bytes"],
            "read_error": state["read_error"],
            "write_error": state["write_error"],
            "pipe_eof": state["pipe_eof"],
            "failure": state["failure"],
            "error": state["failure"] is not None or public["exit"] != 0 or public["waited"] is not True or public["descendants_settled"] is not True or public["forced_cleanup"] or state["overflow"] or state["read_error"] or state["write_error"] or state["pipe_eof"] is not True or not public["deadline_ok"],
        })
        try:
            private["resources_after"] = resource_snapshot()
        except BaseException:
            private["resources_after"] = None
    return public, private
