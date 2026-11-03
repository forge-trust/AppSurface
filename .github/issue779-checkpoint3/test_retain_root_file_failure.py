"""Data-only checks for bounded diagnostics from a failed root-file measurement."""

from __future__ import annotations

import hashlib
import importlib.util
import os
import stat
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("retain_root_file_failure.py")
SPEC = importlib.util.spec_from_file_location("retain_root_file_failure", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
subject = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(subject)


class RootFileFailureRetentionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        self.source = self.root / "source"
        self.parent = self.root / "diagnostics"
        self.source.mkdir(mode=0o700)
        self.parent.mkdir(mode=0o700)
        self.owner = {"destination_uid": os.geteuid(), "destination_gid": os.getegid()}
        (self.source / "source.json").write_bytes(b'{"source":"fixture"}\n')

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def preserve(self):
        return subject.preserve(self.source, self.parent, "attempt", **self.owner)

    def test_preserves_bounded_log_suffix_exact_trx_and_false_claim_summary(self) -> None:
        source_log = b"x" * (subject.MAX_DRIVER_TAIL + 17) + b"useful failure tail\n"
        (self.source / "root-file-driver.log").write_bytes(source_log)
        tests = self.source / "root-file-tests"
        tests.mkdir(mode=0o700)
        raw_trx = b"<TestRun />\n"
        (tests / "root-files.trx").write_bytes(raw_trx)

        summary = self.preserve()
        destination = self.parent / "attempt"
        tail = destination / "root-file-driver.tail.log"
        self.assertEqual(source_log[-subject.MAX_DRIVER_TAIL:], tail.read_bytes())
        self.assertEqual(raw_trx, (destination / "root-file-tests/root-files.trx").read_bytes())
        self.assertEqual(
            hashlib.sha256(source_log).hexdigest(),
            summary["files"]["root-file-driver.log"]["source_sha256"],
        )
        self.assertEqual(len(source_log), summary["files"]["root-file-driver.log"]["source_bytes"])
        self.assertTrue(summary["files"]["root-file-driver.log"]["retained_suffix"])
        self.assertFalse(summary["native_claimed"])
        self.assertFalse(summary["gate_pass_claimed"])
        self.assertFalse(summary["accepted"])
        self.assertEqual(0o700, stat.S_IMODE(destination.stat().st_mode))
        for path in destination.rglob("*"):
            if path.is_file():
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                self.assertEqual(1, path.stat().st_nlink)

    def test_missing_log_and_trx_are_truthfully_recorded_without_fabrication(self) -> None:
        summary = self.preserve()
        self.assertFalse(summary["files"]["root-file-driver.log"]["present"])
        self.assertFalse(summary["files"]["root-file-tests/root-files.trx"]["present"])
        self.assertFalse((self.parent / "attempt/root-file-driver.tail.log").exists())

    def test_rejects_oversized_log_without_truncating_source(self) -> None:
        log = self.source / "root-file-driver.log"
        with log.open("wb") as stream:
            stream.truncate(subject.MAX_DRIVER_SOURCE + 1)
        with self.assertRaisesRegex(subject.RetentionError, "source-size-limit"):
            self.preserve()
        self.assertEqual(subject.MAX_DRIVER_SOURCE + 1, log.stat().st_size)

    def test_rejects_linked_source_files_without_following_symlinks(self) -> None:
        log = self.source / "root-file-driver.log"
        log.write_bytes(b"failed")
        os.link(log, self.root / "external-link")
        with self.assertRaisesRegex(subject.RetentionError, "source-file-type-or-links-rejected"):
            self.preserve()
        # A rejected copy has no summary; use a fresh fixture destination for the next condition.
        (self.parent / "attempt").rmdir()
        (self.root / "external-link").unlink()
        log.unlink()
        log.symlink_to(self.root / "elsewhere")
        (self.root / "elsewhere").write_bytes(b"unchanged")
        with self.assertRaises(subject.RetentionError):
            self.preserve()
        self.assertEqual(b"unchanged", (self.root / "elsewhere").read_bytes())

    def test_diagnostic_summary_is_not_written_when_trx_exceeds_bound(self) -> None:
        (self.source / "root-file-driver.log").write_bytes(b"failed")
        tests = self.source / "root-file-tests"
        tests.mkdir(mode=0o700)
        with (tests / "root-files.trx").open("wb") as stream:
            stream.truncate(subject.MAX_TRX + 1)
        with self.assertRaisesRegex(subject.RetentionError, "source-size-limit"):
            self.preserve()
        self.assertFalse((self.parent / "attempt/retention-summary.json").exists())


if __name__ == "__main__":
    unittest.main()
