"""Tests for the trusted, non-claiming verifier observation builder."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-observation.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_observation", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
observation = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = observation
SPEC.loader.exec_module(observation)


class TrustedObservationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="trusted-observation-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.plan_path = self.root / "verified-plan.json"
        self.identity_path = self.root / "verifier-identity.json"
        self.subject_path = self.root / "subject-result.json"
        self.cleanup_path = self.root / "cleanup.json"
        self.artifacts_path = self.root / "artifacts"
        self.output_path = self.root / "observation.json"
        self.repository = "forge-trust/AppSurface"
        self.base_revision = "a" * 40
        self.head_revision = "b" * 40
        self.snapshot_sha256 = "c" * 64
        self.image_digest = "ghcr.io/forge-trust/appsurface-subject-native-validation@sha256:" + "d" * 64
        self.identity = {
            "EventName": "pull_request_target",
            "Repository": self.repository,
            "RunIdentity": {
                "HeadRepositoryId": 321,
                "PullRequestNumber": 777,
                "RepositoryId": 321,
                "TargetBranch": "main",
                "WorkflowRunAttempt": 2,
                "WorkflowRunId": 654321,
            },
            "SubjectJobId": "5678",
            "WorkflowId": "1234",
        }
        self.plan = self._plan("documentation-only")
        self.subject = self._subject("documentation-only")
        self._write_inputs()

    def _plan(self, profile_id: str) -> dict[str, object]:
        empty = profile_id == "documentation-only"
        selected_profile = {
            "Id": profile_id,
            "Scope": "Targeted",
            "Resources": [] if empty else [{"Id": "coverage"}],
            "Producers": [] if empty else [{"Id": "coverage"}],
            "Obligations": [] if empty else [{"Id": "coverage"}],
        }
        return {
            "BaseRevision": self.base_revision,
            "ChangedPaths": [],
            "ContractVersion": "2.0",
            "DiffDigest": "1" * 64,
            "HeadRevision": self.head_revision,
            "MatchedRuleIds": [],
            "NameStatusDigest": "2" * 64,
            "PlanDigest": "3" * 64,
            "PolicyDigest": "4" * 64,
            "PolicyId": "appsurface",
            "PolicySnapshot": {"Profiles": [selected_profile]},
            "Profile": selected_profile,
            "PullRequestRunIdentity": {
                "RepositoryId": 321,
                "HeadRepositoryId": 321,
                "PullRequestNumber": 777,
                "TargetBranch": "main",
                "WorkflowRunId": 654321,
                "WorkflowRunAttempt": 2,
            },
            "SourceDiffDigest": "5" * 64,
        }

    def _subject(self, profile_id: str) -> dict[str, object]:
        result: dict[str, object] = {
            "claimEligible": False,
            "execution": "completed",
            "exitCode": 0,
            "headRevision": self.head_revision,
            "mode": "SubjectSnapshot",
            "profileId": profile_id,
            "schemaVersion": 1,
            "snapshotSha256": self.snapshot_sha256,
            "workflowRunAttempt": "2",
            "workflowRunId": "654321",
            "diagnostic": {
                "code": "ASEHB010",
                "message": "Subject execution completed without a trusted evidence verifier.",
            },
        }
        if profile_id == "code-coverage":
            result.update({
                "snapshotFileCount": 14,
                "snapshotExpandedBytes": 1024,
                "stdoutBytes": 100,
                "stdoutSha256": "6" * 64,
                "stderrBytes": 0,
                "stderrSha256": "7" * 64,
            })
            binding = {
                "baseRevision": self.base_revision,
                "headRevision": self.head_revision,
                "profileId": profile_id,
                "snapshotSha256": self.snapshot_sha256,
                "workflowRunAttempt": "2",
                "workflowRunId": "654321",
            }
            artifact_index = []
            self.artifacts_path.mkdir(exist_ok=True)
            for basename, logical_name, relative_path in observation.ARTIFACT_INDEX:
                raw = ("fixed artifact: " + basename).encode("ascii")
                (self.artifacts_path / basename).write_bytes(raw)
                artifact_index.append({
                    "logicalName": logical_name,
                    "relativePath": relative_path,
                    "byteCount": len(raw),
                    "sha256": hashlib.sha256(raw).hexdigest(),
                })
            record = {
                "claimEligible": False,
                "profileId": "code-coverage",
                "schemaVersion": 1,
                "status": "completed",
                "steps": [
                    {
                        "exitCode": 0,
                        "name": name,
                        "outputBytes": 0,
                        "stderrSha256": "8" * 64,
                        "stdoutSha256": "9" * 64,
                    }
                    for name in ("dotnet-sdk-version", "dotnet-runtime-list", "offline-locked-restore", "coverage-run", "coverage-gate")
                ],
                "artifacts": artifact_index,
            }
            result["executionReceipt"] = self._receipt(binding, "record", record)
            envelope = {
                "schemaVersion": 1,
                "imageDigest": self.image_digest,
                "profileId": "code-coverage",
                "runnerEnvironment": "github-hosted",
                "runtime": "rootless-podman",
                "networkMode": "none",
                "rootfsReadOnlyConfigured": True,
                "subjectAndDiffReadOnlyConfigured": True,
                "scratchTmpfsQuotaVerified": True,
                "capabilityDropConfigured": True,
                "noNewPrivilegesConfigured": True,
                "pidIpcUtsUserNamespacesConfiguredPrivate": True,
                "resourceLimitsConfigured": True,
            }
            result["envelopeReceipt"] = self._receipt(binding, "observation", envelope)
        return result

    @staticmethod
    def _receipt(binding: dict[str, str], member: str, record: dict[str, object]) -> dict[str, object]:
        payload = {"binding": binding, member: record, "schemaVersion": 1}
        return {**payload, "sha256": hashlib.sha256(observation._canonical(payload)).hexdigest()}

    @staticmethod
    def _write_json(path: Path, value: object) -> None:
        path.write_bytes(observation._canonical(value))

    def _write_inputs(self) -> None:
        self._write_json(self.plan_path, self.plan)
        self._write_json(self.identity_path, self.identity)
        self.subject_path.write_bytes(observation._canonical(self.subject) + b"\n")

    def _build(self, **overrides: object) -> dict[str, object]:
        options: dict[str, object] = {
            "verified_plan_path": self.plan_path,
            "verifier_identity_path": self.identity_path,
            "subject_result_path": self.subject_path,
        }
        options.update(overrides)
        return observation.build_observation(**options)

    def _coverage_inputs(self) -> dict[str, object]:
        self.plan = self._plan("code-coverage")
        self.subject = self._subject("code-coverage")
        self._write_inputs()
        self.cleanup_path.write_bytes(observation._canonical({
            "schemaVersion": 1,
            "claimEligible": False,
            "published": False,
            "status": "complete",
            "trigger": "request",
            "containerStatus": "removed",
            "mountStatus": "unmounted",
            "scratchStatus": "removed",
            "failureCode": "none",
            "cleanupDeadlineSeconds": 600,
        }) + b"\n")
        return {
            "cleanup_record_path": self.cleanup_path,
            "subject_artifacts_directory": self.artifacts_path,
            "expected_image_digest": self.image_digest,
        }

    def test_documentation_observation_is_canonical_bounded_and_nonclaiming(self) -> None:
        self.assertEqual(
            {
                "HeadRepositoryId", "PullRequestNumber", "RepositoryId", "TargetBranch", "WorkflowRunAttempt", "WorkflowRunId",
            },
            set(self.identity["RunIdentity"]),
        )
        result = self._build()
        encoded = observation._canonical(result) + b"\n"

        self.assertLessEqual(len(encoded), observation.MAX_OUTPUT_BYTES)
        self.assertEqual(encoded, observation._canonical(json.loads(encoded)) + b"\n")
        self.assertIs(result["claimEligible"], False)
        self.assertEqual("documentation-only", result["profile"]["id"])
        self.assertIsNone(result["trustedPreflight"])
        self.assertEqual([], result["subjectArtifacts"])
        self.assertEqual(hashlib.sha256(self.plan_path.read_bytes()).hexdigest(), result["inputSha256"]["verifiedPlan"])
        self.assertNotIn(str(self.root), encoded.decode("ascii"))

    def test_coverage_binds_trusted_image_preflight_and_fixed_artifact_bytes(self) -> None:
        self.base_revision = "e" * 40
        self.head_revision = "f" * 40
        result = self._build(**self._coverage_inputs())

        self.assertIs(result["claimEligible"], False)
        self.assertEqual(self.base_revision, result["run"]["pullRequestIdentity"]["baseRevision"])
        self.assertEqual(self.head_revision, result["run"]["pullRequestIdentity"]["headRevision"])
        self.assertEqual({"subjectImageDigest": self.image_digest}, result["trustedPreflight"])
        self.assertEqual(
            hashlib.sha256(self.image_digest.encode("ascii")).hexdigest(),
            result["inputSha256"]["trustedPreflightImageDigest"],
        )
        self.assertEqual(3, len(result["subjectArtifacts"]))
        for artifact in result["subjectArtifacts"]:
            raw = (self.artifacts_path / artifact["name"]).read_bytes()
            self.assertEqual(hashlib.sha256(raw).hexdigest(), artifact["sha256"])

    def test_coverage_requires_preflight_digest_and_rejects_receipt_mismatch(self) -> None:
        inputs = self._coverage_inputs()
        with self.assertRaises(observation.ObservationError):
            self._build(cleanup_record_path=inputs["cleanup_record_path"], subject_artifacts_directory=inputs["subject_artifacts_directory"])

        with self.assertRaises(observation.ObservationError):
            self._build(**{**inputs, "expected_image_digest": self.image_digest[:-1] + "0"})

        self.plan["BaseRevision"] = "e" * 40
        self._write_json(self.plan_path, self.plan)
        with self.assertRaises(observation.ObservationError):
            self._build(**inputs)

    def test_documentation_only_forbids_coverage_inputs_and_unexpected_receipts(self) -> None:
        with self.assertRaises(observation.ObservationError):
            self._build(expected_image_digest=self.image_digest)

        self.subject["envelopeReceipt"] = {}
        self._write_inputs()
        with self.assertRaises(observation.ObservationError):
            self._build()

    def test_rejects_wrong_plan_or_subject_identity_bindings(self) -> None:
        self.identity["RunIdentity"]["WorkflowRunAttempt"] = 1
        self._write_inputs()
        with self.assertRaises(observation.ObservationError):
            self._build()

        self.identity["RunIdentity"]["WorkflowRunAttempt"] = 2
        self._write_inputs()
        self.subject["headRevision"] = "e" * 40
        self._write_json(self.subject_path, self.subject)
        with self.assertRaises(observation.ObservationError):
            self._build()

    def test_rejects_malformed_duplicate_and_oversized_inputs(self) -> None:
        self.plan_path.write_bytes(b'{"Profile":{},"Profile":{}}')
        with self.assertRaises(observation.ObservationError):
            self._build()

        self.plan_path.write_bytes(b"x" * (observation.MAX_PLAN_BYTES + 1))
        with self.assertRaises(observation.ObservationError):
            self._build()

    def test_rejects_symlinked_input_and_parent_paths(self) -> None:
        target = self.root / "target.json"
        target.write_bytes(self.plan_path.read_bytes())
        link = self.root / "linked-plan.json"
        link.symlink_to(target)
        with self.assertRaises(observation.ObservationError):
            self._build(verified_plan_path=link)

        linked_parent = self.root / "linked-parent"
        linked_parent.symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(observation.ObservationError):
            self._build(verified_plan_path=linked_parent / self.plan_path.name)

    def test_rejects_missing_extra_linked_and_digest_mismatched_artifacts(self) -> None:
        inputs = self._coverage_inputs()
        self.artifacts_path.joinpath("cobertura.xml").unlink()
        with self.assertRaises(observation.ObservationError):
            self._build(**inputs)

        self._coverage_inputs()
        (self.artifacts_path / "unexpected.bin").write_bytes(b"unexpected")
        with self.assertRaises(observation.ObservationError):
            self._build(**inputs)

        (self.artifacts_path / "unexpected.bin").unlink()
        (self.artifacts_path / "cobertura.xml").unlink()
        (self.artifacts_path / "cobertura.xml").symlink_to(self.artifacts_path / "gate-report.md")
        with self.assertRaises(observation.ObservationError):
            self._build(**inputs)

        self._coverage_inputs()
        (self.artifacts_path / "cobertura.xml").write_bytes(b"changed bytes")
        with self.assertRaises(observation.ObservationError):
            self._build(**inputs)

    def test_cli_writes_new_output_and_fails_without_disclosing_paths(self) -> None:
        command = [
            sys.executable, str(SCRIPT),
            "--verified-plan", str(self.plan_path),
            "--verifier-identity", str(self.identity_path),
            "--subject-result", str(self.subject_path),
            "--output", str(self.output_path),
        ]
        completed = subprocess.run(command, check=False, capture_output=True, text=True)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual(b"\n", self.output_path.read_bytes()[-1:])
        self.assertIn(b'"claimEligible":false', self.output_path.read_bytes())

        failed_output = self.root / "failed.json"
        completed = subprocess.run(command[:-2] + ["--expected-image-digest", self.image_digest, "--output", str(failed_output)], check=False, capture_output=True, text=True)
        self.assertEqual(2, completed.returncode)
        self.assertNotIn(str(self.root), completed.stderr)


if __name__ == "__main__":
    unittest.main()
