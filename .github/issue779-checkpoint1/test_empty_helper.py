"""Real ordinary-directory preparation controls; never a root/native audit pass."""

import ast
import importlib.util
import os
from pathlib import Path
import stat
import tempfile
import time
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("empty_helper_metadata", ROOT / "procedures/os_audit.py")
audit = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(audit)


class EmptyHelperControls(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="c1-empty-helper-")
        self.addCleanup(temporary.cleanup)
        self.parent = Path(temporary.name).resolve()
        self.parent.chmod(0o700)
        self.target = self.parent / "empty-audit-helper"

    def test_actual_mode_is_0555_under_000_and_077_umasks(self):
        for mask in (0o000, 0o077):
            parent = self.parent / str(mask)
            parent.mkdir(mode=0o700)
            parent.chmod(0o700)
            target = parent / self.target.name
            old = os.umask(mask)
            try:
                actual = audit.create_empty_helper(target, deadline=time.monotonic() + 5)
            finally:
                os.umask(old)
            with self.subTest(mask=mask):
                self.assertEqual(target, actual)
                self.assertEqual(0o555, stat.S_IMODE(target.lstat().st_mode))
                self.assertEqual((os.geteuid(), os.getegid()), (target.stat().st_uid, target.stat().st_gid))
                self.assertEqual([], list(target.iterdir()))
                self.assertEqual(0o700, stat.S_IMODE(parent.stat().st_mode))

    def test_existing_file_is_not_adopted_or_modified(self):
        self.target.write_bytes(b"ordinary canary")
        before = audit.identity(self.target.lstat())
        with self.assertRaises(audit.AuditError):
            audit.create_empty_helper(self.target, deadline=time.monotonic() + 5)
        self.assertEqual(before, audit.identity(self.target.lstat()))
        self.assertEqual(b"ordinary canary", self.target.read_bytes())

    def test_existing_directory_is_not_adopted_or_modified(self):
        self.target.mkdir(mode=0o700)
        marker = self.target / "marker"
        marker.write_bytes(b"retained")
        before = audit.identity(self.target.lstat())
        with self.assertRaises(audit.AuditError):
            audit.create_empty_helper(self.target, deadline=time.monotonic() + 5)
        self.assertEqual(before, audit.identity(self.target.lstat()))
        self.assertEqual(b"retained", marker.read_bytes())

    def test_existing_symlink_and_outside_target_remain_unchanged(self):
        outside = self.parent / "outside"
        outside.mkdir(mode=0o700)
        marker = outside / "marker"
        marker.write_bytes(b"outside")
        self.target.symlink_to(outside, target_is_directory=True)
        before = audit.identity(self.target.lstat()), audit.identity(outside.lstat())
        with self.assertRaises(audit.AuditError):
            audit.create_empty_helper(self.target, deadline=time.monotonic() + 5)
        self.assertEqual(before, (audit.identity(self.target.lstat()), audit.identity(outside.lstat())))
        self.assertTrue(self.target.is_symlink())
        self.assertEqual(b"outside", marker.read_bytes())

    def test_expired_deadline_rejects_before_creation(self):
        before = audit.identity(self.parent.lstat())
        with self.assertRaisesRegex(audit.AuditError, "^empty-helper-deadline$"):
            audit.create_empty_helper(self.target, deadline=time.monotonic() - 1)
        self.assertFalse(os.path.lexists(self.target))
        self.assertEqual(before, audit.identity(self.parent.lstat()))

    def test_fchmod_failure_closes_real_fds_and_retains_unadoptable_partial(self):
        opened = []
        original_open = os.open
        def track(*args, **kwargs):
            fd = original_open(*args, **kwargs)
            opened.append(fd)
            return fd
        with mock.patch.object(audit.os, "open", side_effect=track), \
             mock.patch.object(audit.os, "fchmod", side_effect=OSError("private-error-canary")):
            with self.assertRaisesRegex(audit.AuditError, "^empty-helper-io$") as caught:
                audit.create_empty_helper(self.target, deadline=time.monotonic() + 5)
        self.assertNotIn("private-error-canary", str(caught.exception))
        self.assertEqual(2, len(opened))
        for fd in opened:
            with self.assertRaises(OSError):
                os.fstat(fd)
        self.assertTrue(self.target.is_dir())
        self.assertEqual(0o700, stat.S_IMODE(self.target.stat().st_mode))
        self.assertEqual([], list(self.target.iterdir()))
        before = audit.identity(self.target.lstat())
        with self.assertRaises(audit.AuditError):
            audit.create_empty_helper(self.target, deadline=time.monotonic() + 5)
        self.assertEqual(before, audit.identity(self.target.lstat()))

    def test_fixed_leaf_and_canonical_parent_are_required(self):
        wrong = self.parent / "other-helper"
        with self.assertRaisesRegex(audit.AuditError, "^empty-helper-path$"):
            audit.create_empty_helper(wrong, deadline=time.monotonic() + 5)
        self.assertFalse(wrong.exists())
        real = self.parent / "real"
        real.mkdir(mode=0o700)
        real.chmod(0o700)
        alias = self.parent / "alias"
        alias.symlink_to(real, target_is_directory=True)
        with self.assertRaisesRegex(audit.AuditError, "^empty-helper-path$"):
            audit.create_empty_helper(alias / self.target.name, deadline=time.monotonic() + 5)
        self.assertEqual([], list(real.iterdir()))

    def test_all_four_adapters_select_metadata_helper_without_renewing_deadline(self):
        for case in ("n09", "n10", "n15", "n16"):
            tree = ast.parse((ROOT / "procedures" / case / "adapter.py").read_bytes())
            assignments = [n for n in ast.walk(tree) if isinstance(n, ast.Assign)
                           and any(isinstance(t, ast.Name) and t.id == "empty_helper" for t in n.targets)]
            with self.subTest(case=case):
                self.assertEqual(1, len(assignments))
                call = assignments[0].value
                self.assertIsInstance(call, ast.Call)
                self.assertEqual("create_empty_helper", call.func.attr)
                self.assertEqual("retained / 'empty-audit-helper'", ast.unparse(call.args[0]))
                self.assertEqual(["deadline"], [k.arg for k in call.keywords])
                expected = "min(deadline, original_work_end)" if case == "n15" else "deadline"
                self.assertEqual(expected, ast.unparse(call.keywords[0].value))


if __name__ == "__main__":
    unittest.main()
