#!/usr/bin/env python3
"""Executable transport regression tests for the #798 Tailwind workflows.

Run from the repository root with:
  python3 .github/tests/tailwind_workflow_transport_tests.py

The resolver tests use a temporary fake `gh` on PATH and paginated JSON pages.
Workflow checks bind producer and host artifact IDs through actual outputs and
download fields, and execute the native runner preflight shell extracted from YAML.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
RESOLVER = ROOT / ".github/scripts/resolve-tailwind-release-artifacts.sh"
NATIVE = ROOT / ".github/workflows/tailwind-native-host-evidence.yml"
PUBLISHERS = [
    ROOT / ".github/workflows/nuget-prerelease-publish.yml",
    ROOT / ".github/workflows/nuget-stable-publish.yml",
]
RIDS = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64"]


FAKE_GH = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
if not any(args[index:index + 2] == ["--method", "GET"] for index in range(len(args) - 1)):
    raise SystemExit("artifact and job pagination must explicitly use GET")
calls = pathlib.Path(os.environ["GH_CALLS"])
with calls.open("a") as stream:
    stream.write(json.dumps(args) + "\n")
endpoint = next((item for item in args if item.startswith("repos/")), "")
match = __import__("re").search(r"actions/artifacts/(\d+)/zip$", endpoint)
if match:
    raise SystemExit("unexpected artifact ZIP request: " + endpoint)
collection = "jobs" if "/attempts/" in endpoint else "artifacts"
pages_path = os.environ["GH_JOBS"] if collection == "jobs" else os.environ["GH_PAGES"]
page_number = next((int(args[index + 1].split("=", 1)[1]) for index, value in enumerate(args[:-1])
                    if value == "-F" and args[index + 1].startswith("page=")), 1)
if os.environ.get("GH_FAIL_AFTER_PAGE") == str(page_number - 1):
    print("simulated page API failure", file=sys.stderr)
    raise SystemExit(44)
pages = json.loads(pathlib.Path(pages_path).read_text())
page = pages[page_number - 1] if page_number <= len(pages) else {}
print(json.dumps(page.get(collection, [])))
'''


def artifact(artifact_id: int, name: str, *, expired: bool = False) -> dict:
    return {
        "id": artifact_id,
        "name": name,
        "expired": expired,
        "expires_at": "2026-10-01T00:00:00Z",
    }


class WorkflowTransportTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="tailwind-transport-", dir=Path(__file__).parent)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        fake = self.bin / "gh"
        fake.write_text(FAKE_GH)
        fake.chmod(0o755)
        self.calls = self.root / "calls.jsonl"
        self.pages = self.root / "pages.json"
        self.jobs = self.root / "jobs.json"
        self.jobs.write_text("[]")
        self.fail_after_page: int | None = None
        jq_dir = self.bin / "jq"
        jq_dir.write_text("#!/bin/sh\nexec /usr/bin/jq \"$@\"\n")
        jq_dir.chmod(0o755)

    def tearDown(self) -> None:
        self.temp.cleanup()

    def run_resolver(self, *args: str, pages: list[dict]) -> subprocess.CompletedProcess[str]:
        self.pages.write_text(json.dumps(pages))
        env = os.environ.copy()
        env.update(
            PATH=f"{self.bin}{os.pathsep}{env.get('PATH', '')}",
            GH_PAGES=str(self.pages),
            GH_JOBS=str(self.jobs),
            GH_CALLS=str(self.calls),
        )
        if self.fail_after_page is not None:
            env["GH_FAIL_AFTER_PAGE"] = str(self.fail_after_page)
        return subprocess.run(
            ["bash", str(RESOLVER), *args],
            cwd=ROOT,
            env=env,
            text=True,
            capture_output=True,
            check=False,
        )

    def calls_read(self) -> list[list[str]]:
        return [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def test_complete_pagination_resolves_producer_by_exact_id(self) -> None:
        name = "appsurface-prerelease-packages-42"
        decoys = [artifact(100 + index, f"unrelated-{index}") for index in range(100)]
        output = self.root / "producer.out"
        result = self.run_resolver(
            "resolve-producer", "--repository", "forge-trust/AppSurface", "--run-id", "42",
            "--run-attempt", "2", "--name", name, "--output", str(output),
            pages=[{"artifacts": decoys}, {"artifacts": [artifact(801, name)]}],
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("producer_reused=true", output.read_text())
        self.assertIn("producer_artifact_id=801", output.read_text())
        self.assertFalse(any("/zip" in " ".join(call) for call in self.calls_read()))

    def test_producer_zero_one_multiple_and_expired_outcomes(self) -> None:
        name = "producer"
        base = ["resolve-producer", "--repository", "org/repo", "--run-id", "7", "--run-attempt", "1", "--name", name, "--output"]
        missing = self.run_resolver(*base, str(self.root / "zero"), pages=[{"artifacts": []}])
        self.assertEqual(missing.returncode, 0, missing.stderr)
        self.assertIn("producer_found=false", (self.root / "zero").read_text())
        one = self.run_resolver(*base, str(self.root / "one"), pages=[{"artifacts": [artifact(11, name)]}])
        self.assertEqual(one.returncode, 0, one.stderr)
        multi = self.run_resolver(*base, str(self.root / "multi"), pages=[{"artifacts": [artifact(11, name), artifact(12, name)]}])
        self.assertNotEqual(multi.returncode, 0)
        expired = self.run_resolver(*base, str(self.root / "expired"), pages=[{"artifacts": [artifact(11, name, expired=True)]}])
        self.assertNotEqual(expired.returncode, 0)
        self.assertIn("expired", expired.stderr)

    def test_partial_api_pagination_failure_cannot_select_an_early_match(self) -> None:
        self.fail_after_page = 0
        result = self.run_resolver(
            "resolve-producer", "--repository", "org/repo", "--run-id", "7", "--run-attempt", "1",
            "--name", "producer", "--output", str(self.root / "incomplete"),
            pages=[{"artifacts": [artifact(77, "producer")]}, {"artifacts": []}],
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.root / "incomplete").exists())

    def test_later_attempt_repack_requires_every_prior_upload_and_start_to_be_skipped(self) -> None:
        self.jobs.write_text(json.dumps([{"jobs": [
            {"name": "pack-and-verify", "steps": [{"name": "Upload frozen producer bundle", "conclusion": "skipped"}]},
            {"name": "publish-nuget", "steps": [{"name": "Upload publication-start receipt", "conclusion": "skipped"}]},
        ]}]))
        result = self.run_resolver(
            "resolve-producer", "--repository", "org/repo", "--run-id", "9", "--run-attempt", "2",
            "--name", "producer", "--output", str(self.root / "out"), pages=[{"artifacts": []}],
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("producer_found=false", (self.root / "out").read_text())

    def test_ambiguous_prior_upload_or_publication_start_blocks_repack(self) -> None:
        for upload, start in [("success", "skipped"), ("skipped", "success"), ("unknown", "skipped"), ("skipped", "unknown")]:
            with self.subTest(upload=upload, start=start):
                self.jobs.write_text(json.dumps([{"jobs": [
                    {"name": "pack-and-verify", "steps": [{"name": "Upload frozen producer bundle", "conclusion": upload}]},
                    {"name": "publish-nuget", "steps": [{"name": "Upload publication-start receipt", "conclusion": start}]},
                ]}]))
                result = self.run_resolver(
                    "resolve-producer", "--repository", "org/repo", "--run-id", "9", "--run-attempt", "2",
                    "--name", "producer", "--output", str(self.root / f"out-{upload}-{start}"), pages=[{"artifacts": []}],
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("attempt 1", result.stderr.lower())

    def test_original_publication_start_selects_exact_named_receipt(self) -> None:
        name = "appsurface-publication-start-42"
        result = self.run_resolver(
            "resolve-publication", "--repository", "org/repo", "--run-id", "42", "--run-attempt", "2",
            "--name", name, "--output", str(self.root / "start"),
            pages=[{"artifacts": [artifact(70, "another-attempt"), artifact(71, name)]}],
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        output = (self.root / "start").read_text()
        self.assertIn("publication_found=true", output)
        self.assertIn("publication_artifact_id=71", output)

    def test_missing_duplicate_and_expired_start_receipts_fail_closed(self) -> None:
        name = "appsurface-publication-start-42"
        for index, rows in enumerate(([], [artifact(71, name), artifact(72, name)], [artifact(71, name, expired=True)])):
            with self.subTest(index=index):
                result = self.run_resolver(
                    "resolve-publication", "--repository", "org/repo", "--run-id", "42", "--run-attempt", "1",
                    "--name", name, "--output", str(self.root / f"start-{index}"), pages=[{"artifacts": rows}],
                )
                if index == 0:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("publication_found=false", (self.root / "start-0").read_text())
                else:
                    self.assertNotEqual(result.returncode, 0)

    def test_native_exact_id_map_requires_all_five_current_invocation_artifacts(self) -> None:
        invocation = "900-44-2-tailwind-native"
        rows = [artifact(1000 + index, f"tailwind-native-host-44-{invocation}-{rid}") for index, rid in enumerate(RIDS)]
        out = self.root / "native.out"
        result = self.run_resolver(
            "resolve-native-evidence", "--repository", "org/repo", "--run-id", "44", "--invocation", invocation,
            "--output", str(out),
            pages=[{"artifacts": [artifact(index + 2000, f"unrelated-{index}") for index in range(100)]}, {"artifacts": rows}],
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        host_map = json.loads(Path(str(out) + ".json").read_text())
        self.assertEqual(len(host_map), len(RIDS))
        self.assertEqual({entry["rid"]: entry["artifactId"] for entry in host_map},
                         {rid: str(1000 + index) for index, rid in enumerate(RIDS)})
        self.assertTrue(all(isinstance(entry["artifactId"], str)
                            and re.fullmatch(r"[1-9][0-9]*", entry["artifactId"])
                            for entry in host_map))
        self.assertFalse(any("/zip" in " ".join(call) for call in self.calls_read()))

    def test_missing_or_ambiguous_host_artifacts_fail_and_report_diagnostics(self) -> None:
        invocation = "p-5-1-tailwind-native"
        name = f"tailwind-native-host-5-{invocation}-linux-x64"
        cases = [
            [artifact(1, name, expired=True)],
            [artifact(1, name), artifact(2, name)],
            [],
        ]
        invocation_rows = [
            artifact(10 + index, f"tailwind-native-host-5-{invocation}-{rid}")
            for index, rid in enumerate(RIDS) if rid != "linux-x64"
        ]
        for index, rows in enumerate(cases):
            with self.subTest(index=index):
                output = self.root / f"missing-{index}"
                result = self.run_resolver(
                    "resolve-native-evidence", "--repository", "org/repo", "--run-id", "5", "--invocation", invocation,
                    "--output", str(output), pages=[{"artifacts": invocation_rows + rows}],
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("incomplete", result.stderr)
                self.assertIn("linux-x64", output.read_text())

    def test_native_workflow_preflight_validates_runner_map_and_manual_rehearsal_scope(self) -> None:
        workflow = NATIVE.read_text()
        marker = "      - name: Validate configured native runner map\n"
        self.assertIn(marker, workflow, "runner-map preflight step must exist")
        step_start = workflow.index(marker) + len(marker)
        step_end = workflow.find("\n      - name:", step_start)
        body = workflow[step_start:step_end if step_end >= 0 else len(workflow)]
        run_marker = "        run: |\n"
        self.assertIn(run_marker, body, "preflight must contain an executable shell check")
        run_start = body.index(run_marker) + len(run_marker)
        script_lines = []
        for line in body[run_start:].splitlines():
            if line and not line.startswith("          "):
                break
            script_lines.append(line[10:] if line.startswith("          ") else "")
        script = "\n".join(script_lines)
        valid_map = {rid: ["self-hosted", rid] for rid in RIDS}
        for config, event, ref, repository, should_pass in [
            (json.dumps(valid_map), "push", "refs/heads/main", "forge-trust/AppSurface", True),
            ("", "push", "refs/heads/main", "forge-trust/AppSurface", False),
            (json.dumps({**valid_map, "extra": "runner"}), "push", "refs/heads/main", "forge-trust/AppSurface", False),
            (json.dumps({**valid_map, "win-x64": []}), "push", "refs/heads/main", "forge-trust/AppSurface", False),
            (json.dumps(valid_map), "workflow_dispatch", "refs/heads/main", "forge-trust/AppSurface", True),
            (json.dumps(valid_map), "workflow_dispatch", "refs/heads/feature", "forge-trust/AppSurface", False),
            (json.dumps(valid_map), "workflow_dispatch", "refs/heads/main", "other/repo", False),
        ]:
            with self.subTest(event=event, ref=ref, repository=repository, config=config[:24]):
                with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=self.root) as output:
                    env = os.environ.copy()
                    rehearsal = event == "workflow_dispatch" and ("refs/heads/main" not in ref or repository != "forge-trust/AppSurface")
                    env.update(CONFIGURED_RUNNERS=config, REHEARSAL="true" if rehearsal else "false",
                               EVENT_NAME=event, REF=ref, REPOSITORY=repository, RETENTION_DAYS="30", GITHUB_OUTPUT=output.name)
                    result = subprocess.run(["bash", "-c", script], env=env, text=True, capture_output=True)
                    self.assertEqual(result.returncode == 0, should_pass, result.stderr)

    def test_workflow_ids_flow_through_recovery_outputs_and_exact_download_inputs(self) -> None:
        native = NATIVE.read_text()
        self.assertIn("artifact-ids: ${{ env.PRODUCER_ARTIFACT_ID }}", native)
        self.assertIn("artifact-ids: ${{ inputs.producer_artifact_id }}", native)
        self.assertIn('"$GITHUB_OUTPUT.json"', native)
        self.assertIn('--host-artifacts-map "$HOST_MAP"', native)
        self.assertIn("artifact-ids: ${{ steps.resolve.outputs.artifact_linux_x64_id }}", native)
        self.assertEqual(native.count("artifact-ids: ${{ steps.resolve.outputs.artifact_"), 5)
        for publisher in PUBLISHERS:
            source = publisher.read_text()
            self.assertRegex(source, r"resolve-tailwind-release-artifacts\.sh\s+resolve-producer")
            self.assertIn("producer_artifact_id", source)
            self.assertRegex(source, r"producer_artifact_id:\s*\$\{\{\s*needs\.pack-and-verify\.outputs\.producer_artifact_id")
            self.assertIn("steps.recovery.outputs.producer_artifact_id", source)
            self.assertRegex(source, r"artifact-ids:\s*\$\{\{\s*steps\.recovery\.outputs\.producer_artifact_id")
            self.assertRegex(source, r"UPLOAD(?:ED)?_ID:\s*\$\{\{\s*steps\.upload-producer\.outputs\.artifact-id")

    def test_failed_native_matrix_still_uploads_diagnostics_before_aggregate_validation(self) -> None:
        source = NATIVE.read_text()
        upload = source.index("- name: Upload host evidence (including failure diagnostics)")
        aggregate = source.index("aggregate-evidence:")
        self.assertIn("if: ${{ always() }}", source[upload:aggregate])
        self.assertLess(upload, aggregate)
        self.assertIn("if: ${{ always() && needs.host-matrix-preflight.result == 'success' }}", source[aggregate:])
        self.assertIn("actions: read", source[:aggregate])

    def test_pinned_source_is_checked_out_verified_and_stamped_for_both_builds(self) -> None:
        source = NATIVE.read_text()
        self.assertIn("ref: ${{ env.SOURCE_COMMIT }}", source)
        self.assertIn('actual="$(git rev-parse HEAD)"', source)
        self.assertIn('test "$actual" = "$SOURCE_COMMIT"', source)
        self.assertEqual(source.count('/p:SourceRevisionId="$SOURCE_REVISION_ID"'), 2)
        for publisher in PUBLISHERS:
            text = publisher.read_text()
            self.assertIn("source_commit: ${{ needs.validate-tag.outputs.tag-commit }}", text)
            self.assertIn("--source-commit \"$SOURCE_COMMIT\"", text)

    def test_original_start_receipt_overrides_newer_aggregate_outputs(self) -> None:
        receipt = self.root / "original-start.json"
        original_sha = "a" * 64
        receipt.write_text(json.dumps({
            "producerArtifactId": "123",
            "aggregateArtifactId": "456",
            "aggregateSha256": original_sha,
            "nativeInvocationId": "producer-42-1-tailwind-native",
        }))
        for publisher in PUBLISHERS:
            with self.subTest(publisher=publisher.name):
                receipt.write_text(json.dumps({
                    "producerArtifactId": "123",
                    "aggregateArtifactId": "456",
                    "aggregateSha256": original_sha,
                    "nativeInvocationId": "producer-42-1-tailwind-native",
                }))
                source = publisher.read_text()
                step_marker = "- name: Select original producer and aggregate binding\n"
                self.assertIn(step_marker, source)
                start = source.index(step_marker)
                end = source.find("\n      - name:", start + len(step_marker))
                step = source[start:end if end >= 0 else len(source)]
                run_marker = "        run: |\n"
                self.assertIn(run_marker, step)
                script = "\n".join(
                    line[10:] if line.startswith("          ") else ""
                    for line in step.split(run_marker, 1)[1].splitlines()
                    if not line or line.startswith("          ")
                )
                script = script.replace("${{ steps.start-recovery.outputs.publication_artifact_id }}", "7001")
                output = self.root / f"{publisher.stem}-candidate.out"
                env = os.environ.copy()
                env.update(
                    RECOVERED="true",
                    START_RECEIPT=str(receipt),
                    PRODUCER_ARTIFACT_ID="123",
                    CURRENT_AGGREGATE_ARTIFACT_ID="999",
                    CURRENT_AGGREGATE_SHA256="b" * 64,
                    CURRENT_NATIVE_INVOCATION_ID="newer-invocation",
                    GITHUB_OUTPUT=str(output),
                )
                result = subprocess.run(["bash", "-c", script], cwd=ROOT, env=env, text=True, capture_output=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                actual = output.read_text()
                self.assertIn("aggregate_artifact_id=456", actual)
                self.assertIn(f"expected_aggregate_sha256={original_sha}", actual)
                self.assertIn("native_invocation_id=producer-42-1-tailwind-native", actual)
                self.assertIn("start_artifact_id=7001", actual)

                for producer_id, aggregate_id in ((123, "456"), ("123", 456)):
                    receipt.write_text(json.dumps({
                        "producerArtifactId": producer_id,
                        "aggregateArtifactId": aggregate_id,
                        "aggregateSha256": original_sha,
                        "nativeInvocationId": "producer-42-1-tailwind-native",
                    }))
                    rejected = subprocess.run(["bash", "-c", script], cwd=ROOT, env=env, text=True, capture_output=True)
                    self.assertNotEqual(rejected.returncode, 0, "numeric receipt IDs must fail the frozen string schema")
                receipt.write_text(
                    '{"producerArtifactId":"123","producerArtifactId":"999",'
                    f'"aggregateArtifactId":"456","aggregateSha256":"{original_sha}",'
                    '"nativeInvocationId":"producer-42-1-tailwind-native"}'
                )
                rejected = subprocess.run(["bash", "-c", script], cwd=ROOT, env=env, text=True, capture_output=True)
                self.assertNotEqual(rejected.returncode, 0, "duplicate receipt keys must fail before jq selects authority")
                self.assertIn("duplicate JSON key", rejected.stderr)

    def test_recovered_start_revalidates_frozen_candidate_in_separate_report(self) -> None:
        for publisher in PUBLISHERS:
            with self.subTest(publisher=publisher.name):
                source = publisher.read_text()
                start = source.index("- name: Validate evidence and prepare publication inputs")
                end = source.index("\n      - name:", start + 1)
                step = source[start:end]
                self.assertNotIn("if: ${{ steps.start-recovery.outputs.publication_found", step)
                self.assertIn("tailwind-recovery-validation-report", step)
                self.assertIn("steps.candidate.outputs.aggregate_artifact_id", step)
                upload_start = source.index("- name: Upload publication-start receipt")
                upload_end = source.index("- name: Resolve authoritative publication receipt", upload_start)
                upload = source[upload_start:upload_end]
                self.assertIn("steps.start-recovery.outputs.publication_found != 'true'", upload)
                self.assertIn("tailwind-publish-preflight-report/publication-start-receipt.json", upload)
                self.assertIn('type == "string" and test("^[1-9][0-9]*$")', source)

    def test_host_download_actions_use_resolved_ids_and_avoid_name_selection(self) -> None:
        source = NATIVE.read_text()
        for rid in RIDS:
            output_id = rid.replace("-", "_")
            marker = f"- name: Download {rid} evidence by immutable ID"
            self.assertIn(marker, source)
            start = source.index(marker)
            end = source.find("\n      - name:", start + len(marker))
            step = source[start:end if end >= 0 else len(source)]
            self.assertIn(f"artifact-ids: ${{{{ steps.resolve.outputs.artifact_{output_id}_id }}}}", step)
            self.assertNotRegex(step, r"(?m)^\s+(?:name|pattern|merge-multiple):")

    def test_native_outputs_reemit_frozen_inputs_and_bind_aggregate_id_only_after_upload(self) -> None:
        source = NATIVE.read_text()
        aggregate = source[source.index("  aggregate-evidence:"):]
        self.assertIn("producer_artifact_id: ${{ inputs.producer_artifact_id }}", aggregate)
        self.assertIn("expected_subject_sha256: ${{ inputs.expected_subject_sha256 }}", aggregate)
        self.assertIn("aggregate_artifact_id: ${{ steps.upload-aggregate.outputs.artifact-id }}", aggregate)
        self.assertLess(aggregate.index("--host-artifacts-map \"$HOST_MAP\""), aggregate.index("- name: Upload aggregate and bound host evidence"))

    def test_rehearsal_runs_frozen_preflight_without_publisher_credentials(self) -> None:
        package = (ROOT / ".github/workflows/package-artifacts.yml").read_text()
        native = NATIVE.read_text()
        self.assertIn("tailwind-native-rehearsal:", package)
        self.assertIn("name: Nonpublishing aggregate and publisher replay rehearsal", package)
        self.assertIn("needs: [package-artifacts, tailwind-native-rehearsal]", package)
        self.assertIn("--mode publish-preflight", package)
        self.assertIn("--producer-artifact-id \"$PRODUCER_ARTIFACT_ID\"", package)
        self.assertIn("--aggregate-artifact-id \"$AGGREGATE_ARTIFACT_ID\"", package)
        self.assertIn("FullyQualifiedName~OriginalCandidatePublicationReplayTests", package)
        self.assertIn("- name: Upload rehearsal publication-start receipt", package)
        self.assertIn("artifact-ids: ${{ steps.upload-rehearsal-start.outputs.artifact-id }}", package)
        self.assertIn("--mode validate-publication-start", package)
        self.assertIn("for pass in initial replay; do", package)
        rehearsal = package[package.index("  tailwind-publish-rehearsal:"):]
        self.assertNotIn("NuGet/login", rehearsal)
        self.assertNotIn("NUGET_API_KEY", rehearsal)
        self.assertNotIn("id-token:", rehearsal)
        self.assertIn("timeout-minutes: 30", native)
        self.assertIn("timeout-minutes: 10", native)

    def test_uploaded_start_is_typed_validated_before_nuget_login(self) -> None:
        for workflow in PUBLISHERS:
            with self.subTest(workflow=workflow.name):
                source = workflow.read_text()
                download = source.index("- name: Download authoritative publication-start by immutable ID")
                validation = source.index("- name: Validate uploaded publication-start before requesting credentials")
                login = source.index("- name: Request NuGet trusted publishing token")
                publish = source.index("- name: Publish packages to NuGet")
                self.assertLess(download, validation)
                self.assertLess(validation, login)
                self.assertLess(login, publish)
                download_step = source[download:validation]
                self.assertIn("artifact-ids: ${{ steps.start-binding.outputs.artifact_id }}", download_step)
                self.assertNotRegex(download_step, r"(?m)^\s+(?:name|pattern|merge-multiple):")
                step = source[validation:login]
                self.assertIn("--mode validate-publication-start", step)
                self.assertIn("authoritative-publication-start/publication-start-receipt.json", step)
                self.assertIn("--publication-start-artifact-id \"$START_ARTIFACT_ID\"", step)
                self.assertIn("--publication-start-receipt \"$START_RECEIPT_PATH\"", step)
                self.assertIn("--expected-aggregate-sha256 \"$EXPECTED_AGGREGATE_SHA256\"", step)
                self.assertIn("--publication-directory \"$PUBLICATION_DIRECTORY\"", step)

    def test_recovery_history_requires_proven_skips_and_rejects_unknown_outcomes(self) -> None:
        script = RESOLVER.read_text()
        start = script.index("assert_safe_repack_history() {")
        end = script.index("\n}\n", start) + 3
        function = script[start:end]
        self.assertIn('"$upload_step" == skipped', function)
        self.assertIn('"$start_step" == skipped', function)
        self.assertIn('"unknown"', function)
        self.assertIn('"cancelled"', function) if "cancelled" in function else None
        self.assertIn('for ((prior=1; prior<attempt; prior++))', script)

    def test_native_artifact_failure_cases_retain_empty_partial_map_without_fallback(self) -> None:
        invocation = "p-5-1-tailwind-native"
        names = [f"tailwind-native-host-5-{invocation}-{rid}" for rid in RIDS]
        rows = [artifact(21 + i, name) for i, name in enumerate(names)]
        rows = [row for row in rows if not row["name"].endswith("osx-x64")]
        output = self.root / "partial.out"
        result = self.run_resolver(
            "resolve-native-evidence", "--repository", "org/repo", "--run-id", "5", "--invocation", invocation,
            "--output", str(output), pages=[{"artifacts": rows}],
        )
        self.assertNotEqual(result.returncode, 0)
        mapping = json.loads(Path(str(output) + ".json").read_text())
        self.assertEqual(
            {entry["rid"] for entry in mapping},
            {rid for rid in RIDS if rid != "osx-x64"},
        )
        self.assertEqual(
            {entry["artifactId"] for entry in mapping},
            {str(21 + index) for index, rid in enumerate(RIDS) if rid != "osx-x64"},
        )
        self.assertTrue(all(isinstance(entry["artifactId"], str)
                            and re.fullmatch(r"[1-9][0-9]*", entry["artifactId"])
                            for entry in mapping))
        self.assertNotIn("osx-x64", result.stdout + result.stderr.split("Current native invocation")[0])
        self.assertIn("osx-x64", result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
