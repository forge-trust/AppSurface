"""Portable fixture ordering only: pipe FDs and mocked signaling issue no native ownership."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import tempfile
import unittest
from types import SimpleNamespace
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



class MatrixPartitionOrderingTests(unittest.TestCase):
    """Detached procedure results test orchestration only, never a native owner or accepted receipt."""

    @contextlib.contextmanager
    def data_fixture(self, directory, *, failed_case=None, guard_rows=None,
                     stage_error=None, guard_error=None):
        args = SimpleNamespace(output=Path(directory) / "evidence", source="a" * 40, base="b" * 40)
        clock = [100.0]
        original_end = clock[0] + 1800
        order, guard_bounds = [], []
        tools, runtime = object(), object()
        names = ("bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode",
                 "scratch-fifo", "scratch-hardlink", "scratch-oversize")
        rows = ([{"case": name, "passed": True} for name in names]
                if guard_rows is None else guard_rows)

        def stage(actual, end):
            self.assertIs(args, actual)
            order.append(("stage", end))
            clock[0] += 400
            if stage_error is not None:
                raise stage_error
            return tools, runtime

        def execute(actual, case, actual_tools, actual_runtime, end):
            self.assertIs(args, actual)
            self.assertIs(tools, actual_tools)
            self.assertIs(runtime, actual_runtime)
            order.append((case, end))
            clock[0] += 180
            return {"case": case, "passed": case != failed_case, "native_acceptance": False}

        def guards(actual, actual_tools, actual_runtime, end):
            self.assertIs(args, actual)
            self.assertIs(tools, actual_tools)
            self.assertIs(runtime, actual_runtime)
            order.append(("guards", end, clock[0]))
            if guard_error is not None:
                raise guard_error
            for _ in rows:
                guard_bounds.append(end)
                clock[0] += 60
            return rows

        with patch.object(runner, "stage", side_effect=stage) as staged, \
             patch.object(runner, "execute", side_effect=execute) as executed, \
             patch.object(runner.time, "monotonic", side_effect=lambda: clock[0]) as monotonic, \
             patch.dict(runner.sys.modules, {"workspace_guards": SimpleNamespace(run=guards)}):
            yield SimpleNamespace(args=args, end=original_end, clock=clock, order=order,
                                  guard_bounds=guard_bounds, rows=rows, staged=staged,
                                  executed=executed, monotonic=monotonic)

    @staticmethod
    def receipt(data):
        return json.loads((data.args.output / "native-receipt.json").read_bytes())

    def test_original_seven_precede_one_fresh_shared_guard_partition_with_the_same_schema(self):
        with tempfile.TemporaryDirectory() as directory, self.data_fixture(directory) as data:
            self.assertEqual(0, runner.run_partitions(data.args, data.end))
            self.assertEqual(["stage", *runner.CASES, "guards"], [row[0] for row in data.order])
            self.assertEqual([data.end] * 8, [row[1] for row in data.order[:-1]])
            guard = data.order[-1]
            self.assertEqual(1760.0, guard[2])
            self.assertEqual(guard[2] + 900, guard[1])
            self.assertNotEqual(data.end, guard[1])
            self.assertEqual([guard[1]] * 6, data.guard_bounds)
            data.monotonic.assert_called_once_with()
            receipt = self.receipt(data)
            self.assertEqual("issue779-csharp-checkpoint3-native-v1", receipt["schema"])
            self.assertEqual(0, receipt["exit"])
            self.assertEqual(list(runner.CASES), [row["case"] for row in receipt["cases"]])
            self.assertEqual(data.rows, receipt["workspace_guards"])
            self.assertFalse(receipt["trusted_eligibility"])
            self.assertTrue(all(row["native_acceptance"] is False for row in receipt["cases"]))

    def test_each_original_failure_prevents_later_cases_and_the_guard_clock(self):
        for index, case in enumerate(runner.CASES):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory, \
                 self.data_fixture(directory, failed_case=case) as data:
                self.assertEqual(1, runner.run_partitions(data.args, data.end))
                self.assertEqual(["stage", *runner.CASES[:index + 1]], [row[0] for row in data.order])
                self.assertTrue(all(row[1] == data.end for row in data.order))
                data.monotonic.assert_not_called()
                receipt = self.receipt(data)
                self.assertEqual(index + 1, len(receipt["cases"]))
                self.assertFalse(receipt["cases"][-1]["passed"])
                self.assertEqual(1, receipt["exit"])
                self.assertNotIn("workspace_guards", receipt)

    def test_preparation_exception_keeps_a_failed_receipt_without_dispatching_cases_or_guards(self):
        original = OSError("fixture-preparation-canary")
        with tempfile.TemporaryDirectory() as directory, self.data_fixture(directory, stage_error=original) as data:
            with self.assertRaises(OSError) as error:
                runner.run_partitions(data.args, data.end)
            self.assertIs(original, error.exception)
            data.executed.assert_not_called()
            data.monotonic.assert_not_called()
            receipt = self.receipt(data)
            self.assertEqual(1, receipt["exit"])
            self.assertEqual([], receipt["cases"])
            self.assertNotIn("workspace_guards", receipt)
            self.assertNotIn("canary", json.dumps(receipt))

    def test_guard_exception_cannot_leave_the_original_seven_case_success_as_aggregate_success(self):
        original = RuntimeError("fixture-guard-canary")
        with tempfile.TemporaryDirectory() as directory, self.data_fixture(directory, guard_error=original) as data:
            with self.assertRaises(RuntimeError) as error:
                runner.run_partitions(data.args, data.end)
            self.assertIs(original, error.exception)
            self.assertEqual(7, data.executed.call_count)
            data.monotonic.assert_called_once_with()
            receipt = self.receipt(data)
            self.assertEqual(1, receipt["exit"])
            self.assertEqual(7, len(receipt["cases"]))
            self.assertTrue(all(row["passed"] for row in receipt["cases"]))
            self.assertNotIn("workspace_guards", receipt)
            self.assertNotIn("canary", json.dumps(receipt))

    def test_incomplete_or_failed_guard_results_never_pass_the_unchanged_six_case_requirement(self):
        for count, failed_index in [(0, None), (5, None), (7, None), *[(6, index) for index in range(6)]]:
            rows = [{"case": "guard-" + str(index), "passed": index != failed_index} for index in range(count)]
            with self.subTest(count=count, failed_index=failed_index), tempfile.TemporaryDirectory() as directory, \
                 self.data_fixture(directory, guard_rows=rows) as data:
                self.assertEqual(1, runner.run_partitions(data.args, data.end))
                receipt = self.receipt(data)
                self.assertEqual(1, receipt["exit"])
                self.assertEqual(rows, receipt["workspace_guards"])
                self.assertEqual(7, len(receipt["cases"]))
                data.monotonic.assert_called_once_with()


class OwnerReferenceGuardEventTests(unittest.TestCase):
    """Closed event-data checks only; these do not construct an owner or execute native denials."""

    def event(self):
        return {"schema": "evidence-native-owner-reference-guards-v1", "generation": "a" * 32,
                "rejected": list(runner.OWNER_REFERENCE_GUARDS), "native_acceptance": False}

    def inspect(self, rows, generation="a" * 32):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "stderr"
            path.write_text("\n".join(json.dumps(row) for row in rows) + "\n")
            return runner.owner_reference_guards(path, generation)

    def test_exact_event_requires_all_fixed_denials_without_claiming_native_acceptance(self):
        event = self.event()
        self.assertEqual(26, len(set(runner.OWNER_REFERENCE_GUARDS)))
        self.assertEqual(event, self.inspect([event]))

    def test_missing_duplicate_or_nonclosed_event_cannot_substitute_for_actual_denials(self):
        event = self.event()
        for rows in ([], [event, event], [dict(event, extra="raw-canary")],
                     [dict(event, generation="b" * 32)], [dict(event, native_acceptance=True)],
                     [dict(event, native_acceptance=0)], [dict(event, rejected=event["rejected"][:-1])],
                     [dict(event, rejected=[*event["rejected"][:-1], event["rejected"][0]])],
                     [dict(event, rejected=list(reversed(event["rejected"])))],
                     [dict(event, rejected="raw-canary")]):
            with self.subTest(rows=rows), self.assertRaises(RuntimeError):
                self.inspect(rows)

    def test_generation_must_be_the_original_fixed_run_identity(self):
        for generation in (None, 7, "", "A" * 32, "a" * 31, "a" * 33):
            with self.subTest(generation=generation), self.assertRaises(RuntimeError):
                self.inspect([self.event()], generation)


if __name__ == "__main__":
    unittest.main()
