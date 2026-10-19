"""Detached N16 data controls only; no root/native owner or subprocess is created."""
import copy
import importlib.util
import json
from pathlib import Path
import unittest

import binding

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("n16_data_adapter", HERE / "procedures/n16/adapter.py")
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)
records = adapter._RECORDS
G = "1" * 32
SOURCE = "2" * 40
BASE = "3" * 40
SHA = "4" * 64


def fixture_model():
    """Shape model, explicitly observation-only and never a native execution."""
    return {"schema": "issue779-n16-native-fixture-observation-v2", "generation": G,
            "source_commit": SOURCE, "source_capture_sha256": SHA, "root_projection_sha256": SHA,
            "root_exit": 1, "root_stdout_bytes": 0, "stderr_sha256": SHA,
            "worker_unit": "appsurface-evidence-worker-" + G + ".service", "worker_pid": 123,
            "worker_uid": 456, "worker_gid": 457, "results_gid": 458,
            "owner_load_state": "not-found", "worker_load_state": "not-found",
            "generated_passwd_group_name_checks": 5, "worker_uid_reverse_lookup_exit": 2,
            "worker_gid_reverse_lookup_exit": 2, "results_gid_reverse_lookup_exit": 2,
            "root_launch_group_absent": True, "root_launch_fd_directory_absent": True,
            "workspace_retained_verified": True, "observation_only": True,
            "native_authority": False, "native_acceptance": False}


class N16DataTests(unittest.TestCase):
    def test_terminal_model_then_one_changed_kernel_or_cleanup_field(self):
        records.validate_fixture_result(fixture_model(), G, SHA, SOURCE, SHA)
        for key, bad in (("root_exit", 0), ("root_exit", True), ("root_stdout_bytes", 1),
                         ("owner_load_state", "loaded"), ("worker_load_state", "loaded"),
                         ("root_launch_group_absent", False), ("root_launch_fd_directory_absent", False),
                         ("workspace_retained_verified", False), ("worker_uid_reverse_lookup_exit", 0),
                         ("worker_gid_reverse_lookup_exit", 0), ("results_gid_reverse_lookup_exit", 0),
                         ("generated_passwd_group_name_checks", 4), ("native_acceptance", True),
                         ("generation", "5" * 32), ("worker_unit", "unrelated.service")):
            with self.subTest(field=key, value=bad):
                value = fixture_model()
                value[key] = bad
                with self.assertRaises(ValueError): records.validate_fixture_result(value, G, SHA, SOURCE, SHA)

    def test_request_valid_adjacent_then_one_budget_or_identity_field(self):
        generation = G
        stage = "/var/lib/appsurface-evidence-fixture/" + generation
        value = {"schema": "evidence-supervisor-linux-v1", "mode": "observation",
                 "tool_root": stage + "/tool", "runtime_root": stage + "/runtime",
                 "runtime_host": stage + "/runtime/dotnet", "entry_path": stage + "/tool/ForgeTrust.AppSurface.Cli.dll",
                 "policy_file": stage + "/tool/fixture-policy.json", "subject_root": stage + "/subject",
                 "base_revision": BASE, "subject_revision": SOURCE, "workflow_identity": "github-actions-123-1",
                 "paths": ["docs/designs/issue-779-csharp-supervision-core.md"], "observation_profile_ids": ["empty"],
                 "observation_producer_ids": [], "admission_seconds": 10, "start_seconds": 30,
                 "collection_seconds": 10, "cleanup_seconds": 60, "stopping_seconds": 5,
                 "job_deadline_utc": "2026-10-19T08:00:00Z"}
        def check(model):
            raw = binding.canonical(model)
            policy = binding.canonical(records.FIXTURE_POLICY)
            checksums = (binding.digest(raw) + "  /run/appsurface-evidence-fixture/" + G + "/request.json\n"
                         + binding.digest(policy) + "  " + stage + "/tool/fixture-policy.json\n").encode()
            records.validate_request_binding(raw, checksums, G, "github-actions-123-1", SOURCE, BASE)
        check(value)
        for field, bad in (("start_seconds", 31), ("cleanup_seconds", 59), ("mode", "admission"),
                           ("subject_revision", BASE), ("observation_profile_ids", ["other"]),
                           ("job_deadline_utc", "not-a-date")):
            changed = copy.deepcopy(value)
            changed[field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError): check(changed)

    def test_wrapper_requires_all_twelve_exact_inventory_observations(self):
        facts = {name: {"file_count": 1, "node_count": 2, "total_bytes": 19} for name in ("source", "tool", "runtime")}
        policy_bytes = len(binding.canonical(records.FIXTURE_POLICY))
        counts = {**{name: (1, 2, 19) for name in facts}, "sealed_tool": (1, 3, 19 + policy_bytes)}
        sequence = ("source", "tool", "runtime", "sealed_tool", "runtime", "source",
                    "source", "tool", "runtime", "sealed_tool", "runtime", "source")
        raw = ("".join("FULL_TREE_POINT_SAMPLES_MATCHED:%d:%d:%d\n" % counts[name] for name in sequence)
               + "N16_SOURCE_AND_OS_OBSERVATION_TERMINAL:NOT_ACCEPTANCE\n").encode()
        records.validate_fixture_output(raw, b"", facts)
        for stdout, stderr in ((raw.replace(b"MATCHED:1:2:19", b"MATCHED:1:2:20", 1), b""),
                               (raw, b"unexpected"), (raw.split(b"\n", 1)[1], b"")):
            with self.assertRaises(ValueError): records.validate_fixture_output(stdout, stderr, facts)


if __name__ == "__main__":
    unittest.main()
