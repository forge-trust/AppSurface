"""Actual private-file retention of modeled audit errors; no root/native outcome."""
import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


driver = load("c1_dependency_retention_driver", ROOT / "run-native.py")
audit = load("c1_dependency_retention_audit", ROOT / "procedures/os_audit.py")


class DependencyRetentionControls(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="c1-dependency-context-")
        self.addCleanup(temporary.cleanup)
        self.output = Path(temporary.name).resolve()
        self.output.chmod(0o700)
        self.target = self.output / "os-audit-dependency.json"

    def original_error(self):
        owner = audit.Audit(time.monotonic() + 10)
        owner.commands["readelf"] = "/usr/bin/readelf"
        owner.cache["libfixture.so.1"] = ["/lib/private-canary/libfixture.so.1",
                                         "/usr/lib/private-canary/libfixture.so.1"]
        prefix = bytearray(64)
        prefix[:7] = b"\x7fELF\x02\x01\x01"
        prefix[18:20] = (62).to_bytes(2, "little")
        def readelf(argv):
            return {"-h": "ELF64 Advanced Micro Devices X86-64",
                    "-d": "(NEEDED) Shared library: [libfixture.so.1]", "-l": ""}[argv[1]]
        try:
            with patch.object(owner, "pin_os", return_value=("a" * 64, bytes(prefix))), \
                 patch.object(owner, "run", side_effect=readelf):
                owner.inspect_elf("/private-canary/requester")
        except audit.AuditError as error:
            self.assertEqual("dependency-unresolved-or-ambiguous", str(error))
            self.assertEqual(owner.deadline, error.failure_deadline)
            return error
        self.fail("Ambiguous modeled dependencies unexpectedly resolved")

    def test_original_context_bytes_are_retained_exclusively_private(self):
        error = self.original_error()
        raw = error.failure_context
        self.assertTrue(driver.retain_dependency_failure(error, self.output))
        self.assertEqual(raw, self.target.read_bytes())
        self.assertEqual((0o600, 1), (self.target.stat().st_mode & 0o777, self.target.stat().st_nlink))
        self.assertEqual(0o700, self.output.stat().st_mode & 0o777)
        self.assertLessEqual(len(raw), 32768)
        before = self.target.read_bytes(), self.target.stat().st_ino
        self.assertFalse(driver.retain_dependency_failure(error, self.output))
        self.assertEqual(before, (self.target.read_bytes(), self.target.stat().st_ino))

    def test_invalid_or_foreign_context_rejects_before_open(self):
        original = self.original_error()
        value = json.loads(original.failure_context)
        bad = dict(value, candidate_count=True)
        samples = [b"", b"x" * 32769, b'{"schema":"x","schema":"x"}',
                   json.dumps(dict(value, extra="private-canary")).encode(), json.dumps(bad).encode()]
        for raw in samples:
            original.failure_context = raw
            with self.subTest(size=len(raw)), patch.object(driver.os, "open") as opened:
                self.assertFalse(driver.retain_dependency_failure(original, self.output))
                opened.assert_not_called()
        foreign = audit.AuditError("dependency-unresolved-or-ambiguous",
                                  failure_context=json.dumps(value).encode(), failure_deadline=time.monotonic() + 10)
        with patch.object(driver.os, "open") as opened:
            self.assertFalse(driver.retain_dependency_failure(foreign, self.output))
            opened.assert_not_called()

    def test_expiry_and_nonprivate_parent_reject_before_write(self):
        error = self.original_error()
        error.failure_deadline = time.monotonic() - 1
        with patch.object(driver.os, "open") as opened:
            self.assertFalse(driver.retain_dependency_failure(error, self.output))
            opened.assert_not_called()
        error = self.original_error()
        self.output.chmod(0o755)
        with patch.object(driver.os, "open") as opened:
            self.assertFalse(driver.retain_dependency_failure(error, self.output))
            opened.assert_not_called()
        self.assertFalse(self.target.exists())

    def test_symlink_target_and_its_outside_file_are_unchanged(self):
        outside = self.output / "outside"
        outside.write_bytes(b"outside private canary")
        outside.chmod(0o600)
        self.target.symlink_to(outside)
        before = outside.read_bytes(), outside.stat().st_ino, outside.stat().st_mode
        self.assertFalse(driver.retain_dependency_failure(self.original_error(), self.output))
        self.assertTrue(self.target.is_symlink())
        self.assertEqual(before, (outside.read_bytes(), outside.stat().st_ino, outside.stat().st_mode))

    def test_write_failure_closes_all_actual_fds_without_masking_original_error(self):
        error = self.original_error()
        opened = []
        real_open = os.open
        def capture(*args, **kwargs):
            fd = real_open(*args, **kwargs)
            opened.append(fd)
            return fd
        with patch.object(driver.os, "open", side_effect=capture), patch.object(
                driver.os, "write", side_effect=OSError("private-canary")):
            self.assertFalse(driver.retain_dependency_failure(error, self.output))
        self.assertEqual(2, len(opened))
        for fd in opened:
            with self.assertRaises(OSError):
                os.fstat(fd)
        self.assertEqual("dependency-unresolved-or-ambiguous", str(error))
        self.assertEqual(0o600, self.target.stat().st_mode & 0o777)

    def test_main_keeps_original_failure_and_never_echoes_context(self):
        error = self.original_error()
        def fail(_):
            raise error
        args = SimpleNamespace(action=fail, command="run", output=self.output)
        parser = SimpleNamespace(parse_args=lambda _: args)
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(driver, "parser", return_value=parser), \
             contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = driver.main([])
        self.assertEqual(1, code)
        self.assertEqual("", stdout.getvalue())
        self.assertNotIn("private-canary", stderr.getvalue())
        value = json.loads(stderr.getvalue())
        self.assertEqual("audit-dependency-unresolved-or-ambiguous", value["category"])
        self.assertFalse(value["native_acceptance"])
        self.assertFalse(value["coverage_credit"])
        self.assertEqual(error.failure_context, self.target.read_bytes())

    def test_expiry_during_final_close_cannot_report_completed_retention(self):
        error = self.original_error()
        end = error.failure_deadline
        closed = []
        real_close = os.close
        def close(fd):
            real_close(fd)
            closed.append(fd)
        with patch.object(driver.os, "close", side_effect=close), patch.object(
                driver.binding.time, "monotonic", side_effect=lambda: end + 1 if len(closed) == 2 else end - 1):
            self.assertFalse(driver.retain_dependency_failure(error, self.output))
        self.assertEqual(2, len(closed))
        for fd in closed:
            with self.assertRaises(OSError):
                os.fstat(fd)
        self.assertEqual(error.failure_context, self.target.read_bytes())


if __name__ == "__main__":
    unittest.main()
