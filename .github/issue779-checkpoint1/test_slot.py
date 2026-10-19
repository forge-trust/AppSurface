"""Actual private archive-file controls; no fake native record or owner is issued."""

import importlib.util
import copy
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("c1_slot_bytes", Path(__file__).parent / "procedures/slot/bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)


class SlotArchiveOutputControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-slot-archive-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.root.chmod(0o700)
        self.output = self.root / "native-private.tar"

    def test_raw_archive_larger_than_generic_leaf_cap_is_copied_exactly(self):
        raw = b"actual byte payload\x00" + bytes(8 * 1024**2)
        bridge.save_archive(self.output, raw, deadline=time.monotonic() + 10)
        self.assertEqual(self.output.read_bytes(), raw)
        self.assertEqual(self.output.stat().st_mode & 0o777, 0o600)
        self.assertEqual(self.output.stat().st_nlink, 1)

    def test_archive_bound_and_expiry_reject_before_creating_destination(self):
        with patch.object(bridge, "MAX_ARCHIVE", 4):
            with self.assertRaisesRegex(ValueError, "archive-output-bound$"):
                bridge.save_archive(self.output, b"12345", deadline=time.monotonic() + 5)
        self.assertFalse(self.output.exists())
        with self.assertRaisesRegex(ValueError, "original-deadline$"):
            bridge.save_archive(self.output, b"1234", deadline=time.monotonic() - 1)
        self.assertFalse(self.output.exists())

    def test_occupied_or_symlink_output_keeps_original_and_outside_bytes(self):
        outside = self.root / "outside"
        outside.write_bytes(b"outside sentinel")
        for kind in ("regular", "symlink"):
            with self.subTest(kind=kind):
                if kind == "regular":
                    self.output.write_bytes(b"original sentinel")
                else:
                    self.output.symlink_to(outside)
                try:
                    with self.assertRaises(OSError):
                        bridge.save_archive(self.output, b"new bytes", deadline=time.monotonic() + 5)
                    self.assertEqual(outside.read_bytes(), b"outside sentinel")
                    if kind == "regular":
                        self.assertEqual(self.output.read_bytes(), b"original sentinel")
                finally:
                    self.output.unlink()


class SlotCommandLogControls(unittest.TestCase):
    """Actual files and closed command data; no process or native outcome."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="c1-slot-commands-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        (self.root / "commands").mkdir(mode=0o700)
        self.rows = []
        for ordinal in range(2):
            logs = []
            for stream, raw in (("stdout", b""), ("stderr", b"retained data\n")):
                name = f"{ordinal:02d}-{stream}.log"
                (self.root / "commands" / name).write_bytes(raw)
                logs.append({"name": name, "bytes": len(raw), "sha256": bridge.sha(raw)})
            row = {"ordinal": ordinal, "exit": 0, "waited": True, "group_absent": True,
                   "forced_cleanup": False, "timed_out": False, "failure": None,
                   "root_utility_cgroup_empty": True, "logs": logs}
            self.rows.append(row)
            (self.root / "commands" / f"command-{ordinal:02d}.json").write_bytes(bridge.canonical(row))

    def test_complete_records_reread_both_streams_including_empty_files(self):
        with patch.object(bridge, "read", wraps=bridge.read) as reader:
            bridge.verify_command_logs(self.root, self.rows, deadline=time.monotonic() + 5)
        names = [call.args[0].name for call in reader.call_args_list]
        self.assertEqual(names, ["command-00.json", "00-stdout.log", "00-stderr.log",
                                 "command-01.json", "01-stdout.log", "01-stderr.log"])

    def test_missing_duplicate_or_malformed_streams_reject_before_file_reads(self):
        original = self.rows[0]["logs"]
        variants = [[], original[:1], [original[0], original[0]], None,
                    [dict(original[0], name="01-stdout.log"), original[1]],
                    [dict(original[0], bytes=True), original[1]],
                    [dict(original[0], bytes=bridge.MAX_JSON + 1), original[1]],
                    [dict(original[0], sha256="not-a-digest"), original[1]]]
        for logs in variants:
            with self.subTest(logs=logs):
                rows = copy.deepcopy(self.rows)
                rows[0]["logs"] = logs
                with patch.object(bridge, "read") as reader:
                    with self.assertRaises(ValueError):
                        bridge.verify_command_logs(self.root, rows, deadline=time.monotonic() + 5)
                    reader.assert_not_called()

    def test_missing_empty_file_or_changed_stream_cannot_pass(self):
        empty = self.root / "commands/00-stdout.log"
        empty.unlink()
        with self.assertRaises(FileNotFoundError):
            bridge.verify_command_logs(self.root, self.rows, deadline=time.monotonic() + 5)
        empty.write_bytes(b"")
        (self.root / "commands/01-stderr.log").write_bytes(b"changed stream\n")
        with self.assertRaisesRegex(ValueError, "aggregate-command-log$"):
            bridge.verify_command_logs(self.root, self.rows, deadline=time.monotonic() + 5)


class SlotPrerequisiteDataControls(unittest.TestCase):
    """Synthetic closed records test parser consistency, never native acceptance."""

    def fixture(self, case):
        spec = importlib.util.spec_from_file_location("c1_records_" + case, Path(__file__).parent / f"procedures/slot/records-{case.lower()}.py")
        records = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(records)
        records.SOURCE = "a" * 40
        result = {"schema": "issue779-n03-peer-observation-v1", "control": "N03", "status": "observed",
                  "source_commit": records.SOURCE, "worker_exit": 1, "broker_exit": 0,
                  "worker_stdout_bytes": 0, "asevd402_observed": True,
                  "kernel_peer_bound_to_owned_worker": True, "request_bytes": 0, "sent_bytes": 0,
                  "ready_request_observed": False, "descriptor_supplied": False,
                  "owned_process_groups_joined": True, "native_acceptance": False,
                  "systemd_worker_authority": False, "stream_custody": "private-files-after-original-process-reap",
                  "worker_pid": 123, "broker_pid": 124}
        result_raw = bridge.canonical(result)
        # Exact result-field sets from the four literal fixture publishers.
        fixture = {"generation": "b" * 32, "source_revision": records.SOURCE,
                   "base_revision": "c" * 40, "native_acceptance": False}
        if case == "N04":
            fixture.update(schema="issue779-n04-peer-fixture-v1", control=case,
                           root_launch_join_status=1, helper_launch_join_status=0, root_stdout_bytes=0,
                           account_disposition="preserved-quarantined", observation_only=True,
                           policy_sha256="d" * 64, independent_raw_worker_capture=False,
                           other_controls="not-qualified-by-this-fixture")
        elif case in ("N05", "N06"):
            fixture.update(schema="issue779-negative-slot-fixture-v1", control=case,
                           root_launch_join_status=1, root_stdout_bytes=0,
                           worker_terminal_from_original_monitor=True, original_csharp_joins_required=True,
                           account_disposition="preserved-quarantined", observation_only=True,
                           policy_sha256="d" * 64, other_controls="not-qualified-by-this-fixture")
        else:
            fixture.update(schema="issue779-cancellation-fixture-v1", case=case, capture_sha256="d" * 64,
                           root_launch_exit=1, fixture_processes_joined=True, fresh_generated_groups_empty=True,
                           account_disposition="removed-after-root-custody", trusted=False)
        values = {"fixture-result.json": fixture, "logs/n03/result.json": result,
                  "n03-result-sha256": bridge.sha(result_raw),
                  "_retained_raw": {"logs/n03/result.json": result_raw}}
        for name, pid, uid in (("worker-live.json", 123, 65533), ("broker-live.json", 124, 65534),
                               ("broker-live-after.json", 124, 65534)):
            values["logs/n03/" + name] = {"schema": "issue779-n03-owned-image-sample-v1", "pid": pid,
                "start_ticks": "123456", "uid4": ":".join([str(uid)] * 4), "gid4": ":".join(["65534"] * 4),
                "cgroup": "0::/system.slice/issue779-native-tool-" + "e" * 32 + "-00.service",
                "exact_managed_argv": True, "image_pinned": True, "empty_groups": True,
                "no_new_privs": 1, "effective_capabilities_zero": True}
        return records, values

    def test_four_negative_fixture_shapes_do_not_require_an_absent_enclosing_digest(self):
        for case in ("N04", "N05", "N06", "N08"):
            with self.subTest(case=case):
                records, values = self.fixture(case)
                self.assertNotIn("n03_result_sha256", values["fixture-result.json"])
                records.validate_n03_archive(values)

    def test_raw_result_binding_and_live_prerequisites_remain_required(self):
        for case in ("N04", "N05", "N06", "N08"):
            for mutation in ("digest", "raw", "live", "missing"):
                with self.subTest(case=case, mutation=mutation):
                    records, values = self.fixture(case)
                    if mutation == "digest":
                        values["n03-result-sha256"] = "f" * 64
                    elif mutation == "raw":
                        values["_retained_raw"]["logs/n03/result.json"] = bridge.canonical(dict(values["logs/n03/result.json"], worker_pid=456))
                    elif mutation == "live":
                        values["logs/n03/worker-live.json"]["image_pinned"] = False
                    else:
                        del values["_retained_raw"]["logs/n03/result.json"]
                    with self.assertRaises(ValueError):
                        records.validate_n03_archive(values)


if __name__ == "__main__":
    unittest.main()
