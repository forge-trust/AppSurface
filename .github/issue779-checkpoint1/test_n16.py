"""Detached N16 data controls only; no root/native owner or subprocess is created."""
import copy
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

import binding

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("n16_data_adapter", HERE / "procedures/n16/adapter.py")
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)
records = adapter._RECORDS
G = "1" * 32
SOURCE = "2" * 40
BASE = "3" * 40
SHA = "4" * 64


def fixture_model():
    """Shape model, explicitly observation-only and never a native execution."""
    return {"schema": "issue779-n16-native-fixture-observation-v2", "generation": G,
            "source_commit": SOURCE, "source_capture_sha256": SHA, "root_projection_sha256": SHA,
            "root_exit": 1, "root_stdout_bytes": 0, "stderr_sha256": SHA,
            "worker_unit": "appsurface-evidence-worker-" + G + ".service", "worker_pid": 123,
            "worker_uid": 456, "worker_gid": 457, "results_gid": 458,
            "owner_load_state": "not-found", "worker_load_state": "not-found",
            "generated_passwd_group_name_checks": 5, "worker_uid_reverse_lookup_exit": 2,
            "worker_gid_reverse_lookup_exit": 2, "results_gid_reverse_lookup_exit": 2,
            "root_launch_group_absent": True, "root_launch_fd_directory_absent": True,
            "workspace_retained_verified": True, "observation_only": True,
            "native_authority": False, "native_acceptance": False}


class N16DataTests(unittest.TestCase):
    def test_terminal_model_then_one_changed_kernel_or_cleanup_field(self):
        records.validate_fixture_result(fixture_model(), G, SHA, SOURCE, SHA)
        for key, bad in (("root_exit", 0), ("root_exit", True), ("root_stdout_bytes", 1),
                         ("owner_load_state", "loaded"), ("worker_load_state", "loaded"),
                         ("root_launch_group_absent", False), ("root_launch_fd_directory_absent", False),
                         ("workspace_retained_verified", False), ("worker_uid_reverse_lookup_exit", 0),
                         ("worker_gid_reverse_lookup_exit", 0), ("results_gid_reverse_lookup_exit", 0),
                         ("generated_passwd_group_name_checks", 4), ("native_acceptance", True),
                         ("generation", "5" * 32), ("worker_unit", "unrelated.service")):
            with self.subTest(field=key, value=bad):
                value = fixture_model()
                value[key] = bad
                with self.assertRaises(ValueError): records.validate_fixture_result(value, G, SHA, SOURCE, SHA)

    def test_request_valid_adjacent_then_one_budget_or_identity_field(self):
        generation = G
        stage = "/var/lib/appsurface-evidence-fixture/" + generation
        value = {"schema": "evidence-supervisor-linux-v1", "mode": "observation",
                 "tool_root": stage + "/tool", "runtime_root": stage + "/runtime",
                 "runtime_host": stage + "/runtime/dotnet", "entry_path": stage + "/tool/ForgeTrust.AppSurface.Cli.dll",
                 "policy_file": stage + "/tool/fixture-policy.json", "subject_root": stage + "/subject",
                 "base_revision": BASE, "subject_revision": SOURCE, "workflow_identity": "github-actions-123-1",
                 "paths": ["docs/designs/issue-779-csharp-supervision-core.md"], "observation_profile_ids": ["empty"],
                 "observation_producer_ids": [], "admission_seconds": 10, "start_seconds": 30,
                 "collection_seconds": 10, "cleanup_seconds": 60, "stopping_seconds": 5,
                 "job_deadline_utc": "2026-10-19T08:00:00Z"}
        def check(model):
            raw = binding.canonical(model)
            policy = binding.canonical(records.FIXTURE_POLICY)
            checksums = (binding.digest(raw) + "  /run/appsurface-evidence-fixture/" + G + "/request.json\n"
                         + binding.digest(policy) + "  " + stage + "/tool/fixture-policy.json\n").encode()
            records.validate_request_binding(raw, checksums, G, "github-actions-123-1", SOURCE, BASE)
        check(value)
        for field, bad in (("start_seconds", 31), ("cleanup_seconds", 59), ("mode", "admission"),
                           ("subject_revision", BASE), ("observation_profile_ids", ["other"]),
                           ("job_deadline_utc", "not-a-date")):
            changed = copy.deepcopy(value)
            changed[field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError): check(changed)

    def test_wrapper_requires_all_twelve_exact_inventory_observations(self):
        facts = {name: {"file_count": 1, "node_count": 2, "total_bytes": 19} for name in ("source", "tool", "runtime")}
        policy_bytes = len(binding.canonical(records.FIXTURE_POLICY))
        counts = {**{name: (1, 2, 19) for name in facts}, "sealed_tool": (1, 3, 19 + policy_bytes)}
        sequence = ("source", "tool", "runtime", "sealed_tool", "runtime", "source",
                    "source", "tool", "runtime", "sealed_tool", "runtime", "source")
        raw = ("".join("FULL_TREE_POINT_SAMPLES_MATCHED:%d:%d:%d\n" % counts[name] for name in sequence)
               + "N16_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n").encode()
        records.validate_fixture_output(raw, b"", facts)
        for stdout, stderr in ((raw.replace(b"MATCHED:1:2:19", b"MATCHED:1:2:20", 1), b""),
                               (raw, b"unexpected"), (raw.split(b"\n", 1)[1], b"")):
            with self.assertRaises(ValueError): records.validate_fixture_output(stdout, stderr, facts)


def _joined_command():
    return {"ordinal": 0, "phase": "actual-csharp-n16", "exit": 1,
            "waited": True, "group_absent": True, "root_start_guard_created": True,
            "root_start_guard_closed": True, "root_utility_cgroup_empty": True}


def _failure_dirs(parent):
    parent = parent.resolve()
    control, output = parent / "control", parent / "output"
    control.mkdir(mode=0o700)
    output.mkdir(mode=0o700)
    return control, output


def _retain(control, output, command, *, deadline):
    """Exercise the owned-file mechanics without impersonating production root."""
    return adapter._retain_failure_logs_owned(control, output, command, deadline=deadline,
                                               uid=os.geteuid(), gid=os.getegid())


class N16FailureLogRetentionTests(unittest.TestCase):
    def test_both_logs_are_copied_raw_with_explicit_data_only_manifest(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            stdout, stderr = b"fixed stdout\n", b"closed stderr\n"
            for name, raw in (("fixture-stdout.log", stdout), ("fixture-stderr.log", stderr)):
                path = control / name
                path.write_bytes(raw)
                path.chmod(0o600)
            self.assertTrue(_retain(control, output, _joined_command(),
                                                        deadline=time.monotonic() + 5))
            self.assertEqual((output / "N16-failure-fixture-stdout.log").read_bytes(), stdout)
            self.assertEqual((output / "N16-failure-fixture-stderr.log").read_bytes(), stderr)
            manifest = binding.decode((output / "N16-failure-logs.json").read_bytes())
            self.assertEqual(manifest["schema"], "issue779-n16-failure-logs-v1")
            self.assertEqual(manifest["files"]["fixture-stdout.log"],
                             {"present": True, "bytes": len(stdout), "sha256": binding.digest(stdout)})
            self.assertEqual(manifest["files"]["fixture-stderr.log"],
                             {"present": True, "bytes": len(stderr), "sha256": binding.digest(stderr)})
            self.assertFalse(any((output / name).exists() for name in adapter.LEAVES))

    def test_partial_logs_are_named_as_missing_and_never_filled_with_empty_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            raw = b"only retained stdout\n"
            path = control / "fixture-stdout.log"
            path.write_bytes(raw)
            path.chmod(0o600)
            self.assertTrue(_retain(control, output, _joined_command(),
                                                        deadline=time.monotonic() + 5))
            manifest = binding.decode((output / "N16-failure-logs.json").read_bytes())
            self.assertEqual(manifest["files"]["fixture-stderr.log"], {"present": False})
            self.assertFalse((output / "N16-failure-fixture-stderr.log").exists())

    def test_no_logs_produce_explicit_absent_inventory(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            self.assertTrue(_retain(control, output, _joined_command(),
                                                         deadline=time.monotonic() + 5))
            manifest = binding.decode((output / "N16-failure-logs.json").read_bytes())
            self.assertEqual(manifest["files"], {name: {"present": False}
                                                 for name in adapter.FAILURE_DIAGNOSTIC_CAPS})

    def test_unjoined_or_missing_runner_facts_refuse_to_read_or_write(self):
        for command in ({}, {**_joined_command(), "group_absent": False},
                        {**_joined_command(), "root_utility_cgroup_empty": False},
                        {**_joined_command(), "root_start_guard_created": False},
                        {**_joined_command(), "exit": 0}, {**_joined_command(), "exit": True},
                        {**_joined_command(), "ordinal": 1},
                        {**_joined_command(), "phase": "different"}):
            with self.subTest(command=command), tempfile.TemporaryDirectory() as temporary:
                control, output = _failure_dirs(Path(temporary))
                with self.assertRaises(ValueError):
                    _retain(control, output, command,
                                                deadline=time.monotonic() + 5)
                self.assertEqual(list(output.iterdir()), [])

    def test_public_retention_boundary_requires_root(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            if os.geteuid() != 0:
                with self.assertRaises(ValueError):
                    adapter.retain_failure_logs(control, output, _joined_command(),
                                                deadline=time.monotonic() + 5)
            else:
                self.assertTrue(adapter.retain_failure_logs(
                    control, output, _joined_command(), deadline=time.monotonic() + 5))
            self.assertEqual((output / "N16-failure-logs.json").exists(), os.geteuid() == 0)

    def test_missing_deadline_expires_before_any_copy(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            file = control / "fixture-stdout.log"
            file.write_bytes(b"diagnostic")
            file.chmod(0o600)
            with self.assertRaises(ValueError):
                _retain(control, output, _joined_command(),
                                            deadline=time.monotonic() - 1)
            self.assertEqual(list(output.iterdir()), [])

    def test_deadline_expiring_during_bounded_read_never_publishes(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            path = control / "fixture-stdout.log"
            path.write_bytes(b"diagnostic")
            path.chmod(0o600)
            with mock.patch.object(adapter.time, "monotonic", side_effect=(10.0, 20.0)):
                with self.assertRaises(ValueError):
                    _retain(control, output, _joined_command(), deadline=15.0)
            self.assertEqual(list(output.iterdir()), [])

    def test_symlink_hardlink_and_oversize_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            control, output = _failure_dirs(root)
            target = root / "target"
            target.write_bytes(b"raw")
            target.chmod(0o600)
            (control / "fixture-stdout.log").symlink_to(target)
            with self.assertRaises(ValueError):
                _retain(control, output, _joined_command(), deadline=time.monotonic() + 5,
                                            )
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            control, output = _failure_dirs(root)
            target = root / "target"
            target.write_bytes(b"raw")
            target.chmod(0o600)
            os.link(target, control / "fixture-stdout.log")
            with self.assertRaises(ValueError):
                _retain(control, output, _joined_command(), deadline=time.monotonic() + 5,
                                            )
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            path = control / "fixture-stdout.log"
            with path.open("wb") as stream:
                stream.truncate(adapter.FAILURE_DIAGNOSTIC_CAPS[path.name] + 1)
            path.chmod(0o600)
            with self.assertRaises(ValueError):
                _retain(control, output, _joined_command(), deadline=time.monotonic() + 5,
                                            )

    def test_wrong_mode_and_changed_leaf_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            path = control / "fixture-stdout.log"
            path.write_bytes(b"diagnostic")
            path.chmod(0o644)
            with self.assertRaises(ValueError):
                _retain(control, output, _joined_command(), deadline=time.monotonic() + 5,
                                            )
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            path = control / "fixture-stdout.log"
            path.write_bytes(b"before")
            path.chmod(0o600)
            original_read = os.read
            original_fstat = os.fstat

            def replace_after_read(fd, count):
                raw = original_read(fd, count)
                if original_fstat(fd).st_ino == path.lstat().st_ino:
                    replacement = control / "replacement"
                    replacement.write_bytes(b"after")
                    replacement.chmod(0o600)
                    replacement.replace(path)
                return raw

            with mock.patch.object(adapter.os, "read", side_effect=replace_after_read):
                with self.assertRaises(ValueError):
                    _retain(control, output, _joined_command(), deadline=time.monotonic() + 5,
                                                )

    def test_changed_output_directory_is_detected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            control, output = _failure_dirs(root)
            path = control / "fixture-stdout.log"
            path.write_bytes(b"diagnostic")
            path.chmod(0o600)
            moved = root / "output-original"
            original_write = os.write
            original_fstat = os.fstat
            replaced = False

            def replace_after_write(fd, raw):
                nonlocal replaced
                result = original_write(fd, raw)
                if not replaced and original_fstat(fd).st_uid == os.geteuid():
                    replaced = True
                    output.rename(moved)
                    output.mkdir(mode=0o700)
                return result

            with mock.patch.object(adapter.os, "write", side_effect=replace_after_write):
                with self.assertRaises(ValueError):
                    _retain(control, output, _joined_command(), deadline=time.monotonic() + 5)
            self.assertTrue(replaced)
            self.assertEqual({item.name for item in moved.iterdir()},
                             {"N16-failure-fixture-stdout.log", "N16-failure-logs.json"})
            self.assertEqual(list(output.iterdir()), [])

    def test_primary_runner_error_is_preserved_when_retention_fails(self):
        class FailingRunner:
            records = [_joined_command()]

            def run(self, _argv, _deadline):
                raise primary

        primary = RuntimeError("original runner failure")
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            with mock.patch.object(adapter, "retain_failure_logs", side_effect=OSError("retention failed")):
                with self.assertRaises(RuntimeError) as caught:
                    adapter.run_dispatch_with_failure_retention(FailingRunner(), ["fixed"], control, output,
                                                                 deadline=time.monotonic() + 120)
            self.assertIs(caught.exception, primary)

    def test_unjoined_runner_error_is_preserved_and_does_not_read_logs(self):
        class FailingRunner:
            records = [{**_joined_command(), "waited": False}]

            def run(self, _argv, _deadline):
                raise primary

        primary = RuntimeError("original unsettled runner failure")
        with tempfile.TemporaryDirectory() as temporary:
            control, output = _failure_dirs(Path(temporary))
            file = control / "fixture-stdout.log"
            file.write_bytes(b"unjoined")
            file.chmod(0o600)
            with self.assertRaises(RuntimeError) as caught:
                adapter.run_dispatch_with_failure_retention(FailingRunner(), ["fixed"], control, output,
                                                             deadline=time.monotonic() + 120)
            self.assertIs(caught.exception, primary)
            self.assertEqual(list(output.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
