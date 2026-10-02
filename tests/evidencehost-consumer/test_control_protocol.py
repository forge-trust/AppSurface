#!/usr/bin/env python3
"""Drive the real C# Linux control client against a test-only Unix-socket broker.

This is a protocol-mechanism check only. It needs root to create the fake broker and
setpriv to launch the worker under a separate UID. It does not exercise systemd,
cgroups, a production callback, or EvidenceHost admission.
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import json
import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

SO_PEERCRED = getattr(socket, "SO_PEERCRED", 17)
MAX_REQUEST = 64 * 1024
MAX_RESPONSE = 3 * 1024 * 1024
CHUNK_BYTES = 128 * 1024
WORKER_UID = 65534
WORKER_GID = 65534
SUBJECT_UID = 65533
SUBJECT_GID = 65533


class HarnessFailure(RuntimeError):
    """A stable, value-free harness failure."""


def read_request(connection: socket.socket) -> dict:
    connection.settimeout(8)
    raw = bytearray()
    while len(raw) <= MAX_REQUEST:
        part = connection.recv(min(8192, MAX_REQUEST + 1 - len(raw)))
        if not part:
            break
        raw.extend(part)
        if b"\n" in raw:
            break
    if not raw:
        raise HarnessFailure("client-sent-no-request")
    if len(raw) > MAX_REQUEST or not raw.endswith(b"\n"):
        raise HarnessFailure("client-request-limit-or-framing")
    try:
        value = json.loads(raw[:-1])
    except (UnicodeDecodeError, json.JSONDecodeError):
        raise HarnessFailure("client-request-json") from None
    if not isinstance(value, dict):
        raise HarnessFailure("client-request-shape")
    return value


def write_response(connection: socket.socket, value: dict) -> None:
    connection.sendall(json.dumps(value, separators=(",", ":")).encode() + b"\n")


def peer_credentials(connection: socket.socket) -> tuple[int, int, int]:
    raw = connection.getsockopt(socket.SOL_SOCKET, SO_PEERCRED, struct.calcsize("3i"))
    if len(raw) != struct.calcsize("3i"):
        raise HarnessFailure("peer-credentials-shape")
    return struct.unpack("3i", raw)


def make_descriptor(peer: tuple[int, int, int], socket_path: Path, dotnet_path: str,
                    scenario: str, broker_pid: int) -> dict:
    worker_pid, worker_uid, worker_gid = peer
    deadline = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
    descriptor = {
        "schema": "evidence-worker-linux-v1",
        "run_id": "control-protocol-fixture",
        "broker_pid": broker_pid,
        "worker_pid": worker_pid,
        "worker_uid": worker_uid,
        "worker_gid": worker_gid,
        "subject_uid": SUBJECT_UID,
        "subject_gid": SUBJECT_GID,
        "unit": "evidence-control-protocol-fixture.service",
        "cgroup": "/system.slice/evidence-control-protocol-fixture.service",
        "job_deadline_utc": deadline.isoformat(timespec="seconds").replace("+00:00", "Z"),
        "tool_root": "/opt/evidence-control-protocol/tool",
        "subject_root": "/srv/evidence-control-protocol/subject",
        "output_parent": "/var/tmp/evidence-control-protocol/output",
        "output_slot": "fixture-run",
        "dotnet_path": dotnet_path,
        "test_output_root": "/var/tmp/evidence-control-protocol/test-output",
        "policy_file": "/opt/evidence-control-protocol/tool/policy.json",
        "mode": "observation",
        "socket_path": str(socket_path),
        "descriptor_path": "/run/evidence-control-protocol/worker-control.json",
        "entry_sha256": "a" * 64,
        "base_revision": "fixture-base",
        "subject_revision": "fixture-subject",
        "workflow_identity": "fixture-workflow",
        "provider": "github-actions",
        "platform": "linux-x64",
        "proof_digest": "",
        "policy_sha256": "b" * 64,
        "output_parent_identity": {
            "device_major": 0,
            "device_minor": 0,
            "inode": 1,
            "uid": 0,
            "gid": 0,
        },
        "observation_profile_ids": [],
        "observation_producer_ids": [],
        "paths": [],
        "admission_seconds": 1,
        "start_seconds": 1,
        "collection_seconds": 1,
        "cleanup_seconds": 2,
        "stopping_seconds": 1,
        "diff_file": None,
        "solution": None,
    }
    if scenario == "worker-pid-mismatch":
        descriptor["worker_pid"] += 1
    elif scenario == "worker-uid-mismatch":
        descriptor["worker_uid"] = worker_uid + 1
    elif scenario == "worker-gid-mismatch":
        descriptor["worker_gid"] = worker_gid + 1
    elif scenario == "expired-wall-deadline":
        descriptor["job_deadline_utc"] = "2000-01-01T00:00:00Z"
    return descriptor


def serve(socket_path: Path, scenario: str, dotnet_path: str) -> int:
    listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    ready_path = Path(str(socket_path) + ".ready")
    ready_seen_path = Path(str(socket_path) + ".ready-seen")
    try:
        listener.bind(str(socket_path))
        os.chmod(socket_path, 0o666)
        listener.listen(8)
        listener.settimeout(8)
        ready_path.touch(mode=0o644)

        if scenario == "broker-no-response":
            connection, _ = listener.accept()
            with connection:
                connection.settimeout(5)
                request = read_request(connection)
                if request != {"op": "ready"}:
                    raise HarnessFailure("unexpected-no-response-operation")
                ready_seen_path.touch(mode=0o644)
                while connection.recv(1024):
                    pass
            return 0

        connection, _ = listener.accept()
        peer = peer_credentials(connection)
        if scenario == "wrong-root-peer":
            # ConnectAsync checks SO_PEERCRED before sending its ready request.
            with connection:
                connection.settimeout(5)
                if connection.recv(1024):
                    raise HarnessFailure("non-root-peer-received-request")
            return 0
        if scenario == "broker-second":
            # RequestAsync checks the replacement PID before sending an operation.
            with connection:
                connection.settimeout(5)
                if connection.recv(1024):
                    raise HarnessFailure("replacement-broker-received-request")
            return 0

        with connection:
            request = read_request(connection)
            if request != {"op": "ready"}:
                raise HarnessFailure("unexpected-first-operation")
            descriptor = make_descriptor(peer, socket_path, dotnet_path, scenario, os.getpid())

            if scenario == "malformed-json":
                connection.sendall(b"{bad-json\n")
                return 0
            if scenario == "oversized-response":
                try:
                    connection.sendall(b'{"ok":true,"padding":"' + b"x" * MAX_RESPONSE + b'"}\n')
                except OSError:
                    pass
                return 0
            if scenario == "missing-job-remaining":
                write_response(connection, {"ok": True, "descriptor": descriptor})
                return 0
            if scenario == "zero-job-remaining":
                write_response(connection, {"ok": True, "job_remaining_seconds": 0, "descriptor": descriptor})
                return 0
            if scenario == "excessive-job-remaining":
                write_response(connection, {"ok": True, "job_remaining_seconds": 3601, "descriptor": descriptor})
                return 0
            if scenario == "rejected-response":
                write_response(connection, {"ok": False})
                return 0

            write_response(connection, {"ok": True, "job_remaining_seconds": 600.0,
                                        "descriptor": descriptor})

        if scenario == "broker-first":
            # Keep this PID alive while allowing a second root process to claim the path.
            listener.close()
            try:
                socket_path.unlink()
            except FileNotFoundError:
                pass
            try:
                ready_path.unlink()
            except FileNotFoundError:
                pass
            sys.stdin.readline()
            return 0

        if scenario == "artifact-count-limit":
            connection, _ = listener.accept()
            with connection:
                request = read_request(connection)
                if request.get("op") != "artifacts":
                    raise HarnessFailure("artifact-list-operation-mismatch")
                items = [{"path": f"reports/{index}.json", "length_bytes": 0}
                         for index in range(65)]
                write_response(connection, {"ok": True, "artifacts": items})
            return 0

        if scenario in ("artifact-multichunk", "artifact-bad-end"):
            payload = b"p" * (CHUNK_BYTES + 37)
            connection, _ = listener.accept()
            with connection:
                request = read_request(connection)
                if request.get("op") != "artifacts":
                    raise HarnessFailure("artifact-list-operation-mismatch")
                write_response(connection, {"ok": True, "artifacts": [
                    {"path": "reports/result.bin", "length_bytes": len(payload)}
                ]})

            expected_offset = 0
            while expected_offset < len(payload):
                connection, _ = listener.accept()
                with connection:
                    request = read_request(connection)
                    if request.get("op") != "artifact" or request.get("relative_path") != "reports/result.bin":
                        raise HarnessFailure("artifact-read-operation-mismatch")
                    offset = request.get("offset")
                    if offset != expected_offset:
                        raise HarnessFailure("artifact-read-offset-mismatch")
                    chunk = payload[offset:offset + CHUNK_BYTES]
                    expected_offset += len(chunk)
                    is_end = expected_offset == len(payload)
                    if scenario == "artifact-bad-end":
                        is_end = True
                    write_response(connection, {
                        "ok": True,
                        "bytes_base64": base64.b64encode(chunk).decode("ascii"),
                        "end": is_end,
                    })
                    if scenario == "artifact-bad-end":
                        return 0
            return 0

        return 0
    except (OSError, HarnessFailure, json.JSONDecodeError) as failure:
        reason = str(failure) if isinstance(failure, HarnessFailure) else type(failure).__name__
        print(f"BROKER-FIXTURE-ERROR:{reason}", file=sys.stderr, flush=True)
        return 70
    finally:
        listener.close()
        try:
            socket_path.unlink()
        except FileNotFoundError:
            pass
        try:
            ready_path.unlink()
        except FileNotFoundError:
            pass
        try:
            ready_seen_path.unlink()
        except FileNotFoundError:
            pass


def child_environment(dotnet_path: str, home: Path) -> dict[str, str]:
    return {
        "PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
        "HOME": str(home),
        "LANG": "C.UTF-8",
        "DOTNET_ROOT": str(Path(dotnet_path).parent),
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
        "DOTNET_NOLOGO": "1",
    }


def setpriv_command(setpriv_path: str, uid: int, gid: int, command: list[str]) -> list[str]:
    return [setpriv_path, "--no-new-privs", f"--reuid={uid}", f"--regid={gid}",
            "--clear-groups", "--", *command]


def start_broker(script: Path, socket_path: Path, scenario: str, dotnet_path: str,
                 setpriv_path: str, broker_uid: int, broker_gid: int,
                 directory: Path, home: Path) -> subprocess.Popen:
    os.chown(directory, broker_uid, broker_gid)
    os.chmod(directory, 0o755)
    ready_path = Path(str(socket_path) + ".ready")
    command = [sys.executable, "-B", str(script), "--serve", str(socket_path), scenario, dotnet_path]
    if broker_uid != 0:
        command = setpriv_command(setpriv_path, broker_uid, broker_gid, command)
    process = subprocess.Popen(command, cwd=home, env=child_environment(dotnet_path, home),
                               stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                               stderr=subprocess.PIPE, text=True)
    deadline = time.monotonic() + 8
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise HarnessFailure("broker-start-failed")
        if ready_path.is_file():
            return process
        time.sleep(0.02)
    process.kill()
    process.wait()
    raise HarnessFailure("broker-listener-timeout")


def start_worker(setpriv_path: str, dotnet_path: str, worker_dll: Path, socket_path: Path,
                 mode: str, home: Path) -> subprocess.Popen:
    command = setpriv_command(setpriv_path, WORKER_UID, WORKER_GID,
                              [dotnet_path, str(worker_dll), str(socket_path), mode])
    return subprocess.Popen(command, cwd=home, env=child_environment(dotnet_path, home),
                            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, text=True, bufsize=1)


def stop_child(process: subprocess.Popen | None, *, release: bool = False) -> None:
    if process is None or process.poll() is not None:
        return
    if release and process.stdin is not None:
        try:
            process.stdin.write("release\n")
            process.stdin.flush()
        except OSError:
            pass
    try:
        process.wait(timeout=2)
    except subprocess.TimeoutExpired:
        process.send_signal(signal.SIGTERM)
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def parse_result(stdout: str) -> dict:
    lines = [line for line in stdout.splitlines() if line]
    if not lines:
        raise HarnessFailure("worker-result-missing")
    try:
        value = json.loads(lines[-1])
    except json.JSONDecodeError:
        raise HarnessFailure("worker-result-invalid") from None
    if not isinstance(value, dict):
        raise HarnessFailure("worker-result-shape")
    return value


def run_case(script: Path, root: Path, home: Path, worker_dll: Path, dotnet_path: str,
             setpriv_path: str, name: str, broker_scenario: str, expected: dict,
             expected_exit: int, *, mode: str = "connect", broker_uid: int = 0,
             broker_gid: int = 0) -> None:
    directory = root / name
    directory.mkdir(mode=0o755)
    os.chmod(directory, 0o755)
    socket_path = directory / "c.sock"
    broker = start_broker(script, socket_path, broker_scenario, dotnet_path, setpriv_path,
                          broker_uid, broker_gid, directory, home)
    worker = None
    try:
        worker = start_worker(setpriv_path, dotnet_path, worker_dll, socket_path, mode, home)
        stdout, _stderr = worker.communicate(timeout=15)
        result = parse_result(stdout)
        if worker.returncode != expected_exit or result != expected:
            raise HarnessFailure(f"case-failed:{name}:exit={worker.returncode}:result={result}")
        broker.wait(timeout=10)
        if broker.returncode != 0:
            _unused_stdout, broker_stderr = broker.communicate()
            details = broker_stderr.strip().splitlines()
            reason = details[-1] if details else "no-diagnostic"
            raise HarnessFailure(f"broker-failed:{name}:exit={broker.returncode}:{reason}")
    except subprocess.TimeoutExpired:
        if worker is not None:
            worker.kill()
            worker.wait()
        raise HarnessFailure(f"case-timeout:{name}") from None
    finally:
        stop_child(worker)
        stop_child(broker, release=True)


def read_worker_line(worker: subprocess.Popen, timeout: float) -> str:
    import select

    if worker.stdout is None or not select.select([worker.stdout], [], [], timeout)[0]:
        raise HarnessFailure("worker-handshake-timeout")
    line = worker.stdout.readline()
    if not line:
        raise HarnessFailure("worker-handshake-missing")
    return line


def run_broker_replacement(script: Path, root: Path, home: Path, worker_dll: Path,
                           dotnet_path: str, setpriv_path: str) -> None:
    directory = root / "broker-replacement"
    directory.mkdir(mode=0o755)
    os.chmod(directory, 0o755)
    socket_path = directory / "c.sock"
    first = start_broker(script, socket_path, "broker-first", dotnet_path, setpriv_path,
                         0, 0, directory, home)
    worker = None
    second = None
    try:
        worker = start_worker(setpriv_path, dotnet_path, worker_dll, socket_path, "hold", home)
        connected = parse_result(read_worker_line(worker, 15))
        if connected != {"status": "connected", "armed": True}:
            raise HarnessFailure("case-failed:broker-first-handshake")
        unlink_deadline = time.monotonic() + 5
        while socket_path.exists() and time.monotonic() < unlink_deadline:
            time.sleep(0.01)
        if socket_path.exists():
            raise HarnessFailure("broker-first-socket-not-released")

        second = start_broker(script, socket_path, "broker-second", dotnet_path, setpriv_path,
                              0, 0, directory, home)
        if first.pid == second.pid:
            raise HarnessFailure("broker-pid-not-distinct")
        assert worker.stdin is not None
        worker.stdin.write("stop\n")
        worker.stdin.flush()
        stdout, _stderr = worker.communicate(timeout=15)
        result = parse_result(stdout)
        if worker.returncode != 20 or result != {"status": "rejected", "code": "ASEVD402"}:
            raise HarnessFailure("case-failed:broker-pid-replacement")
        second.wait(timeout=10)
        if second.returncode != 0:
            raise HarnessFailure("broker-failed:broker-pid-replacement")
        first.stdin.write("release\n")
        first.stdin.flush()
        first.wait(timeout=5)
    except subprocess.TimeoutExpired:
        if worker is not None:
            worker.kill()
            worker.wait()
        raise HarnessFailure("case-timeout:broker-pid-replacement") from None
    finally:
        stop_child(worker)
        stop_child(second, release=True)
        stop_child(first, release=True)


def stage_worker(source: Path, staging: Path) -> Path:
    if not source.is_file() or source.name != "EvidenceHost.ControlProtocolWorker.dll":
        raise HarnessFailure("built-control-protocol-worker-required")
    target_directory = staging / "worker"
    target_directory.mkdir(mode=0o755)
    os.chmod(target_directory, 0o755)
    for item in source.parent.iterdir():
        if item.is_file():
            target = target_directory / item.name
            shutil.copyfile(item, target)
            os.chmod(target, 0o644)
    target = target_directory / source.name
    if not target.is_file():
        raise HarnessFailure("worker-staging-failed")
    return target


def run_matrix(worker_source: Path) -> None:
    if sys.platform != "linux":
        raise HarnessFailure("requires-linux")
    if os.geteuid() != 0:
        raise HarnessFailure("requires-root-broker")
    if WORKER_UID == 0 or WORKER_UID == SUBJECT_UID or WORKER_GID == SUBJECT_GID:
        raise HarnessFailure("fixture-identities-not-distinct")

    setpriv_path = shutil.which("setpriv")
    dotnet_path = os.environ.get("DOTNET_HOST_PATH") or shutil.which("dotnet")
    if not setpriv_path or not dotnet_path:
        raise HarnessFailure("requires-setpriv-and-dotnet")
    dotnet_path = str(Path(dotnet_path).resolve(strict=True))

    with tempfile.TemporaryDirectory(prefix="evctl-", dir="/tmp") as temporary:
        root = Path(temporary)
        os.chmod(root, 0o755)
        print("SETUP temporary root", flush=True)
        home = root / "home"
        home.mkdir(mode=0o755)
        os.chown(home, WORKER_UID, WORKER_GID)
        os.chmod(home, 0o755)
        script = root / "test_control_protocol.py"
        shutil.copyfile(Path(__file__).resolve(), script)
        os.chmod(script, 0o644)
        print("SETUP worker files", flush=True)
        worker_dll = stage_worker(worker_source.resolve(strict=True), root)

        cases = [
            ("valid-handshake", "valid", {"status": "connected", "armed": True}, 0, "connect", 0, 0),
            ("wrong-root-peer", "wrong-root-peer", {"status": "rejected", "code": "ASEVD402"}, 20,
             "connect", SUBJECT_UID, SUBJECT_GID),
            ("worker-pid-mismatch", "worker-pid-mismatch", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("worker-uid-mismatch", "worker-uid-mismatch", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("worker-gid-mismatch", "worker-gid-mismatch", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("broker-no-response", "broker-no-response", {"status": "cancelled"}, 22, "cancel-connect", 0, 0),
            ("missing-job-remaining", "missing-job-remaining", {"status": "protocol-error"}, 21, "connect", 0, 0),
            ("zero-job-remaining", "zero-job-remaining", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("excessive-job-remaining", "excessive-job-remaining", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("expired-wall-deadline", "expired-wall-deadline", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("malformed-json", "malformed-json", {"status": "protocol-error"}, 21, "connect", 0, 0),
            ("oversized-response", "oversized-response", {"status": "rejected", "code": "ASEVD420"}, 20, "connect", 0, 0),
            ("broker-rejected-response", "rejected-response", {"status": "rejected", "code": "ASEVD420"}, 20, "connect", 0, 0),
            ("artifact-count-limit", "artifact-count-limit", {"status": "rejected", "code": "ASEVD420"}, 20, "collect", 0, 0),
            ("artifact-multichunk", "artifact-multichunk", {"status": "collected", "count": 1, "bytes": CHUNK_BYTES + 37}, 0, "collect", 0, 0),
            ("artifact-bad-end", "artifact-bad-end", {"status": "rejected", "code": "ASEVD420"}, 20, "collect", 0, 0),
        ]
        for name, server_scenario, expected, exit_code, mode, broker_uid, broker_gid in cases:
            print(f"RUN {name}", flush=True)
            run_case(script, root, home, worker_dll, dotnet_path, setpriv_path, name,
                     server_scenario, expected, exit_code, mode=mode,
                     broker_uid=broker_uid, broker_gid=broker_gid)
            print(f"PASS {name}", flush=True)

        print("RUN broker-pid-replacement", flush=True)
        run_broker_replacement(script, root, home, worker_dll, dotnet_path, setpriv_path)
        print("PASS broker-pid-replacement", flush=True)

    print("PASS all control protocol mechanism cases; admission: none", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker-dll", type=Path,
                        help="prebuilt EvidenceHost.ControlProtocolWorker.dll")
    parser.add_argument("--serve", nargs=3, metavar=("SOCKET", "SCENARIO", "DOTNET"),
                        help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.serve:
        return serve(Path(args.serve[0]), args.serve[1], args.serve[2])
    if args.worker_dll is None:
        parser.error("--worker-dll is required")
    try:
        run_matrix(args.worker_dll)
    except HarnessFailure as failure:
        print(f"FAIL {failure}", file=sys.stderr)
        return 1
    except (OSError, subprocess.SubprocessError) as failure:
        print(f"FAIL harness-runtime-error:{type(failure).__name__}", file=sys.stderr)
        return 1
    except Exception as failure:
        print(f"FAIL unexpected-harness-error:{type(failure).__name__}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
