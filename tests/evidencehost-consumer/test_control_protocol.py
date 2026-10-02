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
WORKER_GID = 65532
SUBJECT_UID = 65533
SUBJECT_GID = 65531
APPLICATION_UID = 65530
APPLICATION_GID = 65529
RESULTS_GID = 65528
RESOURCE_ACCESS_GID = 65527
APPLICATION_ID = "protocol-app"
RESOURCE_ID = "native-http"
ENTRY_DIGEST = "d" * 64
LEASE_ID = "a" * 32
APPLICATION_CGROUP = f"/system.slice/issue779-app-{LEASE_ID}.service"
# These acknowledgements and application identities are synthetic protocol data.
# Only broker/worker credentials and socket framing are real in the native matrix.
APPLICATION_CASES = (
    ("application-valid", "acknowledged", True, True, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-duplicate-start", "rejected", True, False, ("ready", "application-start", "stop", "wait")),
    ("application-wait-before-start", "rejected", False, False, ("ready", "stop", "wait")),
    ("application-wrong-lease", "rejected", True, False, ("ready", "application-start", "stop", "wait")),
    ("application-wrong-resource", "rejected", True, False, ("ready", "application-start", "stop", "wait")),
    ("application-closed-start", "rejected", False, False, ("ready", "stop", "wait")),
    ("application-closed-wait", "rejected", True, False, ("ready", "application-start", "stop", "wait")),
    ("application-wrong-id", "rejected", False, False, ("ready", "stop", "wait")),
    ("application-wrong-digest", "rejected", False, False, ("ready", "stop", "wait")),
    ("application-start-malformed", "rejected", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-start-uid", "rejected", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-start-lease", "rejected", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-start-unowned", "rejected", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-start-cgroup", "rejected", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-wait-malformed", "rejected", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-wait-uid", "rejected", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-wait-kernel", "rejected", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-wait-unhealthy", "rejected", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-wait-overflow", "rejected", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
    ("application-cancel-start", "cancelled", False, False, ("ready", "application-start", "stop", "wait")),
    ("application-cancel-wait", "cancelled", True, False, ("ready", "application-start", "resource-wait", "stop", "wait")),
)


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
    control_root = socket_path.parent.parent
    deadline = dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)
    descriptor = {
        "schema": "evidence-worker-linux-v1",
        "run_id": f"control-{control_root.parent.name}-{control_root.name}/1",
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
        "descriptor_path": str(control_root / "worker-control.json"),
        "entry_sha256": "a" * 64,
        "base_revision": "b" * 40,
        "subject_revision": "c" * 40,
        "workflow_identity": "fixture-workflow",
        "provider": "github-actions",
        "platform": "linux-x64",
        "proof_digest": "",
        "policy_sha256": "b" * 64,
        "output_parent_identity": {
            "device_major": 0,
            "device_minor": 0,
            "inode": 1,
            "uid": worker_uid,
            "gid": worker_gid,
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
        "diff_sha256": None,
        "solution": None,
    }
    if scenario.startswith("application-"):
        descriptor["schema"] = "evidence-worker-linux-v2"
        descriptor["application"] = make_application_descriptor()
    if scenario == "worker-pid-mismatch":
        descriptor["worker_pid"] += 1
    elif scenario == "worker-uid-mismatch":
        descriptor["worker_uid"] = worker_uid + 1
        descriptor["output_parent_identity"]["uid"] = worker_uid + 1
    elif scenario == "worker-gid-mismatch":
        descriptor["worker_gid"] = worker_gid + 1
        descriptor["output_parent_identity"]["gid"] = worker_gid + 1
    elif scenario == "expired-wall-deadline":
        descriptor["job_deadline_utc"] = "2000-01-01T00:00:00Z"
    return descriptor


def make_application_descriptor() -> dict:
    """Closed synthetic v2 metadata, never executable paths or real application inspection."""
    roles = (
        ("app.dll", "apphost"), ("app.runtimeconfig.json", "apphost_runtime_configuration"),
        ("resource.dll", "resource"), ("resource.runtimeconfig.json", "resource_runtime_configuration"),
        ("dcp/dcp", "dcp"), ("dcp/ext/fixture-ext", "dcp_extension"),
        ("dependencies/fixture.dll", "dependency"), ("input/request.json", "declared_input"),
        ("app.deps.json", "dependency_manifest"),
    )
    return {
        "application_id": APPLICATION_ID, "application_version": "1.0.0", "build_id": "protocol-fixture-v2",
        "catalogue_digest": "c" * 64, "entry_digest": ENTRY_DIGEST, "aspire_sdk_version": "13.4.4",
        "resources": [{"id": RESOURCE_ID, "readiness": "aspire_health", "deadline_seconds": 120, "requires": []}],
        "producers": [{"id": "coverage", "kind": "coverage", "version": "1.0.0", "required_resources": [RESOURCE_ID],
                       "assertion_ids": ["appsurface/coverage/behavioral-patch@1"],
                       "artifact_slots": [{"logical_name": "coverage-report", "relative_root": "coverage",
                                           "media_type": "application/xml", "required": True, "maximum_bytes": 1024}],
                       "timeout_seconds": 120,
                       "coverage_gate": {"min_line_percent": 95, "min_branch_percent": 85,
                                         "min_patch_line_percent": None, "min_patch_branch_percent": None,
                                         "patch_line_mode": "measurable", "tolerance_percent": 0}}],
        "bundle_files": [{"relative_path": path, "role": role, "length_bytes": 1, "sha256": "e" * 64,
                          "mode": 0o555 if role in ("dcp", "dcp_extension") else 0o444} for path, role in roles],
        "capabilities": {"read_only_inputs": ["input/request.json"], "scratch_bytes": 1024 ** 3,
                         "memory_bytes": 1024 ** 3, "maximum_tasks": 64, "maximum_output_bytes": 1024 ** 2,
                         "start_seconds": 120, "stopping_seconds": 30},
        "application_uid": APPLICATION_UID, "application_gid": APPLICATION_GID,
        "results_gid": RESULTS_GID, "resource_access_gid": RESOURCE_ACCESS_GID,
    }


def application_start_response(scenario: str) -> dict:
    response = {"ok": True, "lease_id": LEASE_ID, "apphost_pid": 12345,
                "application_uid": APPLICATION_UID, "application_gid": APPLICATION_GID,
                "cgroup": APPLICATION_CGROUP, "owned": True}
    if scenario == "application-start-uid":
        response["application_uid"] = WORKER_UID
    elif scenario == "application-start-lease":
        response["lease_id"] = "INVALID"
    elif scenario == "application-start-unowned":
        response["owned"] = False
    elif scenario == "application-start-cgroup":
        response["cgroup"] = "/system.slice/another.service"
    return response


def application_wait_response(scenario: str) -> dict:
    response = {"ok": True, "lease_id": LEASE_ID, "resource_id": RESOURCE_ID,
                "application_uid": APPLICATION_UID, "cgroup": APPLICATION_CGROUP,
                "kernel_peer_checked": True, "http_status": 200, "healthy": True, "received_bytes": 4096}
    if scenario == "application-wait-uid":
        response["application_uid"] = WORKER_UID
    elif scenario == "application-wait-kernel":
        response["kernel_peer_checked"] = False
    elif scenario == "application-wait-unhealthy":
        response["healthy"] = False
    elif scenario == "application-wait-overflow":
        response["received_bytes"] = 4097
    return response


def serve_application_operations(listener: socket.socket, socket_path: Path, scenario: str,
                                 pinned_peer: tuple[int, int, int]) -> None:
    """Observe exact real wire operations; only the acknowledgement payloads are synthetic."""
    operations = ["ready"]
    blocked_eof = False
    while True:
        connection, _ = listener.accept()
        with connection:
            if peer_credentials(connection) != pinned_peer:
                raise HarnessFailure("application-operation-peer-changed")
            request = read_request(connection)
            op = request.get("op")
            operations.append(op)
            if len(operations) > 6:
                raise HarnessFailure("application-operation-count-limit")
            if op == "application-start":
                if request != {"op": op, "application_id": APPLICATION_ID, "entry_digest": ENTRY_DIGEST}:
                    raise HarnessFailure("application-start-request-mismatch")
            elif op == "resource-wait":
                if request != {"op": op, "lease_id": LEASE_ID, "resource_id": RESOURCE_ID}:
                    raise HarnessFailure("application-wait-request-mismatch")
            elif request != {"op": op} or op not in ("stop", "wait"):
                raise HarnessFailure("application-unknown-operation")

            blocked = (scenario == "application-cancel-start" and op == "application-start"
                       or scenario == "application-cancel-wait" and op == "resource-wait")
            if blocked:
                Path(str(socket_path) + ".operation-seen").touch(mode=0o644)
                connection.settimeout(5)
                if connection.recv(1):
                    raise HarnessFailure("blocked-operation-received-extra-bytes")
                blocked_eof = True
            elif ((scenario == "application-start-malformed" and op == "application-start")
                  or (scenario == "application-wait-malformed" and op == "resource-wait")):
                connection.sendall(b'{"ok":true,"secret":"protocol-canary",bad}\n')
            elif op == "application-start":
                write_response(connection, application_start_response(scenario))
            elif op == "resource-wait":
                write_response(connection, application_wait_response(scenario))
            else:
                write_response(connection, {"ok": True, **({"owned_exit": True} if op == "wait" else {})})
        if op == "wait":
            receipt = {"operations": operations, "blocked_operation_eof": blocked_eof,
                       "synthetic_application_acknowledgements": True}
            fd = os.open(socket_path.parent.parent / "protocol-operations.json",
                         os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "w") as stream:
                os.fchmod(stream.fileno(), 0o600)
                json.dump(receipt, stream)
            return


def prepare_control_root(directory: Path, broker_uid: int) -> Path:
    """Create the fixture grammar; the nonroot-peer control needs owner-independent search."""
    if len(os.fsencode(directory / "broker" / "control.sock")) > 100:
        raise HarnessFailure("broker-control-path-limit")
    directory.mkdir(mode=0o710)
    os.chown(directory, 0, WORKER_GID, follow_symlinks=False)
    os.chmod(directory, 0o710 if broker_uid == 0 else 0o711)
    broker_directory = directory / "broker"
    broker_directory.mkdir(mode=0o710)
    return broker_directory / "control.sock"


def write_descriptor_snapshot(socket_path: Path, descriptor: dict, worker_gid: int) -> None:
    """Retain one fresh root/worker-readable snapshot after this broker observes its peer."""
    descriptor_path = socket_path.parent.parent / "worker-control.json"
    fd = os.open(descriptor_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o440)
    with os.fdopen(fd, "wb") as stream:
        os.fchown(stream.fileno(), 0, worker_gid)
        os.fchmod(stream.fileno(), 0o440)
        stream.write(json.dumps(descriptor, separators=(",", ":")).encode() + b"\n")


def serve(socket_path: Path, scenario: str, dotnet_path: str) -> int:
    listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    ready_path = Path(str(socket_path) + ".ready")
    ready_seen_path = Path(str(socket_path) + ".ready-seen")
    try:
        listener.bind(str(socket_path))
        if os.geteuid() == 0:
            os.chown(socket_path, 0, WORKER_GID, follow_symlinks=False)
            os.chmod(socket_path, 0o660)
        else:
            # This intentionally nonroot broker cannot assign the worker's group.
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
            write_descriptor_snapshot(socket_path, descriptor, peer[2])

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

        if scenario == "application-replacement-first-wait":
            connection, _ = listener.accept()
            with connection:
                if peer_credentials(connection) != peer:
                    raise HarnessFailure("application-operation-peer-changed")
                request = read_request(connection)
                if request != {"op": "application-start", "application_id": APPLICATION_ID, "entry_digest": ENTRY_DIGEST}:
                    raise HarnessFailure("application-start-request-mismatch")
                write_response(connection, application_start_response(scenario))

        if scenario in ("broker-first", "application-replacement-first-start", "application-replacement-first-wait"):
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

        if scenario.startswith("application-"):
            serve_application_operations(listener, socket_path, scenario, peer)
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
        try:
            Path(str(socket_path) + ".operation-seen").unlink()
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
    if socket_path != directory / "broker" / "control.sock":
        raise HarnessFailure("broker-control-layout-mismatch")
    os.chown(socket_path.parent, broker_uid, WORKER_GID, follow_symlinks=False)
    os.chmod(socket_path.parent, 0o710)
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
    socket_path = prepare_control_root(directory, broker_uid)
    broker = start_broker(script, socket_path, broker_scenario, dotnet_path, setpriv_path,
                          broker_uid, broker_gid, directory, home)
    worker = None
    try:
        worker = start_worker(setpriv_path, dotnet_path, worker_dll, socket_path, mode, home)
        stdout, _stderr = worker.communicate(timeout=15)
        if "protocol-canary" in stdout or "protocol-canary" in _stderr:
            raise HarnessFailure("worker-echoed-protocol-canary")
        result = parse_result(stdout)
        if worker.returncode != expected_exit or result != expected:
            raise HarnessFailure(f"case-failed:{name}:exit={worker.returncode}:result={result}")
        broker.wait(timeout=10)
        if broker.returncode != 0:
            _unused_stdout, broker_stderr = broker.communicate()
            details = broker_stderr.strip().splitlines()
            reason = details[-1] if details else "no-diagnostic"
            raise HarnessFailure(f"broker-failed:{name}:exit={broker.returncode}:{reason}")
        if broker_scenario.startswith("application-"):
            receipt = json.loads((directory / "protocol-operations.json").read_bytes())
            expected_ops = next(case[4] for case in APPLICATION_CASES if case[0] == broker_scenario)
            expected_eof = broker_scenario in ("application-cancel-start", "application-cancel-wait")
            if receipt != {"operations": list(expected_ops), "blocked_operation_eof": expected_eof,
                           "synthetic_application_acknowledgements": True}:
                raise HarnessFailure("application-operation-receipt-mismatch")
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
                           dotnet_path: str, setpriv_path: str, application_operation: str | None = None) -> None:
    directory = root / ("broker-replacement" if application_operation is None else "application-replace-" + application_operation)
    socket_path = prepare_control_root(directory, 0)
    first_scenario = "broker-first" if application_operation is None else "application-replacement-first-" + application_operation
    first = start_broker(script, socket_path, first_scenario, dotnet_path, setpriv_path,
                         0, 0, directory, home)
    worker = None
    second = None
    try:
        mode = "hold" if application_operation is None else "application-hold-" + application_operation
        worker = start_worker(setpriv_path, dotnet_path, worker_dll, socket_path, mode, home)
        connected = parse_result(read_worker_line(worker, 15))
        expected_connected = ({"status": "connected", "armed": True} if application_operation is None
                              else {"status": "application-held", "start_ack": application_operation == "wait"})
        if connected != expected_connected:
            raise HarnessFailure("case-failed:broker-first-handshake")
        ready_path = Path(str(socket_path) + ".ready")
        unlink_deadline = time.monotonic() + 5
        while (socket_path.exists() or ready_path.exists()) and time.monotonic() < unlink_deadline:
            if first.poll() is not None:
                raise HarnessFailure("broker-first-exited-before-replacement")
            time.sleep(0.01)
        if socket_path.exists() or ready_path.exists():
            raise HarnessFailure("broker-first-socket-or-ready-not-released")
        if first.poll() is not None:
            raise HarnessFailure("broker-first-exited-before-replacement")

        second = start_broker(script, socket_path, "broker-second", dotnet_path, setpriv_path,
                              0, 0, directory, home)
        if first.pid == second.pid:
            raise HarnessFailure("broker-pid-not-distinct")
        if first.poll() is not None:
            raise HarnessFailure("broker-first-exited-before-replacement")
        assert worker.stdin is not None
        worker.stdin.write("stop\n" if application_operation is None else "operate\n")
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
    identities = (WORKER_UID, WORKER_GID, SUBJECT_UID, SUBJECT_GID,
                  APPLICATION_UID, APPLICATION_GID, RESULTS_GID, RESOURCE_ACCESS_GID)
    if not all(value > 0 for value in identities) or len(set(identities)) != 8:
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
            ("missing-job-remaining", "missing-job-remaining", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("zero-job-remaining", "zero-job-remaining", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("excessive-job-remaining", "excessive-job-remaining", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("expired-wall-deadline", "expired-wall-deadline", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("malformed-json", "malformed-json", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("oversized-response", "oversized-response", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
            ("broker-rejected-response", "rejected-response", {"status": "rejected", "code": "ASEVD402"}, 20, "connect", 0, 0),
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

        for name, outcome, start_ack, readiness_ack, _operations in APPLICATION_CASES:
            print(f"RUN {name}", flush=True)
            expected = {"status": "application-control", "outcome": outcome,
                        "code": ("ASEVD402" if name in ("application-wrong-id", "application-wrong-digest")
                                 else "ASEVD410") if outcome == "rejected" else None,
                        "start_ack": start_ack, "readiness_ack": readiness_ack, "cleanup_ack": True}
            run_case(script, root, home, worker_dll, dotnet_path, setpriv_path, name,
                     name, expected, 0, mode=name)
            print(f"PASS {name}", flush=True)
        for operation in ("start", "wait"):
            print(f"RUN application-replace-{operation}", flush=True)
            run_broker_replacement(script, root, home, worker_dll, dotnet_path, setpriv_path, operation)
            print(f"PASS application-replace-{operation}", flush=True)

    print("PASS 40 control protocol mechanism cases; application ACKs: synthetic; admission: none", flush=True)


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
