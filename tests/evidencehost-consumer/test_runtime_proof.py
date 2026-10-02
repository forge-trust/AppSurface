"""Regression controls for candidate proof rejection; these grant no runtime acceptance."""
import importlib.util
import hashlib
import io
import json
import os
import stat
import tempfile
import tarfile
import uuid
from contextlib import contextmanager
from types import SimpleNamespace
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location("runtime_proof", Path(__file__).with_name("runtime-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


@contextmanager
def portable_runtime_workspace():
    """Use real private files with a test-only parent override; never operate on host /run."""
    with tempfile.TemporaryDirectory() as directory:
        parent = Path(directory)
        child = parent / (proof.RUNTIME_WORKSPACE_PREFIX + uuid.uuid4().hex)
        child.mkdir(mode=0o700)
        with patch.object(proof, "RUNTIME_WORKSPACE_PARENT", parent):
            yield str(child)


class SourceInventoryProofTests(unittest.TestCase):
    SHARED_EXECUTION_SOURCES = (
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducer.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageTransport.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Coverage/EvidenceRestrictedCoverageProducerFactory.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceRestrictedProducerLease.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceWorkerExecution.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Contracts/EvidenceLinuxApplicationProtocol.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Planner/EvidenceClosedApplicationCatalogue.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceHostBootstrap.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceRestrictedAspireApplication.cs",
    )
    PREVIOUS_SOURCES = (
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageProducer.cs",
        "Evidence/ForgeTrust.AppSurface.Evidence.Cli/EvidenceRestrictedCoverageTransport.cs",
    )

    @contextmanager
    def inventory(self):
        """Exercise real required files in a disposable checkout without runtime execution."""
        with tempfile.TemporaryDirectory() as directory, patch.object(proof, "ROOT", Path(directory)):
            contents = {}
            for path in proof.source_paths():
                path.parent.mkdir(parents=True, exist_ok=True)
                relative = path.relative_to(proof.ROOT).as_posix()
                contents[relative] = ("fixture source: " + relative + "\n").encode("utf-8")
                path.write_bytes(contents[relative])
            yield proof.ROOT, contents

    def test_shared_single_copy_and_dependency_inputs_bind_exact_file_bytes(self):
        with self.inventory() as (root, contents):
            relative_paths = [path.relative_to(root).as_posix() for path in proof.source_paths()]
            self.assertEqual(len(relative_paths), len(set(relative_paths)))
            self.assertTrue(set(self.SHARED_EXECUTION_SOURCES).issubset(relative_paths))
            for package in ("Contracts", "Planner", "Coverage", "Cli", "Aspire"):
                directory = "Evidence/ForgeTrust.AppSurface.Evidence." + package
                self.assertIn(directory + "/ForgeTrust.AppSurface.Evidence." + package + ".csproj", relative_paths)
                self.assertIn(directory + "/packages.lock.json", relative_paths)
            for relative in self.PREVIOUS_SOURCES:
                self.assertNotIn(relative, relative_paths)
                self.assertFalse((root / relative).exists())
            hashes, digest = proof.hash_sources()
            expected = {relative: hashlib.sha256(data).hexdigest() for relative, data in contents.items()}
            self.assertEqual(hashes, expected)
            canonical = json.dumps(expected, sort_keys=True, separators=(",", ":")).encode("utf-8")
            self.assertEqual(digest, hashlib.sha256(canonical).hexdigest())

    def test_previous_copy_directory_or_dangling_link_rejects_inventory(self):
        for relative in self.PREVIOUS_SOURCES:
            for kind in ("file", "directory", "dangling-link"):
                with self.subTest(relative=relative, kind=kind), self.inventory() as (root, _):
                    previous = root / relative
                    if kind == "file":
                        previous.write_bytes(b"stale implementation")
                    elif kind == "directory":
                        previous.mkdir()
                    else:
                        previous.symlink_to("missing-implementation.cs")
                    with self.assertRaisesRegex(proof.ProofFailure, "previous location"):
                        proof.hash_sources()

    def test_each_missing_shared_execution_source_rejects_inventory(self):
        for relative in self.SHARED_EXECUTION_SOURCES:
            with self.subTest(relative=relative), self.inventory() as (root, _):
                (root / relative).unlink()
                with self.assertRaisesRegex(proof.ProofFailure, "missing or unsafe"):
                    proof.hash_sources()

    def test_linked_or_nonregular_shared_sources_cannot_replace_required_bytes(self):
        for relative in self.SHARED_EXECUTION_SOURCES:
            for kind in ("link", "dangling-link", "directory"):
                with self.subTest(relative=relative, kind=kind), self.inventory() as (root, _):
                    target = root / relative
                    target.unlink()
                    if kind == "directory":
                        target.mkdir()
                    elif kind == "link":
                        replacement = root / "replacement.cs"
                        replacement.write_bytes(b"different bytes")
                        target.symlink_to(replacement)
                    else:
                        target.symlink_to("missing-implementation.cs")
                    with self.assertRaisesRegex(proof.ProofFailure, "missing or unsafe"):
                        proof.hash_sources()

    def test_shared_source_mutation_changes_only_its_binding_and_canonical_digest(self):
        for relative in self.SHARED_EXECUTION_SOURCES:
            with self.subTest(relative=relative), self.inventory() as (root, _):
                before, before_digest = proof.hash_sources()
                changed = b"changed shared execution source\n"
                (root / relative).write_bytes(changed)
                after, after_digest = proof.hash_sources()
                self.assertEqual(after[relative], hashlib.sha256(changed).hexdigest())
                self.assertEqual([name for name in before if before[name] != after[name]], [relative])
                self.assertNotEqual(after_digest, before_digest)


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
                if path == parent: fields[5] = 0
                return os.stat_result(fields)
            return info
        return inspect

    def test_reserved_owned_child_is_used_without_mkdir_after_parent_protection(self):
        with portable_runtime_workspace() as directory:
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
        with portable_runtime_workspace() as directory:
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
            with self.subTest(kind=kind), portable_runtime_workspace() as directory:
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


class WorkerExitDiagnosticValidationTests(unittest.TestCase):
    """Pure controls for the actual launcher validator used by the runtime driver."""
    @classmethod
    def setUpClass(cls):
        cls.launcher = proof.load_launcher_contract()

    @staticmethod
    def record(cause):
        return {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                "cause": cause, "operation": "worker-exit", "worker_main_code": 1, "worker_main_status": 1,
                "broker_ready_seen": True, "broker_wait_completed": False, "broker_exited": False,
                "broker_work_closed": True, "broker_active_handlers": 0, "broker_active_runs": 0,
                "worker_journal_state": "collected", "worker_journal_written": True,
                "worker_journal_bytes": 4096, "worker_journal_codes": ["ASEVD402", "ASEVD410"]}

    def test_both_worker_exit_causes_accept_the_same_closed_checkpoint_and_journal_fields(self):
        for cause in ("worker-protocol-incomplete", "worker-unsuccessful"):
            with self.subTest(cause=cause):
                record = self.record(cause)
                validated = self.launcher.validate_failure_diagnostic(record)
                self.assertEqual(record, validated)
                self.assertIsNot(record, validated)
                self.assertEqual(cause, validated["cause"])
                self.assertNotIn("status", validated)
                self.assertNotIn("trusted", validated)

    def test_both_causes_serialize_closed_fields_before_runtime_validation_without_canary_echo(self):
        for cause in ("worker-protocol-incomplete", "worker-unsuccessful"):
            with self.subTest(cause=cause):
                expected = {**self.record(cause), "exit_code": 1}
                error = self.launcher.LauncherError(cause, operation="worker-exit", exit_code=1)
                error.worker_main_code = 1
                error.worker_main_status = 1
                error.broker_checkpoints = {name: value for name, value in expected.items() if name.startswith("broker_")}
                error.broker_checkpoints["raw_subject_output"] = "secret-779"
                error.worker_journal = {name: value for name, value in expected.items() if name.startswith("worker_journal_")}
                error.worker_journal["raw_exception"] = "secret-779"
                error.worker_result = "secret-779"
                serialized = self.launcher.failure_diagnostic(error)
                self.assertEqual(expected, serialized)
                self.assertNotIn("secret-779", json.dumps(serialized))
                self.assertEqual(expected, self.launcher.validate_failure_diagnostic(serialized))

    def test_checkpoint_or_journal_metadata_requires_a_permitted_cause_and_worker_exit(self):
        for cause, operation in (("worker-start-unit-failed", "worker-exit"),
                                 ("unclassified-host-failure", "worker-exit"),
                                 ("secret-779", "worker-exit"),
                                 ("worker-unsuccessful", "worker-start"),
                                 ("worker-protocol-incomplete", "worker-start"),
                                 ("worker-unsuccessful", None)):
            with self.subTest(cause=cause, operation=operation):
                record = self.record(cause)
                if operation is None:
                    del record["operation"]
                else:
                    record["operation"] = operation
                with self.assertRaisesRegex(self.launcher.LauncherError, "^invalid-private-diagnostic$") as failure:
                    self.launcher.validate_failure_diagnostic(record)
                self.assertNotIn("secret-779", str(failure.exception))

    def test_both_causes_reject_wrong_types_bounds_unknown_fields_and_journal_canaries(self):
        mutations = ({"broker_ready_seen": "secret-779"}, {"broker_active_handlers": True},
                     {"broker_active_handlers": 4097}, {"broker_active_runs": 2},
                     {"worker_journal_written": 1}, {"worker_journal_bytes": True},
                     {"worker_journal_bytes": -1}, {"worker_journal_bytes": 4097},
                     {"worker_journal_state": "secret-779"}, {"worker_journal_codes": ["ASEVD402", "secret-779"]},
                     {"worker_journal_codes": [1]}, {"worker_journal_codes": ["ASEVD402"] * 15},
                     {"stdout": "secret-779"}, {"exception": "secret-779"}, {"path": "secret-779"},
                     {"status": "completed"}, {"trusted": True})
        for cause in ("worker-protocol-incomplete", "worker-unsuccessful"):
            for mutation in mutations:
                with self.subTest(cause=cause, fields=list(mutation)):
                    record = {**self.record(cause), **mutation}
                    with self.assertRaisesRegex(self.launcher.LauncherError, "^invalid-private-diagnostic$") as failure:
                        self.launcher.validate_failure_diagnostic(record)
                    self.assertNotIn("secret-779", str(failure.exception))


class ObservationFailureDiagnosticTests(unittest.TestCase):
    def test_startup_status_is_published_only_after_closed_schema_validation(self):
        safe = {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                "cause": "worker-start-unit-failed", "operation": "worker-start", "exit_code": 1,
                "worker_load_state": "loaded", "worker_result": "exit-code",
                "worker_main_code": 1, "worker_main_status": 226}
        for record, accepted in ((safe, True), ({**safe, "worker_result": ["secret-779"]}, False),
                                 ({**safe, "worker_main_status": True}, False)):
            with self.subTest(accepted=accepted, record=record), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                with patch.object(proof, "root_command", side_effect=[
                        (1, b"secret-779 stdout", b"secret-779 stderr"),
                        (0, json.dumps(record).encode(), b"secret-779 query stderr")]):
                    with self.assertRaises(proof.ProofFailure) as failure:
                        proof.run_observation_launcher(["launcher"], parent, parent)
                self.assertNotIn("secret-779", str(failure.exception))
                receipt = parent / "launcher-failure.json"
                self.assertEqual(receipt.exists(), accepted)
                if accepted:
                    self.assertEqual(json.loads(receipt.read_text()), safe)

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


class PrivateWorkerJournalRetentionTests(unittest.TestCase):
    @staticmethod
    def helper():
        namespace = {"__name__": "private_journal_portable_control"}
        exec(compile(proof.PRIVATE_WORKER_JOURNAL_ROOT_SCRIPT, "<private journal root helper>", "exec"), namespace)
        return namespace["archive_from_directory"]

    @staticmethod
    def archive(data=b"secret-779 private journal", **changes):
        result = io.BytesIO()
        with tarfile.open(fileobj=result, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            member = tarfile.TarInfo(proof.PRIVATE_WORKER_JOURNAL_NAME)
            member.mode, member.uid, member.gid, member.size = 0o600, 0, 0, len(data)
            for name, value in changes.items():
                setattr(member, name, value)
            archive.addfile(member, io.BytesIO(data) if member.isreg() else None)
        return result.getvalue()

    def test_root_helper_reads_exact_fixed_file_and_exports_only_bounded_root_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            parent.chmod(0o755)
            journal = parent / proof.PRIVATE_WORKER_JOURNAL_NAME
            journal.write_bytes(b"secret-779" + b"x" * (4096 - 10))
            journal.chmod(0o600)
            (parent / "unrelated").write_text("not selected")
            fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                data = self.helper()(fd, expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid())
            finally:
                os.close(fd)
            self.assertTrue(proof._valid_private_journal_archive(data))
            self.assertLessEqual(len(data), proof.MAX_PRIVATE_WORKER_JOURNAL_ARCHIVE_BYTES)
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
                self.assertEqual(archive.getnames(), [proof.PRIVATE_WORKER_JOURNAL_NAME])
                member = archive.getmembers()[0]
                self.assertEqual((member.uid, member.gid, member.mode, member.size), (0, 0, 0o600, 4096))
                self.assertEqual(archive.extractfile(member).read(), journal.read_bytes())

    def test_root_helper_rejects_links_modes_nonregular_missing_oversize_and_unowned_file(self):
        for kind in ("missing", "symlink", "hardlink", "mode", "directory", "oversize", "unowned", "parent-mode"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                parent.chmod(0o755)
                journal = parent / proof.PRIVATE_WORKER_JOURNAL_NAME
                if kind == "directory":
                    journal.mkdir(mode=0o600)
                elif kind == "symlink":
                    journal.symlink_to(parent / "unrelated")
                elif kind != "missing":
                    journal.write_bytes(b"x" * (4097 if kind == "oversize" else 4))
                    journal.chmod(0o644 if kind == "mode" else 0o600)
                    if kind == "hardlink": os.link(journal, parent / "other-name")
                if kind == "parent-mode": parent.chmod(0o777)
                original = os.fstat
                def inspect(fd):
                    info = original(fd)
                    if kind == "unowned" and stat.S_ISREG(info.st_mode):
                        values = {name: getattr(info, name) for name in ("st_dev", "st_ino", "st_uid", "st_gid", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                        return SimpleNamespace(**{**values, "st_uid": os.geteuid() + 1})
                    return info
                fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    with patch.object(proof.os, "fstat", side_effect=inspect):
                        with self.assertRaises((ValueError, OSError)):
                            self.helper()(fd, expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid())
                finally:
                    os.close(fd)

    def test_root_helper_rejects_file_change_and_read_error_without_exporting_archive(self):
        for change in (False, True):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as directory:
                parent = Path(directory)
                parent.chmod(0o755)
                journal = parent / proof.PRIVATE_WORKER_JOURNAL_NAME
                journal.write_bytes(b"journal")
                journal.chmod(0o600)
                original = os.read
                def read(fd, limit):
                    if not change:
                        raise OSError(5, "secret-779")
                    data = original(fd, limit)
                    journal.write_bytes(b"changed journal")
                    return data
                fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    with patch.object(proof.os, "read", side_effect=read):
                        with self.assertRaises((ValueError, OSError)):
                            self.helper()(fd, expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid())
                finally:
                    os.close(fd)

    def test_driver_keeps_archive_600_private_and_root_command_has_no_arbitrary_path(self):
        with portable_runtime_workspace() as directory:
            parent = Path(directory)
            parent.chmod(0o755)
            public = parent / "proof"
            public.mkdir(mode=0o755)
            data = self.archive()
            with patch.object(Path, "lstat", StructuralVerificationProofTests.protected_lstat(parent)), \
                 patch.object(proof, "root_command", return_value=(0, data, b"secret-779 stderr")) as root:
                self.assertTrue(proof.retain_private_worker_journal(parent, public))
                self.assertFalse(proof.retain_private_worker_journal(parent, public))
            private = public / "private-diagnostics"
            archive = private / proof.PRIVATE_WORKER_JOURNAL_ARCHIVE
            self.assertEqual(archive.read_bytes(), data)
            self.assertEqual(stat.S_IMODE(private.stat().st_mode), 0o700)
            self.assertEqual(stat.S_IMODE(archive.stat().st_mode), 0o600)
            command = root.call_args.args[0]
            self.assertEqual(command[:4], ["/usr/bin/python3", "-I", "-c", proof.PRIVATE_WORKER_JOURNAL_ROOT_SCRIPT])
            self.assertEqual(command[4], parent.name[len(proof.RUNTIME_WORKSPACE_PREFIX):])
            self.assertNotIn(str(parent), command)
            self.assertNotIn(str(public), command)
            self.assertEqual(root.call_args.kwargs["timeout"], 10)
            self.assertTrue(root.call_args.kwargs["binary_output"])

    def test_archive_validation_and_root_failures_cannot_copy_invalid_or_public_data(self):
        invalid = (b"secret-779", self.archive(mode=0o644), self.archive(uid=1),
                   self.archive(name="unexpected.log"), self.archive(type=tarfile.SYMTYPE, linkname="secret-779"),
                   self.archive(b"x" * 4097), self.archive() + b"secret-779",
                   b"x" * (proof.MAX_PRIVATE_WORKER_JOURNAL_ARCHIVE_BYTES + 1))
        for data in invalid:
            with self.subTest(length=len(data)):
                self.assertFalse(proof._valid_private_journal_archive(data))
        for result in ((1, b"secret-779", b"secret-779"), (0, invalid[0], b""),
                       proof.ProofFailure("secret-779 copy failure")):
            with self.subTest(result=type(result).__name__), portable_runtime_workspace() as directory:
                parent = Path(directory)
                parent.chmod(0o755)
                public = parent / "proof"
                public.mkdir()
                with patch.object(Path, "lstat", StructuralVerificationProofTests.protected_lstat(parent)), \
                     patch.object(proof, "root_command", side_effect=result if isinstance(result, Exception) else None,
                                  return_value=result):
                    self.assertFalse(proof.retain_private_worker_journal(parent, public))
                self.assertFalse((public / "private-diagnostics").exists())
        with patch.object(proof, "root_command") as root:
            self.assertFalse(proof.retain_private_worker_journal(Path("/tmp/arbitrary"), Path("/tmp/arbitrary")))
            root.assert_not_called()

    def test_observation_failure_retains_only_private_archive_and_original_checkpoint_json(self):
        safe = {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                "cause": "worker-protocol-incomplete", "operation": "worker-exit",
                "worker_main_code": 1, "worker_main_status": 1, "broker_ready_seen": True,
                "broker_wait_completed": False, "broker_exited": False, "broker_work_closed": True,
                "broker_active_handlers": 0, "broker_active_runs": 0, "worker_journal_codes": ["ASEVD402"]}
        for kind, archive_result in (("retained", (0, self.archive(), b"secret-779")),
                                     ("root-failed", (1, b"secret-779", b"secret-779")),
                                     ("timeout", proof.ProofFailure("secret-779 timeout")),
                                     ("copy-failed", (0, self.archive(), b"secret-779"))):
            with self.subTest(kind=kind), portable_runtime_workspace() as directory:
                parent = Path(directory)
                parent.chmod(0o755)
                public = parent / "proof"
                public.mkdir()
                if kind == "copy-failed":
                    (public / "private-diagnostics").symlink_to(public, target_is_directory=True)
                with patch.object(Path, "lstat", StructuralVerificationProofTests.protected_lstat(parent)), \
                     patch.object(proof, "root_command", side_effect=[
                         (1, b"secret-779 stdout", b"secret-779 stderr"),
                         (0, json.dumps(safe).encode(), b"secret-779 query stderr"), archive_result]):
                    with self.assertRaises(proof.ProofFailure) as failure:
                        proof.run_observation_launcher(["launcher"], parent, public)
                self.assertIn("Production Observation launcher exited 1.", str(failure.exception))
                self.assertNotIn("secret-779", str(failure.exception))
                self.assertEqual(json.loads((public / "launcher-failure.json").read_text()), safe)
                self.assertNotIn("secret-779", (public / "launcher-failure.json").read_text())
                private = public / "private-diagnostics/worker-journal.tar"
                self.assertEqual(private.exists(), kind == "retained")


class LauncherWorkspaceProofTests(unittest.TestCase):
    def test_protected_parent_is_traversable_without_changing_private_children(self):
        with portable_runtime_workspace() as directory:
            parent = Path(directory)
            private = parent / "private-cache"
            private.mkdir(mode=0o700)
            original_lstat = Path.lstat
            frozen = False
            before = parent.lstat()

            def protected_stat(path):
                info = original_lstat(path)
                if path == parent and frozen:
                    fields = list(info)
                    fields[0], fields[4], fields[5] = stat.S_IFDIR | 0o755, 0, 0
                    return os.stat_result(fields)
                return info

            def freeze(*args, **kwargs):
                nonlocal frozen
                frozen = True
                return 0, b"", b""

            with patch.object(proof, "root_command", side_effect=freeze) as command, \
                 patch.object(Path, "lstat", protected_stat):
                proof.protect_launcher_workspace(parent)
            self.assertEqual(command.call_args.args[0], [
                "/usr/bin/python3", "-I", "-c", proof.RUNTIME_WORKSPACE_ROOT_SCRIPT, "freeze",
                parent.name[len(proof.RUNTIME_WORKSPACE_PREFIX):], str(os.geteuid()), str(os.getegid()),
                str(before.st_dev), str(before.st_ino),
            ])
            self.assertEqual(stat.S_IMODE(private.stat().st_mode), 0o700)

    def test_unsafe_or_unprotected_parent_cannot_reach_the_launcher(self):
        with portable_runtime_workspace() as directory:
            parent = Path(directory)
            link = parent / "appsurface-evidencehost-runtime-link"
            link.symlink_to(parent, target_is_directory=True)
            for candidate in (parent / "missing", link, Path("relative"), parent / "arbitrary"):
                with self.subTest(candidate=candidate), patch.object(proof, "root_command") as command:
                    with self.assertRaises(proof.ProofFailure):
                        proof.protect_launcher_workspace(candidate)
                    command.assert_not_called()
            with patch.object(proof, "root_command", return_value=(0, b"", b"")):
                with self.assertRaisesRegex(proof.ProofFailure, "protection did not take effect"):
                    proof.protect_launcher_workspace(parent)

    def test_root_creation_is_one_fixed_uuid_child_with_private_driver_owner(self):
        token = "a" * 32
        info = os.stat_result((stat.S_IFDIR | 0o700, 42, 1, 2, os.geteuid(), os.getegid(), 0, 0, 0, 0))
        with patch.object(proof.uuid, "uuid4", return_value=SimpleNamespace(hex=token)), \
             patch.object(proof, "root_command", return_value=(0, b"", b"")) as command, \
             patch.object(Path, "lstat", return_value=info):
            parent = proof.create_launcher_workspace()
        self.assertEqual(parent, Path("/run") / (proof.RUNTIME_WORKSPACE_PREFIX + token))
        self.assertEqual(command.call_args.args[0], ["/usr/bin/python3", "-I", "-c", proof.RUNTIME_WORKSPACE_ROOT_SCRIPT,
            "create", token, str(os.geteuid()), str(os.getegid())])
        self.assertEqual(command.call_args.kwargs["timeout"], 15)
        bindings = {"EVIDENCE_BASE_REVISION": "b" * 40, "EVIDENCE_SUBJECT_REVISION": "c" * 40,
                    "EVIDENCE_WORKFLOW_IDENTITY": "workflow", "EVIDENCE_RUN_ID": "12/1"}
        args = proof.launcher_args(tool_root=parent / "tool-root", policy_file=parent / "tool-root/policy",
            output_parent=parent / "output-parent", bindings=bindings, mode="observation", slot="slot",
            subject_root=parent / "subject-source")
        self.assertEqual(args[args.index("--subject-root") + 1], str(parent / "subject-source"))
        self.assertEqual(args[args.index("--solution") + 1], proof.SUBJECT_PROJECT_RELATIVE)
        self.assertEqual(args[args.index("--subject-revision") + 1], "c" * 40)
        launcher = proof.load_launcher_contract()
        properties = launcher.worker_unit_properties("worker", parent / "tool-root", Path("/run/subject"),
            Path("/run/test-output"), parent / "output-parent", 90)
        self.assertEqual(properties["PrivateTmp"], "yes")
        self.assertEqual(properties["ProtectSystem"], "strict")
        self.assertEqual(properties["ReadOnlyPaths"], str(parent / "tool-root") + " /run/subject")
        self.assertEqual(properties["ReadWritePaths"], str(parent / "output-parent"))
        self.assertEqual(properties["InaccessiblePaths"], "/run/test-output")

    def test_creation_rejects_failures_shared_linked_wrong_owner_or_root_identity_without_echo(self):
        good = (stat.S_IFDIR | 0o700, 42, 1, 2, os.geteuid(), os.getegid(), 0, 0, 0, 0)
        for kind in ("command", "stdout", "missing", "symlink", "mode", "owner", "group"):
            fields = list(good)
            if kind == "symlink": fields[0] = stat.S_IFLNK | 0o700
            if kind == "mode": fields[0] = stat.S_IFDIR | 0o755
            if kind == "owner": fields[4] += 1
            if kind == "group": fields[5] += 1
            result = (1 if kind == "command" else 0, b"secret-779" if kind == "stdout" else b"", b"secret-779")
            with self.subTest(kind=kind), patch.object(proof, "root_command", return_value=result), \
                 patch.object(Path, "lstat", side_effect=OSError("secret-779") if kind == "missing" else None,
                              return_value=os.stat_result(fields)):
                with self.assertRaises(proof.ProofFailure) as failure:
                    proof.create_launcher_workspace()
                self.assertNotIn("secret-779", str(failure.exception))
        for uid, gid in ((0, 1001), (1001, 0), (True, 1001), (4294967295, 1001)):
            with self.subTest(uid=uid, gid=gid), patch.object(proof.os, "geteuid", return_value=uid), \
                 patch.object(proof.os, "getegid", return_value=gid), patch.object(proof, "root_command") as command:
                with self.assertRaises(proof.ProofFailure): proof.create_launcher_workspace()
                command.assert_not_called()


class RootWorkspaceOperationTests(unittest.TestCase):
    def execute(self, mode, *, child_owner=0, child_mode=0o700, inode=42, token="a" * 32,
                parent_mode=0o755, mkdir_error=None):
        parent = SimpleNamespace(st_uid=0, st_mode=stat.S_IFDIR | parent_mode)
        child = SimpleNamespace(st_uid=child_owner, st_gid=0 if child_owner==0 else 1001,
                                st_mode=stat.S_IFDIR | child_mode, st_dev=1, st_ino=inode)
        args = ["-c", mode, token, "1001", "1001"] + (["1", "42"] if mode == "freeze" else [])
        with patch.object(proof.sys, "argv", args), patch.object(proof.os, "geteuid", return_value=0), \
             patch.object(proof.os, "open", side_effect=[10, 11]) as opened, \
             patch.object(proof.os, "fstat", side_effect=[parent, child]), \
             patch.object(proof.os, "mkdir", side_effect=mkdir_error) as mkdir, \
             patch.object(proof.os, "fchown") as chown, patch.object(proof.os, "fchmod") as chmod, \
             patch.object(proof.os, "close"):
            error = None
            try:
                exec(proof.RUNTIME_WORKSPACE_ROOT_SCRIPT, {})
            except (SystemExit, OSError) as caught:
                error = caught
        return error, opened, mkdir, chown, chmod

    def test_root_script_creates_exclusively_and_freezes_pinned_inode_without_recursive_changes(self):
        error, opened, mkdir, chown, chmod = self.execute("create")
        self.assertIsNone(error)
        self.assertEqual(opened.call_args_list[0].args[0], "/run")
        self.assertTrue(opened.call_args_list[0].args[1] & os.O_NOFOLLOW)
        mkdir.assert_called_once_with(proof.RUNTIME_WORKSPACE_PREFIX + "a" * 32, 0o700, dir_fd=10)
        chown.assert_called_once_with(11, 1001, 1001)
        chmod.assert_called_once_with(11, 0o700)
        error, opened, mkdir, chown, chmod = self.execute("freeze", child_owner=1001)
        self.assertIsNone(error)
        mkdir.assert_not_called()
        self.assertTrue(opened.call_args.args[1] & os.O_NOFOLLOW)
        chown.assert_called_once_with(11, 0, 0)
        chmod.assert_called_once_with(11, 0o755)

    def test_root_script_rejects_reuse_arbitrary_name_shared_parent_owner_and_inode_changes(self):
        for mode, values in (("create", {"mkdir_error": FileExistsError()}),
                             ("create", {"token": "../arbitrary"}), ("create", {"parent_mode": 0o775}),
                             ("freeze", {"child_owner": 1002}), ("freeze", {"child_owner": 1001, "inode": 43}),
                             ("freeze", {"child_owner": 1001, "child_mode": 0o755})):
            with self.subTest(mode=mode, values=values):
                error, _open, _mkdir, chown, chmod = self.execute(mode, **values)
                self.assertIsNotNone(error)
                chown.assert_not_called()
                chmod.assert_not_called()


if __name__ == "__main__":
    unittest.main()
