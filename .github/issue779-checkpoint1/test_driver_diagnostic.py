"""Closed failure projection over actual audit guards; no native work or authority."""

import contextlib
import importlib.util
import io
import json
from pathlib import Path
import stat
import subprocess
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import binding


ROOT = Path(__file__).resolve().parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


driver = load("c1_driver_diagnostic", ROOT / "run-native.py")
audit = load("c1_audit_diagnostic", ROOT / "procedures/os_audit.py")


class DriverDiagnosticTests(unittest.TestCase):
    def observe(self, operation):
        try:
            operation()
        except Exception as error:
            return driver.failure_diagnostic(error)
        self.fail("The negative control did not reject")

    def assert_closed(self, value):
        self.assertEqual(set(value), {"schema", "category", "stage", "error_kind", "errno",
                                     "exit", "native_acceptance", "coverage_credit"})
        self.assertEqual(value["exit"], 1)
        self.assertFalse(value["native_acceptance"])
        self.assertFalse(value["coverage_credit"])
        self.assertNotIn("private-canary", json.dumps(value))
        self.assertLess(len(json.dumps(value).encode()), 1024)

    def test_actual_missing_audit_tool_retains_closed_category_and_stage(self):
        owner = audit.Audit(time.monotonic() + 5)
        with patch.object(audit.os.path, "lexists", return_value=False), patch.object(owner, "pin_os") as pin:
            value = self.observe(owner.initialize_tools)
        pin.assert_not_called()
        self.assertEqual((value["category"], value["stage"], value["error_kind"], value["errno"]),
                         ("audit-required-os-tool-missing", "audit-initialize", "audit", None))
        self.assert_closed(value)

    def test_actual_audit_file_failure_projects_errno_without_private_path(self):
        owner = audit.Audit(time.monotonic() + 5)
        with patch.object(audit.os.path, "lexists", return_value=True), patch.object(
                owner, "pin_os", side_effect=FileNotFoundError(2, "private-canary", "/private-canary/tool")):
            value = self.observe(owner.initialize_tools)
        self.assertEqual((value["category"], value["stage"], value["error_kind"], value["errno"]),
                         ("native-os-error", "audit-initialize", "os", 2))
        self.assert_closed(value)

    def test_actual_sealed_root_guard_remains_a_failure(self):
        owner = audit.Audit(time.monotonic() + 5)
        info = SimpleNamespace(st_mode=stat.S_IFDIR | 0o700)
        with patch.object(owner, "no_link_chain", return_value=info):
            value = self.observe(lambda: owner.inventory("/private-canary/owned-tree"))
        self.assertEqual((value["category"], value["stage"]), ("audit-sealed-root", "audit-inventory"))
        self.assert_closed(value)

    def test_unknown_messages_and_malformed_rejected_payloads_are_not_echoed(self):
        for error in (ValueError("private-canary"), binding.Rejected("private-canary"),
                      binding.Rejected({"private-canary": "value"}), audit.AuditError("os-metadata")):
            with self.subTest(kind=type(error).__name__):
                value = driver.failure_diagnostic(error)
                self.assertEqual(value["category"], "input-or-verification-failure")
                self.assert_closed(value)
        value = driver.failure_diagnostic(binding.Rejected("source-git"))
        self.assertEqual(value["category"], "source-git")
        self.assert_closed(value)

    def test_errno_timeout_and_process_diagnostics_are_closed(self):
        for number in (None, True, -1, 0, 1, 13, 4095, 4096):
            with self.subTest(number=number):
                value = driver.failure_diagnostic(OSError(number, "private-canary", "/private-canary"))
                self.assertEqual(value["errno"], number if type(number) is int and 1 <= number <= 4095 else None)
                self.assert_closed(value)
        for error, kind in ((TimeoutError("private-canary"), "timeout"),
                            (subprocess.TimeoutExpired(["private-canary"], 1), "timeout"),
                            (subprocess.CalledProcessError(7, ["private-canary"]), "process")):
            with self.subTest(kind=kind):
                value = driver.failure_diagnostic(error)
                self.assertEqual(value["error_kind"], kind)
                self.assert_closed(value)

    def test_main_preserves_nonzero_exit_and_emits_only_one_closed_record(self):
        owner = audit.Audit(time.monotonic() + 5)
        arguments = SimpleNamespace(action=lambda _: owner.initialize_tools())
        parser = SimpleNamespace(parse_args=lambda _: arguments)
        stdout, stderr = io.StringIO(), io.StringIO()
        with patch.object(driver, "parser", return_value=parser), patch.object(
                audit.os.path, "lexists", return_value=False), contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = driver.main([])
        self.assertEqual(code, 1)
        self.assertEqual(stdout.getvalue(), "")
        self.assertEqual(len(stderr.getvalue().splitlines()), 1)
        value = json.loads(stderr.getvalue())
        self.assertEqual(value["category"], "audit-required-os-tool-missing")
        self.assert_closed(value)


if __name__ == "__main__":
    unittest.main()
