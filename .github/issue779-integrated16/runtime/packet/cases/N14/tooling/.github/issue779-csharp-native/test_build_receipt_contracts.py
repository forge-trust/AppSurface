"""Synthetic receipt-shape controls; never evidence of a real build or native case."""
import importlib.machinery
import importlib.util
from pathlib import Path
import unittest

SCRIPT = Path(__file__).with_name("prepare-case-workspace.py")
loader = importlib.machinery.SourceFileLoader("workspace_materializer_contracts", str(SCRIPT))
spec = importlib.util.spec_from_loader(loader.name, loader)
workspace = importlib.util.module_from_spec(spec)
loader.exec_module(workspace)

SELECTED = ["restore", "publish", "restore", "test", "restore", "test"]

def row(ordinal, argv):
    return {"ordinal": ordinal, "argv": argv, "exit": 0, "failure": None,
            "waited": True, "group_absent": True, "timed_out": False,
            "forced_cleanup": False, "late_completion": False,
            "logs": [{"sha256": "a" * 64}, {"sha256": "b" * 64}]}

def owner_receipt(case):
    c = workspace.BUILD_CONTRACTS[case]
    commands = [row(i, ["/usr/bin/python3", "prepare-stage", str(i)]) for i in range(43)]
    for i, verb in enumerate(SELECTED):
        commands[i + 10] = row(i + 10, ["/usr/bin/dotnet", verb,
            "/build/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj",
            "-p:EvidencePrivateQualification=" + case])
    snapshot = {"head": c["source"], "tree": c["tree"], "count": workspace.SOURCE_COUNT,
                "source_map_sha256": c["map"], "physical_sha256_modes": True,
                "physical_git_sha1": True, "index_tree_matches": True}
    test = {"counters": {"total":"1", "executed":"1", "passed":"1", "failed":"0",
                         "error":"0", "notExecuted":"0", "timeout":"0", "aborted":"0"},
            "trx_sha256":"c" * 64, "native_acceptance":False}
    phase = dict(test, root_factory_exercised=False, unprivileged_library_behavior_only=True)
    return {"schema":"issue779-csharp-fdd-build-v5", "exit":0, "failure":None,
            "diagnostics":[], "private_qualification":case, "selected_variant":case,
            "case":case, "source_commit":c["source"], "source_revision":c["source"],
            "source_parent":c["parent"], "harness_parent":c["source"],
            "harness_retry_parent":c["source"], "harness_retry_parent_parent":c["parent"],
            "source_count":workspace.SOURCE_COUNT,
            "source_capture_sha256":c["capture"], "source_map_sha256":c["map"],
            "source_capture_map_sha256":c["raw_map"], "capture_input_sha256":c["capture"],
            "capture_projection_sha256":c["build_projection"],
            "build_prerequisite_only":True, "test_execution":True, "native_execution":False,
            "native_acceptance":False, "checkpoint_pass":False, "review_clear":False,
            "source_review_clear":False, "fixture_review_clear":False,
            "root_transport_review_clear":False,
            "duration_policy_seconds":{"original_request":240,"owner_runtime":240,
                                        "timeout_stop":5,"n14_owner_bound":245},
            "commands":commands,
            "owner_death_phase_frame_regression":phase,
            "owner_death_cli_qualification":test,
            "source_before":snapshot, "source_after_assets":snapshot,
            "source_after_build":snapshot, "source_final":snapshot,
            "artifacts":{"source":{"file_count":workspace.SOURCE_COUNT},
                         "tool":{}, "runtime":{}}}

def n15_receipt():
    c = workspace.BUILD_CONTRACTS["N15"]
    commands = [row(i, ["/usr/bin/python3", "build-stage", str(i)]) for i in range(43)]
    project = "/build/Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
    selector = "-p:EvidencePrivateQualification=N15"
    commands[30] = row(30, ["/usr/bin/dotnet", "restore", project, selector])
    commands[31] = row(31, ["/usr/bin/dotnet", "publish", project, selector])
    test_project = "/build/Evidence/ForgeTrust.AppSurface.Evidence.Supervision.Tests/Tests.csproj"
    commands[32] = row(32, ["/usr/bin/dotnet", "restore", test_project])
    commands[33] = row(33, ["/usr/bin/dotnet", "test", test_project])
    snapshot = {"head":c["source"], "tree":c["tree"], "count":workspace.SOURCE_COUNT,
                "source_map_sha256":c["map"], "physical_sha256_modes":True,
                "physical_git_sha1":True, "index_tree_matches":True}
    pipe = {"method":"OwnedRawPipeReadWrappingUsesHandleModeAndJoinsBothEofs",
            "counters":{"total":"1","executed":"1","passed":"1","failed":"0",
                        "error":"0","notExecuted":"0","timeout":"0","aborted":"0"},
            "trx_sha256":"d"*64,"unprivileged_library_behavior_only":True,
            "root_factory_exercised":False,"native_acceptance":False,
            "runtime_basis":"selected SDK dotnet host"}
    return {"schema":"issue779-csharp-fdd-build-v5", "exit":0, "failure":None,
            "diagnostics":[], "private_qualification":"N15", "selected_variant":"N15",
            "source_commit":c["source"], "harness_parent":c["source"],
            "harness_retry_parent":c["source"], "harness_retry_parent_parent":c["parent"],
            "capture_input_sha256":c["capture"],
            "capture_projection_sha256":c["build_projection"],
            "build_prerequisite_only":True,"native_execution":False,"checkpoint_pass":False,
            "commands":commands,"source_before":snapshot,"source_after_assets":snapshot,
            "source_after_build":snapshot,"source_final":snapshot,
            "artifacts":{"source":{"file_count":workspace.SOURCE_COUNT},"tool":{},"runtime":{}},
            "linux_pipe_regression":pipe}

class BuildReceiptContractTests(unittest.TestCase):
    def test_n15_full_record_contract_accepts_actual_shape_without_owner_fields(self):
        receipt = n15_receipt()
        self.assertNotIn("source_revision", receipt)
        self.assertNotIn("duration_policy_seconds", receipt)
        self.assertNotIn("test_execution", receipt)
        self.assertNotIn("owner_death_phase_frame_regression", receipt)
        workspace.validate_build_receipt_data(receipt, "N15")

    def test_n13_n14_selected_six_are_filtered_from_full_records(self):
        for case in ("N13", "N14"):
            with self.subTest(case=case):
                receipt = owner_receipt(case)
                self.assertEqual(len(receipt["commands"]), 43)
                workspace.validate_build_receipt_data(receipt, case)

    def test_owner_build_cannot_be_reduced_to_its_six_selected_rows(self):
        receipt = owner_receipt("N13")
        receipt["commands"] = receipt["commands"][10:16]
        for ordinal, command in enumerate(receipt["commands"]):
            command["ordinal"] = ordinal
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N13")

    def test_mixed_variant_is_rejected(self):
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(n15_receipt(), "N14")

    def test_n15_rejects_missing_full_command_record(self):
        receipt = n15_receipt()
        receipt["commands"].pop()
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N15")

    def test_n15_rejects_duplicate_or_wrong_selector(self):
        for replacement in ("-p:EvidencePrivateQualification=N14",
                            "-p:EvidencePrivateQualification=N15"):
            receipt = n15_receipt()
            receipt["commands"][30]["argv"].append(replacement)
            with self.subTest(replacement=replacement), self.assertRaises(workspace.Reject):
                workspace.validate_build_receipt_data(receipt, "N15")

    def test_every_unfiltered_record_must_be_settled_and_have_two_hashed_logs(self):
        receipt = n15_receipt()
        receipt["commands"][8]["group_absent"] = False
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N15")
        receipt = n15_receipt()
        receipt["commands"][8]["logs"][1]["sha256"] = "not-a-digest"
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N15")

    def test_n15_requires_only_its_pipe_regression_metadata(self):
        receipt = n15_receipt()
        receipt["linux_pipe_regression"]["native_acceptance"] = True
        with self.assertRaises(workspace.Reject):
            workspace.validate_build_receipt_data(receipt, "N15")

    def test_capture_projection_key_is_variant_specific(self):
        capture = {"validation_commit":"head", "previous_validation_commit":"parent",
                   "source_count":1, "source_tree":"tree", "source":{"sha256":{},"modes":{}}}
        self.assertNotEqual(workspace.source_projection_sha(capture, "source_tree"),
                            workspace.source_projection_sha(capture, "tree"))
        self.assertEqual(workspace.BUILD_CONTRACTS["N15"]["projection_tree_key"], "source_tree")
        self.assertEqual(workspace.BUILD_CONTRACTS["N13"]["projection_tree_key"], "tree")

if __name__ == "__main__":
    unittest.main()
