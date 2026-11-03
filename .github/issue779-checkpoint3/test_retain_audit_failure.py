"""Data-only tests for bounded preservation of an actual failed audit attempt."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import stat
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("retain_audit_failure.py")
SPEC = importlib.util.spec_from_file_location("retain_audit_failure", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class AuditFailureRetentionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.source = self.root / "source"
        self.parent = self.root / "diagnostics"
        self.source.mkdir(mode=0o700)
        self.parent.mkdir(mode=0o700)
        self.destination_name = "attempt-one"
        self.owner = {"destination_uid": os.geteuid(), "destination_gid": os.getegid()}

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_sources(self, *, log: bytes = b"", trx: bytes | None = None,
                      capture: bytes = b'{"source":"test"}\n') -> None:
        (self.source / "application-bundle-driver.log").write_bytes(log)
        (self.source / "source.json").write_bytes(capture)
        if trx is not None:
            nested = self.source / "application-bundle-tests"
            nested.mkdir(mode=0o700)
            (nested / "application-bundle.trx").write_bytes(trx)

    def run_preserve(self):
        return subject.preserve(self.source, self.parent, self.destination_name, **self.owner)

    def test_preserves_exact_log_capture_and_optional_trx_as_private_files(self) -> None:
        self.write_sources(log=b"failed audit output\n", trx=b"<TestRun />\n")
        summary = self.run_preserve()
        destination = self.parent / self.destination_name
        self.assertEqual(b"failed audit output\n", (destination / "application-bundle-driver.log").read_bytes())
        self.assertEqual(b"<TestRun />\n", (destination / "application-bundle-tests/application-bundle.trx").read_bytes())
        self.assertEqual((self.source / "source.json").read_bytes(), (destination / "source.json").read_bytes())
        self.assertEqual(0o700, stat.S_IMODE(destination.stat().st_mode))
        for path in (destination / "application-bundle-driver.log",
                     destination / "application-bundle-tests/application-bundle.trx",
                     destination / "source.json", destination / "retention-summary.json"):
            self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            self.assertEqual(1, path.stat().st_nlink)
        self.assertEqual(hashlib.sha256(b"<TestRun />\n").hexdigest(),
                         summary["files"]["application-bundle-tests/application-bundle.trx"]["sha256"])
        self.assertFalse(summary["native_claimed"])
        self.assertFalse(summary["gate_pass_claimed"])
        self.assertFalse(summary["accepted"])
        self.assertNotIn("failed audit output", json.dumps(summary))

    def test_empty_required_log_and_absent_trx_are_recorded_truthfully(self) -> None:
        self.write_sources(log=b"")
        summary = self.run_preserve()
        self.assertEqual(b"", (self.parent / self.destination_name / "application-bundle-driver.log").read_bytes())
        self.assertEqual({"present": False, "bytes": 0, "sha256": None},
                         summary["files"]["application-bundle-tests/application-bundle.trx"])

    def test_trx_size_limit_rejects_without_truncating_source(self) -> None:
        oversized = b"x" * (subject.MAX_TRX + 1)
        self.write_sources(log=b"failure", trx=oversized)
        with self.assertRaisesRegex(subject.RetentionError, "source-size-limit"):
            self.run_preserve()
        self.assertEqual(oversized, (self.source / "application-bundle-tests/application-bundle.trx").read_bytes())
        self.assertFalse((self.parent / self.destination_name / "retention-summary.json").exists())

    def test_driver_log_size_limit_rejects_without_truncating_source(self) -> None:
        oversized = b"x" * (subject.MAX_DRIVER_LOG + 1)
        self.write_sources(log=oversized)
        with self.assertRaisesRegex(subject.RetentionError, "source-size-limit"):
            self.run_preserve()
        self.assertEqual(oversized, (self.source / "application-bundle-driver.log").read_bytes())

    def test_source_capture_limit_is_bounded(self) -> None:
        self.write_sources(log=b"failure", capture=b"x" * (subject.MAX_SOURCE_CAPTURE + 1))
        with self.assertRaisesRegex(subject.RetentionError, "source-size-limit"):
            self.run_preserve()

    def test_symlink_and_hardlink_sources_reject(self) -> None:
        outside = self.root / "outside.log"
        outside.write_bytes(b"failure")
        (self.source / "application-bundle-driver.log").symlink_to(outside)
        (self.source / "source.json").write_bytes(b"{}")
        with self.assertRaises(subject.RetentionError):
            self.run_preserve()
        (self.source / "application-bundle-driver.log").unlink()
        (self.source / "application-bundle-driver.log").write_bytes(b"failure")
        os.link(self.source / "application-bundle-driver.log", self.root / "hardlink.log")
        self.destination_name = "attempt-two"
        with self.assertRaisesRegex(subject.RetentionError, "source-file-type-or-links-rejected"):
            self.run_preserve()

    def test_fixed_source_names_cannot_be_redirected_by_caller(self) -> None:
        self.write_sources(log=b"failure")
        with self.assertRaises(TypeError):
            subject.preserve(self.source, self.parent, self.destination_name,
                             log_name="other.log", **self.owner)

    def test_destination_must_be_fresh_and_root_components_must_not_be_symlinked(self) -> None:
        self.write_sources(log=b"failure")
        self.run_preserve()
        with self.assertRaises(FileExistsError):
            self.run_preserve()
        real_parent = self.root / "real-parent"
        real_parent.mkdir()
        parent_link = self.root / "parent-link"
        parent_link.symlink_to(real_parent, target_is_directory=True)
        with self.assertRaises(OSError):
            subject.preserve(self.source, parent_link, "other-attempt", **self.owner)

    def test_invalid_owner_types_are_rejected(self) -> None:
        self.write_sources(log=b"failure")
        with self.assertRaisesRegex(subject.RetentionError, "destination-uid-invalid"):
            subject.preserve(self.source, self.parent, self.destination_name,
                             destination_uid=True, destination_gid=self.owner["destination_gid"])


if __name__ == "__main__":
    unittest.main()
