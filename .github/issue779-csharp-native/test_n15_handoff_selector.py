"""Data-only regressions for the frozen N15 dispatch-marker selector."""
import importlib.util
from pathlib import Path
import unittest

RUNNER_PATH = Path(__file__).with_name("run-native.py")
RUNNER_SPEC = importlib.util.spec_from_file_location("issue779_n15_runner_data", RUNNER_PATH)
runner = importlib.util.module_from_spec(RUNNER_SPEC)
RUNNER_SPEC.loader.exec_module(runner)

MARKER = b"N15_CSHARP_OWNER_DISPATCHED\n"
FIXTURE_LOG = MARKER + b"N15_OWNER_LAUNCH_JOIN_MEASURED:launch=0:observer=0:stdout=0:phase_stderr=1\n"
DISPATCH = ["root-fixture-dispatch"]


class DispatchFailure(RuntimeError):
    pass


class RetentionFailure(RuntimeError):
    pass


class FakeRunner:
    def __init__(self, fail_dispatch=False, failure=None, dispatch_exit=0):
        self.records = []
        self.fail_dispatch = fail_dispatch
        self.failure = failure
        self.dispatch_exit = dispatch_exit

    def run(self, argv, deadline, allow_failure=False):
        row = {"ordinal": len(self.records), "phase": "actual-csharp-n15-owner-launch-observe-and-replay",
               "argv": argv, "exit": None}
        self.records.append(row)
        if argv == DISPATCH and self.fail_dispatch:
            raise self.failure
        row["exit"] = self.dispatch_exit if argv == DISPATCH else 0
        return row["exit"], b""


def collect_with_saved_stdout(test_runner, fixture_stdout):
    def collect():
        for name in ("retention", "archive-sha-before", "archive-read", "archive-sha-after"):
            test_runner.records.append({"ordinal": len(test_runner.records), "phase": "bounded-private-retention",
                                        "argv": [name], "exit": 0})
        return {"fixture-stdout.log": fixture_stdout}, {"retained": True}
    return collect


class N15HandoffSelectorControls(unittest.TestCase):
    def test_success_uses_saved_fixture_stdout_after_unrelated_last_query(self):
        test_runner = FakeRunner()
        result = {}
        index = len(test_runner.records)
        values = runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, FIXTURE_LOG), result, index)
        self.assertEqual(test_runner.records[-1]["argv"], ["archive-sha-after"])
        self.assertIs(values["fixture-stdout.log"], FIXTURE_LOG)
        self.assertIs(result["native_dispatched"], True)
        self.assertEqual(result["fixture_exit"], 0)

    def test_nonzero_dispatch_retains_marker_and_original_failure(self):
        test_runner = FakeRunner(dispatch_exit=7)
        result = {}
        index = len(test_runner.records)
        with self.assertRaises(runner.Rejected) as caught:
            runner.run_fixture_with_retention(
                test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, MARKER), result, index)
        self.assertEqual(caught.exception.category, "native-fixture-wrapper-nonzero")
        self.assertEqual(result["fixture_exit"], 7)
        self.assertIs(result["native_dispatched"], True)

    def test_no_marker_leaves_dispatch_false(self):
        test_runner = FakeRunner()
        result = {}
        index = len(test_runner.records)
        runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0,
            collect_with_saved_stdout(test_runner, b"setup completed; no owner launch\n"), result, index)
        self.assertIs(result["native_dispatched"], False)

    def test_missing_malformed_or_oversized_saved_log_stays_false(self):
        bad_logs = (None, b"", MARKER[:-1], b"N15_CSHARP_OWNER_DISPATCHED\r\n",
                    b"prefix N15_CSHARP_OWNER_DISPATCHED\n",
                    "N15_CSHARP_OWNER_DISPATCHED\n", b"x" * (runner.CAPS["fixture-stdout.log"] + 1))
        for saved in bad_logs:
            with self.subTest(saved_type=type(saved).__name__, size=len(saved) if hasattr(saved, "__len__") else None):
                test_runner = FakeRunner()
                result = {}
                index = len(test_runner.records)
                runner.run_fixture_with_retention(
                    test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, saved), result, index)
                self.assertIs(result["native_dispatched"], False)

    def test_bad_dispatch_index_does_not_fall_back_to_last_record(self):
        test_runner = FakeRunner()
        result = {}
        runner.run_fixture_with_retention(
            test_runner, DISPATCH, 10.0, collect_with_saved_stdout(test_runner, MARKER), result, 9)
        self.assertIs(result["native_dispatched"], False)

    def test_original_dispatch_failure_wins_if_retention_also_fails(self):
        original = DispatchFailure("original fixture failure")
        retention = RetentionFailure("archive query failed")
        test_runner = FakeRunner(fail_dispatch=True, failure=original)

        def collect():
            test_runner.records.append({"ordinal": len(test_runner.records), "phase": "bounded-private-retention",
                                        "argv": ["failed-archive-query"], "exit": 1})
            raise retention

        result = {}
        index = len(test_runner.records)
        with self.assertRaises(DispatchFailure) as caught:
            runner.run_fixture_with_retention(test_runner, DISPATCH, 10.0, collect, result, index)
        self.assertIs(caught.exception, original)
        self.assertIs(result["native_dispatched"], False)
        self.assertEqual(result["retention_failure"]["error_class"], "RetentionFailure")


if __name__ == "__main__":
    unittest.main(verbosity=2)
