import os
import pathlib
import sys
import tempfile
import time
import unittest
from unittest import mock

import n07_helper_process as runner


class BoundedProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="n07-helper-runner-")
        self.root = pathlib.Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_build_helper_receipt_consumer_uses_runner_metadata(self):
        helper = pathlib.Path(__file__).with_name("build-helper.sh").read_text()
        self.assertIn("assert helper_process.successful_command_records(RECORDS)", helper)
        rows = []
        for operation in ("sdk-version", "locked-restore", "fdd-publish"):
            rows.append({
                "operation": operation,
                "exit": 0,
                "waited": True,
                "descendants_settled": True,
                "forced_cleanup": False,
                "error": False,
                "overflow": False,
                "discarded_bytes": 0,
                "read_error": False,
                "write_error": False,
                "pipe_eof": True,
                "deadline_ok": True,
                "failure": None,
            })
        self.assertNotIn("group_absent", rows[0])
        self.assertTrue(runner.successful_command_records(rows))
        rows[1]["descendants_settled"] = False
        self.assertFalse(runner.successful_command_records(rows))
        rows[1]["descendants_settled"] = True
        rows[2]["deadline_ok"] = False
        self.assertFalse(runner.successful_command_records(rows))

    def test_bounded_ps_census_rejects_overcap_and_reaps_direct_child(self):
        waited = []
        real_wait = runner.subprocess.Popen.wait

        def tracked_wait(proc, *args, **kwargs):
            result = real_wait(proc, *args, **kwargs)
            waited.append((proc.pid, proc.returncode))
            return result

        code = "import os,time; os.write(1,b'x'*4096); time.sleep(5)"
        with mock.patch.object(runner.subprocess.Popen, "wait", tracked_wait):
            output, error = runner._bounded_ps_output(
                [sys.executable, "-c", code],
                128,
                deadline=time.monotonic() + 1.0,
            )
        self.assertIsNone(output, "over-cap census output must not be returned truncated")
        self.assertEqual(error, "output-limit")
        self.assertEqual(len(waited), 1, "census child must be reaped exactly once")
        self.assertIsNotNone(waited[0][1], "census child must be stopped before reaping")

    def run_child(self, index, code, check=None, cleanup_remaining=None):
        deadline = time.monotonic() + 5.0

        def default_check():
            if time.monotonic() >= deadline:
                raise TimeoutError("test-work-deadline")

        public, private = runner.run_command(
            "sdk-version",
            [sys.executable, "-c", code],
            cwd=self.root,
            log_path=self.root / ("build-%02d.log" % index),
            env={"PATH": os.environ.get("PATH", ""), "PYTHONUNBUFFERED": "1"},
            check=check or default_check,
            cleanup_remaining=cleanup_remaining or (lambda: 2.0),
        )
        return public, private

    def test_normal_stdout_and_stderr_are_merged_and_reaped(self):
        public, private = self.run_child(0, "import os; os.write(1,b'out\\n'); os.write(2,b'err\\n')")
        data = (self.root / "build-00.log").read_bytes().splitlines()
        self.assertEqual(public["exit"], 0)
        self.assertFalse(public["error"])
        self.assertTrue(public["pipe_eof"])
        self.assertTrue(public["descendants_settled"])
        self.assertEqual(set(data), {b"out", b"err"})
        self.assertEqual(private["operation"], "sdk-version")
        self.assertEqual(private["argv"][0], sys.executable)

    def test_oversized_log_is_capped_counted_and_rejected(self):
        code = "import os; chunk=b'x'*65536; [os.write(1,chunk) for _ in range(130)]"
        public, _ = self.run_child(1, code)
        self.assertTrue(public["error"])
        self.assertTrue(public["overflow"])
        self.assertGreater(public["discarded_bytes"], 0)
        self.assertEqual(public["log_bytes"], runner.MAX_LOG_BYTES)
        self.assertEqual((self.root / "build-01.log").stat().st_size, runner.MAX_LOG_BYTES)
        self.assertTrue(public["waited"])
        self.assertTrue(public["descendants_settled"])
        self.assertTrue(public["pipe_eof"])

    def test_child_can_write_regular_file_larger_than_log_cap(self):
        code = "import pathlib; p=pathlib.Path('large.bin'); f=p.open('wb'); [f.write(b'z'*65536) for _ in range(129)]; f.close(); print('done')"
        public, _ = self.run_child(2, code)
        self.assertFalse(public["error"])
        self.assertEqual((self.root / "large.bin").stat().st_size, 129 * 65536)
        self.assertLessEqual(public["log_bytes"], runner.MAX_LOG_BYTES)

    def test_nonzero_exit_is_rejected_after_pipe_eof(self):
        public, _ = self.run_child(3, "import os; os.write(2,b'expected failure\\n'); raise SystemExit(7)")
        self.assertTrue(public["error"])
        self.assertEqual(public["exit"], 7)
        self.assertEqual(public["failure"], "command-exit-nonzero")
        self.assertTrue(public["waited"])
        self.assertTrue(public["descendants_settled"])
        self.assertTrue(public["pipe_eof"])

    def test_work_deadline_kills_and_settles_child_group(self):
        start = time.monotonic()
        work_end = start + 0.15
        final_end = work_end + 2.0
        events = []
        reaped = set()
        real_wait = runner.subprocess.Popen.wait
        real_killpg = runner.os.killpg

        def check():
            if time.monotonic() >= work_end:
                raise TimeoutError("test-work-deadline")

        def tracked_wait(proc, *args, **kwargs):
            result = real_wait(proc, *args, **kwargs)
            if isinstance(proc.args, (list, tuple)) and proc.args[:2] == [sys.executable, "-c"]:
                reaped.add(proc.pid)
                events.append(("wait", proc.pid))
            return result

        def guarded_killpg(pgid, sig):
            self.assertNotIn(pgid, reaped, "process-group signal after leader reap")
            events.append(("killpg", pgid))
            return real_killpg(pgid, sig)

        with mock.patch.object(runner.subprocess.Popen, "wait", tracked_wait), \
             mock.patch.object(runner.os, "killpg", guarded_killpg):
            public, _ = self.run_child(
                4,
                "import time; time.sleep(5)",
                check=check,
                cleanup_remaining=lambda: max(0.0, final_end - time.monotonic()),
            )
        self.assertLess(time.monotonic() - start, 2.5)
        self.assertTrue(public["error"])
        self.assertFalse(public["deadline_ok"])
        self.assertEqual(public["failure"], "work-deadline")
        self.assertTrue(public["forced_cleanup"])
        self.assertTrue(public["waited"])
        self.assertTrue(public["descendants_settled"])
        self.assertTrue(public["pipe_eof"])
        kill_indices = [i for i, event in enumerate(events) if event[0] == "killpg"]
        wait_indices = [i for i, event in enumerate(events) if event[0] == "wait"]
        self.assertTrue(kill_indices, "deadline cleanup did not signal the live disposable group")
        self.assertTrue(wait_indices, "deadline cleanup did not reap the disposable leader")
        self.assertLess(max(kill_indices), min(wait_indices), "group signal must precede leader reap")

    def test_leader_exit_waits_for_live_descendant_without_signaling_reaped_pgid(self):
        descendant = "import os,time; time.sleep(.30); os.write(1,b'descendant-done\\n')"
        code = "import subprocess,sys; subprocess.Popen([sys.executable,'-c'," + repr(descendant) + "]); print('leader-done')"
        seen_group_states = []
        signals = []
        real_observer = runner._group_descendants_settled
        real_wait = runner.subprocess.Popen.wait
        real_killpg = runner.os.killpg
        reaped = set()

        def observe(pgid, leader_pid, **kwargs):
            state = real_observer(pgid, leader_pid, **kwargs)
            seen_group_states.append(state)
            return state

        def tracked_wait(proc, *args, **kwargs):
            result = real_wait(proc, *args, **kwargs)
            reaped.add(proc.pid)
            return result

        def guarded_killpg(pgid, sig):
            self.assertNotIn(pgid, reaped, "process-group signal after leader reap")
            signals.append((pgid, sig))
            return real_killpg(pgid, sig)

        with mock.patch.object(runner, "_group_descendants_settled", observe), \
             mock.patch.object(runner.subprocess.Popen, "wait", tracked_wait), \
             mock.patch.object(runner.os, "killpg", guarded_killpg):
            public, _ = self.run_child(5, code)
        self.assertFalse(public["error"])
        self.assertTrue(public["leader_exit_observed"])
        self.assertTrue(public["descendants_settled"])
        self.assertTrue(public["pipe_eof"])
        self.assertTrue(public["waited"])
        self.assertIn(False, seen_group_states, "test did not observe live descendant after leader exit")
        self.assertTrue(seen_group_states[-1])
        self.assertEqual(signals, [])

    def test_escaped_descendant_missing_eof_fails_at_original_cleanup_end(self):
        marker = self.root / "escaped-descendant-finished"
        started = time.monotonic()
        work_end = started + 0.15
        cleanup_end = work_end + 0.20
        descendant = (
            "import pathlib,time; time.sleep(.55); "
            f"pathlib.Path({str(marker)!r}).write_text('finished')"
        )
        code = "import subprocess,sys; subprocess.Popen([sys.executable,'-c'," + repr(descendant) + "],start_new_session=True); print('leader-done')"

        def check():
            if time.monotonic() >= work_end:
                raise TimeoutError("test-work-deadline")

        public, _ = self.run_child(
            6,
            code,
            check=check,
            cleanup_remaining=lambda: max(0.0, cleanup_end - time.monotonic()),
        )
        self.assertTrue(public["error"])
        self.assertFalse(public["pipe_eof"])
        self.assertTrue(public["leader_exit_observed"])
        self.assertTrue(public["descendants_settled"], "escaped child must not be counted in original group")
        self.assertTrue(public["waited"])
        self.assertLess(time.monotonic() - started, 1.5)
        end = time.monotonic() + 2.0
        while not marker.exists() and time.monotonic() < end:
            time.sleep(0.02)
        self.assertTrue(marker.exists(), "escaped disposable child did not finish after EOF failure")


if __name__ == "__main__":
    unittest.main()
