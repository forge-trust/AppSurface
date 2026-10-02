"""Regression controls for candidate proof rejection; these grant no runtime acceptance."""
import importlib.util
import hashlib
import json
import os
import stat
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("runtime_proof", Path(__file__).with_name("runtime-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class TrustedRejectionProofTests(unittest.TestCase):
    def test_only_the_exact_missing_proof_rejection_counts(self):
        cases = (
            (1, b"", b'{"status":"failed","diagnostic":"ASEVD407"}', True),
            (1, b"", b'{"status":"failed","diagnostic":"launcher-failed"}', False),
            (2, b"", b"unrelated setup failure", False),
            (0, b"", b'{"status":"failed","diagnostic":"ASEVD407"}', False),
            (1, b"unexpected output", b'{"status":"failed","diagnostic":"ASEVD407"}', False),
        )
        for code, stdout, stderr, expected in cases:
            with self.subTest(code=code, stdout=stdout, stderr=stderr), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                arguments = dict(
                    tool_root=parent, policy_file=parent / "policy.json", output_parent=parent,
                    bindings={"EVIDENCE_BASE_REVISION": "base", "EVIDENCE_SUBJECT_REVISION": "subject",
                              "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "1/1"}, env={},
                )
                with patch.object(proof, "root_command", return_value=(code, stdout, stderr)):
                    if expected:
                        proof.assert_trusted_denied(**arguments)
                    else:
                        with self.assertRaises(proof.ProofFailure):
                            proof.assert_trusted_denied(**arguments)

    def test_declared_rejection_cannot_leave_an_output_anchor(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            (parent / "unexpected-anchor").mkdir()
            with patch.object(proof, "root_command", return_value=(
                    1, b"", b'{"status":"failed","diagnostic":"ASEVD407"}')):
                with self.assertRaisesRegex(proof.ProofFailure, "created an output anchor"):
                    proof.assert_trusted_denied(
                        tool_root=parent, policy_file=parent / "policy.json", output_parent=parent,
                        bindings={"EVIDENCE_BASE_REVISION": "base", "EVIDENCE_SUBJECT_REVISION": "subject",
                                  "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "1/1"}, env={},
                    )


class BuildPublishProofTests(unittest.TestCase):
    SUMMARY = b"Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n"

    @staticmethod
    def process(code, stdout, stderr):
        result = Mock(returncode=code)
        result.communicate.return_value = (stdout, stderr)
        return result

    @staticmethod
    def directories(directory):
        root = Path(directory)
        tool, public = root / "tool", root / "proof"
        tool.mkdir()
        public.mkdir()
        (tool / proof.CLI_DLL_NAME).write_bytes(b"published CLI")
        return tool, public

    def test_verified_build_then_summaryless_publish_retains_all_logs_and_digest(self):
        outputs = (
            (0, b"subject\n" + self.SUMMARY, b"subject stderr\n"),
            (0, b"CLI\n" + self.SUMMARY, b"CLI stderr\n"),
            (0, b"CLI -> publish directory\n", b"publish stderr\n"),
        )
        with tempfile.TemporaryDirectory() as directory:
            tool, public = self.directories(directory)
            with patch.object(proof, "dotnet_host", return_value=Path("/fixture/dotnet")), \
                 patch.object(proof.subprocess, "Popen", side_effect=[self.process(*item) for item in outputs]) as start:
                cli_hash, log_hash = proof.build_before_root(tool, {}, public)
            commands = [call.args[0] for call in start.call_args_list]
            self.assertEqual([command[1] for command in commands], ["build", "build", "publish"])
            self.assertEqual(commands[1][2], str(proof.CLI_PROJECT))
            self.assertEqual(commands[2][2], str(proof.CLI_PROJECT))
            for command in commands[1:]:
                for option, expected in (("--configuration", "Release"), ("--runtime", "linux-x64"),
                                         ("--self-contained", "false")):
                    self.assertEqual(command[command.index(option) + 1], expected)
                self.assertIn("-warnaserror", command)
            self.assertIn("--no-build", commands[2])
            self.assertIn("--no-restore", commands[2])
            streams = [stream for _code, stdout, stderr in outputs for stream in (stdout, stderr)]
            for name, content in zip(proof.BUILD_LOG_FILES, streams):
                self.assertEqual((public / name).read_bytes(), content)
            self.assertEqual(log_hash, hashlib.sha256(b"".join(streams)).hexdigest())
            self.assertEqual(cli_hash, hashlib.sha256(b"published CLI").hexdigest())

    def test_failed_or_unverified_cli_build_rejects_before_publish_and_retains_logs(self):
        cases = (
            (1, self.SUMMARY, b"error CS1002: build failed\n"),
            (0, b"warning NU1903: vulnerability\n" + self.SUMMARY, b""),
            (0, self.SUMMARY, b"warning CS0168: unused variable\n"),
            (0, b"CLI -> build directory\n", b""),
            (0, b"Build succeeded.\n    1 Warning(s)\n", b""),
        )
        for code, stdout, stderr in cases:
            with self.subTest(code=code, stdout=stdout, stderr=stderr), tempfile.TemporaryDirectory() as directory:
                tool, public = self.directories(directory)
                with patch.object(proof, "dotnet_host", return_value=Path("/fixture/dotnet")), \
                     patch.object(proof.subprocess, "Popen", side_effect=[
                         self.process(0, self.SUMMARY, b""), self.process(code, stdout, stderr),
                     ]) as start:
                    with self.assertRaises(proof.ProofFailure):
                        proof.build_before_root(tool, {}, public)
                self.assertEqual(start.call_count, 2)
                self.assertEqual((public / "cli-build.stdout.log").read_bytes(), stdout)
                self.assertEqual((public / "cli-build.stderr.log").read_bytes(), stderr)
                self.assertFalse((public / "cli-publish.stdout.log").exists())

    def test_publish_warning_diagnostic_rejects_and_retains_raw_output(self):
        warning = b"warning NU1903: vulnerability\n"
        for stdout, stderr in ((warning, b""), (b"CLI -> publish directory\n", warning)):
            with self.subTest(stdout=stdout, stderr=stderr), tempfile.TemporaryDirectory() as directory:
                tool, public = self.directories(directory)
                with patch.object(proof, "dotnet_host", return_value=Path("/fixture/dotnet")), \
                     patch.object(proof.subprocess, "Popen", side_effect=[
                         self.process(0, self.SUMMARY, b""), self.process(0, self.SUMMARY, b""),
                         self.process(0, stdout, stderr),
                     ]) as start:
                    with self.assertRaisesRegex(proof.ProofFailure, "CLI publish emitted a warning diagnostic"):
                        proof.build_before_root(tool, {}, public)
                self.assertEqual(start.call_count, 3)
                self.assertEqual((public / "cli-publish.stdout.log").read_bytes(), stdout)
                self.assertEqual((public / "cli-publish.stderr.log").read_bytes(), stderr)

    def test_subject_build_still_requires_summary_before_cli_build(self):
        with tempfile.TemporaryDirectory() as directory:
            tool, public = self.directories(directory)
            with patch.object(proof, "dotnet_host", return_value=Path("/fixture/dotnet")), \
                 patch.object(proof.subprocess, "Popen", return_value=self.process(0, b"subject output\n", b"")) as start:
                with self.assertRaisesRegex(proof.ProofFailure, "RuntimeSubject build did not prove"):
                    proof.build_before_root(tool, {}, public)
            self.assertEqual(start.call_count, 1)
            self.assertEqual((public / "subject-build.stdout.log").read_bytes(), b"subject output\n")
            self.assertFalse((public / "cli-build.stdout.log").exists())

    def test_timed_out_command_retains_stdout_and_stderr(self):
        with tempfile.TemporaryDirectory() as directory:
            public = Path(directory)
            process = Mock(pid=12345)
            process.communicate.side_effect = [
                proof.subprocess.TimeoutExpired(["dotnet", "build"], 5),
                (b"partial stdout", b"partial stderr"),
            ]
            with patch.object(proof.subprocess, "Popen", return_value=process), \
                 patch.object(proof.os, "killpg") as kill:
                with self.assertRaisesRegex(proof.ProofFailure, "exceeded its 5-second process deadline"):
                    proof.run_success(["dotnet", "build"], cwd=public, env={}, timeout=5,
                                      label="CLI build", log_prefix=public / "cli-build")
            kill.assert_called_once_with(12345, proof.signal.SIGTERM)
            self.assertEqual((public / "cli-build.stdout.log").read_bytes(), b"partial stdout")
            self.assertEqual((public / "cli-build.stderr.log").read_bytes(), b"partial stderr")


class RuntimeSubjectStagingTests(unittest.TestCase):
    @staticmethod
    def candidate(parent):
        source = parent / "candidate"
        source.mkdir()
        hashes = {}
        for relative in proof.STAGED_SUBJECT_FILES:
            path = source / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            data = (proof.ROOT / relative).read_bytes()
            path.write_bytes(data)
            hashes[relative] = hashlib.sha256(data).hexdigest()
        return source, hashes

    def test_exact_fixture_bytes_and_build_inputs_are_staged_with_original_launcher_bindings(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            source, hashes = self.candidate(parent)
            generated = source / "Web/node_modules"
            generated.mkdir(parents=True)
            (generated / "unrelated-link").symlink_to(source)
            stage, staged_hashes = proof.stage_runtime_subject(parent, hashes, source_root=source)
            self.assertEqual(staged_hashes, hashes)
            self.assertEqual({path.relative_to(stage).as_posix() for path in stage.rglob("*") if path.is_file()},
                             set(proof.STAGED_SUBJECT_FILES))
            self.assertFalse((stage / "Web").exists())
            for relative in proof.STAGED_SUBJECT_FILES:
                self.assertEqual((stage / relative).read_bytes(), (source / relative).read_bytes())
            proof.verify_staged_subject(stage, hashes)
            bindings = {"EVIDENCE_BASE_REVISION": "base", "EVIDENCE_SUBJECT_REVISION": "candidate",
                        "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "12/1"}
            arguments = proof.launcher_args(tool_root=parent / "tool", policy_file=parent / "policy",
                output_parent=parent / "output", bindings=bindings, mode="observation", slot="slot", subject_root=stage)
            self.assertEqual(arguments[arguments.index("--subject-root") + 1], str(stage))
            self.assertEqual(arguments[arguments.index("--solution") + 1], proof.SUBJECT_PROJECT_RELATIVE)
            self.assertEqual(arguments[arguments.index("--subject-revision") + 1], "candidate")
            (stage / "extra").write_bytes(b"unexpected")
            with self.assertRaises(proof.ProofFailure): proof.verify_staged_subject(stage, hashes)

    def test_required_file_or_ancestor_links_missing_and_nonregular_inputs_are_rejected(self):
        for kind in ("file-link", "ancestor-link", "missing", "directory", "source-link"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                source, hashes = self.candidate(parent)
                path = source / "Directory.Build.props"
                if kind == "ancestor-link":
                    ancestor = source / "tests"
                    ancestor.rename(source / "real-tests")
                    ancestor.symlink_to(source / "real-tests", target_is_directory=True)
                elif kind == "source-link":
                    link = parent / "linked-source"
                    link.symlink_to(source, target_is_directory=True)
                    source = link
                else:
                    data = path.read_bytes()
                    path.unlink()
                    if kind == "file-link":
                        target = source / "other.props"
                        target.write_bytes(data)
                        path.symlink_to(target)
                    if kind == "directory": path.mkdir()
                with self.assertRaises(proof.ProofFailure):
                    proof.stage_runtime_subject(parent, hashes, source_root=source)
                self.assertFalse((parent / "subject-source").exists())

    def test_undeclared_build_inputs_and_imports_require_explicit_scope_update(self):
        for kind in ("global", "ancestor-props", "import", "project-reference"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                source, hashes = self.candidate(parent)
                if kind == "global": (source / "global.json").write_text("{}")
                elif kind == "ancestor-props": (source / "tests/Directory.Build.props").write_text("<Project />")
                else:
                    tag = 'Import Project="other.props"' if kind == "import" else 'ProjectReference Include="other.csproj"'
                    data = f"<Project><{tag} /></Project>".encode()
                    (source / "Directory.Build.props").write_bytes(data)
                    hashes["Directory.Build.props"] = hashlib.sha256(data).hexdigest()
                with self.assertRaises(proof.ProofFailure):
                    proof.stage_runtime_subject(parent, hashes, source_root=source)

    def test_pinned_hash_byte_limit_and_fresh_destination_are_enforced(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            source, hashes = self.candidate(parent)
            with self.assertRaises(proof.ProofFailure):
                proof.stage_runtime_subject(parent, {**hashes, "Directory.Build.props": "0" * 64}, source_root=source)
            with patch.object(proof, "MAX_STAGED_SUBJECT_BYTES", 1), self.assertRaises(proof.ProofFailure):
                proof.stage_runtime_subject(parent, hashes, source_root=source)
            stage, _ = proof.stage_runtime_subject(parent, hashes, source_root=source)
            with self.assertRaises(proof.ProofFailure): proof.stage_runtime_subject(parent, hashes, source_root=source)
            (stage / "Directory.Build.props").write_bytes(b"changed")
            with self.assertRaises(proof.ProofFailure): proof.verify_staged_subject(stage, hashes)


class StructuralVerificationProofTests(unittest.TestCase):
    @staticmethod
    def protected_lstat(parent, *, wrong_child_owner=False):
        original = Path.lstat
        def inspect(path):
            info = original(path)
            if path == parent or (wrong_child_owner and path == parent / "structural-verification"):
                fields = list(info)
                fields[4] = 0 if path == parent else os.geteuid() + 1
                return os.stat_result(fields)
            return info
        return inspect

    def test_reserved_owned_child_is_used_without_mkdir_after_parent_protection(self):
        with tempfile.TemporaryDirectory(prefix="appsurface-evidencehost-runtime-", dir="/tmp") as directory:
            parent = Path(directory)
            child = proof.reserve_structural_verification_directory(parent)
            self.assertEqual(child, parent / "structural-verification")
            self.assertEqual(stat.S_IMODE(child.stat().st_mode), 0o700)
            self.assertEqual(child.stat().st_uid, os.geteuid())
            parent.chmod(0o755)
            stdout = b"Evidence manifest structurally verified: ObservationOnly (Informational)."
            artifacts = {"evidence-plan.json": b"private plan", "evidence-manifest.json": b"private manifest"}
            with patch.object(Path, "lstat", self.protected_lstat(parent)), \
                 patch.object(Path, "mkdir", side_effect=PermissionError("root-owned parent")), \
                 patch.object(proof, "dotnet_host", return_value=Path("/fixture/dotnet")), \
                 patch.object(proof, "root_success", return_value=(stdout, b"")) as command:
                self.assertEqual(proof.verify_collected_structure(Path("/fixture/cli.dll"), artifacts, parent),
                                 hashlib.sha256(stdout).hexdigest())
            for name, content in artifacts.items():
                self.assertEqual((child / name).read_bytes(), content)
                self.assertEqual(stat.S_IMODE((child / name).stat().st_mode), 0o600)
            self.assertEqual(command.call_args.args[0][-3:],
                             [str(child / "evidence-manifest.json"), "--plan", str(child / "evidence-plan.json")])

    def test_reservation_rejects_existing_children_and_unsafe_workspace_inputs(self):
        with tempfile.TemporaryDirectory(prefix="appsurface-evidencehost-runtime-", dir="/tmp") as directory:
            parent = Path(directory)
            link = parent / "linked-workspace"
            link.symlink_to(parent, target_is_directory=True)
            for candidate in (Path("relative"), parent / "missing", link):
                with self.subTest(candidate=candidate), self.assertRaises(proof.ProofFailure):
                    proof.reserve_structural_verification_directory(candidate)
            child = proof.reserve_structural_verification_directory(parent)
            (child / "preserved").write_bytes(b"private")
            with self.assertRaises(proof.ProofFailure):
                proof.reserve_structural_verification_directory(parent)
            self.assertEqual((child / "preserved").read_bytes(), b"private")
            parent.chmod(0o755)
            with self.assertRaises(proof.ProofFailure):
                proof.reserve_structural_verification_directory(parent)

    def test_missing_linked_shared_or_wrong_owner_reserved_child_cannot_invoke_root(self):
        for kind in ("missing", "symlink", "shared", "wrong-owner", "occupied-copy"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(
                    prefix="appsurface-evidencehost-runtime-", dir="/tmp") as directory:
                parent = Path(directory)
                child = parent / "structural-verification"
                if kind == "symlink":
                    child.symlink_to(parent, target_is_directory=True)
                elif kind != "missing":
                    proof.reserve_structural_verification_directory(parent)
                    if kind == "shared": child.chmod(0o755)
                    if kind == "occupied-copy": (child / "evidence-plan.json").symlink_to(parent / "absent")
                parent.chmod(0o755)
                with patch.object(Path, "lstat", self.protected_lstat(parent, wrong_child_owner=kind == "wrong-owner")), \
                     patch.object(proof, "root_success") as command:
                    with self.assertRaises(proof.ProofFailure):
                        proof.verify_collected_structure(Path("/fixture/cli.dll"),
                            {"evidence-plan.json": b"plan", "evidence-manifest.json": b"manifest"}, parent)
                    command.assert_not_called()


class ObservationFailureDiagnosticTests(unittest.TestCase):
    def test_success_does_not_read_diagnostics_and_passes_option_only_to_observation(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            command = ["python3", "launcher.py", "--mode", "observation"]
            with patch.object(proof, "root_command", return_value=(0, b"completed", b"")) as root:
                self.assertEqual(proof.run_observation_launcher(command, parent, parent), (b"completed", b""))
            self.assertEqual(root.call_count, 1)
            self.assertEqual(root.call_args.args[0][-2:], ["--diagnostic-directory", str(parent)])
            self.assertNotIn("--diagnostic-directory", command)
            self.assertFalse((parent / "launcher-failure.json").exists())

    def test_failure_publishes_only_validated_categories_without_output_canaries(self):
        record = {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                  "cause": "worker-protocol-incomplete"}
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            with patch.object(proof, "root_command", side_effect=[
                    (1, b"secret-779 stdout", b"secret-779 stderr"),
                    (0, json.dumps(record).encode(), b"secret-779 read stderr")]) as root:
                with self.assertRaises(proof.ProofFailure) as failure:
                    proof.run_observation_launcher(["launcher"], parent, parent)
            self.assertEqual(root.call_count, 2)
            self.assertNotIn("secret-779", str(failure.exception))
            self.assertIn("worker-protocol-incomplete", str(failure.exception))
            self.assertEqual(json.loads((parent / "launcher-failure.json").read_text()), record)

    def test_missing_malformed_oversize_or_failed_capture_preserves_generic_failure(self):
        good = {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                "cause": "worker-protocol-incomplete"}
        captures = [(1, b"", b"secret-779"), (0, b"not JSON secret-779", b""),
                    (0, json.dumps({**good, "stderr": "secret-779"}).encode(), b""),
                    (0, b"x" * 4097, b""), proof.ProofFailure("capture timed out")]
        for capture in captures:
            with self.subTest(capture=type(capture).__name__), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                with patch.object(proof, "root_command", side_effect=[(1, b"secret-779", b"secret-779"), capture]):
                    with self.assertRaisesRegex(proof.ProofFailure, "safe diagnostic unavailable") as failure:
                        proof.run_observation_launcher(["launcher"], parent, parent)
                self.assertNotIn("secret-779", str(failure.exception))
                self.assertFalse((parent / "launcher-failure.json").exists())


class LauncherWorkspaceProofTests(unittest.TestCase):
    def test_protected_parent_is_traversable_without_changing_private_children(self):
        with tempfile.TemporaryDirectory(prefix="appsurface-evidencehost-runtime-", dir="/tmp") as directory:
            parent = Path(directory)
            private = parent / "private-cache"
            private.mkdir(mode=0o700)
            original_lstat = Path.lstat

            def protected_stat(path):
                info = original_lstat(path)
                if path == parent:
                    fields = list(info)
                    fields[0] = stat.S_IFDIR | 0o755
                    fields[4] = 0
                    return os.stat_result(fields)
                return info

            with patch.object(proof, "root_success", return_value=(b"", b"")) as command, \
                 patch.object(Path, "lstat", protected_stat):
                proof.protect_launcher_workspace(parent)
            self.assertEqual([call.args[0] for call in command.call_args_list], [
                ["/usr/bin/chown", "--no-dereference", "root:root", str(parent)],
                ["/usr/bin/chmod", "0755", str(parent)],
            ])
            self.assertEqual(stat.S_IMODE(private.stat().st_mode), 0o700)

    def test_unsafe_or_unprotected_parent_cannot_reach_the_launcher(self):
        with tempfile.TemporaryDirectory(prefix="appsurface-evidencehost-runtime-", dir="/tmp") as directory:
            parent = Path(directory)
            link = parent / "appsurface-evidencehost-runtime-link"
            link.symlink_to(parent, target_is_directory=True)
            for candidate in (parent / "missing", link, Path("relative")):
                with self.subTest(candidate=candidate), patch.object(proof, "root_success") as command:
                    with self.assertRaises(proof.ProofFailure):
                        proof.protect_launcher_workspace(candidate)
                    command.assert_not_called()
            with patch.object(proof, "root_success", return_value=(b"", b"")):
                with self.assertRaisesRegex(proof.ProofFailure, "protection did not take effect"):
                    proof.protect_launcher_workspace(parent)


if __name__ == "__main__":
    unittest.main()
