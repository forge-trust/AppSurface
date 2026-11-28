#!/usr/bin/env python3
"""Compare the pinned #780 untimed package replay with its ordinary CI reference."""

import argparse
from contextlib import contextmanager
from hashlib import sha256
from io import BytesIO
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
from threading import Timer
from zipfile import ZipFile

REPO = "forge-trust/AppSurface"
WORKFLOW = ".github/workflows/evidence-profile-replay.yml"
MAX_REFERENCE = 256 * 1024
MAX_ARCHIVE = 4 * 1024 * 1024
MAX_LOG = 16 * 1024 * 1024
REPLAY_ONLY_STEPS = {
    "Record replay scope and identity",
    "Upload untimed replay identity",
}
ANSI = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
CACHE_KEY = r"node-cache-Linux-x64-pnpm-[a-f0-9]{64}"


class InvalidEvidence(ValueError):
    """Required evidence was missing, ambiguous, or inconsistent."""


def require(condition, name):
    if not condition:
        raise InvalidEvidence(name)


def read_reference(path, digest):
    with path.open("rb") as stream:
        data = stream.read(MAX_REFERENCE + 1)
    require(len(data) <= MAX_REFERENCE, "ordinary-reference-size")
    require(sha256(data).hexdigest() == digest, "ordinary-reference-digest")
    reference = json.loads(data)
    require(
        reference["schemaVersion"] == 1
        and reference["repository"] == REPO
        and reference["issue"] == 780
        and reference["pullRequest"] == 960
        and reference["ordinaryReferenceQualified"] is True
        and reference["untimedReplayVerified"] is False
        and reference["timingSamplesAdmitted"] == 0,
        "ordinary-reference-identity",
    )
    return reference


def api(endpoint, *, binary=False, limit=MAX_ARCHIVE):
    args = ["gh", "api"]
    if binary:
        args.append("--allow-escape-sequences")
    with subprocess.Popen(
        [*args, f"repos/{REPO}/{endpoint}"], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL
    ) as process:
        deadline = Timer(30, process.kill)
        deadline.start()
        try:
            data = process.stdout.read(limit + 1)
            if len(data) > limit:
                process.kill()
                raise InvalidEvidence("provider-response-size")
            require(process.wait() == 0, "provider-response-unavailable")
        finally:
            deadline.cancel()
    return data if binary else json.loads(data)


def only(rows, condition, label):
    matches = [row for row in rows if condition(row)]
    require(len(matches) == 1, label)
    return matches[0]


def completed_run(run_id, attempt, path, sha, events):
    run = api(f"actions/runs/{run_id}/attempts/{attempt}")
    require(
        run["id"] == run_id and run["run_attempt"] == attempt and run["path"] == path
        and run["event"] in events and run["head_sha"] == sha
        and run["head_repository"]["full_name"] == REPO,
        "provider-run-identity",
    )
    require(run["status"] == "completed" and run["conclusion"] == "success", "workflow-did-not-pass")
    return run


def package_job(run):
    attempt = run["run_attempt"]
    page = api(f"actions/runs/{run['id']}/attempts/{attempt}/jobs?per_page=100")
    require(page["total_count"] == len(page["jobs"]) == 1, "package-job-inventory")
    job = only(page["jobs"], lambda row: row["name"] == "package-gate", "package-job")
    require(
        job["run_id"] == run["id"] and job["run_attempt"] == attempt
        and job["head_sha"] == run["head_sha"] and job["conclusion"] == "success",
        "package-job-identity-or-outcome",
    )
    return job


def single_log_value(log, pattern, label):
    values = set(re.findall(pattern, log, re.MULTILINE))
    require(len(values) == 1, label)
    return values.pop()


def runner_facts(job):
    log = api(f"actions/jobs/{job['id']}/logs", binary=True, limit=MAX_LOG)
    log = ANSI.sub("", log.decode("utf-8", "replace"))
    image = single_log_value(
        log, r"##\[group\]Runner Image\s*\n[^\n]*Image: ([A-Za-z0-9 ._-]+)\r?\n[^\n]*Version: ([0-9.]+)",
        "runner-image",
    )
    checkout = single_log_value(
        log, r"\[command\][^\n]*git[^\n]*log -1 --format[^\n]*\n[^\n]*?([0-9a-f]{40})",
        "package-checkout",
    )
    dotnet, arch = single_log_value(log, r"dotnet-sdk-(\d+\.\d+\.\d+)-linux-(x64)\.tar\.gz", "dotnet-sdk")
    toolchain = {
        "dotnet": dotnet,
        "architecture": arch,
        "node": single_log_value(log, r"\bnode: v(\d+\.\d+\.\d+)", "node"),
        "pnpm": single_log_value(log, r"using pnpm v(\d+\.\d+\.\d+)\b", "pnpm"),
        "aspire": single_log_value(log, r"aspire.cli@(\d+\.\d+\.\d+)\b", "aspire-cli"),
    }
    hits = set(re.findall(r"Cache restored from key: (" + CACHE_KEY + r")", log))
    misses = set(re.findall(r"pnpm cache is not found|Cache not found for input keys:", log))
    require((len(hits) == 1 and not misses) or (not hits and len(misses) >= 1), "pnpm-cache-observation")
    claim, plan = single_log_value(
        log, r"Evidence gate passed: ([A-Za-z]+) \(([0-9a-f]{64})\)", "package-verifier-log"
    )
    return {
        "runnerImage": dict(zip(("name", "version"), image)),
        "checkout": checkout,
        "toolchain": toolchain,
        "pnpmCache": {"outcome": "hit" if hits else "miss", "key": next(iter(hits), None)},
        "verification": {"claim": claim, "planDigest": plan},
    }


@contextmanager
def download(run, name):
    page = api(f"actions/runs/{run['id']}/artifacts?per_page=100")
    require(page["total_count"] == len(page["artifacts"]), "artifact-inventory")
    meta = only(page["artifacts"], lambda row: row["name"] == name, "artifact-name")
    origin = meta["workflow_run"]
    require(
        not meta["expired"] and origin["id"] == run["id"] and origin["head_sha"] == run["head_sha"]
        and meta["created_at"] >= run["run_started_at"] and 0 < meta["size_in_bytes"] <= MAX_ARCHIVE,
        "artifact-origin-or-size",
    )
    data = api(f"actions/artifacts/{meta['id']}/zip", binary=True)
    require(meta["digest"] == "sha256:" + sha256(data).hexdigest(), "artifact-archive-digest")
    with ZipFile(BytesIO(data)) as archive:
        members = archive.infolist()
        require(
            len(members) <= 256 and len(set(archive.namelist())) == len(members)
            and sum(member.file_size for member in members) <= 32 * 1024 * 1024,
            "archive-member-limits",
        )
        for member in members:
            path = PurePosixPath(member.filename)
            require(
                not path.is_absolute() and ".." not in path.parts and "\\" not in member.filename,
                "archive-member-path",
            )
        yield archive, {key: meta[key] for key in ("id", "name", "digest")}


def member_json(archive, name):
    require(archive.getinfo(name).file_size <= MAX_REFERENCE, "json-member-size")
    return json.loads(archive.read(name))


def verify_reference_archives(reference):
    runs = {}
    for original in reference["ordinaryWorkflows"]:
        run = completed_run(
            original["run"], original["attempt"], original["path"], reference["head"], {"pull_request"}
        )
        workflow = api(f"contents/{original['path']}?ref={reference['testMerge']}")
        require(workflow["sha"] == original["workflowBlob"], "ordinary-workflow-blob")
        for expected in original["archives"]:
            with download(run, expected["name"]) as (_, actual):
                require(expected == actual, "ordinary-archive-binding")
        runs[original["scope"]] = run
    return runs


def identity_expected(reference, ordinary, run):
    return {
        "schemaVersion": 1,
        "mode": "untimed",
        "repository": REPO,
        "run": str(run["id"]),
        "attempt": str(run["run_attempt"]),
        "workflow": run["head_sha"],
        "base": reference["targetBase"],
        "head": reference["head"],
        "merge": reference["testMerge"],
        "tree": reference["checkoutTree"],
        "policy": reference["sharedPolicyBlob"],
        "ordinaryRun": str(ordinary["run"]),
        "ordinaryAttempt": str(ordinary["attempt"]),
        "timingSamplesAdmitted": 0,
    }


def package_proof(archive, run):
    suffix = f"{run['id']}-{run['run_attempt']}"
    plan_root = f"package-plan-{suffix}/"
    evidence_root = f"package-evidence-{suffix}/"
    source = member_json(archive, plan_root + "changes/source-state.json")
    plan = member_json(archive, plan_root + "plan/evidence-plan.json")
    paths = member_json(archive, plan_root + "changes/changed-paths.json")
    selection = member_json(archive, plan_root + "plan/selection.json")
    manifest = member_json(archive, evidence_root + "evidence-manifest.json")
    require(
        paths == plan["ChangedPaths"] == [
            {"Kind": row["kind"], "Path": row["path"], "PreviousPath": row["previousPath"]}
            for row in source["paths"]
        ],
        "replay-changed-paths",
    )
    require(
        manifest["ExecutionVerdict"] == "Passed" and manifest["Eligibility"] == "PullRequestGate"
        and manifest["Metrics"]["CleanupCompleted"] is True
        and manifest["PlanDigest"] == plan["PlanDigest"],
        "package-manifest-verdict-or-plan",
    )
    producer = only(manifest["ProducerResults"], lambda p: p["ProducerId"] == "package-checks", "package-producer")
    require(len(manifest["ProducerResults"]) == 1 and producer["Outcome"] == "Passed", "package-producer-outcome")
    prefix = evidence_root + "artifacts/package-checks/"
    files = []
    for item in producer["Artifacts"]:
        part = PurePosixPath(item["RelativePath"])
        require(not part.is_absolute() and ".." not in part.parts, "package-proof-path")
        name = prefix + item["RelativePath"]
        require(archive.getinfo(name).file_size == item["LengthBytes"] <= MAX_ARCHIVE, "package-proof-size")
        require(sha256(archive.read(name)).hexdigest() == item["Sha256"], "package-proof-digest")
        files.append((item["LogicalName"], item["RelativePath"]))
    result = member_json(archive, prefix + "proof/result.json")
    commands = [(item["Stage"], item["ExitCode"]) for item in result["commands"]]
    require(result["completed"] is True, "package-proof-incomplete")
    return {
        "source": source,
        "selection": selection,
        "claim": manifest["ClaimKind"],
        "planDigest": plan["PlanDigest"],
        "manifestDigest": manifest["ManifestDigest"],
        "selectedObligations": sorted(manifest["SelectedObligationIds"]),
        "closedObligations": sorted(manifest["ClosedObligationIds"]),
        "proofFiles": sorted(files),
        "commands": commands,
    }


def compare(reference, ordinary, replay_job, observed, package, projections):
    """Return field-level differences without timing values or raw provider data."""
    differences = []

    def check(field, expected, actual):
        if actual != expected:
            differences.append({"field": field, "ordinary": expected, "replay": actual})

    original_job = ordinary["jobs"][0]
    original_proof = ordinary["proofs"][0]
    check("runner-labels", original_job["labels"], replay_job["labels"])
    check("job-conclusion", original_job["conclusion"], replay_job["conclusion"])
    common_steps = [s for s in replay_job["steps"] if s["name"] not in REPLAY_ONLY_STEPS]
    check("package-steps", original_job["steps"], common_steps)
    replay_steps = [s for s in replay_job["steps"] if s["name"] in REPLAY_ONLY_STEPS]
    for name in sorted(REPLAY_ONLY_STEPS):
        check(f"replay-step:{name}", ["success"], [s["conclusion"] for s in replay_steps if s["name"] == name])
    for key in ("runnerImage", "checkout", "toolchain", "pnpmCache", "verification"):
        check(key, observed["ordinary"][key], observed["replay"][key])
    expected_source = [
        reference["selectionMergeBase"], reference["head"], reference["normalizedChangeDigest"],
        sorted(reference["fixturePaths"]),
    ]
    source = package["source"]
    check("fixture-source", expected_source, [
        source["baseCommit"], source["headCommit"], source["sourceDigest"],
        sorted(path["path"] for path in source["paths"]),
    ])
    check("package-selection", ["package-checks"], package["selection"]["producerIds"])
    check("package-profile", "packages", package["selection"]["profileId"])
    check("package-prerequisites", [True, False, False], [
        package["selection"]["requiresPackageChecks"], package["selection"]["requiresCoverage"],
        package["selection"]["requiresNativeCoverage"],
    ])
    check("package-claim", original_proof["claim"], package["claim"])
    check("package-plan", original_proof["planDigest"], package["planDigest"])
    check("replay-verifier", {"claim": package["claim"], "planDigest": package["planDigest"]}, observed["replay"]["verification"])
    obligation_ids = sorted(original_proof["closedObligations"])
    check("selected-obligations", obligation_ids, package["selectedObligations"])
    check("closed-obligations", obligation_ids, package["closedObligations"])
    check("package-proof-files", sorted((p["LogicalName"], p["RelativePath"]) for p in original_proof["proofFiles"]), package["proofFiles"])
    check("package-proof-stages", [("gate", 0), ("verify", 0), ("verify-packages", 0)], package["commands"])
    for scope in ("coverage", "native"):
        expected = only(reference["ordinaryWorkflows"], lambda w: w["scope"] == scope, "ordinary-scope")
        require(all(p["claim"] == "NoEvidenceRequired" for p in expected["proofs"]), "ordinary-sibling-scope")
        plan, selection = projections[scope]
        check(f"{scope}-plan", sorted({p["planDigest"] for p in expected["proofs"]}), [plan["PlanDigest"]])
        check(f"{scope}-selection", [], selection["producerIds"])
        check(f"{scope}-prerequisites", [False, False, False], [
            selection["requiresCoverage"], selection["requiresNativeCoverage"], selection["requiresPackageChecks"]
        ])
    return differences


def verify(reference, run_id, attempt, workflow_sha):
    ordinary = only(reference["ordinaryWorkflows"], lambda w: w["scope"] == "packages", "ordinary-package-scope")
    replay_run = completed_run(run_id, attempt, WORKFLOW, workflow_sha, {"push", "workflow_dispatch"})
    ordinary_run = verify_reference_archives(reference)["packages"]
    ordinary_job = package_job(ordinary_run)
    require(ordinary_job["id"] == ordinary["jobs"][0]["id"], "ordinary-job-binding")
    replay_job = package_job(replay_run)
    recorded = {"ordinary": runner_facts(ordinary_job), "replay": runner_facts(replay_job)}
    for key, value in (("runnerImage", "image"), ("toolchain", "toolchain"), ("checkout", "checkout")):
        require(recorded["ordinary"][key] == ordinary["jobs"][0][value], "ordinary-log-receipt-binding")
    with download(replay_run, f"appsurface-package-gate-validation-diagnostics-{replay_run['run_number']}") as (archive, package_artifact):
        package = package_proof(archive, replay_run)
    with download(replay_run, f"issue-780-untimed-replay-{attempt}") as (archive, identity_artifact):
        require(member_json(archive, "identity.json") == identity_expected(reference, ordinary, replay_run), "replay-receipt-identity")
        projections = {
            scope: (member_json(archive, f"{scope}/evidence-plan.json"), member_json(archive, f"{scope}/selection.json"))
            for scope in ("coverage", "native")
        }
    steps = [{"name": s["name"], "conclusion": s["conclusion"]} for s in replay_job["steps"]]
    compared_job = {**replay_job, "steps": steps}
    mismatches = compare(reference, ordinary, compared_job, recorded, package, projections)
    return {
        "ordinaryRun": ordinary_run["id"],
        "ordinaryAttempt": ordinary_run["run_attempt"],
        "fixtureHead": reference["head"],
        "fixtureBase": reference["targetBase"],
        "replayJob": replay_job["id"],
        "replayWorkflowBlob": api(f"contents/{WORKFLOW}?ref={workflow_sha}")["sha"],
        "replayArtifacts": [package_artifact, identity_artifact],
        "runnerFacts": recorded,
        "replayPackagePlan": package["planDigest"],
        "replayPackageManifest": package["manifestDigest"],
        "siblingPlans": {scope: plan["PlanDigest"] for scope, (plan, _) in projections.items()},
        "mismatches": mismatches,
        "parityVerified": not mismatches,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ordinary-reference", type=Path, required=True)
    parser.add_argument("--ordinary-sha256", required=True)
    parser.add_argument("--run", type=int, required=True)
    parser.add_argument("--attempt", type=int, required=True)
    parser.add_argument("--workflow-sha", required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    report = {
        "schemaVersion": 1, "mode": "untimed", "replayRun": args.run, "replayAttempt": args.attempt,
        "workflowRevision": args.workflow_sha, "parityVerified": False,
        "timingSamplesAdmitted": 0, "timingAdmission": "blocked",
    }
    try:
        require(args.run > 0 and args.attempt > 0, "run-and-attempt")
        require(re.fullmatch(r"[a-f0-9]{40}", args.workflow_sha) is not None, "workflow-sha")
        require(re.fullmatch(r"[a-f0-9]{64}", args.ordinary_sha256) is not None, "ordinary-sha")
        reference = read_reference(args.ordinary_reference, args.ordinary_sha256)
        report.update(verify(reference, args.run, args.attempt, args.workflow_sha))
    except InvalidEvidence as error:
        report["error"] = str(error)
    except (KeyError, ValueError, OSError, subprocess.SubprocessError):
        report["error"] = "invalid-or-unavailable-evidence"
    with args.report.open("x", encoding="utf-8") as output:
        json.dump(report, output, indent=2)
        output.write("\n")
    return 0 if report["parityVerified"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
