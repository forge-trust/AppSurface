"""Actual owned-file retention negatives; no root or native worker is created."""

import importlib.util
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("c1_retention", Path(__file__).with_name("check-retention.py"))
retention = importlib.util.module_from_spec(spec)
spec.loader.exec_module(retention)


class RetentionControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-retention-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.evidence = self.root / "evidence"
        self.evidence.mkdir(mode=0o755)
        self.log = self.evidence / "private.log"
        self.log.write_bytes(b"private canary\x00")
        self.log.chmod(0o644)

    def test_private_modes_preserve_exact_bytes_and_report_actual_totals(self):
        expected = self.log.read_bytes()
        result = retention.check(self.evidence)
        self.assertEqual(result, {"files": 1, "bytes": len(expected)})
        self.assertEqual(self.log.read_bytes(), expected)
        self.assertEqual(self.log.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.evidence.stat().st_mode & 0o777, 0o700)

    def test_only_fixed_raw_archives_retain_the_original_ustar_byte_cap(self):
        for name in ("native-private.tar", "native.tar", "arbitrary.tar"):
            with self.subTest(name=name):
                node = self.evidence / name
                with node.open("xb") as stream:
                    stream.truncate(retention.ARCHIVE_LIMIT)
                node.chmod(0o644)
                try:
                    if name in retention.ARCHIVE_NAMES:
                        result = retention.check(self.evidence)
                        self.assertEqual(result["bytes"], retention.ARCHIVE_LIMIT + self.log.stat().st_size)
                        self.assertEqual(node.stat().st_size, retention.ARCHIVE_LIMIT)
                        with node.open("r+b") as stream:
                            stream.truncate(retention.ARCHIVE_LIMIT + 1)
                        with self.assertRaisesRegex(ValueError, "^retention-file-bytes$"):
                            retention.check(self.evidence)
                    else:
                        with patch.object(retention.os, "fchmod") as chmod:
                            with self.assertRaisesRegex(ValueError, "^retention-file-bytes$"):
                                retention.check(self.evidence)
                            chmod.assert_not_called()
                finally:
                    node.unlink()

    def test_symlink_hardlink_and_fifo_reject_before_any_permission_change(self):
        outside = self.root / "outside"
        outside.write_bytes(b"outside sentinel")
        outside.chmod(0o644)
        for kind in ("symlink", "hardlink", "fifo"):
            with self.subTest(kind=kind):
                node = self.evidence / "unsafe"
                if kind == "symlink":
                    node.symlink_to(outside)
                elif kind == "hardlink":
                    os.link(outside, node)
                else:
                    os.mkfifo(node)
                try:
                    with patch.object(retention.os, "fchmod") as chmod:
                        with self.assertRaisesRegex(ValueError, "^retention-file$"):
                            retention.check(self.evidence)
                        chmod.assert_not_called()
                    self.assertEqual(outside.read_bytes(), b"outside sentinel")
                    self.assertEqual(outside.stat().st_mode & 0o777, 0o644)
                    self.assertEqual(self.log.stat().st_mode & 0o777, 0o644)
                finally:
                    node.unlink()

    def test_aggregate_bound_keeps_all_original_files_without_truncation(self):
        second = self.evidence / "second"
        second.write_bytes(b"second canary")
        with patch.object(retention, "TOTAL_LIMIT", self.log.stat().st_size):
            with self.assertRaisesRegex(ValueError, "^retention-tree-bound$"):
                retention.check(self.evidence)
        self.assertEqual(second.read_bytes(), b"second canary")
        self.assertEqual(self.log.read_bytes(), b"private canary\x00")

    def test_root_symlink_and_expired_deadline_do_not_change_outside_directory(self):
        alias = self.root / "alias"
        alias.symlink_to(self.evidence, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "^retention-file$"):
            retention.check(alias)
        with self.assertRaisesRegex(ValueError, "^retention-deadline$"):
            retention.check(self.evidence, deadline=time.monotonic() - 1)
        self.assertEqual(self.evidence.stat().st_mode & 0o777, 0o755)

    def test_substituted_leaf_rejects_after_validation_and_preserves_replacement(self):
        original = retention.os.open
        replaced = False

        def open_after_substitution(path, *args, **kwargs):
            nonlocal replaced
            if Path(path) == self.log and not replaced:
                replaced = True
                self.log.rename(self.evidence / "original")
                self.log.write_bytes(b"replacement canary")
                self.log.chmod(0o644)
            return original(path, *args, **kwargs)

        with patch.object(retention.os, "open", side_effect=open_after_substitution):
            with self.assertRaisesRegex(ValueError, "^retention-node-changed$"):
                retention.check(self.evidence)
        self.assertTrue(replaced)
        self.assertEqual(self.log.read_bytes(), b"replacement canary")
        self.assertEqual(self.log.stat().st_mode & 0o777, 0o644)


if __name__ == "__main__":
    unittest.main()
