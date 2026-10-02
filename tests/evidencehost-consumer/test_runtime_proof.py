"""Regression controls for candidate proof rejection; these grant no runtime acceptance."""
import importlib.util
import hashlib
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
