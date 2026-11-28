from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path
import runpy
import unittest
from unittest.mock import patch
from zipfile import ZipFile

REPORTER = runpy.run_path(
    str(Path(__file__).resolve().parents[1] / "scripts" / "report_evidence_profile_study.py")
)


def make_qualification(resource_proof_passed=False):
    return {
        "schemaVersion": 1,
        "run": 46171759317,
        "attempt": 1,
        "studySha": "747b30b762adc6b7b58f4c82e081c6b98a22049f",
        "definitionSha256": "a" * 64,
        "baselinePolicySha256": REPORTER["BASELINE_POLICY"],
        "candidatePolicySha256": REPORTER["CANDIDATE_POLICY"],
        "candidatePlanDigest": "b" * 64,
        "warmSeedSha256": "c" * 64,
        "parityVerified": True,
        "selectionQuality": {
            "reviews": [
                {"case": "PR #960", "requiredRisks": ["docs-export", "release-contract"],
                 "allowedPackageChecks": [], "baselinePackageChecks": ["package-checks"],
                 "candidatePackageChecks": []},
                {"case": "package-index", "requiredRisks": ["manifest", "generated-docs", "archives"],
                 "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"],
                 "candidatePackageChecks": ["package-checks"]},
                {"case": "mixed", "requiredRisks": ["manifest", "generated-docs", "archives"],
                 "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"],
                 "candidatePackageChecks": ["package-checks"]},
                {"case": "fallback", "requiredRisks": ["manifest", "generated-docs", "archives"],
                 "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"],
                 "candidatePackageChecks": ["package-checks"]},
                {"case": "no-evidence", "requiredRisks": [], "allowedPackageChecks": [],
                 "baselinePackageChecks": [], "candidatePackageChecks": []},
            ],
            "baselineMissedRisks": 0,
            "candidateMissedRisks": 0,
            "requiredRisks": 11,
            "baselineSelectedChecks": 1,
            "candidateSelectedChecks": 0,
        },
        "resourceProofPassed": resource_proof_passed,
    }


def make_sample(state="warm", arm="baseline", pair=1):
    return {
        "schemaVersion": 1,
        "run": 46171759317,
        "attempt": 1,
        "studySha": "747b30b762adc6b7b58f4c82e081c6b98a22049f",
        "head": REPORTER["BASELINE_HEAD"],
        "base": REPORTER["BASE_COMMIT"],
        "arm": arm,
        "pair": pair,
        "state": state,
        "sourceDigest": REPORTER["SOURCE_DIGEST"],
        "policySha256": REPORTER["BASELINE_POLICY"] if arm == "baseline" else REPORTER["CANDIDATE_POLICY"],
        "planDigest": REPORTER["BASELINE_PLAN"] if arm == "baseline" else "b" * 64,
        "claim": "TargetedComplete" if arm == "baseline" else "NoEvidenceRequired",
        "resources": 0,
        "cleanupMs": 12.5,
        "resourceStartupMs": 0,
        "plannerMs": 1500,
        "manifestMs": 20,
        "cacheSeedSha256": "c" * 64 if state == "warm" else None,
        "runnerImage": REPORTER["RUNNER_IMAGE"],
        "runnerImageVersion": REPORTER["RUNNER_BUILD"],
        "sdk": REPORTER["SDK_VERSION"],
        "selection": arm == "baseline",
        "node": REPORTER["NODE_VERSION"] if arm == "baseline" else None,
        "pnpm": REPORTER["PNPM_VERSION"] if arm == "baseline" else None,
        "aspire": REPORTER["ASPIRE_VERSION"] if arm == "baseline" else None,
    }


def make_resource_proof():
    return {
        "schema": REPORTER["RESOURCE_PROOF_SCHEMA"],
        "test": "EvidenceReadinessMeasurementIntegrationTests.ResourceBackedHost_ReportsMonotonicStartupAndOwnedCleanupMeasurements",
        "monotonic_clock": "System.Diagnostics.Stopwatch",
        "stopwatch_frequency": 1000,
        "resource_kind": "loopback_tcp_http",
        "resource_startup": {
            "factory_invocation_timestamp_ticks": 100,
            "ready_timestamp_ticks": 600,
            "elapsed_ticks": 500,
            "elapsed_milliseconds": 500.0,
            "evidence_host_readiness_milliseconds": 490.0,
            "outcome": "Ready",
        },
        "owned_cleanup": {
            "started_timestamp_ticks": 700,
            "settled_timestamp_ticks": 950,
            "elapsed_ticks": 250,
            "elapsed_milliseconds": 250.0,
            "evidence_host_cleanup_milliseconds": 249.0,
            "completed": True,
            "resource_disposals": 1,
        },
        "test_evidence": {
            "producer_outcome": "Passed",
            "claim_kind": "TargetedComplete",
            "producer_runs": 1,
            "http_requests_served": 1,
        },
    }


def make_job(state="warm", arm="baseline", pair=1, runner_id=100):
    conclusions = REPORTER["expected_steps"](state, arm)
    steps = [
        {"name": "Set up job", "conclusion": "success"},
        *[
            {"name": name, "conclusion": conclusion}
            for name, conclusion in zip(REPORTER["JOB_STEP_NAMES"], conclusions)
        ],
    ]
    if arm == "baseline":
        posts = REPORTER["GITHUB_POST_STEPS"]
        steps.extend({"name": name, "conclusion": "success"} for name in posts)
    else:
        steps.extend(
            [
                {"name": "Post Setup .NET SDK", "conclusion": "success"},
                {"name": "Post Checkout code", "conclusion": "success"},
                {"name": "Post Checkout study definition", "conclusion": "success"},
                {"name": "Complete job", "conclusion": "success"},
            ]
        )
    return {
        "name": f"pairs ({state}, {pair}) / {'first' if pair % 2 else 'second'} / sample {state} {pair} {arm}",
        "id": 500 + pair * 2 + (arm == "candidate"),
        "run_id": 46171759317,
        "run_attempt": 1,
        "head_sha": "747b30b762adc6b7b58f4c82e081c6b98a22049f",
        "status": "completed",
        "conclusion": "success",
        "labels": ["ubuntu-latest"],
        "runner_id": runner_id,
        "started_at": "2026-11-28T18:00:00Z",
        "completed_at": "2026-11-28T18:00:30Z",
        "steps": steps,
    }


def make_admitted_arm(state, arm, pair, job_id):
    return {
        "receipt": make_sample(state, arm, pair),
        "jobSeconds": 60.0,
        "jobId": job_id,
        "runnerId": job_id + 10000,
        "startedAt": "2026-11-28T18:00:00Z",
        "completedAt": "2026-11-28T18:01:00Z",
        "artifact": {
            "id": job_id + 20000,
            "name": f"sample-{state}-{pair}-{arm}-1",
            "archiveSha256": "d" * 64,
        },
    }


class StudyReportTests(unittest.TestCase):
    def test_resource_proof_accepts_both_monotonic_tick_equations_and_returns_digest(self):
        encoded = json.dumps(make_resource_proof(), separators=(",", ":")).encode()
        proof = REPORTER["validate_resource_proof_bytes"](encoded)
        self.assertEqual(sha256(encoded).hexdigest(), proof["digest"])
        self.assertEqual(500, proof["metrics"]["startupElapsedTicks"])
        self.assertEqual(250, proof["metrics"]["cleanupElapsedTicks"])
        self.assertEqual(500.0, proof["metrics"]["startupElapsedMilliseconds"])

    def test_resource_proof_rejects_tick_mismatch_frequency_and_failed_outcomes(self):
        mutations = (
            ("resource_startup", "ready_timestamp_ticks", 601, "resource-proof-monotonic-ticks"),
            ("owned_cleanup", "elapsed_ticks", 249, "resource-proof-monotonic-ticks"),
            (None, "stopwatch_frequency", 0, "resource-proof-frequency"),
            ("resource_startup", "outcome", "Failed", "resource-proof-outcome"),
            ("owned_cleanup", "completed", False, "resource-proof-outcome"),
            ("test_evidence", "producer_runs", True, "resource-proof-outcome"),
            ("test_evidence", "producer_outcome", "Failed", "resource-proof-outcome"),
        )
        for section, key, value, reason in mutations:
            with self.subTest(key=key):
                proof = make_resource_proof()
                (proof if section is None else proof[section])[key] = value
                with self.assertRaisesRegex(REPORTER["InvalidEvidence"], reason):
                    REPORTER["validate_resource_proof_bytes"](json.dumps(proof).encode())

    def test_job_validation_accepts_github_bookends_and_exact_arm_steps(self):
        for state in ("warm", "cold"):
            for arm in ("baseline", "candidate"):
                with self.subTest(state=state, arm=arm):
                    facts = REPORTER["validate_job"](
                        make_job(state, arm), 46171759317, 1,
                        "747b30b762adc6b7b58f4c82e081c6b98a22049f", state, 1, arm,
                    )
                    self.assertEqual(30.0, facts["jobSeconds"])

    def test_candidate_generated_post_steps_may_be_skipped_but_unknown_steps_fail(self):
        job = make_job(arm="candidate")
        position = next(i for i, step in enumerate(job["steps"]) if step["name"] == "Post Setup .NET SDK")
        job["steps"][position:position] = [
            {"name": "Post Setup Node.js", "conclusion": "skipped"},
            {"name": "Post Setup pnpm", "conclusion": "skipped"},
        ]
        REPORTER["validate_job"](
            job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", "warm", 1, "candidate"
        )
        job["steps"].insert(-1, {"name": "Unrecognized provider step", "conclusion": "success"})
        with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "sample-job-provider-post-steps"):
            REPORTER["validate_job"](
                job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", "warm", 1, "candidate"
            )
        job = make_job(arm="candidate")
        position = next(i for i, step in enumerate(job["steps"]) if step["name"] == "Post Setup .NET SDK")
        job["steps"].insert(position, {"name": "Post Setup pnpm", "conclusion": None})
        with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "sample-job-provider-step-conclusions"):
            REPORTER["validate_job"](
                job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", "warm", 1, "candidate"
            )

    def test_repeated_candidate_evidence_step_failures_are_counted_separately(self):
        job = make_job(arm="candidate", pair=4)
        job["conclusion"] = "failure"
        step = next(step for step in job["steps"]
                    if step["name"] == "Run and verify scoped package evidence")
        step["conclusion"] = "failure"
        failure = REPORTER["candidate_evidence_step_failure"](
            job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f"
        )
        self.assertEqual({"state": "warm", "pair": 4, "jobId": job["id"]}, failure)
        job["name"] = "pairs (warm, 4) / first / sample warm 4 baseline"
        self.assertIsNone(REPORTER["candidate_evidence_step_failure"](
            job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f"
        ))

    def test_job_validation_rejects_missing_workflow_step_and_wrong_conclusion(self):
        job = make_job(state="cold", arm="candidate")
        job["steps"] = [step for step in job["steps"] if step["name"] != "Upload untimed qualification"]
        with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "sample-job-step-inventory"):
            REPORTER["validate_job"](
                job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", "cold", 1, "candidate"
            )
        job = make_job(state="cold", arm="candidate")
        step = next(step for step in job["steps"] if step["name"] == "Download frozen warm seed")
        step["conclusion"] = "success"
        with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "sample-job-step-conclusions"):
            REPORTER["validate_job"](
                job, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", "cold", 1, "candidate"
            )

    def test_sample_validation_checks_warm_and_cold_identity_and_candidate_toolchain(self):
        qualification = REPORTER["validate_qualification"](
            make_qualification(), 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f"
        )
        for state in ("warm", "cold"):
            for arm in ("baseline", "candidate"):
                with self.subTest(state=state, arm=arm):
                    sample = make_sample(state, arm)
                    REPORTER["validate_sample"](
                        sample, run_id=46171759317, attempt=1,
                        workflow_sha="747b30b762adc6b7b58f4c82e081c6b98a22049f",
                        qualification=qualification, state=state, pair=1, arm=arm,
                    )
        invalid = make_sample("cold", "candidate")
        invalid["node"] = "24.22.3"
        with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "sample-candidate-toolchain"):
            REPORTER["validate_sample"](
                invalid, run_id=46171759317, attempt=1,
                workflow_sha="747b30b762adc6b7b58f4c82e081c6b98a22049f",
                qualification=qualification, state="cold", pair=1, arm="candidate",
            )

    def test_artifact_verification_binds_origin_archive_digest_and_single_member(self):
        buffer = BytesIO()
        with ZipFile(buffer, "w") as archive:
            archive.writestr("sample.json", json.dumps(make_sample()))
        archive_bytes = buffer.getvalue()
        artifact = {
            "id": 85,
            "name": "sample-warm-1-baseline-1",
            "expired": False,
            "size_in_bytes": len(archive_bytes),
            "digest": "sha256:" + sha256(archive_bytes).hexdigest(),
            "created_at": "2026-11-28T18:00:15Z",
            "workflow_run": {
                "id": 46171759317,
                "head_sha": "747b30b762adc6b7b58f4c82e081c6b98a22049f",
                "repository_id": REPORTER["REPOSITORY_ID"],
                "head_repository_id": REPORTER["REPOSITORY_ID"],
                "run_attempt": 1,
            },
        }
        run = {"id": 46171759317, "head_sha": "747b30b762adc6b7b58f4c82e081c6b98a22049f", "run_attempt": 1}
        with patch.dict(REPORTER["verify_artifact"].__globals__, {"api": lambda *_args, **_kwargs: archive_bytes}):
            receipt, metadata = REPORTER["verify_artifact"](
                artifact, run, expected_name="sample-warm-1-baseline-1", member_name="sample.json",
                start=REPORTER["parse_time"]("2026-11-28T18:00:00Z", "test"),
                end=REPORTER["parse_time"]("2026-11-28T18:00:30Z", "test"),
            )
        self.assertEqual(make_sample(), receipt)
        self.assertEqual(sha256(archive_bytes).hexdigest(), metadata["archiveSha256"])
        artifact["digest"] = "sha256:" + "0" * 64
        with patch.dict(REPORTER["verify_artifact"].__globals__, {"api": lambda *_args, **_kwargs: archive_bytes}):
            with self.assertRaisesRegex(REPORTER["InvalidEvidence"], "artifact-archive-digest"):
                REPORTER["verify_artifact"](
                    artifact, run, expected_name="sample-warm-1-baseline-1", member_name="sample.json",
                    start=REPORTER["parse_time"]("2026-11-28T18:00:00Z", "test"),
                    end=REPORTER["parse_time"]("2026-11-28T18:00:30Z", "test"),
                )

    def test_pagination_uses_bounded_pages_and_requires_exact_count(self):
        calls = []

        def fake_api(endpoint):
            calls.append(endpoint)
            page = int(endpoint.rsplit("page=", 1)[1])
            ids = list(range((page - 1) * 25 + 1, min(page * 25, 31) + 1))
            return {"total_count": 31, "jobs": [{"id": value} for value in ids]}

        with patch.dict(REPORTER["paginated"].__globals__, {"api": fake_api}):
            rows = REPORTER["paginated"]("jobs", "jobs")
        self.assertEqual(31, len(rows))
        self.assertEqual(2, len(calls))
        self.assertIn("per_page=25", calls[0])

    def test_nearest_rank_and_minimum_sample_reporting(self):
        self.assertEqual(10, REPORTER["nearest_rank"](list(range(1, 21)), 0.50))
        self.assertEqual(19, REPORTER["nearest_rank"](list(range(1, 21)), 0.95))
        self.assertEqual({"admittedPairs": 19, "reported": False}, REPORTER["summarize_state"]([{}] * 19))

    def test_report_preserves_quality_counts_and_only_promotes_with_resource_proof(self):
        qualification = make_qualification(resource_proof_passed=False)
        qualified_data = REPORTER["validate_qualification"](
            qualification, 46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f"
        )
        pairs = []
        for number in range(1, 21):
            baseline = make_admitted_arm("warm", "baseline", number, 1000 + number * 2)
            candidate = make_admitted_arm("warm", "candidate", number, 1001 + number * 2)
            pairs.append({"pair": number, "baseline": baseline, "candidate": candidate})
        cold_pairs = []
        for number in range(1, 21):
            cold_pairs.append({
                "pair": number,
                "baseline": make_admitted_arm("cold", "baseline", number, 2000 + number * 2),
                "candidate": make_admitted_arm("cold", "candidate", number, 2001 + number * 2),
            })
        admitted = {"warm": pairs, "cold": cold_pairs}
        proof = REPORTER["validate_resource_proof_bytes"](json.dumps(make_resource_proof()).encode())
        report = REPORTER["build_report"](
            46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", qualification,
            qualified_data, admitted, 96, {"warm": 48, "cold": 48}, {}, {"warm": {}, "cold": {}},
            proof, None, {"id": 1, "archiveSha256": "d" * 64}, None,
        )
        self.assertEqual("eligible-for-maintainer-review", report["verdict"])
        self.assertEqual(11, report["qualification"]["selectionQualityCounts"]["requiredRisks"])
        self.assertEqual(2, report["qualification"]["selectionQualityCases"][0]["requiredRisksCount"])
        self.assertEqual(20, len(report["admittedPairAudit"]["warm"]))
        self.assertEqual(60.0, report["admittedPairAudit"]["warm"][0]["baseline"]["jobSeconds"])
        single_failure = REPORTER["build_report"](
            46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", qualification,
            qualified_data, admitted, 96, {"warm": 48, "cold": 48}, {}, {"warm": {}, "cold": {}},
            proof, None, {"id": 1, "archiveSha256": "d" * 64}, None,
            [{"state": "warm", "pair": 2, "jobId": 102}],
        )
        self.assertEqual("eligible-for-maintainer-review", single_failure["verdict"])
        self.assertFalse(single_failure["candidateEvidenceStepFailures"]["promotionBlocked"])
        blocked = REPORTER["build_report"](
            46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", qualification,
            qualified_data, admitted, 96, {"warm": 48, "cold": 48}, {}, {"warm": {}, "cold": {}},
            proof, None, {"id": 1, "archiveSha256": "d" * 64}, None,
            [{"state": "warm", "pair": 2, "jobId": 102},
             {"state": "cold", "pair": 3, "jobId": 103}],
        )
        self.assertEqual("keep-opt-in", blocked["verdict"])
        self.assertTrue(blocked["candidateEvidenceStepFailures"]["promotionBlocked"])
        self.assertEqual(2, blocked["reasonCounts"]["candidate-evidence-step-failures"])
        without_proof = REPORTER["build_report"](
            46171759317, 1, "747b30b762adc6b7b58f4c82e081c6b98a22049f", qualification,
            qualified_data, admitted, 96, {"warm": 48, "cold": 48}, {}, {"warm": {}, "cold": {}},
            None, None, None, None,
        )
        self.assertEqual("keep-opt-in", without_proof["verdict"])


if __name__ == "__main__":
    unittest.main()
