"""Bounded external-process controls for public gate evaluation; no admission or platform proof."""
import argparse
import copy
import hashlib
import json
import shutil
import subprocess
import sys
import unittest
from pathlib import Path

SCHEMA = "evidence-protected-gate-input-v1"
MAXIMUM_BYTES = 20 * 1024 * 1024
CANARY = "subject-secret-canary-MUST-NOT-APPEAR"
CONSUMER = None
DOTNET = None


def canonical(value):
    """Encode this ASCII-only fixture, including the canonical writer's escaped date-offset plus sign."""
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).replace("+", "\\u002B").encode("utf-8")


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def synthetic_envelope():
    """Create coherent public JSON values, never a runtime capability or protected assertion."""
    assertion_id = "synthetic/public-api@1"
    profile = {
        "Id": "synthetic-api", "Scope": "Targeted", "Resources": [],
        "Producers": [{"Id": "producer", "Kind": "synthetic", "Version": "1.0.0",
                       "RequiredResources": [], "AssertionIds": [assertion_id], "ArtifactSlots": [],
                       "TimeoutSeconds": 10, "CoverageGate": None}],
        "Obligations": [{"Id": "obligation", "RiskClass": "synthetic", "Rationale": "Pure API control.",
                         "RequiredProducerIds": ["producer"], "RequiredAssertionId": assertion_id}],
    }
    policy = {"Id": "synthetic-policy", "Version": "1", "ConservativeProfileId": profile["Id"],
              "Profiles": [profile], "Rules": []}
    paths = [{"Path": "src/Feature.cs", "Kind": "modified", "PreviousPath": None}]
    plan = {"ContractVersion": "1.0", "PolicyId": policy["Id"], "PolicyDigest": digest(policy),
            "DiffDigest": digest(paths), "Profile": profile, "ChangedPaths": paths,
            "MatchedRuleIds": [], "PlanDigest": "", "PolicySnapshot": policy}
    plan["PlanDigest"] = digest(plan)
    expected = {
        "RunId": "123/1", "BaseRevision": "synthetic-base", "SubjectRevision": "synthetic-subject",
        "WorkflowIdentity": "synthetic-workflow@immutable", "PolicyDigest": plan["PolicyDigest"],
        "AcceptanceProofDigest": "d" * 64, "OutputIdentity": "synthetic-output",
        "VerifierId": "synthetic-verifier", "VerifierVersion": "1", "Provider": "synthetic-provider",
        "CatalogueDigest": "b" * 64, "CapabilitiesDigest": "c" * 64, "AllocationPolicyDigest": "a" * 64,
        "ToolRootIdentity": "synthetic-tool", "SubjectRootIdentity": "synthetic-subject-root",
        "OutputParentIdentity": "synthetic-parent", "AllowReleaseValidatedNotAttested": False,
    }
    assertion = {key: value for key, value in expected.items()
                 if key not in ("PolicyDigest", "AllowReleaseValidatedNotAttested")}
    assertion.update({"SchemaVersion": "1.0", "VerifiedAtUtc": "2026-10-02T00:00:00+00:00"})
    manifest = {
        "ContractVersion": "1.0", "PlanDigest": plan["PlanDigest"], "ExecutionVerdict": "Passed",
        "ClaimKind": "TargetedComplete", "Eligibility": "PullRequestGate",
        "EnvelopeStatus": "ValidatedNotAttested", "ResourceResults": [],
        "SelectedObligationIds": ["obligation"], "ClosedObligationIds": ["obligation"],
        "UnmediatedObligationIds": [], "ProducerResults": [{"ProducerId": "producer", "Outcome": "Passed",
            "SatisfiedAssertionIds": [assertion_id], "Diagnostic": None, "Artifacts": None, "ElapsedMilliseconds": 0}],
        "Metrics": {"PlanningMilliseconds": 0, "ResourceReadinessMilliseconds": 0, "ProducerMilliseconds": 0,
                    "CleanupMilliseconds": 0, "TotalMilliseconds": 0, "CleanupCompleted": True,
                    "CleanupDiagnostic": None},
        "ManifestDigest": "", "Mode": "Trusted", "EnvelopeAssertion": assertion,
    }
    manifest["ManifestDigest"] = digest(manifest)
    return {"Schema": SCHEMA, "Plan": plan, "Manifest": manifest, "Expected": expected}


class ProtectedGateConsumerTests(unittest.TestCase):
    def assert_process(self, payload, code, diagnostic, arguments=()):
        result = subprocess.run([DOTNET, str(CONSUMER), *arguments], input=payload,
                                capture_output=True, timeout=45, check=False)
        self.assertEqual(result.returncode, code)
        expected_output = (diagnostic + "\n").encode("ascii")
        self.assertEqual(result.stdout, expected_output if code == 0 else b"")
        self.assertEqual(result.stderr, expected_output if code != 0 else b"")
        self.assertNotIn(CANARY.encode("ascii"), result.stdout + result.stderr)

    def test_synthetic_trusted_shape_allows_public_api_only(self):
        self.assert_process(canonical(synthetic_envelope()), 0, "EVIDENCE_GATE_ALLOWED")

    def test_current_expected_facts_are_compared_independently(self):
        for key in ("RunId", "BaseRevision", "SubjectRevision", "AcceptanceProofDigest", "OutputIdentity",
                    "WorkflowIdentity", "ToolRootIdentity", "SubjectRootIdentity", "OutputParentIdentity"):
            with self.subTest(field=key):
                envelope = synthetic_envelope()
                envelope["Expected"][key] = "e" * 64 if key.endswith("Digest") else "different-current-fact"
                self.assert_process(canonical(envelope), 1, "EVIDENCE_GATE_DENIED")

    def test_observation_remains_informational_and_denied(self):
        envelope = synthetic_envelope()
        manifest = envelope["Manifest"]
        manifest.update({"Mode": "Observation", "ClaimKind": "ObservationOnly", "Eligibility": "Informational",
                         "EnvelopeStatus": "NotRequired"})
        del manifest["EnvelopeAssertion"]
        manifest["ManifestDigest"] = ""
        manifest["ManifestDigest"] = digest(manifest)
        self.assert_process(canonical(envelope), 1, "EVIDENCE_GATE_DENIED")

    def test_missing_manifest_denied(self):
        envelope = synthetic_envelope()
        envelope["Manifest"] = None
        self.assert_process(canonical(envelope), 1, "EVIDENCE_GATE_DENIED")

    def test_recomputed_manifest_cannot_hide_failed_cleanup(self):
        envelope = synthetic_envelope()
        manifest = envelope["Manifest"]
        manifest["Metrics"]["CleanupCompleted"] = False
        manifest["ManifestDigest"] = ""
        manifest["ManifestDigest"] = digest(manifest)
        self.assert_process(canonical(envelope), 1, "EVIDENCE_GATE_DENIED")

    def test_structural_digest_drift_denied(self):
        envelope = synthetic_envelope()
        envelope["Manifest"]["ProducerResults"][0]["Diagnostic"] = CANARY
        self.assert_process(canonical(envelope), 1, "EVIDENCE_GATE_DENIED")

    def test_malformed_empty_and_trailing_documents_do_not_echo_input(self):
        for payload in (b"", b'{"Schema":"' + CANARY.encode() + b'",', b"null",
                        canonical(synthetic_envelope()) + b" {}"):
            with self.subTest(length=len(payload)):
                self.assert_process(payload, 2, "EVIDENCE_GATE_INPUT_REJECTED")

    def test_required_fields_and_null_constraints(self):
        for key in ("Schema", "Plan", "Manifest", "Expected"):
            with self.subTest(missing=key):
                envelope = synthetic_envelope()
                del envelope[key]
                self.assert_process(canonical(envelope), 2, "EVIDENCE_GATE_INPUT_REJECTED")
        for key in ("Schema", "Plan", "Expected"):
            with self.subTest(null=key):
                envelope = synthetic_envelope()
                envelope[key] = None
                self.assert_process(canonical(envelope), 2, "EVIDENCE_GATE_INPUT_REJECTED")

    def test_duplicate_keys_unsupported_versions_and_enum_reject(self):
        envelope = synthetic_envelope()
        payload = canonical(envelope)
        duplicate = payload[:-1] + b',"schema":"' + CANARY.encode() + b'"}'
        self.assert_process(duplicate, 2, "EVIDENCE_GATE_INPUT_REJECTED")
        for owner, key, value in ((envelope, "Schema", CANARY),
                                  (envelope["Plan"], "ContractVersion", "99.0"),
                                  (envelope["Manifest"], "ContractVersion", "99.0"),
                                  (envelope["Manifest"], "Mode", CANARY)):
            with self.subTest(field=key, value=value):
                modified = copy.deepcopy(envelope)
                target = modified if owner is envelope else modified["Plan"] if owner is envelope["Plan"] else modified["Manifest"]
                target[key] = value
                self.assert_process(canonical(modified), 2, "EVIDENCE_GATE_INPUT_REJECTED")

    def test_counted_whole_envelope_exact_limit_and_one_byte_over(self):
        payload = canonical(synthetic_envelope())
        exact = payload + b" " * (MAXIMUM_BYTES - len(payload))
        self.assert_process(exact, 0, "EVIDENCE_GATE_ALLOWED")
        # The secret canary occurs in a valid additive property; the byte ceiling rejects before deserialization.
        envelope = synthetic_envelope()
        envelope["SubjectDiagnostic"] = CANARY
        payload = canonical(envelope)
        self.assert_process(payload + b" " * (MAXIMUM_BYTES + 1 - len(payload)),
                            2, "EVIDENCE_GATE_INPUT_REJECTED")

    def test_path_arguments_cannot_select_expectations(self):
        self.assert_process(canonical(synthetic_envelope()), 2, "EVIDENCE_GATE_INPUT_REJECTED",
                            ("--expected", "/subject/" + CANARY))


def main():
    global CONSUMER, DOTNET
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--consumer", required=True, type=Path, help="Built standalone consumer DLL; no build is started.")
    parser.add_argument("--dotnet", default=shutil.which("dotnet"), help="Explicit dotnet host, or dotnet from PATH.")
    args = parser.parse_args()
    CONSUMER = args.consumer.resolve(strict=True)
    if not CONSUMER.is_file() or not args.dotnet:
        parser.error("A built consumer DLL and dotnet host are required.")
    DOTNET = args.dotnet
    result = unittest.TextTestRunner(verbosity=2).run(unittest.defaultTestLoader.loadTestsFromTestCase(ProtectedGateConsumerTests))
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    sys.exit(main())
