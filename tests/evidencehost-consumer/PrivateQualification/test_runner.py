"""Real finite trusted-build process controls; no native admission or qualification proof."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("qualification_process_preparation", Path(__file__).with_name("prepare.py"))
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class BuildRunnerControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()

    def test_normal_real_process_and_child_join_complete_without_group_survivor(self):
        runner = module.Runner(self.root, time.monotonic()+10)
        output = runner.run([sys.executable, "-c", "import subprocess,sys; subprocess.run([sys.executable,'-c','pass'],check=True); print('metadata')"],
                            self.root, capture=True)
        self.assertEqual(b"metadata\n", output)
        self.assertEqual(0, runner.results[0]["exit_code"])
        self.assertTrue(runner.results[0]["owned_group_empty"])
        self.assertFalse(runner.results[0]["timed_out"])

    def test_real_deadline_kills_and_reaps_owned_leader_before_rejecting(self):
        runner = module.Runner(self.root, time.monotonic()+.2)
        with self.assertRaises(module.PreparationFailure):
            runner.run([sys.executable, "-c", "import time; time.sleep(30)"], self.root)
        self.assertEqual(124, runner.results[0]["exit_code"])
        self.assertTrue(runner.results[0]["timed_out"])
        self.assertTrue(runner.results[0]["owned_group_empty"])

    def test_real_nonzero_exit_is_retained_and_cannot_become_success(self):
        runner = module.Runner(self.root, time.monotonic()+10)
        with self.assertRaises(module.PreparationFailure):
            runner.run([sys.executable, "-c", "raise SystemExit(17)"], self.root)
        self.assertEqual(17, runner.results[0]["exit_code"])
        self.assertTrue(runner.results[0]["owned_group_empty"])

    def test_real_oversized_log_rejects_after_cleanup_and_keeps_numeric_receipt(self):
        runner = module.Runner(self.root, time.monotonic()+10)
        with self.assertRaises(module.PreparationFailure):
            runner.run([sys.executable, "-c", "import sys; sys.stdout.buffer.write(b'x'*(8*1024*1024+1))"], self.root)
        record = json.loads((self.root/"command-01.json").read_bytes())
        self.assertEqual(0, record["exit_code"])
        self.assertEqual("output-limit", record["failure_category"])
        self.assertIsNone(record["log_sha256"])
        self.assertTrue(record["owned_group_empty"])

    def test_communication_error_keeps_original_exception_after_real_owned_cleanup(self):
        runner = module.Runner(self.root, time.monotonic()+10)
        real_popen = subprocess.Popen
        failure = OSError("private-canary")
        def spawn(*args, **kwargs):
            process = real_popen(*args, **kwargs)
            process.communicate = lambda *args, **kwargs: (_ for _ in ()).throw(failure)
            return process
        with patch.object(module.subprocess, "Popen", side_effect=spawn):
            with self.assertRaises(OSError) as caught:
                runner.run([sys.executable, "-c", "import time; time.sleep(30)"], self.root)
        self.assertIs(failure, caught.exception)
        record = json.loads((self.root/"command-01.json").read_bytes())
        self.assertEqual("process-communication-failed", record["failure_category"])
        self.assertTrue(record["owned_group_empty"])
        self.assertNotIn("private-canary", (self.root/"command-01.json").read_text())

    def test_timeout_cleanup_errors_keep_timeout_receipt_after_real_group_join(self):
        real_popen, real_killpg = subprocess.Popen, module.os.killpg
        for phase in ("kill", "second-communicate", "pipe-close"):
            with self.subTest(phase=phase):
                logs = self.root / phase
                logs.mkdir()
                runner = module.Runner(logs, time.monotonic()+.1)
                calls = []
                def spawn(*args, **kwargs):
                    process = real_popen(*args, **kwargs)
                    original = process.communicate
                    def communicate(*args, **kwargs):
                        calls.append("communicate")
                        if len(calls) == 2 and phase != "kill":
                            if phase == "pipe-close":
                                original_close = process.stdout.close
                                def close():
                                    process.stdout.close = original_close
                                    original_close()
                                    raise ValueError("private-cleanup-canary")
                                process.stdout.close = close
                            raise OSError("private-cleanup-canary")
                        return original(*args, **kwargs)
                    process.communicate = communicate
                    return process
                kills = []
                def killpg(pid, sig):
                    kills.append((pid, sig))
                    if phase == "kill" and len(kills) == 1:
                        raise OSError("private-cleanup-canary")
                    return real_killpg(pid, sig)
                with patch.object(module.subprocess, "Popen", side_effect=spawn), patch.object(module.os, "killpg", side_effect=killpg):
                    with self.assertRaises(module.PreparationFailure) as caught:
                        runner.run([sys.executable, "-c", "import time; time.sleep(30)"], logs, capture=True)
                self.assertEqual(("qualification-preparation-rejected",), caught.exception.args)
                record = json.loads((logs/"command-01.json").read_bytes())
                self.assertEqual(124, record["exit_code"])
                self.assertEqual("process-timeout", record["failure_category"])
                self.assertTrue(record["timed_out"])
                self.assertTrue(record["cleanup_failed"])
                self.assertTrue(record["owned_group_empty"])
                self.assertEqual(record, runner.results[0])
                self.assertNotIn("private-cleanup-canary", (logs/"command-01.json").read_text())


if __name__ == "__main__":
    unittest.main()
