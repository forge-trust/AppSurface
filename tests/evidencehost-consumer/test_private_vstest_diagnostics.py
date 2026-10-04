"""Real-FD portable diagnostic data controls; no root/exit/admission claim."""

import errno
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import time
import unittest
from unittest import mock


SOURCE = Path(__file__).resolve().parents[2] / "scripts/evidencehost_private_vstest_diagnostics.py"
SPEC = importlib.util.spec_from_file_location("private_vstest_diagnostic_data", SOURCE)
diagnostics = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(diagnostics)
BASE = "results-0123-qualification-vstest.log"
COLLECTOR = "results-0123-qualification-vstest.datacollector.26-10-04_06-10-11_12345_7.log"
HOST = "results-0123-qualification-vstest.host.26-10-04_06-10-11_54321_8.log"


class TraceDataControls(unittest.TestCase):
    """Owner override labels current-user files, never authenticates root or a job."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.results = self.root / "results"
        self.token = self.results / "results-0123"
        self.destination = self.root / "private"
        self.results.mkdir()
        self.token.mkdir()
        self.destination.mkdir()
        os.chmod(self.results, 0o2770)
        os.chmod(self.token, 0o2770)
        os.chmod(self.destination, 0o700)
        self.parent_fd = os.open(self.results, os.O_RDONLY | os.O_DIRECTORY)
        self.destination_fd = os.open(self.destination, os.O_RDONLY | os.O_DIRECTORY)
        self.addCleanup(os.close, self.parent_fd)
        self.addCleanup(os.close, self.destination_fd)

    def write(self, name, data=b"private-canary\x00\xff"):
        path = self.results / name
        path.write_bytes(data)
        os.chmod(path, 0o600)
        return path

    def capture(self, **kwargs):
        args = dict(deadline=time.monotonic() + 5, expected_root_uid=os.geteuid())
        args.update(kwargs)
        return diagnostics.capture_traces(
            self.parent_fd, (self.token.name,), self.destination_fd,
            os.geteuid(), os.getegid(), **args)

    def assert_no_capture(self):
        self.assertFalse((self.destination / diagnostics.DESTINATION_NAME).exists())
        os.fstat(self.parent_fd)
        os.fstat(self.destination_fd)

    def test_binary_empty_and_missing_role_are_retained_with_closed_index(self):
        binary = b"\x00\xffprivate-canary\r\n"
        self.write(BASE, binary)
        self.write(COLLECTOR, b"")
        self.write("coverage.cobertura.xml", b"unrelated")
        self.write(COLLECTOR.replace(".log", ".bak.log"), b"excluded-rolling-canary")
        self.write("results-0123-qualification-vstest.host.unrecognized-guid.log", b"excluded")
        (self.token / "coverage.cobertura.xml").write_bytes(b"report-stays-in-token")
        self.assertTrue(self.capture())
        target = self.destination / diagnostics.DESTINATION_NAME
        self.assertEqual(stat.S_IMODE(target.stat().st_mode), 0o700)
        self.assertEqual(set(p.name for p in target.iterdir()),
                         {"runner.log", "collector.log", "host.log", "index.json"})
        self.assertEqual((target / "runner.log").read_bytes(), binary)
        self.assertEqual((target / "collector.log").read_bytes(), b"")
        self.assertEqual((target / "host.log").read_bytes(), b"")
        index = json.loads((target / "index.json").read_bytes())
        self.assertEqual(set(index), {"roles"})
        self.assertEqual([r["role"] for r in index["roles"]], list(diagnostics.ROLES))
        self.assertEqual(index["roles"][0]["sha256"], hashlib.sha256(binary).hexdigest())
        self.assertEqual(index["roles"][1]["length"], 0)
        self.assertIsNone(index["roles"][2]["length"])
        self.assertNotIn("private-canary", (target / "index.json").read_text())
        for path in target.iterdir():
            info = path.stat()
            self.assertEqual(stat.S_IMODE(info.st_mode), 0o600)
            self.assertEqual((info.st_uid, info.st_gid, info.st_nlink),
                             (os.geteuid(), os.getegid(), 1))
        os.fstat(self.parent_fd)
        os.fstat(self.destination_fd)
        self.assertEqual((self.token / "coverage.cobertura.xml").read_bytes(), b"report-stays-in-token")

    def test_exact_limit_and_oversized_prefix_suffix_are_bounded(self):
        lengths = (diagnostics.MAX_ROLE_BYTES, diagnostics.MAX_ROLE_BYTES + 1,
                   3 * diagnostics.MAX_ROLE_BYTES + 17)
        for length in lengths:
            with self.subTest(length=length):
                source = b"A" * diagnostics.WINDOW_BYTES + b"B" * (length - diagnostics.WINDOW_BYTES)
                self.write(BASE, source)
                with mock.patch.object(diagnostics.os, "pread", wraps=os.pread) as reads:
                    self.assertTrue(self.capture())
                target = self.destination / diagnostics.DESTINATION_NAME
                expected = source if length <= diagnostics.MAX_ROLE_BYTES else (
                    source[:diagnostics.WINDOW_BYTES] + source[-diagnostics.WINDOW_BYTES:])
                self.assertEqual((target / "runner.log").read_bytes(), expected)
                record = json.loads((target / "index.json").read_bytes())["roles"][0]
                self.assertEqual(record["length"], length)
                self.assertEqual(record["retained_length"], diagnostics.MAX_ROLE_BYTES)
                self.assertEqual(record["truncated"], length > diagnostics.MAX_ROLE_BYTES)
                self.assertTrue(all(call.args[1] <= diagnostics.WINDOW_BYTES for call in reads.call_args_list))
                self.assertLessEqual(sum(p.stat().st_size for p in target.iterdir()), diagnostics.MAX_TOTAL_BYTES)
                for path in target.iterdir():
                    path.unlink()
                target.rmdir()

    def test_pinned_sdk_companions_and_ambiguous_role_rejection(self):
        self.write(BASE)
        self.write(COLLECTOR)
        self.write(HOST)
        self.assertTrue(self.capture())
        target = self.destination / diagnostics.DESTINATION_NAME
        for path in target.iterdir():
            path.unlink()
        target.rmdir()
        self.write(COLLECTOR.replace("_7.log", "_9.log"))
        with mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
            self.assertFalse(self.capture())
        self.assertEqual(creates.call_count, 0)
        self.assert_no_capture()

    def test_unknown_and_invalid_diagnostic_names_fail_before_destination_io(self):
        names = ("results-0123-qualification-vstest.log.datacollector.26-10-04_06-10-11_12345_7.log",
                 COLLECTOR.replace("_12345_", "_123_"), COLLECTOR.replace("26-10-04", "26-99-04"),
                 COLLECTOR.replace("_7.log", "_0.log"), COLLECTOR.replace("_7.log", "_2147483648.log"),
                 "results-0123-qualification-vstest.unknown.log", "results-0123-qualification-vstest.host.raw.log")
        for name in names:
            with self.subTest(name=name):
                path = self.write(name)
                with mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
                    self.assertFalse(self.capture())
                self.assertEqual(creates.call_count, 0)
                path.unlink()
                self.assert_no_capture()

    def test_link_fifo_and_permission_rejections_use_actual_files(self):
        source = self.write(BASE)
        for mode in (0o666, 0o700, 0o000, 0o4700):
            with self.subTest(mode=mode):
                os.chmod(source, mode)
                self.assertNotIn(stat.S_IMODE(source.stat().st_mode), (0o400, 0o440, 0o444, 0o600, 0o640, 0o644))
                self.assertFalse(self.capture())
                self.assert_no_capture()
        os.chmod(source, 0o600)
        alias = self.root / "hardlink"
        os.link(source, alias)
        self.assertFalse(self.capture())
        alias.unlink()
        source.unlink()
        source.symlink_to(self.root / "missing")
        self.assertFalse(self.capture())
        source.unlink()
        os.mkfifo(source, 0o600)
        self.assertFalse(self.capture())
        self.assert_no_capture()

    def test_owner_gid_and_token_modes_reject_without_capture(self):
        self.write(BASE)
        self.assertFalse(diagnostics.capture_traces(
            self.parent_fd, (self.token.name,), self.destination_fd,
            os.geteuid() + 1, os.getegid(), deadline=time.monotonic() + 5,
            expected_root_uid=os.geteuid()))
        self.assertFalse(diagnostics.capture_traces(
            self.parent_fd, (self.token.name,), self.destination_fd,
            os.geteuid(), os.getegid() + 1, deadline=time.monotonic() + 5,
            expected_root_uid=os.geteuid()))
        for path in (self.results, self.token):
            os.chmod(path, 0o770)
            self.assertFalse(self.capture())
            os.chmod(path, 0o2770)
        os.chmod(self.destination, 0o777)
        self.assertFalse(self.capture())
        os.chmod(self.destination, 0o700)
        self.assert_no_capture()

    def test_one_recorded_token_and_missing_trace_constraints(self):
        self.assertFalse(self.capture())
        binary = b"otherwise-valid-token-canary\x00\xff"
        self.write(BASE, binary)
        positive = self.root / "token-positive"
        positive.mkdir(mode=0o700)
        positive_fd = os.open(positive, os.O_RDONLY | os.O_DIRECTORY)
        try:
            self.assertTrue(diagnostics.capture_traces(
                self.parent_fd, (self.token.name,), positive_fd, os.geteuid(), os.getegid(),
                deadline=time.monotonic() + 5, expected_root_uid=os.geteuid()))
            self.assertEqual((positive / diagnostics.DESTINATION_NAME / "runner.log").read_bytes(), binary)
            os.fstat(positive_fd)
        finally:
            os.close(positive_fd)
        for index, tokens in enumerate(((), (self.token.name, self.token.name), ("../results-0123",),
                                        ("/tmp",), "results-0123", (None,))):
            with self.subTest(tokens=tokens):
                fresh = self.root / ("token-rejection-" + str(index))
                fresh.mkdir(mode=0o700)
                fresh_fd = os.open(fresh, os.O_RDONLY | os.O_DIRECTORY)
                try:
                    with mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
                        self.assertFalse(diagnostics.capture_traces(
                            self.parent_fd, tokens, fresh_fd, os.geteuid(), os.getegid(),
                            deadline=time.monotonic() + 5, expected_root_uid=os.geteuid()))
                    self.assertEqual(creates.call_count, 0)
                    self.assertFalse((fresh / diagnostics.DESTINATION_NAME).exists())
                    os.fstat(fresh_fd)
                finally:
                    os.close(fresh_fd)
        self.assertEqual((self.results / BASE).read_bytes(), binary)
        self.assert_no_capture()

    def test_last_owned_close_crossing_original_deadline_returns_false(self):
        binary = b"stable-source-during-close\x00\xff"
        source = self.write(BASE, binary)
        source_stat = source.stat()
        sampled = time.monotonic()
        real_open, real_close = os.open, os.close
        opened, closed = [], []
        last_owned = None
        expired = False

        def record_open(name, flags, *args, **kwargs):
            nonlocal last_owned
            fd = real_open(name, flags, *args, **kwargs)
            opened.append(fd)
            if name == "." and kwargs.get("dir_fd") == self.parent_fd:
                last_owned = fd
            return fd

        def expire_on_last_close(fd):
            nonlocal expired
            real_close(fd)
            closed.append(fd)
            if fd == last_owned:
                expired = True

        with mock.patch.object(diagnostics.time, "monotonic", side_effect=lambda: sampled + (6 if expired else 0)), \
                mock.patch.object(diagnostics.os, "open", side_effect=record_open), \
                mock.patch.object(diagnostics.os, "close", side_effect=expire_on_last_close):
            self.assertFalse(self.capture(deadline=sampled + 100))
        self.assertTrue(expired)
        self.assertEqual(list(reversed(opened)), closed)
        self.assertEqual(len(set(closed)), len(closed))
        self.assertNotIn(self.parent_fd, closed)
        self.assertNotIn(self.destination_fd, closed)
        os.fstat(self.parent_fd)
        os.fstat(self.destination_fd)
        self.assertEqual(source.read_bytes(), binary)
        final = source.stat()
        self.assertEqual((source_stat.st_dev, source_stat.st_ino, source_stat.st_mode, source_stat.st_size,
                          source_stat.st_mtime_ns, source_stat.st_ctime_ns),
                         (final.st_dev, final.st_ino, final.st_mode, final.st_size,
                          final.st_mtime_ns, final.st_ctime_ns))
        # A complete private copy may remain; False grants no success authority.
        self.assertTrue((self.destination / diagnostics.DESTINATION_NAME).is_dir())

    def test_bounded_listing_stops_on_first_excess_entry_before_output(self):
        self.write(BASE)
        for number in range(diagnostics.MAX_ENTRIES):
            self.write("unrelated-" + str(number), b"")
        with mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
            self.assertFalse(self.capture())
        self.assertEqual(creates.call_count, 0)
        self.assert_no_capture()

    def test_file_and_token_substitution_are_rejected_after_retained_read(self):
        source = self.write(BASE)
        real_read = os.pread
        for substitute_token in (False, True):
            changed = False
            def replace(fd, size, offset):
                nonlocal changed
                data = real_read(fd, size, offset)
                if data and not changed:
                    changed = True
                    if substitute_token:
                        old = self.results / "old-token"
                        self.token.rename(old)
                        self.token.mkdir()
                        os.chmod(self.token, 0o2770)
                    else:
                        old = self.results / "old-file"
                        source.rename(old)
                        self.write(BASE)
                return data
            with self.subTest(token=substitute_token), mock.patch.object(diagnostics.os, "pread", side_effect=replace):
                self.assertFalse(self.capture())
            self.assertTrue(changed)
            self.assert_no_capture()
            if not substitute_token:
                (self.results / "old-file").unlink()

    def test_growth_and_short_read_fail_with_borrowed_descriptors_alive(self):
        source = self.write(BASE)
        real_read = os.pread
        changed = False
        def grow(fd, size, offset):
            nonlocal changed
            data = real_read(fd, size, offset)
            if data and not changed:
                changed = True
                with source.open("ab") as stream:
                    stream.write(b"growth-canary")
            return data
        with mock.patch.object(diagnostics.os, "pread", side_effect=grow):
            self.assertFalse(self.capture())
        self.assertTrue(changed)
        with mock.patch.object(diagnostics.os, "pread", return_value=b""):
            self.assertFalse(self.capture())
        self.assert_no_capture()

    def test_read_errors_and_expired_deadline_do_not_echo_or_create(self):
        self.write(BASE)
        with mock.patch.object(diagnostics.os, "pread", side_effect=OSError(errno.EIO, "private-canary")), \
                mock.patch("builtins.print") as echo:
            self.assertFalse(self.capture())
        echo.assert_not_called()
        with mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
            self.assertFalse(self.capture(deadline=time.monotonic() - 1))
            self.assertFalse(self.capture(deadline=float("nan")))
        self.assertEqual(creates.call_count, 0)
        self.assert_no_capture()
        sampled = time.monotonic()
        expired = False
        real_read = os.pread
        def expire_after_read(fd, size, offset):
            nonlocal expired
            result = real_read(fd, size, offset)
            expired = True
            return result
        with mock.patch.object(diagnostics.time, "monotonic", side_effect=lambda: sampled + (6 if expired else 0)), \
                mock.patch.object(diagnostics.os, "pread", side_effect=expire_after_read), \
                mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
            self.assertFalse(self.capture(deadline=sampled + 100))
        self.assertTrue(expired)
        self.assertEqual(creates.call_count, 0)
        self.assert_no_capture()

    def test_one_bad_file_identity_field_rejects_with_other_inputs_valid(self):
        """Observed stat projection is invalid data, not a root ownership grant."""
        from types import SimpleNamespace
        source = self.write(BASE)
        real_stat = os.stat
        for field in ("st_uid", "st_gid"):
            def bad_file_stat(name, *args, **kwargs):
                observed = real_stat(name, *args, **kwargs)
                if name == source.name and kwargs.get("dir_fd") is not None:
                    row = {key: getattr(observed, key) for key in (
                        "st_dev", "st_ino", "st_mode", "st_uid", "st_gid", "st_nlink",
                        "st_size", "st_mtime_ns", "st_ctime_ns")}
                    row[field] += 1
                    return SimpleNamespace(**row)
                return observed
            with self.subTest(field=field), \
                    mock.patch.object(diagnostics.os, "stat", side_effect=bad_file_stat), \
                    mock.patch.object(diagnostics.os, "mkdir", wraps=os.mkdir) as creates:
                self.assertFalse(self.capture())
            self.assertEqual(creates.call_count, 0)
            self.assert_no_capture()

    def test_destination_is_exclusive_and_write_failure_does_not_replace_existing_data(self):
        self.write(BASE)
        target = self.destination / diagnostics.DESTINATION_NAME
        target.mkdir(mode=0o700)
        canary = target / "runner.log"
        canary.write_bytes(b"existing-private-canary")
        self.assertFalse(self.capture())
        self.assertEqual(canary.read_bytes(), b"existing-private-canary")
        canary.unlink()
        target.rmdir()
        with mock.patch.object(diagnostics.os, "write", side_effect=OSError(errno.EIO, "private-canary")):
            self.assertFalse(self.capture())
        self.assert_no_capture()


if __name__ == "__main__":
    unittest.main()
