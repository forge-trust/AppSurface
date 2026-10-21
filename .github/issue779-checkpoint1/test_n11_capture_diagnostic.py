"""Fixed helper diagnostics only; no native capture or qualification is modeled."""

from pathlib import Path
import sys
import types
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parent / "procedures/n11"
sys.path.insert(0, str(ROOT))
import stall_capture


class CaptureDiagnosticTests(unittest.TestCase):
    def test_every_closed_stage_projects_only_its_fixed_marker(self):
        for stage in stall_capture.CAPTURE_STAGES:
            with self.subTest(stage=stage):
                error = stall_capture.CaptureRejected(stage)
                self.assertEqual("STALL_CAPTURE_REJECTED:" + stage + "\n",
                                 stall_capture.failure_marker(error))

    def test_unknown_types_canaries_and_malformed_arguments_use_fixed_fallback(self):
        class Foreign(stall_capture.CaptureRejected):
            pass

        for error in (ValueError("/private/canary"), OSError("secret"),
                      stall_capture.CaptureRejected("/private/canary"),
                      stall_capture.CaptureRejected({"raw": "canary"}),
                      stall_capture.CaptureRejected(True),
                      stall_capture.CaptureRejected("workspace", "canary"),
                      stall_capture.CaptureRejected(), Foreign("workspace")):
            with self.subTest(error_type=type(error).__name__):
                self.assertEqual("STALL_CAPTURE_REJECTED\n", stall_capture.failure_marker(error))

    def test_actual_capture_failure_latches_private_layout_and_closes_owner(self):
        owner = mock.Mock()
        owner.directory.side_effect = OSError("/private/canary")
        root_stat = types.SimpleNamespace(st_uid=0, st_gid=0, st_mode=0o40755)
        generation = "1" * 32
        with mock.patch.object(stall_capture.os, "geteuid", return_value=0), \
                mock.patch.object(stall_capture, "NativeCapture", return_value=owner), \
                mock.patch.object(stall_capture.os, "open", return_value=99), \
                mock.patch.object(stall_capture.os, "fstat", return_value=root_stat):
            owner.fds = []
            with self.assertRaises(stall_capture.CaptureRejected) as caught:
                stall_capture.capture("/run/appsurface-evidence-fixture/" + generation,
                                      generation, "a" * 40, "b" * 40, "c" * 64,
                                      "d" * 64, "dotnet", "fixture", 123)
        self.assertEqual(("private-layout",), caught.exception.args)
        self.assertIsNone(caught.exception.__cause__)
        owner.close.assert_called_once_with()
        owner.read.assert_not_called()
        owner.write.assert_not_called()
        self.assertEqual("STALL_CAPTURE_REJECTED:private-layout\n",
                         stall_capture.failure_marker(caught.exception))

    def test_final_deadline_rejection_is_mapped_after_finally_close(self):
        # Execute the actual terminal tail with a closed owner; it must neither
        # reopen nor return the cached result after the final deadline expires.
        source = (ROOT / "stall_capture.py").read_text()
        tail = source[source.rindex("    finally:\n        capture_owner.close()") :]
        program = "def terminal(capture_owner):\n    result = 'must-not-return'\n    try:\n        pass\n" + tail
        namespace = {"CaptureRejected": stall_capture.CaptureRejected}
        exec(compile(program, "capture-terminal-tail", "exec"), namespace)
        owner = mock.Mock()
        owner.check.side_effect = ValueError("private-canary")
        with self.assertRaises(stall_capture.CaptureRejected) as caught:
            namespace["terminal"](owner)
        self.assertEqual([mock.call.close(), mock.call.check()], owner.mock_calls)
        self.assertEqual(("final-deadline",), caught.exception.args)
        self.assertIsNone(caught.exception.__cause__)


if __name__ == "__main__":
    unittest.main()
