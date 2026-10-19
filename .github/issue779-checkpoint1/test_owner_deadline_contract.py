"""N13/N14 receipt and terminal-guard controls; no systemd or native execution."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent
PARSER = ROOT / "procedures/parsers/check_owner_death_record.py"
COMMON = ROOT / "procedures/owner-death/checkpoint-owner-death-common.sh"
SOURCE = COMMON.read_text()
GUARD = SOURCE.split("# BEGIN OWNER DEADLINE TERMINAL PREDICATE\n", 1)[1].split(
    "# END OWNER DEADLINE TERMINAL PREDICATE", 1
)[0]
FIELD = next(line for line in SOURCE.splitlines() if line.startswith("field(){"))
ACTIVE_ENTER = 1_000_000_000


def record(case="N14", *, result="timeout", code=2, status=15, exit_usec=None):
    generation = "0123456789abcdef0123456789abcdef"
    owner = {"pid": 101, "starttime": 1001, "uid": 0, "gid": 0,
             "unit": f"appsurface-evidence-owner-{generation}.service",
             "cgroup": f"/system.slice/appsurface-evidence-owner-{generation}.service"}
    worker = {"pid": 202, "starttime": 2002, "uid": 1234, "gid": 1234,
              "unit": f"appsurface-evidence-worker-{generation}.service",
              "cgroup": f"/system.slice/appsurface-evidence-worker-{generation}.service"}
    activation = ACTIVE_ENTER
    policy = {"active_enter_usec": activation, "runtime_max_usec": 120_000_000,
              "runtime_max_show": "2min", "timeout_stop_usec": 5_000_000,
              "timeout_stop_show": "5s",
              "final_kill_policy": "SendSIGKILL=yes;FinalKillSignal=9;KillMode=control-group",
              "timer_rearmed": False, "new_acceptance": False}
    exit_at = activation + 122_000_000 if exit_usec is None else exit_usec
    observed_elapsed = 120_000_000 if case == "N13" else max(122_000_000, exit_at - activation)
    termination = {"owner_exited": True, "worker_exited": True,
                   "owner_unit_terminal": True, "worker_unit_terminal": True,
                   "owner_active_state": "failed", "worker_active_state": "failed",
                   "owner_sub_state": "failed", "worker_sub_state": "failed",
                   "owner_terminal_main_pid": 0, "worker_terminal_main_pid": 0,
                   "owner_cgroup_empty": True, "worker_cgroup_empty": True,
                   "elapsed_seconds": observed_elapsed // 1_000_000,
                   "from_activation_usec": observed_elapsed,
                   "owner_exec_main_code": code,
                   "owner_exec_main_status": 9 if case == "N13" else status}
    if case == "N14":
        termination.update(owner_result=result, owner_exit_usec=exit_at)
    phase = f"NATIVE_PRIVATE_PHASE:{case}:ACCEPTED_BLOCKED_WORK"
    return {"schema": "issue779-owner-death-native-fixture-v1", "case": case,
            "generation": generation, "phase_line": phase,
            "accepted_reply_committed": True, "original_request_pending": True,
            "owner": owner, "worker": worker,
            "signal": {"name": "SIGKILL" if case == "N13" else "SIGSTOP",
                       "target_pid": owner["pid"], "pre_signal_owner": owner,
                       "syscall_succeeded": True, "elapsed_seconds": 10,
                       "from_activation_usec": 10_000_000},
            "systemd_bound": policy, "termination": termination,
            "pipes": {"root_stdout_eof": True, "root_stderr_eof": True,
                      "stdout_bytes": len((phase + "\n").encode("ascii")),
                      "stderr_bytes": 0, "stdout_sha256": "a" * 64,
                      "stderr_sha256": "b" * 64, "launch_exit": 1},
            "positive_files_before": 0, "positive_files_after": 0,
            "accounts": {"generation_bound": True, "retained": True, "reusable": False,
                         "names": ["evw" + generation[:28], "evs" + generation[:28],
                                   "evr" + generation[:28]], "worker_uid": 1234,
                         "worker_gid": 1234, "subject_uid": 2345,
                         "subject_gid": 2345, "results_gid": 3456},
            "workspace": {"generation_bound": True, "quarantined": True, "retained": True},
            "owner_death_outcome_preserved": True, "native_credit": False}


class OwnerDeadlineContractTests(unittest.TestCase):
    def parse(self, value):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "record.json"
            path.write_text(json.dumps(value, separators=(",", ":")))
            return subprocess.run([sys.executable, "-I", "-S", "-B", str(PARSER), str(path)],
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  timeout=2, check=False)

    def shell_guard(self, case, *, result="timeout", code=2, status=15, exit_usec=None):
        active = ACTIVE_ENTER
        exit_at = active + 122_000_000 if exit_usec is None else exit_usec
        facts = (f"Result={result}\nExecMainCode={code}\nExecMainStatus={status}\n"
                 f"ExecMainExitTimestampMonotonic={exit_at}")
        program = ("set -euo pipefail\n" + FIELD + "\n" +
                   "bounded_exec(){ \"$@\"; }\nowner_terminal_diagnostic(){ :; }\nfail(){ exit 1; }\n" +
                   "owner_terminal=$2; active_enter_usec=$3; runtime_usec=120000000; "
                   "timeout_usec=5000000\n" + GUARD)
        return subprocess.run(["/bin/bash", "--noprofile", "--norc", "-c", program,
                               "_", case, facts, str(active)],
                              env={"PATH": "/usr/bin:/bin", "OWNER_DEATH_CASE": case},
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                              timeout=2, check=False)

    def validate_data(self, value):
        return self.parse(value).returncode == 0

    def test_n14_timeout_accepts_sigterm_or_sigkill_within_original_window(self):
        for status in (15, 9):
            for exit_usec in (ACTIVE_ENTER + 120_000_000,
                              ACTIVE_ENTER + 122_000_000,
                              ACTIVE_ENTER + 125_000_000):
                with self.subTest(status=status, exit_usec=exit_usec):
                    self.assertTrue(self.validate_data(record(status=status, exit_usec=exit_usec)))
                    self.assertEqual(0, self.shell_guard("N14", status=status,
                                                         exit_usec=exit_usec).returncode)

    def test_n14_rejects_wrong_result_early_late_and_non_systemd_exit(self):
        invalid = (
            (record(result="signal"), {"result": "signal"}),
            (record(exit_usec=ACTIVE_ENTER + 119_999_999), {"exit_usec": ACTIVE_ENTER + 119_999_999}),
            (record(exit_usec=ACTIVE_ENTER + 125_000_001), {"exit_usec": ACTIVE_ENTER + 125_000_001}),
            (record(status=0), {"status": 0}),
            (record(code=1), {"code": 1}),
        )
        for value, shell_args in invalid:
            with self.subTest(shell_args=shell_args):
                self.assertNotEqual(0, self.parse(value).returncode)
                self.assertNotEqual(0, self.shell_guard("N14", **shell_args).returncode)

    def test_n14_exit_timestamp_cannot_follow_the_observed_terminal_sample(self):
        value = record()
        value["termination"].update(elapsed_seconds=121, from_activation_usec=121_000_000)
        self.assertNotEqual(0, self.parse(value).returncode)

    def test_final_uptime_sample_retains_its_actual_centisecond_precision(self):
        for rounding, accepted in ((5000, True), (9999, True), (10000, False)):
            with self.subTest(rounding=rounding):
                value = record(exit_usec=ACTIVE_ENTER + 122_000_000 + rounding)
                value["termination"].update(elapsed_seconds=122, from_activation_usec=122_000_000)
                self.assertEqual(accepted, self.validate_data(value))

    def test_n13_keeps_original_closed_schema_and_rejects_sigterm(self):
        value = record("N13")
        self.assertTrue(self.validate_data(value))
        self.assertNotIn("owner_result", value["termination"])
        self.assertNotIn("owner_exit_usec", value["termination"])
        self.assertEqual(0, self.shell_guard("N13", status=9).returncode)
        value["termination"]["owner_exec_main_status"] = 15
        self.assertNotEqual(0, self.parse(value).returncode)
        self.assertNotEqual(0, self.shell_guard("N13", status=15).returncode)


if __name__ == "__main__":
    unittest.main()
