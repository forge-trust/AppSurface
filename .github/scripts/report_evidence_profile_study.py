#!/usr/bin/env python3
"""Report the bounded, paired observations from the Issue #780 hosted study."""

import argparse
from datetime import datetime
from hashlib import sha256
from io import BytesIO
import json
import math
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
from threading import Timer
from zipfile import BadZipFile, ZipFile

REPOSITORY = "forge-trust/AppSurface"
REPOSITORY_ID = 1035215851
WORKFLOW_PATH = ".github/workflows/evidence-profile-study.yml"
REPORT_SCHEMA = "forge-trust.appsurface.issue-780.evidence-profile-study-report.v1"
RESOURCE_PROOF_SCHEMA = "forge-trust.appsurface.issue-780.resource-proof.v1"
BASELINE_HEAD = "8c7f906bc31a56e2f6d0c9eb6bfe531eb91f1ce5"
BASE_COMMIT = "c3c9b6bd83d8852c37df596fae3339fbc3ae2274"
SOURCE_DIGEST = "1847f627d23ee6f1aeec80f4b3d2012aa874b592d97ff6ea7cad03c1c3afd639"
BASELINE_POLICY = "83b2881a3ec0602952608d1c7db7debe3314ee973137a69f3f6709c2fb9702fd"
CANDIDATE_POLICY = "f3576f1dab4add3862485b283de62cb77cce90d705d0bc16a7dc9ebcbf723080"
BASELINE_PLAN = "0a6b1da3c3ae4f1effa3198683a4233e32781ad99c6304e35e394509fc4dbff5"
RUNNER_IMAGE = "ubuntu-26.04"
RUNNER_BUILD = "20261122.9.1"
SDK_VERSION = "10.0.402"
NODE_VERSION = "24.22.3"
PNPM_VERSION = "11.1.3"
ASPIRE_VERSION = "13.6.5"
QUALITY_CASE_NAMES = ["PR #960", "package-index", "mixed", "fallback", "no-evidence"]
PAIRS_PER_STATE = 24
MINIMUM_PAIRS = 20
MAX_JSON = 256 * 1024
MAX_PROVIDER_JSON = 8 * 1024 * 1024
MAX_ARCHIVE = 4 * 1024 * 1024
MAX_RESOURCE_PROOF = 64 * 1024
MAX_PAGINATION_PAGES = 100
MAX_RETAINED_BYTES = 8 * 1024 * 1024

SAMPLE_FIELDS = {
    "schemaVersion", "run", "attempt", "studySha", "head", "base", "arm", "pair", "state",
    "sourceDigest", "policySha256", "planDigest", "claim", "resources", "cleanupMs",
    "resourceStartupMs", "plannerMs", "manifestMs", "cacheSeedSha256", "runnerImage",
    "runnerImageVersion", "sdk", "selection", "node", "pnpm", "aspire",
}
JOB_STEP_NAMES = [
    "Checkout study definition",
    "Checkout code",
    "Setup .NET SDK",
    "Verify immutable source and empty caches",
    "Download frozen warm seed",
    "Establish assigned restore state",
    "Build validation tool and collect and plan package checks",
    "Setup pnpm",
    "Setup Node.js",
    "Verify web assets",
    "Prepare pinned Aspire CLI bundle",
    "Run and verify scoped package evidence",
    "Record verified observation",
    "Upload bounded observation",
    "Upload untimed qualification",
]
GITHUB_PREFIX_STEPS = ["Set up job"]
GITHUB_POST_STEPS = [
    "Post Setup Node.js",
    "Post Setup pnpm",
    "Post Setup .NET SDK",
    "Post Checkout code",
    "Post Checkout study definition",
    "Complete job",
]
JOB_SUFFIX = re.compile(r"(?:^|[/ ])sample (warm|cold) ([0-9]+) (baseline|candidate)$")
HEX_64 = re.compile(r"[0-9a-f]{64}\Z")


class InvalidEvidence(ValueError):
    """Evidence is absent, ambiguous, or inconsistent with the frozen study."""

    def __init__(self, reason):
        super().__init__(reason)
        self.reason = reason


def require(condition, reason):
    if not condition:
        raise InvalidEvidence(reason)


def is_integer(value):
    return isinstance(value, int) and not isinstance(value, bool)


def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "duplicate-json-key")
        result[key] = value
    return result


def _reject_json_constant(_value):
    raise InvalidEvidence("non-finite-json-number")


def parse_json(data):
    try:
        return json.loads(data, object_pairs_hook=_unique_object, parse_constant=_reject_json_constant)
    except InvalidEvidence:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError, TypeError) as error:
        raise InvalidEvidence("invalid-json") from error


def api(endpoint, *, binary=False, limit=MAX_PROVIDER_JSON):
    args = ["gh", "api"]
    if binary:
        args.append("--allow-escape-sequences")
    try:
        with subprocess.Popen(
            [*args, f"repos/{REPOSITORY}/{endpoint}"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
        ) as process:
            deadline = Timer(60, process.kill)
            deadline.start()
            try:
                data = process.stdout.read(limit + 1)
                if len(data) > limit:
                    process.kill()
                    raise InvalidEvidence("provider-response-size")
                require(process.wait() == 0, "provider-response-unavailable")
            finally:
                deadline.cancel()
    except OSError as error:
        raise InvalidEvidence("provider-response-unavailable") from error
    return data if binary else parse_json(data)


def paginated(endpoint, collection):
    rows = []
    expected_count = None
    seen_ids = set()
    for page_number in range(1, MAX_PAGINATION_PAGES + 1):
        page = api(f"{endpoint}?per_page=25&page={page_number}")
        require(isinstance(page, dict) and isinstance(page.get(collection), list), "provider-pagination-shape")
        count = page.get("total_count")
        require(is_integer(count) and count >= 0, "provider-pagination-count")
        if expected_count is None:
            expected_count = count
        require(count == expected_count, "provider-pagination-count-changed")
        batch = page[collection]
        require(len(batch) <= 25, "provider-pagination-page-size")
        for row in batch:
            identifier = row.get("id") if isinstance(row, dict) else None
            require(is_integer(identifier) and identifier not in seen_ids, "provider-pagination-duplicate")
            seen_ids.add(identifier)
        rows.extend(batch)
        if len(rows) >= expected_count:
            break
        require(batch, "provider-pagination-truncated")
    require(expected_count is not None and len(rows) == expected_count, "provider-pagination-truncated")
    return rows


def parse_time(value, reason):
    require(isinstance(value, str), reason)
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise InvalidEvidence(reason) from error
    require(result.tzinfo is not None, reason)
    return result


def validate_run(run, run_id, attempt, workflow_sha):
    require(isinstance(run, dict), "provider-run-shape")
    repo = run.get("repository") or {}
    head_repo = run.get("head_repository") or {}
    require(
        is_integer(run.get("id")) and run.get("id") == run_id
        and is_integer(run.get("run_attempt")) and run.get("run_attempt") == attempt
        and run.get("head_sha") == workflow_sha and run.get("path") == WORKFLOW_PATH
        and run.get("event") in {"push", "workflow_dispatch"}
        and repo.get("id") == REPOSITORY_ID and repo.get("full_name") == REPOSITORY
        and head_repo.get("id") == REPOSITORY_ID and head_repo.get("full_name") == REPOSITORY,
        "provider-run-identity",
    )
    require(run.get("status") == "completed", "workflow-not-terminal")
    started = parse_time(run.get("run_started_at"), "provider-run-time")
    completed_text = run.get("updated_at") or run.get("completed_at")
    completed = parse_time(completed_text, "provider-run-time")
    require(completed >= started, "provider-run-time")
    return started, completed


def parse_job_suffix(name):
    if not isinstance(name, str):
        return None
    match = JOB_SUFFIX.search(name)
    if match is None:
        return None
    return match.group(1), int(match.group(2)), match.group(3)


def candidate_evidence_step_failure(job, run_id, attempt, workflow_sha):
    identity = parse_job_suffix(job.get("name"))
    if identity is None or identity[2] != "candidate" or not 1 <= identity[1] <= PAIRS_PER_STATE:
        return None
    if (job.get("run_id") != run_id or not is_integer(job.get("run_attempt"))
            or job.get("run_attempt") != attempt or job.get("head_sha") != workflow_sha
            or job.get("status") != "completed" or job.get("conclusion") != "failure"
            or not is_integer(job.get("id"))):
        return None
    steps = job.get("steps")
    if not isinstance(steps, list):
        return None
    matches = [
        step for step in steps
        if isinstance(step, dict) and step.get("name") == "Run and verify scoped package evidence"
    ]
    if len(matches) != 1 or matches[0].get("conclusion") != "failure":
        return None
    return {"state": identity[0], "pair": identity[1], "jobId": job["id"]}


def expected_steps(state, arm):
    skipped = "skipped"
    success = "success"
    expected = [success] * len(JOB_STEP_NAMES)
    expected[4] = success if state == "warm" else skipped
    for index in (7, 8, 9, 10):
        expected[index] = success if arm == "baseline" else skipped
    expected[14] = skipped
    return expected


def validate_job(job, run_id, attempt, workflow_sha, state, pair, arm):
    require(
        job.get("run_id") == run_id and is_integer(job.get("run_attempt")) and job.get("run_attempt") == attempt
        and job.get("head_sha") == workflow_sha and job.get("conclusion") == "success"
        and job.get("status") == "completed",
        "sample-job-identity-or-conclusion",
    )
    require(job.get("labels") == ["ubuntu-latest"], "sample-runner-labels")
    runner_id = job.get("runner_id")
    require(is_integer(runner_id) and runner_id > 0, "sample-runner-id")
    job_id = job.get("id")
    require(is_integer(job_id) and job_id > 0, "sample-job-id")
    steps = job.get("steps")
    require(isinstance(steps, list) and all(isinstance(step, dict) for step in steps),
            "sample-job-step-inventory")
    workflow_indexes = [index for index, step in enumerate(steps) if step.get("name") in JOB_STEP_NAMES]
    require(len(workflow_indexes) == len(JOB_STEP_NAMES), "sample-job-step-inventory")
    first_workflow = workflow_indexes[0]
    last_workflow = workflow_indexes[-1]
    prefix = steps[:first_workflow]
    workflow_steps = steps[first_workflow:last_workflow + 1]
    suffix = steps[last_workflow + 1:]
    require([step.get("name") for step in prefix] == GITHUB_PREFIX_STEPS,
            "sample-job-provider-prefix")
    require([step.get("name") for step in workflow_steps] == JOB_STEP_NAMES,
            "sample-job-step-inventory")
    suffix_names = [step.get("name") for step in suffix]
    require(
        all(name in GITHUB_POST_STEPS for name in suffix_names)
        and suffix_names == [name for name in GITHUB_POST_STEPS if name in suffix_names],
        "sample-job-provider-post-steps",
    )
    mandatory_posts = GITHUB_POST_STEPS[2:]
    require(all(name in suffix_names for name in mandatory_posts), "sample-job-provider-post-steps")
    if arm == "baseline":
        require(suffix_names == GITHUB_POST_STEPS, "sample-job-provider-post-steps")
    post_conclusions = {step["name"]: step.get("conclusion") for step in suffix}
    optional_post_conclusions = {"success"} if arm == "baseline" else {"skipped", "absent"}
    require(
        post_conclusions.get("Post Setup Node.js", "absent") in optional_post_conclusions
        and post_conclusions.get("Post Setup pnpm", "absent") in optional_post_conclusions
        and all(post_conclusions.get(name) == "success" for name in mandatory_posts)
        and prefix[0].get("conclusion") == "success",
        "sample-job-provider-step-conclusions",
    )
    actual_conclusions = [step.get("conclusion") for step in workflow_steps]
    require(actual_conclusions == expected_steps(state, arm), "sample-job-step-conclusions")
    started = parse_time(job.get("started_at"), "sample-job-time")
    completed = parse_time(job.get("completed_at"), "sample-job-time")
    require(completed > started, "sample-job-time")
    return {
        "jobId": job_id,
        "runnerId": runner_id,
        "started": started,
        "completed": completed,
        "startedAt": job["started_at"],
        "completedAt": job["completed_at"],
        "jobSeconds": (completed - started).total_seconds(),
    }


def verify_artifact(artifact, run, *, expected_name, member_name, start, end):
    require(isinstance(artifact, dict), "artifact-metadata")
    origin = artifact.get("workflow_run") or {}
    require(
        artifact.get("name") == expected_name and artifact.get("expired") is False
        and origin.get("id") == run["id"] and origin.get("head_sha") == run["head_sha"]
        and origin.get("repository_id") == REPOSITORY_ID
        and origin.get("head_repository_id") == REPOSITORY_ID
        and origin.get("run_attempt", run["run_attempt"]) == run["run_attempt"],
        "artifact-origin-or-identity",
    )
    created = parse_time(artifact.get("created_at"), "artifact-created-at")
    require(start <= created <= end, "artifact-created-at")
    size = artifact.get("size_in_bytes")
    require(is_integer(size) and 0 < size <= MAX_ARCHIVE, "artifact-size")
    artifact_id = artifact.get("id")
    digest = artifact.get("digest")
    require(is_integer(artifact_id) and artifact_id > 0 and isinstance(digest, str)
            and re.fullmatch(r"sha256:[0-9a-f]{64}", digest), "artifact-digest-metadata")
    archive_bytes = api(f"actions/artifacts/{artifact_id}/zip", binary=True, limit=MAX_ARCHIVE)
    require(sha256(archive_bytes).hexdigest() == digest.removeprefix("sha256:"), "artifact-archive-digest")
    try:
        with ZipFile(BytesIO(archive_bytes)) as archive:
            members = archive.infolist()
            require(len(members) == 1 and archive.namelist() == [member_name], "artifact-archive-members")
            member = members[0]
            path = PurePosixPath(member.filename)
            require(not path.is_absolute() and ".." not in path.parts and "\\" not in member.filename,
                    "artifact-member-path")
            require(0 < member.file_size <= MAX_JSON, "artifact-json-size")
            data = archive.read(member)
    except (BadZipFile, OSError, KeyError) as error:
        raise InvalidEvidence("artifact-invalid-zip") from error
    return parse_json(data), {
        "id": artifact_id,
        "name": expected_name,
        "archiveSha256": digest.removeprefix("sha256:"),
        "sizeInBytes": size,
        "createdAt": artifact["created_at"],
    }


def only(rows, predicate, reason):
    matches = [row for row in rows if predicate(row)]
    require(len(matches) == 1, reason)
    return matches[0]


def validate_qualification(value, run_id, attempt, workflow_sha):
    expected = {
        "schemaVersion", "run", "attempt", "studySha", "definitionSha256", "baselinePolicySha256",
        "candidatePolicySha256", "candidatePlanDigest", "warmSeedSha256", "parityVerified",
        "selectionQuality", "resourceProofPassed",
    }
    require(isinstance(value, dict) and set(value) == expected, "qualification-shape")
    require(
        is_integer(value["schemaVersion"]) and value["schemaVersion"] == 1
        and is_integer(value["run"]) and value["run"] == run_id
        and is_integer(value["attempt"]) and value["attempt"] == attempt
        and value["studySha"] == workflow_sha and value["baselinePolicySha256"] == BASELINE_POLICY
        and value["candidatePolicySha256"] == CANDIDATE_POLICY and value["parityVerified"] is True,
        "qualification-identity-or-parity",
    )
    require(isinstance(value["definitionSha256"], str) and HEX_64.fullmatch(value["definitionSha256"]),
            "qualification-definition-digest")
    require(isinstance(value["candidatePlanDigest"], str) and HEX_64.fullmatch(value["candidatePlanDigest"]),
            "qualification-candidate-plan")
    require(isinstance(value["warmSeedSha256"], str) and HEX_64.fullmatch(value["warmSeedSha256"]),
            "qualification-warm-seed")
    require(type(value["resourceProofPassed"]) is bool, "qualification-resource-proof-flag")
    quality = value["selectionQuality"]
    require(isinstance(quality, dict), "qualification-selection-quality")
    missed = quality.get("candidateMissedRisks")
    require(is_integer(missed) and missed >= 0, "qualification-candidate-missed-risks")
    reviews = quality.get("reviews")
    require(
        isinstance(reviews, list)
        and [review.get("case") for review in reviews if isinstance(review, dict)] == QUALITY_CASE_NAMES,
        "qualification-selection-quality-reviews",
    )
    for review in reviews:
        require(
            all(
                isinstance(review.get(key), list) and all(isinstance(item, str) for item in review[key])
                for key in (
                    "requiredRisks", "allowedPackageChecks", "baselinePackageChecks", "candidatePackageChecks"
                )
            ),
            "qualification-selection-quality-review-shape",
        )
    return {
        "candidatePlanDigest": value["candidatePlanDigest"],
        "warmSeedSha256": value["warmSeedSha256"],
        "resourceProofPassed": value["resourceProofPassed"],
        "candidateMissedRisks": missed,
    }


def validate_sample(value, *, run_id, attempt, workflow_sha, qualification, state, pair, arm):
    require(isinstance(value, dict) and set(value) == SAMPLE_FIELDS, "sample-shape")
    require(
        is_integer(value["schemaVersion"]) and value["schemaVersion"] == 1
        and is_integer(value["run"]) and value["run"] == run_id
        and is_integer(value["attempt"]) and value["attempt"] == attempt
        and is_integer(value["pair"]) and value["pair"] == pair
        and value["studySha"] == workflow_sha and value["head"] == BASELINE_HEAD
        and value["base"] == BASE_COMMIT and value["sourceDigest"] == SOURCE_DIGEST
        and value["arm"] == arm and value["state"] == state,
        "sample-identity",
    )
    expected_policy = BASELINE_POLICY if arm == "baseline" else CANDIDATE_POLICY
    expected_plan = BASELINE_PLAN if arm == "baseline" else qualification["candidatePlanDigest"]
    expected_claim = "TargetedComplete" if arm == "baseline" else "NoEvidenceRequired"
    require(
        value["policySha256"] == expected_policy and value["planDigest"] == expected_plan
        and value["claim"] == expected_claim and value["selection"] is (arm == "baseline"),
        "sample-policy-plan-claim",
    )
    require(value["resources"] == 0 and is_integer(value["resources"]), "sample-resources")
    for name in ("cleanupMs", "resourceStartupMs", "plannerMs", "manifestMs"):
        require(finite_number(value[name]) and value[name] >= 0, "sample-metric")
    require(
        value["cacheSeedSha256"] == (qualification["warmSeedSha256"] if state == "warm" else None),
        "sample-cache-seed",
    )
    require(
        value["runnerImage"] == RUNNER_IMAGE and value["runnerImageVersion"] == RUNNER_BUILD
        and value["sdk"] == SDK_VERSION,
        "sample-runner-image-or-sdk",
    )
    if arm == "baseline":
        require(
            value["node"] == NODE_VERSION and value["pnpm"] == PNPM_VERSION
            and value["aspire"] == ASPIRE_VERSION,
            "sample-baseline-toolchain",
        )
    else:
        require(value["node"] is None and value["pnpm"] is None and value["aspire"] is None,
                "sample-candidate-toolchain")
    return value


def validate_resource_proof_bytes(data):
    require(0 < len(data) <= MAX_RESOURCE_PROOF, "resource-proof-size")
    digest = sha256(data).hexdigest()
    value = parse_json(data)
    require(
        isinstance(value, dict) and set(value) == {
            "schema", "test", "resource_kind", "monotonic_clock", "stopwatch_frequency",
            "resource_startup", "owned_cleanup", "test_evidence",
        }
        and value["schema"] == RESOURCE_PROOF_SCHEMA
        and value["test"] == "EvidenceReadinessMeasurementIntegrationTests.ResourceBackedHost_ReportsMonotonicStartupAndOwnedCleanupMeasurements"
        and value["resource_kind"] == "loopback_tcp_http"
        and value["monotonic_clock"] == "System.Diagnostics.Stopwatch",
        "resource-proof-identity",
    )
    frequency = value["stopwatch_frequency"]
    require(is_integer(frequency) and frequency > 0, "resource-proof-frequency")
    resource = value["resource_startup"]
    cleanup = value["owned_cleanup"]
    evidence = value["test_evidence"]
    require(
        isinstance(resource, dict) and set(resource) == {
            "factory_invocation_timestamp_ticks", "ready_timestamp_ticks", "elapsed_ticks",
            "elapsed_milliseconds", "evidence_host_readiness_milliseconds", "outcome",
        }
        and isinstance(cleanup, dict) and set(cleanup) == {
            "started_timestamp_ticks", "settled_timestamp_ticks", "elapsed_ticks", "elapsed_milliseconds",
            "evidence_host_cleanup_milliseconds", "completed", "resource_disposals",
        }
        and isinstance(evidence, dict) and set(evidence) == {
            "producer_outcome", "claim_kind", "producer_runs", "http_requests_served",
        },
        "resource-proof-shape",
    )
    start = resource["factory_invocation_timestamp_ticks"]
    ready = resource["ready_timestamp_ticks"]
    startup = resource["elapsed_ticks"]
    cleanup_start = cleanup["started_timestamp_ticks"]
    cleanup_end = cleanup["settled_timestamp_ticks"]
    cleanup_ticks = cleanup["elapsed_ticks"]
    require(
        all(is_integer(item) for item in (start, ready, startup, cleanup_start, cleanup_end, cleanup_ticks))
        and start >= 0 and ready - start == startup > 0
        and cleanup_start >= 0 and cleanup_end - cleanup_start == cleanup_ticks > 0,
        "resource-proof-monotonic-ticks",
    )
    require(
        resource["outcome"] == "Ready" and cleanup["completed"] is True
        and evidence["producer_outcome"] == "Passed" and evidence["claim_kind"] == "TargetedComplete"
        and is_integer(evidence["producer_runs"]) and evidence["producer_runs"] == 1
        and is_integer(evidence["http_requests_served"]) and evidence["http_requests_served"] == 1
        and is_integer(cleanup["resource_disposals"]) and cleanup["resource_disposals"] == 1,
        "resource-proof-outcome",
    )
    metrics = {
        "resourceKind": value["resource_kind"],
        "stopwatchFrequency": frequency,
        "startupElapsedTicks": startup,
        "startupElapsedMilliseconds": resource["elapsed_milliseconds"],
        "evidenceHostReadinessMilliseconds": resource["evidence_host_readiness_milliseconds"],
        "cleanupElapsedTicks": cleanup_ticks,
        "cleanupElapsedMilliseconds": cleanup["elapsed_milliseconds"],
        "evidenceHostCleanupMilliseconds": cleanup["evidence_host_cleanup_milliseconds"],
        "producerRuns": evidence["producer_runs"],
        "httpRequestsServed": evidence["http_requests_served"],
        "resourceDisposals": cleanup["resource_disposals"],
    }
    for key in (
        "startupElapsedMilliseconds", "evidenceHostReadinessMilliseconds", "cleanupElapsedMilliseconds",
        "evidenceHostCleanupMilliseconds",
    ):
        require(finite_number(metrics[key]) and metrics[key] >= 0, "resource-proof-metric")
    require(
        math.isclose(metrics["startupElapsedMilliseconds"], startup * 1000.0 / frequency,
                     rel_tol=1e-9, abs_tol=0.01)
        and math.isclose(metrics["cleanupElapsedMilliseconds"], cleanup_ticks * 1000.0 / frequency,
                         rel_tol=1e-9, abs_tol=0.01),
        "resource-proof-metric-ticks-mismatch",
    )
    return {"digest": digest, "metrics": metrics}


def nearest_rank(values, percentile):
    require(values and 0 < percentile <= 1, "percentile-input")
    ordered = sorted(values)
    index = math.ceil(percentile * len(ordered)) - 1
    return ordered[index]


def summarize_state(pairs):
    result = {"admittedPairs": len(pairs), "reported": len(pairs) >= MINIMUM_PAIRS}
    if not result["reported"]:
        return result
    arms = {}
    totals = {}
    for arm in ("baseline", "candidate"):
        samples = [pair[arm] for pair in pairs]
        metric_names = ("cleanupMs", "resourceStartupMs", "plannerMs", "manifestMs")
        metrics = {}
        for name in metric_names:
            values = [sample["receipt"][name] for sample in samples]
            metrics[name] = {"p50": nearest_rank(values, 0.50), "p95": nearest_rank(values, 0.95)}
        combined = [sample["receipt"]["plannerMs"] + sample["receipt"]["manifestMs"] for sample in samples]
        job_times = [sample["jobSeconds"] for sample in samples]
        metrics["plannerManifestMs"] = {
            "p50": nearest_rank(combined, 0.50), "p95": nearest_rank(combined, 0.95)
        }
        metrics["jobSeconds"] = {"p50": nearest_rank(job_times, 0.50), "p95": nearest_rank(job_times, 0.95)}
        total_seconds = sum(job_times)
        totals[arm] = total_seconds
        arms[arm] = {
            "pairs": len(samples), "metrics": metrics,
            "occupiedRunnerMinutes": round(total_seconds / 60.0, 6),
        }
    baseline_seconds = totals["baseline"]
    require(baseline_seconds > 0, "baseline-runner-occupancy-zero")
    delta = (totals["candidate"] - baseline_seconds) / baseline_seconds
    result["arms"] = arms
    result["matchedRunnerDeltaRatio"] = round(delta, 8)
    result["candidatePlannerManifestP95Under30Seconds"] = (
        arms["candidate"]["metrics"]["plannerManifestMs"]["p95"] < 30000
    )
    result["candidateRunnerDeltaWithin10Percent"] = delta <= 0.10
    return result


def selection_quality_counts(qualification):
    if qualification is None:
        return {}, []
    quality = qualification["selectionQuality"]
    counts = {key: value for key, value in quality.items() if is_integer(value)}
    cases = []
    reviews = quality.get("reviews")
    if isinstance(reviews, list):
        for review in reviews:
            if not isinstance(review, dict) or not isinstance(review.get("case"), str):
                continue
            case_counts = {"case": review["case"]}
            for key in ("requiredRisks", "allowedPackageChecks", "baselinePackageChecks", "candidatePackageChecks"):
                value = review.get(key)
                if isinstance(value, list):
                    case_counts[f"{key}Count"] = len(value)
            cases.append(case_counts)
    return counts, cases


def retain_samples(retained_dir, retained_pairs):
    if retained_dir is None:
        return
    retained_dir.mkdir(parents=True, exist_ok=True)
    total_size = 0
    outputs = []
    for state, pair, arm, receipt in retained_pairs:
        encoded = (json.dumps(receipt, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
        total_size += len(encoded)
        require(total_size <= MAX_RETAINED_BYTES, "retained-observations-size")
        path = retained_dir / f"{state}-{pair:02d}-{arm}.json"
        outputs.append((path, encoded))
    require(all(not path.exists() for path, _ in outputs), "retained-observations-exist")
    created = []
    try:
        for path, encoded in outputs:
            with path.open("xb") as stream:
                created.append(path)
                stream.write(encoded)
    except OSError as error:
        for path in created:
            try:
                path.unlink()
            except OSError:
                pass
        if isinstance(error, FileExistsError):
            raise InvalidEvidence("retained-observations-exist") from error
        raise


def build_report(run_id, attempt, workflow_sha, qualification, qualified_data,
                 admitted, total_attempts, state_attempts, reasons, exclusions_by_state,
                 proof, proof_error, qualification_artifact, retained_dir, candidate_evidence_failures=None):
    candidate_evidence_failures = candidate_evidence_failures or []
    for state, pairs in admitted.items():
        if len(pairs) < MINIMUM_PAIRS:
            reasons[f"insufficient-pairs-{state}"] = reasons.get(f"insufficient-pairs-{state}", 0) + 1
    missed = qualified_data["candidateMissedRisks"] if qualified_data else None
    if missed is not None and missed > 0:
        reasons["candidate-missed-risks"] = 1
    if len(candidate_evidence_failures) >= 2:
        reasons["candidate-evidence-step-failures"] = len(candidate_evidence_failures)
    qualification_flag = qualified_data["resourceProofPassed"] if qualified_data else False
    effective_proof_passed = qualification_flag or proof is not None
    if qualified_data and not effective_proof_passed:
        reason = "external-resource-proof-invalid" if proof_error else "external-resource-proof-required"
        reasons[reason] = 1
    states = {state: summarize_state(admitted[state]) for state in ("warm", "cold")}
    for state, summary in states.items():
        if summary.get("reported"):
            if not summary["candidatePlannerManifestP95Under30Seconds"]:
                reasons[f"candidate-planner-manifest-p95-{state}"] = 1
            if not summary["candidateRunnerDeltaWithin10Percent"]:
                reasons[f"candidate-runner-delta-{state}"] = 1
    enough = qualified_data is not None and all(states[state]["reported"] for state in states)
    if not enough:
        verdict = "insufficient-evidence"
    elif missed or len(candidate_evidence_failures) >= 2 or not effective_proof_passed or any(
        not states[state]["candidatePlannerManifestP95Under30Seconds"]
        or not states[state]["candidateRunnerDeltaWithin10Percent"] for state in states
    ):
        verdict = "keep-opt-in"
    else:
        verdict = "eligible-for-maintainer-review"
    quality_counts, quality_cases = selection_quality_counts(qualification if qualified_data else None)
    proof_report = {
        "qualificationPassed": qualification_flag,
        "externalProvided": proof is not None or proof_error is not None,
        "externalPassed": proof is not None,
        "effectivePassed": effective_proof_passed,
        "digest": proof["digest"] if proof else None,
        "metrics": proof["metrics"] if proof else None,
    }
    report = {
        "schema": REPORT_SCHEMA,
        "repository": REPOSITORY,
        "workflow": WORKFLOW_PATH,
        "run": run_id,
        "attempt": attempt,
        "workflowSha": workflow_sha,
        "baselineHead": BASELINE_HEAD,
        "base": BASE_COMMIT,
        "sourceDigest": SOURCE_DIGEST,
        "verdict": verdict,
        "totalAttempts": total_attempts,
        "expectedAttempts": 2 * PAIRS_PER_STATE * 2,
        "attemptsByState": state_attempts,
        "admittedPairsByState": {state: len(admitted[state]) for state in ("warm", "cold")},
        "qualification": {
            "valid": qualified_data is not None,
            "parityVerified": qualified_data is not None,
            "candidateMissedRisks": missed,
            "candidatePolicySha256": CANDIDATE_POLICY if qualified_data else None,
            "candidatePlanDigest": qualified_data["candidatePlanDigest"] if qualified_data else None,
            "selectionQualityCounts": quality_counts,
            "selectionQualityCases": quality_cases,
            "artifactId": qualification_artifact["id"] if qualification_artifact else None,
            "artifactArchiveSha256": qualification_artifact["archiveSha256"] if qualification_artifact else None,
        },
        "resourceProof": proof_report,
        "states": states,
        "exclusionCountsByState": {
            state: dict(sorted(exclusions_by_state[state].items())) for state in ("warm", "cold")
        },
        "candidateEvidenceStepFailures": {
            "count": len(candidate_evidence_failures),
            "promotionBlocked": len(candidate_evidence_failures) >= 2,
            "byState": {
                state: sum(failure["state"] == state for failure in candidate_evidence_failures)
                for state in ("warm", "cold")
            },
            "jobs": candidate_evidence_failures,
        },
        "admittedPairAudit": {
            state: [
                {
                    "pair": pair["pair"],
                    **{
                        arm: {
                            "jobId": pair[arm]["jobId"],
                            "runnerId": pair[arm]["runnerId"],
                            "startedAt": pair[arm]["startedAt"],
                            "completedAt": pair[arm]["completedAt"],
                            "jobSeconds": round(pair[arm]["jobSeconds"], 6),
                            "artifactId": pair[arm]["artifact"]["id"],
                            "artifactName": pair[arm]["artifact"]["name"],
                            "artifactArchiveSha256": pair[arm]["artifact"]["archiveSha256"],
                        }
                        for arm in ("baseline", "candidate")
                    },
                }
                for pair in admitted[state]
            ]
            for state in ("warm", "cold")
        },
        "reasonCounts": dict(sorted(reasons.items())),
    }
    retained = []
    for state, pairs in admitted.items():
        for pair in pairs:
            for arm in ("baseline", "candidate"):
                retained.append((state, pair["pair"], arm, pair[arm]["receipt"]))
    retain_samples(retained_dir, retained)
    return report


def collect_report(run_id, attempt, workflow_sha, resource_proof_path, retained_dir):
    run = api(f"actions/runs/{run_id}/attempts/{attempt}")
    run_started, run_completed = validate_run(run, run_id, attempt, workflow_sha)
    jobs = paginated(f"actions/runs/{run_id}/attempts/{attempt}/jobs", "jobs")
    artifacts = paginated(f"actions/runs/{run_id}/artifacts", "artifacts")
    job_map = {"warm": {}, "cold": {}}
    reasons = {}
    exclusions_by_state = {"warm": {}, "cold": {}}
    total_attempts = 0
    state_attempts = {"warm": 0, "cold": 0}
    candidate_evidence_failures = []
    for job in jobs:
        parsed = parse_job_suffix(job.get("name"))
        if parsed is None:
            continue
        state, pair, arm = parsed
        if pair == 0:
            continue
        failure = candidate_evidence_step_failure(job, run_id, attempt, workflow_sha)
        if failure is not None:
            candidate_evidence_failures.append(failure)
        total_attempts += 1
        if state in state_attempts:
            state_attempts[state] += 1
        if pair > PAIRS_PER_STATE:
            reasons["unexpected-sample-job"] = reasons.get("unexpected-sample-job", 0) + 1
            exclusions_by_state[state]["unexpected-sample-job"] = (
                exclusions_by_state[state].get("unexpected-sample-job", 0) + 1
            )
            continue
        key = (pair, arm)
        if key in job_map[state]:
            job_map[state][key] = None
        else:
            job_map[state][key] = job

    qualification = None
    qualified_data = None
    qualification_artifact_info = None
    qualification_job = None
    for job in jobs:
        if isinstance(job.get("name"), str) and job["name"].endswith("qualification"):
            qualification_job = job
            break
    try:
        require(qualification_job is not None, "qualification-job-missing")
        q_started = parse_time(qualification_job.get("started_at"), "qualification-job-time")
        q_completed = parse_time(qualification_job.get("completed_at"), "qualification-job-time")
        artifact = only(artifacts, lambda item: item.get("name") == f"study-qualification-{attempt}",
                        "qualification-artifact-missing-or-ambiguous")
        qualification, qualification_artifact_info = verify_artifact(
            artifact, run, expected_name=f"study-qualification-{attempt}", member_name="qualification.json",
            start=max(run_started, q_started), end=min(run_completed, q_completed),
        )
        qualified_data = validate_qualification(qualification, run_id, attempt, workflow_sha)
    except InvalidEvidence as error:
        reasons["qualification-invalid"] = 1

    proof = None
    proof_error = None
    if resource_proof_path is not None:
        try:
            with resource_proof_path.open("rb") as stream:
                data = stream.read(MAX_RESOURCE_PROOF + 1)
            proof = validate_resource_proof_bytes(data)
        except (OSError, InvalidEvidence) as error:
            proof_error = error.reason if isinstance(error, InvalidEvidence) else "resource-proof-unreadable"
            reasons["external-resource-proof-invalid"] = 1

    admitted = {"warm": [], "cold": []}
    for state in ("warm", "cold"):
        for pair_number in range(1, PAIRS_PER_STATE + 1):
            pair_jobs = [job_map[state].get((pair_number, arm)) for arm in ("baseline", "candidate")]
            try:
                require(all(job is not None for job in pair_jobs), "missing-or-duplicate-sample-job")
                require(qualified_data is not None, "qualification-unavailable")
                facts = {
                    arm: validate_job(job_map[state][(pair_number, arm)], run_id, attempt, workflow_sha,
                                      state, pair_number, arm)
                    for arm in ("baseline", "candidate")
                }
                first_arm = "baseline" if pair_number % 2 else "candidate"
                second_arm = "candidate" if first_arm == "baseline" else "baseline"
                require(
                    facts[first_arm]["started"] < facts[first_arm]["completed"]
                    and facts[first_arm]["completed"] <= facts[second_arm]["started"]
                    and facts[second_arm]["started"] < facts[second_arm]["completed"],
                    "sample-job-chronology",
                )
                require(facts["baseline"]["runnerId"] != facts["candidate"]["runnerId"],
                        "sample-runners-not-separated")
                pair_samples = {}
                for arm in ("baseline", "candidate"):
                    job = job_map[state][(pair_number, arm)]
                    sample_name = f"sample-{state}-{pair_number}-{arm}-{attempt}"
                    artifact = only(artifacts, lambda item, name=sample_name: item.get("name") == name,
                                    "sample-artifact-missing-or-ambiguous")
                    value, artifact_info = verify_artifact(
                        artifact, run, expected_name=sample_name, member_name="sample.json",
                        start=facts[arm]["started"], end=facts[arm]["completed"],
                    )
                    receipt = validate_sample(
                        value, run_id=run_id, attempt=attempt, workflow_sha=workflow_sha,
                        qualification=qualified_data, state=state, pair=pair_number, arm=arm,
                    )
                    pair_samples[arm] = {
                        **facts[arm],
                        "receipt": receipt,
                        "artifact": artifact_info,
                    }
                admitted[state].append({"pair": pair_number, **pair_samples})
            except InvalidEvidence as error:
                reasons[error.reason] = reasons.get(error.reason, 0) + 1
                exclusions_by_state[state][error.reason] = (
                    exclusions_by_state[state].get(error.reason, 0) + 1
                )

    if proof_error and qualified_data and not qualified_data["resourceProofPassed"]:
        reasons["external-resource-proof-invalid"] = 1
    return build_report(
        run_id, attempt, workflow_sha, qualification, qualified_data, admitted, total_attempts,
        state_attempts, reasons, exclusions_by_state, proof, proof_error, qualification_artifact_info,
        retained_dir, candidate_evidence_failures,
    )


def write_new_json(path, value):
    payload = (json.dumps(value, indent=2, sort_keys=True, allow_nan=False) + "\n").encode("utf-8")
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL
    descriptor = os.open(path, flags, 0o600)
    try:
        with os.fdopen(descriptor, "wb") as stream:
            stream.write(payload)
    except BaseException:
        try:
            path.unlink()
        except OSError:
            pass
        raise


def positive_int(text):
    try:
        result = int(text)
    except ValueError as error:
        raise argparse.ArgumentTypeError("must be a positive integer") from error
    if result < 1:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return result


def workflow_sha(text):
    if not re.fullmatch(r"[0-9a-fA-F]{40}", text):
        raise argparse.ArgumentTypeError("must be a full 40-character hexadecimal SHA")
    return text.lower()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, type=positive_int)
    parser.add_argument("--attempt", required=True, type=positive_int)
    parser.add_argument("--workflow-sha", required=True, type=workflow_sha)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--retained-dir", type=Path)
    parser.add_argument("--resource-proof", type=Path)
    args = parser.parse_args(argv)
    if args.output.suffix.lower() != ".json":
        parser.error("--output must name a .json path")
    if os.path.lexists(args.output):
        parser.error("--output must be a new path")
    try:
        report = collect_report(args.run, args.attempt, args.workflow_sha, args.resource_proof, args.retained_dir)
        write_new_json(args.output, report)
    except (InvalidEvidence, OSError) as error:
        reason = error.reason if isinstance(error, InvalidEvidence) else "report-write-failed"
        parser.exit(2, f"evidence profile study report failed: {reason}\n")
    print(json.dumps({"output": str(args.output), "verdict": report["verdict"]}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
