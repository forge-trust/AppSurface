"""Regression checks for the caller-owned doctor report consumer.

Run: python3 -m unittest discover -s examples/durable-external-activation -p 'test_*.py'
"""

import contextlib
import copy
import io
from pathlib import Path
import runpy
import unittest


consume = runpy.run_path(str(Path(__file__).with_name("consume-doctor-report.py")))["consume"]


class DoctorReportConsumerTests(unittest.TestCase):
    def setUp(self):
        self.report = {
            "schemaVersion": 1,
            "status": "passed",
            "exitCode": 0,
            "requestedChecks": [
                {"name": name, "requested": True, "status": "passed"}
                for name in ("credential", "schema", "epoch", "retention")
            ] + [{"name": "worker", "requested": False, "status": "not-requested"}],
            "findings": [],
            "nextAction": {
                "kind": "application-verifier",
                "command": None,
                "requiredInputs": ["consumer verifier command"],
            },
        }

    def test_pass_requires_every_store_runtime_check(self):
        for index in range(4):
            for worker_requested in (False, True):
                with self.subTest(check=index, worker_requested=worker_requested):
                    report = copy.deepcopy(self.report)
                    report["requestedChecks"][index].update(requested=False, status="not-requested")
                    if worker_requested:
                        report["requestedChecks"][4].update(requested=True, status="passed")
                    output = io.StringIO()
                    with contextlib.redirect_stdout(output):
                        with self.assertRaisesRegex(ValueError, "required store/runtime check"):
                            consume(report, 0)
                    self.assertEqual("", output.getvalue())

    def test_pass_rejects_all_checks_unrequested(self):
        for check in self.report["requestedChecks"]:
            check.update(requested=False, status="not-requested")
        with self.assertRaises(ValueError):
            consume(self.report, 0)

    def test_pass_accepts_optional_worker_or_passed_worker(self):
        for worker_requested in (False, True):
            with self.subTest(worker_requested=worker_requested):
                report = copy.deepcopy(self.report)
                if worker_requested:
                    report["requestedChecks"][4].update(requested=True, status="passed")
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    self.assertEqual(0, consume(report, 0))
                self.assertIn("application's composition verifier", output.getvalue())

    def test_pass_rejects_requested_check_that_did_not_pass(self):
        for index in range(5):
            for status in ("finding", "not-checked"):
                with self.subTest(check=index, status=status):
                    report = copy.deepcopy(self.report)
                    report["requestedChecks"][index].update(requested=True, status=status)
                    with self.assertRaisesRegex(ValueError, "incomplete requested check"):
                        consume(report, 0)

    def test_nonzero_result_preserves_process_exit_and_action(self):
        self.report.update(status="unavailable", exitCode=4)
        self.report["requestedChecks"][0]["status"] = "not-checked"
        self.report["findings"] = [{"code": "ASDUR414"}]
        self.report["nextAction"] = {"kind": "command", "command": {"argv": ["durable", "doctor"]}}
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(4, consume(self.report, 4))
        self.assertIn("ASDUR414", output.getvalue())

    def test_exit_mismatch_and_unsupported_version_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "exit disagree"):
            consume(self.report, 2)
        self.report["schemaVersion"] = 2
        with self.assertRaisesRegex(ValueError, "Unsupported doctor schema"):
            consume(self.report, 0)


if __name__ == "__main__":
    unittest.main()
