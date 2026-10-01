"""Focused isolation and failure-path tests for the subject OCI launcher."""

from __future__ import annotations

import importlib.util
import io
import json
import os
from pathlib import Path
import signal
import stat
import sys
import tempfile
import threading
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


class FakeExecutor:
    """Return deterministic Podman JSON while retaining every fixed argv."""

    def __init__(self, root: Path, image: str = IMAGE) -> None:
        self.root = root
        self.image = image
        self.calls: list[tuple[list[str], dict[str, object]]] = []
        self.container_name: str | None = None
        self.fail_start: BaseException | None = None
        self.fail_cleanup = False
        self.config_override: dict[str, object] | None = None

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
            return self.container_inspect()
        if command[1] == "start":
            if self.fail_start is not None:
                raise self.fail_start
            return subject.CommandResult(0, b"subject output", b"")
        if command[1:3] == ["rm", "--force"]:
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

    def container_inspect(self) -> subject.CommandResult:
        assert self.container_name is not None
        create_call = next(call for call, _ in self.calls if len(call) > 2 and call[2] == "create")
        mounts = [create_call[index + 1] for index, value in enumerate(create_call[:-1]) if value == "--mount"]
        subject_mount = next(value for value in mounts if "dst=/subject," in value)
        scratch_mount = next(value for value in mounts if "dst=/scratch," in value)
        subject_source = next(value.split("=", 1)[1] for value in subject_mount.split(",") if value.startswith("src="))
        scratch_source = next(value.split("=", 1)[1] for value in scratch_mount.split(",") if value.startswith("src="))
        create_arguments = create_call
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
                    "Cmd": list(subject.PROFILE_ENTRYPOINT_ARGUMENTS["code-coverage"]),
                    "User": "65532:65532",
                    "WorkingDir": "/subject",
                    "Env": environment,
                },
                "HostConfig": {
                    "ReadonlyRootfs": True,
                    "CapDrop": ["ALL"],
                    "SecurityOpt": ["no-new-privileges"],
                    "NetworkMode": "none",
                    "PidsLimit": pids,
                    "Memory": memory_mib * 1024 * 1024,
                    "NanoCpus": cpu_millis * 1_000_000,
                    "Privileged": False,
                    "Devices": [],
                    "PortBindings": {},
                    "PidMode": "private",
                    "IpcMode": "private",
                    "CgroupnsMode": "private",
                    "UsernsMode": "keep-id:uid=65532,gid=65532",
                },
                "Mounts": [
                    {"Type": "bind", "Source": subject_source, "Destination": "/subject", "RW": False},
                    {"Type": "bind", "Source": scratch_source, "Destination": "/scratch", "RW": True},
                ],
            }
        ]
        if self.config_override:
            document[0]["HostConfig"].update(self.config_override)
        return self.result(document)


class EvidenceGateSubjectTests(unittest.TestCase):
    def setUp(self) -> None:
        if os.geteuid() == 0:
            self.skipTest("host context validation requires a non-root test process")
        self.temporary_directory = tempfile.TemporaryDirectory(prefix="evidence-gate-subject-tests-")
        self.root = Path(self.temporary_directory.name).resolve()
        self.subject_root = self.root / "trusted-subject-snapshot"
        self.subject_root.mkdir()
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

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def launch(self, **overrides: object) -> subject.SubjectRunResult:
        arguments: dict[str, object] = {
            "subject_checkout": self.subject_root,
            "image_digest": IMAGE,
            "scratch_directory": self.runner_temp / "subject-scratch",
            "profile_id": "code-coverage",
            "limits": self.limits,
            "_environment": self.environment,
            "_system_name": "Linux",
            "_effective_uid": os.geteuid(),
            "_engine_path": "/usr/bin/podman",
            "_command_executor": self.executor,
        }
        arguments.update(overrides)
        return subject.launch_subject(**arguments)

    def test_success_uses_fixed_offline_argv_and_exact_container_envelope(self) -> None:
        hostile_command = "--entrypoint=/bin/sh -c 'cat /runner/token'"
        (self.subject_root / "subject-owned-command.txt").write_text(hostile_command, encoding="utf-8")
        result = self.launch()

        self.assertEqual(0, result.exit_code)
        self.assertEqual(b"subject output", result.stdout)
        self.assertFalse(result.claim_eligible)
        self.assertEqual(0o700, stat.S_IMODE(result.scratch_directory.stat().st_mode))

        self.assertTrue(all(call[1] == "--remote=false" for call, _ in self.executor.calls))
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
        self.assertNotIn(hostile_command, " ".join(create))
        self.assertNotIn("--entrypoint=/bin/sh", create)
        self.assertNotIn("GITHUB_TOKEN", " ".join(create))
        self.assertNotIn("subject-owned-command.txt", " ".join(create))
        self.assertFalse(any("token" in value.casefold() or "socket" in value.casefold() for value in create))
        self.assertEqual(2, len([value for value in create if value == "--mount"]))
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

    def test_bounded_output_rejection_is_preserved_and_container_is_cleaned(self) -> None:
        self.executor.fail_start = subject._ProcessOutputExceeded()
        with self.assertRaises(subject.SubjectLauncherError) as caught:
            self.launch()
        self.assertEqual("ASEGS015", caught.exception.code)
        self.assertTrue(any(call[2] == "rm" for call, _ in self.executor.calls))

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
