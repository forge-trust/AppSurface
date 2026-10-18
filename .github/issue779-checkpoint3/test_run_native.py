"""Portable fixture ordering only: pipe FDs and mocked signaling issue no native ownership."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("checkpoint3_runner", Path(__file__).with_name("run-native.py"))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)
SAMPLE = {"pid": 123, "uid": 456, "gid": 789, "starttime": 42,
          "cgroup": "/system.slice/app.service", "threads": 1}


@contextlib.contextmanager
def fixture():
    read_fd, write_fd = os.pipe()
    try:
        with patch.object(runner.os, "pidfd_open", return_value=read_fd, create=True) as opened, \
             patch.object(runner.signal, "pidfd_send_signal", create=True) as sent, \
             patch.object(runner, "process_sample", return_value=dict(SAMPLE)) as sampled:
            yield read_fd, write_fd, opened, sent, sampled
    finally:
        for fd in (read_fd, write_fd):
            try:
                os.close(fd)
            except OSError:
                pass


class DescendantControlTests(unittest.TestCase):
    def test_term_then_exact_marker_and_live_identity_precede_health_release(self):
        with fixture() as (fd, _, opened, sent, sampled), tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            order = []
            opened.side_effect = lambda *args: order.append("open") or fd
            sampled.side_effect = lambda *args: order.append("sample") or dict(SAMPLE)
            sent.side_effect = lambda *args: order.append("signal")
            control = runner.DescendantControl(123, 456, 789, "app.service")
            try:
                opened.assert_called_once_with(123, 0)
                sent.assert_called_once_with(fd, signal.SIGTERM, None, 0)
                self.assertEqual(["open", "sample", "sample", "signal"], order)
                self.assertFalse(control.confirm(scratch))
                self.assertFalse((scratch / "descendant-verified").exists())
                (scratch / "descendant-term").write_bytes(b"")
                self.assertFalse(control.confirm(scratch))
                self.assertFalse((scratch / "descendant-verified").exists())
                (scratch / "descendant-term").write_bytes(b"SIGTERM handler ran\n")
                sampled.side_effect = None
                sampled.return_value = dict(SAMPLE, threads=2)
                self.assertTrue(control.confirm(scratch))
                self.assertTrue(control.term_seen)
                self.assertEqual(b"kernel-identity-and-term-checked\n",
                                 (scratch / "descendant-verified").read_bytes())
                self.assertTrue(control.confirm(scratch))
                sent.assert_called_once()
            finally:
                control.close()

    def test_each_identity_drift_rejects_before_signal_and_closes_original_fd(self):
        for key, value in (("pid", 124), ("uid", 457), ("gid", 790),
                           ("starttime", 43), ("cgroup", "/system.slice/other.service")):
            with self.subTest(field=key), fixture() as (fd, _, _, sent, sampled):
                sampled.side_effect = [dict(SAMPLE), dict(SAMPLE, **{key: value})]
                with self.assertRaises(RuntimeError):
                    runner.DescendantControl(123, 456, 789, "app.service")
                sent.assert_not_called()
                with self.assertRaises(OSError):
                    os.fstat(fd)

    def test_exit_notification_rejects_before_proc_read_or_signal(self):
        with fixture() as (fd, write_fd, _, sent, sampled):
            os.write(write_fd, b"exit-notification")
            with self.assertRaises(RuntimeError):
                runner.DescendantControl(123, 456, 789, "app.service")
            sampled.assert_not_called()
            sent.assert_not_called()
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_signal_failure_closes_original_fd(self):
        with fixture() as (fd, _, _, sent, _):
            sent.side_effect = ProcessLookupError()
            with self.assertRaises(ProcessLookupError):
                runner.DescendantControl(123, 456, 789, "app.service")
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_exit_during_initial_sample_rejects_before_signal(self):
        with fixture() as (fd, write_fd, _, sent, sampled):
            def sample_after_exit(*args):
                os.write(write_fd, b"pinned-process-exited")
                return dict(SAMPLE)
            sampled.side_effect = sample_after_exit
            with self.assertRaises(RuntimeError):
                runner.DescendantControl(123, 456, 789, "app.service")
            sampled.assert_called_once()
            sent.assert_not_called()
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_wrong_handler_bytes_cannot_release_health(self):
        with fixture(), tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            control = runner.DescendantControl(123, 456, 789, "app.service")
            try:
                for payload in (b"wrong-handler-canary", b"x" * 65):
                    (scratch / "descendant-term").write_bytes(payload)
                    self.assertFalse(control.confirm(scratch))
                    self.assertFalse(control.term_seen)
                    self.assertFalse((scratch / "descendant-verified").exists())
            finally:
                control.close()

    def test_pid_reuse_after_term_cannot_release_health(self):
        with fixture() as (_, _, _, sent, sampled), tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            control = runner.DescendantControl(123, 456, 789, "app.service")
            try:
                (scratch / "descendant-term").write_bytes(b"SIGTERM handler ran\n")
                sampled.return_value = dict(SAMPLE, starttime=99)
                with self.assertRaises(RuntimeError):
                    control.confirm(scratch)
                self.assertFalse(control.term_seen)
                self.assertFalse((scratch / "descendant-verified").exists())
                sent.assert_called_once()
            finally:
                control.close()

    def test_closed_handle_cannot_confirm_and_close_is_idempotent(self):
        with fixture(), tempfile.TemporaryDirectory() as directory:
            control = runner.DescendantControl(123, 456, 789, "app.service")
            control.close()
            control.close()
            with self.assertRaises(RuntimeError):
                control.confirm(Path(directory))


class DescriptorPublicationTests(unittest.TestCase):
    def test_absent_empty_and_partial_writable_publications_remain_pending_until_sealed(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "worker-control.json"
            self.assertIsNone(runner.sealed_descriptor(path))
            path.write_bytes(b"")
            path.chmod(0o640)
            self.assertIsNone(runner.sealed_descriptor(path))
            path.write_bytes(b'{"schema":')
            self.assertIsNone(runner.sealed_descriptor(path))
            path.write_bytes(b'{"schema":"evidence-worker-linux-v2","worker_pid":123}')
            self.assertIsNone(runner.sealed_descriptor(path))
            path.chmod(0o440)
            self.assertEqual({"schema": "evidence-worker-linux-v2", "worker_pid": 123},
                             runner.sealed_descriptor(path))

    def test_sealed_malformed_empty_wrong_schema_and_oversized_data_never_pass(self):
        for data in (b"", b'{"schema":', b'{"schema":"wrong"}', b"x" * 65537):
            with self.subTest(data_length=len(data)), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "worker-control.json"
                path.write_bytes(data)
                path.chmod(0o440)
                with self.assertRaises((RuntimeError, ValueError)):
                    runner.sealed_descriptor(path)

    def test_links_and_public_modes_cannot_be_a_sealed_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "worker-control.json"
            outside = Path(directory) / "outside"
            outside.write_bytes(b'{"schema":"evidence-worker-linux-v2"}')
            outside.chmod(0o440)
            path.symlink_to(outside)
            with self.assertRaises(OSError):
                runner.sealed_descriptor(path)
            path.unlink()
            os.link(outside, path)
            with self.assertRaises(RuntimeError):
                runner.sealed_descriptor(path)
            path.unlink()
            path.write_bytes(outside.read_bytes())
            path.chmod(0o444)
            with self.assertRaises(RuntimeError):
                runner.sealed_descriptor(path)
            self.assertEqual(b'{"schema":"evidence-worker-linux-v2"}', outside.read_bytes())

    def test_changed_retained_metadata_cannot_supply_a_descriptor(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "worker-control.json"
            path.write_bytes(b'{"schema":"evidence-worker-linux-v2"}')
            path.chmod(0o440)
            actual_read = runner.os.read
            def mutate(fd, count):
                data = actual_read(fd, count)
                path.chmod(0o640)
                return data
            with patch.object(runner.os, "read", side_effect=mutate):
                with self.assertRaises(RuntimeError):
                    runner.sealed_descriptor(path)


if __name__ == "__main__":
    unittest.main()
