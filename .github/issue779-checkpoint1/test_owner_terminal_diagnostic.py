"""Actual shell diagnostic/guard data controls; no systemd, signals or native pass."""
import json
from pathlib import Path
import subprocess
import unittest

PATH = Path(__file__).resolve().parent / "procedures/owner-death/checkpoint-owner-death-common.sh"
SOURCE = PATH.read_text()
HELPER = SOURCE.split("# BEGIN OWNER TERMINAL DIAGNOSTIC\n", 1)[1].split("# END OWNER TERMINAL DIAGNOSTIC", 1)[0]
GUARD = SOURCE.split("# BEGIN OWNER DEADLINE TERMINAL PREDICATE\n", 1)[1].split(
    "# END OWNER DEADLINE TERMINAL PREDICATE", 1
)[0]
FIELD = next(line for line in SOURCE.splitlines() if line.startswith("field(){"))


class OwnerTerminalDiagnosticTests(unittest.TestCase):
    def invoke(self, facts, *, guard=False):
        program = "set -euo pipefail\n" + HELPER
        if guard:
            program += '\nbounded_exec(){ "$@"; }\n' + FIELD
            program += ('\nfail(){ exit 1; }\nowner_terminal=$1\nOWNER_DEATH_CASE=N13\n' +
                        'active_enter_usec=1000000000; runtime_usec=120000000; timeout_usec=5000000\n' + GUARD)
        else:
            program += '\nowner_terminal_diagnostic "$1"\n'
        return subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program, "_", facts],
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=2, check=False)

    def diagnostic(self, facts):
        result = self.invoke(facts)
        self.assertEqual((0, b""), (result.returncode, result.stdout))
        self.assertLess(len(result.stderr), 512)
        self.assertEqual(1, len(result.stderr.splitlines()))
        self.assertNotIn(b"private-canary", result.stderr)
        value = json.loads(result.stderr)
        self.assertEqual(set(value), {"schema", "exec_main_code", "exec_main_status", "main_pid", "active_state", "sub_state"})
        return value

    def test_actual_guard_keeps_sigkill_required_and_emits_only_on_failure(self):
        good = "ExecMainCode=2\nExecMainStatus=9\nMainPID=0\nActiveState=failed\nSubState=failed"
        result = self.invoke(good, guard=True)
        self.assertEqual((0, b"", b""), (result.returncode, result.stdout, result.stderr))
        for code, status in ((1, 0), (2, 15), (0, 0)):
            with self.subTest(code=code, status=status):
                result = self.invoke(good.replace("ExecMainCode=2", f"ExecMainCode={code}")
                                     .replace("ExecMainStatus=9", f"ExecMainStatus={status}"), guard=True)
                self.assertEqual(1, result.returncode)
                value = json.loads(result.stderr)
                self.assertEqual((code, status, 0), (value["exec_main_code"], value["exec_main_status"], value["main_pid"]))

    def test_numeric_and_closed_state_neighbors_are_retained(self):
        value = self.diagnostic("ExecMainCode=2\nExecMainStatus=9\nMainPID=2147483647\nActiveState=failed\nSubState=stop-sigkill\nUnit=private-canary")
        self.assertEqual((2, 9, 2147483647, "failed", "stop-sigkill"),
                         (value["exec_main_code"], value["exec_main_status"], value["main_pid"], value["active_state"], value["sub_state"]))

    def test_invalid_numeric_and_unknown_text_never_echo(self):
        for code, status, pid in (("256", "-1", "2147483648"), ("true", "private-canary", "0001"), ("", "01", "1;private-canary")):
            with self.subTest(code=code, status=status, pid=pid):
                value = self.diagnostic(f"ExecMainCode={code}\nExecMainStatus={status}\nMainPID={pid}\nActiveState=private-canary\nSubState=private-canary")
                self.assertEqual((None, None, None, "unknown", "unknown"),
                                 (value["exec_main_code"], value["exec_main_status"], value["main_pid"], value["active_state"], value["sub_state"]))

    def test_missing_and_duplicate_fields_remain_unknown(self):
        for facts in ("", "ExecMainCode=2\nExecMainCode=1\nExecMainCode=2\nExecMainStatus=9\nExecMainStatus=0\nMainPID=1\nMainPID=0\nActiveState=failed\nActiveState=active\nSubState=dead\nSubState=failed"):
            value = self.diagnostic(facts)
            self.assertEqual((None, None, None, "unknown", "unknown"),
                             (value["exec_main_code"], value["exec_main_status"], value["main_pid"], value["active_state"], value["sub_state"]))


if __name__ == "__main__":
    unittest.main()
