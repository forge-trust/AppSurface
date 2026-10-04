#!/usr/bin/env python3
"""Root-owned, non-systemd protocol fixture for the production EvidenceHost consumers.

The fixture authenticates the real non-root testhost through SO_PEERCRED and answers
only the finite operations needed by the direct CLI/Aspire consumer tests. The run
operation is deliberately synthetic: this validates the production consumer path,
not subject isolation, a systemd unit, or a CI acceptance gate.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import platform
import shutil
import signal
import socket
import socketserver
import struct
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path
from typing import Any

REPORT = (
    b'<coverage lines-covered="1" lines-valid="1" branches-covered="2" branches-valid="2" '
    b'line-rate="1" branch-rate="1"><packages><package name="fixture" line-rate="1" '
    b'branch-rate="1"><classes><class name="Fixture" filename="fixture.cs" line-rate="1" '
    b'branch-rate="1"><methods /><lines><line number="1" hits="1" branch="true" '
    b'condition-coverage="100% (2/2)"><conditions><condition number="0" type="jump" '
    b'coverage="100%" /></conditions></line></lines></class></classes></package></packages></coverage>'
)
MAX_LINE = 64 * 1024
PROTOCOL_PRIVATE_CANARY = b"protocol-private-canary"
SANDBOX_MARKER_ENVIRONMENT = ("CODEX_SANDBOX", "SANDBOX_MODE", "IN_SANDBOX", "IS_SANDBOX")
SCENARIOS: dict[str, tuple[str, str, str]] = {
    "cli-coverage": ("observation", "coverage", "coverage"),
    "cli-trusted": ("trusted", "coverage", "none"),
    "cli-mode-conflict": ("observation", "coverage", "none"),
    "cli-policy-drift": ("observation", "coverage", "none"),
    "cli-subject-failure": ("observation", "coverage", "failure"),
    "cli-output-overflow": ("observation", "coverage", "overflow"),
    "cli-malformed": ("observation", "coverage", "malformed"),
    "cli-cancel": ("observation", "coverage", "none"),
    "protocol-negative-length": ("observation", "empty", "negative-length"),
    "protocol-declared-length": ("observation", "empty", "declared-length"),
    "protocol-encoded-limit": ("observation", "empty", "encoded-limit"),
    "protocol-owned-exit-false": ("observation", "empty", "owned-exit-false"),
    "protocol-duplicate-declaration": ("observation", "empty", "duplicate-declaration"),
    "protocol-decoded-limit": ("observation", "empty", "decoded-limit"),
    "protocol-empty-chunk": ("observation", "empty", "empty-chunk"),
    "aspire-empty": ("observation", "empty", "none"),
    "aspire-trusted": ("trusted", "coverage", "none"),
    "aspire-mode-conflict": ("observation", "empty", "none"),
    "aspire-policy-drift": ("observation", "empty", "none"),
}


def fixture_policy() -> dict[str, Any]:
    assertion = "appsurface/coverage/behavioral-patch@1"
    return {
        "id": "execution-broker-fixture",
        "version": "1",
        "conservativeProfileId": "coverage",
        "profiles": [
            {
                "id": "coverage",
                "scope": "targeted",
                "resources": [],
                "producers": [
                    {
                        "id": "coverage",
                        "kind": "coverage",
                        "version": "1.0.0",
                        "requiredResources": [],
                        "assertionIds": [assertion],
                        "artifactSlots": [
                            {"logicalName": "coverage-report", "relativeRoot": "coverage/merged", "mediaType": "application/xml", "required": True, "maximumBytes": 1048576},
                            {"logicalName": "coverage-summary", "relativeRoot": "coverage", "mediaType": "text/markdown", "required": True, "maximumBytes": 1048576},
                            {"logicalName": "coverage-gate", "relativeRoot": "coverage", "mediaType": "application/json", "required": True, "maximumBytes": 1048576},
                        ],
                        "timeoutSeconds": 60,
                        "coverageGate": {"minLinePercent": 0, "minBranchPercent": 0, "minPatchLinePercent": None, "minPatchBranchPercent": None, "patchLineMode": "measurable", "tolerancePercent": 0},
                    }
                ],
                "obligations": [
                    {"id": "coverage-fixture", "riskClass": "behavior", "rationale": "Exercise the registered protected coverage producer.", "requiredProducerIds": ["coverage"], "requiredAssertionId": assertion}
                ],
            },
            {"id": "empty", "scope": "targeted", "resources": [], "producers": [], "obligations": []},
        ],
        "rules": [
            {"id": "empty-observation", "pattern": "docs/evidence/no-evidence.txt", "profileId": "empty", "precedence": 0}
        ],
    }


def fail(message: str) -> None:
    raise RuntimeError(message)


def chown_mode(path: Path, uid: int, gid: int, mode: int) -> None:
    os.chown(path, uid, gid, follow_symlinks=False)
    os.chmod(path, mode, follow_symlinks=False)


def configure_coverage_ancestors(base: Path, worker_root: Path, worker_gid: int) -> None:
    """Allow retained coverage leases to read/search root-owned worker ancestors.

    The worker group cannot write these directories, and other identities receive
    no access. The fixture requires the subject UID/GID to differ from the worker.
    """
    for path in (base, worker_root):
        chown_mode(path, 0, worker_gid, 0o750)


def write_root_file(path: Path, data: bytes, gid: int, mode: int = 0o440) -> None:
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as target:
        target.write(data)
    chown_mode(path, 0, gid, mode)


def copy_reporter(source: Path, destination: Path, worker_gid: int) -> None:
    if source.name == "ReportGenerator.dll":
        source = source.parent
    elif (source / "tools" / "net10.0").is_dir():
        source = source / "tools" / "net10.0"
    if not (source / "ReportGenerator.dll").is_file():
        fail("The supplied restored ReportGenerator 5.5.10 net10.0 payload is missing.")
    if "5.5.10" not in str(source):
        fail("The ReportGenerator source must resolve from the pinned 5.5.10 package path.")
    for path in source.rglob("*"):
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            fail("The restored ReportGenerator payload contains a link or special file.")
    shutil.copytree(source, destination)
    for path in sorted(destination.rglob("*"), key=lambda item: len(item.parts), reverse=True):
        chown_mode(path, 0, worker_gid, 0o750 if path.is_dir() else 0o640)
    chown_mode(destination, 0, worker_gid, 0o750)


class Scenario:
    def __init__(self, name: str, base: Path, tool: Path, subject: Path,
                 output: Path, worker_uid: int, worker_gid: int,
                 subject_uid: int, subject_gid: int, dotnet: str,
                 policy_file: Path, policy_digest: str, log_file: Path,
                 socket_path: Path):
        mode, profile, behavior = SCENARIOS[name]
        self.name = name
        self.run_id = f"fixture-{base.name}-{name}/1"
        self.worker_uid = worker_uid
        self.worker_gid = worker_gid
        self.subject_uid = subject_uid
        self.subject_gid = subject_gid
        self.dotnet = dotnet
        self.policy_file = policy_file
        self.policy_digest = policy_digest
        self.log_file = log_file
        self.socket_path = socket_path
        self.subject = subject
        self.output = output
        self.profile = profile
        self.behavior = behavior
        self.mode = mode
        self.paths = ["src/Feature.cs"] if profile == "coverage" else ["docs/evidence/no-evidence.txt"]
        self.peer: tuple[int, int, int] | None = None
        self.lock = threading.Lock()
        self.descriptor: dict[str, Any] | None = None
        self.report = REPORT
        self.stop_seen = False
        self.exit_seen = False

        info = output.stat()
        self.parent_identity = {
            "device_major": os.major(info.st_dev),
            "device_minor": os.minor(info.st_dev),
            "inode": info.st_ino,
            "uid": info.st_uid,
            "gid": info.st_gid,
        }
        digest = policy_digest
        if behavior == "none" and name.endswith("policy-drift"):
            digest = ("0" if digest[0] != "0" else "1") + digest[1:]
        self.policy_sha256 = digest
        self.metadata = {
            "scenario": name,
            "socket": str(socket_path),
            "policyFile": str(policy_file),
            "policySha256": policy_digest,
            "mode": mode,
            "profile": profile,
            "paths": self.paths,
            "toolRoot": str(tool),
            "subjectRoot": str(subject),
            "dotnetPath": dotnet,
            "outputParent": str(output),
            "outputSlot": "evidence-output",
            "outputDirectory": str(output / "evidence-output"),
            "operationsFile": str(log_file),
            "peerFile": str(log_file.with_suffix(".peer.json")),
            "workerUid": worker_uid,
            "workerGid": worker_gid,
            "subjectUid": subject_uid,
            "subjectGid": subject_gid,
            "syntheticCgroup": f"/system.slice/evidence-fixture-{name}.service",
        }

    def pin_peer(self, connection: socket.socket) -> tuple[int, int, int]:
        peer = struct.unpack("3i", connection.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
        if peer[1] != self.worker_uid or peer[2] != self.worker_gid or peer[1] == 0:
            fail("The connected consumer is not the configured non-root worker UID/GID.")
        with self.lock:
            if self.peer is None:
                self.peer = peer
                self.descriptor = self.make_descriptor(peer)
                write_root_file(Path(self.descriptor["descriptor_path"]),
                                json.dumps(self.descriptor, separators=(",", ":")).encode() + b"\n", self.worker_gid)
                peer_path = Path(self.log_file).with_suffix(".peer.json")
                record = {"pid": peer[0], "uid": peer[1], "gid": peer[2]}
                write_root_file(peer_path, json.dumps(record, separators=(",", ":")).encode() + b"\n", self.worker_gid)
                self.metadata["peerFile"] = str(peer_path)
            elif self.peer != peer:
                fail("A scenario socket cannot be reused by a different PID/UID/GID.")
        return peer

    def make_descriptor(self, peer: tuple[int, int, int]) -> dict[str, Any]:
        policy_digest = self.policy_sha256
        unit = f"evidence-fixture-{self.name}.service"
        cgroup = f"/system.slice/{unit}"  # Synthetic shape only; no systemd unit is created.
        if self.behavior == "malformed":
            cgroup = "/user.slice/malformed-fixture.scope"
        now = time.time()
        return {
            "schema": "evidence-worker-linux-v1",
            "run_id": self.run_id,
            "worker_pid": peer[0], "broker_pid": os.getpid(), "worker_uid": peer[1], "worker_gid": peer[2],
            "subject_uid": self.subject_uid, "subject_gid": self.subject_gid,
            "unit": unit, "cgroup": cgroup,
            "job_deadline_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now + 600)),
            "tool_root": str(self.metadata["toolRoot"]),
            "subject_root": str(self.subject), "output_parent": str(self.output),
            "output_slot": "evidence-output", "dotnet_path": self.dotnet,
            "test_output_root": str(self.subject / "test-output"),
            "policy_file": str(self.policy_file), "mode": self.mode,
            "socket_path": str(self.socket_path),
            "descriptor_path": str(self.socket_path.parent.parent / "worker-control.json"),
            "entry_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
            "base_revision": "b" * 40,
            "subject_revision": "c" * 40,
            "workflow_identity": "fixture:issue-779:consumer-tests",
            "provider": "github-actions", "platform": "linux-x64",
            "proof_digest": "0" * 64, "policy_sha256": policy_digest,
            "output_parent_identity": self.parent_identity,
            "observation_profile_ids": ["coverage", "empty"],
            "observation_producer_ids": ["coverage"],
            "paths": self.paths,
            "admission_seconds": 10, "start_seconds": 10,
            "collection_seconds": 20, "cleanup_seconds": 30,
            "stopping_seconds": 5,
            "solution": str(self.subject / "fixture.slnx"),
            "diff_file": None, "diff_sha256": None,
        }

    def log(self, operation: str, peer: tuple[int, int, int], request: dict[str, Any]) -> None:
        record = {"op": operation, "peer": {"pid": peer[0], "uid": peer[1], "gid": peer[2]}}
        if operation == "run":
            record["executable"] = request.get("executable")
            record["workingDirectory"] = request.get("working_directory")
        fd = os.open(self.log_file, os.O_WRONLY | os.O_APPEND)
        try:
            os.write(fd, json.dumps(record, separators=(",", ":")).encode() + b"\n")
        finally:
            os.close(fd)

    def response(self, request: dict[str, Any], peer: tuple[int, int, int]) -> dict[str, Any]:
        op = request.get("op")
        self.log(str(op), peer, request)
        if op == "ready":
            return {"ok": True, "descriptor": self.descriptor, "job_remaining_seconds": 600.0}
        if op == "run":
            if self.behavior == "coverage":
                code, received = 0, 32
            elif self.behavior == "failure":
                code, received = 17, 64
            elif self.behavior == "overflow":
                code, received = 0, 16 * 1024 * 1024 + 1
            else:
                return {"ok": False, "error": "unexpected-run"}
            return {"ok": True, "exit_code": code, "stdout": "", "stderr": "", "output_truncated": False, "received_bytes": received}
        if op == "artifacts" and self.behavior in ("negative-length", "declared-length", "encoded-limit"):
            if request.get("relative_root") != "coverage-protocol":
                return {"ok": False, "error": "invalid-results-token"}
            length = {"negative-length": -1, "declared-length": 1, "encoded-limit": 131076}[self.behavior]
            return {"ok": True, "artifacts": [{"path": "reports/result.bin", "length_bytes": length}]}
        if op == "artifact" and self.behavior in ("declared-length", "encoded-limit"):
            if (request.get("relative_root") != "coverage-protocol"
                    or request.get("relative_path") != "reports/result.bin" or request.get("offset") != 0):
                return {"ok": False, "error": "invalid-artifact-request"}
            payload = b"ab" if self.behavior == "declared-length" else b"p" * 131076
            return {"ok": True, "bytes_base64": base64.b64encode(payload).decode("ascii"), "end": True}
        if op == "artifacts" and self.behavior in ("duplicate-declaration", "decoded-limit", "empty-chunk"):
            if request.get("relative_root") != "coverage-protocol":
                return {"ok": False, "error": "invalid-results-token"}
            length = {"duplicate-declaration": 0,
                      "decoded-limit": 128 * 1024 + 1, "empty-chunk": 1}[self.behavior]
            row = {"path": "reports/protocol-private-canary.bin", "length_bytes": length}
            declarations = [dict(row), dict(row)] if self.behavior == "duplicate-declaration" else [row]
            return {"ok": True, "artifacts": declarations}
        if op == "artifact" and self.behavior in ("duplicate-declaration", "decoded-limit", "empty-chunk"):
            if (request.get("relative_root") != "coverage-protocol"
                    or request.get("relative_path") != "reports/protocol-private-canary.bin"
                    or request.get("offset") != 0):
                return {"ok": False, "error": "invalid-artifact-request"}
            if self.behavior == "duplicate-declaration":
                payload = b""
            elif self.behavior == "decoded-limit":
                payload = PROTOCOL_PRIVATE_CANARY + b"p" * (128 * 1024 + 1 - len(PROTOCOL_PRIVATE_CANARY))
            else:
                payload = b""
            return {"ok": True, "bytes_base64": base64.b64encode(payload).decode("ascii"),
                    "end": self.behavior != "empty-chunk"}
        if op == "artifacts" and self.behavior == "coverage":
            if not isinstance(request.get("relative_root"), str) or not request["relative_root"].startswith("coverage-"):
                return {"ok": False, "error": "invalid-results-token"}
            return {"ok": True, "artifacts": [{"path": "coverage.cobertura.xml", "length_bytes": len(self.report)}]}
        if op == "artifact" and self.behavior == "coverage":
            offset = request.get("offset")
            if request.get("relative_path") != "coverage.cobertura.xml" or not isinstance(offset, int) or offset < 0 or offset > len(self.report):
                return {"ok": False, "error": "invalid-artifact-request"}
            chunk = self.report[offset:offset + 128 * 1024]
            end = offset + len(chunk) == len(self.report)
            return {"ok": True, "bytes_base64": base64.b64encode(chunk).decode("ascii"), "end": end}
        if op == "stop":
            self.stop_seen = True
            return {"ok": True}
        if op == "wait":
            self.stop_seen = True
            return {"ok": True, "owned_exit": self.behavior != "owned-exit-false"}
        if op == "exit" and self.stop_seen and self.behavior != "owned-exit-false":
            self.exit_seen = True
            return {"ok": True}
        return {"ok": False, "error": "unsupported-fixture-operation"}


def scenario_server(scenario: Scenario, gid: int) -> socketserver.ThreadingUnixStreamServer:
    class Server(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
        daemon_threads = True
        block_on_close = False
        allow_reuse_address = False

        def process_request(self, request: socket.socket, client_address: Any) -> None:
            request.settimeout(5)
            thread = threading.Thread(target=self.process_request_thread, args=(request, client_address),
                                      name=f"handler-{scenario.name}", daemon=True)
            with self.handler_lock:
                self.handler_threads.append(thread)
            thread.start()

    class Handler(socketserver.StreamRequestHandler):
        def handle(self) -> None:
            raw = self.rfile.readline(MAX_LINE + 1)
            if not raw.endswith(b"\n") or len(raw) > MAX_LINE:
                self.wfile.write(b'{"ok":false,"error":"request-limit"}\n')
                return
            try:
                request = json.loads(raw)
                if not isinstance(request, dict):
                    raise ValueError("request-shape")
                peer = scenario.pin_peer(self.request)
                response = scenario.response(request, peer)
            except Exception:
                response = {"ok": False, "error": "fixture-request-rejected"}
            self.wfile.write(json.dumps(response, separators=(",", ":")).encode() + b"\n")

    server = Server(str(scenario.socket_path), Handler)
    os.chown(scenario.socket_path, 0, gid)
    os.chmod(scenario.socket_path, 0o660)
    server.handler_lock = threading.Lock()
    server.handler_threads = []
    server.scenario = scenario  # type: ignore[attr-defined]
    return server


def make_environment(base: Path, socket_dir: Path, worker_root: Path,
                     worker_uid: int, worker_gid: int, dotnet: str) -> dict[str, str]:
    home = worker_root / "home"
    cli_home = worker_root / "dotnet-home"
    cache = worker_root / "cache"
    source = worker_root / "source"
    tmp = worker_root / "tmp"
    results = worker_root / "logs"
    for directory in (home, cli_home, cache, source, tmp, results):
        directory.mkdir(mode=0o700)
        chown_mode(directory, worker_uid, worker_gid, 0o700)
    env = {
        "PATH": f"{Path(dotnet).parent}:/usr/local/bin:/usr/bin:/bin",
        "HOME": str(home), "TMPDIR": str(tmp), "TMP": str(tmp), "TEMP": str(tmp),
        "DOTNET_CLI_HOME": str(cli_home), "NUGET_PACKAGES": str(cache),
        "DOTNET_CLI_TELEMETRY_OPTOUT": "1", "LANG": "C.UTF-8",
        "EVIDENCEHOST_TEST_BROKER_SOCKET": str(socket_dir),
        "EVIDENCEHOST_TEST_BROKER_POLICY": str(base / "tool" / "policy.json"),
        "EVIDENCEHOST_TEST_BROKER_RESULTS": str(results),
        "EVIDENCEHOST_TEST_SOURCE_ROOT": str(source),
    }
    env.update({key: os.environ[key] for key in SANDBOX_MARKER_ENVIRONMENT if key in os.environ})
    return env


def main(argv: list[str]) -> int:
    if "--" not in argv:
        if "-h" in argv or "--help" in argv:
            parser().parse_args(argv[1:])
        fail("Supply the non-root test command after --.")
    split = argv.index("--")
    args = parser().parse_args(argv[1:split])
    command = argv[split + 1:]
    if not command:
        fail("The command after -- is empty.")
    if os.geteuid() != 0:
        fail("Run this fixture as root; it drops the test command with setpriv.")
    if sys.platform != "linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        fail("The protocol fixture requires Linux x86-64.")
    if args.worker_uid == 0 or args.worker_gid == 0 or args.subject_uid == 0 or args.subject_gid == 0:
        fail("Worker and subject UID/GID values must be non-root.")
    if args.worker_uid == args.subject_uid or args.worker_gid == args.subject_gid:
        fail("Worker and subject identities must differ by both UID and GID.")
    if args.worker_supplementary_groups is not None and args.subject_gid in args.worker_supplementary_groups:
        fail("The subject GID cannot be a worker supplementary group.")
    setpriv = shutil.which("setpriv")
    dotnet = str(Path(args.dotnet or shutil.which("dotnet") or "").resolve())
    if not setpriv or not Path(dotnet).is_file():
        fail("setpriv and the supplied .NET host must be installed.")
    source = Path(args.reportgenerator_package).resolve(strict=True)
    package_tools = source.parent if source.name == "ReportGenerator.dll" else source
    base = Path(tempfile.mkdtemp(prefix="eh779-", dir="/tmp")).resolve()
    socket_dir = base / "s"
    tool = base / "tool"
    subject = base / "subject"
    output_root = base / "o"
    worker_root = base / "w"
    socket_dir.mkdir(mode=0o710)
    tool.mkdir(mode=0o750)
    subject.mkdir(mode=0o711)
    output_root.mkdir(mode=0o711)
    worker_root.mkdir(mode=0o711)
    configure_coverage_ancestors(base, worker_root, args.worker_gid)
    for path, gid, mode in ((socket_dir, args.worker_gid, 0o710),
                            (tool, args.worker_gid, 0o750), (output_root, args.worker_gid, 0o710)):
        chown_mode(path, 0, gid, mode)
    chown_mode(subject, args.subject_uid, args.subject_gid, 0o711)
    (subject / "test-output").mkdir(mode=0o700)
    chown_mode(subject / "test-output", args.subject_uid, args.subject_gid, 0o700)
    (subject / "fixture.slnx").write_text("<Solution />\n", encoding="utf-8")
    chown_mode(subject / "fixture.slnx", args.subject_uid, args.subject_gid, 0o440)
    reporter = tool / "reportgenerator" / "net10.0"
    reporter.parent.mkdir(mode=0o750)
    chown_mode(reporter.parent, 0, args.worker_gid, 0o750)
    copy_reporter(package_tools, reporter, args.worker_gid)
    policy_file = tool / "policy.json"
    policy_bytes = json.dumps(fixture_policy(), sort_keys=True, separators=(",", ":")).encode("utf-8") + b"\n"
    write_root_file(policy_file, policy_bytes, args.worker_gid)
    policy_digest = hashlib.sha256(policy_bytes).hexdigest()
    scenarios: dict[str, Scenario] = {}
    servers: list[socketserver.ThreadingUnixStreamServer] = []
    threads: list[threading.Thread] = []
    try:
        for name in SCENARIOS:
            control_root = socket_dir / name
            broker_root = control_root / "broker"
            for directory in (control_root, broker_root):
                directory.mkdir(mode=0o710)
                chown_mode(directory, 0, args.worker_gid, 0o710)
            output = output_root / name
            output.mkdir(mode=0o700)
            chown_mode(output, args.worker_uid, args.worker_gid, 0o700)
            log_file = worker_root / f"{name}.jsonl"
            fd = os.open(log_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            os.close(fd)
            chown_mode(log_file, args.worker_uid, args.worker_gid, 0o600)
            scenario = Scenario(name, base, tool, subject, output, args.worker_uid, args.worker_gid,
                                args.subject_uid, args.subject_gid, dotnet, policy_file, policy_digest,
                                log_file, broker_root / "control.sock")
            server = scenario_server(scenario, args.worker_gid)
            metadata_file = socket_dir / f"{name}.json"
            write_root_file(metadata_file, json.dumps(scenario.metadata, separators=(",", ":")).encode() + b"\n", args.worker_gid)
            scenarios[name] = scenario
            servers.append(server)
            thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05},
                                      name=f"fixture-{name}", daemon=True)
            threads.append(thread)
            thread.start()
        env = make_environment(base, socket_dir, worker_root, args.worker_uid, args.worker_gid, dotnet)
        env["EVIDENCEHOST_TEST_BROKER_METADATA"] = str(socket_dir)
        child_command = [setpriv, "--reuid", str(args.worker_uid), "--regid", str(args.worker_gid)]
        if args.worker_supplementary_groups is None:
            child_command.append("--clear-groups")
        else:
            child_command.extend(("--groups", ",".join(map(str, args.worker_supplementary_groups))))
        child_command.extend(("--", *command))
        print(f"fixture_root={base}", file=sys.stderr, flush=True)
        print(f"fixture_worker_uid={args.worker_uid} fixture_worker_gid={args.worker_gid}", file=sys.stderr, flush=True)
        returncode = run_test_command(child_command, env, args.command_timeout_seconds)
    finally:
        # Physical ownership is independent of any synthetic owned_exit acknowledgement.
        cleanup_ok = join_broker_servers(servers, threads)
        if not cleanup_ok:
            fail("fixture-owned-work-not-joined")
    for scenario in scenarios.values():
        if scenario.peer is not None:
            expected = (scenario.peer[1], scenario.peer[2])
            if expected != (args.worker_uid, args.worker_gid):
                fail(f"Broker peer identity mismatch in {scenario.name}.")
    print(f"fixture_process_exit={returncode}", file=sys.stderr, flush=True)
    return returncode


def run_test_command(command: list[str], environment: dict[str, str], timeout_seconds: float) -> int:
    """Run the owner's selected command, then independently close its physical ownership."""
    process = subprocess.Popen(command, env=environment, start_new_session=True)
    try:
        return process.wait(timeout=timeout_seconds)
    finally:
        if not join_test_process(process):
            fail("fixture-process-group-not-joined")


def join_test_process(process: subprocess.Popen, cleanup_seconds: float = 8) -> bool:
    """Reap the leader and poll physical group absence under one retained cleanup deadline."""
    deadline = time.monotonic() + cleanup_seconds

    def remaining() -> float:
        return max(0, deadline - time.monotonic())

    def group_present() -> bool:
        try:
            os.killpg(process.pid, 0)
            return True
        except ProcessLookupError:
            return False

    if group_present():
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=min(2, remaining()))
        except subprocess.TimeoutExpired:
            pass
        # Leader exit never substitutes for whole-group absence.
        if group_present():
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except OSError:
                pass  # Reap even if a dead, unreaped leader cannot be signalled.
    try:
        process.wait(timeout=min(3, remaining()))
    except subprocess.TimeoutExpired:
        return False
    poll = threading.Event()
    while group_present():
        allowance = remaining()
        if allowance <= 0:
            return False
        poll.wait(timeout=min(0.02, allowance))
    return time.monotonic() <= deadline


def join_broker_servers(servers: list[Any], threads: list[threading.Thread], cleanup_seconds: float = 6) -> bool:
    """Explicitly join retained daemon threads; survivors reject cleanup without trapping interpreter exit."""
    for index, server in enumerate(servers):
        if index < len(threads) and threads[index].is_alive():
            server.shutdown()
        server.server_close()
    deadline = time.monotonic() + cleanup_seconds
    owned = list(threads)
    for server in servers:
        with server.handler_lock:
            owned.extend(server.handler_threads)
    for thread in owned:
        if thread.ident is not None:
            thread.join(timeout=max(0, deadline - time.monotonic()))
    return all(not thread.is_alive() for thread in owned)


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument("--worker-uid", required=True, type=int)
    result.add_argument("--worker-gid", required=True, type=int)
    result.add_argument("--worker-supplementary-groups", type=parse_supplementary_groups,
                        help="explicit comma-separated positive GIDs; omitted means clear supplementary groups")
    result.add_argument("--subject-uid", required=True, type=int)
    result.add_argument("--subject-gid", required=True, type=int)
    result.add_argument("--reportgenerator-package", required=True)
    result.add_argument("--dotnet")
    result.add_argument("--command-timeout-seconds", type=parse_command_timeout, default=4200,
                        help="owner-selected command allowance, 1..4200 seconds; focused runs may select 600")
    return result


def parse_command_timeout(value: str) -> int:
    if not value.isascii() or not value.isdecimal():
        raise argparse.ArgumentTypeError("command timeout must be an integer from 1 through 4200 seconds")
    seconds = int(value)
    if not 1 <= seconds <= 4200:
        raise argparse.ArgumentTypeError("command timeout must be an integer from 1 through 4200 seconds")
    return seconds


def parse_supplementary_groups(value: str) -> list[int]:
    parts = value.split(",")
    if not parts or any(not part or any(character not in "0123456789" for character in part) for part in parts):
        raise argparse.ArgumentTypeError("supplementary groups must be comma-separated positive GIDs")
    groups = [int(part) for part in parts]
    if any(group <= 0 for group in groups):
        raise argparse.ArgumentTypeError("supplementary GIDs must be positive and cannot include root GID 0")
    if len(set(groups)) != len(groups):
        raise argparse.ArgumentTypeError("supplementary GIDs must not contain duplicates")
    return groups


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv))
    except (OSError, RuntimeError, subprocess.SubprocessError, ValueError) as error:
        print(f"execution-broker-fixture: {type(error).__name__}", file=sys.stderr)
        raise SystemExit(2)
