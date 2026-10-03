"""Failure-focused tests for the fixed offline subject entrypoint."""

from __future__ import annotations

import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-subject-entrypoint.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_subject_entrypoint", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
entrypoint = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = entrypoint
SPEC.loader.exec_module(entrypoint)


def process_result(exit_code: int = 0, stdout: bytes = b"ok", stderr: bytes = b"") -> entrypoint.ProcessResult:
    return entrypoint.ProcessResult(exit_code, stdout, stderr)


class FixedOfflineSubjectEntrypointTests(unittest.TestCase):
    def invocation(self) -> entrypoint.Invocation:
        return entrypoint.Invocation(
            entrypoint.FIXED_PROFILE_ID,
            entrypoint.SUBJECT_ROOT,
            entrypoint.SCRATCH_ROOT,
            entrypoint.DEPENDENCY_SOURCE,
            entrypoint.PATCH_DIFF_PATH,
            hashlib.sha256(b"captured unified diff\n").hexdigest(),
            True,
        )

    @staticmethod
    def mountinfo(subject: Path, scratch: Path, dependencies: Path, *, diff_options: str = "ro") -> str:
        return "\n".join(
            (
                f"1 0 0:1 / {subject} ro - bind none ro",
                f"2 0 0:2 / {scratch} rw - bind none rw",
                f"3 0 0:3 / {dependencies} ro - bind none ro",
                f"4 0 0:4 / {entrypoint.PATCH_DIFF_PATH} {diff_options} - bind none {diff_options}",
            )
        )

    @staticmethod
    def make_subject(
        root: Path,
        *,
        include_lock: bool = True,
        runtime_identifier_lock: bool = False,
    ) -> None:
        project = root / "Cli/ForgeTrust.AppSurface.Cli"
        project.mkdir(parents=True)
        (root / "ForgeTrust.AppSurface.slnx").write_text(
            '<Solution><Project Path="Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj" /></Solution>',
            encoding="utf-8",
        )
        project_file = project / "ForgeTrust.AppSurface.Cli.csproj"
        project_file.write_text(
            "<Project><PropertyGroup><NuGetLockFilePath>"
            "packages.$(NETCoreSdkRuntimeIdentifier).lock.json"
            "</NuGetLockFilePath></PropertyGroup></Project>"
            if runtime_identifier_lock
            else "<Project />",
            encoding="utf-8",
        )
        if include_lock:
            lock_name = "packages.linux-x64.lock.json" if runtime_identifier_lock else "packages.lock.json"
            (project / lock_name).write_text(
                '{"version":1,"dependencies":{"net10.0":{"Example.Package":{"type":"Direct","resolved":"1.0.0","contentHash":"x"}}}}',
                encoding="utf-8",
            )
        (root / "subject.txt").write_text("fixed source", encoding="utf-8")

    @staticmethod
    def make_feed(root: Path) -> None:
        root.mkdir(parents=True)
        (root / "example.package.1.0.0.nupkg").write_bytes(b"locked-package-payload")

    def patch_paths(self, subject: Path, scratch: Path, feed: Path) -> mock._patch:
        diff_path = subject.parent / "source.diff"
        diff_path.write_bytes(b"captured unified diff\n")
        combined = mock.patch.multiple(
            entrypoint,
            SUBJECT_ROOT=subject,
            SCRATCH_ROOT=scratch,
            DEPENDENCY_SOURCE=feed,
            PATCH_DIFF_PATH=diff_path,
            FIXED_SUBJECT_ARGUMENT=f"--subject-root={subject}",
            FIXED_SCRATCH_ARGUMENT=f"--scratch-root={scratch}",
            FIXED_DEPENDENCY_ARGUMENT=f"--dependency-source={feed}",
        )
        return combined

    def prepared_roots(self, parent: Path) -> tuple[Path, Path, Path]:
        subject = parent / "subject"
        scratch = parent / "scratch"
        feed = parent / "locked-dependencies"
        subject.mkdir()
        scratch.mkdir(mode=0o700)
        scratch.chmod(0o700)
        self.make_feed(feed)
        return subject, scratch, feed

    def test_invocation_accepts_only_the_launcher_fixed_contract(self) -> None:
        digest = hashlib.sha256(b"captured unified diff\n").hexdigest()
        invocation = entrypoint.parse_invocation(
            [
                "--profile-id=code-coverage",
                "--subject-root=/subject",
                "--scratch-root=/scratch",
                "--dependency-source=/opt/appsurface/locked-dependencies",
                "--diff-file=/source.diff",
                f"--diff-sha256={digest}",
                "--offline",
            ]
        )
        self.assertEqual("code-coverage", invocation.profile_id)
        self.assertEqual(digest, invocation.diff_sha256)
        self.assertEqual(Path("/source.diff"), invocation.diff_file)
        self.assertTrue(invocation.offline)
        with self.assertRaises(entrypoint.EntrypointError):
            entrypoint.parse_invocation(
                [
                    "--profile-id=code-coverage", "--subject-root=/subject", "--scratch-root=/scratch",
                    "--dependency-source=/opt/appsurface/locked-dependencies", "--diff-file=/source.diff",
                    f"--diff-sha256={digest}\\Z", "--offline",
                ]
            )
        for invalid in (
            [
                "--profile-id=code-coverage", "--subject-root=/tmp/subject", "--scratch-root=/scratch",
                "--dependency-source=/opt/appsurface/locked-dependencies", "--diff-file=/source.diff",
                f"--diff-sha256={digest}", "--offline",
            ],
            [
                "--profile-id=code-coverage", "--subject-root=/subject", "--scratch-root=/scratch",
                "--dependency-source=/opt/appsurface/locked-dependencies", "--diff-file=/source.diff",
                f"--diff-sha256={digest}",
            ],
            [
                "--profile-id=code-coverage", "--subject-root=/subject", "--scratch-root=/scratch",
                "--dependency-source=/opt/appsurface/locked-dependencies", "--diff-file=/source.diff",
                f"--diff-sha256={digest}", "--offline", "dotnet", "restore",
            ],
        ):
            with self.subTest(argv=invalid), self.assertRaises(SystemExit):
                entrypoint.parse_invocation(invalid)
        with self.assertRaises(entrypoint.EntrypointError):
            entrypoint.parse_invocation(
                [
                    "--profile-id=code-coverage", "--subject-root=/subject", "--scratch-root=/scratch",
                    "--dependency-source=/opt/appsurface/locked-dependencies", "--diff-file=/source.diff",
                    "--diff-sha256=" + "z" * 64, "--offline",
                ]
            )

    def test_mount_validation_rejects_writable_subject_and_public_scratch(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            subject, scratch, feed = self.prepared_roots(Path(temporary).resolve())
            self.make_subject(subject)
            with self.patch_paths(subject, scratch, feed):
                invocation = self.invocation()
                cases = (
                    (self.mountinfo(subject, scratch, feed).replace(f"{subject} ro", f"{subject} rw"), "read-only"),
                    (self.mountinfo(subject, scratch, feed, diff_options="rw"), "source diff mount is not read-only"),
                )
                for mountinfo, message in cases:
                    with self.subTest(message=message), self.assertRaisesRegex(entrypoint.EntrypointError, message):
                        entrypoint.validate_mounts(
                            subject,
                            scratch,
                            feed,
                            invocation.diff_file,
                            invocation.diff_sha256,
                            mountinfo_text=mountinfo,
                            effective_uid=os.geteuid(),
                        )
                scratch.chmod(0o755)
                with self.assertRaisesRegex(entrypoint.EntrypointError, "scratch directory is not private"):
                    entrypoint.validate_mounts(
                        subject,
                        scratch,
                        feed,
                        invocation.diff_file,
                        invocation.diff_sha256,
                        mountinfo_text=self.mountinfo(subject, scratch, feed),
                        effective_uid=os.geteuid(),
                    )

    def test_mount_validation_rejects_source_diff_digest_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            subject, scratch, feed = self.prepared_roots(Path(temporary).resolve())
            self.make_subject(subject)
            with self.patch_paths(subject, scratch, feed):
                invocation = self.invocation()
                invocation.diff_file.write_bytes(b"different captured diff\n")
                with self.assertRaises(entrypoint.EntrypointError) as failure:
                    entrypoint.validate_mounts(
                        subject,
                        scratch,
                        feed,
                        invocation.diff_file,
                        invocation.diff_sha256,
                        mountinfo_text=self.mountinfo(subject, scratch, feed),
                        effective_uid=os.geteuid(),
                    )
            self.assertEqual("ASESE012", failure.exception.code)

    def test_mount_validation_rejects_missing_source_diff(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            subject, scratch, feed = self.prepared_roots(Path(temporary).resolve())
            self.make_subject(subject)
            with self.patch_paths(subject, scratch, feed):
                invocation = self.invocation()
                invocation.diff_file.unlink()
                with self.assertRaises(entrypoint.EntrypointError) as failure:
                    entrypoint.validate_mounts(
                        subject,
                        scratch,
                        feed,
                        invocation.diff_file,
                        invocation.diff_sha256,
                        mountinfo_text=self.mountinfo(subject, scratch, feed),
                        effective_uid=os.geteuid(),
                    )
            self.assertEqual("ASESE012", failure.exception.code)

    def test_missing_offline_package_feed_writes_bounded_failure_without_running_dotnet(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject)
            (feed / "example.package.1.0.0.nupkg").unlink()
            scratch.rmdir()
            scratch.mkdir(mode=0o700)
            scratch.chmod(0o700)
            runner = mock.Mock(side_effect=AssertionError("runtime must not start"))
            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )
            self.assertEqual(2, code)
            runner.assert_not_called()
            result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["status"])
            self.assertEqual("ASESE002", result["diagnostic"]["code"])
            self.assertLess((scratch / entrypoint.RESULT_RELATIVE_PATH).stat().st_size, entrypoint.MAX_RESULT_BYTES)

    def test_dependency_feed_scans_every_entry_after_first_package(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            feed = Path(temporary).resolve() / "locked-dependencies"
            self.make_feed(feed)
            nested = feed / "nested"
            nested.mkdir()
            (nested / "second.1.0.0.nupkg").write_bytes(b"second locked package")

            entrypoint._validate_dependency_feed(feed)

            (nested / "unsafe-link").symlink_to(feed / "example.package.1.0.0.nupkg")
            with self.assertRaises(entrypoint.EntrypointError) as failure:
                entrypoint._validate_dependency_feed(feed)
            self.assertEqual("ASESE002", failure.exception.code)

    def test_unlocked_solution_project_fails_before_restore(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject, include_lock=False)
            calls: list[list[str]] = []

            def runner(arguments: list[str], **_options: object) -> entrypoint.ProcessResult:
                calls.append(arguments)
                if arguments == ["dotnet", "--version"]:
                    return process_result(stdout=b"10.0.100\n")
                if arguments == ["dotnet", "--list-runtimes"]:
                    return process_result(stdout=b"Microsoft.NETCore.App 10.0.2 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n")
                self.fail(f"Unexpected command before lock validation: {arguments!r}")

            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )
            self.assertEqual(2, code)
            self.assertEqual([["dotnet", "--version"], ["dotnet", "--list-runtimes"]], calls)
            result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
            self.assertEqual("ASESE002", result["diagnostic"]["code"])
            self.assertFalse(result["claimEligible"])

    def test_subject_nuget_configuration_is_rejected_before_restore(self) -> None:
        for relative_path in ("NuGet.Config", "nested/nuget.config"):
            with self.subTest(relative_path=relative_path), tempfile.TemporaryDirectory() as temporary:
                parent = Path(temporary).resolve()
                subject, scratch, feed = self.prepared_roots(parent)
                self.make_subject(subject)
                controlled_config = subject / relative_path
                controlled_config.parent.mkdir(parents=True, exist_ok=True)
                controlled_config.write_text("<configuration />", encoding="utf-8")
                calls: list[list[str]] = []

                def runner(arguments: list[str], **_options: object) -> entrypoint.ProcessResult:
                    calls.append(arguments)
                    if arguments == ["dotnet", "--version"]:
                        return process_result(stdout=b"10.0.100\n")
                    if arguments == ["dotnet", "--list-runtimes"]:
                        return process_result(stdout=b"Microsoft.NETCore.App 10.0.2 [/dotnet]\n")
                    self.fail(f"Restore must not run with subject NuGet configuration: {arguments!r}")

                with self.patch_paths(subject, scratch, feed):
                    code = entrypoint.execute(
                        self.invocation(),
                        mountinfo_text=self.mountinfo(subject, scratch, feed),
                        effective_uid=os.geteuid(),
                        runner=runner,
                    )

                self.assertEqual(2, code)
                self.assertEqual([["dotnet", "--version"], ["dotnet", "--list-runtimes"]], calls)
                result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
                self.assertEqual("ASESE003", result["diagnostic"]["code"])
                self.assertFalse(result["claimEligible"])

    def test_project_declared_linux_runtime_lock_is_used(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            staged = parent / "staged"
            staged.mkdir()
            self.make_subject(staged, runtime_identifier_lock=True)

            entrypoint._validate_solution_locks(staged)

            project = staged / "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
            self.assertEqual(
                project.parent / "packages.linux-x64.lock.json",
                entrypoint._project_lock_path(project),
            )

    def test_project_declared_missing_runtime_lock_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            staged = Path(temporary).resolve() / "staged"
            staged.mkdir()
            self.make_subject(staged, include_lock=False, runtime_identifier_lock=True)

            with self.assertRaisesRegex(entrypoint.EntrypointError, "dependency lock"):
                entrypoint._validate_solution_locks(staged)

    def test_container_dependent_solution_fails_before_coverage(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject)
            unsupported_project = subject / entrypoint.OFFLINE_UNSUPPORTED_TEST_PROJECTS[0]
            unsupported_project.parent.mkdir(parents=True)
            unsupported_project.write_text("<Project />", encoding="utf-8")
            calls: list[list[str]] = []

            def runner(arguments: list[str], **_options: object) -> entrypoint.ProcessResult:
                calls.append(arguments)
                if arguments == ["dotnet", "--version"]:
                    return process_result(stdout=b"10.0.100\n")
                if arguments == ["dotnet", "--list-runtimes"]:
                    return process_result(stdout=b"Microsoft.NETCore.App 10.0.2 [/dotnet]\n")
                if len(arguments) > 1 and arguments[1] == "restore":
                    return process_result()
                self.fail(f"Coverage must not run for an unsupported offline profile: {arguments!r}")

            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )

            self.assertEqual(2, code)
            self.assertEqual(3, len(calls))
            result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["status"])
            self.assertEqual("ASESE010", result["diagnostic"]["code"])
            self.assertEqual(
                ["dotnet-sdk-version", "dotnet-runtime-list", "offline-locked-restore"],
                [step["name"] for step in result["steps"]],
            )

    def test_mocked_fixed_command_flow_restores_offline_and_keeps_result_nonclaiming(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject)
            calls: list[tuple[list[str], dict[str, object]]] = []

            def runner(arguments: list[str], **options: object) -> entrypoint.ProcessResult:
                calls.append((arguments, options))
                if arguments == ["dotnet", "--version"]:
                    return process_result(stdout=b"10.0.100\n")
                if arguments == ["dotnet", "--list-runtimes"]:
                    return process_result(stdout=b"Microsoft.NETCore.App 10.0.2 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n")
                return process_result()

            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )
            self.assertEqual(0, code)
            commands = [arguments for arguments, _ in calls]
            restore = next(command for command in commands if len(command) > 1 and command[1] == "restore")
            self.assertIn("--locked-mode", restore)
            self.assertIn("--configfile", restore)
            self.assertIn("--source", restore)
            self.assertEqual(str(feed), restore[restore.index("--source") + 1])
            offline_config = Path(restore[restore.index("--configfile") + 1]).read_text(encoding="utf-8")
            self.assertIn("<clear/>", offline_config)
            self.assertIn(f'<add key="locked-dependencies" value="{feed}"/>', offline_config)
            user_config = scratch / "dotnet-home/.nuget/NuGet/NuGet.Config"
            self.assertEqual(offline_config, user_config.read_text(encoding="utf-8"))
            self.assertEqual(0o600, stat.S_IMODE(user_config.stat().st_mode))
            coverage_run, coverage_gate = entrypoint._coverage_commands(
                scratch / entrypoint.STAGED_SUBJECT_RELATIVE_PATH,
                scratch,
                parent / "source.diff",
            )
            self.assertIn(coverage_run, commands)
            self.assertIn(coverage_gate, commands)
            self.assertEqual("95", coverage_gate[coverage_gate.index("--min-patch-line") + 1])
            self.assertEqual("85", coverage_gate[coverage_gate.index("--min-patch-branch") + 1])
            self.assertEqual("codecov", coverage_gate[coverage_gate.index("--patch-line-mode") + 1])
            self.assertEqual(str(parent / "source.diff"), coverage_gate[coverage_gate.index("--diff-file") + 1])
            self.assertEqual("--exclusive-test-project", coverage_run[coverage_run.index("--exclusive-test-project")])
            self.assertNotIn("--build", coverage_run)
            result_bytes = (scratch / entrypoint.RESULT_RELATIVE_PATH).read_bytes()
            result = json.loads(result_bytes)
            self.assertFalse(result["claimEligible"])
            self.assertEqual("completed", result["status"])
            self.assertEqual(
                ["dotnet-sdk-version", "dotnet-runtime-list", "offline-locked-restore", "coverage-run", "coverage-gate"],
                [step["name"] for step in result["steps"]],
            )
            self.assertLessEqual(len(result_bytes), entrypoint.MAX_RESULT_BYTES)

    def test_restore_failure_does_not_claim_coverage_or_run_coverage_commands(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject)
            calls: list[list[str]] = []

            def runner(arguments: list[str], **_options: object) -> entrypoint.ProcessResult:
                calls.append(arguments)
                if arguments == ["dotnet", "--version"]:
                    return process_result(stdout=b"10.0.100\n")
                if arguments == ["dotnet", "--list-runtimes"]:
                    return process_result(stdout=b"Microsoft.NETCore.App 10.0.2 [/dotnet]\n")
                if len(arguments) > 1 and arguments[1] == "restore":
                    return process_result(exit_code=1, stderr=b"untrusted oversized restore detail")
                self.fail("coverage must not run after a failed restore")

            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )
            self.assertEqual(2, code)
            result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["status"])
            self.assertEqual("ASESE008", result["diagnostic"]["code"])
            self.assertNotIn("untrusted oversized restore detail", json.dumps(result))
            self.assertEqual(["dotnet-sdk-version", "dotnet-runtime-list", "offline-locked-restore"], [step["name"] for step in result["steps"]])

    def test_runtime_version_mismatch_fails_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            subject, scratch, feed = self.prepared_roots(parent)
            self.make_subject(subject)

            def runner(arguments: list[str], **_options: object) -> entrypoint.ProcessResult:
                if arguments == ["dotnet", "--version"]:
                    return process_result(stdout=b"9.0.999\n")
                self.fail("runtime inventory must not run after an SDK version mismatch")

            with self.patch_paths(subject, scratch, feed):
                code = entrypoint.execute(
                    self.invocation(),
                    mountinfo_text=self.mountinfo(subject, scratch, feed),
                    effective_uid=os.geteuid(),
                    runner=runner,
                )
            self.assertEqual(2, code)
            result = json.loads((scratch / entrypoint.RESULT_RELATIVE_PATH).read_text(encoding="ascii"))
            self.assertEqual("ASESE004", result["diagnostic"]["code"])
            self.assertFalse(result["claimEligible"])

    def test_copy_refuses_links_and_copies_regular_files_into_scratch(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            source = parent / "source"
            source.mkdir()
            (source / "safe.txt").write_text("source bytes", encoding="utf-8")
            staged = parent / "staged"
            files, copied_bytes = entrypoint._copy_subject_tree(source, staged, deadline=time.monotonic() + 2)
            self.assertEqual(1, files)
            self.assertEqual(len(b"source bytes"), copied_bytes)
            self.assertEqual("source bytes", (staged / "safe.txt").read_text(encoding="utf-8"))
            (source / "link").symlink_to(staged / "safe.txt")
            with self.assertRaises(entrypoint.EntrypointError):
                entrypoint._copy_subject_tree(source, parent / "linked-copy", deadline=time.monotonic() + 2)

    def test_process_output_limit_and_deadline_terminate_child(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            environment = {"PATH": os.environ["PATH"]}
            output_budget = entrypoint.OutputBudget(32)
            with self.assertRaisesRegex(entrypoint.EntrypointError, "output exceeded"):
                entrypoint.run_bounded_process(
                    [sys.executable, "-c", "import os,time; os.write(1, b'x' * 4096); time.sleep(3)"],
                    working_directory=root,
                    environment=environment,
                    deadline=time.monotonic() + 5,
                    output_budget=output_budget,
                )
            with self.assertRaisesRegex(entrypoint.EntrypointError, "deadline expired"):
                entrypoint.run_bounded_process(
                    [sys.executable, "-c", "import time; time.sleep(3)"],
                    working_directory=root,
                    environment=environment,
                    deadline=time.monotonic() + 0.05,
                    output_budget=entrypoint.OutputBudget(128),
                )
            with self.assertRaisesRegex(entrypoint.EntrypointError, "deadline expired"):
                entrypoint.run_bounded_process(
                    [sys.executable, "-c", "import os,time; os.close(1); os.close(2); time.sleep(3)"],
                    working_directory=root,
                    environment=environment,
                    deadline=time.monotonic() + 0.05,
                    output_budget=entrypoint.OutputBudget(128),
                )

    def test_result_schema_is_canonical_and_diagnostic_is_bounded(self) -> None:
        diagnostic = ("ASESE002", "dependency unavailable")
        first = entrypoint._make_result("failed", [], diagnostic=diagnostic)
        second = entrypoint._make_result("failed", [], diagnostic=diagnostic)
        self.assertEqual(first, second)
        self.assertTrue(first.endswith(b"\n"))
        self.assertEqual(first, (json.dumps(json.loads(first), ensure_ascii=True, separators=(",", ":"), sort_keys=True) + "\n").encode("ascii"))

    def test_main_streams_the_bounded_result_record_for_attached_launcher_capture(self) -> None:
        class CapturedStdout:
            def __init__(self) -> None:
                self.buffer = io.BytesIO()

        for status, exit_code, diagnostic in (
            ("completed", 0, None),
            ("failed", 2, ("ASESE010", "coverage unavailable")),
        ):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as temporary:
                scratch = Path(temporary).resolve()
                invocation = entrypoint.Invocation(
                    entrypoint.FIXED_PROFILE_ID,
                    entrypoint.SUBJECT_ROOT,
                    scratch,
                    entrypoint.DEPENDENCY_SOURCE,
                    entrypoint.PATCH_DIFF_PATH,
                    hashlib.sha256(b"captured unified diff\n").hexdigest(),
                    True,
                )
                expected = entrypoint._make_result(status, [], diagnostic=diagnostic)
                captured = CapturedStdout()

                def write_record(_invocation: entrypoint.Invocation) -> int:
                    entrypoint._write_result(scratch, expected)
                    return exit_code

                with (
                    mock.patch.object(entrypoint, "parse_invocation", return_value=invocation),
                    mock.patch.object(entrypoint, "execute", side_effect=write_record),
                    mock.patch("sys.stdout", captured),
                ):
                    self.assertEqual(exit_code, entrypoint.main([]))

                self.assertEqual(expected, captured.buffer.getvalue())

    def test_result_transport_rejects_oversized_or_symlinked_record(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            scratch = Path(temporary).resolve()
            oversized = scratch / entrypoint.RESULT_RELATIVE_PATH
            oversized.write_bytes(b"x" * (entrypoint.MAX_RESULT_BYTES + 1))
            with self.assertRaisesRegex(entrypoint.EntrypointError, "bounded") as oversized_failure:
                entrypoint._result_for_transport(scratch)
            self.assertEqual("ASESE009", oversized_failure.exception.code)

            oversized.unlink()
            target = scratch / "target"
            target.write_bytes(b"{}")
            (scratch / entrypoint.RESULT_RELATIVE_PATH).symlink_to(target)
            with self.assertRaises(entrypoint.EntrypointError) as symlink_failure:
                entrypoint._result_for_transport(scratch)
            self.assertEqual("ASESE009", symlink_failure.exception.code)

    def test_process_cleanup_fails_closed_when_group_cannot_be_reaped(self) -> None:
        process = mock.Mock(pid=12345)
        process.wait.side_effect = subprocess.TimeoutExpired("fixture", 2)
        with mock.patch.object(entrypoint.os, "killpg") as kill_group:
            with self.assertRaisesRegex(entrypoint.EntrypointError, "could not be reaped") as failure:
                entrypoint._terminate_process_group(process)
        self.assertEqual("ASESE011", failure.exception.code)
        kill_group.assert_called_once_with(12345, entrypoint.signal.SIGKILL)


if __name__ == "__main__":
    unittest.main()
