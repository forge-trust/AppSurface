"""Detached grammar and real-file custody controls; no native execution credit."""
import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("n16_records", HERE / "check_n16_records.py")
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
GEN = "1" * 32
SOURCE = "2" * 40
CAPTURE = "3" * 64
BUILD = "4" * 64


def data():
    # Deliberate detached data: these records are not produced by a live root.
    accepted = {"schema": "issue779-accepted-blocked-work-v1", "generation": GEN,
        "case": "N16", "active_workloads": 0, "active_controls": 0,
        "native_authority": False, "native_acceptance": False}
    for key in ("accepted_write_committed", "request_blocked_during_control_overlap",
                "original_body_joined", "original_request_response_committed", "stop_write_committed",
                "positive_wait_write_committed", "exit_write_committed", "handlers_joined"):
        accepted[key] = True
    def stream(raw):
        return {"received_bytes": len(raw), "retained_bytes": len(raw), "discarded_bytes": 0,
                "eof": True, "failure": "None", "sha256": hashlib.sha256(raw).hexdigest()}
    kernel = {"schema": "issue779-negative-kernel-observation-v1", "generation": GEN,
        "worker_unit": "appsurface-evidence-worker-" + GEN + ".service",
        "process": {"pid": 321, "starttime_ticks": 456, "uid4": [1001] * 4, "gid4": [1002] * 4,
                    "control_group": "/system.slice/appsurface-evidence-worker-" + GEN + ".service"},
        "ready": {"committed": True, "descriptor_sha256": "5" * 64},
        "terminal": {"exec_main_pid": 321, "exec_main_code": 1, "exec_main_status": 1,
                     "active_state": "active", "sub_state": "exited"},
        "cgroup": {"exists": False, "populated": None, "frozen": None,
                   "device_major": None, "device_minor": None, "inode": None},
        "pumps": {"stdout": stream(b""), "stderr": stream(m.WORKER_BYTES),
                  "received_bytes": len(m.WORKER_BYTES), "received_byte_limit": 1048576,
                  "failure": "None", "discarded_bytes": 0},
        "joins": dict.fromkeys(("startup", "pending_stop", "monitor", "server", "pumps"), True),
        "observation_only": True, "native_authority": False, "native_acceptance": False}
    failure = {"schema": "evidence-native-observation-failure-v4", "phase": "WorkerCompletion",
        "error_kind": "Admission", "diagnostic_code": "ASEVD410", "account_failure": None,
        "control_failure": None, "custody_failure": None}
    cleanup = {"schema": "issue779-accepted-work-root-cleanup-v1", "generation": GEN,
        "results_gid": 1003, "accounts_closed": True, "root_custody_closed": True,
        "original_owners_closed": True, "observation_only": True,
        "native_authority": False, "native_acceptance": False}
    return [accepted, kernel, failure, cleanup]


def encode(rows=None, message=None):
    rows = data() if rows is None else rows
    message = m.ROOT_MESSAGE if message is None else message
    return ("\n".join(json.dumps(row, separators=(",", ":")) for row in rows)
            + "\n" + message + "\n").encode()


def parse(raw=None, **kwargs):
    return m.validate_bytes(encode() if raw is None else raw, kwargs.get("generation", GEN),
        kwargs.get("root_exit", 1), kwargs.get("source_commit", SOURCE),
        kwargs.get("capture_sha", CAPTURE), kwargs.get("projection_sha", BUILD))


class RecordGrammarControls(unittest.TestCase):
    def reject_change(self, record, path, value):
        rows = data()
        target = rows[record]
        for key in path[:-1]:
            target = target[key]
        target[path[-1]] = value
        with self.assertRaises(m.Reject):
            parse(encode(rows))

    def test_full_fixed_suffix_and_commit40_parse_as_data_only(self):
        result = parse()
        self.assertEqual(SOURCE, result["source_commit"])
        self.assertFalse(result["native_authority"])
        self.assertFalse(result["native_acceptance"])
        self.assertIn("checks remain separate", result["meaning"])
        self.assertEqual(hashlib.sha256(encode()).hexdigest(), result["stderr_sha256"])

    def test_source_commit_is_not_a_sha256(self):
        for value in ("2" * 64, "2" * 39, "Z" * 40, None):
            with self.subTest(value=value), self.assertRaises(m.Reject):
                parse(source_commit=value)

    def test_root_exit_requires_actual_unix_one(self):
        for value in (0, 410, 255, True):
            with self.subTest(value=value), self.assertRaises(m.Reject):
                parse(root_exit=value)

    def test_exact_full_fixed_message_required(self):
        for message in (m.ROOT_MESSAGE.removesuffix(m.FIX), m.ROOT_MESSAGE + " canary",
                        "ASEVD410: canary", ""):
            with self.subTest(message=message), self.assertRaises(m.Reject):
                parse(encode(message=message))

    def test_all_four_record_order_and_final_cleanup_required(self):
        for rows in (data()[:3], list(reversed(data())), data() + [data()[0]]):
            with self.subTest(count=len(rows)), self.assertRaises(m.Reject):
                parse(encode(rows))

    def test_all_past_commits_and_original_joins_required(self):
        for key in data()[0]:
            if data()[0][key] is True:
                with self.subTest(event=key): self.reject_change(0, (key,), False)
        for key in data()[1]["joins"]:
            with self.subTest(join=key): self.reject_change(1, ("joins", key), False)
        for key in ("accounts_closed", "root_custody_closed", "original_owners_closed"):
            with self.subTest(cleanup=key): self.reject_change(3, (key,), False)

    def test_bool_not_an_integer_and_generation_bound_in_each_record(self):
        for i in (0, 1, 3):
            with self.subTest(record=i): self.reject_change(i, ("generation",), "9" * 32)
        for i, path in ((0, ("active_workloads",)), (1, ("process", "pid")),
                        (1, ("process", "starttime_ticks")), (1, ("terminal", "exec_main_code")),
                        (3, ("results_gid",))):
            with self.subTest(path=path): self.reject_change(i, path, True)

    def test_all_uid_gid_values_and_generated_group_are_bound(self):
        for key in ("uid4", "gid4"):
            for value in ([1001, 1001, 0, 1001], [1001, 1001, 1001, 1002],
                          [1001] * 3, [True] * 4, "canary"):
                with self.subTest(key=key, value=value): self.reject_change(1, ("process", key), value)
        self.reject_change(1, ("process", "control_group"), "/system.slice/foreign.service")
        rows = data(); rows[1]["cgroup"]["symlink"] = False
        with self.assertRaises(m.Reject): parse(encode(rows))
        self.reject_change(1, ("worker_unit",), "foreign.service")
        self.reject_change(3, ("results_gid",), 1002)

    def test_ready_is_committed_and_descriptor_digest_bounded(self):
        self.reject_change(1, ("ready", "committed"), False)
        for value in ("canary", "5" * 63, ["5" * 64]):
            with self.subTest(value=value): self.reject_change(1, ("ready", "descriptor_sha256"), value)

    def test_worker_exit_code_not_diagnostic_code_and_pid_is_original(self):
        self.reject_change(1, ("terminal", "exec_main_status"), 410)
        self.reject_change(1, ("terminal", "exec_main_pid"), 322)
        self.reject_change(1, ("terminal", "active_state"), "activating")

    def test_absent_pruned_group_exact_null_shape_is_valid_data_only(self):
        result = parse()
        self.assertFalse(result["native_authority"])
        self.assertFalse(result["native_acceptance"])
        rows = data()
        rows[1]["cgroup"] = {"exists": True, "populated": False, "frozen": False,
                              "device_major": 0, "device_minor": 29, "inode": 789}
        self.assertFalse(parse(encode(rows))["native_acceptance"])

    def test_absent_group_rejects_each_invented_or_mixed_field(self):
        for field, value in (("populated", False), ("frozen", False),
                             ("device_major", 0), ("device_minor", 29), ("inode", 789)):
            with self.subTest(field=field):
                self.reject_change(1, ("cgroup", field), value)
        rows = data()
        rows[1]["cgroup"].update(populated=False, frozen=False,
                                  device_major=0, device_minor=29, inode=789)
        with self.assertRaises(m.Reject):
            parse(encode(rows))

    def test_absent_group_cannot_bypass_stream_joins_or_owner_cleanup(self):
        self.assertFalse(parse()["native_acceptance"])
        for record, path, value in (
                (1, ("pumps", "stdout", "eof"), False),
                (1, ("joins", "pumps"), False),
                (3, ("accounts_closed",), False)):
            with self.subTest(path=path):
                self.reject_change(record, path, value)

    def test_live_group_or_unknown_kernel_values_rejected(self):
        for path, value in (("populated", True), ("frozen", True), ("exists", 1),
                            ("inode", 0), ("device_major", -1), ("device_minor", True)):
            with self.subTest(path=path): self.reject_change(1, ("cgroup", path), value)

    def test_pumps_require_exact_expected_worker_error_and_two_eofs(self):
        for stream in ("stdout", "stderr"):
            for key, value in (("eof", False), ("failure", "Read"), ("discarded_bytes", 1),
                               ("received_bytes", 1), ("sha256", "9" * 64)):
                with self.subTest(stream=stream, key=key): self.reject_change(1, ("pumps", stream, key), value)
        self.reject_change(1, ("pumps", "received_bytes"), 0)
        self.reject_change(1, ("pumps", "received_byte_limit"), 16 * 1024 * 1024 + 1)
        self.reject_change(1, ("pumps", "received_byte_limit"), 1)

    def test_failure_is_closed_intentional_admission_not_unknown_error(self):
        for key, value in (("phase", "canary"), ("error_kind", "Unknown"),
                           ("diagnostic_code", "ASEVD420"), ("account_failure", {"raw": "canary"}),
                           ("control_failure", {"raw": "canary"}), ("custody_failure", {"raw": "canary"})):
            with self.subTest(key=key): self.reject_change(2, (key,), value)

    def test_schema_unknown_field_and_duplicate_members_rejected(self):
        rows = data(); rows[0]["raw_canary"] = "secret"
        with self.assertRaises(m.Reject): parse(encode(rows))
        raw = encode().replace(b'"case":"N16"', b'"case":"N16","case":"N16"', 1)
        with self.assertRaises(m.Reject): parse(raw)
        raw = encode().replace(b'"active_workloads":0', b'"active_workloads":NaN', 1)
        with self.assertRaises(m.Reject): parse(raw)

    def test_authority_and_acceptance_cannot_be_claimed_by_any_record(self):
        for i in (0, 1, 3):
            for key in ("native_authority", "native_acceptance"):
                with self.subTest(record=i, key=key): self.reject_change(i, (key,), True)

    def test_raw_bounds_utf8_nul_cr_and_terminal_lf(self):
        for value in (encode()[:-1], encode().replace(b"\n", b"\r\n"), b"\xff\n",
                      b"\0" + encode(), b"x" * (m.MAX_STDERR + 1), b""):
            with self.subTest(length=len(value)), self.assertRaises(m.Reject): parse(value)


class RetainedFileControls(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="n16-records-")
        self.addCleanup(self.temp.cleanup)
        self.file = Path(self.temp.name) / "stderr"
        self.file.write_bytes(encode()); self.file.chmod(0o600)

    def read(self):
        return m.read_stderr(self.file, expected_owner_uid=os.getuid())

    def test_owned_portable_file_matches_original_bytes(self):
        self.assertEqual(encode(), self.read())

    def test_public_mode_hardlink_symlink_and_fifo_rejected(self):
        self.file.chmod(0o644)
        with self.assertRaises(m.Reject): self.read()
        self.file.chmod(0o600)
        link = self.file.with_name("hardlink"); os.link(self.file, link)
        with self.assertRaises(m.Reject): self.read()
        link.unlink(); self.file.unlink(); self.file.symlink_to(link)
        with self.assertRaises((m.Reject, FileNotFoundError)): self.read()
        self.file.unlink(); os.mkfifo(self.file, 0o600)
        with self.assertRaises(m.Reject): self.read()

    def test_wrong_owner_and_oversize_reject_before_open(self):
        with patch.object(m.os, "open", side_effect=AssertionError("must reject before open")):
            with self.assertRaises(m.Reject):
                m.read_stderr(self.file, expected_owner_uid=(os.getuid() + 1))
        self.file.write_bytes(b"x" * (m.MAX_STDERR + 1))
        with patch.object(m.os, "open", side_effect=AssertionError("must reject before open")):
            with self.assertRaises(m.Reject): self.read()

    def test_actual_path_substitution_is_detected_after_retained_fd_read(self):
        read = os.read; moved = self.file.with_name("old")
        changed = False
        def replace(fd, count):
            nonlocal changed
            result = read(fd, count)
            if not changed:
                self.file.rename(moved); self.file.write_bytes(encode()); self.file.chmod(0o600)
                changed = True
            return result
        with patch.object(m.os, "read", side_effect=replace), self.assertRaises(m.Reject):
            self.read()

    def test_read_error_still_closes_original_fd(self):
        close = os.close
        with patch.object(m.os, "close", wraps=close) as closed:
            with patch.object(m.os, "read", side_effect=OSError("private canary")):
                with self.assertRaises(OSError): self.read()
            closed.assert_called_once()


if __name__ == "__main__":
    unittest.main()
