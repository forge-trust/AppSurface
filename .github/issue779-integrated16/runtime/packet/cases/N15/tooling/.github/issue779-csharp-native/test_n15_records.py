"""Detached-data controls only; these cases execute no root or native operation."""
import copy
import hashlib
import json
import unittest

import check_n15_records as m

GEN = "0123456789abcdef0123456789abcdef"
OWNER = "appsurface-evidence-owner-" + GEN + ".service"
WORKER = "appsurface-evidence-worker-" + GEN + ".service"


def phase():
    return (json.dumps({"case_name": "N15", "phase": "start-transient-unit-reply",
        "unit": WORKER, "job_path": "/org/freedesktop/systemd1/job/123"},
        separators=(",", ":")) + "\n").encode()


def result():
    return {"schema": "issue779-n15-native-fixture-observation-v1", "case_generation": GEN,
        "source_commit": m.SOURCE, "source_capture_sha256": m.CAPTURE_SHA256,
        "phase_sha256": hashlib.sha256(phase()).hexdigest(),
        "job_path": "/org/freedesktop/systemd1/job/123",
        "worker": {"unit": WORKER, "main_pid": 321, "starttime_ticks": 456,
            "uid": 1001, "gid": 1002, "control_group": "/system.slice/" + WORKER,
            "cgroup_membership_matched": True, "identity_rechecked_before_signal": True},
        "owner": {"unit": OWNER, "main_pid": 654, "starttime_ticks": 987, "uid": 0, "gid": 0,
            "control_group": "/system.slice/" + OWNER, "pidfd_opened": True,
            "identity_rechecked_before_signal": True},
        "pidfd_signal": {"signal": 9, "syscall_succeeded": True},
        "owner_terminal": {"main_pid": 0, "active_state": "failed", "sub_state": "failed",
            "exec_main_pid": 654, "exec_main_code": 2, "exec_main_status": 9,
            "restart_policy": "no", "same_unit_no_replacement": True},
        "worker_terminal": {"main_pid": 0, "active_state": "failed", "sub_state": "failed",
            "exec_main_pid": 321, "exec_main_code": 1, "exec_main_status": 1,
            "restart_policy": "no", "cgroup_populated": False,
            "cgroup_tree_empty_twice": True, "no_successful_completion": True,
            "same_unit_no_replacement": True},
        "guard": {"armed_absent": True, "consumed_root_owned_exact_marker": True,
            "consumed_marker_same_generation": True},
        "reactivation": {"attempted_same_owner_generation": True, "owner_exit_nonzero": True,
            "reply_phase_absent": True, "worker_start_not_repeated": True,
            "failure_before_worker_start": True},
        "observation_only": True, "native_authority": False,
        "native_acceptance": False, "native_credit": False}


class PhaseFrameControls(unittest.TestCase):
    def test_exact_source_frame_is_data_only(self):
        parsed = m.validate_phase(phase(), GEN)
        self.assertEqual(parsed["unit"], WORKER)
        self.assertEqual(parsed["job_path"], "/org/freedesktop/systemd1/job/123")
        self.assertFalse(parsed["native_credit"])

    def test_duplicate_unknown_wrong_generation_and_boundaries_reject(self):
        raw = phase()
        bad = [raw.replace(b'"unit":', b'"unit":"ignored","unit":', 1),
               raw.replace(b'"case_name":"N15"', b'"case_name":"N08"'),
               raw.replace(GEN.encode(), b"f" * 32),
               raw.replace(b'"job_path":"/org/freedesktop/systemd1/job/123"', b'"job_path":"/tmp/x"'),
               raw + b"extra\n", b"\xff\n", raw.replace(b"\n", b"\r\n")]
        for sample in bad:
            with self.subTest(sample=sample[:50]), self.assertRaises(m.Reject):
                m.validate_phase(sample, GEN)
        with self.assertRaises(m.Reject):
            m.validate_phase(b"x" * (m.MAX_PHASE + 1), GEN)


class ObservationReceiptControls(unittest.TestCase):
    def test_complete_receipt_is_still_non_authoritative_data(self):
        summary = m.validate_result(result(), GEN)
        self.assertEqual(summary["worker_pid"], 321)
        self.assertFalse(summary["native_credit"])

    def test_live_identity_and_owner_death_require_exact_bindings(self):
        mutations = [
            ("source_capture_sha256", "0" * 64),
            ("phase_sha256", "x" * 64),
        ]
        for key, value in mutations:
            item = result(); item[key] = value
            with self.subTest(key=key), self.assertRaises(m.Reject):
                m.validate_result(item, GEN)
        for path, value in ((("worker", "uid"), 0),
                            (("worker", "cgroup_membership_matched"), False),
                            (("owner", "starttime_ticks"), 0),
                            (("pidfd_signal", "syscall_succeeded"), False),
                            (("owner_terminal", "exec_main_status"), 0)):
            item = result(); target = item
            for key in path[:-1]: target = target[key]
            target[path[-1]] = value
            with self.subTest(path=path), self.assertRaises(m.Reject):
                m.validate_result(item, GEN)

    def test_empty_cgroup_nonzero_worker_exit_and_guard_reactivation_are_required(self):
        for path, value in ((("worker_terminal", "cgroup_populated"), True),
                            (("worker_terminal", "no_successful_completion"), False),
                            (("worker_terminal", "exec_main_status"), 0),
                            (("guard", "armed_absent"), False),
                            (("reactivation", "failure_before_worker_start"), False)):
            item = result(); target = item
            for key in path[:-1]: target = target[key]
            target[path[-1]] = value
            with self.subTest(path=path), self.assertRaises(m.Reject):
                m.validate_result(item, GEN)

    def test_all_numeric_fields_reject_bool_and_float_in_partial_and_final(self):
        numeric_paths = (
            ("worker", "main_pid"), ("worker", "starttime_ticks"),
            ("worker", "uid"), ("worker", "gid"),
            ("owner", "main_pid"), ("owner", "starttime_ticks"),
            ("owner", "uid"), ("owner", "gid"), ("pidfd_signal", "signal"),
            ("owner_terminal", "main_pid"), ("owner_terminal", "exec_main_pid"),
            ("owner_terminal", "exec_main_code"), ("owner_terminal", "exec_main_status"),
            ("worker_terminal", "main_pid"), ("worker_terminal", "exec_main_pid"),
            ("worker_terminal", "exec_main_code"), ("worker_terminal", "exec_main_status"),
        )
        valid_records = (("partial", PartialAdapterControls().partial()), ("final", result()))
        for schema_name, record in valid_records:
            for path in numeric_paths:
                target = record
                for key in path[:-1]: target = target[key]
                original = target[path[-1]]
                for replacement in (bool(original), float(original)):
                    item = copy.deepcopy(record)
                    changed = item
                    for key in path[:-1]: changed = changed[key]
                    changed[path[-1]] = replacement
                    with self.subTest(schema=schema_name, path=path, replacement=type(replacement).__name__), self.assertRaises(m.Reject):
                        (m.validate_partial_observation(item, GEN) if schema_name == "partial"
                         else m.validate_result(item, GEN))
        partial = PartialAdapterControls().partial()
        for replacement in (True, 12000.0):
            item = copy.deepcopy(partial)
            item["admission_remaining_ms_at_trigger"] = replacement
            with self.subTest(schema="partial", field="admission_remaining_ms_at_trigger", replacement=type(replacement).__name__), self.assertRaises(m.Reject):
                m.validate_partial_observation(item, GEN)
        completion = PartialAdapterControls().completion()
        for replacement in (True, 1.0):
            item = copy.deepcopy(completion)
            item["measurement_count"] = replacement
            with self.subTest(schema="completion-extra", replacement=type(replacement).__name__), self.assertRaises(m.Reject):
                m.validate_completion_evidence(item, GEN)

    def test_any_native_or_acceptance_claim_rejects(self):
        for key in ("native_authority", "native_acceptance", "native_credit"):
            item = result(); item[key] = True
            with self.subTest(key=key), self.assertRaises(m.Reject):
                m.validate_result(item, GEN)

    def test_duplicate_json_keys_and_nonfinite_values_reject(self):
        for raw in (b'{"x":1,"x":2}\n', b'{"x":NaN}\n'):
            with self.subTest(raw=raw), self.assertRaises(m.Reject):
                m.decode(raw, 128)


class AdmissionWindowControls(unittest.TestCase):
    def test_original_admission_window_boundary_is_closed(self):
        now = 1_000_000
        self.assertEqual(m.admission_window_remaining(now + 5000, now), 5000)
        self.assertEqual(m.admission_window_remaining(now + 30000, now), 30000)
        for remaining in (4999, 0, -1, 30001):
            with self.subTest(remaining=remaining), self.assertRaises(m.Reject):
                m.admission_window_remaining(now + remaining, now)
        for deadline, trigger in ((True, now), (now + 5000, False)):
            with self.subTest(deadline=deadline, trigger=trigger), self.assertRaises(m.Reject):
                m.admission_window_remaining(deadline, trigger)


class PartialAdapterControls(unittest.TestCase):
    def partial(self):
        complete = result()
        keys = ("case_generation", "phase_sha256", "job_path", "worker", "owner",
                "pidfd_signal", "owner_terminal", "worker_terminal", "guard",
                "observation_only", "native_authority", "native_acceptance", "native_credit")
        value = {key: copy.deepcopy(complete[key]) for key in keys}
        value.update({"schema": m.PARTIAL_SCHEMA, "admission_remaining_ms_at_trigger": 12000})
        suffix=GEN[:28]
        value["account_snapshot"]={"names":{"worker":"evw"+suffix,"subject":"evs"+suffix,"results":"evr"+suffix},
            "ids":{"worker_uid":complete["worker"]["uid"],"worker_gid":complete["worker"]["gid"],
                "subject_uid":1003,"subject_gid":1004,"results_gid":1005}}
        return value

    def completion(self):
        complete = result()
        return {"schema": m.COMPLETION_SCHEMA, "case_generation": GEN,
                "source_commit": m.SOURCE, "source_capture_sha256": m.CAPTURE_SHA256,
                "reactivation": copy.deepcopy(complete["reactivation"])}

    def test_adapter_copies_closed_observation_and_separate_measurements(self):
        partial = self.partial()
        measured = self.completion()
        final = m.assemble_final_record(partial, measured, GEN)
        summary = m.validate_result(final, GEN)
        self.assertEqual(final["reactivation"], measured["reactivation"])
        self.assertEqual(final["source_commit"], measured["source_commit"])
        self.assertEqual(final["source_capture_sha256"], measured["source_capture_sha256"])
        self.assertIs(final["native_authority"], partial["native_authority"])
        self.assertIs(final["native_acceptance"], partial["native_acceptance"])
        self.assertIs(final["native_credit"], partial["native_credit"])
        self.assertNotIn("admission_remaining_ms_at_trigger", final)
        self.assertFalse(summary["native_credit"])

    def test_adapter_rejects_missing_or_mismatched_external_evidence(self):
        cases = []
        item = self.completion(); item.pop("reactivation"); cases.append(item)
        item = self.completion(); item["source_commit"] = "0" * 40; cases.append(item)
        item = self.completion(); item["source_capture_sha256"] = "0" * 64; cases.append(item)
        item = self.completion(); item["case_generation"] = "f" * 32; cases.append(item)
        item = self.completion(); item["reactivation"]["reply_phase_absent"] = False; cases.append(item)
        item = self.completion(); item["native_credit"] = False; cases.append(item)
        for evidence in cases:
            with self.subTest(evidence=evidence), self.assertRaises(m.Reject):
                m.assemble_final_record(self.partial(), evidence, GEN)

    def test_adapter_rejects_partial_without_admission_or_false_flags(self):
        cases = []
        item = self.partial(); item.pop("admission_remaining_ms_at_trigger"); cases.append(item)
        item = self.partial(); item["admission_remaining_ms_at_trigger"] = 4999; cases.append(item)
        item = self.partial(); item.pop("native_credit"); cases.append(item)
        item = self.partial(); item["native_acceptance"] = True; cases.append(item)
        item = self.partial(); item["case_generation"] = "f" * 32; cases.append(item)
        for observation in cases:
            with self.subTest(observation=observation), self.assertRaises(m.Reject):
                m.assemble_final_record(observation, self.completion(), GEN)


if __name__ == "__main__":
    unittest.main(verbosity=2)
