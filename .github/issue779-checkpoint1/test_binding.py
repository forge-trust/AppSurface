"""Detached C1 prerequisite/receipt models and real unprivileged FD controls.

No model represents an acquired owner, Linux worker, lease, native execution or
acceptance. These tests intentionally do not import/execute a native procedure.
They have not been executed as part of source preparation.
"""

import copy
import hashlib
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest import mock

import binding


HEAD = "1" * 40
SHA = "2" * 64
OTHER = "3" * 64


def capture_model():
    return {"schema": "issue779-checkpoint2-source-v1", "source": HEAD,
            "tree": "4" * 40, "count": 1,
            "files": {"sample.cs": {"sha256": SHA, "mode": "0644", "git_blob": "5" * 40}}}


def image_model(root, entry):
    return {"root": root, "entry": entry,
            "files": {entry: {"sha256": SHA, "bytes": 19, "mode": "0755" if entry == "dotnet" else "0644"}},
            "directories": {}}


def build_model(selector="None"):
    capture = binding.canonical(capture_model())
    value = {"schema": "issue779-checkpoint1-build-v1", "source": HEAD,
             "selector": selector, "configuration": "Release", "sdk_version": "10.0.401",
             "source_root": "/data/current-source", "source_capture_sha256": binding.digest(capture),
             "source_after_capture_sha256": binding.digest(capture),
             "tool": image_model("/data/fresh-tool", "ForgeTrust.AppSurface.Cli.dll"),
             "runtime": image_model("/data/runtime", "dotnet"), "helpers": {}, "native_execution": False,
             "commands": []}
    for phase, verb in (("locked-restore", "restore"), ("publish", "publish")):
        argv = ["/usr/bin/dotnet", verb, binding.CLI_PROJECT, "-warnaserror",
                "-p:EvidencePrivateQualification=" + selector]
        argv += ["--locked-mode"] if verb == "restore" else ["--no-restore", "-c", "Release", "-o", "/data/fresh-tool"]
        value["commands"].append({"phase": phase, "argv": argv, "exit": 0,
                                  "timed_out": False, "joined": True,
                                  "log_path": "/data/logs/" + phase + ".log",
                                  "log_sha256": SHA, "log_bytes": 100})
    for helper_id in binding.required_helpers(selector):
        project, entry = binding.HELPER_PROJECTS[helper_id]
        root = "/data/helper-" + helper_id
        helper = {"image": image_model(root, entry), "commands": []}
        for phase, verb in (("locked-restore", "restore"), ("publish", "publish")):
            argv = ["/usr/bin/dotnet", verb, project, "-warnaserror"]
            argv += ["-p:Configuration=Release"] if verb == "restore" else ["-c", "Release"]
            argv += ["--locked-mode"] if verb == "restore" else ["--no-restore", "-o", root]
            helper["commands"].append({"phase": "helper-" + helper_id + "-" + phase, "argv": argv,
                "exit": 0, "timed_out": False, "joined": True,
                "log_path": "/data/logs/" + helper_id + "-" + phase + ".log", "log_sha256": SHA, "log_bytes": 100})
        value["helpers"][helper_id] = helper
    return value, capture


def results_model():
    raw = binding.canonical(capture_model())
    values = []
    for selector in binding.SELECTORS:
        values.append({"schema": "issue779-checkpoint1-result-v1", "source": HEAD,
                       "source_capture_sha256": binding.digest(raw), "selector": selector,
                       "build_receipt_sha256": SHA, "plan_sha256": OTHER, "exit": 0,
                       "skipped": 0, "native_acceptance": False,
                       "cases": [{"case": case, "passed": True, "skipped": False,
                                  "procedure_sha256": SHA, "evidence_sha256": OTHER}
                                 for case in binding.CASE_SETS[selector]]})
    return values, raw


class CaptureTests(unittest.TestCase):
    def test_complete_detached_capture_has_no_native_authority(self):
        value = capture_model()
        self.assertIs(value, binding.validate_capture(value, HEAD))
        self.assertNotIn("native_acceptance", value)

    def test_one_source_field_mutations_reject_after_valid_neighbor(self):
        mutations = (
            ("source", "6" * 40), ("tree", "not-a-tree"), ("count", True),
            ("count", 0), ("count", 2), ("schema", "issue779-csharp-common-source-capture-v4"),
        )
        for field, bad in mutations:
            with self.subTest(field=field, bad=bad):
                value = capture_model()
                binding.validate_capture(value, HEAD)
                value[field] = bad
                with self.assertRaises(binding.Rejected):
                    binding.validate_capture(value, HEAD)

    def test_full_file_set_and_modes_are_bound(self):
        for field, bad in (("sha256", "canary-not-a-hash"), ("git_blob", "8" * 39), ("mode", "0666")):
            with self.subTest(field=field):
                value = capture_model()
                binding.validate_capture(value, HEAD)
                value["files"]["sample.cs"][field] = bad
                with self.assertRaises(binding.Rejected):
                    binding.validate_capture(value, HEAD)

    def test_case_alias_schema_member_and_duplicate_json_reject(self):
        value = capture_model()
        binding.validate_capture(value, HEAD)
        value["Source"] = HEAD
        with self.assertRaises(binding.Rejected):
            binding.validate_capture(value, HEAD)
        with self.assertRaisesRegex(binding.Rejected, "duplicate-json-member"):
            binding.decode(b'{"source":"a","source":"b"}')

    def test_json_nonfinite_encoding_and_depth_reject(self):
        self.assertEqual({}, binding.decode(b"{}"))
        for raw in (b"", b"\xff", b'{"v":NaN}', b"[" * 26 + b"0" + b"]" * 26):
            with self.subTest(raw=raw):
                with self.assertRaises(binding.Rejected):
                    binding.decode(raw)

    def test_relative_path_negative_controls(self):
        self.assertEqual("dir/data.bin", binding.relative("dir/data.bin"))
        for name in ("/absolute", "../outside", "a//b", "a/./b", "a/.git/config", "a\\b", "a:b", "a\nb"):
            with self.subTest(name=name):
                with self.assertRaises(binding.Rejected):
                    binding.relative(name)


class BuildTests(unittest.TestCase):
    def test_every_selector_has_valid_adjacent_build_model(self):
        for selector in binding.SELECTORS:
            with self.subTest(selector=selector):
                value, raw = build_model(selector)
                binding.validate_build(value, raw, HEAD, selector)

    def test_one_build_identity_field_mutations_reject(self):
        for field, bad in (("source", "7" * 40), ("selector", "N03"), ("configuration", "Debug"),
                           ("sdk_version", "10.0.402"), ("source_after_capture_sha256", OTHER),
                           ("native_execution", True)):
            with self.subTest(field=field):
                value, raw = build_model()
                binding.validate_build(value, raw, HEAD, "None")
                value[field] = bad
                with self.assertRaises(binding.Rejected):
                    binding.validate_build(value, raw, HEAD, "None")

    def test_each_command_terminal_is_required_and_boolean_zero_is_not_an_exit(self):
        for index in (0, 1):
            for field, bad in (("exit", 1), ("exit", False), ("timed_out", True), ("joined", False)):
                with self.subTest(index=index, field=field, bad=bad):
                    value, raw = build_model()
                    binding.validate_build(value, raw, HEAD, "None")
                    value["commands"][index][field] = bad
                    with self.assertRaises(binding.Rejected):
                        binding.validate_build(value, raw, HEAD, "None")

    def test_actual_two_phase_order_and_no_historical_command_inventory(self):
        for operation in ("reverse", "omit", "append"):
            with self.subTest(operation=operation):
                value, raw = build_model()
                binding.validate_build(value, raw, HEAD, "None")
                if operation == "reverse":
                    value["commands"].reverse()
                elif operation == "omit":
                    value["commands"].pop()
                else:
                    value["commands"].append(copy.deepcopy(value["commands"][0]))
                with self.assertRaises(binding.Rejected):
                    binding.validate_build(value, raw, HEAD, "None")

    def test_locked_restore_and_explicit_both_command_selector_properties(self):
        for index, removal in ((0, "--locked-mode"), (0, "-p:EvidencePrivateQualification=None"),
                               (1, "--no-restore"), (1, "-p:EvidencePrivateQualification=None")):
            with self.subTest(index=index, removal=removal):
                value, raw = build_model()
                binding.validate_build(value, raw, HEAD, "None")
                value["commands"][index]["argv"].remove(removal)
                with self.assertRaises(binding.Rejected):
                    binding.validate_build(value, raw, HEAD, "None")

    def test_changed_publish_output_or_configuration_reject(self):
        for original, replacement in (("Release", "Debug"), ("/data/fresh-tool", "/data/wrong-tool")):
            with self.subTest(original=original):
                value, raw = build_model()
                binding.validate_build(value, raw, HEAD, "None")
                argv = value["commands"][1]["argv"]
                argv[argv.index(original)] = replacement
                with self.assertRaises(binding.Rejected):
                    binding.validate_build(value, raw, HEAD, "None")

    def test_shared_or_overlapping_input_roots_reject(self):
        value, raw = build_model()
        binding.validate_build(value, raw, HEAD, "None")
        value["runtime"]["root"] = value["tool"]["root"] + "/nested"
        with self.assertRaisesRegex(binding.Rejected, "build-root-overlap"):
            binding.validate_build(value, raw, HEAD, "None")

    def test_complete_directory_inventory_and_single_node_role(self):
        value = image_model("/data/tool", "ForgeTrust.AppSurface.Cli.dll")
        value["directories"]["nested"] = "0755"
        value["files"]["nested/data"] = {"sha256": SHA, "bytes": 0, "mode": "0644"}
        binding.validate_image(value, value["entry"])
        del value["directories"]["nested"]
        with self.assertRaisesRegex(binding.Rejected, "image-missing-ancestor"):
            binding.validate_image(value, value["entry"])


class AggregateTests(unittest.TestCase):
    def test_fourteen_detached_models_cover_exact_sixteen_without_acceptance_credit(self):
        values, raw = results_model()
        summary = binding.aggregate(values, raw)
        self.assertEqual(list(binding.CASES), summary["cases"])
        self.assertEqual(16, summary["passed"])
        self.assertIs(summary["native_acceptance"], False)
        self.assertIs(summary["coverage_credit"], False)

    def test_missing_duplicate_and_extra_selector_receipts_reject(self):
        for mutation in ("missing", "duplicate", "extra"):
            with self.subTest(mutation=mutation):
                values, raw = results_model()
                binding.aggregate(values, raw)
                if mutation == "missing":
                    values.pop()
                elif mutation == "duplicate":
                    values[-1] = copy.deepcopy(values[0])
                else:
                    values.append(copy.deepcopy(values[0]))
                with self.assertRaises(binding.Rejected):
                    binding.aggregate(values, raw)

    def test_one_bad_result_never_passes(self):
        for field, bad in (("source", "9" * 40), ("source_capture_sha256", OTHER),
                           ("exit", 1), ("exit", False), ("skipped", 1), ("skipped", False),
                           ("native_acceptance", True)):
            with self.subTest(field=field):
                values, raw = results_model()
                binding.aggregate(values, raw)
                values[-1][field] = bad
                with self.assertRaises(binding.Rejected):
                    binding.aggregate(values, raw)

    def test_each_named_case_must_be_observed_passed_and_unskipped(self):
        for selector_index, value in enumerate(results_model()[0]):
            for case_index in range(len(value["cases"])):
                for field in ("passed", "skipped"):
                    with self.subTest(selector=value["selector"], case=case_index, field=field):
                        values, raw = results_model()
                        binding.aggregate(values, raw)
                        values[selector_index]["cases"][case_index][field] = field == "skipped"
                        with self.assertRaises(binding.Rejected):
                            binding.aggregate(values, raw)

    def test_none_requires_all_three_in_order_no_alias_case(self):
        for change in ("omit", "swap", "alias"):
            with self.subTest(change=change):
                values, raw = results_model()
                binding.aggregate(values, raw)
                rows = values[0]["cases"]
                if change == "omit":
                    rows.pop()
                elif change == "swap":
                    rows[0], rows[1] = rows[1], rows[0]
                else:
                    rows[0]["case"] = "n01"
                with self.assertRaises(binding.Rejected):
                    binding.aggregate(values, raw)


class FileDataTests(unittest.TestCase):
    def test_real_single_link_file_neighbor_then_symlink_hardlink_fifo_reject(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            original = root / "data"
            original.write_bytes(b"actual-data")
            self.assertEqual(b"actual-data", binding.read(original, deadline=time.monotonic() + 2))
            link = root / "alias"
            link.symlink_to(original)
            with self.assertRaises(binding.Rejected):
                binding.read(link, deadline=time.monotonic() + 2)
            link.unlink()
            os.link(original, link)
            with self.assertRaises(binding.Rejected):
                binding.read(original, deadline=time.monotonic() + 2)
            link.unlink()
            if hasattr(os, "mkfifo"):
                os.mkfifo(root / "fifo")
                with self.assertRaises(binding.Rejected):
                    binding.read(root / "fifo", deadline=time.monotonic() + 2)

    def test_size_and_deadline_bounds_have_valid_adjacent_read(self):
        with tempfile.TemporaryDirectory() as temporary:
            p = Path(temporary) / "data"
            p.write_bytes(b"12345678")
            self.assertEqual(b"12345678", binding.read(p, 8, deadline=time.monotonic() + 2))
            with self.assertRaises(binding.Rejected):
                binding.read(p, 7, deadline=time.monotonic() + 2)
            with self.assertRaisesRegex(binding.Rejected, "verification-deadline"):
                binding.read(p, 8, deadline=time.monotonic() - 1)

    def test_named_replacement_during_retained_fd_read_rejects_without_timing_race(self):
        with tempfile.TemporaryDirectory() as temporary:
            p = Path(temporary) / "data"
            p.write_bytes(b"original")
            self.assertEqual(b"original", binding.read(p, deadline=time.monotonic() + 2))
            actual_read = os.read
            changed = False
            def replace_after_read(fd, count):
                nonlocal changed
                result = actual_read(fd, count)
                if not changed:
                    changed = True
                    p.rename(p.with_name("retained-original"))
                    p.write_bytes(b"replaced")
                return result
            with mock.patch.object(binding.os, "read", side_effect=replace_after_read):
                with self.assertRaisesRegex(binding.Rejected, "read-final-substitution"):
                    binding.read(p, deadline=time.monotonic() + 2)

    def test_inventory_has_real_zero_byte_and_directory_neighbors_then_extra_file_rejects(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            entry = root / "dotnet"
            entry.write_bytes(b"fixture-data-only")
            entry.chmod(0o755)
            (root / "nested").mkdir(mode=0o755)
            (root / "nested/empty").write_bytes(b"")
            (root / "nested/empty").chmod(0o644)
            value = binding.inventory(root, "dotnet", deadline=time.monotonic() + 2)
            self.assertEqual(0, value["files"]["nested/empty"]["bytes"])
            binding.verify_image(value, deadline=time.monotonic() + 2)
            (root / "unexpected").write_bytes(b"outside-inventory")
            with self.assertRaises(binding.Rejected):
                binding.verify_image(value, deadline=time.monotonic() + 2)




class HelperBuildTests(unittest.TestCase):
    """Detached current helper receipts; no helper/native process is started."""
    def test_required_helper_sets_and_one_field_failures(self):
        for selector in binding.SELECTORS:
            value, raw = build_model(selector)
            binding.validate_build(value, raw, HEAD, selector)
            if value["helpers"]:
                helper_id = next(iter(value["helpers"]))
                bad = copy.deepcopy(value)
                del bad["helpers"][helper_id]
                with self.assertRaisesRegex(binding.Rejected, "helper-set"):
                    binding.validate_build(bad, raw, HEAD, selector)
                for field, changed in (("exit", 1), ("joined", False), ("phase", "publish")):
                    bad = copy.deepcopy(value)
                    bad["helpers"][helper_id]["commands"][0][field] = changed
                    with self.assertRaises(binding.Rejected): binding.validate_build(bad, raw, HEAD, selector)
                bad = copy.deepcopy(value)
                bad["helpers"][helper_id]["image"]["root"] = bad["tool"]["root"]
                with self.assertRaisesRegex(binding.Rejected, "helper-root-overlap"):
                    binding.validate_build(bad, raw, HEAD, selector)
                bad = copy.deepcopy(value)
                bad["helpers"][helper_id]["commands"][1]["argv"].append("-p:EvidencePrivateQualification=N04")
                with self.assertRaisesRegex(binding.Rejected, "helper-command-unknown-option"):
                    binding.validate_build(bad, raw, HEAD, selector)


class PublishImagePreparationTests(unittest.TestCase):
    """Owned ordinary-file packaging controls; no protected image is admitted."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-image-modes-")
        self.addCleanup(self.temporary.cleanup)
        self.parent = Path(self.temporary.name).resolve()
        self.root = self.parent / "bundle"
        self.root.mkdir(mode=0o700)
        self.data = self.root / "data.bin"
        self.data.write_bytes(b"actual copied dependency bytes")
        self.data.chmod(0o600)
        self.executable = self.root / "native"
        self.executable.write_bytes(b"actual executable fixture bytes")
        self.executable.chmod(0o744)
        self.nested = self.root / "nested"
        self.nested.mkdir(mode=0o700)
        self.metadata = self.nested / "metadata.json"
        self.metadata.write_bytes(b"{}")
        self.metadata.chmod(0o640)

    def prepare(self):
        return binding.prepare_publish_image(self.root, deadline=time.monotonic() + 5)

    def test_canonical_modes_preserve_all_bytes_and_private_parent(self):
        expected = {p: p.read_bytes() for p in (self.data, self.executable, self.metadata)}
        self.prepare()
        self.assertEqual({p: p.read_bytes() for p in expected}, expected)
        self.assertEqual(self.data.stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.metadata.stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.executable.stat().st_mode & 0o777, 0o755)
        self.assertEqual(self.nested.stat().st_mode & 0o777, 0o755)
        self.assertEqual(self.parent.stat().st_mode & 0o777, 0o700)
        measured = binding.inventory(self.root, "data.bin", deadline=time.monotonic() + 5)
        self.assertEqual(set(measured["files"]), {"data.bin", "native", "nested/metadata.json"})

    def test_symlink_hardlink_and_fifo_reject_before_chmod(self):
        outside = self.parent / "outside"
        outside.write_bytes(b"outside sentinel")
        outside.chmod(0o640)
        for kind in ("symlink", "hardlink", "fifo"):
            with self.subTest(kind=kind):
                bad = self.root / "z-bad"
                if kind == "symlink": bad.symlink_to(outside)
                elif kind == "hardlink": os.link(outside, bad)
                else: os.mkfifo(bad)
                try:
                    with mock.patch.object(binding.os, "fchmod") as chmod:
                        with self.assertRaises(binding.Rejected): self.prepare()
                        chmod.assert_not_called()
                    self.assertEqual(outside.read_bytes(), b"outside sentinel")
                    self.assertEqual(outside.stat().st_mode & 0o777, 0o640)
                    self.assertEqual(self.data.stat().st_mode & 0o777, 0o600)
                finally:
                    bad.unlink()

    def test_writable_by_others_files_and_directories_reject_before_chmod(self):
        for directory in (False, True):
            with self.subTest(directory=directory):
                bad = self.root / "z-writable"
                if directory: bad.mkdir()
                else: bad.write_bytes(b"owned but unsafe")
                bad.chmod(0o777)
                try:
                    with mock.patch.object(binding.os, "fchmod") as chmod:
                        with self.assertRaises(binding.Rejected): self.prepare()
                        chmod.assert_not_called()
                    self.assertEqual(bad.stat().st_mode & 0o777, 0o777)
                finally:
                    bad.rmdir() if directory else bad.unlink()

    def test_expired_deadline_and_byte_bound_reject_before_chmod(self):
        with mock.patch.object(binding.os, "fchmod") as chmod:
            with self.assertRaises(binding.Rejected):
                binding.prepare_publish_image(self.root, deadline=time.monotonic() - 1)
            with mock.patch.object(binding, "MAX_FILE", 1):
                with self.assertRaises(binding.Rejected): self.prepare()
            chmod.assert_not_called()

    def test_nonprivate_or_foreign_parent_rejects_before_chmod(self):
        self.parent.chmod(0o755)
        try:
            with mock.patch.object(binding.os, "fchmod") as chmod:
                with self.assertRaises(binding.Rejected): self.prepare()
                chmod.assert_not_called()
        finally:
            self.parent.chmod(0o700)
        with mock.patch.object(binding.os, "geteuid", return_value=os.geteuid() + 1), \
                mock.patch.object(binding.os, "fchmod") as chmod:
            with self.assertRaises(binding.Rejected): self.prepare()
            chmod.assert_not_called()

    def test_root_alias_rejects_without_changing_its_target(self):
        alias = self.parent / "alias"
        alias.symlink_to(self.root, target_is_directory=True)
        with mock.patch.object(binding.os, "fchmod") as chmod:
            with self.assertRaises(binding.Rejected):
                binding.prepare_publish_image(alias, deadline=time.monotonic() + 5)
            chmod.assert_not_called()
        self.assertEqual(self.data.stat().st_mode & 0o777, 0o600)

    def test_named_file_substitution_preserves_replacement_mode(self):
        original_open = os.open
        replaced = False

        def open_replacement(path, *args, **kwargs):
            nonlocal replaced
            if isinstance(path, (str, Path)) and Path(path).name == "data.bin" and not replaced:
                replaced = True
                self.data.rename(self.root / "saved")
                self.data.write_bytes(b"replacement sentinel")
                self.data.chmod(0o640)
            return original_open(path, *args, **kwargs)

        with mock.patch.object(binding.os, "open", side_effect=open_replacement):
            with self.assertRaises(binding.Rejected): self.prepare()
        self.assertTrue(replaced)
        self.assertEqual(self.data.read_bytes(), b"replacement sentinel")
        self.assertEqual(self.data.stat().st_mode & 0o777, 0o640)

    def test_same_inode_growth_is_rejected_without_changing_affected_mode(self):
        original_open = os.open
        changed = False

        def open_growth(path, *args, **kwargs):
            nonlocal changed
            if isinstance(path, (str, Path)) and Path(path).name == "data.bin" and not changed:
                changed = True
                with self.data.open("ab") as stream: stream.write(b"unexpected growth")
            return original_open(path, *args, **kwargs)

        with mock.patch.object(binding.os, "open", side_effect=open_growth):
            with self.assertRaises(binding.Rejected): self.prepare()
        self.assertTrue(changed)
        self.assertEqual(self.data.stat().st_mode & 0o777, 0o600)


if __name__ == "__main__":
    unittest.main()
