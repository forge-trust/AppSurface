"""Portable fixture ordering only: pipe FDs and mocked signaling issue no native ownership."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import tempfile
import time
import unittest
from types import SimpleNamespace
from unittest.mock import patch
from unittest.mock import Mock

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
        retained_validation = Mock()
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
             patch.dict(runner.sys.modules, {"workspace_guards": SimpleNamespace(
                 run=guards, APPLICATION_CASES=("bundle-extra-directory", "bundle-input-bytes", "bundle-input-mode"),
                 PRODUCER_CASES=("scratch-fifo", "scratch-hardlink", "scratch-oversize")),
                                             "retained_application": SimpleNamespace(validate=retained_validation)}):
            yield SimpleNamespace(args=args, end=original_end, clock=clock, order=order,
                                  guard_bounds=guard_bounds, rows=rows, staged=staged,
                                  executed=executed, monotonic=monotonic,
                                  retained_validation=retained_validation)

    @staticmethod
    def receipt(data):
        return json.loads((data.args.output / "native-receipt.json").read_bytes())

    def test_original_seven_precede_one_fresh_shared_guard_partition_with_the_same_schema(self):
        with tempfile.TemporaryDirectory() as directory, self.data_fixture(directory) as data:
            self.assertEqual(0, runner.run_partitions(data.args, data.end))
            data.retained_validation.assert_called_once()
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


class FixtureCleanupGuardTests(unittest.TestCase):
    """Rejected path data must never begin a filesystem cleanup operation."""

    def test_only_exact_generated_roots_can_begin_cleanup(self):
        generation = "a" * 32
        paths = (Path("/"), Path("/run"), Path("/tmp/issue779-c3-lifecycle-" + generation),
                 Path("/run/issue779-c3-lifecycle-" + generation) / "..",
                 Path("/run/appsurface-evidence-owners") / ("b" * 32))
        for path in paths:
            with self.subTest(path=path), patch.object(runner.os, "open") as opened, \
                 patch.object(Path, "lstat") as observed:
                with self.assertRaisesRegex(RuntimeError, "fixed-fixture-cleanup-root"):
                    runner.remove_sealed_fixture_tree(path, generation, 0)
                opened.assert_not_called()
                observed.assert_not_called()
        for bad in ("", "A" * 32, "a" * 31, "a" * 33, "../" + "a" * 29):
            with self.subTest(generation=bad), patch.object(runner.os, "open") as opened, \
                 patch.object(Path, "lstat") as observed:
                with self.assertRaisesRegex(RuntimeError, "fixed-fixture-cleanup-root"):
                    runner.remove_sealed_fixture_tree(Path("/run/issue779-c3-lifecycle-" + bad), bad, 0)
                opened.assert_not_called()
                observed.assert_not_called()


class LifecycleStallMarkerTests(unittest.TestCase):
    def test_fixed_compile_owned_markers_are_data_only(self):
        cases = (("configure-sync-stall", "configure-sync-stall"),
                 ("registered-disposer-stall", "registered-application-disposer-stall"))
        self.assertEqual((*tuple(case for case, _ in cases), "post-stop-guards"), runner.LIFECYCLE_STALL_CASES)
        for case, marker_case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                log = Path(directory) / "stderr.log"
                log.write_text(json.dumps({"schema": "evidence-worker-lifecycle-stall-v1", "case": marker_case,
                                           "phase": "entered", "native_acceptance": False}) + "\n")
                marker = runner.lifecycle_stall_events(log, marker_case)
                self.assertIs(marker["native_acceptance"], False)

    def test_wrong_case_duplicate_marker_and_extra_fields_are_rejected(self):
        valid = {"schema": "evidence-worker-lifecycle-stall-v1", "case": "configure-sync-stall",
                 "phase": "entered", "native_acceptance": False}
        for rows in ([dict(valid, case="registered-application-disposer-stall")],
                     [valid, valid], [dict(valid, admission=True)],
                     [dict(valid, native_acceptance=True)], [dict(valid, phase="completed")]):
            with self.subTest(rows=rows), tempfile.TemporaryDirectory() as directory:
                log = Path(directory) / "stderr.log"
                log.write_text("\n".join(json.dumps(row) for row in rows) + "\n")
                with self.assertRaises(RuntimeError):
                    runner.lifecycle_stall_events(log, "configure-sync-stall")

    def test_unrelated_json_and_malformed_lines_cannot_supply_the_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "stderr.log"
            log.write_text("not-json\n" + json.dumps({"schema": "other", "case": "configure-sync-stall"}) + "\n")
            with self.assertRaises(RuntimeError):
                runner.lifecycle_stall_events(log, "configure-sync-stall")


class FailedLifecycleDiagnosticsTests(unittest.TestCase):
    def test_only_failed_contained_terminal_cases_retain_existing_worker_bytes(self):
        import lifecycle_stalls
        for passed, empty, terminal in ((True, True, True), (False, False, True),
                                        (False, True, False), (False, True, True)):
            with self.subTest(passed=passed, empty=empty, terminal=terminal), \
                    tempfile.TemporaryDirectory() as directory:
                result = {'passed': passed, 'final_groups_empty': empty,
                          'failure_category': 'original-failure', 'native_acceptance': False}
                process = Mock(returncode=-9)
                process.poll.return_value = -9 if terminal else None
                leaves = {'stdout.prefix': b'', 'stderr.prefix': b'original fatal prefix',
                          'state.json': b'{"schema":"evidence-worker-failure-output-v1"}'}
                with patch.object(lifecycle_stalls, 'read_worker_prefixes', create=True,
                                  return_value=(leaves, {'stderr.prefix': 'd' * 64})) as read:
                    runner.retain_failed_lifecycle_worker(
                        result, Path(directory), 'configure-sync-stall', 'a' * 32,
                        process, time.monotonic() + 30)
                self.assertEqual(passed, result['passed'])
                self.assertEqual('original-failure', result['failure_category'])
                self.assertIs(False, result['native_acceptance'])
                if not passed and empty and terminal:
                    read.assert_called_once()
                    self.assertTrue(result['worker_diagnostics_retained'])
                    self.assertEqual(-9, result['owner_process_returncode'])
                    for name, raw in leaves.items():
                        self.assertEqual(raw, (Path(directory) / ('configure-sync-stall-worker-' + name)).read_bytes())
                else:
                    read.assert_not_called()
                    self.assertEqual([], list(Path(directory).iterdir()))

    def test_diagnostic_error_and_absent_process_cannot_replace_primary_failure(self):
        import lifecycle_stalls
        with tempfile.TemporaryDirectory() as directory:
            result = {'passed': False, 'final_groups_empty': True,
                      'failure_category': 'original-timeout', 'native_acceptance': False}
            process = Mock(returncode=-9)
            process.poll.return_value = -9
            with patch.object(lifecycle_stalls, 'read_worker_prefixes', create=True,
                              side_effect=RuntimeError('private-path-canary')) as read:
                runner.retain_failed_lifecycle_worker(
                    result, Path(directory), 'configure-sync-stall', 'a' * 32,
                    None, time.monotonic() + 30)
                read.assert_not_called()
                runner.retain_failed_lifecycle_worker(
                    result, Path(directory), 'configure-sync-stall', 'a' * 32,
                    process, time.monotonic() + 30)
            self.assertEqual('original-timeout', result['failure_category'])
            self.assertFalse(result['passed'])
            self.assertFalse(result['worker_diagnostics_retained'])
            self.assertEqual('RuntimeError', result['worker_diagnostic_error_kind'])
            self.assertNotIn('private-path-canary', json.dumps(result))


if __name__ == "__main__":
    unittest.main()
