"""Closed SDK bootstrap data/procedure controls, never root or SDK proof.

Privileged ancestor protection and SDK audit are mocked. Temporary host bytes
are metadata only, never executable SDK content. The real children use the
current Python interpreter to exercise Runner's timeout and physical ownership.
No publisher, SDK, subject, build, admission or qualification is executed here.
"""
from contextlib import ExitStack
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch


SPEC = importlib.util.spec_from_file_location(
    "private_qualification_sdk_bootstrap_controls", Path(__file__).with_name("prepare.py"))
prepare = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = prepare
SPEC.loader.exec_module(prepare)


class SdkBootstrapControls(unittest.TestCase):
    """Exercise the existing bootstrap entry with fixed, nonauthoritative data."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="qualification-sdk-bootstrap-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.source = self.root / "source"
        self.workspace = self.root / "workspace"
        self.source.mkdir(mode=0o700)
        self.workspace.mkdir(mode=0o700)
        self.commit = "a" * 40

    def provenance(self, root=None):
        return {"schema": "issue779-pinned-sdk-distribution-v1",
                "sdk_version": prepare.SDK_VERSION, "rid": "linux-x64",
                "root": str(prepare.SDK_ROOT if root is None else root),
                "archive_url": prepare.ARCHIVE_URL,
                "archive_sha512": prepare.ARCHIVE_SHA512,
                "compressed_bytes": 1, "expanded_bytes": 1,
                "node_count": 1, "explicit_member_count": 1,
                "tree_sha256": "b" * 64, "gnu_longname_headers": 0,
                "complete": True, "sdk_audit_completed": False,
                "qualification_claim": False}

    @staticmethod
    def encoded(value):
        return json.dumps(value, separators=(",", ":")).encode()

    def binding(self):
        path = self.workspace / "build-binding.json"
        self.assertLessEqual(path.stat().st_size, 4096)
        self.assertEqual(0o600, path.stat().st_mode & 0o777)
        return json.loads(path.read_bytes())

    def assert_rejected(self, raw):
        with self.assertRaises(prepare.PreparationFailure) as caught:
            prepare.validate_distribution_provenance(raw)
        self.assertEqual("qualification-preparation-rejected", str(caught.exception))

    def test_provenance_accepts_closed_positive_and_numeric_boundary_neighbors(self):
        ordinary = self.provenance()
        self.assertEqual(15, len(ordinary))
        maximum = dict(ordinary, compressed_bytes=prepare.MAX_ARCHIVE_BYTES,
                       expanded_bytes=prepare.MAX_TOTAL_BYTES,
                       node_count=prepare.MAX_NODES,
                       explicit_member_count=prepare.MAX_NODES,
                       gnu_longname_headers=prepare.MAX_NODES)
        for value in (ordinary, maximum, dict(ordinary, gnu_longname_headers=1)):
            with self.subTest(value=value):
                self.assertEqual(value, prepare.validate_distribution_provenance(self.encoded(value)))

    def test_provenance_rejects_each_field_type_identity_bound_and_relationship(self):
        ordinary = self.provenance()
        invalid = {"schema": [None, "other-schema"],
                   "sdk_version": [None, "10.0.400"], "rid": [None, "linux-arm64"],
                   "root": [None, str(prepare.SDK_ROOT) + "/../redirect"],
                   "archive_url": [None, prepare.ARCHIVE_URL + "?redirect=1"],
                   "archive_sha512": [None, "0" * 128],
                   "compressed_bytes": [True, 0, -1, 1.0, prepare.MAX_ARCHIVE_BYTES + 1],
                   "expanded_bytes": [True, 0, -1, 1.0, prepare.MAX_TOTAL_BYTES + 1],
                   "node_count": [True, 0, -1, 1.0, prepare.MAX_NODES + 1],
                   "explicit_member_count": [True, 0, -1, 1.0, prepare.MAX_NODES + 1, 2],
                   "tree_sha256": [None, "B" * 64, "b" * 63, "g" * 64],
                   "gnu_longname_headers": [True, -1, 1.0, 2],
                   "complete": [False, 1, None],
                   "sdk_audit_completed": [True, 0, None],
                   "qualification_claim": [True, 0, None]}
        self.assertEqual(set(ordinary), set(invalid))
        for key, alternatives in invalid.items():
            for alternative in alternatives:
                with self.subTest(field=key, value=alternative):
                    self.assert_rejected(self.encoded(dict(ordinary, **{key: alternative})))
            missing = dict(ordinary)
            del missing[key]
            with self.subTest(missing=key):
                self.assert_rejected(self.encoded(missing))
        self.assertEqual(ordinary, prepare.validate_distribution_provenance(self.encoded(ordinary)))

    def test_provenance_rejects_aliases_duplicates_untrusted_wire_and_canary_fields(self):
        ordinary = self.provenance()
        encoded = self.encoded(ordinary)
        canary = "synthetic-bootstrap-canary-no-authority"
        raws = [None, encoded.decode(), b"", b" " * 4097, b"\xff", b"{", b"[]", b"null",
                self.encoded(dict(ordinary, extra=canary)),
                encoded[:-1] + b',"schema":"duplicate"}',
                encoded[:-1] + b',"SCHEMA":"alias"}']
        renamed = dict(ordinary)
        renamed["SCHEMA"] = renamed.pop("schema")
        raws.append(self.encoded(renamed))
        for raw in raws:
            with self.subTest(wire_type=type(raw).__name__, length=len(raw) if raw is not None else None):
                self.assert_rejected(raw)

    def test_bootstrap_installs_once_then_records_host_before_returning_only_audit_result(self):
        sdk_root = self.root / "metadata-sdk"
        host = sdk_root / "dotnet"
        events = []
        ancestors = {"schema": "temporary-ancestor-data-not-root-proof"}
        audited_host = Path(str(host))
        audited_binding = {"sealed": True, "marker": "mock-audit-result-not-proof"}
        runner = Mock(deadline=time.monotonic() + 180)
        original_lstat = Path.lstat
        installed = False

        def observe_host(path, *args, **kwargs):
            if path == host:
                self.assertTrue(installed, "host-read-before-installer-joined")
                events.append("host-record")
            return original_lstat(path, *args, **kwargs)

        def protect(deadline, *, diagnostic):
            events.append("protect")
            binding = self.binding()
            self.assertFalse(binding["preparation_complete"])
            self.assertEqual(self.commit, binding["source_commit"])
            self.assertIsNone(binding["sdk_bootstrap"]["host_before"])
            self.assertEqual("unavailable", binding["sdk_bootstrap"]["host_metadata_state"])
            self.assertLessEqual(deadline, runner.deadline)
            return ancestors

        def install(argv, cwd, *, capture, env):
            nonlocal installed
            events.append("installer")
            self.assertEqual(["/usr/bin/python3", "-B", str(self.source /
                "tests/evidencehost-consumer/PrivateQualification/trusted_sdk_distribution.py"), "--install"], argv)
            self.assertEqual(self.source, cwd)
            self.assertIs(capture, True)
            self.assertEqual(prepare.SDK_PATH, env["PATH"])
            self.assertIsNone(self.binding()["sdk_bootstrap"]["host_before"])
            sdk_root.mkdir()
            host.write_bytes(b"temporary-host-metadata-never-executed")
            host.chmod(0o644)
            installed = True
            return self.encoded(self.provenance(sdk_root))

        def audit(deadline, *, diagnostic):
            events.append("audit")
            binding = self.binding()
            self.assertFalse(binding["preparation_complete"])
            initial = binding["sdk_bootstrap"]
            self.assertEqual("observed", initial["host_metadata_state"])
            self.assertEqual(host.stat().st_size, initial["host_before"]["length"])
            self.assertEqual(self.provenance(sdk_root), initial["distribution"])
            self.assertEqual(ancestors, initial["preinstallation_ancestors"])
            self.assertLessEqual(deadline, runner.deadline)
            return audited_host, audited_binding

        original_validate = prepare.validate_distribution_provenance

        def validate(raw):
            events.append("provenance")
            return original_validate(raw)

        runner.run.side_effect = install
        with ExitStack() as stack:
            stack.enter_context(patch.object(prepare, "SDK_ROOT", sdk_root))
            stack.enter_context(patch.object(Path, "lstat", observe_host))
            protect_mock = stack.enter_context(patch.object(prepare, "protect_sdk_ancestors", side_effect=protect))
            audit_mock = stack.enter_context(patch.object(prepare, "seal_trusted_sdk", side_effect=audit))
            validate_mock = stack.enter_context(patch.object(prepare, "validate_distribution_provenance", side_effect=validate))
            returned_host, returned_binding = prepare.bootstrap_sdk(self.source, self.workspace, self.commit, runner)
        self.assertEqual(["protect", "installer", "provenance", "host-record", "audit"], events)
        self.assertIs(audited_host, returned_host)
        self.assertIs(audited_binding, returned_binding)
        self.assertEqual(self.provenance(sdk_root), returned_binding["distribution"])
        self.assertEqual(ancestors, returned_binding["preinstallation_ancestors"])
        protect_mock.assert_called_once()
        runner.run.assert_called_once()
        validate_mock.assert_called_once()
        audit_mock.assert_called_once()

    def test_bootstrap_ancestor_installer_and_malformed_provenance_fail_before_audit_or_build(self):
        for stage in ("ancestor", "installer", "provenance"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory(dir=self.root) as directory:
                workspace = Path(directory)
                runner = Mock(deadline=time.monotonic() + 180)
                original = RuntimeError("synthetic-failure-canary-never-public")
                runner.run.side_effect = original if stage == "installer" else None
                runner.run.return_value = b"{}" if stage == "provenance" else self.encoded(self.provenance())
                with ExitStack() as stack:
                    protect = stack.enter_context(patch.object(prepare, "protect_sdk_ancestors",
                        side_effect=original if stage == "ancestor" else None, return_value={}))
                    audit = stack.enter_context(patch.object(prepare, "seal_trusted_sdk"))
                    record = stack.enter_context(patch.object(prepare, "record_installed_sdk_preflight"))
                    retention = stack.enter_context(patch.object(prepare, "retain_sdk_failure_diagnostic", return_value=False))
                    host_read = stack.enter_context(patch.object(prepare, "sdk_metadata", side_effect=AssertionError("premature-host-read")))
                    with self.assertRaises(prepare.PreparationFailure if stage == "provenance" else RuntimeError) as caught:
                        prepare.bootstrap_sdk(self.source, workspace, self.commit, runner)
                if stage != "provenance":
                    self.assertIs(original, caught.exception)
                else:
                    self.assertEqual("qualification-preparation-rejected", str(caught.exception))
                protect.assert_called_once()
                self.assertEqual(0 if stage == "ancestor" else 1, runner.run.call_count)
                audit.assert_not_called()
                record.assert_not_called()
                host_read.assert_not_called()
                retention.assert_called_once()
                binding = json.loads((workspace / "build-binding.json").read_bytes())
                self.assertFalse(binding["preparation_complete"])
                self.assertIsNone(binding["sdk_bootstrap"]["host_before"])
                self.assertNotIn("synthetic-failure-canary", json.dumps(binding))

    def test_preflight_missing_host_is_unobserved_until_installed_record_attempt(self):
        sdk_root = self.root / "absent-metadata-sdk"
        distribution = self.provenance(sdk_root)
        ancestors = {"schema": "temporary-ancestor-data-not-root-proof"}
        with patch.object(prepare, "SDK_ROOT", sdk_root):
            prepare.retain_sdk_preflight_binding(self.workspace, self.commit, observe_host=False)
            self.assertIsNone(self.binding()["sdk_bootstrap"]["host_before"])
            with self.assertRaises(FileNotFoundError):
                prepare.record_installed_sdk_preflight(self.workspace, distribution, ancestors)
        binding = self.binding()
        self.assertFalse(binding["preparation_complete"])
        self.assertEqual(distribution, binding["sdk_bootstrap"]["distribution"])
        self.assertEqual(ancestors, binding["sdk_bootstrap"]["preinstallation_ancestors"])
        self.assertIsNone(binding["sdk_bootstrap"]["host_before"])
        self.assertEqual("unavailable", binding["sdk_bootstrap"]["host_metadata_state"])

    def test_runner_real_python_timeout_reaps_group_and_prevents_next_dispatch(self):
        logs = self.root / "timeout-logs"
        logs.mkdir(mode=0o700)
        processes = []
        original_popen = subprocess.Popen

        def launch(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process

        def cleanup():
            for process in processes:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except OSError:
                    pass
                process.wait(timeout=3)

        self.addCleanup(cleanup)
        runner = prepare.Runner(logs, time.monotonic() + .2)
        argv = [sys.executable, "-c", "import time; time.sleep(60)"]
        with patch.object(prepare.subprocess, "Popen", side_effect=launch) as popen:
            with self.assertRaises(prepare.PreparationFailure):
                runner.run(argv, self.root)
            with self.assertRaises(prepare.PreparationFailure):
                runner.run([sys.executable, "-c", "raise SystemExit(0)"], self.root)
            popen.assert_called_once()
        self.assertEqual(1, len(processes))
        self.assertIsNotNone(processes[0].returncode)
        with self.assertRaises(ProcessLookupError):
            os.killpg(processes[0].pid, 0)
        self.assertEqual(1, len(runner.results))
        record = json.loads((logs / "command-01.json").read_bytes())
        self.assertEqual(runner.results[0], record)
        self.assertEqual(argv, record["argv"])
        self.assertEqual(124, record["exit_code"])
        self.assertIs(record["timed_out"], True)
        self.assertIs(record["owned_group_empty"], True)
        self.assertIs(record["cleanup_failed"], False)
        self.assertEqual("process-timeout", record["failure_category"])
        self.assertEqual(0o600, (logs / "command-01.json").stat().st_mode & 0o777)
        self.assertEqual(0o600, (logs / "build-01.log").stat().st_mode & 0o777)
        self.assertEqual({"command-01.json", "build-01.log"}, {path.name for path in logs.iterdir()})

    def test_runner_rejects_output_when_command_deadline_crosses_after_durable_receipt(self):
        logs = self.root / "late-acceptance-logs"
        logs.mkdir(mode=0o700)
        receipt_path = logs / "command-01.json"
        original_clock = time.monotonic
        original_popen = subprocess.Popen
        processes, late_samples = [], []
        runner = prepare.Runner(logs, original_clock() + 600)

        def launch(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process

        def clock():
            now = original_clock()
            if receipt_path.exists():
                # The earlier category/hash work completed within the command
                # allowance. Only a fully written receipt advances this sample
                # beyond that allowance, while the cumulative budget stays live.
                encoded = receipt_path.read_bytes()
                self.assertTrue(encoded.endswith(b"\n"))
                self.assertEqual(0o600, receipt_path.stat().st_mode & 0o777)
                self.assertEqual(0, json.loads(encoded)["exit_code"])
                now += 181
                late_samples.append(now)
                self.assertLess(now, runner.deadline)
            return now

        def cleanup():
            for process in processes:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except OSError:
                    pass
                process.wait(timeout=3)

        self.addCleanup(cleanup)
        argv = [sys.executable, "-c", "print('portable-runner-output', end='')"]
        with ExitStack() as stack:
            popen = stack.enter_context(patch.object(prepare.subprocess, "Popen", side_effect=launch))
            stack.enter_context(patch.object(prepare.time, "monotonic", side_effect=clock))
            with self.assertRaises(prepare.PreparationFailure) as caught:
                runner.run(argv, self.root, capture=True)
            popen.assert_called_once()
        self.assertEqual("qualification-preparation-rejected", str(caught.exception))
        self.assertEqual(1, len(late_samples))
        self.assertEqual(1, len(processes))
        self.assertEqual(0, processes[0].returncode)
        with self.assertRaises(ProcessLookupError):
            os.killpg(processes[0].pid, 0)
        record = json.loads(receipt_path.read_bytes())
        self.assertEqual([record], runner.results)
        self.assertEqual(argv, record["argv"])
        self.assertIs(record["timed_out"], False)
        self.assertIs(record["owned_group_empty"], True)
        self.assertIs(record["cleanup_failed"], False)
        self.assertIsNone(record["failure_category"])
        self.assertEqual(prepare.sha((logs / "build-01.log").read_bytes()), record["log_sha256"])
        self.assertEqual({"command-01.json", "build-01.log"}, {path.name for path in logs.iterdir()})
