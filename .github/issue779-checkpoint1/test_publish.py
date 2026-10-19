"""Finite publisher process/file controls, not a .NET build or native acceptance."""
from pathlib import Path
from types import SimpleNamespace
import concurrent.futures
import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("c1_publisher", Path(__file__).with_name("publish.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class PublisherControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-publisher-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.processes = []
        original = publisher.subprocess.Popen

        def capture(*args, **kwargs):
            process = original(*args, **kwargs)
            self.processes.append(process)
            return process

        p = patch.object(publisher.subprocess, "Popen", side_effect=capture)
        p.start()
        self.addCleanup(p.stop)
        self.addCleanup(self.assert_joined)

    def assert_joined(self):
        for process in self.processes:
            self.assertIsNotNone(process.poll())
            self.assertTrue(publisher.group_absent(process.pid))

    def invoke(self, code, *args, deadline=None):
        return publisher.command([sys.executable, "-c", code, *map(str, args)], "publish",
            self.root / "build.log", self.root, os.environ.copy(),
            deadline if deadline is not None else time.monotonic() + 5)

    def test_original_exit_eof_and_exact_private_log_are_recorded(self):
        row = self.invoke("import sys; print('out',flush=True); print('err',file=sys.stderr,flush=True)")
        raw = (self.root / "build.log").read_bytes()
        self.assertEqual(raw, b"out\nerr\n")
        self.assertEqual(row["exit"], 0)
        self.assertTrue(row["joined"])
        self.assertFalse(row["timed_out"])
        self.assertEqual(row["log_sha256"], hashlib.sha256(raw).hexdigest())
        self.assertEqual(row["log_bytes"], len(raw))
        self.assertEqual((self.root / "build.log").stat().st_mode & 0o777, 0o600)

    def test_nonzero_exit_is_preserved_without_publishing_a_receipt(self):
        row = self.invoke("raise SystemExit(7)")
        self.assertEqual(row["exit"], 7)
        self.assertTrue(row["joined"])
        self.assertEqual(row["log_bytes"], 0)
        self.assertFalse((self.root / "build.json").exists())

    def test_build_child_modes_do_not_change_private_parent_or_log_modes(self):
        previous = os.umask(0o077)
        try:
            row = self.invoke("from pathlib import Path; Path('image').mkdir(); "
                              "Path('image/output.dll').write_bytes(b'actual child bytes')")
            observed = os.umask(0o077)
            self.assertEqual(observed, 0o077)
        finally:
            os.umask(previous)
        self.assertEqual(row["exit"], 0)
        self.assertEqual((self.root / "image").stat().st_mode & 0o777, 0o755)
        self.assertEqual((self.root / "image/output.dll").stat().st_mode & 0o777, 0o644)
        self.assertEqual(self.root.stat().st_mode & 0o777, 0o700)
        self.assertEqual((self.root / "build.log").stat().st_mode & 0o777, 0o600)

    def test_publisher_failure_diagnostic_is_closed_and_never_echoes_raw_errors(self):
        secret = "/private-canary/publish"
        for error, category, kind, number in (
                (publisher.binding.Rejected("image-file"), "image-file", "input", None),
                (publisher.binding.Rejected(secret), "publication-input-invalid", "input", None),
                (ValueError(secret), "publication-input-invalid", "input", None),
                (PermissionError(13, secret, secret), "publication-os-error", "os", 13),
                (subprocess.CalledProcessError(7, [secret]), "publication-process-error", "process", None)):
            with self.subTest(error=type(error).__name__):
                result = publisher.failure_diagnostic(error)
                self.assertEqual(result, {
                    "schema": "issue779-checkpoint1-publisher-failure-v1",
                    "category": category, "error_kind": kind, "errno": number,
                })
                raw = json.dumps(result)
                self.assertNotIn(secret, raw)
                self.assertLess(len(raw.encode()), 512)

    def test_log_overflow_rejects_and_joins_original_process(self):
        with patch.object(publisher.binding, "MAX_LOG", 32):
            with self.assertRaisesRegex(publisher.binding.Rejected, "^build-log-limit$"):
                self.invoke("import sys; sys.stdout.write('x'*100); sys.stdout.flush()")

    def test_eof_does_not_replace_original_process_settlement(self):
        entered = threading.Event()
        expired = threading.Event()
        ready = self.root / "ready"
        fake_clock = SimpleNamespace(monotonic=lambda: 2 if expired.is_set() else 0)
        code = ("import os,pathlib,sys,time; os.close(1); os.close(2); "
                "pathlib.Path(sys.argv[1]).write_text('ready'); time.sleep(60)")
        with patch.object(publisher, "time", fake_clock):
            with concurrent.futures.ThreadPoolExecutor(max_workers=1) as executor:
                future = executor.submit(self.invoke, code, ready, deadline=1)
                try:
                    end = time.monotonic() + 3
                    while not ready.exists() and time.monotonic() < end:
                        entered.wait(0.01)
                    self.assertTrue(ready.exists(), "The owned child must close both streams before expiry.")
                finally:
                    expired.set()
                with self.assertRaisesRegex(publisher.binding.Rejected, "^build-command-deadline$"):
                    future.result(timeout=3)

    def arguments(self):
        source, runtime, evidence, sdk = [self.root / name for name in ("source", "runtime", "evidence", "sdk")]
        for p in (source, runtime, evidence, sdk):
            p.mkdir(mode=0o700)
        return SimpleNamespace(source_root=source, runtime=runtime, build_sdk=sdk, tool=self.root / "tool",
            output=evidence / "build.json", source_capture=evidence / "source.json",
            source="a" * 40, selector="None")

    def test_invalid_selector_rejects_before_capture_or_command(self):
        args = self.arguments()
        args.selector = "N99"
        with patch.object(publisher.binding, "read") as read:
            with self.assertRaisesRegex(publisher.binding.Rejected, "^publish-fresh-output$"):
                publisher.publish(args)
        read.assert_not_called()
        self.assertEqual(self.processes, [])

    def test_nonprivate_input_parent_rejects_before_capture_or_command(self):
        args = self.arguments()
        parent = self.root / "public-input-parent"
        parent.mkdir(mode=0o755)
        parent.chmod(0o755)
        args.tool = parent / "tool"
        with patch.object(publisher.binding, "read") as read:
            with self.assertRaisesRegex(publisher.binding.Rejected, "^publisher-input-directory$"):
                publisher.publish(args)
        read.assert_not_called()
        self.assertEqual(self.processes, [])
        self.assertFalse(args.tool.exists())

    def test_source_output_overlap_rejects_before_capture_or_command(self):
        args = self.arguments()
        args.tool = args.source_root / "fresh-tool"
        with patch.object(publisher.binding, "read") as read:
            with self.assertRaisesRegex(publisher.binding.Rejected, "^publisher-root-overlap$"):
                publisher.publish(args)
        read.assert_not_called()
        self.assertEqual(self.processes, [])

    def test_occupied_receipt_preserves_original_bytes_without_dispatch(self):
        args = self.arguments()
        args.output.write_bytes(b"owned sentinel")
        with self.assertRaisesRegex(publisher.binding.Rejected, "^publish-fresh-output$"):
            publisher.publish(args)
        self.assertEqual(args.output.read_bytes(), b"owned sentinel")
        self.assertEqual(self.processes, [])


if __name__ == "__main__":
    unittest.main()
