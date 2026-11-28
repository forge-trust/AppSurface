"""Emit one bounded observation after the repository gate verifies it."""

import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys


def read(path):
    return json.loads(path.read_bytes())


root = Path(sys.argv[1])
definition = read(Path(sys.argv[2]))
env = os.environ
arm = env["ARM"]
pair = int(env["PAIR"])
state = env["STATE"]
assert arm in {"baseline", "candidate"} and state in definition["states"]
assert 0 <= pair <= definition["pairsPerState"]
plan = read(root / "plan/evidence-plan.json")
manifest = read(root / "proof/evidence-manifest.json")
metrics = manifest["Metrics"]
claim = "TargetedComplete" if arm == "baseline" else "NoEvidenceRequired"
assert manifest["PlanDigest"] == plan["PlanDigest"]
assert manifest["ClaimKind"] == claim
assert manifest["ExecutionVerdict"] == "Passed"
assert manifest["Eligibility"] == "PullRequestGate"
assert manifest["SelectedObligationIds"] == manifest["ClosedObligationIds"]
assert manifest["UnmediatedObligationIds"] == []
assert manifest.get("ReuseCount", 0) == 0
assert metrics["CleanupCompleted"] is True
assert metrics["ResourceReadinessMilliseconds"] == 0
assert manifest["ResourceResults"] == []
if arm == "baseline":
    assert manifest["ClosedObligationIds"] == ["package-checks-0", "package-checks-1", "package-checks-2"]
    [producer] = manifest["ProducerResults"]
    assert producer["ProducerId"] == "package-checks"
    assert sorted(artifact["LogicalName"] for artifact in producer["Artifacts"]) == [
        "package-manifest", "package-report", "result", "transcript"
    ]
    transcript = next(item for item in producer["Artifacts"] if item["LogicalName"] == "transcript")
    stages = read(root / "proof/artifacts/package-checks" / transcript["RelativePath"])
    assert [(stage["Stage"], stage["ExitCode"]) for stage in stages] == [
        ("gate", 0), ("verify", 0), ("verify-packages", 0)
    ]
else:
    assert manifest["ClosedObligationIds"] == [] and manifest["ProducerResults"] == []

phases = {"planner": [], "manifest": []}
for phase, value in re.findall(r"^ASEVD_STUDY (planner|manifest) (\S+)$", (root / "phases").read_text(), re.M):
    duration = float(value)
    assert math.isfinite(duration) and duration >= 0
    phases[phase].append(duration)
assert len(phases["planner"]) == 7 and len(phases["manifest"]) == 2
runner = definition["runner"]
result = {
    "schemaVersion": 1, "run": int(env["GITHUB_RUN_ID"]), "attempt": int(env["GITHUB_RUN_ATTEMPT"]),
    "studySha": env["GITHUB_SHA"], "head": definition["head"], "base": definition["base"],
    "arm": arm, "pair": pair, "state": state, "sourceDigest": read(root / "changes/source-state.json")["sourceDigest"],
    "policySha256": hashlib.sha256((root / "policy.json").read_bytes()).hexdigest(),
    "planDigest": plan["PlanDigest"], "claim": claim, "resources": 0,
    "cleanupMs": metrics["CleanupMilliseconds"], "resourceStartupMs": 0,
    "plannerMs": sum(phases["planner"]), "manifestMs": sum(phases["manifest"]),
    "cacheSeedSha256": env["WARM_SEED_SHA"] if state == "warm" else None,
    "runnerImage": runner["image"], "runnerImageVersion": env["ImageVersion"],
    "sdk": runner["sdk"], "selection": arm == "baseline",
}
for tool in ("node", "pnpm", "aspire"):
    result[tool] = runner[tool] if arm == "baseline" else None
if pair == 0:
    result["qualificationOnly"] = True
    for key in ("cleanupMs", "resourceStartupMs", "plannerMs", "manifestMs"):
        del result[key]
(root / "sample.json").write_text(json.dumps(result, indent=2) + "\n")
