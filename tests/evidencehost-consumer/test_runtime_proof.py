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


class PrivateFailureOutputRetentionTests(unittest.TestCase):
    """Actual portable FD copies and private failure preservation; no root admission."""

    @staticmethod
    def helper():
        namespace = {"__name__": "private_failure_output_portable_control"}
        exec(compile(proof.PRIVATE_FAILURE_OUTPUT_ROOT_SCRIPT, "<private failure output helper>", "exec"), namespace)
        return namespace["archive_from_output"]

    @contextmanager
    def output(self):
        with tempfile.TemporaryDirectory() as directory:
            outer = Path(directory)
            outer.chmod(0o755)
            anchor = outer / ("run-" + "a" * 12)
            anchor.mkdir(mode=0o700)
            slot = anchor / "observation-123"
            slot.mkdir(mode=0o700)
            for name in proof.PRIVATE_FAILURE_OUTPUT_NAMES:
                path = slot / name
                path.write_bytes(("private-canary-779 " + name).encode())
                path.chmod(0o600)
            fd = os.open(outer, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                yield outer, anchor, slot, fd
            finally:
                os.close(fd)

    def capture(self, fd, slot="observation-123"):
        return self.helper()(fd, slot, expected_root_uid=os.geteuid(), expected_root_gid=os.getegid())

    def test_fixed_files_real_fds_maximum_bounds_and_canary_are_private_canonical_ustar(self):
        with self.output() as (_, _, slot, fd):
            (slot / "unselected.json").write_bytes(b"not selected")
            for name in proof.PRIVATE_FAILURE_OUTPUT_NAMES:
                (slot / name).write_bytes(b"private-canary-779" + b"x" * (131072 - 18))
            data = self.capture(fd)
            contents = proof._private_failure_output_contents(data)
            self.assertEqual(tuple(contents), proof.PRIVATE_FAILURE_OUTPUT_NAMES)
            self.assertEqual(sum(map(len, contents.values())), proof.MAX_PRIVATE_FAILURE_OUTPUT_BYTES)
            self.assertLessEqual(len(data), proof.MAX_PRIVATE_FAILURE_ARCHIVE_BYTES)
            self.assertTrue(all(value.startswith(b"private-canary-779") for value in contents.values()))
            with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
                self.assertTrue(all((m.uid, m.gid, m.mode) == (0, 0, 0o600) for m in archive.getmembers()))

    def test_missing_files_retain_available_subset_but_no_files_and_wrong_slot_fail(self):
        with self.output() as (_, _, slot, fd):
            for name in proof.PRIVATE_FAILURE_OUTPUT_NAMES[1:]:
                (slot / name).unlink()
            self.assertEqual(list(proof._private_failure_output_contents(self.capture(fd))), [proof.PRIVATE_FAILURE_OUTPUT_NAMES[0]])
            (slot / proof.PRIVATE_FAILURE_OUTPUT_NAMES[0]).unlink()
            for selected in (slot.name, "missing", "../observation-123"):
                with self.subTest(selected=selected), self.assertRaises((ValueError, OSError)):
                    self.capture(fd, selected)

    def test_unsafe_source_links_owners_modes_nonregular_and_oversize_reject(self):
        for kind in ("symlink", "hardlink", "directory", "fifo", "mode", "owner", "oversize", "outer-mode", "anchor-mode", "slot-mode", "extra-anchor"):
            with self.subTest(kind=kind), self.output() as (outer, anchor, slot, fd):
                target = slot / proof.PRIVATE_FAILURE_OUTPUT_NAMES[0]
                if kind in ("symlink", "directory", "fifo"):
                    target.unlink()
                    if kind == "symlink": target.symlink_to(slot / proof.PRIVATE_FAILURE_OUTPUT_NAMES[1])
                    elif kind == "directory": target.mkdir(mode=0o600)
                    else: os.mkfifo(target, 0o600)
                if kind == "hardlink": os.link(target, slot / "alias")
                if kind == "mode": target.chmod(0o644)
                if kind == "oversize": target.write_bytes(b"x" * 131073)
                if kind == "outer-mode": outer.chmod(0o777)
                if kind == "anchor-mode": anchor.chmod(0o755)
                if kind == "slot-mode": slot.chmod(0o755)
                if kind == "extra-anchor": (outer / ("run-" + "b" * 12)).mkdir()
                original = os.fstat
                def inspect(opened):
                    info = original(opened)
                    if kind == "owner" and stat.S_ISREG(info.st_mode):
                        values = {n: getattr(info, n) for n in ("st_dev", "st_ino", "st_uid", "st_gid", "st_mode", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                        return SimpleNamespace(**{**values, "st_uid": info.st_uid + 1})
                    return info
                with patch.object(proof.os, "fstat", side_effect=inspect), self.assertRaises((ValueError, OSError)):
                    self.capture(fd)

    def test_retained_file_and_directory_substitution_or_same_inode_mutation_reject(self):
        for kind in ("file", "directory", "mutation", "read-error"):
            with self.subTest(kind=kind), self.output() as (_, anchor, slot, fd):
                original = os.read
                changed = False
                def read(opened, limit):
                    nonlocal changed
                    if kind == "read-error": raise OSError(5, "private-canary-779")
                    data = original(opened, limit)
                    if not changed:
                        changed = True
                        target = slot / proof.PRIVATE_FAILURE_OUTPUT_NAMES[0]
                        if kind == "file":
                            target.rename(slot / "replaced")
                            target.write_bytes(data); target.chmod(0o600)
                        elif kind == "mutation": target.write_bytes(b"changed")
                        else:
                            slot.rename(anchor / "replaced-slot")
                            slot.mkdir(mode=0o700)
                    return data
                with patch.object(proof.os, "read", side_effect=read), self.assertRaises((ValueError, OSError)):
                    self.capture(fd)

    @staticmethod
    def checkpoint():
        return {"schema": "evidence-launcher-failure-v1", "error_class": "LauncherError",
                "cause": "worker-unsuccessful", "operation": "worker-exit", "worker_main_code": 1,
                "worker_main_status": 1, "broker_ready_seen": True, "broker_wait_completed": True,
                "broker_exited": True, "broker_work_closed": True, "broker_active_handlers": 0, "broker_active_runs": 0}

    def test_archive_validator_rejects_trailing_canary_and_hostile_member_metadata(self):
        with self.output() as (_, _, _, fd):
            data = self.capture(fd)
        self.assertIsNone(proof._private_failure_output_contents(data + b"private-canary-779"))
        self.assertIsNone(proof._private_failure_output_contents(b"x" * 409601))
        for change in ({"mode": 0o644}, {"uid": 1}, {"name": "../outside"}, {"type": tarfile.SYMTYPE, "linkname": "canary"}):
            result = io.BytesIO()
            with tarfile.open(fileobj=result, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                member = tarfile.TarInfo(proof.PRIVATE_FAILURE_OUTPUT_NAMES[0]);member.mode=0o600
                for name, value in change.items(): setattr(member, name, value)
                archive.addfile(member)
            self.assertIsNone(proof._private_failure_output_contents(result.getvalue()))

    def test_driver_private_files_exclusive_collision_and_original_failure_preserved(self):
        with self.output() as (_, _, _, fd): data = self.capture(fd)
        for kind in ("retained", "collision", "missing", "capture-error"):
            with self.subTest(kind=kind), portable_runtime_workspace() as directory:
                work = Path(directory); work.chmod(0o755)
                output = work / "output-parent"; output.mkdir(mode=0o755)
                public = work / "proof"; public.mkdir(mode=0o755)
                original_lstat = Path.lstat
                def protected(path):
                    info = original_lstat(path)
                    if path in (work, output):
                        fields = list(info); fields[4] = fields[5] = 0
                        return os.stat_result(fields)
                    return info
                if kind == "collision":
                    private = public / "private-diagnostics"; private.mkdir(mode=0o700)
                    capture = private / "failure-output"; capture.mkdir(mode=0o700)
                    (capture / "sentinel").write_bytes(b"unchanged")
                safe = self.checkpoint()
                capture_result = (0, data, b"private-canary-779") if kind in ("retained", "collision") else (1, b"", b"canary")
                if kind == "capture-error": capture_result = proof.ProofFailure("private-canary-779")
                with patch.object(Path, "lstat", protected), patch.object(proof, "retain_private_worker_journal", return_value=False), \
                     patch.object(proof, "root_command", side_effect=[(1, b"private-canary-779", b"private-canary-779"),
                        (0, json.dumps(safe).encode(), b""), capture_result]):
                    with self.assertRaises(proof.ProofFailure) as failure:
                        proof.run_observation_launcher(["launcher"], work, public, output_parent=output, slot="observation-123")
                self.assertIn("Production Observation launcher exited 1.", str(failure.exception))
                self.assertNotIn("private-canary-779", str(failure.exception))
                self.assertEqual(json.loads((public / "launcher-failure.json").read_text()), safe)
                archive = public / "private-diagnostics/failure-output" / proof.PRIVATE_FAILURE_OUTPUT_ARCHIVE
                self.assertEqual(archive.exists(), kind == "retained")
                if kind == "retained":
                    self.assertEqual(archive.read_bytes(), data)
                    for path in archive.parent.iterdir(): self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
                if kind == "collision": self.assertEqual((archive.parent / "sentinel").read_bytes(), b"unchanged")

    def test_unconfirmed_exit_or_unselected_path_cannot_invoke_capture(self):
        safe = self.checkpoint()
        with self.output() as (_, _, _, fd):
            data = self.capture(fd)
        with portable_runtime_workspace() as directory:
            work = Path(directory)
            work.chmod(0o755)
            output = work / "output-parent"
            output.mkdir(mode=0o755)
            public = work / "proof"
            public.mkdir(mode=0o755)
            original_lstat = Path.lstat

            def protected_lstat_rootstats(path):
                info = original_lstat(path)
                if path in (work, output):
                    fields = list(info)
                    fields[4] = fields[5] = 0
                    return os.stat_result(fields)
                return info

            with patch.object(Path, "lstat", protected_lstat_rootstats):
                with self.subTest(case="valid-checkpoint"), \
                     patch.object(proof, "root_command", return_value=(0, data, b"")) as command:
                    self.assertTrue(proof.retain_private_failure_output(work, output, "observation-123", public, safe))
                    command.assert_called_once()
                    self.assertEqual(command.call_args.args[0][-1], "observation-123")
                for name, value in (("cause", "worker-protocol-incomplete"), ("operation", "worker-start"),
                                    ("worker_main_code", 2), ("worker_main_status", 0),
                                    ("broker_ready_seen", False), ("broker_wait_completed", False),
                                    ("broker_exited", False), ("broker_work_closed", False),
                                    ("broker_active_handlers", 1), ("broker_active_runs", 1)):
                    with self.subTest(field=name, value=value), patch.object(proof, "root_command") as command:
                        self.assertFalse(proof.retain_private_failure_output(
                            work, output, "observation-123", public, {**safe, name: value}))
                        command.assert_not_called()
                for selected_output, selected_slot in ((work / "unselected-output", "observation-123"),
                                                       (output, "../observation-123")):
                    with self.subTest(output=selected_output.name, slot=selected_slot), \
                         patch.object(proof, "root_command") as command:
                        self.assertFalse(proof.retain_private_failure_output(
                            work, selected_output, selected_slot, public, safe))
                        command.assert_not_called()


class PrivateSubjectPrefixRetentionTests(unittest.TestCase):
    """Real portable FD/archive controls and mocked privilege dispatch; no root admission."""

    @staticmethod
    def helper():
        namespace = {"__name__": "private_subject_prefix_portable_control"}
        exec(compile(proof.PRIVATE_SUBJECT_PREFIX_ROOT_SCRIPT, "<subject-prefix helper>", "exec"), namespace)
        return namespace["archive_subject_prefixes"]

    @contextmanager
    def workspace(self):
        with portable_runtime_workspace() as directory:
            work = Path(directory); work.chmod(0o755)
            child = work / proof.PRIVATE_SUBJECT_PREFIX_DIRECTORY; child.mkdir(mode=0o700)
            for name in proof.PRIVATE_SUBJECT_PREFIX_NAMES:
                path = child / name; path.write_bytes(b"private-prefix-canary\x00\xff"); path.chmod(0o600)
            fd = os.open(work, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                yield work, child, fd
            finally:
                os.close(fd)

    def capture(self, fd, **kwargs):
        return self.helper()(fd, expected_root_uid=os.geteuid(), expected_root_gid=os.getegid(), **kwargs)

    @staticmethod
    def archive(contents=(b"private-prefix-canary\x00\xff", b""), *, order=None, change=None):
        output = io.BytesIO()
        names = proof.PRIVATE_SUBJECT_PREFIX_NAMES if order is None else order
        with tarfile.open(fileobj=output, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for index, name in enumerate(names):
                content = contents[index % len(contents)]
                item = tarfile.TarInfo(name); item.mode = 0o600; item.size = len(content)
                if change:
                    for key, value in change.items(): setattr(item, key, value)
                archive.addfile(item, io.BytesIO(content) if item.isreg() else None)
        return output.getvalue()

    @staticmethod
    def checkpoint():
        return PrivateFailureOutputRetentionTests.checkpoint()

    @contextmanager
    def rootstats(self, work, **changes):
        original = Path.lstat
        def selected(path):
            info = original(path)
            if path == work:
                fields = list(info); fields[4] = fields[5] = 0
                info = os.stat_result(fields)
                if changes:
                    values = {name: getattr(info, name) for name in
                              ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid")}
                    info = SimpleNamespace(**{**values, **changes})
            return info
        with patch.object(Path, "lstat", selected): yield

    def test_real_binary_prefixes_maximum_and_empty_have_exact_canonical_metadata(self):
        for sizes in ((519168, 519168), (0, 0)):
            with self.subTest(sizes=sizes), self.workspace() as (_, child, fd):
                contents = []
                for name, size in zip(proof.PRIVATE_SUBJECT_PREFIX_NAMES, sizes):
                    data = (b"private-prefix-canary\x00\xff" + b"x" * size)[:size]
                    (child / name).write_bytes(data); contents.append(data)
                archive = self.capture(fd)
                self.assertTrue(proof._valid_private_subject_prefix_archive(archive))
                self.assertLessEqual(len(archive), 1024 * 1024)
                self.assertEqual(archive, self.archive(tuple(contents)))
                with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as opened:
                    self.assertEqual(list(proof.PRIVATE_SUBJECT_PREFIX_NAMES), opened.getnames())
                    for member, data in zip(opened.getmembers(), contents):
                        self.assertEqual((0, 0, 0, 0o600, len(data)),
                                         (member.uid, member.gid, member.mtime, member.mode, member.size))
                        self.assertEqual(data, opened.extractfile(member).read())

    def test_one_unsafe_file_or_directory_field_rejects_after_valid_fd_control(self):
        for kind in ("missing", "file-link", "directory-link", "hardlink", "file-mode", "child-mode",
                     "work-mode", "oversize", "extra", "nonregular", "owner", "group"):
            with self.subTest(kind=kind), self.workspace() as (work, child, fd):
                self.assertTrue(proof._valid_private_subject_prefix_archive(self.capture(fd)))
                target = child / proof.PRIVATE_SUBJECT_PREFIX_NAMES[0]
                if kind == "missing": target.unlink()
                elif kind == "file-link": target.unlink(); target.symlink_to(child / proof.PRIVATE_SUBJECT_PREFIX_NAMES[1])
                elif kind == "directory-link": child.rename(work / "original"); child.symlink_to(work / "original")
                elif kind == "hardlink": os.link(target, work / "linked")
                elif kind == "file-mode": target.chmod(0o644)
                elif kind == "child-mode": child.chmod(0o755)
                elif kind == "work-mode": work.chmod(0o775)
                elif kind == "oversize": target.write_bytes(b"x" * 519169)
                elif kind == "extra": (child / "extra").write_bytes(b"private-prefix-canary")
                elif kind == "nonregular": target.unlink(); target.mkdir()
                original = os.fstat
                def inspected(opened):
                    info = original(opened)
                    if kind in ("owner", "group") and stat.S_ISREG(info.st_mode):
                        fields = {name: getattr(info, name) for name in
                                  ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                        fields["st_uid" if kind == "owner" else "st_gid"] += 1
                        return SimpleNamespace(**fields)
                    return info
                with patch.object(proof.os, "fstat", side_effect=inspected), self.assertRaises((OSError, ValueError)):
                    self.capture(fd)

    def test_substitution_growth_same_inode_mutation_and_read_failure_reject(self):
        for kind in ("file", "directory", "growth", "same-inode", "read-error", "extra-after-read"):
            with self.subTest(kind=kind), self.workspace() as (work, child, fd):
                self.assertTrue(proof._valid_private_subject_prefix_archive(self.capture(fd)))
                original = os.read; changed = False
                def reading(opened, bound):
                    nonlocal changed
                    if kind == "read-error": raise OSError(5, "private-prefix-canary")
                    data = original(opened, bound)
                    if not changed:
                        changed = True; target = child / proof.PRIVATE_SUBJECT_PREFIX_NAMES[0]
                        if kind == "file": target.rename(work / "old"); target.write_bytes(data); target.chmod(0o600)
                        elif kind == "directory": child.rename(work / "old"); child.mkdir(mode=0o700)
                        elif kind == "growth":
                            with target.open("ab") as output: output.write(b"growth")
                        elif kind == "same-inode": target.write_bytes(b"z" * target.stat().st_size)
                        else: (child / "extra").write_bytes(b"extra")
                    return data
                with patch.object(proof.os, "read", side_effect=reading) as read, self.assertRaises((OSError, ValueError)):
                    self.capture(fd)
                self.assertTrue(read.called)

    def test_expired_or_mid_read_deadline_cannot_export_archive(self):
        with self.workspace() as (_, _, fd), patch.object(proof.os, "read") as read:
            with self.assertRaises(ValueError): self.capture(fd, deadline=0)
            read.assert_not_called()
        with self.workspace() as (_, _, fd):
            original = os.read
            with patch.object(proof.time, "monotonic", return_value=1) as clock:
                def reading(opened, bound):
                    data = original(opened, bound); clock.return_value = 10; return data
                with patch.object(proof.os, "read", side_effect=reading) as read, self.assertRaises(ValueError):
                    self.capture(fd, deadline=5)
                self.assertTrue(read.called)

    def test_archive_extra_reordered_missing_noncanonical_or_unsafe_member_rejects(self):
        valid = self.archive(); self.assertTrue(proof._valid_private_subject_prefix_archive(valid))
        malformed = [valid + b"private-prefix-canary", valid + b"\x00" * 10240, b"x" * 1048577,
                     self.archive(order=("stdout.prefix",)), self.archive(order=("stderr.prefix", "stdout.prefix")),
                     self.archive(order=("stdout.prefix", "stderr.prefix", "extra")),
                     self.archive(order=("stdout.prefix", "stdout.prefix")),
                     self.archive(contents=(b"x" * 519169, b""))]
        for change in ({"uid": 1}, {"gid": 1}, {"mtime": 1}, {"mode": 0o644}, {"uname": "canary"},
                       {"type": tarfile.SYMTYPE, "linkname": "canary"}):
            malformed.append(self.archive(change=change))
        for data in malformed:
            with self.subTest(size=len(data)): self.assertFalse(proof._valid_private_subject_prefix_archive(data))

    def test_valid_selected_root_dispatch_and_exclusive_private_archive(self):
        with self.workspace() as (work, _, fd):
            public = work / "proof"; public.mkdir(mode=0o755)
            data = self.capture(fd); info = work.lstat()
            with self.rootstats(work), patch.object(proof, "root_command", return_value=(0, data, b"private-prefix-canary")) as command:
                self.assertTrue(proof.retain_private_subject_prefixes(work, public, self.checkpoint()))
                command.assert_called_once()
                self.assertEqual(command.call_args.args[0], ["/usr/bin/python3", "-I", "-c", proof.PRIVATE_SUBJECT_PREFIX_ROOT_SCRIPT,
                    work.name[len(proof.RUNTIME_WORKSPACE_PREFIX):], str(info.st_dev), str(info.st_ino)])
                self.assertEqual(command.call_args.kwargs["timeout"], 5)
                self.assertTrue(command.call_args.kwargs["binary_output"])
                self.assertFalse(proof.retain_private_subject_prefixes(work, public, self.checkpoint()))
            private = public / "private-diagnostics"; archive = private / proof.PRIVATE_SUBJECT_PREFIX_ARCHIVE
            self.assertEqual(data, archive.read_bytes()); self.assertEqual(0o700, stat.S_IMODE(private.stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE(archive.stat().st_mode)); self.assertEqual(1, archive.stat().st_nlink)

    def test_each_bad_checkpoint_or_workspace_field_blocks_root_io_with_valid_positive_foundation(self):
        with self.workspace() as (work, _, fd):
            public = work / "proof"; public.mkdir(mode=0o755); data = self.capture(fd)
            with self.rootstats(work):
                with patch.object(proof, "root_command", return_value=(0, data, b"")) as command:
                    self.assertTrue(proof.retain_private_subject_prefixes(work, public, self.checkpoint()))
                    command.assert_called_once()
                for name, value in (("cause", "worker-protocol-incomplete"), ("operation", "worker-start"),
                                    ("worker_main_code", 2), ("worker_main_code", True), ("worker_main_status", 0),
                                    ("worker_main_status", True), ("broker_ready_seen", False), ("broker_wait_completed", False),
                                    ("broker_exited", False), ("broker_work_closed", False), ("broker_active_handlers", 1),
                                    ("broker_active_runs", 1), ("broker_active_runs", False), ("schema", "canary")):
                    with self.subTest(field=name, value=value), patch.object(proof, "root_command") as command:
                        self.assertFalse(proof.retain_private_subject_prefixes(work, public, {**self.checkpoint(), name: value}))
                        command.assert_not_called()
            for name, value in (("st_uid", 1), ("st_gid", 1), ("st_mode", stat.S_IFDIR | 0o775),
                                ("st_mode", stat.S_IFREG | 0o755)):
                with self.subTest(field=name), self.rootstats(work, **{name: value}), patch.object(proof, "root_command") as command:
                    self.assertFalse(proof.retain_private_subject_prefixes(work, public, self.checkpoint()))
                    command.assert_not_called()
            with patch.object(proof, "root_command") as command:
                self.assertFalse(proof.retain_private_subject_prefixes(work.parent / "not-selected", public, self.checkpoint()))
                command.assert_not_called()

    def test_copy_rejects_existing_symlink_unprotected_destination_invalid_archive_or_capture_failure(self):
        for kind in ("collision", "file-link", "directory-link", "mode", "proof-mode", "malformed", "exit", "capture-error"):
            with self.subTest(kind=kind), self.workspace() as (work, _, fd):
                data = self.capture(fd); public = work / "proof"; public.mkdir(mode=0o755)
                private = public / "private-diagnostics"
                if kind == "directory-link": private.symlink_to(public, target_is_directory=True)
                elif kind in ("collision", "file-link", "mode"):
                    private.mkdir(mode=0o755 if kind == "mode" else 0o700)
                    if kind == "collision": (private / proof.PRIVATE_SUBJECT_PREFIX_ARCHIVE).write_bytes(b"unchanged")
                    if kind == "file-link": (private / proof.PRIVATE_SUBJECT_PREFIX_ARCHIVE).symlink_to(public / "outside")
                elif kind == "proof-mode": public.chmod(0o777)
                result = (1 if kind == "exit" else 0, b"bad" if kind == "malformed" else data, b"private-prefix-canary")
                side = proof.ProofFailure("private-prefix-canary") if kind == "capture-error" else [result]
                with self.rootstats(work), patch.object(proof, "root_command", side_effect=side) as command:
                    self.assertFalse(proof.retain_private_subject_prefixes(work, public, self.checkpoint()))
                    command.assert_called_once()
                if kind == "collision": self.assertEqual(b"unchanged", (private / proof.PRIVATE_SUBJECT_PREFIX_ARCHIVE).read_bytes())

    def test_failure_always_preserves_original_safe_record_and_canary_never_becomes_public(self):
        for kind in ("retained", "missing", "capture-error"):
            with self.subTest(kind=kind), self.workspace() as (work, _, fd):
                public = work / "proof"; public.mkdir(mode=0o755); data = self.capture(fd); calls = []
                final = (0, data, b"private-prefix-canary") if kind == "retained" else (1, b"", b"private-prefix-canary")
                if kind == "capture-error": final = proof.ProofFailure("private-prefix-canary")
                with self.rootstats(work), patch.object(proof, "retain_private_worker_journal", side_effect=lambda *args: calls.append("journal")), \
                     patch.object(proof, "retain_private_failure_output", side_effect=lambda *args: calls.append("manifest")), \
                     patch.object(proof, "retain_private_vstest_traces", return_value=False), \
                     patch.object(proof, "root_command", side_effect=[(1, b"private-prefix-canary", b"private-prefix-canary"),
                         (0, json.dumps(self.checkpoint()).encode(), b""), final]) as command:
                    with self.assertRaises(proof.ProofFailure) as failure:
                        proof.run_observation_launcher(["launcher"], work, public, output_parent=work / "output-parent", slot="slot")
                    self.assertEqual(3, command.call_count)
                self.assertEqual(["journal", "manifest"], calls)
                self.assertIn("Production Observation launcher exited 1.", str(failure.exception))
                self.assertNotIn("private-prefix-canary", str(failure.exception))
                self.assertEqual(self.checkpoint(), json.loads((public / "launcher-failure.json").read_text()))
                self.assertNotIn("private-prefix-canary", (public / "launcher-failure.json").read_text())
                archive = public / "private-diagnostics" / proof.PRIVATE_SUBJECT_PREFIX_ARCHIVE
                self.assertEqual(kind == "retained", archive.exists())
                if archive.exists(): self.assertEqual(data, archive.read_bytes())

    def test_root_entry_pins_uuid_device_inode_before_actual_fd_capture(self):
        for mismatch in (False, True):
            with self.subTest(mismatched_inode=mismatch), self.workspace() as (work, _, _):
                namespace = {"__name__": "root_prefix_entry_portable_control"}
                exec(compile(proof.PRIVATE_SUBJECT_PREFIX_ROOT_SCRIPT, "<subject-prefix helper>", "exec"), namespace)
                capture = Mock(wraps=namespace["archive_subject_prefixes"])
                namespace["archive_subject_prefixes"] = capture
                actual = work.lstat(); output = io.BytesIO()
                original_open, original_fstat, original_stat = os.open, os.fstat, os.stat
                def opened(path, flags, *args, **kwargs):
                    return original_open(work.parent if path == "/run" else path, flags, *args, **kwargs)
                def root_owned(info):
                    fields = {name: getattr(info, name) for name in
                              ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                    return SimpleNamespace(**{**fields, "st_uid": 0, "st_gid": 0})
                argv = ["-c", work.name[len(proof.RUNTIME_WORKSPACE_PREFIX):], str(actual.st_dev),
                        str(actual.st_ino + int(mismatch))]
                with patch.object(proof.os, "geteuid", return_value=0), patch.object(proof.sys, "argv", argv), \
                     patch.object(proof.sys, "stdout", SimpleNamespace(buffer=output)), \
                     patch.object(proof.os, "open", side_effect=opened) as open_call, \
                     patch.object(proof.os, "fstat", side_effect=lambda fd: root_owned(original_fstat(fd))), \
                     patch.object(proof.os, "stat", side_effect=lambda *args, **kwargs: root_owned(original_stat(*args, **kwargs))):
                    if mismatch:
                        with self.assertRaises(ValueError): namespace["main"]()
                    else:
                        namespace["main"]()
                self.assertEqual("/run", open_call.call_args_list[0].args[0])
                if mismatch:
                    capture.assert_not_called(); self.assertEqual(b"", output.getvalue())
                else:
                    capture.assert_called_once(); self.assertTrue(proof._valid_private_subject_prefix_archive(output.getvalue()))

    def test_success_returns_before_all_new_diagnostic_io(self):
        with self.workspace() as (work, _, _):
            public = work / "proof"; public.mkdir(mode=0o755)
            with patch.object(proof, "root_command", return_value=(0, b"success", b"")) as command, \
                 patch.object(proof, "retain_private_subject_prefixes") as retained:
                self.assertEqual((b"success", b""), proof.run_observation_launcher(["launcher"], work, public))
                command.assert_called_once(); retained.assert_not_called()
            self.assertFalse((public / "private-diagnostics").exists())


class PrivateVstestTraceRetentionTests(unittest.TestCase):
    """Real portable FD/archive controls and mocked privilege dispatch; no root admission."""

    @staticmethod
    def helper():
        namespace = {"__name__": "private_vstest_trace_portable_control"}
        exec(compile(proof.PRIVATE_VSTEST_TRACE_ROOT_SCRIPT, "<vstest-trace helper>", "exec"), namespace)
        return namespace["archive_vstest_traces"]

    @contextmanager
    def workspace(self):
        with portable_runtime_workspace() as directory:
            work = Path(directory); work.chmod(0o755)
            child = work / proof.PRIVATE_VSTEST_TRACE_DIRECTORY; child.mkdir(mode=0o700)
            for name in proof.PRIVATE_VSTEST_TRACE_NAMES:
                path = child / name; path.write_bytes(b"private-prefix-canary\x00\xff"); path.chmod(0o600)
            fd = os.open(work, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
            try:
                yield work, child, fd
            finally:
                os.close(fd)

    def capture(self, fd, **kwargs):
        return self.helper()(fd, expected_root_uid=os.geteuid(), expected_root_gid=os.getegid(), **kwargs)

    @staticmethod
    def archive(contents=(b"private-prefix-canary\x00\xff", b""), *, order=None, change=None):
        output = io.BytesIO()
        names = proof.PRIVATE_VSTEST_TRACE_NAMES if order is None else order
        with tarfile.open(fileobj=output, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for index, name in enumerate(names):
                content = contents[index % len(contents)]
                item = tarfile.TarInfo(name); item.mode = 0o600; item.size = len(content)
                if change:
                    for key, value in change.items(): setattr(item, key, value)
                archive.addfile(item, io.BytesIO(content) if item.isreg() else None)
        return output.getvalue()

    @staticmethod
    def checkpoint():
        return PrivateFailureOutputRetentionTests.checkpoint()

    @contextmanager
    def rootstats(self, work, **changes):
        original = Path.lstat
        def selected(path):
            info = original(path)
            if path == work:
                fields = list(info); fields[4] = fields[5] = 0
                info = os.stat_result(fields)
                if changes:
                    values = {name: getattr(info, name) for name in
                              ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid")}
                    info = SimpleNamespace(**{**values, **changes})
            return info
        with patch.object(Path, "lstat", selected): yield

    def test_real_binary_traces_maximum_and_empty_have_exact_canonical_metadata(self):
        for sizes in ((262144, 262144), (0, 0)):
            with self.subTest(sizes=sizes), self.workspace() as (_, child, fd):
                contents = []
                for name, size in zip(proof.PRIVATE_VSTEST_TRACE_NAMES, sizes):
                    data = (b"private-prefix-canary\x00\xff" + b"x" * size)[:size]
                    (child / name).write_bytes(data); contents.append(data)
                archive = self.capture(fd)
                self.assertTrue(proof._valid_private_vstest_trace_archive(archive))
                self.assertLessEqual(len(archive), 768 * 1024)
                self.assertEqual(archive, self.archive(tuple(contents)))
                with tarfile.open(fileobj=io.BytesIO(archive), mode="r:") as opened:
                    self.assertEqual(list(proof.PRIVATE_VSTEST_TRACE_NAMES), opened.getnames())
                    for member, data in zip(opened.getmembers(), contents):
                        self.assertEqual((0, 0, 0, 0o600, len(data)),
                                         (member.uid, member.gid, member.mtime, member.mode, member.size))
                        self.assertEqual(data, opened.extractfile(member).read())

    def test_one_unsafe_file_or_directory_field_rejects_after_valid_fd_control(self):
        for kind in ("missing", "file-link", "directory-link", "hardlink", "file-mode", "child-mode",
                     "work-mode", "oversize", "extra", "nonregular", "owner", "group"):
            with self.subTest(kind=kind), self.workspace() as (work, child, fd):
                self.assertTrue(proof._valid_private_vstest_trace_archive(self.capture(fd)))
                target = child / proof.PRIVATE_VSTEST_TRACE_NAMES[0]
                if kind == "missing": target.unlink()
                elif kind == "file-link": target.unlink(); target.symlink_to(child / proof.PRIVATE_VSTEST_TRACE_NAMES[1])
                elif kind == "directory-link": child.rename(work / "original"); child.symlink_to(work / "original")
                elif kind == "hardlink": os.link(target, work / "linked")
                elif kind == "file-mode": target.chmod(0o644)
                elif kind == "child-mode": child.chmod(0o755)
                elif kind == "work-mode": work.chmod(0o775)
                elif kind == "oversize": target.write_bytes(b"x" * 262145)
                elif kind == "extra": (child / "extra").write_bytes(b"private-prefix-canary")
                elif kind == "nonregular": target.unlink(); target.mkdir()
                original = os.fstat
                def inspected(opened):
                    info = original(opened)
                    if kind in ("owner", "group") and stat.S_ISREG(info.st_mode):
                        fields = {name: getattr(info, name) for name in
                                  ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                        fields["st_uid" if kind == "owner" else "st_gid"] += 1
                        return SimpleNamespace(**fields)
                    return info
                with patch.object(proof.os, "fstat", side_effect=inspected), self.assertRaises((OSError, ValueError)):
                    self.capture(fd)

    def test_substitution_growth_same_inode_mutation_and_read_failure_reject(self):
        for kind in ("file", "directory", "growth", "same-inode", "read-error", "extra-after-read"):
            with self.subTest(kind=kind), self.workspace() as (work, child, fd):
                self.assertTrue(proof._valid_private_vstest_trace_archive(self.capture(fd)))
                original = os.read; changed = False
                def reading(opened, bound):
                    nonlocal changed
                    if kind == "read-error": raise OSError(5, "private-prefix-canary")
                    data = original(opened, bound)
                    if not changed:
                        changed = True; target = child / proof.PRIVATE_VSTEST_TRACE_NAMES[0]
                        if kind == "file": target.rename(work / "old"); target.write_bytes(data); target.chmod(0o600)
                        elif kind == "directory": child.rename(work / "old"); child.mkdir(mode=0o700)
                        elif kind == "growth":
                            with target.open("ab") as output: output.write(b"growth")
                        elif kind == "same-inode": target.write_bytes(b"z" * target.stat().st_size)
                        else: (child / "extra").write_bytes(b"extra")
                    return data
                with patch.object(proof.os, "read", side_effect=reading) as read, self.assertRaises((OSError, ValueError)):
                    self.capture(fd)
                self.assertTrue(read.called)

    def test_expired_or_mid_read_deadline_cannot_export_archive(self):
        with self.workspace() as (_, _, fd), patch.object(proof.os, "read") as read:
            with self.assertRaises(ValueError): self.capture(fd, deadline=0)
            read.assert_not_called()
        with self.workspace() as (_, _, fd):
            original = os.read
            with patch.object(proof.time, "monotonic", return_value=1) as clock:
                def reading(opened, bound):
                    data = original(opened, bound); clock.return_value = 10; return data
                with patch.object(proof.os, "read", side_effect=reading) as read, self.assertRaises(ValueError):
                    self.capture(fd, deadline=5)
                self.assertTrue(read.called)

    def test_archive_extra_reordered_missing_noncanonical_or_unsafe_member_rejects(self):
        valid = self.archive(); self.assertTrue(proof._valid_private_vstest_trace_archive(valid))
        malformed = [valid + b"private-prefix-canary", valid + b"\x00" * 10240, b"x" * 786433,
                     self.archive(order=("runner.log",)), self.archive(order=("collector.log", "runner.log")),
                     self.archive(order=("runner.log", "collector.log", "extra")),
                     self.archive(order=("runner.log", "runner.log")),
                     self.archive(contents=(b"x" * 262145, b""))]
        for change in ({"uid": 1}, {"gid": 1}, {"mtime": 1}, {"mode": 0o644}, {"uname": "canary"},
                       {"type": tarfile.SYMTYPE, "linkname": "canary"}):
            malformed.append(self.archive(change=change))
        for data in malformed:
            with self.subTest(size=len(data)): self.assertFalse(proof._valid_private_vstest_trace_archive(data))

    def test_valid_selected_root_dispatch_and_exclusive_private_archive(self):
        with self.workspace() as (work, _, fd):
            public = work / "proof"; public.mkdir(mode=0o755)
            data = self.capture(fd); info = work.lstat()
            with self.rootstats(work), patch.object(proof, "root_command", return_value=(0, data, b"private-prefix-canary")) as command:
                self.assertTrue(proof.retain_private_vstest_traces(work, public, self.checkpoint()))
                command.assert_called_once()
                self.assertEqual(command.call_args.args[0], ["/usr/bin/python3", "-I", "-c", proof.PRIVATE_VSTEST_TRACE_ROOT_SCRIPT,
                    work.name[len(proof.RUNTIME_WORKSPACE_PREFIX):], str(info.st_dev), str(info.st_ino)])
                self.assertEqual(command.call_args.kwargs["timeout"], 5)
                self.assertTrue(command.call_args.kwargs["binary_output"])
                self.assertFalse(proof.retain_private_vstest_traces(work, public, self.checkpoint()))
            private = public / "private-diagnostics"; archive = private / proof.PRIVATE_VSTEST_TRACE_ARCHIVE
            self.assertEqual(data, archive.read_bytes()); self.assertEqual(0o700, stat.S_IMODE(private.stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE(archive.stat().st_mode)); self.assertEqual(1, archive.stat().st_nlink)

    def test_each_bad_checkpoint_or_workspace_field_blocks_root_io_with_valid_positive_foundation(self):
        with self.workspace() as (work, _, fd):
            public = work / "proof"; public.mkdir(mode=0o755); data = self.capture(fd)
            with self.rootstats(work):
                with patch.object(proof, "root_command", return_value=(0, data, b"")) as command:
                    self.assertTrue(proof.retain_private_vstest_traces(work, public, self.checkpoint()))
                    command.assert_called_once()
                for name, value in (("cause", "worker-protocol-incomplete"), ("operation", "worker-start"),
                                    ("worker_main_code", 2), ("worker_main_code", True), ("worker_main_status", 0),
                                    ("worker_main_status", True), ("broker_ready_seen", False), ("broker_wait_completed", False),
                                    ("broker_exited", False), ("broker_work_closed", False), ("broker_active_handlers", 1),
                                    ("broker_active_runs", 1), ("broker_active_runs", False), ("schema", "canary")):
                    with self.subTest(field=name, value=value), patch.object(proof, "root_command") as command:
                        self.assertFalse(proof.retain_private_vstest_traces(work, public, {**self.checkpoint(), name: value}))
                        command.assert_not_called()
            for name, value in (("st_uid", 1), ("st_gid", 1), ("st_mode", stat.S_IFDIR | 0o775),
                                ("st_mode", stat.S_IFREG | 0o755)):
                with self.subTest(field=name), self.rootstats(work, **{name: value}), patch.object(proof, "root_command") as command:
                    self.assertFalse(proof.retain_private_vstest_traces(work, public, self.checkpoint()))
                    command.assert_not_called()
            with patch.object(proof, "root_command") as command:
                self.assertFalse(proof.retain_private_vstest_traces(work.parent / "not-selected", public, self.checkpoint()))
                command.assert_not_called()

    def test_copy_rejects_existing_symlink_unprotected_destination_invalid_archive_or_capture_failure(self):
        for kind in ("collision", "file-link", "directory-link", "mode", "proof-mode", "malformed", "exit", "capture-error"):
            with self.subTest(kind=kind), self.workspace() as (work, _, fd):
                data = self.capture(fd); public = work / "proof"; public.mkdir(mode=0o755)
                private = public / "private-diagnostics"
                if kind == "directory-link": private.symlink_to(public, target_is_directory=True)
                elif kind in ("collision", "file-link", "mode"):
                    private.mkdir(mode=0o755 if kind == "mode" else 0o700)
                    if kind == "collision": (private / proof.PRIVATE_VSTEST_TRACE_ARCHIVE).write_bytes(b"unchanged")
                    if kind == "file-link": (private / proof.PRIVATE_VSTEST_TRACE_ARCHIVE).symlink_to(public / "outside")
                elif kind == "proof-mode": public.chmod(0o777)
                result = (1 if kind == "exit" else 0, b"bad" if kind == "malformed" else data, b"private-prefix-canary")
                side = proof.ProofFailure("private-prefix-canary") if kind == "capture-error" else [result]
                with self.rootstats(work), patch.object(proof, "root_command", side_effect=side) as command:
                    self.assertFalse(proof.retain_private_vstest_traces(work, public, self.checkpoint()))
                    command.assert_called_once()
                if kind == "collision": self.assertEqual(b"unchanged", (private / proof.PRIVATE_VSTEST_TRACE_ARCHIVE).read_bytes())

    def test_failure_always_preserves_original_safe_record_and_canary_never_becomes_public(self):
        for kind in ("retained", "missing", "capture-error"):
            with self.subTest(kind=kind), self.workspace() as (work, _, fd):
                public = work / "proof"; public.mkdir(mode=0o755); data = self.capture(fd); calls = []
                final = (0, data, b"private-prefix-canary") if kind == "retained" else (1, b"", b"private-prefix-canary")
                if kind == "capture-error": final = proof.ProofFailure("private-prefix-canary")
                with self.rootstats(work), patch.object(proof, "retain_private_worker_journal", side_effect=lambda *args: calls.append("journal")), \
                     patch.object(proof, "retain_private_failure_output", side_effect=lambda *args: calls.append("manifest")), \
                     patch.object(proof, "retain_private_subject_prefixes", side_effect=lambda *args: calls.append("prefix")), \
                     patch.object(proof, "root_command", side_effect=[(1, b"private-prefix-canary", b"private-prefix-canary"),
                         (0, json.dumps(self.checkpoint()).encode(), b""), final]) as command:
                    with self.assertRaises(proof.ProofFailure) as failure:
                        proof.run_observation_launcher(["launcher"], work, public, output_parent=work / "output-parent", slot="slot")
                    self.assertEqual(3, command.call_count)
                self.assertEqual(["journal", "manifest", "prefix"], calls)
                self.assertIn("Production Observation launcher exited 1.", str(failure.exception))
                self.assertNotIn("private-prefix-canary", str(failure.exception))
                self.assertEqual(self.checkpoint(), json.loads((public / "launcher-failure.json").read_text()))
                self.assertNotIn("private-prefix-canary", (public / "launcher-failure.json").read_text())
                archive = public / "private-diagnostics" / proof.PRIVATE_VSTEST_TRACE_ARCHIVE
                self.assertEqual(kind == "retained", archive.exists())
                if archive.exists(): self.assertEqual(data, archive.read_bytes())

    def test_root_entry_pins_uuid_device_inode_before_actual_fd_capture(self):
        for mismatch in (False, True):
            with self.subTest(mismatched_inode=mismatch), self.workspace() as (work, _, _):
                namespace = {"__name__": "root_prefix_entry_portable_control"}
                exec(compile(proof.PRIVATE_VSTEST_TRACE_ROOT_SCRIPT, "<vstest-trace helper>", "exec"), namespace)
                capture = Mock(wraps=namespace["archive_vstest_traces"])
                namespace["archive_vstest_traces"] = capture
                actual = work.lstat(); output = io.BytesIO()
                original_open, original_fstat, original_stat = os.open, os.fstat, os.stat
                def opened(path, flags, *args, **kwargs):
                    return original_open(work.parent if path == "/run" else path, flags, *args, **kwargs)
                def root_owned(info):
                    fields = {name: getattr(info, name) for name in
                              ("st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink", "st_size", "st_mtime_ns", "st_ctime_ns")}
                    return SimpleNamespace(**{**fields, "st_uid": 0, "st_gid": 0})
                argv = ["-c", work.name[len(proof.RUNTIME_WORKSPACE_PREFIX):], str(actual.st_dev),
                        str(actual.st_ino + int(mismatch))]
                with patch.object(proof.os, "geteuid", return_value=0), patch.object(proof.sys, "argv", argv), \
                     patch.object(proof.sys, "stdout", SimpleNamespace(buffer=output)), \
                     patch.object(proof.os, "open", side_effect=opened) as open_call, \
                     patch.object(proof.os, "fstat", side_effect=lambda fd: root_owned(original_fstat(fd))), \
                     patch.object(proof.os, "stat", side_effect=lambda *args, **kwargs: root_owned(original_stat(*args, **kwargs))):
                    if mismatch:
                        with self.assertRaises(ValueError): namespace["main"]()
                    else:
                        namespace["main"]()
                self.assertEqual("/run", open_call.call_args_list[0].args[0])
                if mismatch:
                    capture.assert_not_called(); self.assertEqual(b"", output.getvalue())
                else:
                    capture.assert_called_once(); self.assertTrue(proof._valid_private_vstest_trace_archive(output.getvalue()))

    def test_success_returns_before_all_new_diagnostic_io(self):
        with self.workspace() as (work, _, _):
            public = work / "proof"; public.mkdir(mode=0o755)
            with patch.object(proof, "root_command", return_value=(0, b"success", b"")) as command, \
                 patch.object(proof, "retain_private_vstest_traces") as retained:
                self.assertEqual((b"success", b""), proof.run_observation_launcher(["launcher"], work, public))
                command.assert_called_once(); retained.assert_not_called()
            self.assertFalse((public / "private-diagnostics").exists())


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



class CanonicalObservationMetadataTests(unittest.TestCase):
    """Observed Pascal metadata with synthetic Passed data only, never native proof."""
    OBSERVED_PLAN_JSON = '{"ChangedPaths":[{"Kind":"modified","Path":"tests/evidencehost-consumer/RuntimeSubject/Program.cs","PreviousPath":null},{"Kind":"modified","Path":"tests/evidencehost-consumer/RuntimeSubject/RuntimeSubject.csproj","PreviousPath":null}],"ContractVersion":"1.0","DiffDigest":"7bb96a385e2b8110ee4f9e8b9107b7ee5f93d321845c1ab0b21f16c9423351a7","MatchedRuleIds":["conservative:runtime-observation"],"PlanDigest":"c8d3dfe89f0868ed4611c8f65f2db4651a36609a6c6f898ac2f33c6bff656542","PolicyDigest":"0e6b0ef9a1dafd3bb2d62617be38a5a3926e5b47b6a68591731d1ef9e1874366","PolicyId":"evidencehost-runtime-proof","PolicySnapshot":{"ConservativeProfileId":"runtime-observation","Id":"evidencehost-runtime-proof","Profiles":[{"Id":"runtime-observation","Obligations":[{"Id":"runtime-coverage-report-required","Rationale":"The consumer fixture must execute and return a real Cobertura coverage report.","RequiredAssertionId":"appsurface/coverage/behavioral-patch@1","RequiredProducerIds":["runtime-coverage"],"RiskClass":"runtime-boundary-proof"}],"Producers":[{"ArtifactSlots":[{"LogicalName":"coverage-report","MaximumBytes":20971520,"MediaType":"application/xml","RelativeRoot":"merged","Required":true}],"AssertionIds":["appsurface/coverage/behavioral-patch@1"],"CoverageGate":{"MinBranchPercent":0,"MinLinePercent":0,"MinPatchBranchPercent":null,"MinPatchLinePercent":null,"PatchLineMode":"measurable","TolerancePercent":0},"Id":"runtime-coverage","Kind":"coverage","RequiredResources":[],"TimeoutSeconds":120,"Version":"1.0.0"}],"Resources":[],"Scope":"Targeted"}],"Rules":[],"Version":"1"},"Profile":{"Id":"runtime-observation","Obligations":[{"Id":"runtime-coverage-report-required","Rationale":"The consumer fixture must execute and return a real Cobertura coverage report.","RequiredAssertionId":"appsurface/coverage/behavioral-patch@1","RequiredProducerIds":["runtime-coverage"],"RiskClass":"runtime-boundary-proof"}],"Producers":[{"ArtifactSlots":[{"LogicalName":"coverage-report","MaximumBytes":20971520,"MediaType":"application/xml","RelativeRoot":"merged","Required":true}],"AssertionIds":["appsurface/coverage/behavioral-patch@1"],"CoverageGate":{"MinBranchPercent":0,"MinLinePercent":0,"MinPatchBranchPercent":null,"MinPatchLinePercent":null,"PatchLineMode":"measurable","TolerancePercent":0},"Id":"runtime-coverage","Kind":"coverage","RequiredResources":[],"TimeoutSeconds":120,"Version":"1.0.0"}],"Resources":[],"Scope":"Targeted"}}'

    def fixture(self):
        plan = json.loads(self.OBSERVED_PLAN_JSON)
        report = b'<coverage line-rate="1" branch-rate="1"><packages /></coverage>'
        obligation = "runtime-coverage-report-required"
        manifest = {
            "ContractVersion": "1.0", "PlanDigest": plan["PlanDigest"],
            "ManifestDigest": "e" * 64, "ExecutionVerdict": "Passed",
            "Mode": "Observation", "ClaimKind": "ObservationOnly", "Eligibility": "Informational",
            "EnvelopeStatus": "NotRequired", "ResourceResults": [],
            "SelectedObligationIds": [obligation], "ClosedObligationIds": [obligation],
            "UnmediatedObligationIds": [],
            "Metrics": {"CleanupCompleted": True, "CleanupDiagnostic": None},
            "ProducerResults": [{
                "ProducerId": proof.PRODUCER_ID, "Outcome": "Passed", "SatisfiedAssertionIds": [proof.ASSERTION_ID],
                "Diagnostic": None, "ElapsedMilliseconds": 1, "Artifacts": [{
                    "LogicalName": "coverage-report", "RelativePath": "merged/coverage.cobertura.xml",
                    "MediaType": "application/xml", "LengthBytes": len(report),
                    "Sha256": hashlib.sha256(report).hexdigest(),
                }],
            }],
        }
        summary = {name: manifest[name] for name in
                   ("Mode", "ClaimKind", "Eligibility", "ExecutionVerdict", "EnvelopeStatus")}
        summary.update(Procedure="registered-protected-producer", SandboxAttestation=False)
        return plan, manifest, summary, report

    def artifacts(self, plan, manifest, summary, report):
        return {"evidence-plan.json": json.dumps(plan).encode(),
                "evidence-manifest.json": json.dumps(manifest).encode(),
                "evidence-summary.json": json.dumps(summary).encode(), "coverage-report": report}

    def test_observed_canonical_plan_and_synthetic_passed_metadata_are_parseable_only(self):
        plan, manifest, summary, report = self.fixture()
        verification = proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())
        self.assertEqual(plan["PolicySnapshot"], proof.expected_runtime_policy_snapshot(proof.create_policy()))
        gate = plan["PolicySnapshot"]["Profiles"][0]["Producers"][0]["CoverageGate"]
        self.assertEqual({"MinLinePercent": 0, "MinBranchPercent": 0, "MinPatchLinePercent": None,
                          "MinPatchBranchPercent": None, "PatchLineMode": "measurable", "TolerancePercent": 0}, gate)
        self.assertEqual(manifest, verification["manifest"])
        self.assertEqual(hashlib.sha256(report).hexdigest(), verification["coverageReportSha256"])

    def test_wrong_key_casing_is_rejected_at_each_consumed_boundary(self):
        cases = (("plan", "PolicySnapshot"), ("plan", "Profile"), ("plan", "PlanDigest"),
                 ("manifest", "Mode"), ("manifest", "Eligibility"), ("manifest", "EnvelopeStatus"),
                 ("manifest", "ProducerResults"), ("manifest", "ManifestDigest"),
                 ("summary", "Procedure"), ("summary", "Mode"), ("summary", "ExecutionVerdict"),
                 ("metrics", "CleanupCompleted"), ("producer", "Outcome"), ("artifact", "Sha256"))
        for container, name in cases:
            with self.subTest(container=container, name=name):
                plan, manifest, summary, report = self.fixture()
                target = {"plan": plan, "manifest": manifest, "summary": summary,
                          "metrics": manifest["Metrics"], "producer": manifest["ProducerResults"][0],
                          "artifact": manifest["ProducerResults"][0]["Artifacts"][0]}[container]
                target[name[0].lower() + name[1:]] = target.pop(name)
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())

    def test_full_snapshot_defaults_and_policy_content_must_match(self):
        for field, value in (("MinPatchLinePercent", 1), ("MinPatchBranchPercent", 1),
                             ("PatchLineMode", "codecov"), ("TolerancePercent", 0.5),
                             ("MinLinePercent", 95), ("MinBranchPercent", 85)):
            with self.subTest(field=field):
                plan, manifest, summary, report = self.fixture()
                plan["PolicySnapshot"]["Profiles"][0]["Producers"][0]["CoverageGate"][field] = value
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())
        for field in ("MinPatchLinePercent", "MinPatchBranchPercent", "PatchLineMode"):
            with self.subTest(missing=field):
                plan, manifest, summary, report = self.fixture()
                del plan["PolicySnapshot"]["Profiles"][0]["Producers"][0]["CoverageGate"][field]
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())
        plan, manifest, summary, report = self.fixture()
        policy = proof.create_policy(); policy["profiles"][0]["producers"][0]["timeoutSeconds"] += 1
        with self.assertRaises(proof.ProofFailure):
            proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), policy)

    def test_profile_resource_and_snapshot_identity_mismatches_reject(self):
        for target, field, value in (("profile", "Id", "different"), ("profile", "Resources", [{"Id": "external"}]),
                                    ("profile", "Scope", "Release"), ("snapshot", "Id", "different"),
                                    ("manifest", "ResourceResults", [{"ResourceId": "external", "Outcome": "Ready"}])):
            with self.subTest(target=target, field=field):
                plan, manifest, summary, report = self.fixture()
                {"profile": plan["Profile"], "snapshot": plan["PolicySnapshot"], "manifest": manifest}[target][field] = value
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())

    def test_wrong_mode_eligibility_envelope_cleanup_verdict_or_outcome_reject(self):
        cases = (("manifest", "Mode", "Trusted"), ("manifest", "ClaimKind", "TargetedComplete"),
                 ("manifest", "Eligibility", "PullRequestGate"), ("manifest", "EnvelopeAssertion", {"synthetic": True}),
                 ("manifest", "envelopeAssertion", {"synthetic": True}), ("manifest", "EnvelopeStatus", "ValidatedNotAttested"),
                 ("manifest", "ExecutionVerdict", "Incomplete"), ("metrics", "CleanupCompleted", False),
                 ("producer", "Outcome", "Failed"), ("producer", "ProducerId", "different"),
                 ("summary", "Eligibility", "None"), ("summary", "SandboxAttestation", True))
        for target, field, value in cases:
            with self.subTest(target=target, field=field):
                plan, manifest, summary, report = self.fixture()
                {"manifest": manifest, "metrics": manifest["Metrics"], "producer": manifest["ProducerResults"][0],
                 "summary": summary}[target][field] = value
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())

    def test_digest_and_artifact_metadata_or_bytes_mismatches_reject(self):
        cases = (("manifest", "PlanDigest", "a" * 64), ("manifest", "ManifestDigest", ""),
                 ("artifact", "RelativePath", "outside/report.xml"), ("artifact", "LengthBytes", 0),
                 ("artifact", "Sha256", "a" * 64), ("artifact", "LogicalName", "wrong"))
        for target, field, value in cases:
            with self.subTest(target=target, field=field):
                plan, manifest, summary, report = self.fixture()
                {"manifest": manifest, "artifact": manifest["ProducerResults"][0]["Artifacts"][0]}[target][field] = value
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, report), proof.create_policy())
        plan, manifest, summary, report = self.fixture()
        with self.assertRaises(proof.ProofFailure):
            proof.verify_observation_output(self.artifacts(plan, manifest, summary, report + b"changed"), proof.create_policy())
        for invalid in (b"<coverage", b"<other />"):
            with self.subTest(xml=invalid):
                plan, manifest, summary, _ = self.fixture()
                metadata = manifest["ProducerResults"][0]["Artifacts"][0]
                metadata.update(LengthBytes=len(invalid), Sha256=hashlib.sha256(invalid).hexdigest())
                with self.assertRaises(proof.ProofFailure):
                    proof.verify_observation_output(self.artifacts(plan, manifest, summary, invalid), proof.create_policy())

    def publish(self, directory, artifacts, verification):
        proof.write_public_artifacts(
            directory, artifacts, {"EVIDENCE_RUN_ID": "123/1", "EVIDENCE_BASE_REVISION": "a" * 40,
            "EVIDENCE_SUBJECT_REVISION": "b" * 40, "EVIDENCE_WORKFLOW_IDENTITY": "synthetic-data-only"},
            "b" * 40, {}, "c" * 64, json.dumps(proof.create_policy()).encode(), "d" * 64,
            "e" * 64, "synthetic/reporter", "f" * 64, verification, "fixture", {}, {})

    def test_public_record_extracts_canonical_digests_without_changing_source_bytes(self):
        plan, manifest, summary, report = self.fixture()
        artifacts = self.artifacts(plan, manifest, summary, report)
        verification = proof.verify_observation_output(artifacts, proof.create_policy())
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); self.publish(root, artifacts, verification)
            record = json.loads((root / "runtime-proof.json").read_bytes())
            self.assertEqual(plan["PlanDigest"], record["planDigest"])
            self.assertEqual(manifest["ManifestDigest"], record["manifestDigest"])
            self.assertFalse(record["gateEligible"])
            self.assertEqual("none", record["admission"])
            self.assertEqual(artifacts["evidence-plan.json"], (root / "evidence-plan.json").read_bytes())

    def test_public_digest_missing_wrong_case_or_mismatch_rejects_before_writes(self):
        for target, field, value in (("plan", "PlanDigest", None), ("manifest", "ManifestDigest", None),
                                    ("manifest", "PlanDigest", "a" * 64)):
            with self.subTest(target=target, field=field), tempfile.TemporaryDirectory() as directory:
                plan, manifest, summary, report = self.fixture()
                artifacts = self.artifacts(plan, manifest, summary, report)
                verification = proof.verify_observation_output(artifacts, proof.create_policy())
                document = verification[target]
                if value is None: document[field[0].lower() + field[1:]] = document.pop(field)
                else: document[field] = value
                with self.assertRaises(proof.ProofFailure):
                    self.publish(Path(directory), artifacts, verification)
                self.assertEqual([], os.listdir(directory))

if __name__ == "__main__":
    unittest.main()
