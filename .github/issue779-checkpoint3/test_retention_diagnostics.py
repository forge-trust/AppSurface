"""Regression tests for bounded, specific C3 retention failure diagnostics."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import os
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("check-retention.py")
SPEC = importlib.util.spec_from_file_location("c3_check_retention", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class RetentionDiagnosticTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "evidence"
        self.root.mkdir(mode=0o700)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def rejected(self) -> str:
        output = io.StringIO()
        with contextlib.redirect_stderr(output), self.assertRaises(SystemExit) as raised:
            subject.check(self.root)
        self.assertEqual(1, raised.exception.code)
        return output.getvalue().strip()

    def test_size_failure_names_known_relative_file_and_exact_reason(self) -> None:
        nested = self.root / "root-file-tests"
        nested.mkdir()
        with (nested / "root-files.trx").open("wb") as stream:
            stream.truncate(8 * 1024 * 1024 + 1)
        message = self.rejected()
        self.assertEqual("C3_RETENTION_REJECT reason=size-cap leaf=root-file-tests/root-files.trx", message)

    def test_symlink_failure_has_fixed_reason_without_target_path(self) -> None:
        outside = Path(self.temporary.name) / "outside-secret-path"
        outside.write_bytes(b"unchanged")
        (self.root / "untrusted-name-with-secrets").symlink_to(outside)
        message = self.rejected()
        self.assertIn("reason=symlink", message)
        self.assertNotIn("outside-secret-path", message)
        self.assertIn("leaf=leaf-", message)
        self.assertEqual(b"unchanged", outside.read_bytes())

    def test_hardlink_failure_is_reported_separately(self) -> None:
        leaf = self.root / "root-supplement.trx"
        leaf.write_bytes(b"fixture")
        os.link(leaf, Path(self.temporary.name) / "extra-link")
        self.assertEqual("C3_RETENTION_REJECT reason=link-count leaf=root-supplement.trx", self.rejected())


if __name__ == "__main__":
    unittest.main()
