#!/usr/bin/env python3
"""Data-only N10 parser controls; never creates a lease, worker, signal, or native result."""
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("n10_parser_under_test", ROOT / "check_n10_pending_start.py")
MOD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MOD)
GEN = "11111111222233334444555555555555"
UNIT = "appsurface-evidence-worker-" + GEN + ".service"
JOB = "/org/freedesktop/systemd1/job/17"


def frames():
    start = {"schema": "issue779-n10-pending-start-phase-v1", "case": "N10",
             "phase": "start-transient-unit-reply", "unit": UNIT, "job_path": JOB,
             "native_authority": False, "native_acceptance": False}
    end = {"schema": "issue779-n10-pending-start-observation-v1", "case": "N10",
           "phase": "original-start-stop-and-pumps-joined-before-custody", "generation": GEN,
           "unit": UNIT, "job_path": JOB, "start_reply_observed": True,
           "pending_start": {"start_reserved": True, "started": False, "start_joined": True,
                             "closed": True, "stop_joined": True, "first_failure": "StartCancelled"},
           "lifetime": {"startup_joined": True, "startup_failed": True, "stop_joined": True,
                        "physically_settled": False},
           "original_stop_delegate_calls": 2,
           "final_unit": {"id": UNIT, "active_state": "inactive", "sub_state": "dead", "main_pid": 0,
                          "exec_main_pid": 217, "exec_main_code": 1, "exec_main_status": 2, "stopped": True},
           "final_group_after_pumps": {"sample_taken": True, "empty": True, "exists": True,
                                       "populated": False, "frozen": False, "device_major": 0,
                                       "device_minor": 34, "inode": 123},
           "pumps": {"joined": True, "received_bytes": 0, "discarded_bytes": 0, "failure": "None",
                     "stdout_eof": True, "stdout_failure": "None", "stderr_eof": True,
                     "stderr_failure": "None"},
           "root_custody_completed": False, "native_authority": False, "native_acceptance": False}
    return start, end


def encode(start=None, end=None):
    a, b = frames()
    return (json.dumps(start or a, separators=(",", ":")) + "\n" +
            json.dumps(end or b, separators=(",", ":")) + "\n").encode()


class N10ParserControls(unittest.TestCase):
    def test_joined_actual_contract_is_data_only(self):
        result = MOD.validate_bytes(encode())
        self.assertEqual(GEN, result["generation"])
        self.assertEqual(2, result["original_stop_delegate_calls"])
        self.assertFalse(result["physically_settled"])
        self.assertTrue(result["final_group_empty"])
        self.assertFalse(result["native_authority"])
        self.assertFalse(result["native_acceptance"])

    def test_phase_requires_original_reply_only(self):
        result = MOD.validate_bytes((json.dumps(frames()[0]) + "\n").encode(), "phase")
        self.assertEqual(JOB, result["job_path"])

    def test_bad_unit_and_job_are_rejected(self):
        for field, value in (("unit", "appsurface-evidence-worker-canary.service"),
                             ("job_path", "/org/freedesktop/systemd1/unit/17")):
            start, _ = frames()
            start[field] = value
            with self.subTest(field=field), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start=start))

    def test_reply_must_precede_unique_join_frame(self):
        start, end = frames()
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes((json.dumps(end) + "\n" + json.dumps(start) + "\n").encode())
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes(encode() + (json.dumps(start) + "\n").encode())

    def test_two_stops_sticky_failure_and_false_physical_state_are_required(self):
        for mutate in (
            lambda x: x["pending_start"].update(first_failure="None"),
            lambda x: x.update(original_stop_delegate_calls=1),
            lambda x: x["lifetime"].update(physically_settled=True),
            lambda x: x["lifetime"].update(startup_failed=False),
            lambda x: x["pending_start"].update(started=True),
        ):
            start, end = frames()
            mutate(end)
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_pending_start_flags_reject_integer_boolean_aliases(self):
        for field in ("start_reserved", "started", "start_joined", "closed", "stop_joined"):
            start, end = frames()
            # The integer equals the valid bool under Python dictionary equality.
            end["pending_start"][field] = int(end["pending_start"][field])
            with self.subTest(field=field), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_post_pump_group_and_error_free_eof_are_required(self):
        for mutate in (
            lambda x: x["final_group_after_pumps"].update(sample_taken=False),
            lambda x: x["final_group_after_pumps"].update(empty=False),
            lambda x: x["pumps"].update(stderr_eof=False),
            lambda x: x["pumps"].update(stdout_failure="Read"),
            lambda x: x["pumps"].update(discarded_bytes=1),
        ):
            start, end = frames()
            mutate(end)
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_final_unit_accepts_source_terminal_alternatives_and_rejects_bad_primitives(self):
        start, end = frames()
        end["final_unit"].update(active_state="failed", sub_state="failed", exec_main_code=2,
                                 exec_main_status=9)
        self.assertTrue(MOD.validate_bytes(encode(start, end))["unit_stopped"])
        for field, value in (("active_state", "active"), ("sub_state", "exited"), ("main_pid", 1),
                             ("exec_main_pid", True), ("exec_main_pid", 0),
                             ("exec_main_code", 1.0), ("exec_main_code", 4),
                             ("exec_main_status", True), ("exec_main_status", 256)):
            start, end = frames()
            end["final_unit"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))
        start, end = frames()
        end["final_unit"].update(exec_main_code=2, exec_main_status=65)
        with self.assertRaises(MOD.Reject):
            MOD.validate_bytes(encode(start, end))

    def test_group_accepts_absent_all_null_and_rejects_mixed_or_contradictory_samples(self):
        start, end = frames()
        end["final_group_after_pumps"].update(exists=False, populated=None, frozen=None,
                                               device_major=None, device_minor=None, inode=None)
        self.assertTrue(MOD.validate_bytes(encode(start, end))["final_group_empty"])
        for field, value in (("exists", False), ("populated", True), ("frozen", True),
                             ("device_major", True), ("device_minor", 1.0),
                             ("inode", False), ("inode", 0)):
            start, end = frames()
            if field == "exists":
                end["final_group_after_pumps"].update(exists=False, populated=None, frozen=None,
                                                       device_major=None, device_minor=None, inode=None)
                end["final_group_after_pumps"]["device_major"] = 0
            else:
                end["final_group_after_pumps"][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))

    def test_custody_or_acceptance_cannot_be_upgraded(self):
        for field, value in (("native_authority", True), ("native_acceptance", True),
                             ("root_custody_completed", True)):
            start, end = frames()
            (start if field != "root_custody_completed" else end)[field] = value
            with self.assertRaises(MOD.Reject):
                MOD.validate_bytes(encode(start, end))


if __name__ == "__main__":
    unittest.main()
