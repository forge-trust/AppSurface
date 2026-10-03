"""Focused isolation and failure-path tests for the subject OCI launcher."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import signal
import stat
import sys
import tempfile
import threading
from types import SimpleNamespace
from typing import Callable
import unittest
from contextlib import redirect_stdout
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-subject.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_subject", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = subject
SPEC.loader.exec_module(subject)


IMAGE = "ghcr.io/forge-trust/appsurface-subject@sha256:" + "a" * 64
ARTIFACT_CONTENTS = (
    b"cobertura artifact\n",
    b"coverage gate report\n",
    b"coverage diagnostics\n",
)


class FakeExecutor:
    """Return deterministic Podman JSON while retaining every fixed argv."""

    def __init__(self, root: Path, image: str = IMAGE) -> None:
        self.root = root
        self.image = image
        self.calls: list[tuple[list[str], dict[str, object]]] = []
        self.container_name: str | None = None
        self.fail_start: BaseException | None = None
        self.start_exit_code = 0
        self.start_stderr = b""
        self.fail_cleanup = False
        self.fail_mount = False
        self.fail_unmount = False
        self.cleanup_order: list[str] = []
        self.mount_options: str | None = None
        self.mount_target: str | None = None
        self.result_export: bytes | None = self.successful_subject_record()
        self.config_override: dict[str, object] | None = None
        self.scratch_mount_override: dict[str, object] | None = None
        self.labels_override: dict[str, str] | None = None
        self.partial_mount_present = False
        self.stopped_state: dict[str, object] = {"Running": False, "Status": "exited"}
        self.artifact_mutation: Callable[[Path], None] | None = None
        self.lifecycle: list[str] = []
        self.scratch_mountpoint: Path | None = None
        self.container_started = False
        self.inspect_count = 0

    def __call__(self, arguments: list[str], **options: object) -> subject.CommandResult:
        self.calls.append((list(arguments), dict(options)))
        self.assert_execution_options(options)
        command = arguments[1:]
        if command == ["--remote=false", "info", "--format=json"]:
            return self.result(
                {
                    "host": {
                        "security": {"rootless": True},
                        "cgroupVersion": "v2",
                        "cgroupControllers": ["cpu", "memory", "pids"],
                    }
                }
            )
        if command[:3] == ["--remote=false", "image", "exists"]:
            return subject.CommandResult(0, b"", b"")
        if command[:3] == ["--remote=false", "image", "inspect"]:
            return self.result(
                [
                    {
                        "RepoDigests": [self.image],
                        "Config": {"Volumes": {}},
                    }
                ]
            )
        if command[1] == "create":
            name_value = next(value for value in command if value.startswith("--name="))
            self.container_name = name_value.partition("=")[2]
            return subject.CommandResult(0, b"", b"")
        if command[1:3] == ["inspect", "--format=json"]:
            self.lifecycle.append("inspect")
            self.inspect_count += 1
            return self.container_inspect()
        if arguments[0] == subject.SUDO_PATH:
            privileged_command = arguments[1:]
            if privileged_command[:4] == ["-n", "mount", "-t", "tmpfs"]:
                self.cleanup_order.append("mount")
                self.mount_options = privileged_command[5]
                self.mount_target = privileged_command[-1]
                return subject.CommandResult(1 if self.fail_mount else 0, b"", b"")
            if privileged_command[:3] == ["-n", "umount", "--"]:
                self.cleanup_order.append("umount")
                assert self.mount_target == privileged_command[-1]
                return subject.CommandResult(1 if self.fail_unmount else 0, b"", b"")
        if command[1] == "start":
            if self.fail_start is not None:
                raise self.fail_start
            self.lifecycle.append("start")
            self._write_scratch_artifacts()
            self.container_started = True
            return subject.CommandResult(self.start_exit_code, self.result_export or b"", self.start_stderr)
        if command[1:3] == ["rm", "--force"]:
            self.cleanup_order.append("container-remove")
            self.lifecycle.append("container-remove")
            return subject.CommandResult(1 if self.fail_cleanup else 0, b"", b"")
        raise AssertionError(f"unexpected fixed engine argv: {arguments!r}")

    @staticmethod
    def assert_execution_options(options: dict[str, object]) -> None:
        assert options["timeout_seconds"] > 0
        assert options["maximum_output_bytes"] > 0
        assert options["environment"] == {
            "PATH": subject.ENGINE_PATH,
            "HOME": str(options["environment"]["HOME"]),
            "XDG_RUNTIME_DIR": str(options["environment"]["XDG_RUNTIME_DIR"]),
            "TMPDIR": str(options["environment"]["TMPDIR"]),
            "REGISTRY_AUTH_FILE": "/dev/null",
        }
        assert isinstance(options["cancel_event"], threading.Event)

    @staticmethod
    def result(value: object) -> subject.CommandResult:
        return subject.CommandResult(0, json.dumps(value, separators=(",", ":")).encode(), b"")

    @staticmethod
    def successful_subject_record() -> bytes:
        steps = [
            {
                "exitCode": 0,
                "name": name,
                "outputBytes": 0,
                "stderrSha256": "0" * 64,
                "stdoutSha256": "0" * 64,
            }
            for name in subject.SUBJECT_RESULT_STEP_NAMES
        ]
        value = {
            "artifacts": [
                {
                    "logicalName": logical_name,
                    "relativePath": relative_path,
                    "byteCount": len(content),
                    "sha256": hashlib.sha256(content).hexdigest(),
                }
                for (logical_name, relative_path, _maximum_bytes), content in zip(
                    subject.SUBJECT_ARTIFACTS, ARTIFACT_CONTENTS, strict=True
                )
            ],
            "claimEligible": False,
            "profileId": "code-coverage",
            "schemaVersion": 1,
            "status": "completed",
            "steps": steps,
        }
        return (json.dumps(value, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii")

    def container_inspect(self) -> subject.CommandResult:
        assert self.container_name is not None
        create_call = next(call for call, _ in self.calls if len(call) > 2 and call[2] == "create")
        mounts = [create_call[index + 1] for index, value in enumerate(create_call[:-1]) if value == "--mount"]
        subject_mount = next(value for value in mounts if "dst=/subject," in value)
        subject_source = next(value.split("=", 1)[1] for value in subject_mount.split(",") if value.startswith("src="))
        diff_mount = next(value for value in mounts if "dst=/source.diff," in value)
        diff_source = next(value.partition("=")[2] for value in diff_mount.split(",") if value.startswith("src="))
        scratch_mount = next(
            value for value in mounts if "dst=/scratch," in value
        )
        scratch_source = next(
            value.partition("=")[2] for value in scratch_mount.split(",") if value.startswith("src=")
        )
        self.scratch_mountpoint = Path(scratch_source)
        create_arguments = create_call
        labels = {
            key: value
            for argument in create_arguments
            if argument.startswith("--label=")
            for key, separator, value in [argument[len("--label="):].partition("=")]
            if separator
        }
        if self.labels_override is not None:
            labels = dict(self.labels_override)
        pids = int(next(value.partition("=")[2] for value in create_arguments if value.startswith("--pids-limit=")))
        memory_mib = int(next(value.partition("=")[2][:-1] for value in create_arguments if value.startswith("--memory=")))
        cpu_millis = round(
            float(next(value.partition("=")[2] for value in create_arguments if value.startswith("--cpus="))) * 1000
        )
        environment = [value.partition("=")[2] for value in create_arguments if value.startswith("--env=")]
        document = [
            {
                "Config": {
                    "Entrypoint": [subject.CONTAINER_ENTRYPOINT],
                    "Cmd": create_call[create_call.index(self.image) + 1 :],
                    "User": "65532:65532",
                    "WorkingDir": "/subject",
                    "Env": environment,
                    "Labels": labels,
                },
                "HostConfig": {
                    "ReadonlyRootfs": True,
                    "CapDrop": sorted(subject.PODMAN_DEFAULT_CAPABILITIES),
                    "SecurityOpt": ["no-new-privileges"],
                    "NetworkMode": "none",
                    "PidsLimit": pids,
                    "Memory": memory_mib * 1024 * 1024,
                    "NanoCpus": cpu_millis * 1_000_000,
                    "Privileged": False,
                    "Devices": [],
                    "PortBindings": {},
                    "Tmpfs": {},
                    "PidMode": "private",
                    "IpcMode": "private",
                    "UTSMode": "private",
                    "CgroupnsMode": None,
                    "UsernsMode": "private",
                },
                "Mounts": [
                    {
                        "Type": "bind",
                        "Source": subject_source,
                        "Destination": "/subject",
                        "RW": False,
                        "Propagation": "rprivate",
                    },
                    {
                        "Type": "bind",
                        "Source": diff_source,
                        "Destination": "/source.diff",
                        "RW": False,
                        "Propagation": "rprivate",
                    },
                    {
                        "Type": "bind",
                        "Source": scratch_source,
                        "Destination": "/scratch",
                        "RW": True,
                        "Propagation": "rprivate",
                    },
                ],
                "State": (
                    {
                        "Running": False,
                        "Status": "created",
                        "Paused": False,
                        "Restarting": False,
                        "OOMKilled": False,
                        "Dead": False,
                        "Pid": 0,
                        "ExitCode": 0,
                        "Error": "",
                    }
                    if not self.container_started
                    else dict(
                        {
                            "Running": False,
                            "Status": "exited",
                            "Paused": False,
                            "Restarting": False,
                            "OOMKilled": False,
                            "Dead": False,
                            "Pid": 0,
                            "ExitCode": self.start_exit_code,
                            "Error": "",
                        }
                        | self.stopped_state
                    )
                ),
            }
        ]
        if self.config_override:
            document[0]["HostConfig"].update(self.config_override)
        if self.scratch_mount_override is not None:
            scratch_mount_document = next(
                mount for mount in document[0]["Mounts"] if mount["Destination"] == "/scratch"
            )
            scratch_mount_document.update(self.scratch_mount_override)
        return self.result(document)

    def _write_scratch_artifacts(self) -> None:
        assert self.scratch_mountpoint is not None
        root = self.scratch_mountpoint
        for (_logical_name, relative_path, _maximum_bytes), content in zip(
            subject.SUBJECT_ARTIFACTS, ARTIFACT_CONTENTS, strict=True
        ):
            source = root / relative_path
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(content)
        if self.artifact_mutation is not None:
            self.artifact_mutation(root)


class FakeCleanupSupervisor:
    """Public launcher seam fake; cleanup actions remain owned by this fake supervisor."""

    def __init__(
        self,
        executor: FakeExecutor,
        environment: dict[str, str],
        *,
        ready: bool = True,
        fail_alive: bool = False,
        attestation: dict[str, object] | None = None,
    ) -> None:
        self.executor = executor
        self.environment = environment
        self.ready = ready
        self.fail_alive = fail_alive
        self.attestation = attestation
        self.owner_token = "b" * 32
        self.session: subject.SupervisorSession | None = None
        self.scratch_directory: Path | None = None
        self.started = False
        self.requested = False
        self.request_observed_export = False
        self.artifact_export_directory: Path | None = None
        self.request_observed_artifact_directory = False
        self.request_observed_artifact_names: tuple[str, ...] = ()
        self.lifecycle: list[str] = []

    def start(self, **arguments: object) -> subject.SupervisorSession:
        self.started = True
        self.lifecycle.append("start")
        self.scratch_directory = Path(str(arguments["scratch_directory"]))
        if self.ready:
            state = Path(str(arguments["runner_temp"])) / f"appsurface-subject-supervisor-{self.owner_token}"
            self.session = subject.SupervisorSession(
                owner_token=self.owner_token,
                owner_label=subject.SUPERVISOR_OWNER_LABEL,
                manifest_path=state / "supervisor.json",
                state_directory=state,
                supervisor_pid=12345,
                supervisor_start_time=67890,
                ready=True,
                state_directory_device=-1,
                state_directory_inode=-1,
            )
            return self.session
        return subject.SupervisorSession(
            owner_token=self.owner_token,
            owner_label=subject.SUPERVISOR_OWNER_LABEL,
            manifest_path=Path("/unavailable/supervisor.json"),
            state_directory=Path("/unavailable"),
            supervisor_pid=12345,
            supervisor_start_time=67890,
            ready=False,
            state_directory_device=-1,
            state_directory_inode=-1,
        )

    def assert_alive(self, session: subject.SupervisorSession) -> None:
        if not self.ready or self.fail_alive or session is not self.session:
            raise subject.SubjectLauncherError("ASEGS017", "fake supervisor is not ready")
        self.lifecycle.append("assert-alive")

    def request_and_wait(self, session: subject.SupervisorSession) -> dict[str, object]:
        self.requested = True
        self.lifecycle.append("request")
        self.executor.lifecycle.append("supervisor-request")
        if self.artifact_export_directory is not None:
            self.request_observed_artifact_directory = self.artifact_export_directory.is_dir()
            if self.request_observed_artifact_directory:
                self.request_observed_artifact_names = tuple(
                    sorted(path.name for path in self.artifact_export_directory.iterdir())
                )
        if session is not self.session:
            raise subject.SubjectLauncherError("ASEGS017", "fake supervisor session mismatch")
        mount_call = next(
            (call for call, _ in self.executor.calls if call[0] == subject.SUDO_PATH and call[2] == "mount"),
            None,
        )
        create_attempted = any(call[2] == "create" for call, _ in self.executor.calls)
        container_status = "absent"
        if create_attempted:
            result = self.executor(
                ["/usr/bin/podman", "--remote=false", "rm", "--force", "--ignore", self.executor.container_name or ""],
                **self._command_options(),
            )
            container_status = "removed" if result.returncode == 0 else "failed"

        mount_status = "absent"
        mount_present = False
        if mount_call is not None:
            mount_present = (
                not self.executor.fail_mount
                or subject._host_scratch_mount_is_present(Path(str(self.executor.mount_target)))
            )
        if mount_call is not None and mount_present:
            if container_status in {"removed", "absent"}:
                result = self.executor(
                    [subject.SUDO_PATH, "-n", "umount", "--", str(self.executor.mount_target)],
                    **self._command_options(),
                )
                mount_status = "unmounted" if result.returncode == 0 else "failed"
                if result.returncode == 0:
                    shutil.rmtree(Path(str(self.executor.mount_target)))
            else:
                mount_status = "unverified"
        elif mount_call is not None:
            shutil.rmtree(Path(str(self.executor.mount_target)))
        scratch_status = (
            "removed"
            if container_status in {"removed", "absent"} and mount_status in {"unmounted", "absent"}
            else "preserved"
        )
        self.request_observed_export = (
            self.scratch_directory is not None
            and (self.scratch_directory / subject.SUBJECT_RESULT_RELATIVE_PATH).is_file()
        )
        complete = (
            container_status in {"removed", "absent"}
            and mount_status in {"unmounted", "absent"}
            and scratch_status == "removed"
        )
        record: dict[str, object] = {
            "schemaVersion": 1,
            "claimEligible": False,
            "published": False,
            "status": "complete" if complete else "incomplete",
            "trigger": "request",
            "containerStatus": container_status,
            "mountStatus": mount_status,
            "scratchStatus": scratch_status,
            "failureCode": "none" if complete else "resource-cleanup-incomplete",
            "cleanupDeadlineSeconds": subject.SUPERVISOR_CLEANUP_DEADLINE_SECONDS,
        }
        return self.attestation or record

    def _command_options(self) -> dict[str, object]:
        return {
            "timeout_seconds": subject.CLEANUP_TIMEOUT_SECONDS,
            "maximum_output_bytes": 4096,
            "environment": {
                "PATH": subject.ENGINE_PATH,
                "HOME": self.environment["HOME"],
                "XDG_RUNTIME_DIR": self.environment["XDG_RUNTIME_DIR"],
                "TMPDIR": self.environment["RUNNER_TEMP"],
                "REGISTRY_AUTH_FILE": "/dev/null",
            },
            "cancel_event": threading.Event(),
        }


class EvidenceGateSubjectTests(unittest.TestCase):
    def setUp(self) -> None:
        if os.geteuid() == 0:
            self.skipTest("host context validation requires a non-root test process")
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="evidence-gate-subject-tests-")
        self.root = Path(self.temporary_directory.name).resolve()
        self.launch_count = 0
        self.subject_root = self.root / "trusted-subject-snapshot"
        self.subject_root.mkdir()
        self.source_diff = self.root / "source.diff"
        self.source_diff.write_bytes(b"controller captured unified diff\n")
        self.source_diff_sha256 = hashlib.sha256(self.source_diff.read_bytes()).hexdigest()
        self.runner_temp = self.root / "runner-temp"
        self.runner_temp.mkdir()
        self.home = self.root / "runner-home"
        self.home.mkdir()
        self.runtime = self.root / "runtime"
        self.runtime.mkdir(mode=0o700)
        self.environment = {
            "GITHUB_ACTIONS": "true",
            "RUNNER_ENVIRONMENT": "github-hosted",
            "GITHUB_RUN_ID": "123456",
            "GITHUB_RUN_ATTEMPT": "2",
            "RUNNER_TEMP": str(self.runner_temp),
            "HOME": str(self.home),
            "XDG_RUNTIME_DIR": str(self.runtime),
        }
        self.limits = subject.SubjectLimits(
            timeout_seconds=60,
            memory_mib=512,
            cpu_millis=1000,
            pids=64,
            output_bytes=16 * 1024,
        )
        self.executor = FakeExecutor(self.root)
        self.supervisor = FakeCleanupSupervisor(self.executor, self.environment)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def test_completed_subject_result_requires_exact_bounded_artifact_index(self) -> None:
        valid = json.loads(FakeExecutor.successful_subject_record())
        mutations = (
            lambda value: value.pop("artifacts"),
            lambda value: value["artifacts"].__setitem__(1, dict(value["artifacts"][0])),
            lambda value: value["artifacts"].reverse(),
            lambda value: value["artifacts"][0].update(relativePath="../coverage.cobertura.xml"),
            lambda value: value["artifacts"][2].update(byteCount=subject.MAX_GATE_JSON_ARTIFACT_BYTES + 1),
            lambda value: value["artifacts"][1].update(sha256="A" * 64),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                result = json.loads(json.dumps(valid))
                mutate(result)
                content = (json.dumps(result, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii")
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    subject._validate_subject_result_content(content, expected_status="completed")
                self.assertEqual("ASEGS021", caught.exception.code)

    def test_failed_subject_result_cannot_carry_artifact_index(self) -> None:
        result = json.loads(FakeExecutor.successful_subject_record())
        result["status"] = "failed"
        result["diagnostic"] = {"code": "ASESE013", "message": "artifact missing"}
        content = (json.dumps(result, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii")
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            subject._validate_subject_result_content(content, expected_status="failed")
        self.assertEqual("ASEGS018", caught.exception.code)

    def test_stopped_container_verification_requires_exited_state(self) -> None:
        invalid_states = (
            {"Running": True, "Status": "running"},
            {"Running": False, "Status": "created"},
            {"Running": 0, "Status": "exited"},
            None,
        )
        for state in invalid_states:
            with self.subTest(state=state), self.assertRaises(subject.SubjectLauncherError) as caught:
                subject._verify_container_stopped(
                    FakeExecutor.result([{"State": state}]).stdout
                )
            self.assertEqual("ASEGS012", caught.exception.code)

    def launch(self, **overrides: object) -> subject.SubjectRunResult:
        self.launch_count += 1
        export_directory = self.runner_temp / (
            "subject-artifact-export"
            if self.launch_count == 1
            else f"subject-artifact-export-{self.launch_count}"
        )
        arguments: dict[str, object] = {
            "subject_checkout": self.subject_root,
            "image_digest": IMAGE,
            "scratch_directory": self.runner_temp / "subject-scratch",
            "profile_id": "code-coverage",
            "source_diff": self.source_diff,
            "source_diff_sha256": self.source_diff_sha256,
            "artifact_export_directory": export_directory,
            "limits": self.limits,
            "_environment": self.environment,
            "_system_name": "Linux",
            "_effective_uid": os.geteuid(),
            "_engine_path": "/usr/bin/podman",
            "_host_mount_verifier": self.verify_fake_host_mount,
            "_command_executor": self.executor,
            "supervisor_client": self.supervisor,
        }
        arguments.update(overrides)
        if "supervisor_client" not in overrides:
            self.supervisor = FakeCleanupSupervisor(
                arguments["_command_executor"],  # type: ignore[arg-type]
                arguments["_environment"],  # type: ignore[arg-type]
            )
            arguments["supervisor_client"] = self.supervisor
        return subject.launch_subject(**arguments)

    def verify_fake_host_mount(self, mountpoint: Path, *, host_uid: int, host_gid: int) -> None:
        self.assertTrue(mountpoint.is_dir())
        self.assertEqual("quota-limited-scratch-" + mountpoint.name.rsplit("-", 1)[-1], mountpoint.name)
        self.assertEqual(os.geteuid(), host_uid)
        self.assertEqual(os.getegid(), host_gid)

    def test_success_uses_fixed_offline_argv_and_exact_container_envelope(self) -> None:
        hostile_command = "--entrypoint=/bin/sh -c 'cat /runner/token'"
        (self.subject_root / "subject-owned-command.txt").write_text(hostile_command, encoding="utf-8")
        result = self.launch()

        self.assertEqual(0, result.exit_code)
        self.assertEqual(b"", result.stdout)
        self.assertFalse(result.claim_eligible)
        self.assertEqual(self.runner_temp / "subject-artifact-export", result.artifact_export_directory)
        export = result.artifact_export_directory
        assert export is not None
        self.assertEqual(0o700, stat.S_IMODE(export.stat().st_mode))
        self.assertEqual(
            tuple(sorted(subject.SUBJECT_ARTIFACT_EXPORT_NAMES)),
            tuple(sorted(path.name for path in export.iterdir())),
        )
        for export_name, content in zip(subject.SUBJECT_ARTIFACT_EXPORT_NAMES, ARTIFACT_CONTENTS, strict=True):
            exported = export / export_name
            self.assertEqual(content, exported.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(exported.stat().st_mode))
        self.assertEqual(2, self.executor.inspect_count)
        self.assertLess(self.executor.lifecycle.index("start"), self.executor.lifecycle.index("inspect", 2))
        self.assertLess(self.executor.lifecycle.index("inspect", 2), self.executor.lifecycle.index("container-remove"))
        self.assertEqual(0o700, stat.S_IMODE(result.scratch_directory.stat().st_mode))
        exported_result = result.scratch_directory / subject.SUBJECT_RESULT_RELATIVE_PATH
        self.assertTrue(exported_result.is_file())
        self.assertFalse(json.loads(exported_result.read_bytes())["claimEligible"])
        self.assertEqual("completed", json.loads(exported_result.read_bytes())["status"])
        self.assertTrue(self.supervisor.requested)
        self.assertTrue(self.supervisor.request_observed_export)
        self.assertEqual("start", self.supervisor.lifecycle[0])
        self.assertEqual("request", self.supervisor.lifecycle[-1])

        podman_calls = [call for call, _ in self.executor.calls if call[0] != subject.SUDO_PATH]
        self.assertTrue(all(call[1] == "--remote=false" for call in podman_calls))
        create = next(call for call, _ in self.executor.calls if call[2] == "create")
        self.assertEqual("none", next(value.partition("=")[2] for value in create if value.startswith("--network=")))
        self.assertIn("--http-proxy=false", create)
        self.assertIn("--read-only", create)
        self.assertIn("--cap-drop=ALL", create)
        self.assertIn("--security-opt=no-new-privileges", create)
        self.assertIn("--userns=keep-id:uid=65532,gid=65532", create)
        self.assertIn("--user=65532:65532", create)
        self.assertIn("--pids-limit=64", create)
        self.assertIn("--memory=512m", create)
        self.assertIn("--cpus=1.000", create)
        self.assertIn("--unsetenv-all", create)
        self.assertIn(f"--entrypoint={subject.CONTAINER_ENTRYPOINT}", create)
        self.assertIn(IMAGE, create)
        self.assertIn("--profile-id=code-coverage", create)
        self.assertIn(f"--diff-sha256={self.source_diff_sha256}", create)
        self.assertNotIn(hostile_command, " ".join(create))
        self.assertNotIn("--entrypoint=/bin/sh", create)
        self.assertNotIn("GITHUB_TOKEN", " ".join(create))
        self.assertNotIn("subject-owned-command.txt", " ".join(create))
        owner_label_argument = (
            f"--label={subject.SUPERVISOR_OWNER_LABEL}={self.supervisor.owner_token}"
        )
        self.assertIn(owner_label_argument, create)
        self.assertFalse(
            any("token" in value.casefold() for value in create if value != owner_label_argument)
        )
        self.assertFalse(any("socket" in value.casefold() for value in create))
        self.assertEqual(3, len([value for value in create if value == "--mount"]))
        self.assertNotIn("--tmpfs", create)
        mount_arguments = [create[index + 1] for index, value in enumerate(create[:-1]) if value == "--mount"]
        self.assertIn("dst=/subject,ro=true,bind-propagation=rprivate", mount_arguments[0])
        diff_mount = next(value for value in mount_arguments if "dst=/source.diff," in value)
        self.assertIn(f"src={self.source_diff}", diff_mount)
        self.assertIn("dst=/source.diff,ro=true,bind-propagation=rprivate", diff_mount)
        scratch_mount = next(value for value in mount_arguments if "dst=/scratch," in value)
        self.assertIn("type=bind", scratch_mount)
        self.assertIn("rw=true", scratch_mount)
        self.assertIn("bind-propagation=rprivate", scratch_mount)
        self.assertTrue(scratch_mount.startswith("type=bind,src=" + str(result.scratch_directory / "quota-limited-scratch-")))
        mount_call = next(call for call, _ in self.executor.calls if call[0] == subject.SUDO_PATH and call[2] == "mount")
        self.assertEqual([subject.SUDO_PATH, "-n", "mount", "-t", "tmpfs"], mount_call[:5])
        self.assertEqual("-o", mount_call[5])
        self.assertEqual(
            "rw,nosuid,nodev,"
            f"size={subject.MAX_PROFILE_SCRATCH_BYTES},"
            f"nr_inodes={subject.MAX_PROFILE_SCRATCH_INODES},"
            f"mode=0700,uid={os.geteuid()},gid={os.getegid()}",
            mount_call[6],
        )
        self.assertEqual(["tmpfs", mount_call[-1]], mount_call[-2:])
        unmount_call = next(call for call, _ in self.executor.calls if call[0] == subject.SUDO_PATH and call[2] == "umount")
        self.assertEqual([subject.SUDO_PATH, "-n", "umount", "--", mount_call[-1]], unmount_call)
        self.assertEqual(mount_call[-1], self.executor.mount_target)
        self.assertFalse(Path(mount_call[-1]).exists())
        self.assertEqual(["mount", "container-remove", "umount"], self.executor.cleanup_order)
        self.assertTrue((result.scratch_directory / subject.SUBJECT_RESULT_RELATIVE_PATH).is_file())
        self.assertFalse(any(mountpoint.iterdir() for mountpoint in result.scratch_directory.glob("quota-limited-scratch-*")))
        self.assertFalse(any(call[2] == "cp" for call, _ in self.executor.calls))
        start_options = next(options for call, options in self.executor.calls if call[2] == "start")
        self.assertEqual(
            self.limits.output_bytes + subject.MAX_SUBJECT_RESULT_BYTES,
            start_options["maximum_output_bytes"],
        )
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in self.executor.calls))
        self.assertTrue(all(options["maximum_output_bytes"] <= subject.MAX_ENGINE_OUTPUT_BYTES for _, options in self.executor.calls))

    def test_resource_backed_profile_fails_before_engine_or_scratch(self) -> None:
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(profile_id="postgresql-integration")
        self.assertEqual("ASEGS005", caught.exception.code)
        self.assertIn("network envelope", caught.exception.message)
        self.assertEqual([], self.executor.calls)
        self.assertFalse((self.runner_temp / "subject-scratch").exists())

    def test_unknown_or_documentation_profile_cannot_launch_subject(self) -> None:
        for profile in ("documentation-only", "new-policy-command"):
            with self.subTest(profile=profile):
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    self.launch(profile_id=profile)
                self.assertEqual("ASEGS005", caught.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_unpinned_image_unsafe_scratch_and_path_symlink(self) -> None:
        with self.assertRaises(subject.SubjectLauncherError) as image_error:
            self.launch(image_digest="ghcr.io/forge-trust/appsurface:latest")
        self.assertEqual("ASEGS004", image_error.exception.code)

        with self.assertRaises(subject.SubjectLauncherError) as scratch_error:
            self.launch(scratch_directory=self.subject_root / "scratch")
        self.assertEqual("ASEGS002", scratch_error.exception.code)

        alias = self.root / "subject-alias"
        alias.symlink_to(self.subject_root, target_is_directory=True)
        with self.assertRaises(subject.SubjectLauncherError) as path_error:
            self.launch(subject_checkout=alias)
        self.assertEqual("ASEGS002", path_error.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_source_diff_digest_mismatch_before_engine_use(self) -> None:
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(source_diff_sha256="0" * 64)
        self.assertEqual("ASEGS020", caught.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_source_diff_inside_subject_checkout_before_engine_use(self) -> None:
        subject_diff = self.subject_root / "source.diff"
        content = b"head-controlled diff\n"
        subject_diff.write_bytes(content)
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(
                source_diff=subject_diff,
                source_diff_sha256=hashlib.sha256(content).hexdigest(),
            )
        self.assertEqual("ASEGS020", caught.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_missing_linked_and_oversized_source_diff_before_engine_use(self) -> None:
        missing = self.root / "missing.diff"
        linked = self.root / "linked.diff"
        linked.symlink_to(self.source_diff)
        for path in (missing, linked):
            with self.subTest(path=path), self.assertRaises(subject.SubjectLauncherError) as caught:
                self.launch(source_diff=path)
            self.assertEqual("ASEGS002", caught.exception.code)
        with mock.patch.object(subject, "MAX_SOURCE_DIFF_BYTES", 4):
            with self.assertRaises(subject.SubjectLauncherError) as oversized:
                self.launch()
        self.assertEqual("ASEGS020", oversized.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_runner_capability_gaps_and_unbounded_limits(self) -> None:
        with self.assertRaises(subject.SubjectLauncherError) as hosted_error:
            self.launch(_environment={**self.environment, "RUNNER_ENVIRONMENT": "self-hosted"})
        self.assertEqual("ASEGS003", hosted_error.exception.code)

        with self.assertRaises(subject.SubjectLauncherError) as linux_error:
            self.launch(_system_name="Darwin")
        self.assertEqual("ASEGS003", linux_error.exception.code)

        with self.assertRaises(subject.SubjectLauncherError) as limit_error:
            self.launch(limits=subject.SubjectLimits(60, 512, 1001, 64, 16384))
        self.assertEqual("ASEGS001", limit_error.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_rejects_missing_rootless_and_cgroup_capabilities(self) -> None:
        original = self.executor.__call__

        def no_rootless(arguments: list[str], **options: object) -> subject.CommandResult:
            if arguments[1:4] == ["--remote=false", "info", "--format=json"]:
                return FakeExecutor.result(
                    {
                        "host": {
                            "security": {"rootless": False},
                            "cgroupVersion": "v1",
                            "cgroupControllers": ["cpu"],
                        }
                    }
                )
            return original(arguments, **options)

        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(_command_executor=no_rootless)
        self.assertEqual("ASEGS009", caught.exception.code)
        self.assertFalse((self.runner_temp / "subject-scratch").exists())

    def test_container_configuration_mismatch_is_removed_and_never_claimed(self) -> None:
        self.executor.config_override = {"NetworkMode": "bridge"}
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS012", caught.exception.code)
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in self.executor.calls))
        self.assertFalse(any(call[2] == "start" for call, _ in self.executor.calls))

    def test_podman_capability_presentation_requires_every_default_drop_and_no_additions(self) -> None:
        # The native Podman 4.9 pilot expands --cap-drop=ALL into a list of
        # default capabilities, rather than retaining the literal ALL value.
        self.assertEqual(0, self.launch().exit_code)

        invalid_presentations = (
            {"CapDrop": sorted(subject.PODMAN_DEFAULT_CAPABILITIES - {"CAP_CHOWN"})},
            {"CapDrop": sorted(subject.PODMAN_DEFAULT_CAPABILITIES) + ["CAP_CHOWN"]},
            {"CapAdd": ["CAP_CHOWN"]},
        )
        for index, override in enumerate(invalid_presentations):
            with self.subTest(override=override):
                executor = FakeExecutor(self.root)
                executor.config_override = override
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    self.launch(
                        scratch_directory=self.runner_temp / f"scratch-invalid-cap-{index}",
                        _command_executor=executor,
                    )
                self.assertEqual("ASEGS012", caught.exception.code)
                self.assertFalse(any(call[2] == "start" for call, _ in executor.calls))

        literal_executor = FakeExecutor(self.root)
        literal_executor.config_override = {"CapDrop": ["ALL"]}
        self.assertEqual(
            0,
            self.launch(
                scratch_directory=self.runner_temp / "scratch-literal-cap-drop",
                _command_executor=literal_executor,
            ).exit_code,
        )

    def test_scratch_bind_inspection_rejects_untrusted_source_access_and_propagation(self) -> None:
        overrides = (
            {"Source": str(self.runner_temp)},
            {"RW": False},
            {"Propagation": "rshared"},
            {"Type": "tmpfs"},
        )
        for index, override in enumerate(overrides):
            with self.subTest(override=override):
                executor = FakeExecutor(self.root)
                executor.scratch_mount_override = override
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    self.launch(
                        scratch_directory=self.runner_temp / f"scratch-invalid-{index}",
                        _command_executor=executor,
                    )
                self.assertEqual("ASEGS012", caught.exception.code)
                self.assertFalse(any(call[2] == "start" for call, _ in executor.calls))
                self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in executor.calls))

        executor = FakeExecutor(self.root)
        executor.config_override = {"Tmpfs": {"/scratch": "unexpected"}}
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(scratch_directory=self.runner_temp / "scratch-unexpected-tmpfs", _command_executor=executor)
        self.assertEqual("ASEGS012", caught.exception.code)
        self.assertFalse(any(call[2] == "start" for call, _ in executor.calls))

    def test_successful_subject_requires_a_bounded_nonclaiming_result_export(self) -> None:
        self.executor.result_export = b'{"claimEligible":true,"profileId":"code-coverage","schemaVersion":1,"status":"completed","steps":[]}\n'
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS018", caught.exception.code)
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in self.executor.calls))

        executor = FakeExecutor(self.root)
        executor.result_export = b"x" * (subject.MAX_SUBJECT_RESULT_BYTES + 1)
        with self.assertRaises(subject.SubjectLauncherError) as oversized_error:
            self.launch(scratch_directory=self.runner_temp / "scratch-export-oversized", _command_executor=executor)
        self.assertEqual("ASEGS018", oversized_error.exception.code)
        self.assertFalse(
            (self.runner_temp / "scratch-export-oversized" / subject.SUBJECT_RESULT_RELATIVE_PATH).exists()
        )
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in executor.calls))

        executor = FakeExecutor(self.root)
        executor.result_export = None
        with self.assertRaises(subject.SubjectLauncherError) as missing_error:
            self.launch(scratch_directory=self.runner_temp / "scratch-export-missing", _command_executor=executor)
        self.assertEqual("ASEGS018", missing_error.exception.code)
        self.assertFalse(
            (self.runner_temp / "scratch-export-missing" / subject.SUBJECT_RESULT_RELATIVE_PATH).exists()
        )
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in executor.calls))

    def test_artifact_export_rejects_unsafe_or_changed_source_and_removes_partial_copy(self) -> None:
        def mutate_missing(root: Path) -> None:
            (root / subject.SUBJECT_ARTIFACTS[0][1]).unlink()

        def mutate_symlink(root: Path) -> None:
            artifact = root / subject.SUBJECT_ARTIFACTS[0][1]
            saved = artifact.with_name("original.xml")
            artifact.rename(saved)
            artifact.symlink_to(saved.name)

        def mutate_hardlink(root: Path) -> None:
            artifact = root / subject.SUBJECT_ARTIFACTS[0][1]
            os.link(artifact, artifact.with_name("second-link.xml"))

        def mutate_digest(root: Path) -> None:
            artifact = root / subject.SUBJECT_ARTIFACTS[1][1]
            artifact.write_bytes(b"x" * len(ARTIFACT_CONTENTS[1]))

        def mutate_oversized(root: Path) -> None:
            artifact = root / subject.SUBJECT_ARTIFACTS[2][1]
            with artifact.open("r+b") as output:
                output.truncate(subject.MAX_GATE_JSON_ARTIFACT_BYTES + 1)

        for index, mutation in enumerate(
            (mutate_missing, mutate_symlink, mutate_hardlink, mutate_digest, mutate_oversized)
        ):
            with self.subTest(mutation=mutation.__name__):
                executor = FakeExecutor(self.root)
                executor.artifact_mutation = mutation
                exported = self.runner_temp / f"unsafe-export-{index}"
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    self.launch(
                        scratch_directory=self.runner_temp / f"unsafe-scratch-{index}",
                        artifact_export_directory=exported,
                        _command_executor=executor,
                    )
                self.assertEqual("ASEGS021", caught.exception.code)
                self.assertFalse(exported.exists())
                self.assertTrue(self.supervisor.requested)
                self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in executor.calls))

    def test_successful_subject_requires_every_fixed_zero_exit_step(self) -> None:
        valid = json.loads(FakeExecutor.successful_subject_record())
        mutations = (
            lambda value: value.update(steps=[]),
            lambda value: value["steps"].pop(),
            lambda value: value["steps"][2].update(name="unexpected-step"),
            lambda value: value["steps"][2].update(exitCode=1),
            lambda value: value["steps"][0].update(stdoutSha256="not-a-digest"),
            lambda value: value["steps"][0].update(outputBytes=True),
            lambda value: value["steps"][0].update(unexpected=True),
        )
        for index, mutate in enumerate(mutations):
            with self.subTest(mutation=index):
                value = json.loads(json.dumps(valid))
                mutate(value)
                executor = FakeExecutor(self.root)
                executor.result_export = (
                    json.dumps(value, separators=(",", ":"), sort_keys=True) + "\n"
                ).encode("ascii")
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    self.launch(
                        scratch_directory=self.runner_temp / f"scratch-step-proof-{index}",
                        _command_executor=executor,
                    )
                self.assertEqual("ASEGS018", caught.exception.code)

    def test_failed_subject_exports_its_typed_nonclaiming_result_record(self) -> None:
        self.executor.start_exit_code = 2
        self.executor.result_export = (
            b'{"claimEligible":false,"diagnostic":{"code":"ASESE010","message":"coverage unavailable"},'
            b'"profileId":"code-coverage","schemaVersion":1,"status":"failed","steps":[]}\n'
        )
        result = self.launch()

        self.assertEqual(2, result.exit_code)
        self.assertFalse(result.claim_eligible)
        record = json.loads((result.scratch_directory / subject.SUBJECT_RESULT_RELATIVE_PATH).read_bytes())
        self.assertEqual("failed", record["status"])
        self.assertEqual("ASESE010", record["diagnostic"]["code"])

    def test_cancellation_during_subject_process_still_cleans_container(self) -> None:
        self.executor.fail_start = subject._ProcessCancelled()
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS013", caught.exception.code)
        cleanup = next(call for call, _ in self.executor.calls if call[2] == "rm")
        self.assertIn("--force", cleanup)

    def test_pre_cancelled_request_starts_no_engine_process(self) -> None:
        cancelled = threading.Event()
        cancelled.set()
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(cancel_event=cancelled)
        self.assertEqual("ASEGS013", caught.exception.code)
        self.assertEqual([], self.executor.calls)

    def test_cleanup_failure_invalidates_even_a_zero_exit_run(self) -> None:
        self.executor.fail_cleanup = True
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS017", caught.exception.code)
        self.assertEqual(["mount", "container-remove"], self.executor.cleanup_order)
        self.assertTrue(Path(self.executor.mount_target or "").is_dir())

    def test_unready_supervisor_stops_before_mount_or_container_creation(self) -> None:
        self.supervisor = FakeCleanupSupervisor(self.executor, self.environment, ready=False)

        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(supervisor_client=self.supervisor)

        self.assertEqual("ASEGS017", caught.exception.code)
        self.assertTrue(self.supervisor.started)
        self.assertFalse(self.supervisor.requested)
        self.assertFalse(any(call[0] == subject.SUDO_PATH for call, _ in self.executor.calls))
        self.assertFalse(any(len(call) > 2 and call[2] == "create" for call, _ in self.executor.calls))

    def test_missing_container_owner_label_fails_before_start_and_still_requests_cleanup(self) -> None:
        self.executor.labels_override = {}

        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()

        self.assertEqual("ASEGS012", caught.exception.code)
        self.assertTrue(self.supervisor.requested)
        self.assertFalse(any(len(call) > 2 and call[2] == "start" for call, _ in self.executor.calls))

    def test_incomplete_supervisor_attestation_invalidates_zero_exit_execution(self) -> None:
        self.supervisor = FakeCleanupSupervisor(
            self.executor,
            self.environment,
            attestation={
                "schemaVersion": 1,
                "claimEligible": False,
                "published": False,
                "status": "incomplete",
                "trigger": "request",
                "containerStatus": "removed",
                "mountStatus": "unmounted",
                "scratchStatus": "preserved",
                "failureCode": "resource-cleanup-incomplete",
                "cleanupDeadlineSeconds": subject.SUPERVISOR_CLEANUP_DEADLINE_SECONDS,
            },
        )

        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch(supervisor_client=self.supervisor)

        self.assertEqual("ASEGS017", caught.exception.code)
        self.assertTrue(self.supervisor.requested)
        self.assertTrue(self.supervisor.request_observed_export)

    def test_host_tmpfs_mount_failure_stops_before_container_creation(self) -> None:
        self.executor.fail_mount = True

        with mock.patch.object(subject, "_host_scratch_mount_is_present", return_value=False):
            with self.assertRaises(subject.SubjectLauncherError) as caught:
                self.launch()

        self.assertEqual("ASEGS019", caught.exception.code)
        self.assertEqual(["mount"], self.executor.cleanup_order)
        self.assertFalse(any(call[2] == "create" for call, _ in self.executor.calls))
        self.assertFalse(Path(self.executor.mount_target or "").exists())

        partial_executor = FakeExecutor(self.root)
        partial_executor.fail_mount = True
        with mock.patch.object(subject, "_host_scratch_mount_is_present", return_value=True):
            with self.assertRaises(subject.SubjectLauncherError) as partial_failure:
                self.launch(
                    scratch_directory=self.runner_temp / "scratch-partial-mount",
                    _command_executor=partial_executor,
                )

        self.assertEqual("ASEGS019", partial_failure.exception.code)
        self.assertEqual(["mount", "umount"], partial_executor.cleanup_order)
        self.assertFalse(any(call[2] == "create" for call, _ in partial_executor.calls))
        self.assertFalse(Path(partial_executor.mount_target or "").exists())

    def test_host_tmpfs_unmount_failure_invalidates_run_and_keeps_mountpoint(self) -> None:
        self.executor.fail_unmount = True

        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()

        self.assertEqual("ASEGS017", caught.exception.code)
        self.assertEqual(["mount", "container-remove", "umount"], self.executor.cleanup_order)
        mountpoint = Path(self.executor.mount_target or "")
        self.assertTrue(mountpoint.is_dir())
        self.assertTrue((mountpoint.parent / subject.SUBJECT_RESULT_RELATIVE_PATH).is_file())

    def test_mountinfo_parser_decodes_mountpoint_and_checks_live_tmpfs_limits(self) -> None:
        mountpoint = self.root / "scratch mount"
        mountpoint.mkdir(mode=0o700)
        os.chmod(mountpoint, 0o700)
        mountinfo_path = self.root / "mountinfo"
        escaped_mountpoint = str(mountpoint).replace(" ", r"\040")

        def write_mountinfo(*, inode_limit: int = subject.MAX_PROFILE_SCRATCH_INODES) -> None:
            mountinfo_path.write_text(
                "41 25 0:38 / "
                f"{escaped_mountpoint} rw,nosuid,nodev - tmpfs tmpfs "
                f"rw,nosuid,nodev,size={subject.MAX_PROFILE_SCRATCH_BYTES},"
                f"nr_inodes={inode_limit},mode=700,uid={os.geteuid()},gid={os.getegid()}\n",
                encoding="utf-8",
            )

        filesystem_stats = SimpleNamespace(
            f_blocks=subject.MAX_PROFILE_SCRATCH_BYTES // 4096,
            f_frsize=4096,
            f_files=subject.MAX_PROFILE_SCRATCH_INODES,
        )
        with mock.patch.object(subject, "MOUNTINFO_PATH", mountinfo_path):
            with mock.patch.object(subject.os, "statvfs", return_value=filesystem_stats):
                write_mountinfo()
                self.assertTrue(subject._host_scratch_mount_is_present(mountpoint))
                subject._verify_host_scratch_mount(
                    mountpoint,
                    host_uid=os.geteuid(),
                    host_gid=os.getegid(),
                )

                write_mountinfo(inode_limit=subject.MAX_PROFILE_SCRATCH_INODES + 1)
                with self.assertRaises(subject.SubjectLauncherError) as caught:
                    subject._verify_host_scratch_mount(
                        mountpoint,
                        host_uid=os.geteuid(),
                        host_gid=os.getegid(),
                    )
                self.assertEqual("ASEGS019", caught.exception.code)

                mountinfo_path.write_text("malformed mountinfo\n", encoding="utf-8")
                with self.assertRaises(subject.SubjectLauncherError) as malformed:
                    subject._host_scratch_mount_is_present(mountpoint)
                self.assertEqual("ASEGS019", malformed.exception.code)

    def test_bounded_output_rejection_is_preserved_and_container_is_cleaned(self) -> None:
        self.executor.fail_start = subject._ProcessOutputExceeded()
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS015", caught.exception.code)
        self.assertTrue(any(call[2] == "rm" for call, _ in self.executor.calls))

        executor = FakeExecutor(self.root)
        executor.start_stderr = b"x" * (self.limits.output_bytes + 1)
        with self.assertRaises(subject.SubjectLauncherError) as stderr_error:
            self.launch(scratch_directory=self.runner_temp / "scratch-stderr-overflow", _command_executor=executor)
        self.assertEqual("ASEGS015", stderr_error.exception.code)
        self.assertTrue(any(call[2:4] == ["rm", "--force"] for call, _ in executor.calls))

    def test_process_executor_uses_argv_and_kills_process_group_on_cancellation(self) -> None:
        class FakeProcess:
            pid = 43210

            def __init__(self) -> None:
                stdout_read, stdout_write = os.pipe()
                stderr_read, stderr_write = os.pipe()
                os.close(stdout_write)
                os.close(stderr_write)
                self.stdout = os.fdopen(stdout_read, "rb", buffering=0)
                self.stderr = os.fdopen(stderr_read, "rb", buffering=0)
                self.returncode: int | None = None

            def poll(self) -> int | None:
                return self.returncode

            def wait(self, timeout: float) -> int:
                self.returncode = -15
                return self.returncode

            def terminate(self) -> None:
                self.returncode = -15

            def kill(self) -> None:
                self.returncode = -9

        process = FakeProcess()
        cancellation = threading.Event()
        cancellation.set()
        with mock.patch.object(subject.subprocess, "Popen", return_value=process) as popen:
            with mock.patch.object(subject.os, "killpg") as kill_group:
                with self.assertRaises(subject._ProcessCancelled):
                    subject._run_command(
                        ["/usr/bin/podman", "--remote=false", "start", "--attach", "fixed-id"],
                        timeout_seconds=10,
                        maximum_output_bytes=4096,
                        environment={"PATH": subject.ENGINE_PATH},
                        cancel_event=cancellation,
                    )
        self.assertEqual([signal.SIGTERM, signal.SIGKILL], [call.args[1] for call in kill_group.call_args_list])
        self.assertEqual(["/usr/bin/podman", "--remote=false", "start", "--attach", "fixed-id"], popen.call_args.args[0])
        self.assertFalse(popen.call_args.kwargs["shell"])
        self.assertTrue(popen.call_args.kwargs["start_new_session"])
        self.assertEqual("/", popen.call_args.kwargs["cwd"])

    def test_process_executor_enforces_combined_output_budget(self) -> None:
        class FakeProcess:
            pid = 54321

            def __init__(self) -> None:
                stdout_read, stdout_write = os.pipe()
                stderr_read, stderr_write = os.pipe()
                os.write(stdout_write, b"12345")
                os.close(stdout_write)
                os.close(stderr_write)
                self.stdout = os.fdopen(stdout_read, "rb", buffering=0)
                self.stderr = os.fdopen(stderr_read, "rb", buffering=0)
                self.returncode: int | None = None

            def poll(self) -> int | None:
                return self.returncode

            def wait(self, timeout: float) -> int:
                self.returncode = -15
                return self.returncode

            def terminate(self) -> None:
                self.returncode = -15

            def kill(self) -> None:
                self.returncode = -9

        process = FakeProcess()
        with mock.patch.object(subject.subprocess, "Popen", return_value=process):
            with mock.patch.object(subject.os, "killpg") as kill_group:
                with self.assertRaises(subject._ProcessOutputExceeded):
                    subject._run_command(
                        ["/usr/bin/podman", "inspect"],
                        timeout_seconds=10,
                        maximum_output_bytes=4,
                        environment={"PATH": subject.ENGINE_PATH},
                        cancel_event=threading.Event(),
                    )
        self.assertTrue(kill_group.called)

    def test_cli_never_returns_a_green_gate_status_for_a_zero_exit_subject(self) -> None:
        completed = subject.SubjectRunResult(0, b"", b"", self.runner_temp)
        output = io.StringIO()
        with mock.patch.object(subject, "launch_subject", return_value=completed):
            with redirect_stdout(output):
                status = subject.main(
                    [
                        "--subject-checkout", str(self.subject_root),
                        "--image-digest", IMAGE,
                        "--scratch-directory", str(self.runner_temp / "cli-scratch"),
                        "--profile-id", "code-coverage",
                        "--source-diff", str(self.source_diff),
                        "--source-diff-sha256", self.source_diff_sha256,
                        "--artifact-export-directory", str(self.runner_temp / "cli-artifact-export"),
                        "--timeout-seconds", "60",
                        "--memory-mib", "512",
                        "--cpu-millis", "1000",
                        "--pids", "64",
                        "--output-bytes", "16384",
                    ]
                )
        self.assertEqual(2, status)
        self.assertFalse(json.loads(output.getvalue())["claimEligible"])


if __name__ == "__main__":
    unittest.main()
