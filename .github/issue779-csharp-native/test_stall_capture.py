"""Detached data and owned-file controls; no root or systemd is invoked."""
import base64
import copy
import hashlib
import json
import os
import pathlib
import stat
import tempfile
import unittest
from unittest import mock

import stall_capture as capture
from root_stall_frame import TERMINAL_LINE

G = "1" * 32
SOURCE = "2" * 40
BASE = "3" * 40
ENTRY = "4" * 64
POLICY = "5" * 64
WORKFLOW = "closed-N11-data-fixture"
ROOT = "/run/appsurface-evidence-" + G
DEPLOYMENT = "/var/lib/appsurface-evidence-fixture/" + G
UNIT = "appsurface-evidence-worker-" + G + ".service"


def encode(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def fixture():
    descriptor = {"schema": "evidence-worker-linux-v1", "run_id": "csharp/" + G,
        "worker_pid": 100, "broker_pid": 101, "worker_uid": 500, "worker_gid": 501,
        "subject_uid": 502, "subject_gid": 503, "unit": UNIT, "cgroup": "/system.slice/" + UNIT,
        "job_deadline_utc": "2026-10-10T00:40:00Z", "tool_root": DEPLOYMENT + "/tool",
        "subject_root": DEPLOYMENT + "/subject", "output_parent": ROOT + "/output",
        "output_slot": "evidence", "dotnet_path": DEPLOYMENT + "/runtime/dotnet",
        "test_output_root": ROOT + "/raw-results", "policy_file": DEPLOYMENT + "/tool/fixture-policy.json",
        "mode": "observation", "socket_path": ROOT + "/worker/broker/control.sock",
        "descriptor_path": ROOT + "/worker/worker-control.json", "entry_sha256": ENTRY,
        "policy_sha256": POLICY, "base_revision": BASE, "subject_revision": SOURCE,
        "workflow_identity": WORKFLOW, "provider": "github-actions", "platform": "linux-x64",
        "proof_digest": "", "output_parent_identity": {"device_major": 0, "device_minor": 1,
        "inode": 1000, "uid": 500, "gid": 501}, "observation_profile_ids": ["empty"],
        "observation_producer_ids": [], "paths": ["docs/designs/issue-779-csharp-supervision-core.md"],
        "admission_seconds": 10, "start_seconds": 30, "collection_seconds": 10,
        "cleanup_seconds": 60, "stopping_seconds": 5, "diff_file": None, "diff_sha256": None,
        "solution": None}
    stdout = b""
    stderr = b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n"

    def stream(raw):
        return {"received_bytes": len(raw), "retained_bytes": len(raw), "discarded_bytes": 0,
            "eof": True, "failure": "None", "sha256": hashlib.sha256(raw).hexdigest(),
            "raw_base64": base64.b64encode(raw).decode("ascii")}

    record = {"schema": "issue779-n11-original-failed-settlement-v1", "generation": G,
        "worker_unit": UNIT, "process": {"pid": 100, "starttime_ticks": 200,
        "uid4": [500] * 4, "gid4": [501] * 4, "control_group": "/system.slice/" + UNIT},
        "ready": {"committed": True, "descriptor_sha256": hashlib.sha256(encode(descriptor)).hexdigest()},
        "lifetime": {"startup_joined": True, "stop_joined": True, "failed": True, "physically_settled": False},
        "pending_start": {"start_reserved": True, "started": True, "start_joined": True, "closed": True,
        "stop_joined": True, "first_failure": "None"}, "monitor": "Faulted",
        "terminal": {"exec_main_pid": 100, "exec_main_code": 3, "exec_main_status": 6},
        "cgroup": {"exists": False, "populated": None, "frozen": None,
        "device_major": None, "device_minor": None, "inode": None, "after_pumps": True},
        "pumps": {"joined": True, "stdout": stream(stdout), "stderr": stream(stderr),
        "received_bytes": len(stderr), "received_byte_limit": 65536, "failure": "None",
        "discarded_bytes": 0, "quota_exceeded": False, "stop_signal_failed": False, "export_complete": True},
        "observation_only": True, "native_authority": False, "native_acceptance": False}
    failure = {"schema": "evidence-native-observation-failure-v4", "phase": "ServerRun",
        "error_kind": "Admission", "diagnostic_code": "ASEVD410", "account_failure": None,
        "control_failure": None, "custody_failure": None}
    return descriptor, record, failure


def request_fixture():
    descriptor, _, _ = fixture()
    result = {name: descriptor[name] for name in capture.REQUEST_KEYS
              if name in descriptor}
    result.update(schema="evidence-supervisor-linux-v1", runtime_root=DEPLOYMENT + "/runtime",
                  runtime_host=DEPLOYMENT + "/runtime/dotnet", entry_path=DEPLOYMENT + "/tool/AppSurface.Cli.dll")
    return result


def parse(d, r, f, **changes):
    expected = {"generation": G, "source": SOURCE, "base": BASE, "entry_sha": ENTRY,
        "policy_sha": POLICY, "runtime_relative_host": "dotnet", "workflow": WORKFLOW,
        "request": encode(request_fixture())}
    expected.update(changes)
    return capture.parse_original(encode(r) + b"\n" + encode(f) + b"\n" + TERMINAL_LINE + b"\n",
                                 encode(d), **expected)


class StallCaptureDataControls(unittest.TestCase):
    def test_complete_original_failure_remains_failure_without_authority(self):
        d, r, f = fixture()
        self.assertEqual(set(d), capture.DESCRIPTOR_KEYS)
        result, consistency, records = parse(d, r, f)
        self.assertEqual(result, d)
        self.assertFalse(consistency["physically_settled"])
        self.assertFalse(consistency["native_authority"])
        self.assertFalse(consistency["native_acceptance"])
        self.assertEqual(set(records), capture.ORIGINAL_RECORD_NAMES)
        self.assertEqual(records["original-request.json"], encode(request_fixture()))
        self.assertEqual(records["worker.stdout"], b"")
        self.assertEqual(records["worker.stderr"], b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n")

    def test_binding_changes_reject_with_no_input_echo(self):
        original, r, f = fixture()
        for key, value in (("broker_pid", 100), ("worker_uid", 502), ("subject_revision", "6" * 40),
                           ("descriptor_path", ROOT + "/canary"), ("dotnet_path", "/canary/dotnet"),
                           ("workflow_identity", "canary"), ("cleanup_seconds", True),
                           ("proof_digest", "6" * 64)):
            with self.subTest(key=key):
                d = copy.deepcopy(original)
                d[key] = value
                changed_record = copy.deepcopy(r)
                changed_record["ready"]["descriptor_sha256"] = hashlib.sha256(encode(d)).hexdigest()
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$") as error:
                    parse(d, changed_record, f)
                self.assertIsNone(error.exception.__cause__)

    def test_failed_original_join_or_truncated_stream_cannot_be_replaced(self):
        d, original, f = fixture()
        for section, name, value in (("lifetime", "stop_joined", False),
                                     ("pending_start", "closed", False),
                                     ("pumps", "export_complete", False),
                                     ("terminal", "exec_main_code", 0)):
            with self.subTest(section=section, name=name):
                r = copy.deepcopy(original)
                r[section][name] = value
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, r, f)
        r = copy.deepcopy(original)
        r["pumps"]["stderr"]["raw_base64"] = base64.b64encode(b"canary").decode()
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            parse(d, r, f)

    def test_arbitrary_diagnostic_or_context_cannot_pass(self):
        d, r, original = fixture()
        for name, value in (("phase", "canary"), ("custody_failure", {}), ("raw", "canary")):
            with self.subTest(name=name):
                f = copy.deepcopy(original)
                f[name] = value
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, r, f)
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            parse(d, r, original, runtime_relative_host="../dotnet")


class DeadlineProvenanceControls(unittest.TestCase):
    def test_same_requested_instant_accepts_utc_renderings_without_authority(self):
        d, r, f = fixture()
        request = request_fixture()
        request["job_deadline_utc"] = "2026-10-10T00:40:00.0000000+00:00"
        _, consistency, _ = parse(d, r, f, request=encode(request))
        self.assertFalse(consistency["native_authority"])
        self.assertFalse(consistency["native_acceptance"])

    def test_one_tick_request_difference_rejects_before_capture(self):
        d, r, f = fixture()
        request = request_fixture()
        request["job_deadline_utc"] = "2026-10-10T00:40:00.0000001Z"
        with mock.patch.object(capture, "NativeCapture") as native:
            with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                parse(d, r, f, request=encode(request))
            native.assert_not_called()

    def test_malformed_or_non_utc_requested_deadline_rejects_without_echo(self):
        d, r, f = fixture()
        for value in ("canary", "2026-02-30T00:40:00Z", "2026-10-10T00:40:00+01:00", True):
            with self.subTest(value=value):
                request = request_fixture()
                request["job_deadline_utc"] = value
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$") as error:
                    parse(d, r, f, request=encode(request))
                self.assertIsNone(error.exception.__cause__)

    def test_missing_extra_duplicate_or_wrong_request_schema_rejects(self):
        d, r, f = fixture()
        original = request_fixture()
        candidates = []
        missing = copy.deepcopy(original); del missing["job_deadline_utc"]
        extra = copy.deepcopy(original); extra["raw_canary"] = "canary"
        wrong = copy.deepcopy(original); wrong["schema"] = "canary"
        candidates.extend(encode(value) for value in (missing, extra, wrong))
        candidates.append(encode(original)[:-1] + b',"job_deadline_utc":"canary"}')
        for raw in candidates:
            with self.subTest(raw_hash=hashlib.sha256(raw).hexdigest()):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, r, f, request=raw)


class OwnedFileControls(unittest.TestCase):
    def owner(self):
        c = capture.NativeCapture(1000)
        c.check = lambda: None  # File-only portable seam; no deadline/native claim.
        return c

    def test_expired_original_end_rejects_before_file_operation(self):
        c = capture.NativeCapture(1000)
        with mock.patch.object(capture.time, "CLOCK_BOOTTIME", 0, create=True), \
                mock.patch.object(capture.time, "clock_gettime", return_value=1.0), \
                mock.patch.object(capture.os, "stat") as stat_call, \
                mock.patch.object(capture.os, "open") as open_call:
            with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                c.read(123, "worker.stderr", 0, 0, 0o600, 1)
            stat_call.assert_not_called()
            open_call.assert_not_called()
        self.assertEqual(c.fds, [])

    def test_actual_binary_fd_read_and_exclusive_private_write(self):
        with tempfile.TemporaryDirectory() as directory:
            root = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            c = self.owner()
            c.fds.append(root)
            try:
                raw = bytes(range(256))
                c.write(root, "worker.stderr", raw)
                p = pathlib.Path(directory) / "worker.stderr"
                self.assertEqual(stat.S_IMODE(p.stat().st_mode), 0o600)
                # BSD files inherit their parent directory's group, including a
                # root-group /tmp parent. Bind this owned fixture to that actual
                # retained parent; native root-capture expectations stay fixed.
                expected_gid = os.fstat(root).st_gid
                self.assertEqual(p.stat().st_uid, os.getuid())
                self.assertEqual(p.stat().st_gid, expected_gid)
                self.assertEqual(c.read(root, "worker.stderr", os.getuid(), expected_gid, 0o600, 256), raw)
                with self.assertRaises(FileExistsError):
                    c.write(root, "worker.stderr", b"canary")
                self.assertEqual(p.read_bytes(), raw)
            finally:
                c.close()
            self.assertEqual(c.fds, [])

    def test_real_links_modes_and_length_reject_before_read(self):
        with tempfile.TemporaryDirectory() as directory:
            p = pathlib.Path(directory) / "held"
            p.write_bytes(b"original")
            p.chmod(0o600)
            root = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            c = self.owner()
            c.fds.append(root)
            try:
                os.link(p, pathlib.Path(directory) / "hard")
                os.symlink("held", pathlib.Path(directory) / "link")
                for name in ("held", "hard", "link"):
                    with self.subTest(name=name), self.assertRaises(ValueError):
                        c.read(root, name, os.getuid(), os.getgid(), 0o600, 8)
                (pathlib.Path(directory) / "hard").unlink()
                p.chmod(0o644)
                with self.assertRaises(ValueError):
                    c.read(root, "held", os.getuid(), os.getgid(), 0o600, 8)
                p.chmod(0o600)
                with self.assertRaises(ValueError):
                    c.read(root, "held", os.getuid(), os.getgid(), 0o600, 7)
                self.assertEqual(p.read_bytes(), b"original")
            finally:
                c.close()


if __name__ == "__main__":
    unittest.main()
