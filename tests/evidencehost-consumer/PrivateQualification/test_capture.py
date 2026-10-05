"""New ordinary process controls; none is executed or credited by source reconstruction."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
SPEC = importlib.util.spec_from_file_location("qualification_recovered_capture", HERE / "prepare.py")
prepare = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(prepare)


class PreparationCaptureControls(unittest.TestCase):
    def run_child(self, script, cap, *, input_bytes=None):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+5)
            result = runner.run([sys.executable, "-c", script], root, capture=True,
                                capture_limit=cap, input_bytes=input_bytes)
            self.assertEqual(1, len(runner.results))
            self.assertEqual(0, runner.results[0]["exit_code"])
            self.assertTrue(runner.results[0]["owned_group_empty"])
            self.assertFalse(runner.results[0]["cleanup_failed"])
            self.assertEqual(len(result), runner.results[0]["captured_bytes"])
            return result

    def test_actual_multimegabyte_stdout_fits_its_exact_selected_cap(self):
        expected = b"x"*(2*1024*1024+1)
        result = self.run_child("import sys;sys.stdout.buffer.write(b'x'*(2*1024*1024+1))", len(expected))
        self.assertEqual(expected, result)

    def test_one_byte_over_cap_rejects_and_joins_the_real_child(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+5)
            with self.assertRaises(prepare.PreparationFailure):
                runner.run([sys.executable, "-c", "import sys;sys.stdout.buffer.write(b'x'*129)"],
                           root, capture=True, capture_limit=128)
            self.assertEqual(1, len(runner.results))
            self.assertTrue(runner.results[0]["owned_group_empty"])
            self.assertFalse(runner.results[0]["cleanup_failed"])
            self.assertIsNotNone(runner.results[0]["failure_category"])
            self.assertIsNone(runner.results[0]["captured_bytes"])

    def test_large_stdin_finishes_then_child_observes_eof(self):
        expected = b"owned-input"*10000
        self.assertEqual(expected, self.run_child(
            "import sys;sys.stdout.buffer.write(sys.stdin.buffer.read())", len(expected), input_bytes=expected))

    def test_empty_stdout_eof_with_zero_exit_is_valid(self):
        self.assertEqual(b"", self.run_child("pass", 1))

    def test_closed_stdout_does_not_hide_live_child_deadline(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+.5)
            with self.assertRaises(prepare.PreparationFailure):
                runner.run([sys.executable, "-c", "import os,time;os.close(1);time.sleep(60)"],
                           root, capture=True, capture_limit=1)
            row = runner.results[0]
            self.assertTrue(row["timed_out"])
            self.assertEqual(124, row["exit_code"])
            self.assertTrue(row["owned_group_empty"])
            self.assertFalse(row["cleanup_failed"])

    def test_invalid_caps_and_inputs_reject_without_dispatch(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+5)
            with patch.object(prepare.subprocess, "Popen") as spawned:
                for cap in (0, -1, True, "1", None, 32*1024*1024+2):
                    with self.subTest(cap=cap), self.assertRaises(prepare.PreparationFailure):
                        runner.run([sys.executable, "-c", "pass"], root, capture=True, capture_limit=cap)
                for value in ("text", bytearray(b"x"), b"x"*(1024*1024+1)):
                    with self.subTest(kind=type(value).__name__), self.assertRaises(prepare.PreparationFailure):
                        runner.run([sys.executable, "-c", "pass"], root, capture=True, input_bytes=value)
                spawned.assert_not_called()
            self.assertEqual([], runner.results)

    def test_real_stdout_close_then_error_cannot_publish_captured_bytes(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+5)
            real_popen = subprocess.Popen
            closed = []
            def spawn(*args, **kwargs):
                child = real_popen(*args, **kwargs)
                real_close = child.stdout.close
                def close():
                    child.stdout.close = real_close
                    real_close()
                    closed.append(child.stdout.closed)
                    raise OSError("private-close-canary")
                child.stdout.close = close
                return child
            with patch.object(prepare.subprocess, "Popen", side_effect=spawn):
                with self.assertRaises(prepare.PreparationFailure):
                    runner.run([sys.executable, "-c", "print('metadata')"], root,
                               capture=True, capture_limit=128)
            self.assertEqual([True], closed)
            row = runner.results[0]
            self.assertEqual(0, row["exit_code"])
            self.assertTrue(row["owned_group_empty"])
            self.assertTrue(row["cleanup_failed"])
            self.assertEqual("cleanup-failed", row["failure_category"])
            self.assertNotIn("private-close-canary", (root / "command-01.json").read_text())

    def test_cleanup_late_clock_keeps_original_command_end_and_rejects_output(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root, clock, ends = Path(directory), [100.0], []
            runner = prepare.Runner(root, 101.0)
            real_capture, real_join = prepare.Runner._capture, prepare.join_process_group
            def capture(child, data, deadline, cap):
                self.assertEqual(100.5, deadline)
                return real_capture(child, data, deadline, cap)
            def join(child, deadline):
                ends.append(deadline)
                self.assertTrue(real_join(child, deadline))
                clock[0] = 101.0
                return True
            with patch.object(prepare.time, "monotonic", side_effect=lambda: clock[0]), patch.object(
                    prepare.Runner, "_capture", side_effect=capture), patch.object(prepare, "join_process_group", side_effect=join):
                with self.assertRaises(prepare.PreparationFailure):
                    runner.run([sys.executable, "-c", "print('metadata')"], root, capture=True, capture_limit=128)
            self.assertEqual([101.0], ends)
            row = runner.results[0]
            self.assertTrue(row["owned_group_empty"])
            self.assertEqual("deadline-expired", row["failure_category"])
            self.assertEqual(101.0, row["command_deadline"])

    def test_unconfirmed_cleanup_rejects_zero_exit_after_actual_group_join(self):
        with tempfile.TemporaryDirectory(prefix="issue779-capture-") as directory:
            root = Path(directory)
            end = time.monotonic()+5
            runner, ends = prepare.Runner(root, end), []
            real_join = prepare.join_process_group
            def join(child, deadline):
                ends.append(deadline)
                self.assertTrue(real_join(child, deadline))
                return False
            with patch.object(prepare, "join_process_group", side_effect=join):
                with self.assertRaises(prepare.PreparationFailure):
                    runner.run([sys.executable, "-c", "print('metadata')"], root, capture=True, capture_limit=128)
            self.assertEqual([end], ends)
            self.assertEqual(0, runner.results[0]["exit_code"])
            self.assertFalse(runner.results[0]["owned_group_empty"])
            self.assertEqual("ownership-unconfirmed", runner.results[0]["failure_category"])


if __name__ == "__main__":
    unittest.main()
