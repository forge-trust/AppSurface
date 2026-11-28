import copy
import hashlib
import json
from pathlib import Path
import runpy
import tempfile
import unittest

VERIFIER = runpy.run_path(str(Path(__file__).resolve().parents[1] / "scripts" / "verify_evidence_profile_replay.py"))


class ReplayParityTests(unittest.TestCase):
    def setUp(self):
        self.steps = [{"name": "Run and verify scoped package evidence", "conclusion": "success"}]
        self.job = {"labels": ["ubuntu-latest"], "conclusion": "success", "steps": self.steps + [
            {"name": name, "conclusion": "success"} for name in VERIFIER["REPLAY_ONLY_STEPS"]
        ]}
        self.original = {
            "scope": "packages",
            "jobs": [{**self.job, "steps": self.steps}],
            "proofs": [{
                "claim": "TargetedComplete", "planDigest": "package-plan",
                "closedObligations": ["packages"],
                "proofFiles": [{"LogicalName": "result", "RelativePath": "proof/result.json"}],
            }],
        }
        self.reference = {
            "selectionMergeBase": "base", "head": "head", "normalizedChangeDigest": "paths",
            "fixturePaths": ["packages/ci-evidence.md"],
            "ordinaryWorkflows": [
                self.original,
                *[{"scope": scope, "proofs": [{"claim": "NoEvidenceRequired", "planDigest": scope}]}
                  for scope in ("coverage", "native")],
            ],
        }
        facts = {
            "runnerImage": {"name": "ubuntu", "version": "1"}, "checkout": "head",
            "toolchain": {"dotnet": "10.0.402"}, "pnpmCache": {"outcome": "hit", "key": "same"},
            "verification": {"claim": "TargetedComplete", "planDigest": "package-plan"},
        }
        self.observed = {"ordinary": facts, "replay": copy.deepcopy(facts)}
        flags = {
            "requiresCoverage": False, "requiresNativeCoverage": False,
            "requiresPackageChecks": False,
        }
        self.package = {
            "source": {"baseCommit": "base", "headCommit": "head", "sourceDigest": "paths",
                       "paths": [{"path": "packages/ci-evidence.md"}]},
            "selection": {**flags, "profileId": "packages", "producerIds": ["package-checks"],
                          "requiresPackageChecks": True},
            "claim": "TargetedComplete", "planDigest": "package-plan",
            "selectedObligations": ["packages"], "closedObligations": ["packages"],
            "proofFiles": [("result", "proof/result.json")],
            "commands": [("gate", 0), ("verify", 0), ("verify-packages", 0)],
        }
        self.projections = {
            scope: ({"PlanDigest": scope}, {**flags, "producerIds": []}) for scope in ("coverage", "native")
        }

    def differences(self):
        return VERIFIER["compare"](self.reference, self.original, self.job, self.observed, self.package, self.projections)

    def test_matching_execution_accepts_only_parity(self):
        self.assertEqual([], self.differences())

    def test_skipped_package_step_in_a_successful_job_is_a_mismatch(self):
        self.job["steps"][0] = {**self.steps[0], "conclusion": "skipped"}
        self.assertEqual(["package-steps"], [d["field"] for d in self.differences()])

    def test_runner_and_cache_drift_are_both_reported(self):
        self.observed["replay"]["runnerImage"]["version"] = "2"
        self.observed["replay"]["pnpmCache"] = {"outcome": "miss", "key": None}
        self.assertEqual(["runnerImage", "pnpmCache"], [d["field"] for d in self.differences()])

    def test_sibling_plan_cannot_change_without_a_mismatch(self):
        self.projections["coverage"][1]["producerIds"] = ["solution-coverage"]
        self.assertEqual(["coverage-selection"], [d["field"] for d in self.differences()])

    def test_incomplete_obligations_fail_even_if_reported_claim_matches(self):
        self.package["closedObligations"] = []
        self.assertEqual(["closed-obligations"], [d["field"] for d in self.differences()])

    def test_a_reference_cannot_be_modified_after_qualification(self):
        data = {"schemaVersion": 1, "repository": "forge-trust/AppSurface", "issue": 780,
                "pullRequest": 960, "ordinaryReferenceQualified": True,
                "untimedReplayVerified": False, "timingSamplesAdmitted": 0}
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "ordinary.json"
            path.write_text(json.dumps(data))
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertEqual(data, VERIFIER["read_reference"](path, digest))
            path.write_text(json.dumps({**data, "timingSamplesAdmitted": 1}))
            with self.assertRaisesRegex(ValueError, "ordinary-reference-digest"):
                VERIFIER["read_reference"](path, digest)


if __name__ == "__main__":
    unittest.main()
