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
        real_popen, real_killpg, real_read = subprocess.Popen, module.os.killpg, module.os.read
        for phase in ("kill", "wait-close", "pipe-close"):
            with self.subTest(phase=phase):
                logs = self.root / phase
                logs.mkdir(mode=0o700)
                runner = module.Runner(logs, time.monotonic()+15)
                processes, observed_reads, observed_waits, closes, kills = [], [], [], [], []
                def cleanup(owned):
                    for process in owned:
                        try: real_killpg(process.pid, 9)
                        except OSError: pass
                        process.wait(timeout=3)
                self.addCleanup(cleanup, processes)
                def spawn(*args, **kwargs):
                    process = real_popen(*args, **kwargs)
                    processes.append(process)
                    if phase != "kill":
                        original_close = process.stdout.close
                        def close():
                            process.stdout.close = original_close
                            original_close()
                            closes.append(process.stdout.closed)
                            raise OSError("private-cleanup-canary")
                        process.stdout.close = close
                    if phase == "wait-close":
                        original_wait = process.wait
                        def wait(*args, **kwargs):
                            try: return original_wait(*args, **kwargs)
                            except subprocess.TimeoutExpired:
                                observed_waits.append("actual-timeout")
                                raise
                        process.wait = wait
                    return process
                def read(fd, count):
                    data = real_read(fd, count)
                    if processes and fd == processes[-1].stdout.fileno() and data:
                        observed_reads.append(data)
                    return data
                def killpg(pid, sig):
                    kills.append((pid, sig))
                    if phase == "kill" and len(kills) == 1:
                        raise OSError("private-cleanup-canary")
                    return real_killpg(pid, sig)
                script = "import os,signal; os.write(1,b'role-ready'+bytes([10])); "
                if phase == "wait-close": script += "os.close(1); "
                script += "signal.pause()"
                with patch.object(module.subprocess, "Popen", side_effect=spawn), patch.object(module.os, "killpg", side_effect=killpg), patch.object(module.os, "read", side_effect=read):
                    with self.assertRaises(module.PreparationFailure) as caught:
                        runner.run([sys.executable, "-c", script], logs, capture=True, maximum_seconds=2)
                self.assertEqual(("qualification-preparation-rejected",), caught.exception.args)
                self.assertEqual(b"role-ready\n", b"".join(observed_reads))
                if phase == "wait-close": self.assertEqual(["actual-timeout"], observed_waits)
                if phase != "kill": self.assertEqual([True], closes)
                record = json.loads((logs/"command-01.json").read_bytes())
                self.assertEqual(124, record["exit_code"])
                self.assertEqual("process-timeout", record["failure_category"])
                self.assertTrue(record["timed_out"])
                self.assertTrue(record["cleanup_failed"])
                self.assertTrue(record["owned_group_empty"])
                self.assertIsNotNone(processes[0].returncode)
                with self.assertRaises(ProcessLookupError): real_killpg(processes[0].pid, 0)
                self.assertEqual(record, runner.results[0])
                self.assertNotIn("private-cleanup-canary", (logs/"command-01.json").read_text())

    def test_pending_reentry_and_sticky_failure_with_actual_success_neighbor(self):
        real_popen = subprocess.Popen
        runner = module.Runner(self.root, time.monotonic()+15)
        reentries = []
        with patch.object(module.subprocess, "Popen") as popen:
            with self.assertRaises(module.PreparationFailure):
                runner.run([sys.executable, "-c", "pass"], self.root, maximum_seconds=0)
            popen.assert_not_called()
        def spawn(*args, **kwargs):
            with self.assertRaises(module.PreparationFailure):
                runner.run([sys.executable, "-c", "raise SystemExit(0)"], self.root)
            reentries.append("rejected-before-spawn")
            return real_popen(*args, **kwargs)
        with patch.object(module.subprocess, "Popen", side_effect=spawn) as popen:
            self.assertEqual(b"first\n", runner.run([sys.executable,"-c","print('first')"],self.root,capture=True))
            self.assertEqual(b"second\n", runner.run([sys.executable,"-c","print('second')"],self.root,capture=True))
            self.assertEqual(2, popen.call_count)
        self.assertEqual(["rejected-before-spawn"]*2, reentries)
        self.assertTrue(all(row["exit_code"] == 0 and row["owned_group_empty"] for row in runner.results))
        with self.assertRaises(module.PreparationFailure):
            runner.run([sys.executable,"-c","raise SystemExit(17)"],self.root)
        self.assertLess(time.monotonic(), runner.deadline)
        before = {p.name:p.read_bytes() for p in self.root.iterdir()}
        with patch.object(module.subprocess, "Popen") as popen:
            with self.assertRaises(module.PreparationFailure):
                runner.run([sys.executable,"-c","raise SystemExit(0)"],self.root)
            popen.assert_not_called()
        self.assertEqual(before, {p.name:p.read_bytes() for p in self.root.iterdir()})
        self.assertEqual(3,len(runner.results))
        # Failure before process creation also latches before the log open.
        logs = self.root/"blocked-log"; logs.mkdir(mode=0o700)
        (logs/"build-01.log").write_bytes(b"owned-sentinel")
        failed = module.Runner(logs,time.monotonic()+10)
        with patch.object(module.subprocess, "Popen") as popen:
            with self.assertRaises(FileExistsError): failed.run([sys.executable,"-c","pass"],logs)
            with self.assertRaises(module.PreparationFailure): failed.run([sys.executable,"-c","pass"],logs)
            popen.assert_not_called()
        self.assertEqual(b"owned-sentinel",(logs/"build-01.log").read_bytes())


if __name__ == "__main__":
    unittest.main()
