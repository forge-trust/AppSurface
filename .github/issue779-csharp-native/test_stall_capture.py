"""Detached data and owned-file controls; no root or systemd is invoked."""
import base64
import tarfile
import io
import ast
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
    stderr = b"FIXTURE_N11_DISPOSER_ARMED\nFIXTURE_N11_INPUT_FACTORY_ENTERED\n"

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
        "stop_timing": {"schema": "issue779-original-worker-stop-timing-v1", "job_remaining_ms": 200000,
        "stopping_ms": 5000, "worker_runtime_us": 105000000, "containment_started_ms": 0,
        "stop_1_started_ms": 1, "stop_1_replied_ms": 10, "stop_2_started_ms": None,
        "stop_2_replied_ms": None, "unit_and_group_stopped_ms": 200, "monitor_joined_ms": 201,
        "pumps_joined_ms": 202, "final_unit_and_group_sampled_ms": 203,
        "settlement_published_ms": None, "stop_joined_ms": 205},
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
        self.assertEqual(records["worker.stderr"], b"FIXTURE_N11_DISPOSER_ARMED\nFIXTURE_N11_INPUT_FACTORY_ENTERED\n")

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



class LaunchTimingHandoffControls(unittest.TestCase):
    timing_name = "logs/n11-launch-timing.json"

    def _runner_scope(self, source):
        tree = ast.parse(source)
        archive_nodes = []
        caps_node = caps_update = None
        functions = {}
        for node in tree.body:
            if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "ARCHIVE_NAMES" for t in node.targets):
                archive_nodes.append(node.value)
            elif isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == "NEGATIVE_CAPS" for t in node.targets):
                caps_node = node.value
            elif isinstance(node, ast.Expr) and isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Attribute) and isinstance(node.value.func.value, ast.Name) and node.value.func.value.id == "NEGATIVE_CAPS" and node.value.func.attr == "update":
                caps_update = node.value.args[0]
            elif isinstance(node, ast.FunctionDef) and node.name in {"require_data", "unique_pairs", "decode_data", "exact_object", "inspect_retention"}:
                functions[node.name] = node
        archive = set()
        for node in archive_nodes:
            archive.update(x.value for x in ast.walk(node) if isinstance(x, ast.Constant) and isinstance(x.value, str))
        caps = ast.literal_eval(caps_node)
        caps.update(ast.literal_eval(caps_update))
        module = ast.Module(body=[functions[n] for n in ("require_data", "unique_pairs", "decode_data", "exact_object", "inspect_retention")], type_ignores=[])
        scope = {"io": io, "tarfile": tarfile, "json": json, "NEGATIVE_CAPS": caps}
        exec(compile(ast.fix_missing_locations(module), "<extracted-retention-validation>", "exec"), scope)
        return archive, scope["inspect_retention"]

    def _archive(self, timing_bytes):
        files = {self.timing_name: timing_bytes}
        selection = {"schema":"issue779-native-retention-selection-v1", "selection_status":"one-namespace",
            "data_file_count":1, "data_bytes":len(timing_bytes), "missing_fixed_file_count":0}
        files["retention-selection.json"] = (json.dumps(selection,sort_keys=True,separators=(",",":"))+"\n").encode()
        out = io.BytesIO()
        with tarfile.open(fileobj=out,mode="w:",format=tarfile.USTAR_FORMAT) as tar:
            for name,data in sorted(files.items()):
                info=tarfile.TarInfo(name);info.size=len(data);info.mode=0o600;info.uid=info.gid=info.mtime=0
                tar.addfile(info,io.BytesIO(data))
        return out.getvalue()

    def _timing(self):
        return {"schema":"issue779-n11-launch-timing-v1","generation":"1"*32,"source":"2"*40,
            "owner_unit":"appsurface-evidence-owner-"+"1"*32+".service","request_sha256":"6"*64,
            "utc_sample_epoch_seconds":1791590210,"utc_deadline_epoch_seconds":1791590400,
            "boottime_sample_seconds":110,"boottime_deadline_seconds":300,"utc_remaining_seconds":190,
            "boottime_remaining_seconds":190,"fixture_remaining_seconds":300,"selected_runtime_seconds":180,
            "observer_reserve_seconds":5,"stop_reserve_seconds":5,"dispatch_reserve_seconds":5,
            "timeout_stop_seconds":5,"native_authority":False,"native_acceptance":False}

    def test_actual_extracted_archive_function_rejects_old_allowlist_and_accepts_timing(self):
        runner_path=pathlib.Path(__file__).with_name("run-native.py")
        allowed,inspect=self._runner_scope(runner_path.read_text())
        timing=self._timing();raw=(json.dumps(timing,sort_keys=True,separators=(",",":"))+"\n").encode()
        archive=self._archive(raw)
        with self.assertRaises(ValueError):
            inspect(archive,allowed-{self.timing_name})
        self.assertIn(self.timing_name,allowed)
        self.assertEqual(inspect(archive,allowed)[self.timing_name],raw)

    def test_timing_validator_rejects_missing_and_mismatched_provenance(self):
        from check_stall_record import StallRecordRejected,check_launch_timing
        valid=self._timing()
        def check(value):
            raw=(json.dumps(value,sort_keys=True,separators=(",",":"))+"\n").encode()
            return check_launch_timing(raw,expected_generation="1"*32,expected_source="2"*40,
                expected_request_sha256="6"*64,expected_job_deadline_utc="2026-10-10T00:00:00Z")
        self.assertEqual(check(valid)["selected_runtime_seconds"],180)
        for field,bad in (("generation","3"*32),("request_sha256","7"*64),
                          ("selected_runtime_seconds",181),("utc_deadline_epoch_seconds",1791590401)):
            with self.subTest(field=field):
                value=dict(valid);value[field]=bad
                with self.assertRaises(StallRecordRejected):check(value)
        with self.assertRaises(StallRecordRejected):check(None)



class DisposerMarkerDataControls(unittest.TestCase):
    """Joined private marker data; no registered disposer or native process is fabricated."""
    def rewrite_stderr(self, record, data):
        result = copy.deepcopy(record)
        result["pumps"]["stderr"].update(received_bytes=len(data), retained_bytes=len(data),
            sha256=hashlib.sha256(data).hexdigest(), raw_base64=base64.b64encode(data).decode("ascii"))
        result["pumps"]["received_bytes"] = len(data)
        return result

    def test_armed_before_stall_accepts_only_failure_data(self):
        d, r, f = fixture()
        _, consistency, records = parse(d, r, f)
        self.assertEqual(records["worker.stderr"],
            b"FIXTURE_N11_DISPOSER_ARMED\nFIXTURE_N11_INPUT_FACTORY_ENTERED\n")
        self.assertFalse(consistency["physically_settled"])
        self.assertFalse(consistency["native_authority"])

    def test_old_unarmed_or_wrong_order_cannot_supply_disposer_observation(self):
        d, r, f = fixture()
        for data in [b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n",
                     b"FIXTURE_N11_INPUT_FACTORY_ENTERED\nFIXTURE_N11_DISPOSER_ARMED\n",
                     b"FIXTURE_N11_DISPOSER_ARMED\n"]:
            with self.subTest(data=data):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, self.rewrite_stderr(r, data), f)

    def test_duplicate_markers_reject_with_coherent_stream_hashes(self):
        d, r, f = fixture()
        prefix = b"FIXTURE_N11_DISPOSER_ARMED\nFIXTURE_N11_INPUT_FACTORY_ENTERED\n"
        for tail in [b"FIXTURE_N11_DISPOSER_ARMED\n", b"FIXTURE_N11_INPUT_FACTORY_ENTERED\n"]:
            with self.subTest(tail=tail):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, self.rewrite_stderr(r, prefix + tail), f)

    def test_invoked_disposer_rejects_detached_failure_data_even_without_final_newline(self):
        d, r, f = fixture()
        prefix = b"FIXTURE_N11_DISPOSER_ARMED\nFIXTURE_N11_INPUT_FACTORY_ENTERED\n"
        for invoked in (b"FIXTURE_N11_DISPOSER_INVOKED\n", b"FIXTURE_N11_DISPOSER_INVOKED"):
            with self.subTest(final_newline=invoked.endswith(b"\n")):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, self.rewrite_stderr(r, prefix + invoked), f)

    def test_marker_cannot_upgrade_failed_settlement(self):
        d, r, f = fixture()
        r["lifetime"]["physically_settled"] = True
        r["stop_timing"]["settlement_published_ms"] = 204
        with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
            parse(d, r, f)


class StopTimingDataControls(unittest.TestCase):
    def valid_timing(self):
        return {"schema": "issue779-original-worker-stop-timing-v1", "job_remaining_ms": 200000,
            "stopping_ms": 5000, "worker_runtime_us": 105000000,
            "containment_started_ms": 0, "stop_1_started_ms": 1, "stop_1_replied_ms": 10,
            "stop_2_started_ms": None, "stop_2_replied_ms": None,
            "unit_and_group_stopped_ms": 200, "monitor_joined_ms": 201, "pumps_joined_ms": 202,
            "final_unit_and_group_sampled_ms": 203, "settlement_published_ms": None, "stop_joined_ms": 205}

    def test_bounded_failed_stop_is_distinct_from_successful_settlement(self):
        d, record, failure = fixture()
        record["stop_timing"] = self.valid_timing()
        _, consistency, _ = parse(d, record, failure)
        self.assertTrue(consistency["stop_timing"]["present"])
        self.assertFalse(consistency["stop_timing"]["proof"])
        self.assertTrue(consistency["stop_timing"]["bounded_failed_stop"])
        self.assertEqual(consistency["stop_timing"]["failed_stop_missing_fields"], [])
        self.assertEqual(consistency["stop_timing"]["missing_fields"], ["settlement_published_ms"])
        self.assertEqual(consistency["stop_timing"]["clock_origin"], "LinuxWorkerStopTiming.Begin")
        self.assertFalse(consistency["physically_settled"])
        self.assertFalse(consistency["native_authority"])
        self.assertFalse(consistency["native_acceptance"])

    def test_missing_timing_or_failed_stop_milestone_rejects(self):
        d, record, failure = fixture()
        cases = []
        missing = copy.deepcopy(record); del missing["stop_timing"]; cases.append(missing)
        for key in ("stop_joined_ms", "unit_and_group_stopped_ms", "monitor_joined_ms",
                    "pumps_joined_ms", "final_unit_and_group_sampled_ms"):
            candidate = copy.deepcopy(record); candidate["stop_timing"][key] = None; cases.append(candidate)
        expired = copy.deepcopy(record); expired["stop_timing"]["job_remaining_ms"] = 0; cases.append(expired)
        late_job = copy.deepcopy(record); late_job["stop_timing"]["job_remaining_ms"] = 100; cases.append(late_job)
        overbound = copy.deepcopy(record); overbound["stop_timing"]["stop_joined_ms"] = 5001; cases.append(overbound)
        changed_allowance = copy.deepcopy(record); changed_allowance["stop_timing"]["stopping_ms"] = 4000; cases.append(changed_allowance)
        for candidate in cases:
            with self.subTest(timing=candidate.get("stop_timing")):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, candidate, failure)

    def test_bad_timing_values_order_or_signal_claim_reject(self):
        d, original, failure = fixture()
        cases = []
        boolean = self.valid_timing(); boolean["stop_1_started_ms"] = True; cases.append(boolean)
        out_of_order = self.valid_timing(); out_of_order["pumps_joined_ms"] = 9; cases.append(out_of_order)
        partial_second = self.valid_timing(); partial_second["stop_2_started_ms"] = 11; cases.append(partial_second)
        signal_claim = self.valid_timing(); signal_claim["sigterm_delivered_ms"] = 10; cases.append(signal_claim)
        positive_settlement = self.valid_timing(); positive_settlement["settlement_published_ms"] = 204; cases.append(positive_settlement)
        for timing in cases:
            with self.subTest(keys=tuple(sorted(timing))):
                record = copy.deepcopy(original); record["stop_timing"] = timing
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, record, failure)

    def test_missing_eof_or_nonempty_group_rejects_failed_stop_control(self):
        d, original, failure = fixture()
        cases = []
        stdout_eof = copy.deepcopy(original); stdout_eof["pumps"]["stdout"]["eof"] = False; cases.append(stdout_eof)
        stderr_eof = copy.deepcopy(original); stderr_eof["pumps"]["stderr"]["eof"] = False; cases.append(stderr_eof)
        nonempty_group = copy.deepcopy(original)
        nonempty_group["cgroup"].update(exists=True, populated=True, frozen=False,
                                        device_major=0, device_minor=1, inode=55)
        cases.append(nonempty_group)
        pre_pump_group = copy.deepcopy(original); pre_pump_group["cgroup"]["after_pumps"] = False; cases.append(pre_pump_group)
        settled = copy.deepcopy(original); settled["lifetime"]["physically_settled"] = True; cases.append(settled)
        for record in cases:
            with self.subTest(record=record):
                with self.assertRaisesRegex(ValueError, "^stall-capture-rejected$"):
                    parse(d, record, failure)

    def test_actual_retained_v14_failure_record_replays_without_settlement_claim(self):
        fixture_root = pathlib.Path(__file__).resolve().parents[3] / "fixtures" / "n11-v14"
        manifest = json.loads((fixture_root / "manifest.json").read_bytes())
        self.assertEqual(manifest["source_archive"]["sha256"],
                         "6f45fa309bba0e7fe12b038f2052849adfd1ba8400c2aa18b98826846a112bbf")
        expected_member_hashes = {
            "original-failed-settlement.json": "633c6529c46ee0396c0d3c3a96ab3114d266abbfdcecb7013d4fdd2d44542144",
            "worker-control.json": "78af8a6288067fd0a2aeca118eda65acf2876d86d2ae269410cdd4221146aae6",
            "worker.stdout": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "worker.stderr": "2cea2b95fb8a26a3b92d29e64149929dad95e0f2f2c7d700f27d71a6d1166979",
            "root-failure.json": "395108d4fde331b5789d17cc5c8730c97c8b180c4f875462ca1166b1e6a99b2b",
            "original-request.json": "fbe51498bd49824fc3094f1bcda5233fef12edc418efcbf24668dbd67357e9e4",
        }
        raw = {name: (fixture_root / name).read_bytes() for name in expected_member_hashes}
        for name, expected in expected_member_hashes.items():
            self.assertEqual(hashlib.sha256(raw[name]).hexdigest(), expected)
        record = json.loads(raw["original-failed-settlement.json"])
        descriptor = json.loads(raw["worker-control.json"])
        request = json.loads(raw["original-request.json"])
        failure = raw["root-failure.json"].rstrip(b"\n")
        frame = (raw["original-failed-settlement.json"].rstrip(b"\n") + b"\n" + failure
                 + b"\n" + TERMINAL_LINE + b"\n")
        generation = descriptor["run_id"].removeprefix("csharp/")
        runtime_root = f"/var/lib/appsurface-evidence-fixture/{generation}/runtime/"
        runtime_relative_host = descriptor["dotnet_path"].removeprefix(runtime_root)
        result, consistency, records = capture.parse_original(
            frame, raw["worker-control.json"], request=raw["original-request.json"],
            generation=generation, source=descriptor["subject_revision"],
            base=descriptor["base_revision"], entry_sha=descriptor["entry_sha256"],
            policy_sha=descriptor["policy_sha256"], runtime_relative_host=runtime_relative_host,
            workflow=descriptor["workflow_identity"])
        self.assertFalse(consistency["native_authority"])
        self.assertFalse(consistency["native_acceptance"])
        self.assertIs(record["stop_timing"]["settlement_published_ms"], None)
        self.assertIs(record["lifetime"]["failed"], True)
        self.assertIs(record["lifetime"]["physically_settled"], False)
        self.assertEqual(json.loads(failure)["control_failure"]["stage"], "WorkerTerminalTaskCompleted")
        self.assertFalse(consistency["stop_timing"]["proof"])
        self.assertTrue(consistency["stop_timing"]["bounded_failed_stop"])
        self.assertEqual(consistency["stop_timing"]["missing_fields"], ["settlement_published_ms"])
        self.assertEqual(consistency["stop_timing"]["failed_stop_missing_fields"], [])
        self.assertEqual(records["worker.stdout"], raw["worker.stdout"])
        self.assertEqual(records["worker.stderr"], raw["worker.stderr"])


if __name__ == "__main__":
    unittest.main()
