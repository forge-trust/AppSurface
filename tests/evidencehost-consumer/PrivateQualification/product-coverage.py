"""Private root Coverlet owner with an independent provider supervisor.

No worker grant, runtime enrollment or acceptance authority is supplied here.
The watchdog directly spawns its provider and holds stdin until physical
consumer exit. Uncertain settlement uses SIGKILL, never EOF/TERM restoration.
"""
from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import selectors
import signal
import socket
import stat
import struct
import subprocess
import sys
import threading
import time
import uuid
import xml.etree.ElementTree as ET

LIBRARIES = tuple("ForgeTrust.AppSurface.Evidence." + s for s in ("Cli", "Aspire", "Coverage"))
ELIGIBLE = frozenset(s + ext for s in LIBRARIES for ext in (".dll", ".pdb"))
REPORTS = ("coverage.cobertura.xml", "coverage.json")
SESSION = "product-coverage-session"
FILE_LIMIT, TREE_LIMIT, MAX_FILES = 32 << 20, 256 << 20, 2048
IPC_LIMIT, LOG_LIMIT = 256 << 10, 192 << 10
ENV = {"PATH": "/usr/bin:/bin", "HOME": "/nonexistent"}


class ProductCoverageError(RuntimeError):
    """Only closed local categories leave the private owner."""


def require(value, category):
    if not value:
        raise ProductCoverageError(category)


def left(deadline):
    require(type(deadline) in (int, float) and math.isfinite(deadline), "deadline")
    value = deadline-time.monotonic()
    require(value > 0, "deadline")
    return value


def basename(value):
    require(type(value) is str and re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", value)
            and ".." not in value, "basename")
    return value


def identity(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_uid, info.st_gid,
            info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def directory_identity(info):
    return (info.st_dev, info.st_ino, info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))


def read_file(parent_fd, name, deadline, *, uid=0, gid=None, mode=None, cap=FILE_LIMIT):
    """Borrow one directory FD; reject links, growth and named substitution."""
    left(deadline)
    basename(name)
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent_fd)
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid == uid
                and before.st_dev == os.fstat(parent_fd).st_dev and 0 <= before.st_size <= cap, "file-shape")
        require(gid is None or before.st_gid == gid, "file-gid")
        require(mode is None or stat.S_IMODE(before.st_mode) == mode, "file-mode")
        data = bytearray()
        while len(data) < before.st_size:
            left(deadline)
            part = os.read(fd, min(65536, before.st_size-len(data)))
            require(bool(part), "file-short")
            data.extend(part)
        require(os.read(fd, 1) == b"" and identity(before) == identity(os.fstat(fd))
                == identity(os.stat(name, dir_fd=parent_fd, follow_symlinks=False)), "file-changed")
        result = bytes(data), identity(before)
    finally:
        os.close(fd)
    left(deadline)
    return result


def open_directory(path, deadline, *, uid=0, gid=None, mode=None):
    """Pin an absolute directory through no-follow ancestor handles."""
    require(type(path) is Path or isinstance(path, Path), "directory-path")
    require(path.is_absolute() and not any(s in (".", "..") for s in path.parts), "directory-path")
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for part in path.parts[1:]:
            left(deadline)
            child = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
            os.close(fd)
            fd = child
        info = os.fstat(fd)
        require(info.st_uid == uid and (gid is None or info.st_gid == gid)
                and (mode is None or stat.S_IMODE(info.st_mode) == mode), "directory-owner")
        require(directory_identity(info) == directory_identity(path.stat(follow_symlinks=False)), "directory-changed")
        left(deadline)
        result, fd = fd, -1
        return result
    finally:
        if fd >= 0: os.close(fd)


def names(fd, deadline, maximum=MAX_FILES):
    result = []
    with os.scandir(fd) as rows:
        for row in rows:
            left(deadline)
            require(len(result) < maximum, "file-count")
            result.append(basename(row.name))
    return sorted(result)


def snapshot_tree(path, deadline, *, uid=0, gid=None):
    """Bounded real-file map. Portable UID overrides grant no owner capability."""
    files, directories, total = {}, {}, 0
    root = open_directory(path, deadline, uid=uid, gid=gid)
    def walk(fd, prefix, depth):
        nonlocal total
        require(depth <= 8 and len(files)+len(directories) < MAX_FILES, "tree-bound")
        info = os.fstat(fd)
        require(info.st_uid == uid and (gid is None or info.st_gid == gid) and not info.st_mode & 0o022, "tree-owner")
        directories[prefix] = format(stat.S_IMODE(info.st_mode), "04o")
        for name in names(fd, deadline):
            relative = name if not prefix else prefix+"/"+name
            require(len(files)+len(directories) < MAX_FILES, "tree-bound")
            selected = os.stat(name, dir_fd=fd, follow_symlinks=False)
            if stat.S_ISDIR(selected.st_mode):
                child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=fd)
                try:
                    require(directory_identity(selected) == directory_identity(os.fstat(child)), "tree-changed")
                    walk(child, relative, depth+1)
                finally: os.close(child)
            else:
                require(not selected.st_mode & 0o022, "tree-mode")
                data, info = read_file(fd, name, deadline, uid=uid, gid=gid)
                total += len(data)
                require(total <= TREE_LIMIT, "tree-byte-bound")
                files[relative] = {"sha256": hashlib.sha256(data).hexdigest(), "mode": format(stat.S_IMODE(info[2]), "04o")}
    try: walk(root, "", 0)
    finally: os.close(root)
    left(deadline)
    return files, directories


def kernel(path, deadline, cap=1 << 20):
    left(deadline)
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC)
    try:
        require(stat.S_ISREG(os.fstat(fd).st_mode), "kernel-type")
        result = bytearray()
        while True:
            left(deadline)
            part = os.read(fd, min(65536, cap+1-len(result)))
            if not part: break
            result.extend(part)
            require(len(result) <= cap, "kernel-byte-bound")
        data = bytes(result)
    finally: os.close(fd)
    left(deadline)
    return data


def starttime(pid, deadline):
    data = kernel(f"/proc/{pid}/stat", deadline, 8192)
    fields = data[data.rfind(b")")+2:].split()
    require(len(fields) >= 20 and fields[19].isdigit(), "process-identity")
    return int(fields[19])


def packet(data, stage):
    require(type(data) is bytes and len(data) <= 16384, "provider-packet-bound")
    def unique(rows):
        value = {}
        for key, item in rows:
            require(key not in value, "provider-packet-duplicate")
            value[key] = item
        return value
    value = json.loads(data, object_pairs_hook=unique)
    expected = {"stage", "state", "temp"} if stage == "prepared" else {"stage", "passed", "errors", "warnings"}
    require(type(value) is dict and set(value) == expected and value["stage"] == stage, "provider-packet-schema")
    if stage == "prepared":
        require(all(type(value[k]) is str and len(value[k]) <= 4096 for k in ("state", "temp")), "provider-packet-path")
    else:
        require(value["passed"] is True and type(value["errors"]) is int and value["errors"] == 0
                and type(value["warnings"]) is int and value["warnings"] == 0, "provider-packet-failed")
    return value


class Child:
    """The independent watchdog is the actual provider parent and stdin owner."""
    def __init__(self, argv, environment):
        self.process = subprocess.Popen(argv, env=environment, stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True, start_new_session=True)
        self.selector = None
        try:
            self.data = {"stdout": bytearray(), "stderr": bytearray()}
            self.selector = selectors.DefaultSelector()
            self.offset = 0
            self.packets = []
            for key, stream in (("stdout", self.process.stdout), ("stderr", self.process.stderr)):
                os.set_blocking(stream.fileno(), False)
                self.selector.register(stream, selectors.EVENT_READ, key)
        except BaseException:
            # Popen has already created the owned child. Never let construction
            # failure release stdin while a managed process can restore files.
            try: os.killpg(self.process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
            while self.process.poll() is None:
                try: self.process.wait(timeout=1)
                except subprocess.TimeoutExpired: continue
            self.close()
            raise

    def pump(self, deadline, delay=0.01):
        for key, _ in self.selector.select(min(delay, left(deadline))):
            part = os.read(key.fileobj.fileno(), 8192)
            if not part: self.selector.unregister(key.fileobj)
            else:
                cap = 64 << 10 if key.data == "stdout" else 128 << 10
                require(len(self.data[key.data])+len(part) <= cap, "provider-log-bound")
                self.data[key.data].extend(part)

    def next_packet(self, stage):
        end = self.data["stdout"].find(b"\n", self.offset)
        while end >= 0:
            line = bytes(self.data["stdout"][self.offset:end])
            self.offset = end+1
            if line.lstrip().startswith(b"{"):
                require(line.startswith(b"{"), "provider-packet-framing")
                value = packet(line, stage)
                self.packets.append(value)
                return value
            end = self.data["stdout"].find(b"\n", self.offset)
        return None

    def absent(self):
        self.process.poll()
        try: os.killpg(self.process.pid, 0); return False
        except ProcessLookupError: return True

    def collect(self, deadline):
        left(deadline)
        require(self.process.poll() is None, "provider-early-exit")
        os.set_blocking(self.process.stdin.fileno(), False)
        require(os.write(self.process.stdin.fileno(), b"collect\n") == 8, "provider-input")

    def joined(self, deadline):
        left(deadline)
        code = self.process.poll()
        if code is None or self.selector.get_map(): return False
        require(code == 0 and self.absent(), "provider-exit")
        # No extra JSON packet is allowed after the exact collected packet.
        require(self.next_packet("collected") is None and len(self.packets) == 2, "provider-extra-packet")
        return True

    def kill_without_restore(self, deadline):
        """No TERM, collect command or EOF may precede uncatchable termination."""
        try: os.killpg(self.process.pid, signal.SIGKILL)
        except ProcessLookupError: pass
        self.process.wait(timeout=left(deadline))
        while self.selector.get_map(): self.pump(deadline)
        require(self.absent(), "provider-kill-unconfirmed")
        left(deadline)

    def close(self):
        for stream in (self.process.stdin, self.process.stdout, self.process.stderr): stream.close()
        if self.selector is not None: self.selector.close()


def command(argv, deadline):
    """Bound output before buffering and settle every actual post-spawn resource."""
    end = min(deadline, time.monotonic() + 5)
    left(end)
    selected = None
    process = subprocess.Popen(argv, env=ENV, stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, close_fds=True, start_new_session=True)
    failure = None
    result = None
    try:
        data = {"stdout": bytearray(), "stderr": bytearray()}
        selected = selectors.DefaultSelector()
        for key, stream in (("stdout", process.stdout), ("stderr", process.stderr)):
            os.set_blocking(stream.fileno(), False)
            selected.register(stream, selectors.EVENT_READ, key)
        while selected.get_map() or process.poll() is None:
            for key, _ in selected.select(min(.01, left(end))):
                part = os.read(key.fileobj.fileno(), 8192)
                if not part: selected.unregister(key.fileobj)
                else:
                    require(sum(map(len, data.values())) + len(part) <= 65536, "root-command-bound")
                    data[key.data].extend(part)
        require(process.wait(timeout=left(end)) == 0, "root-command-exit")
        result = bytes(data['stdout'])
    except BaseException as error:
        failure = error
    finally:
        try:
            try: os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
        except BaseException as error:
            if failure is None: failure = error
        try: process.wait(timeout=max(.001, min(5, deadline-time.monotonic())))
        except BaseException as error:
            if failure is None: failure = error
        for stream in (process.stdout, process.stderr):
            try: stream.close()
            except BaseException as error:
                if failure is None: failure = error
        if selected is not None:
            try: selected.close()
            except BaseException as error:
                if failure is None: failure = error
        try:
            try: os.killpg(process.pid, 0)
            except ProcessLookupError: pass
            else: raise ProductCoverageError("root-command-residue")
        except BaseException as error:
            if failure is None: failure = error
    if failure is not None: raise failure
    left(end)
    return result


def unit_kind(unit, worker):
    match = re.fullmatch(r"evidencehost-([0-9a-f]{12})-worker\.service", worker)
    require(match is not None and type(unit) is str, "unit-selection")
    if unit == worker: return "worker"
    found = re.fullmatch(r"evidencehost-"+match.group(1)+r"-s-(0|[1-9][0-9]{0,2})\.service", unit)
    if found and int(found.group(1)) < 128: return "producer"
    if re.fullmatch(r"issue779-app-[0-9a-f]{32}\.service", unit): return "application"
    raise ProductCoverageError("unit-selection")


def capture_group(unit, captured, deadline):
    path = Path("/sys/fs/cgroup/system.slice") / unit
    try: fd = open_directory(path, deadline, uid=0, gid=0)
    except FileNotFoundError: return
    try:
        value = directory_identity(os.fstat(fd))
        require(unit not in captured or captured[unit] == value, "cgroup-replaced")
        captured[unit] = value
    finally: os.close(fd)


def units_empty(units, captured, deadline):
    """Use generated paths and captured identities, never a GC-cleared path value."""
    for unit in units:
        left(deadline)
        capture_group(unit, captured, deadline)
        path = Path("/sys/fs/cgroup/system.slice") / unit
        try: fd = open_directory(path, deadline, uid=0, gid=0)
        except FileNotFoundError:
            require(unit in captured, "consumer-never-captured")
            continue
        try:
            require(command(["/usr/bin/systemctl", "show", unit, "--property=MainPID", "--value"], deadline).strip() == b"0", "consumer-mainpid")
            require(unit in captured and directory_identity(os.fstat(fd)) == captured[unit], "cgroup-replaced")
            with os.scandir(fd) as rows:
                for row in rows:
                    left(deadline)
                    require(not row.is_dir(follow_symlinks=False), "consumer-child-cgroup")
            require(not kernel(str(path/"cgroup.procs"), deadline, 4096).strip()
                    and kernel(str(path/"cgroup.events"), deadline, 4096).decode().splitlines().count("populated 0") == 1,
                    "consumer-populated")
        finally: os.close(fd)
    left(deadline)


def stop_units(units, captured, deadline):
    """Attempt every exact registered unit; all errors retain failure."""
    failure = None
    for operation in ("stop", "kill"):
        for unit in sorted(units):
            try:
                capture_group(unit, captured, deadline)
                options = ["--no-block"] if operation == "stop" else ["--kill-whom=all", "--signal=KILL"]
                command(["/usr/bin/systemctl", operation, *options, unit], deadline)
            except BaseException as error:
                if failure is None: failure = error
    units_empty(units, captured, deadline)
    if failure is not None: raise failure


def send(channel, value, deadline):
    raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    require(len(raw) <= IPC_LIMIT, "ipc-bound")
    while True:
        left(deadline)
        try:
            require(channel.send(raw) == len(raw), "ipc-send")
            return
        except BlockingIOError:
            with selectors.DefaultSelector() as selected:
                selected.register(channel, selectors.EVENT_WRITE)
                selected.select(min(0.01, left(deadline)))


def receive(channel, deadline, pid, uid):
    while True:
        left(deadline)
        with selectors.DefaultSelector() as selected:
            selected.register(channel, selectors.EVENT_READ)
            if not selected.select(min(0.01, left(deadline))): continue
        raw, ancillary, flags, _ = channel.recvmsg(IPC_LIMIT+1, socket.CMSG_SPACE(12))
        require(raw and len(raw) <= IPC_LIMIT and not flags & (socket.MSG_TRUNC | socket.MSG_CTRUNC), "ipc-bound")
        require(len(ancillary) == 1 and ancillary[0][:2] == (socket.SOL_SOCKET, socket.SCM_CREDENTIALS)
                and len(ancillary[0][2]) == 12, "ipc-credentials")
        actual = struct.unpack("3i", ancillary[0][2])
        require(actual[0] == pid and actual[1] == uid, "ipc-credentials")
        def unique(rows):
            value = {}
            for key, item in rows:
                require(key not in value, "ipc-duplicate")
                value[key] = item
            return value
        value = json.loads(raw, object_pairs_hook=unique)
        require(type(value) is dict, "ipc-schema")
        return value


def pidfd_exited(fd):
    with selectors.DefaultSelector() as selected:
        selected.register(fd, selectors.EVENT_READ)
        return bool(selected.select(0))


def watchdog_service(channel, parent_pid, parent_start, worker, argv, environment, deadline):
    """Actual direct provider owner. Failure never releases stdin before SIGKILL/join."""
    parent_fd = None
    child = None
    units, captured = {worker: "worker"}, {}
    dispatched = collecting = collected = False
    try:
        require(os.geteuid() == 0 and os.getsid(0) == os.getpid(), "watchdog-root")
        parent_fd = os.pidfd_open(parent_pid, 0)
        require(starttime(parent_pid, deadline) == parent_start and os.getppid() == parent_pid, "watchdog-parent")
        send(channel, {"stage": "ready", "pid": os.getpid()}, deadline)
        while True:
            left(deadline)
            if pidfd_exited(parent_fd) or os.getppid() != parent_pid or time.monotonic() >= deadline-5:
                raise ProductCoverageError("watchdog-parent-or-deadline")
            for unit in units: capture_group(unit, captured, deadline)
            if child is not None:
                child.pump(deadline)
                if not collected:
                    require(child.process.poll() is None or collecting, "provider-early-exit")
            with selectors.DefaultSelector() as selected:
                selected.register(channel, selectors.EVENT_READ)
                if not selected.select(min(0.01, left(deadline))): continue
            request = receive(channel, deadline, parent_pid, 0)
            operation = request.get("op")
            if operation == "start":
                require(set(request) == {"op"} and child is None and not dispatched, "watchdog-start-order")
                child = Child(argv, environment)  # this process owns Popen and stdin atomically
                ready = None
                while ready is None:
                    left(deadline)
                    require(not pidfd_exited(parent_fd) and time.monotonic() < deadline-5, "watchdog-parent-or-deadline")
                    child.pump(deadline)
                    ready = child.next_packet("prepared")
                    require(child.process.poll() is None, "provider-early-exit")
                reply = {"stage": "prepared", "provider": ready, "pid": child.process.pid}
            elif operation == "register":
                require(set(request) == {"op", "unit"} and child is not None and not collecting, "watchdog-registration")
                unit, kind = request["unit"], unit_kind(request["unit"], worker)
                require(len(units) < 130 or unit in units, "watchdog-unit-bound")
                require(kind != "application" or unit in units or "application" not in units.values(), "watchdog-application-bound")
                units[unit] = kind
                reply = {"stage": "registered"}
            elif operation == "dispatch":
                require(set(request) == {"op"} and child is not None and not dispatched and not collecting, "watchdog-dispatch")
                dispatched = True
                reply = {"stage": "dispatched"}
            elif operation == "ping":
                require(set(request) == {"op"} and child is not None and not collecting, "watchdog-ping")
                reply = {"stage": "live"}
            elif operation == "confirm":
                require(set(request) == {"op"} and child is not None and dispatched and not collecting, "watchdog-confirm-order")
                units_empty(units, captured, deadline)
                reply = {"stage": "empty", "captured": {unit: list(captured[unit]) for unit in units}}
            elif operation == "collect":
                require(set(request) == {"op"} and child is not None and not collecting, "watchdog-collect-order")
                units_empty(units, captured, deadline)
                collecting = True
                child.collect(deadline)
                value = None
                while value is None or not child.joined(deadline):
                    require(not pidfd_exited(parent_fd) and time.monotonic() < deadline-5, "watchdog-parent-or-deadline")
                    child.pump(deadline)
                    if value is None: value = child.next_packet("collected")
                collected = True
                reply = {"stage": "collected", "provider": value,
                    "stdout": bytes(child.data["stdout"]).decode("utf-8", "strict"),
                    "stderr": bytes(child.data["stderr"]).decode("utf-8", "strict"),
                    "exit_code": 0, "two_eof": True, "group_absent": True}
            elif operation == "abort":
                require(set(request) == {"op"}, "watchdog-abort-schema")
                # Controlled failure never sends EOF or permits managed restoration.
                if child is not None:
                    child.kill_without_restore(deadline)
                    child.close()
                    child = None
                try: stop_units(units, captured, deadline)
                except BaseException: pass
                send(channel, {"stage": "aborted", "provider_reaped": True}, deadline)
                return 1
            elif operation == "disarm":
                require(set(request) == {"op"} and collected, "watchdog-disarm-order")
                units_empty(units, captured, deadline)
                child.close()
                child = None
                send(channel, {"stage": "done"}, deadline)
                left(deadline)
                return 0
            else:
                raise ProductCoverageError("watchdog-request")
            send(channel, reply, deadline)
    except BaseException:
        # Prevent a paused parent from dispatching after the selected empty check.
        if parent_fd is not None:
            try:
                if not pidfd_exited(parent_fd): signal.pidfd_send_signal(parent_fd, signal.SIGKILL)
                with selectors.DefaultSelector() as selected:
                    selected.register(parent_fd, selectors.EVENT_READ)
                    require(selected.select(max(0, deadline-time.monotonic())), "parent-exit-unconfirmed")
                stop_units(units, captured, deadline)
            except BaseException:
                pass  # never an exit/restoration or cleanup-positive fact
        # Even when consumers are uncertain, prevent managed EOF/ProcessExit restore.
        if child is not None:
            try: child.kill_without_restore(deadline)
            except BaseException:
                try: os.killpg(child.process.pid, signal.SIGKILL)
                except ProcessLookupError: pass
                # This independent process is the actual provider parent/reaper.
                # Retain stdin during unresolved kernel exit, keep failure forever,
                # and perform no further consumer work, collection or restoration.
                # This is quarantined retention, not an added success grace period.
                while child.process.poll() is None:
                    try: child.process.wait(timeout=1)
                    except subprocess.TimeoutExpired: continue
                child.close()
                child = None
                return 2
            finally:
                if child is not None and child.process.poll() is not None:
                    child.close()
        return 1
    finally:
        if parent_fd is not None: os.close(parent_fd)
        channel.close()


class Watchdog:
    """Private credential-authenticated RPC; no caller transport or argv injection."""
    def __init__(self, worker, argv, environment, deadline):
        self.worker, self.argv, self.environment, self.deadline = worker, argv, environment, deadline
        self.pid = self.channel = None
        self.disarmed = False

    def start(self):
        require(platform.system() == "Linux" and os.geteuid() == 0 and threading.active_count() == 1,
                "watchdog-fork-requirements")
        parent_pid = os.getpid()
        parent_start = starttime(parent_pid, self.deadline)
        parent, child = socket.socketpair(socket.AF_UNIX, socket.SOCK_SEQPACKET)
        for channel in (parent, child):
            channel.setsockopt(socket.SOL_SOCKET, socket.SO_PASSCRED, 1)
            channel.setblocking(False)
            channel.set_inheritable(False)
        pid = os.fork()
        if pid == 0:
            code = 1
            try:
                parent.close()
                os.setsid()
                null = os.open("/dev/null", os.O_RDWR | os.O_NOFOLLOW | os.O_CLOEXEC)
                require(stat.S_ISCHR(os.fstat(null).st_mode), "watchdog-null")
                for fd in (0, 1, 2): os.dup2(null, fd)
                keep = child.fileno()
                selected = [int(s) for s in os.listdir("/proc/self/fd") if s.isdecimal()]
                require(len(selected) <= 4096, "watchdog-fd-bound")
                for fd in selected:
                    if fd > 2 and fd != keep:
                        try: os.close(fd)
                        except OSError: pass
                code = watchdog_service(child, parent_pid, parent_start, self.worker, self.argv, self.environment, self.deadline)

            except BaseException:
                code = 1
            finally:
                os._exit(code)
        child.close()
        self.pid, self.channel = pid, parent
        require(receive(parent, self.deadline, pid, 0) == {"stage": "ready", "pid": pid}, "watchdog-ready")
        self.live()

    def live(self):
        left(self.deadline)
        require(self.pid is not None and not self.disarmed, "watchdog-not-live")
        pid, _ = os.waitpid(self.pid, os.WNOHANG)
        require(pid == 0, "watchdog-exited")

    def request(self, operation, stage, **fields):
        self.live()
        send(self.channel, {"op": operation, **fields}, self.deadline)
        reply = receive(self.channel, self.deadline, self.pid, 0)
        require(reply.get("stage") == stage, "watchdog-ack")
        if stage != "done": self.live()
        return reply

    def disarm(self):
        require(self.request("disarm", "done") == {"stage": "done"}, "watchdog-done")
        while True:
            left(self.deadline)
            pid, status = os.waitpid(self.pid, os.WNOHANG)
            if pid:
                require(os.WIFEXITED(status) and os.WEXITSTATUS(status) == 0, "watchdog-exit")
                break
            with selectors.DefaultSelector() as selected: selected.select(min(0.01, left(self.deadline)))
        self.channel.close()
        left(self.deadline)
        self.disarmed = True


def mount_identifier(fd, deadline):
    values = []
    for line in kernel(f"/proc/self/fdinfo/{fd}", deadline, 4096).decode('ascii').splitlines():
        key, separator, value = line.partition(':')
        if key == 'mnt_id':
            require(separator and value.strip().isdigit(), 'mount-id')
            values.append(int(value.strip()))
    require(len(values) == 1 and values[0] > 0, 'mount-id')
    return values[0]


def validate_mount(text, path, device, selected):
    """Data check for the actual opened mount, including a worker namespace view."""
    require(type(text) is str and len(text.encode()) <= 1 << 20, 'mount-bound')
    matches = []
    for line in text.splitlines():
        first, separator, second = line.partition(' - ')
        before, after = first.split(), second.split()
        if separator and len(before) >= 6 and before[4] == str(path) and before[0] == str(selected):
            matches.append((before, after))
    require(len(matches) == 1, 'mount-selected')
    before, after = matches[0]
    require(len(after) >= 3 and after[0] == 'tmpfs'
            and before[2] == f'{os.major(device)}:{os.minor(device)}'
            and {'nosuid', 'nodev', 'noexec'} <= set(before[5].split(',')) | set(after[2].split(',')), 'mount-properties')
    return {'tmpfs': True, 'nosuid': True, 'nodev': True, 'noexec': True, 'opened_mount_id': selected}


def seal_file(fd, name, deadline, mode):
    """Change only the pinned root-owned single-link file, then recheck its name."""
    before = os.stat(name, dir_fd=fd, follow_symlinks=False)
    require(stat.S_ISREG(before.st_mode) and before.st_uid == 0 and before.st_nlink == 1, 'seal-file')
    selected = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=fd)
    try:
        require(identity(before) == identity(os.fstat(selected)), 'seal-substitution')
        os.fchmod(selected, mode)
        require(identity(os.fstat(selected)) == identity(os.stat(name, dir_fd=fd, follow_symlinks=False)), 'seal-substitution')
    finally:
        os.close(selected)
    left(deadline)


def restore_metadata(tool, original, deadline, *, uid=0, gid=0):
    """Preflight all hashes, retain verified FDs, then restore captured metadata.

    Portable UID/GID overrides exercise file procedures only. They cannot issue
    the root owner, a permission grant, consumer-exit receipt or admission.
    """
    changes = []
    failure = None
    total = 0
    require(type(original) is tuple and len(original) == 2 and len(original[0])+len(original[1]) <= MAX_FILES, 'restore-map')
    try:
        for relative, entry in original[0].items():
            parts = Path(relative).parts
            require(parts and not Path(relative).is_absolute() and len(parts) <= 8, 'restore-path')
            for part in parts: basename(part)
            parent = open_directory(tool / Path(relative).parent, deadline, uid=uid)
            file_fd = -1
            try:
                data, verified = read_file(parent, parts[-1], deadline, uid=uid)
                total += len(data)
                require(total <= TREE_LIMIT and hashlib.sha256(data).hexdigest() == entry['sha256'], 'restore-hash')
                file_fd = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=parent)
                require(identity(os.fstat(file_fd)) == verified
                        == identity(os.stat(parts[-1], dir_fd=parent, follow_symlinks=False)), 'restore-pin')
                changes.append((parent, file_fd, parts[-1], verified, int(entry['mode'], 8)))
                parent = file_fd = -1
            finally:
                original_error = sys.exc_info()[1]
                close_error = None
                for fd in (file_fd, parent):
                    if fd >= 0:
                        try: os.close(fd)
                        except BaseException as error:
                            if close_error is None: close_error = error
                if original_error is None and close_error is not None: raise close_error
        # No metadata operation has occurred before EVERY original hash passed.
        for parent, file_fd, name, verified, mode in changes:
            left(deadline)
            require(identity(os.fstat(file_fd)) == verified
                    == identity(os.stat(name, dir_fd=parent, follow_symlinks=False)), 'restore-pin')
            os.fchown(file_fd, uid, gid)
            os.fchmod(file_fd, mode)
            require(identity(os.fstat(file_fd)) == identity(os.stat(name, dir_fd=parent, follow_symlinks=False)), 'restore-name')
        for relative, mode in sorted(original[1].items(), key=lambda item: item[0].count('/'), reverse=True):
            require(not Path(relative).is_absolute(), 'restore-path')
            for part in Path(relative).parts: basename(part)
            fd = open_directory(tool / relative, deadline, uid=uid)
            try:
                os.fchown(fd, uid, gid)
                os.fchmod(fd, int(mode, 8))
            finally: os.close(fd)
    except BaseException as error:
        failure = error
    finally:
        for parent, file_fd, _, _, _ in reversed(changes):
            for fd in (file_fd, parent):
                try: os.close(fd)
                except BaseException as error:
                    if failure is None: failure = error
    if failure is not None: raise failure
    left(deadline)


def tool_permission_map(files, directories):
    """Pure expected map of the launcher's existing worker-group permission formulas."""
    def parse(value):
        require(type(value) is str and re.fullmatch('[0-7]{4}', value), 'tool-mode')
        return int(value, 8)
    pinned_files = {}
    for name, entry in files.items():
        mode = parse(entry['mode'])
        pinned = (mode & 0o700) | 0o040
        if mode & 0o100: pinned |= 0o010
        pinned_files[name] = {'sha256': entry['sha256'], 'mode': format(pinned & ~0o022, '04o')}
    pinned_directories = {name: format(((parse(mode) & 0o700) | 0o050) & ~0o022, '04o')
                          for name, mode in directories.items()}
    return pinned_files, pinned_directories


def close_receipt(failed, closed, mounted, owned_exit):
    """Pure diagnostic projection; accepting these bytes cannot issue an owner."""
    require(all(type(value) is bool for value in (closed, mounted, owned_exit)), 'close-data')
    require(failed is None or failed in {'preparation-failed', 'registration-failed', 'watchdog-not-live',
            'worker-validation-failed', 'consumer-exit-unconfirmed', 'collection-or-restoration-failed',
            'execution-aborted', 'close-failed'}, 'close-category')
    allowed = failed is None and closed and not mounted and owned_exit
    return {'schema': 'issue779-private-product-coverage-close-v1',
        'account_cleanup_allowed': allowed, 'cleanup_complete': allowed,
        'workspace_quarantined': not allowed, 'mount_retained': mounted,
        'consumer_exit_confirmed': owned_exit, 'failure_category': failed}


class ProductCoverageOwner:
    """Private root issuer; constructor performs no instrumentation or dispatch.

    Only launcher-selected paths/accounts enter this owner. Its data maps cannot
    create admission, a registered application, a public writer or a Trust claim.
    Mount and account release require genuine joined coverage and restoration.
    """
    def __init__(self, tool, anchor, worker_uid, worker_gid, deadline, dotnet, taskhost, reports, worker_unit):
        require(platform.system() == 'Linux' and os.geteuid() == 0, 'owner-platform')
        require(type(worker_uid) is int and worker_uid > 0 and type(worker_gid) is int and worker_gid > 0, 'owner-identity')
        require(unit_kind(worker_unit, worker_unit) == 'worker', 'owner-unit')
        require(all(isinstance(path, Path) and path.is_absolute() for path in (tool, anchor, dotnet, taskhost, reports)), 'owner-path')
        require(dotnet.name == 'dotnet' and not any(reports == p or reports.is_relative_to(p) for p in (tool, anchor, taskhost)), 'owner-layout')
        self.tool, self.anchor, self.taskhost, self.reports = tool, anchor, taskhost, reports
        self.uid, self.gid, self.deadline, self.dotnet, self.worker = worker_uid, worker_gid, deadline, dotnet, worker_unit
        self.lock = threading.RLock()
        self.session = anchor / SESSION
        self.mount_fd = None
        self.mounted = self.dispatched = self.owned_exit = self.collected = self.closed = False
        self.failed = None
        self.watchdog = self.broker = None
        self.runtime_map = None
        self.units, self.captured = {worker_unit: 'worker'}, {}
        self.original = self.execution = self.root_files = self.hits = self.receipt = None
        self.worker_evidence = None

    def _check(self):
        left(self.deadline)
        require(self.failed is None and not self.closed, 'owner-failed-or-closed')
        if self.watchdog is not None: self.watchdog.live()

    def _fail(self, category):
        if self.failed is None: self.failed = category

    def prepare(self):
        with self.lock:
            try:
                self._check()
                require(self.watchdog is None and not self.mounted, 'owner-replay')
                anchor = open_directory(self.anchor, self.deadline, uid=self.uid, gid=self.gid, mode=0o700)
                os.close(anchor)
                tool = open_directory(self.tool, self.deadline, uid=0, mode=0o755)
                os.close(tool)
                host = open_directory(self.taskhost, self.deadline, uid=0, mode=0o700)
                os.close(host)
                report = open_directory(self.reports, self.deadline, uid=0, mode=0o700)
                try: require(not names(report, self.deadline), 'reports-not-fresh')
                finally: os.close(report)
                self.original = snapshot_tree(self.tool, self.deadline, gid=0)
                require(ELIGIBLE <= set(self.original[0]) and all(self.original[0][s]['mode'] == '0444' for s in ELIGIBLE), 'eligible-originals')
                # A separate FD and mount preserve artifact-root NO_XDEV semantics.
                self.session.mkdir(mode=0o700)
                self.mounted = True  # reserve cleanup ownership before mount I/O
                command(['/usr/bin/mount', '--types', 'tmpfs', '--options',
                         f'size=32M,nr_inodes=64,nosuid,nodev,noexec,uid=0,gid={self.gid},mode=1770', 'tmpfs', str(self.session)], self.deadline)
                self.mounted = True
                self.mount_fd = open_directory(self.session, self.deadline, uid=0, gid=self.gid, mode=0o1770)
                self.mount_identity = directory_identity(os.fstat(self.mount_fd))
                limits = os.fstatvfs(self.mount_fd)
                require(0 < limits.f_blocks * limits.f_frsize <= 32 << 20 and 0 < limits.f_files <= 64, 'mount-budget')
                validate_mount(kernel('/proc/self/mountinfo', self.deadline).decode('ascii'), self.session,
                               self.mount_identity[0], mount_identifier(self.mount_fd, self.deadline))
                environment = dict(ENV, TMPDIR=str(self.session), DOTNET_EnableDiagnostics='0', DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
                self.watchdog = Watchdog(self.worker, [str(self.dotnet), str(self.taskhost / 'OfficialTaskHost.dll'),
                    str(self.tool / 'ForgeTrust.AppSurface.Cli.dll'), str(self.reports / 'coverage')], environment, self.deadline)
                self.watchdog.start()
                ready = self.watchdog.request('start', 'prepared')
                require(set(ready) == {'stage', 'provider', 'pid'} and type(ready['pid']) is int and ready['pid'] > 0, 'provider-start-ack')
                prepared = ready['provider']
                require(prepared['temp'] == str(self.session) + '/' and Path(prepared['state']).parent == self.session, 'provider-temp')
                state = basename(Path(prepared['state']).name)
                selected = names(self.mount_fd, self.deadline, 16)
                backups = []
                for library in LIBRARIES:
                    matches = [n for n in selected if re.fullmatch(re.escape(library) + r'_[0-9a-fA-F-]{36}\.dll', n)]
                    require(len(matches) == 1, 'backup-selection')
                    dll = matches[0]
                    uuid.UUID(dll[len(library)+1:-4])
                    backups.extend((dll, dll[:-4] + '.pdb'))
                require(set(selected) == {state, *backups}, 'session-inventory')
                self.root_files = {}
                for name in selected:
                    seal_file(self.mount_fd, name, self.deadline, 0o600)
                    pinned = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=self.mount_fd)
                    try: os.fchown(pinned, 0, self.gid)
                    finally: os.close(pinned)
                    data, info = read_file(self.mount_fd, name, self.deadline, uid=0, gid=self.gid, mode=0o600)
                    self.root_files[name] = {'sha256': hashlib.sha256(data).hexdigest(), 'identity': info}
                self.hit_names = {name[:-4] for name in backups if name.endswith('.dll')}
                tool_fd = open_directory(self.tool, self.deadline, uid=0, gid=0, mode=0o755)
                try:
                    for name in sorted(ELIGIBLE): seal_file(tool_fd, name, self.deadline, 0o444)
                finally: os.close(tool_fd)
                self.execution = snapshot_tree(self.tool, self.deadline, gid=0)
                require(self.execution[1] == self.original[1] and set(self.execution[0]) == set(self.original[0]), 'execution-inventory')
                for name, original in self.original[0].items():
                    if name not in ELIGIBLE: require(self.execution[0][name] == original, 'noneligible-mutation')
                require(all(self.execution[0][name]['mode'] == '0444' for name in ELIGIBLE), 'execution-modes')
                require(all(self.execution[0][library+'.dll']['sha256'] != self.original[0][library+'.dll']['sha256']
                            for library in LIBRARIES), 'eligible-not-instrumented')
                self._check()
                return dict((name, item['sha256']) for name, item in self.execution[0].items())
            except BaseException:
                self._fail('preparation-failed')
                raise

    def bind_tool_pin(self):
        """Verify the actual launcher's permission transition without changing bytes."""
        with self.lock:
            try:
                self._check()
                require(self.execution is not None and self.runtime_map is None and not self.dispatched, 'tool-pin-order')
                expected = tool_permission_map(*self.execution)
                actual = snapshot_tree(self.tool, self.deadline, gid=self.gid)
                require(actual == expected, 'tool-pin-map')
                self.runtime_map = actual
                self._check()
            except BaseException:
                self._fail('preparation-failed')
                raise

    def register_owned_unit(self, unit):
        with self.lock:
            try:
                self._check()
                require(self.execution is not None and not self.owned_exit, 'registration-order')
                kind = unit_kind(unit, self.worker)
                require(len(self.units) < 130 or unit in self.units, 'unit-count')
                require(kind != 'application' or unit in self.units or 'application' not in self.units.values(), 'application-count')
                require(self.watchdog.request('register', 'registered', unit=unit) == {'stage': 'registered'}, 'registration-ack')
                self.units[unit] = kind
            except BaseException:
                self._fail('registration-failed')
                raise

    def live_abort(self):
        with self.lock:
            try:
                self._check()
                require(self.watchdog.request('ping', 'live') == {'stage': 'live'}, 'live-ack')
                return False
            except BaseException:
                self._fail('watchdog-not-live')
                return True

    def mark_worker_dispatch(self):
        with self.lock:
            self._check()
            require(not self.dispatched and self.execution is not None and self.runtime_map is not None, 'dispatch-order')
            require(self.watchdog.request('dispatch', 'dispatched') == {'stage': 'dispatched'}, 'dispatch-ack')
            self.dispatched = True

    def verify_live_worker(self, pid):
        with self.lock:
            try:
                self._check()
                require(type(pid) is int and pid > 0 and self.dispatched, 'worker-pid')
                initial = starttime(pid, self.deadline)
                values = {}
                for line in kernel(f'/proc/{pid}/status', self.deadline, 16384).decode('ascii').splitlines():
                    key, separator, value = line.partition(':')
                    if separator and key in ('Uid', 'Gid', 'Groups', 'CapEff', 'NoNewPrivs'):
                        require(key not in values, 'worker-status-duplicate')
                        values[key] = value.strip()
                require(set(values) == {'Uid', 'Gid', 'Groups', 'CapEff', 'NoNewPrivs'}, 'worker-status')
                require(values['Uid'].split() == [str(self.uid)]*4 and values['Gid'].split() == [str(self.gid)]*4
                        and values['Groups'].split() in ([], [str(self.gid)]) and int(values['CapEff'], 16) == 0
                        and values['NoNewPrivs'] == '1', 'worker-kernel-identity')
                require(kernel(f'/proc/{pid}/cgroup', self.deadline, 4096).decode().splitlines() == ['0::/system.slice/' + self.worker], 'worker-cgroup')
                # /proc/PID/root is the kernel-selected view; final component is no-follow.
                fd = os.open(f'/proc/{pid}/root' + str(self.session), os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
                try:
                    require(directory_identity(os.fstat(fd)) == self.mount_identity, 'worker-mount-identity')
                    mount = validate_mount(kernel(f'/proc/{pid}/mountinfo', self.deadline).decode('ascii'), self.session,
                        self.mount_identity[0], mount_identifier(fd, self.deadline))
                finally: os.close(fd)
                require(starttime(pid, self.deadline) == initial, 'worker-replaced')
                capture_group(self.worker, self.captured, self.deadline)
                self.worker_evidence = {'pid': pid, 'starttime': initial, 'uid4': [self.uid]*4, 'gid4': [self.gid]*4,
                    'cap_eff': 0, 'no_new_privs': 1, 'cgroup_exact': True, 'session_mount': mount}
                self._check()
            except BaseException:
                self._fail('worker-validation-failed')
                raise

    def bind_broker(self, broker):
        with self.lock:
            self._check()
            require(self.broker is None and self.worker_evidence is not None, 'broker-order')
            self.broker = broker

    def confirm_owned_exit(self):
        with self.lock:
            try:
                self._check()
                require(self.worker_evidence is not None and self.broker is not None and not self.owned_exit, 'exit-order')
                # Launcher calls only after its physical aggregate join. The actual
                # watchdog captured even transient groups while consumers were live.
                reply = self.watchdog.request('confirm', 'empty')
                require(set(reply) == {'stage', 'captured'} and type(reply['captured']) is dict
                        and set(reply['captured']) == set(self.units), 'consumer-capture-ack')
                for unit, observed in reply['captured'].items():
                    require(type(observed) is list and len(observed) == 5
                            and all(type(value) is int and value >= 0 for value in observed)
                            and observed[2:4] == [0, 0], 'consumer-capture-identity')
                    require(unit not in self.captured or self.captured[unit] == tuple(observed), 'consumer-capture-conflict')
                    self.captured[unit] = tuple(observed)
                units_empty(self.units, self.captured, self.deadline)
                self._check()
                self.owned_exit = True
            except BaseException:
                self._fail('consumer-exit-unconfirmed')
                raise

    def collect_and_restore(self):
        with self.lock:
            try:
                self._check()
                require(self.owned_exit and not self.collected, 'collect-order')
                units_empty(self.units, self.captured, self.deadline)
                require(directory_identity(os.fstat(self.mount_fd)) == self.mount_identity, 'mount-changed')
                current = set(names(self.mount_fd, self.deadline, 16))
                require(set(self.root_files) < current and current <= set(self.root_files) | self.hit_names, 'hit-inventory')
                self.hits = {}
                for name in sorted(current):
                    if name in self.root_files:
                        data, info = read_file(self.mount_fd, name, self.deadline, uid=0, gid=self.gid, mode=0o600)
                        require(hashlib.sha256(data).hexdigest() == self.root_files[name]['sha256']
                                and info == tuple(self.root_files[name]['identity']), 'root-state-changed')
                    else:
                        data, info = read_file(self.mount_fd, name, self.deadline, uid=self.uid, gid=self.gid)
                        require(len(data) >= 8 and not info[2] & 0o022, 'hit-shape')
                        self.hits[name] = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest(), 'uid': self.uid, 'gid': self.gid}
                reply = self.watchdog.request('collect', 'collected')
                require(set(reply) == {'stage', 'provider', 'stdout', 'stderr', 'exit_code', 'two_eof', 'group_absent'}
                        and reply['exit_code'] == 0 and reply['two_eof'] is True and reply['group_absent'] is True, 'collect-ack')
                require(type(reply['stdout']) is str and len(reply['stdout'].encode()) <= 64 << 10
                        and type(reply['stderr']) is str and len(reply['stderr'].encode()) <= 128 << 10, 'collect-log-bound')
                for key in ('stdout', 'stderr'):
                    self._save('taskhost-' + key + '.log', reply[key].encode(), (64 if key == 'stdout' else 128) << 10)
                reports = {}
                report_fd = open_directory(self.reports, self.deadline, uid=0, mode=0o700)
                try:
                    require(set(names(report_fd, self.deadline, 8)) == {*REPORTS, 'taskhost-stdout.log', 'taskhost-stderr.log'}, 'report-inventory')
                    for name, cap in zip(REPORTS, (2 << 20, 4 << 20)):
                        data, _ = read_file(report_fd, name, self.deadline, cap=cap)
                        if name.endswith('.xml'):
                            require(b'<!ENTITY' not in data and b'<!DOCTYPE' not in data, 'report-xml')
                            value = ET.fromstring(data)
                            require(value.tag == 'coverage' and int(value.get('lines-covered', '0')) > 0
                                    and int(value.get('branches-covered', '0')) > 0, 'report-empty')
                        else: require(type(json.loads(data)) is dict, 'report-json')
                        reports[name] = {'bytes': len(data), 'sha256': hashlib.sha256(data).hexdigest()}
                        seal_file(report_fd, name, self.deadline, 0o600)
                finally: os.close(report_fd)
                require(self.runtime_map is not None, 'runtime-map-missing')
                tool_fd = open_directory(self.tool, self.deadline, uid=0, gid=self.gid,
                                         mode=int(self.runtime_map[1][''], 8))
                os.close(tool_fd)
                restore_metadata(self.tool, self.original, self.deadline)
                require(snapshot_tree(self.tool, self.deadline, gid=0) == self.original, 'restore-tree')
                self.watchdog.disarm()
                self._check_after_disarm()
                self.collected = True
                receipt = {'schema': 'issue779-private-product-coverage-v1', 'reports': reports, 'genuine_hits': self.hits,
                    'worker': self.worker_evidence, 'physical_consumer_exit': True, 'provider_exit': 0,
                    'two_eof': True, 'watchdog_joined': True, 'originals_restored': True,
                    'eligible_libraries': list(LIBRARIES), 'claim': 'None', 'eligibility': 'None'}
                self._save('receipt.json', json.dumps(receipt, sort_keys=True, separators=(',', ':')).encode(), 256 << 10)
                return receipt
            except BaseException:
                self._fail('collection-or-restoration-failed')
                raise

    def _check_after_disarm(self):
        left(self.deadline)
        require(self.failed is None and self.watchdog.disarmed, 'final-owner-failed')
        units_empty(self.units, self.captured, self.deadline)
        left(self.deadline)

    def _save(self, name, data, cap):
        left(self.deadline)
        require(type(data) is bytes and len(data) <= cap, 'receipt-bound')
        root = open_directory(self.reports, self.deadline, uid=0, mode=0o700)
        fd = -1
        try:
            fd = os.open(basename(name), os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600, dir_fd=root)
            os.fchmod(fd, 0o600)
            view = memoryview(data)
            while view:
                left(self.deadline)
                written = os.write(fd, view)
                require(written > 0, 'receipt-write')
                view = view[written:]
        finally:
            if fd >= 0: os.close(fd)
            os.close(root)
        left(self.deadline)

    def abort_after_owned_exit(self):
        """Seal failure. Never authorize release or send a restoration command."""
        with self.lock:
            self._fail('execution-aborted')
            # Closing the credentialed channel makes the actual watchdog own the
            # stop/KILL/quarantine path; stdin is retained by that same process.
            if self.watchdog is not None and self.watchdog.channel is not None:
                try:
                    self.watchdog.live()
                    send(self.watchdog.channel, {'op': 'abort'}, self.deadline)
                    require(receive(self.watchdog.channel, self.deadline, self.watchdog.pid, 0)
                            == {'stage': 'aborted', 'provider_reaped': True}, 'abort-ack')
                    while True:
                        left(self.deadline)
                        pid, status = os.waitpid(self.watchdog.pid, os.WNOHANG)
                        if pid:
                            require(os.WIFEXITED(status) and os.WEXITSTATUS(status) == 1, 'abort-exit')
                            break
                        with selectors.DefaultSelector() as selected:
                            selected.select(min(.01, left(self.deadline)))
                finally:
                    self.watchdog.channel.close()

    def close(self):
        with self.lock:
            if self.receipt is not None: return dict(self.receipt)
            try:
                left(self.deadline)
                require(self.failed is None and self.collected and self.watchdog is not None and self.watchdog.disarmed, 'close-order')
                units_empty(self.units, self.captured, self.deadline)
                if self.mount_fd is not None:
                    os.close(self.mount_fd)
                    self.mount_fd = None
                require(self.mounted, 'mount-close-order')
                command(['/usr/bin/umount', str(self.session)], self.deadline)
                self.mounted = False
                self.session.rmdir()
                left(self.deadline)
                self.closed = True
            except BaseException:
                self._fail('close-failed')
                self.closed = True
            self.receipt = close_receipt(self.failed, self.closed, self.mounted, self.owned_exit)
            return dict(self.receipt)
