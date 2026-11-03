"""Detached current N10 data controls; no owner, worker, lease or native execution."""
import copy
import hashlib
import io
import json
from pathlib import Path
import tarfile
import types
import unittest

import binding

ROOT = Path(__file__).resolve().parent

def data_module(name, path):
    raw = path.read_bytes()
    value = types.ModuleType(name)
    value.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), value.__dict__)
    return value

m = data_module("n10_parser_detached", ROOT / "procedures/parsers/check_n10_pending_start.py")
r = data_module("n10_records_detached", ROOT / "procedures/n10/records.py")
a = data_module("n10_adapter_detached", ROOT / "procedures/n10/adapter.py")
GEN = "11111111222233334444555555555555"
UNIT = "appsurface-evidence-worker-" + GEN + ".service"
JOB = "/org/freedesktop/systemd1/job/17"
SOURCE, BASE, SHA = "2" * 40, "3" * 40, "4" * 64


def frames():
    """Original public diagnostic-frame shape represented as detached data only."""
    start = {"schema": "issue779-n10-pending-start-phase-v1", "case": "N10",
             "phase": "start-transient-unit-reply", "unit": UNIT, "job_path": JOB,
             "native_authority": False, "native_acceptance": False}
    end = {"schema": "issue779-n10-pending-start-observation-v1", "case": "N10",
           "phase": "original-start-stop-and-pumps-joined-before-custody", "generation": GEN,
           "unit": UNIT, "job_path": JOB, "start_reply_observed": True,
           "pending_start": {"start_reserved": True, "started": False, "start_joined": True,
                             "closed": True, "stop_joined": True, "first_failure": "StartCancelled"},
           "lifetime": {"startup_joined": True, "startup_failed": True, "stop_joined": True,
                        "physically_settled": False}, "original_stop_delegate_calls": 2,
           "final_unit": {"id": UNIT, "active_state": "inactive", "sub_state": "dead", "main_pid": 0,
                          "exec_main_pid": 217, "exec_main_code": 1, "exec_main_status": 2, "stopped": True},
           "final_group_after_pumps": {"sample_taken": True, "empty": True, "exists": True,
                                       "populated": False, "frozen": False, "device_major": 0,
                                       "device_minor": 34, "inode": 123},
           "pumps": {"joined": True, "received_bytes": 0, "discarded_bytes": 0, "failure": "None",
                     "stdout_eof": True, "stdout_failure": "None", "stderr_eof": True, "stderr_failure": "None"},
           "root_custody_completed": False, "native_authority": False, "native_acceptance": False}
    return start, end


def encode(start, end):
    return (json.dumps(start, separators=(",", ":")) + "\n" + json.dumps(end, separators=(",", ":")) + "\n").encode()


def fixture_model():
    return {"schema": "issue779-n10-native-fixture-observation-v1", "generation": GEN,
            "source_commit": SOURCE, "source_capture_sha256": SHA, "root_projection_sha256": SHA,
            "root_exit": 1, "worker_unit": UNIT, "job_path": JOB, "worker_pid": 217,
            "worker_uid": 1001, "worker_gid": 1002, "worker_starttime_ticks": 123,
            "root_owner_pid": 218, "root_owner_starttime_ticks": 124,
            "worker_passwd_lookup_exit": 2, "worker_group_lookup_exit": 2,
            "generated_name_checks": 5, "generated_names_present": 0, "stderr_sha256": SHA,
            "pending_start_failure": "StartCancelled", "original_stop_delegate_calls": 2,
            "physically_settled": False, "unit_stopped": True, "final_group_sampled_after_pumps": True,
            "final_group_empty": True, "pumps_joined": True, "stdout_eof": True, "stderr_eof": True,
            "discarded_bytes": 0, "staging_path_retained": True, "private_path_retained": True,
            "root_custody_completed": False, "quarantined_workspace_retained": True,
            "owner_terminal_verified": True, "worker_terminal_verified": True,
            "native_authority": False, "native_acceptance": False}


class N10DataTests(unittest.TestCase):
    def test_original_joined_frames_then_one_stop_join_or_first_failure_mutation(self):
        start, end = frames()
        actual = m.validate_bytes(encode(start, end), "joined")
        self.assertEqual(2, actual["original_stop_delegate_calls"])
        self.assertFalse(actual["physically_settled"])
        for path, bad in ((("original_stop_delegate_calls",), 1), (("original_stop_delegate_calls",), 3),
                          (("pending_start", "first_failure"), "None"), (("pending_start", "started"), True),
                          (("lifetime", "physically_settled"), True), (("pumps", "joined"), False),
                          (("pumps", "stdout_eof"), False), (("final_group_after_pumps", "empty"), False)):
            changed = copy.deepcopy(end)
            target = changed
            for part in path[:-1]: target = target[part]
            target[path[-1]] = bad
            with self.subTest(path=path), self.assertRaises(m.Reject): m.validate_bytes(encode(start, changed), "joined")

    def test_current_fixture_binding_then_one_identity_custody_or_exit_field(self):
        r.validate_fixture_result(fixture_model(), GEN, SHA, SOURCE, SHA)
        for key, bad in (("root_exit", 0), ("root_exit", True), ("worker_uid", 0),
                         ("worker_starttime_ticks", 0), ("root_owner_starttime_ticks", 0),
                         ("original_stop_delegate_calls", 1), ("pumps_joined", False),
                         ("root_custody_completed", True), ("native_acceptance", True),
                         ("source_commit", BASE), ("source_capture_sha256", "5" * 64)):
            changed = fixture_model()
            changed[key] = bad
            with self.subTest(field=key), self.assertRaises(r.Rejected):
                r.validate_fixture_result(changed, GEN, SHA, SOURCE, SHA)

    def test_unit_collision_diagnostic_is_closed_data_not_success(self):
        value = {"schema": "issue779-n10-unit-collision-v1", "role": "worker", "load_state": "loaded", "query_exit": 0}
        self.assertEqual(value, r.validate_unit_collision_diagnostic(binding.canonical(value)))
        for key, bad in (("role", "other"), ("load_state", "success"), ("query_exit", True), ("query_exit", 256)):
            changed = dict(value)
            changed[key] = bad
            with self.subTest(field=key), self.assertRaises(r.Rejected):
                r.validate_unit_collision_diagnostic(binding.canonical(changed))

    def test_twelve_stdout_observations_bind_exact_actual_inventory_neighbors(self):
        facts = {name: {"file_count": 1, "node_count": 2, "total_bytes": 19} for name in ("source", "tool", "runtime")}
        policy_bytes = len(binding.canonical(r.FIXTURE_POLICY))
        normal = b"FULL_TREE_POINT_SAMPLES_MATCHED:1:2:19\n"
        policy = ("FULL_TREE_POINT_SAMPLES_MATCHED:1:3:%d\n" % (19 + policy_bytes)).encode()
        raw = normal * 3 + policy + normal * 2 + normal * 3 + policy + normal * 2 + b"N10_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n"
        r.validate_fixture_stdout(raw, facts)
        for changed in (raw.split(b"\n", 1)[1], raw.replace(b"MATCHED:1:2:19", b"MATCHED:1:2:20", 1), raw + b"extra\n"):
            with self.assertRaises(r.Rejected): r.validate_fixture_stdout(changed, facts)

    def test_canonical_diagnostic_archive_cannot_be_a_native_case_and_rejects_suffix(self):
        selection = {"schema": "issue779-native-retention-selection-v1", "selection_status": "parent-empty",
                     "data_file_count": 0, "data_bytes": 0, "missing_fixed_file_count": 11}
        raw_json = binding.canonical(selection)
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:", format=tarfile.USTAR_FORMAT) as archive:
            entry = tarfile.TarInfo("retention-selection.json")
            entry.size = len(raw_json)
            entry.mode = 0o600
            entry.uid = entry.gid = entry.mtime = 0
            archive.addfile(entry, io.BytesIO(raw_json))
        raw = buffer.getvalue()
        values = r.inspect_archive(raw)
        self.assertEqual({"retention-selection.json"}, set(values))
        with self.assertRaises(r.Rejected):
            r.validate_n10_archive(values, b"not-parser", GEN, SHA, "github-actions-1-1", {}, SOURCE, BASE, SHA, SHA)
        with self.assertRaises(r.Rejected): r.inspect_archive(raw + b"unexpected")

    def test_truthful_node_projection_then_one_hash_mode_or_membership_mutation(self):
        source = {"files": {"one.cs": {"sha256": SHA, "mode": "0644", "git_blob": "6" * 40}}}
        build = {role: {"files": {name: {"sha256": SHA, "mode": "0755" if name == "dotnet" else "0644", "bytes": 19}}, "directories": {}}
                 for role, name in (("tool", "entry.dll"), ("runtime", "dotnet"))}
        nodes = {"source": {"files": {"one.cs": {"sha256": SHA, "mode": "0644", "bytes": 19}}, "directories": {".": {"mode": "0700"}}},
                 "tool": {"files": {"entry.dll": {"sha256": SHA, "mode": "0444", "bytes": 19}}, "directories": {".": {"mode": "0555"}}},
                 "runtime": {"files": {"dotnet": {"sha256": SHA, "mode": "0555", "bytes": 19}}, "directories": {".": {"mode": "0555"}}}}
        self.assertEqual({"file_count": 1, "node_count": 2, "total_bytes": 19}, a.node_artifacts(nodes, source, build)["source"])
        for key, bad in (("sha256", "5" * 64), ("mode", "0755"), ("bytes", True)):
            changed = copy.deepcopy(nodes)
            changed["source"]["files"]["one.cs"][key] = bad
            with self.subTest(field=key), self.assertRaises(binding.Rejected): a.node_artifacts(changed, source, build)
        changed = copy.deepcopy(nodes)
        changed["tool"]["directories"] = {}
        with self.assertRaises(binding.Rejected): a.node_artifacts(changed, source, build)

    def test_full_detached_archive_binds_current_request_summary_and_fixture(self):
        # These bytes are a parser model, never evidence of an actual launch.
        stderr = encode(*frames())
        parser_raw = (ROOT / a.PARSER).read_bytes()
        parser_sha = hashlib.sha256(parser_raw).hexdigest()
        summary = m.validate_bytes(stderr, "joined")
        fixture = fixture_model()
        fixture["stderr_sha256"] = summary["stderr_sha256"]
        facts = {name: {"file_count": 1, "node_count": 2, "total_bytes": 19}
                 for name in ("source", "tool", "runtime")}
        policy_raw = binding.canonical(r.FIXTURE_POLICY)
        normal = b"FULL_TREE_POINT_SAMPLES_MATCHED:1:2:19\n"
        with_policy = ("FULL_TREE_POINT_SAMPLES_MATCHED:1:3:%d\n" % (19 + len(policy_raw))).encode()
        stdout = normal * 3 + with_policy + normal * 2 + normal * 3 + with_policy + normal * 2
        stdout += b"N10_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n"
        base = "/var/lib/appsurface-evidence-fixture/" + GEN
        request = {"schema": "evidence-supervisor-linux-v1", "mode": "observation",
            "tool_root": base + "/tool", "runtime_root": base + "/runtime",
            "runtime_host": base + "/runtime/dotnet", "entry_path": base + "/tool/ForgeTrust.AppSurface.Cli.dll",
            "policy_file": base + "/tool/fixture-policy.json", "subject_root": base + "/subject",
            "base_revision": BASE, "subject_revision": SOURCE, "workflow_identity": "github-actions-1-1",
            "paths": ["docs/designs/issue-779-csharp-supervision-core.md"], "observation_profile_ids": ["empty"],
            "observation_producer_ids": [], "job_deadline_utc": "2030-01-01T00:00:00Z",
            "admission_seconds": 10, "start_seconds": 30, "collection_seconds": 10,
            "cleanup_seconds": 60, "stopping_seconds": 5}
        def checksum(raw):
            return (binding.digest(raw) + "  /run/appsurface-evidence-fixture/" + GEN + "/request.json\n"
                    + binding.digest(policy_raw) + "  " + base + "/tool/fixture-policy.json\n").encode()
        request_raw = binding.canonical(request)
        values = {"fixture-stdout.log": stdout, "fixture-stderr.log": b"", "logs/n10.stdout": b"",
                  "logs/n10.stderr": stderr, "n10-record-summary.json": binding.canonical(summary),
                  "n10-fixture-result.json": binding.canonical(fixture), "request.json": request_raw,
                  "request-policy.sha256": checksum(request_raw)}
        values["retention-selection.json"] = binding.canonical({
            "schema": "issue779-native-retention-selection-v1", "selection_status": "one-namespace",
            "data_file_count": len(values), "data_bytes": sum(map(len, values.values())),
            "missing_fixed_file_count": 11 - len(values)})
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w:", format=tarfile.USTAR_FORMAT) as archive:
            for name, raw in sorted(values.items()):
                entry = tarfile.TarInfo(name)
                entry.size, entry.mode = len(raw), 0o600
                entry.uid = entry.gid = entry.mtime = 0
                archive.addfile(entry, io.BytesIO(raw))
        values = r.inspect_archive(buffer.getvalue())
        args = (parser_raw, GEN, SHA, "github-actions-1-1", facts, SOURCE, BASE, SHA, parser_sha)
        actual, actual_summary, _ = r.validate_n10_archive(values, *args)
        self.assertEqual(fixture, actual)
        self.assertEqual(summary, actual_summary)
        # Rebind checksum bytes so a rejection must come from the current request contract.
        for field, bad in (("subject_revision", BASE), ("base_revision", SOURCE),
                           ("workflow_identity", "github-actions-2-1"), ("cleanup_seconds", 61)):
            changed_request = dict(request)
            changed_request[field] = bad
            changed = dict(values)
            changed["request.json"] = binding.canonical(changed_request)
            changed["request-policy.sha256"] = checksum(changed["request.json"])
            with self.subTest(field=field), self.assertRaises(r.Rejected):
                r.validate_n10_archive(changed, *args)
        changed = dict(values)
        changed_fixture = dict(fixture)
        changed_fixture["root_projection_sha256"] = "5" * 64
        changed["n10-fixture-result.json"] = binding.canonical(changed_fixture)
        with self.assertRaises(r.Rejected): r.validate_n10_archive(changed, *args)


if __name__ == "__main__":
    unittest.main()
