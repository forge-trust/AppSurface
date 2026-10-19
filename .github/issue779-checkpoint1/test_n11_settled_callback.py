"""Detached N11 callback-fail-stop consistency controls; no native execution."""
import base64
import copy
import hashlib
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parent
N11 = ROOT / "procedures/n11"
sys.path.insert(0, str(N11))
import check_stall_record as checker
import root_stall_frame
import stall_capture

GENERATION = "0123456789abcdef0123456789abcdef"
PID, UID, GID = 2401, 1501, 1601
DESCRIPTOR_SHA = "d" * 64
SOURCE = "a" * 40
BASE = "b" * 40
ENTRY_SHA = "c" * 64
POLICY_SHA = "e" * 64
WORKER_UNIT = "appsurface-evidence-worker-" + GENERATION + ".service"
FATAL = (checker.ARMED + checker.ENTERED + b"Process terminated. "
         + checker.CALLBACK_FAIL_FAST + b"\n"
         b"at EvidenceWorkerExecution.StopAndJoinAsync\n"
         b"at EvidenceWorkerExecution.WaitWithinStopDeadlineAsync\n"
         b"at EvidenceWorkerExecution.FatalTermination\n")


def stream(raw):
    return {"received_bytes": len(raw), "retained_bytes": len(raw), "discarded_bytes": 0,
            "eof": True, "failure": "None", "sha256": hashlib.sha256(raw).hexdigest(),
            "raw_base64": base64.b64encode(raw).decode("ascii")}


def timing(*, published=80, joined=90, job=180000):
    return {"schema": "issue779-original-worker-stop-timing-v1", "job_remaining_ms": job,
            "stopping_ms": 5000, "worker_runtime_us": 160000000,
            "containment_started_ms": 10, "stop_1_started_ms": 20,
            "stop_1_replied_ms": 30, "stop_2_started_ms": None,
            "stop_2_replied_ms": None, "unit_and_group_stopped_ms": 40,
            "monitor_joined_ms": 50, "pumps_joined_ms": 60,
            "final_unit_and_group_sampled_ms": 70,
            "settlement_published_ms": published, "stop_joined_ms": joined}


def record(*, stderr=FATAL, stdout=b"", settled=True):
    unit = WORKER_UNIT
    return {"schema": "issue779-n11-original-failed-settlement-v1",
            "generation": GENERATION, "worker_unit": unit,
            "process": {"pid": PID, "starttime_ticks": 987654321, "uid4": [UID] * 4,
                        "gid4": [GID] * 4, "control_group": "/system.slice/" + unit},
            "ready": {"committed": True, "descriptor_sha256": DESCRIPTOR_SHA},
            "lifetime": {"startup_joined": True, "stop_joined": True,
                         "failed": not settled, "physically_settled": settled},
            "pending_start": {"start_reserved": True, "started": True, "start_joined": True,
                              "closed": True, "stop_joined": True, "first_failure": "None"},
            "monitor": "Completed" if settled else "Faulted",
            "terminal": {"exec_main_pid": PID, "exec_main_code": 3 if settled else 1,
                         "exec_main_status": 6 if settled else 1},
            "cgroup": {"exists": True, "populated": False, "frozen": False,
                       "device_major": 0, "device_minor": 0, "inode": 1234,
                       "after_pumps": True},
            "pumps": {"joined": True, "stdout": stream(stdout), "stderr": stream(stderr),
                      "received_bytes": len(stdout) + len(stderr), "received_byte_limit": 16777216,
                      "failure": "None", "discarded_bytes": 0, "quota_exceeded": False,
                      "stop_signal_failed": False, "export_complete": True},
            "observation_only": True, "native_authority": False, "native_acceptance": False,
            "stop_timing": timing(published=80 if settled else None)}


def request_bytes():
    value = {"schema": "evidence-supervisor-linux-v1", "mode": "observation",
             "tool_root": "/var/lib/appsurface-evidence-fixture/" + GENERATION + "/tool",
             "runtime_root": "/var/lib/appsurface-evidence-fixture/" + GENERATION + "/runtime",
             "runtime_host": "dotnet", "entry_path": "ForgeTrust.AppSurface.Cli.dll",
             "policy_file": "/var/lib/appsurface-evidence-fixture/" + GENERATION + "/tool/fixture-policy.json",
             "subject_root": "/var/lib/appsurface-evidence-fixture/" + GENERATION + "/subject",
             "base_revision": BASE, "subject_revision": SOURCE, "workflow_identity": "N11-test-workflow",
             "paths": ["docs/designs/issue-779-csharp-supervision-core.md"],
             "observation_profile_ids": ["empty"], "observation_producer_ids": [],
             "job_deadline_utc": "2026-11-01T00:00:00Z", "admission_seconds": 10,
             "start_seconds": 30, "collection_seconds": 10, "cleanup_seconds": 60,
             "stopping_seconds": 5}
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def descriptor_bytes(request):
    dpath = "/run/appsurface-evidence-" + GENERATION
    deploy = "/var/lib/appsurface-evidence-fixture/" + GENERATION
    value = {"schema": "evidence-worker-linux-v1", "run_id": "csharp/" + GENERATION,
             "worker_pid": PID, "broker_pid": PID + 1, "worker_uid": UID, "worker_gid": GID,
             "subject_uid": UID + 1, "subject_gid": GID + 1, "unit": WORKER_UNIT,
             "cgroup": "/system.slice/" + WORKER_UNIT,
             "job_deadline_utc": "2026-11-01T00:00:00Z", "tool_root": deploy + "/tool",
             "subject_root": deploy + "/subject", "output_parent": dpath + "/output",
             "output_slot": "evidence", "dotnet_path": deploy + "/runtime/dotnet",
             "test_output_root": dpath + "/raw-results", "policy_file": deploy + "/tool/fixture-policy.json",
             "mode": "observation", "socket_path": dpath + "/worker/broker/control.sock",
             "descriptor_path": dpath + "/worker/worker-control.json", "entry_sha256": ENTRY_SHA,
             "policy_sha256": POLICY_SHA, "base_revision": BASE, "subject_revision": SOURCE,
             "workflow_identity": "N11-test-workflow", "provider": "github-actions",
             "platform": "linux-x64", "proof_digest": "",
             "output_parent_identity": {"device_major": 0, "device_minor": 0,
                                        "inode": 4321, "uid": UID, "gid": GID},
             "observation_profile_ids": ["empty"], "observation_producer_ids": [],
             "paths": ["docs/designs/issue-779-csharp-supervision-core.md"],
             "admission_seconds": 10, "start_seconds": 30, "collection_seconds": 10,
             "cleanup_seconds": 60, "stopping_seconds": 5,
             "diff_file": None, "diff_sha256": None, "solution": None}
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def framed(record_value, failure=None):
    failure = failure or {"schema": "evidence-native-observation-failure-v4",
        "phase": "ServerRun", "error_kind": "Admission", "diagnostic_code": "ASEVD410",
        "account_failure": None,
        "control_failure": {"stage": "WorkerTerminalTaskCompleted", "operation": None,
                            "error_kind": "Unknown", "diagnostic_code": None},
        "custody_failure": None}
    return (json.dumps(record_value, sort_keys=True, separators=(",", ":")).encode() + b"\n" +
            json.dumps(failure, sort_keys=True, separators=(",", ":")).encode() + b"\n" +
            root_stall_frame.TERMINAL_LINE + b"\n")


class N11SettledCallbackTests(unittest.TestCase):
    def arguments(self, *, stderr=FATAL, stdout=b""):
        raw = json.dumps(record(stderr=stderr, stdout=stdout), sort_keys=True,
                         separators=(",", ":")).encode() + b"\n"
        return dict(worker_stdout=stdout, worker_stderr=stderr,
                    expected_generation=GENERATION, expected_pid=PID,
                    expected_starttime_ticks=987654321, expected_uid=UID, expected_gid=GID,
                    expected_descriptor_sha256=DESCRIPTOR_SHA, expected_stopping_ms=5000), raw

    def test_original_frame_selects_closed_settled_schema_and_preserves_bytes(self):
        request = request_bytes()
        descriptor = descriptor_bytes(request)
        value = record()
        value["ready"]["descriptor_sha256"] = hashlib.sha256(descriptor).hexdigest()
        frame = framed(value)
        _, result, retained = stall_capture.parse_original(frame, descriptor, request=request,
            generation=GENERATION, source=SOURCE, base=BASE, entry_sha=ENTRY_SHA,
            policy_sha=POLICY_SHA, runtime_relative_host="dotnet", workflow="N11-test-workflow")
        self.assertEqual("issue779-n11-settled-callback-failure-data-v1", result["schema"])
        self.assertEqual("worker lifecycle fail-stop, then root physical settlement", result["outcome"])
        self.assertTrue(result["callback_failed"])
        self.assertEqual(80, result["settlement_published_ms"])
        self.assertFalse(result["native_acceptance"])
        self.assertIs(retained["root.stderr"], frame)

    def test_retained_settled_frame_rejects_start_failed_pending_start(self):
        request = request_bytes()
        descriptor = descriptor_bytes(request)
        changed = record()
        changed["ready"]["descriptor_sha256"] = hashlib.sha256(descriptor).hexdigest()
        changed["pending_start"]["first_failure"] = "StartFailed"
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            stall_capture.parse_original(framed(changed), descriptor, request=request,
                generation=GENERATION, source=SOURCE, base=BASE, entry_sha=ENTRY_SHA,
                policy_sha=POLICY_SHA, runtime_relative_host="dotnet", workflow="N11-test-workflow")

    def test_legacy_api_accepts_only_its_original_failed_false_settlement_shape(self):
        legacy = record(settled=False, stderr=checker.ARMED + checker.ENTERED)
        raw = json.dumps(legacy, sort_keys=True, separators=(",", ":")).encode() + b"\n"
        kwargs = dict(worker_stdout=b"", worker_stderr=checker.ARMED + checker.ENTERED,
            expected_generation=GENERATION, expected_pid=PID, expected_starttime_ticks=987654321,
            expected_uid=UID, expected_gid=GID, expected_descriptor_sha256=DESCRIPTOR_SHA,
            expected_stopping_ms=5000)
        self.assertEqual("issue779-n11-detached-data-consistency-v1",
                         checker.check_stall_record(raw, **kwargs)["schema"])
        settled_args, settled_raw = self.arguments()
        with self.assertRaises(checker.StallRecordRejected):
            checker.check_stall_record(settled_raw, **settled_args)
        with self.assertRaises(checker.StallRecordRejected):
            checker.check_callback_failed_settled_record(raw, **kwargs)

    def test_callback_branch_rejects_each_changed_contract_fact(self):
        cases = []
        for field, value in (("failed", True), ("physically_settled", False),
                             ("stop_joined", False)):
            changed = record(); changed["lifetime"][field] = value
            cases.append((changed, FATAL, b""))
        changed = record(); changed["pending_start"]["first_failure"] = "StartFailed"
        cases.append((changed, FATAL, b""))
        changed = record(); changed["monitor"] = "Faulted"
        cases.append((changed, FATAL, b""))
        changed = record(); changed["terminal"]["exec_main_code"] = 1
        cases.append((changed, FATAL, b""))
        changed = record(); changed["terminal"]["exec_main_status"] = 15
        cases.append((changed, FATAL, b""))
        for field, value in (("job_remaining_ms", 0), ("settlement_published_ms", None),
                             ("stop_joined_ms", 5001)):
            changed = record(); changed["stop_timing"][field] = value
            cases.append((changed, FATAL, b""))
        for token in (checker.CALLBACK_FAIL_FAST, *checker.CALLBACK_FAIL_STACK,
                      checker.ARMED, checker.ENTERED):
            cases.append((record(), FATAL.replace(token, b"removed"), b""))
        changed = record(); changed["pumps"]["quota_exceeded"] = True
        cases.append((changed, FATAL, b""))
        changed = record(); changed["cgroup"]["populated"] = True
        cases.append((changed, FATAL, b""))
        cases.append((record(), FATAL, b"unexpected stdout"))
        for value, stderr, stdout in cases:
            raw = json.dumps(value, sort_keys=True, separators=(",", ":")).encode() + b"\n"
            kwargs = dict(worker_stdout=stdout, worker_stderr=stderr,
                expected_generation=GENERATION, expected_pid=PID,
                expected_starttime_ticks=987654321, expected_uid=UID, expected_gid=GID,
                expected_descriptor_sha256=DESCRIPTOR_SHA, expected_stopping_ms=5000)
            with self.subTest(lifetime=value["lifetime"], monitor=value["monitor"],
                              terminal=value["terminal"], stderr=stderr[-24:]):
                with self.assertRaises(checker.StallRecordRejected):
                    checker.check_callback_failed_settled_record(raw, **kwargs)

    def test_root_failure_selection_requires_exact_source_owned_context(self):
        value = record()
        good = {"phase": "ServerRun", "error_kind": "Admission", "diagnostic_code": "ASEVD410",
                "account_failure": None, "custody_failure": None,
                "control_failure": {"stage": "WorkerTerminalTaskCompleted", "operation": None,
                                    "error_kind": "Unknown", "diagnostic_code": None}}
        self.assertTrue(stall_capture.callback_failed_settled_branch(value, good))
        for field, bad in (("phase", "WorkerExit"), ("error_kind", "Unknown"),
                           ("diagnostic_code", None)):
            changed = dict(good); changed[field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError):
                stall_capture.callback_failed_settled_branch(value, changed)
        control = dict(good); control["control_failure"] = dict(good["control_failure"], stage="HandlerJoin")
        with self.assertRaises(ValueError):
            stall_capture.callback_failed_settled_branch(value, control)
        self.assertFalse(stall_capture.callback_failed_settled_branch(
            record(settled=False), {"phase": "WorkerExit"}))


if __name__ == "__main__":
    unittest.main()
