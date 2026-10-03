"""Portable real-FD data controls only: no launcher completion, root, lease or acceptance is fabricated."""
import importlib.util
import os
from pathlib import Path
import stat
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("private_retained_output", Path(__file__).with_name("retained-output.py"))
collector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(collector)


class RetainedOutputControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.parent = Path(self.temp.name).resolve() / "anchor"
        self.output = self.parent / collector.OUTPUT_SLOT
        self.output.mkdir(parents=True)
        self.parent.chmod(0o700); self.output.chmod(0o700)
        self.plan = b"opaque-plan\x00\xff"
        self.contents = {name: (self.plan if name == "evidence-plan.json" else b"private-canary\x00\xfe")
                         for name in collector.EXPECTED_FILES}
        for name, data in self.contents.items():
            path = self.output / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data); path.chmod(0o600)
        for name in collector.EXPECTED_DIRECTORIES:
            (self.output / name).chmod(0o700)
        self.parent_fd = os.open(self.parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        self.output_fd = os.open(self.output, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        self.addCleanup(os.close, self.parent_fd); self.addCleanup(os.close, self.output_fd)
        self.parent_identity = collector._receipt(os.fstat(self.parent_fd))
        self.output_identity = collector._receipt(os.fstat(self.output_fd))

    def read(self, **changes):
        values = dict(output_fd=self.output_fd, parent_fd=self.parent_fd, parent_path=str(self.parent),
                      output_identity=self.output_identity, parent_identity=self.parent_identity,
                      expected_plan_bytes=self.plan, deadline=time.monotonic() + 5,
                      expected_uid=os.geteuid(), expected_gid=os.getegid())
        values.update(changes)
        return collector._collect_fds(**values)

    def reject(self, **changes):
        with self.assertRaises(collector.RetainedOutputError) as failure:
            self.read(**changes)
        self.assertEqual("retained-output-failed", str(failure.exception))
        self.assertNotIn("private-canary", str(failure.exception))

    def test_exact_four_raw_binary_files_and_matching_plan_with_real_retained_fds(self):
        self.assertEqual(self.contents, self.read())
        self.assertEqual(set(collector.EXPECTED_FILES), set(self.read()))
        os.fstat(self.parent_fd); os.fstat(self.output_fd)

    def test_expected_plan_mismatch_missing_file_and_unexpected_file_reject(self):
        self.assertEqual(self.contents, self.read())
        self.reject(expected_plan_bytes=b"different")
        path = self.output / "evidence-summary.json"
        data = path.read_bytes(); path.unlink()
        self.reject()
        path.write_bytes(data); path.chmod(0o600)
        extra = self.output / "unexpected.json"
        extra.write_bytes(b"private-canary"); extra.chmod(0o600)
        self.reject()

    def test_file_symlink_hardlink_directory_and_fifo_never_count_as_regular_output(self):
        path = self.output / "evidence-summary.json"
        original = path.read_bytes()
        for shape in ("symlink", "hardlink", "directory", "fifo"):
            with self.subTest(shape=shape):
                path.unlink()
                outside = Path(self.temp.name).resolve() / "outside"
                outside.write_bytes(original); outside.chmod(0o600)
                if shape == "symlink": path.symlink_to(outside)
                elif shape == "hardlink": os.link(outside, path)
                elif shape == "directory": path.mkdir()
                else: os.mkfifo(path, 0o600)
                self.reject()
                if shape == "directory": path.rmdir()
                else: path.unlink()
                outside.unlink()
                path.write_bytes(original); path.chmod(0o600)

    def test_file_and_directory_modes_wrong_uid_gid_and_receipt_shape_are_rejected(self):
        self.assertEqual(self.contents, self.read())
        path = self.output / "evidence-summary.json"
        for mode in (0o644, 0o700, 0o4700):
            with self.subTest(mode=mode):
                path.chmod(mode)
                self.assertNotEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                self.reject(); path.chmod(0o600)
        for directory in (self.parent, self.output, self.output / "qual-coverage"):
            directory.chmod(0o750); self.reject(); directory.chmod(0o700)
        self.reject(expected_uid=os.geteuid() + 1)
        self.reject(expected_gid=os.getegid() + 1)
        self.reject(output_identity={**self.output_identity, "inode": self.output_identity["inode"] + 1})
        self.reject(parent_identity={**self.parent_identity, "extra": 0})
        self.reject(output_identity={**self.output_identity, "uid": True})

    def test_named_output_and_parent_substitution_reject_even_when_original_fds_remain_live(self):
        renamed = self.parent / "retained-original"
        self.output.rename(renamed)
        self.output.mkdir(mode=0o700)
        self.reject()
        self.output.rmdir(); renamed.rename(self.output)
        old_parent = self.parent.with_name("retained-anchor")
        self.parent.rename(old_parent)
        self.parent.mkdir(mode=0o700)
        self.reject()
        self.parent.rmdir(); old_parent.rename(self.parent)

    def test_symlinked_tree_directory_and_current_absolute_ancestor_are_rejected(self):
        merged = self.output / "qual-coverage" / "merged"
        saved = merged.with_name("saved")
        merged.rename(saved); merged.symlink_to(saved, target_is_directory=True)
        self.reject()
        merged.unlink(); saved.rename(merged)
        saved_parent = self.parent.with_name("actual-anchor")
        self.parent.rename(saved_parent); self.parent.symlink_to(saved_parent, target_is_directory=True)
        self.reject()
        self.parent.unlink(); saved_parent.rename(self.parent)

    def test_content_growth_or_same_length_named_replacement_during_read_is_rejected(self):
        original_read = os.read
        path = self.output / "evidence-manifest.json"
        for mutation in ("growth", "replace"):
            with self.subTest(mutation=mutation):
                path.write_bytes(self.contents["evidence-manifest.json"]); path.chmod(0o600)
                inode = path.stat().st_ino
                changed = False
                def read(fd, size):
                    nonlocal changed
                    data = original_read(fd, size)
                    if not changed and os.fstat(fd).st_ino == inode:
                        changed = True
                        if mutation == "growth":
                            with path.open("ab") as output: output.write(b"extra")
                        else:
                            path.unlink(); path.write_bytes(b"x" * len(data)); path.chmod(0o600)
                    return data
                with patch.object(collector.os, "read", side_effect=read): self.reject()
                self.assertTrue(changed)

    def test_per_file_aggregate_count_depth_bounds_and_chunk_deadline_are_enforced(self):
        self.assertEqual(self.contents, self.read())
        for constant, value in (("MAX_FILE_BYTES", 5), ("MAX_TOTAL_BYTES", 25), ("MAX_FILES", 3), ("MAX_DEPTH", 1)):
            with self.subTest(constant=constant), patch.object(collector, constant, value): self.reject()
        self.reject(deadline=time.monotonic() - 1)
        self.reject(deadline=float("inf")); self.reject(deadline=True)
        original_read = os.read
        expired = False
        def read(fd, size):
            nonlocal expired
            data = original_read(fd, size); expired = True
            return data
        with patch.object(collector.os, "read", side_effect=read), \
             patch.object(collector.time, "monotonic", side_effect=lambda: 6 if expired else 0):
            self.reject(deadline=5)

    def test_reader_error_closes_every_opened_child_and_keeps_borrowed_fds(self):
        original_open = os.open
        opened = []
        def open_fd(*args, **kwargs):
            fd = original_open(*args, **kwargs); opened.append(fd); return fd
        with patch.object(collector.os, "open", side_effect=open_fd), \
             patch.object(collector.os, "read", side_effect=OSError("private-canary")):
            self.reject()
        for fd in set(opened):
            with self.assertRaises(OSError): os.fstat(fd)
        os.fstat(self.parent_fd); os.fstat(self.output_fd)

    def test_final_rechecks_catch_replacement_after_first_file_has_been_read(self):
        original_read = os.read
        first = self.output / "evidence-manifest.json"
        later = self.output / "evidence-summary.json"
        changed = False
        def read(fd, size):
            nonlocal changed
            data = original_read(fd, size)
            if not changed and os.fstat(fd).st_ino == later.stat().st_ino:
                changed = True
                first.unlink(); first.write_bytes(b"same-length-canary"); first.chmod(0o600)
            return data
        with patch.object(collector.os, "read", side_effect=read): self.reject()
        self.assertTrue(changed)

    def test_public_wrapper_failure_closes_first_duplicate_and_never_closes_completion(self):
        duplicate = os.dup(self.output_fd)
        closed = []
        completion = SimpleNamespace(
            descriptor={"worker_uid": 65010, "worker_gid": 65011, "output_slot": "qualification",
                        "output_parent": str(self.parent),
                        "output_parent_identity": {**self.parent_identity, "uid": 65010, "gid": 65011}},
            output_identity={**self.output_identity, "uid": 65010, "gid": 65011},
            output_parent_identity={**self.parent_identity, "uid": 65010, "gid": 65011},
            output=self.output, duplicate_output_directory=lambda: duplicate,
            duplicate_output_parent=lambda: (_ for _ in ()).throw(OSError("private-canary")),
            close=lambda: closed.append(True))
        with self.assertRaises(collector.RetainedOutputError):
            collector.collect(completion, self.plan, time.monotonic() + 5)
        with self.assertRaises(OSError): os.fstat(duplicate)
        self.assertEqual([], closed)
        os.fstat(self.output_fd)

    def test_bad_descriptor_preflight_makes_no_duplicate_calls(self):
        called = []
        completion = SimpleNamespace(descriptor={"worker_uid": 0}, output_identity={}, output_parent_identity={},
                                     duplicate_output_directory=lambda: called.append(True),
                                     duplicate_output_parent=lambda: called.append(True))
        with self.assertRaises(collector.RetainedOutputError):
            collector.collect(completion, self.plan, time.monotonic() + 5)
        self.assertEqual([], called)

    def test_closed_host_layout_returns_only_actual_manifest_and_report_without_synthetic_plan(self):
        report = "qual-coverage/merged/coverage.cobertura.xml"
        for name in collector.EXPECTED_FILES - {report}:
            (self.output / name).unlink()
        actual_manifest = b"opaque-host-manifest\x00\xff"
        manifest = self.output / "manifest.json"
        manifest.write_bytes(actual_manifest); manifest.chmod(0o600)
        result = self.read(entry="host")
        self.assertEqual({"manifest.json": actual_manifest, report: self.contents[report]}, result)
        self.assertEqual(result, self.read(entry="host", expected_plan_bytes=b"other-root-selected-plan"))
        self.assertNotIn("evidence-plan.json", result)
        self.assertNotIn("evidence-summary.json", result)
        self.reject(entry="cli")
        for entry in ("Host", "unknown", True, None):
            with self.subTest(entry=entry): self.reject(entry=entry)
        extra = self.output / "evidence-plan.json"
        extra.write_bytes(self.plan); extra.chmod(0o600)
        self.reject(entry="host")


if __name__ == "__main__":
    unittest.main()
