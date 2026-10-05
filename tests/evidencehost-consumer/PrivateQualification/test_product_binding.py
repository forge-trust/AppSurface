"""Local build-data deadline controls; no root capability or subject evaluation."""
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import time
import sys
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
SPEC = importlib.util.spec_from_file_location("qualification_build_binding", HERE / "prepare.py")
prepare = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(prepare)


class BuildBindingPublicationControls(unittest.TestCase):
    def preflight(self, root):
        target = root / "build-binding.json"
        target.write_text('{"preparation_complete":false}')
        target.chmod(0o600)
        return target

    def test_real_exclusive_file_is_closed_and_private_before_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.preflight(root)
            with patch.object(prepare.time, "monotonic", return_value=10):
                prepare.write_complete_build_binding(root, {"preparation_complete": True}, 11)
            target = root / "build-binding.json"
            self.assertEqual({"preparation_complete": True}, json.loads(target.read_bytes()))
            self.assertEqual(0o600, stat.S_IMODE(target.stat().st_mode))
            before = target.read_bytes()
            with patch.object(prepare.time, "monotonic", return_value=10):
                with self.assertRaises(prepare.PreparationFailure):
                    prepare.write_complete_build_binding(root, {}, 11)
            self.assertEqual(before, target.read_bytes())

    def test_expired_before_write_or_oversized_data_creates_nothing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.preflight(root)
            with patch.object(prepare.time, "monotonic", return_value=11):
                with self.assertRaises(prepare.PreparationFailure):
                    prepare.write_complete_build_binding(root, {}, 11)
            with patch.object(prepare.time, "monotonic", return_value=10):
                with self.assertRaises(prepare.PreparationFailure):
                    prepare.write_complete_build_binding(root, {"data": "x"*(1024*1024)}, 11)
            self.assertEqual({"preparation_complete": False}, json.loads((root / "build-binding.json").read_bytes()))
            self.assertFalse((root / ".build-binding-complete.tmp").exists())

    def test_expiry_after_real_file_close_rejects_retained_diagnostic_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            root, fd = Path(directory), []
            self.preflight(root)
            actual = prepare.os.fchmod
            def capture(number, mode):
                fd.append(number)
                return actual(number, mode)
            with patch.object(prepare.time, "monotonic", side_effect=[10, 10, 11]), patch.object(
                    prepare.os, "fchmod", side_effect=capture):
                with self.assertRaises(prepare.PreparationFailure):
                    prepare.write_complete_build_binding(root, {"data": "private"}, 11)
            self.assertEqual({"data": "private"}, json.loads((root / ".build-binding-complete.tmp").read_bytes()))
            self.assertEqual({"preparation_complete": False}, json.loads((root / "build-binding.json").read_bytes()))
            self.assertEqual(1, len(fd))
            with self.assertRaises(OSError):
                os.fstat(fd[0])


class SelectedPreparationTimeControls(unittest.TestCase):
    def test_selected_one_second_end_bounds_real_child_and_keeps_global_deadline(self):
        with tempfile.TemporaryDirectory() as directory:
            root, started = Path(directory), time.monotonic()
            global_end = started+20
            runner = prepare.Runner(root, global_end)
            with self.assertRaises(prepare.PreparationFailure):
                runner.run([sys.executable, "-c", "import time;time.sleep(60)"], root,
                    capture=True, maximum_seconds=1)
            row = runner.results[0]
            self.assertTrue(row["timed_out"])
            self.assertTrue(row["owned_group_empty"])
            self.assertFalse(row["cleanup_failed"])
            self.assertLessEqual(row["command_deadline"], started+1.2)
            self.assertEqual(global_end, runner.deadline)
            self.assertLess(row["io_deadline"], row["command_deadline"])
            self.assertLess(time.monotonic(), row["command_deadline"])

    def test_invalid_selected_time_bounds_reject_before_process_dispatch(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runner = prepare.Runner(root, time.monotonic()+20)
            with patch.object(prepare.subprocess, "Popen") as dispatched:
                for value in (0, -1, True, 181, "30", None, 1.5):
                    with self.subTest(value=value), self.assertRaises(prepare.PreparationFailure):
                        runner.run([sys.executable, "-c", "pass"], root, maximum_seconds=value)
                dispatched.assert_not_called()
            self.assertEqual([], runner.results)


if __name__ == "__main__":
    unittest.main()
