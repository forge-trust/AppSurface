"""Focused tests for the private evidence-gate controller handoff."""

from __future__ import annotations

from contextlib import redirect_stdout
import importlib.util
import hashlib
import io
import json
from pathlib import Path
import shutil
import sys
import subprocess
import tarfile
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "evidence-gate-handoff.py"
SPEC = importlib.util.spec_from_file_location("evidence_gate_handoff", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
handoff = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = handoff
SPEC.loader.exec_module(handoff)


class HandoffPathTests(unittest.TestCase):
    def test_evidence_json_matches_dotnet_escaping_for_git_path_characters(self) -> None:
        self.assertEqual(
            b'{"Value":"a\\u002Bb\\u0060\\u0022\\\\"}',
            handoff._canonical_evidence_json({"Value": 'a+b`"\\'}),
        )
        self.assertEqual(
            b'{"Value":"ends-with-backslash\\\\"}',
            handoff._canonical_evidence_json({"Value": "ends-with-backslash\\"}),
        )
        self.assertEqual(
            b'{"Value":"quote\\u0022plus\\u002Bbacktick\\u0060"}',
            handoff._canonical_evidence_json({"Value": 'quote"plus+backtick`'}),
        )
        self.assertEqual(
            b'{"Value":"\\\\u00af\\u002B"}',
            handoff._canonical_evidence_json({"Value": "\\u00af+"}),
        )
        self.assertEqual(
            b'{"Value":"caf\\u00E9"}',
            handoff._canonical_evidence_json({"Value": "café"}),
        )

    def test_new_output_directory_can_be_created_under_physical_parent(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-handoff-") as temporary:
            parent = Path(temporary).resolve() / "physical-parent"
            parent.mkdir()
            output = handoff._new_output_directory(parent / "new-handoff", "handoff output")

            self.assertEqual(parent / "new-handoff", output)
            self.assertFalse(output.exists())
            output.mkdir(mode=0o700)
            self.assertTrue(output.is_dir())


class HandoffArchiveTests(unittest.TestCase):
    def _git(self, directory: Path, *arguments: str) -> str:
        completed = subprocess.run(
            ["git", *arguments],
            cwd=directory,
            check=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        return completed.stdout.strip()

    def _write_plan(
        self,
        root: Path,
        capture: Path,
        *,
        profile_id: str = "code-coverage",
        snapshot_profile_id: str | None = None,
        scope: str = "Targeted",
        resources: list[object] | None = None,
        producers: list[object] | None = None,
        obligations: list[object] | None = None,
        identity_overrides: dict[str, object] | None = None,
        revision_overrides: dict[str, str] | None = None,
        source_diff_digest: str | None = None,
    ) -> Path:
        identity, _ = handoff._read_json(capture / "pull-request-run-identity.json", 64 * 1024, "test identity")
        source_diff = (capture / "source.diff").read_bytes()
        profile = {
            "Id": profile_id,
            "Scope": scope,
            "Resources": [] if resources is None else resources,
            "Producers": [] if producers is None else producers,
            "Obligations": [] if obligations is None else obligations,
        }
        snapshot_profile = dict(profile)
        snapshot_profile["Id"] = profile_id if snapshot_profile_id is None else snapshot_profile_id
        plan_identity = {
            key: identity[key]
            for key in (
                "RepositoryId",
                "HeadRepositoryId",
                "PullRequestNumber",
                "TargetBranch",
                "WorkflowRunId",
                "WorkflowRunAttempt",
            )
        }
        if identity_overrides:
            plan_identity.update(identity_overrides)
        plan = {
            "BaseRevision": identity["BaseRevision"],
            "ChangedPaths": [],
            "ContractVersion": "2.0",
            "DiffDigest": "d" * 64,
            "HeadRevision": identity["HeadRevision"],
            "MatchedRuleIds": [],
            "NameStatusDigest": "e" * 64,
            "PlanDigest": "b" * 64,
            "PolicyDigest": "a" * 64,
            "PolicyId": "test-policy",
            "PolicySnapshot": {
                "Id": "test-policy",
                "Version": "1",
                "ConservativeProfileId": profile_id,
                "Profiles": [snapshot_profile],
                "Rules": [],
            },
            "Profile": profile,
            "PullRequestRunIdentity": plan_identity,
            "SourceDiffDigest": hashlib.sha256(source_diff).hexdigest()
            if source_diff_digest is None
            else source_diff_digest,
        }
        if revision_overrides:
            plan.update(revision_overrides)
        path = root / "evidence-plan.json"
        path.write_bytes(handoff._canonical_evidence_json(plan))
        return path

    def _capture(self, root: Path, *, attributes: str | None = None) -> tuple[Path, Path, Path]:
        worktree = root / "worktree"
        capture = root / "capture"
        scripts = root / "trusted-scripts"
        worktree.mkdir(parents=True)
        capture.mkdir()
        scripts.mkdir()
        self._git(worktree, "init", "--quiet")
        self._git(worktree, "config", "user.name", "Evidence Gate Test")
        self._git(worktree, "config", "user.email", "evidence-gate@example.invalid")

        (worktree / "base.txt").write_text("base\n", encoding="utf-8")
        self._git(worktree, "add", "base.txt")
        self._git(worktree, "commit", "--quiet", "-m", "base")
        base_revision = self._git(worktree, "rev-parse", "HEAD")

        if attributes is not None:
            (worktree / ".gitattributes").write_text(attributes, encoding="utf-8")
        (worktree / "subject.txt").write_text("subject $Format:%H$\n", encoding="utf-8")
        (worktree / "nested").mkdir()
        (worktree / "nested" / "source.txt").write_text("nested source\n", encoding="utf-8")
        if attributes and "export-ignore" in attributes:
            (worktree / "omitted.txt").write_text("tracked but ignored by git archive\n", encoding="utf-8")
        self._git(worktree, "add", ".")
        self._git(worktree, "commit", "--quiet", "-m", "subject")
        head_revision = self._git(worktree, "rev-parse", "HEAD")
        self._git(worktree, "clone", "--bare", "--quiet", str(worktree), str(capture / "repository.git"))

        identity = {
            "BaseRevision": base_revision,
            "HeadRevision": head_revision,
            "HeadRepositoryId": 123,
            "PullRequestNumber": 7,
            "RepositoryId": 123,
            "TargetBranch": "main",
            "WorkflowRunAttempt": 1,
            "WorkflowRunId": 456,
        }
        (capture / "pull-request-run-identity.json").write_bytes(handoff._canonical_json(identity))
        (capture / "source.diff").write_bytes(b"bounded test diff\n")
        for name in handoff.TRUSTED_SUBJECT_FILES:
            shutil.copyfile(SCRIPT.parent / name, scripts / name)
        plan_file = self._write_plan(root, capture)
        return capture, scripts, plan_file

    def _execute(self, output: Path, root: Path, *, image_digest: str | None = None, environment: dict[str, str] | None = None) -> tuple[int, Path]:
        result_path = root / "subject-result.json"
        exit_code = handoff.execute_handoff(
            handoff_directory=output,
            subject_checkout=root / "subject-checkout",
            scratch_directory=root / "subject-scratch",
            result_path=result_path,
            image_digest=image_digest,
            environment=environment
            or {
                "GITHUB_RUN_ID": "456",
                "GITHUB_RUN_ATTEMPT": "1",
                "GITHUB_REPOSITORY_ID": "123",
            },
        )
        return exit_code, result_path

    def _create(self, capture: Path, scripts: Path, plan_file: Path, output: Path) -> str:
        return handoff.create_handoff(
            capture_directory=capture,
            trusted_scripts_directory=scripts,
            output_directory=output,
            plan_file=plan_file,
        )

    def _write_test_launcher(
        self,
        scripts: Path,
        *,
        exit_code: int = 0,
        include_record: bool = True,
        step_mutation: str | None = None,
    ) -> None:
        step_names = (
            "dotnet-sdk-version",
            "dotnet-runtime-list",
            "offline-locked-restore",
            "coverage-run",
            "coverage-gate",
        )
        execution_record = {
            "claimEligible": False,
            "profileId": "code-coverage",
            "schemaVersion": 1,
            "status": "completed",
            "steps": [
                {
                    "exitCode": 0,
                    "name": name,
                    "outputBytes": 0,
                    "stderrSha256": "0" * 64,
                    "stdoutSha256": "0" * 64,
                }
                for name in step_names
            ],
        }
        (scripts / "evidence-gate-subject.py").write_text(
            f"""from types import SimpleNamespace

class SubjectLimits:
    def __init__(self, **values):
        self.values = values

def launch_subject(*, subject_checkout, image_digest, scratch_directory, profile_id, limits, _environment):
    with open(_environment['TEST_PROFILE_LOG'], 'w', encoding='utf-8') as log:
        log.write(profile_id)
    execution_record = {execution_record!r}
    if {step_mutation!r} == 'omit-last':
        execution_record['steps'].pop()
    elif {step_mutation!r} == 'rename-first':
        execution_record['steps'][0]['name'] = 'unexpected-step'
    return SimpleNamespace(
        exit_code={exit_code},
        stdout=b'completed',
        stderr=b'',
        execution_record=execution_record if {exit_code} == 0 and {include_record} else None,
    )
""",
            encoding="utf-8",
        )

    @staticmethod
    def _resign_execution_receipt(result: dict[str, object]) -> None:
        receipt = result["executionReceipt"]
        assert isinstance(receipt, dict)
        payload = {
            "binding": receipt["binding"],
            "record": receipt["record"],
            "schemaVersion": receipt["schemaVersion"],
        }
        receipt["sha256"] = hashlib.sha256(handoff._canonical_json(payload)).hexdigest()

    def test_valid_handoff_round_trip_accepts_sha256_source_diff(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-round-trip-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root)
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"

            self.assertEqual(
                "captured",
                self._create(capture, scripts, plan_file, output),
            )

            manifest, _ = handoff._read_json(output / "handoff.json", 128 * 1024, "handoff manifest")
            identity, archive, evidence_plan = handoff._validate_bundle_directory(output, manifest)
            self.assertIsNotNone(identity)
            self.assertEqual("code-coverage", evidence_plan["Profile"]["Id"])
            self.assertEqual(identity["HeadRevision"], manifest["HeadRevision"])
            self.assertRegex(manifest["SourceDiffSha256"], r"^[0-9a-f]{64}$")
            self.assertEqual(len(plan_file.read_bytes()), manifest["EvidencePlanBytes"])
            self.assertEqual(hashlib.sha256(plan_file.read_bytes()).hexdigest(), manifest["EvidencePlanSha256"])
            self.assertIsNotNone(archive)

            exit_code, result_path = self._execute(output, root)

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("ASEHB008", result["diagnostic"]["code"])

    def test_trusted_verifier_copies_only_a_bundle_matching_fresh_git_state(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-verify-handoff-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, profile_id="documentation-only")
            output = root / "handoff"
            self._create(capture, scripts, plan_file, output)
            subject_exit, subject_result = self._execute(output, root)
            self.assertEqual(0, subject_exit)
            verified_plan = root / "verified-plan.json"

            with redirect_stdout(io.StringIO()):
                self.assertEqual(
                    0,
                    handoff.main(
                        [
                            "verify",
                            "--handoff-directory", str(output),
                            "--fresh-capture-directory", str(capture),
                            "--subject-result", str(subject_result),
                            "--output-plan", str(verified_plan),
                        ]
                    ),
                )

            self.assertEqual(plan_file.read_bytes(), verified_plan.read_bytes())
            with self.assertRaises(handoff.HandoffError):
                handoff.verify_handoff(
                    handoff_directory=output,
                    fresh_capture_directory=capture,
                    subject_result_file=subject_result,
                    output_plan=verified_plan,
                )

    def test_trusted_verifier_rejects_stale_identity_or_changed_diff(self) -> None:
        for mismatch in ("identity", "diff"):
            with self.subTest(mismatch=mismatch), tempfile.TemporaryDirectory(prefix="evidence-gate-stale-handoff-") as temporary:
                root = Path(temporary).resolve()
                capture, scripts, _ = self._capture(root)
                plan_file = self._write_plan(root, capture, profile_id="documentation-only")
                output = root / "handoff"
                self._create(capture, scripts, plan_file, output)
                subject_exit, subject_result = self._execute(output, root)
                self.assertEqual(0, subject_exit)
                verified_plan = root / "verified-plan.json"
                if mismatch == "identity":
                    identity, _ = handoff._read_json(capture / "pull-request-run-identity.json", 64 * 1024, "test identity")
                    identity["WorkflowRunAttempt"] = 2
                    (capture / "pull-request-run-identity.json").write_bytes(handoff._canonical_json(identity))
                else:
                    (capture / "source.diff").write_bytes(b"a later exact source diff\n")

                with self.assertRaises(handoff.HandoffError):
                    handoff.verify_handoff(
                        handoff_directory=output,
                        fresh_capture_directory=capture,
                        subject_result_file=subject_result,
                        output_plan=verified_plan,
                    )
                self.assertFalse(verified_plan.exists())

    def test_trusted_verifier_rejects_a_forged_subject_result(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-forged-result-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, profile_id="documentation-only")
            output = root / "handoff"
            self._create(capture, scripts, plan_file, output)
            subject_exit, subject_result = self._execute(output, root)
            self.assertEqual(0, subject_exit)
            valid_result = json.loads(subject_result.read_bytes())
            for field, value in (
                ("headRevision", "0" * 40),
                ("workflowRunAttempt", "2"),
                ("snapshotSha256", "0" * 64),
                ("profileId", "code-coverage"),
                ("claimEligible", True),
                ("execution", "failed"),
                ("exitCode", 2),
                ("diagnostic", {"code": "ASEHB009", "message": "failed"}),
            ):
                with self.subTest(field=field):
                    result = dict(valid_result, **{field: value})
                    subject_result.write_bytes(handoff._canonical_json(result) + b"\n")
                    with self.assertRaises(handoff.HandoffError):
                        handoff.verify_handoff(
                            handoff_directory=output,
                            fresh_capture_directory=capture,
                            subject_result_file=subject_result,
                            output_plan=root / "verified-plan.json",
                        )
                    self.assertFalse((root / "verified-plan.json").exists())

    def test_empty_documentation_only_profile_succeeds_without_oci(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-docs-only-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, profile_id="documentation-only")
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"
            self._create(capture, scripts, plan_file, output)

            exit_code, result_path = self._execute(output, root)

            self.assertEqual(0, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("completed", result["execution"])
            self.assertEqual(0, result["exitCode"])
            self.assertEqual("documentation-only", result["profileId"])
            self.assertEqual("ASEHB010", result["diagnostic"]["code"])
            self.assertFalse((root / "subject-checkout").exists())
            self.assertFalse((root / "subject-scratch").exists())

    def test_code_coverage_profile_is_passed_to_the_bounded_launcher(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-code-coverage-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root)
            self._write_test_launcher(scripts)
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"
            self._create(capture, scripts, plan_file, output)
            profile_log = root / "selected-profile.txt"

            exit_code, result_path = self._execute(
                output,
                root,
                image_digest="sha256:" + "f" * 64,
                environment={
                    "GITHUB_RUN_ID": "456",
                    "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_REPOSITORY_ID": "123",
                    "TEST_PROFILE_LOG": str(profile_log),
                },
            )

            self.assertEqual("code-coverage", profile_log.read_text(encoding="utf-8"))
            self.assertEqual(0, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("completed", result["execution"])
            self.assertEqual("code-coverage", result["profileId"])
            self.assertEqual(1, result["executionReceipt"]["schemaVersion"])
            self.assertEqual(
                handoff.SUBJECT_RESULT_STEP_NAMES,
                tuple(step["name"] for step in result["executionReceipt"]["record"]["steps"]),
            )
            self.assertRegex(result["executionReceipt"]["sha256"], r"^[0-9a-f]{64}$")
            verified_plan = root / "verified-plan.json"
            handoff.verify_handoff(
                handoff_directory=output,
                fresh_capture_directory=capture,
                subject_result_file=result_path,
                output_plan=verified_plan,
            )
            self.assertEqual(plan_file.read_bytes(), verified_plan.read_bytes())

    def test_code_coverage_verifier_rejects_missing_or_tampered_execution_receipts(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-receipt-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root)
            self._write_test_launcher(scripts)
            output = root / "handoff"
            self._create(capture, scripts, plan_file, output)
            profile_log = root / "selected-profile.txt"
            exit_code, result_path = self._execute(
                output,
                root,
                image_digest="sha256:" + "f" * 64,
                environment={
                    "GITHUB_RUN_ID": "456",
                    "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_REPOSITORY_ID": "123",
                    "TEST_PROFILE_LOG": str(profile_log),
                },
            )
            self.assertEqual(0, exit_code)
            valid_result = json.loads(result_path.read_bytes())
            receipt_mutations = {
                "omitted-receipt": lambda value: value.pop("executionReceipt"),
                "omitted-step": lambda value: value["executionReceipt"]["record"]["steps"].pop(),
                "duplicate-step": lambda value: value["executionReceipt"]["record"]["steps"][1].update(
                    name=value["executionReceipt"]["record"]["steps"][0]["name"]
                ),
                "reordered-step": lambda value: value["executionReceipt"]["record"]["steps"].__setitem__(
                    slice(0, 2),
                    reversed(value["executionReceipt"]["record"]["steps"][:2]),
                ),
                "nonzero-step": lambda value: value["executionReceipt"]["record"]["steps"][4].update(exitCode=1),
                "wrong-run-id": lambda value: value["executionReceipt"]["binding"].update(workflowRunId="999"),
                "wrong-attempt": lambda value: value["executionReceipt"]["binding"].update(workflowRunAttempt="2"),
                "wrong-base": lambda value: value["executionReceipt"]["binding"].update(baseRevision="0" * 40),
                "wrong-head": lambda value: value["executionReceipt"]["binding"].update(headRevision="0" * 40),
                "wrong-profile": lambda value: value["executionReceipt"]["binding"].update(profileId="documentation-only"),
                "wrong-snapshot": lambda value: value["executionReceipt"]["binding"].update(snapshotSha256="0" * 64),
                "wrong-receipt-schema": lambda value: value["executionReceipt"].update(schemaVersion=2),
                "claimable-record": lambda value: value["executionReceipt"]["record"].update(claimEligible=True),
            }
            for name, mutate in receipt_mutations.items():
                with self.subTest(mutation=name):
                    result = json.loads(json.dumps(valid_result))
                    mutate(result)
                    if name not in {"omitted-receipt", "wrong-receipt-schema"}:
                        self._resign_execution_receipt(result)
                    result_path.write_bytes(handoff._canonical_json(result) + b"\n")
                    with self.assertRaises(handoff.HandoffError):
                        handoff.verify_handoff(
                            handoff_directory=output,
                            fresh_capture_directory=capture,
                            subject_result_file=result_path,
                            output_plan=root / f"verified-{name}.json",
                        )
                    self.assertFalse((root / f"verified-{name}.json").exists())

            digest_tampered = json.loads(json.dumps(valid_result))
            digest_tampered["executionReceipt"]["sha256"] = "0" * 64
            result_path.write_bytes(handoff._canonical_json(digest_tampered) + b"\n")
            with self.assertRaisesRegex(handoff.HandoffError, "digest does not match"):
                handoff.verify_handoff(
                    handoff_directory=output,
                    fresh_capture_directory=capture,
                    subject_result_file=result_path,
                    output_plan=root / "verified-digest-tampered.json",
                )
            self.assertFalse((root / "verified-digest-tampered.json").exists())

    def test_code_coverage_handoff_fails_without_or_with_invalid_step_proof(self) -> None:
        scenarios = (
            ("missing-record", {"include_record": False}),
            ("missing-step", {"step_mutation": "omit-last"}),
            ("renamed-step", {"step_mutation": "rename-first"}),
        )
        for name, options in scenarios:
            with self.subTest(scenario=name), tempfile.TemporaryDirectory(prefix="evidence-gate-step-failure-") as temporary:
                root = Path(temporary).resolve()
                capture, scripts, plan_file = self._capture(root)
                self._write_test_launcher(scripts, **options)
                output = root / "handoff"
                self._create(capture, scripts, plan_file, output)
                profile_log = root / "selected-profile.txt"
                exit_code, result_path = self._execute(
                    output,
                    root,
                    image_digest="sha256:" + "f" * 64,
                    environment={
                        "GITHUB_RUN_ID": "456",
                        "GITHUB_RUN_ATTEMPT": "1",
                        "GITHUB_REPOSITORY_ID": "123",
                        "TEST_PROFILE_LOG": str(profile_log),
                    },
                )
                self.assertEqual(2, exit_code)
                result = json.loads(result_path.read_bytes())
                self.assertFalse(result["claimEligible"])
                self.assertEqual("failed", result["execution"])
                self.assertNotIn("executionReceipt", result)
                self.assertEqual("ASEHB009", result["diagnostic"]["code"])

    def test_failed_bounded_launcher_returns_two_and_remains_non_claiming(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-launch-failure-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root)
            self._write_test_launcher(scripts, exit_code=17)
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"
            self._create(capture, scripts, plan_file, output)
            profile_log = root / "selected-profile.txt"

            exit_code, result_path = self._execute(
                output,
                root,
                image_digest="sha256:" + "f" * 64,
                environment={
                    "GITHUB_RUN_ID": "456",
                    "GITHUB_RUN_ATTEMPT": "1",
                    "GITHUB_REPOSITORY_ID": "123",
                    "TEST_PROFILE_LOG": str(profile_log),
                },
            )

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["execution"])
            self.assertEqual(17, result["exitCode"])
            self.assertEqual("ASEHB009", result["diagnostic"]["code"])

    def test_fork_observation_remains_non_claiming_and_exits_two(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-fork-observation-") as temporary:
            root = Path(temporary).resolve()
            capture = root / "capture"
            scripts = root / "trusted-scripts"
            output_parent = root / "outputs"
            capture.mkdir()
            scripts.mkdir()
            output_parent.mkdir()
            (capture / "observation.json").write_bytes(
                handoff._canonical_json(
                    {"Mode": "ObservationOnly", "WorkflowRunAttempt": 1, "WorkflowRunId": 456}
                )
            )
            shutil.copyfile(SCRIPT, scripts / "evidence-gate-handoff.py")
            output = output_parent / "handoff"

            self.assertEqual(
                "observation",
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output,
                ),
            )
            exit_code, result_path = self._execute(
                output,
                root,
                environment={"GITHUB_RUN_ID": "456", "GITHUB_RUN_ATTEMPT": "1"},
            )

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("observation-only", result["execution"])
            self.assertEqual("ASEHB007", result["diagnostic"]["code"])

    def test_missing_plan_is_rejected_for_same_repository_capture(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-missing-plan-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "EvidencePlan is required"):
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                )

    def test_tampered_handoff_plan_fails_execution(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-plan-tamper-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root)
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"
            self._create(capture, scripts, plan_file, output)
            plan, _ = handoff._read_evidence_plan(output / "evidence-plan.json")
            tampered = dict(plan)
            tampered["PolicyDigest"] = "c" * 64
            (output / "evidence-plan.json").write_bytes(handoff._canonical_evidence_json(tampered))

            exit_code, result_path = self._execute(output, root)

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["execution"])
            self.assertEqual("ASEHB001", result["diagnostic"]["code"])

    def test_revision_mismatch_in_plan_is_rejected_during_capture(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-plan-revision-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, revision_overrides={"BaseRevision": "0" * 40})
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "BaseRevision does not match"):
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                    plan_file=plan_file,
                )

    def test_pull_request_identity_mismatch_in_plan_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-plan-identity-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, identity_overrides={"WorkflowRunAttempt": 2})
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "run identity does not match"):
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                    plan_file=plan_file,
                )

    def test_profile_not_closed_by_policy_snapshot_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-plan-profile-mismatch-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, snapshot_profile_id="documentation-only")
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "not closed by its policy snapshot"):
                handoff.create_handoff(
                    capture_directory=capture,
                    trusted_scripts_directory=scripts,
                    output_directory=output_parent / "handoff",
                    plan_file=plan_file,
                )

    def test_unsupported_policy_profile_fails_closed_without_substitute(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-unsupported-profile-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, _ = self._capture(root)
            plan_file = self._write_plan(root, capture, profile_id="postgresql-integration")
            output_parent = root / "outputs"
            output_parent.mkdir()
            output = output_parent / "handoff"
            self._create(capture, scripts, plan_file, output)

            exit_code, result_path = self._execute(output, root)

            self.assertEqual(2, exit_code)
            result = json.loads(result_path.read_text(encoding="utf-8"))
            self.assertFalse(result["claimEligible"])
            self.assertEqual("failed", result["execution"])
            self.assertEqual("postgresql-integration", result["profileId"])
            self.assertEqual("ASEHB008", result["diagnostic"]["code"])
            self.assertFalse((root / "subject-checkout").exists())

    def test_export_ignore_omission_is_rejected_against_git_tree(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-export-ignore-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root, attributes="omitted.txt export-ignore\n")
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "omits or adds a regular file") as failure:
                self._create(capture, scripts, plan_file, output_parent / "handoff")

            self.assertEqual("ASEHB004", failure.exception.code)

    def test_export_subst_transformation_is_rejected_against_git_blob(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-export-subst-") as temporary:
            root = Path(temporary).resolve()
            capture, scripts, plan_file = self._capture(root, attributes="subject.txt export-subst\n")
            output_parent = root / "outputs"
            output_parent.mkdir()

            with self.assertRaisesRegex(handoff.HandoffError, "differs from its captured Git blob") as failure:
                self._create(capture, scripts, plan_file, output_parent / "handoff")

            self.assertEqual("ASEHB004", failure.exception.code)

    def test_archive_case_collision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory(prefix="evidence-gate-case-collision-") as temporary:
            archive_path = Path(temporary) / "snapshot.tar"
            with tarfile.open(archive_path, "w") as archive:
                for name in ("Directory", "directory"):
                    member = tarfile.TarInfo(name)
                    member.type = tarfile.DIRTYPE
                    archive.addfile(member)

            with self.assertRaisesRegex(handoff.HandoffError, "case-colliding") as failure:
                handoff._scan_archive(archive_path)

            self.assertEqual("ASEHB004", failure.exception.code)


if __name__ == "__main__":
    unittest.main()
