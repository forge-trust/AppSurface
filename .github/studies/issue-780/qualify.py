"""Gate the pair matrix on complete matched untimed replays."""

from pathlib import Path
from hashlib import sha256
import json
import os
import re
import sys


def read(path):
    return json.loads(path.read_bytes())


here = Path(__file__).parent
definition = read(here / "definition.json")
env = os.environ
run = int(env["GITHUB_RUN_ID"])
attempt = int(env["GITHUB_RUN_ATTEMPT"])
study_sha = env["GITHUB_SHA"]
seed = env["SEED_SHA"]
assert re.fullmatch(r"[0-9a-f]{64}", seed)
probes = Path(sys.argv[1])
policy = plan = None
assert len(list(probes.iterdir())) == 4
for state in definition["states"]:
    for arm in ("baseline", "candidate"):
        probe = probes / f"probe-{state}-{arm}-{attempt}"
        r = read(probe / "sample.json")
        assert r["qualificationOnly"] and r["pair"] == 0
        assert (r["arm"], r["state"]) == (arm, state)
        assert (r["run"], r["attempt"], r["studySha"]) == (run, attempt, study_sha)
        assert r["cacheSeedSha256"] == (seed if state == "warm" else None)
        assert r["head"] == definition["head"] and r["base"] == definition["base"]
        assert r["sourceDigest"] == definition["sourceDigest"]
        assert r["runnerImageVersion"] == definition["runner"]["build"]
        assert r["sdk"] == definition["runner"]["sdk"]
        q = read(probe / "quality.json")
        assert q == {
            "planDigest": r["planDigest"], "packageIndex": ["package-checks"], "mixed": ["package-checks"],
            "fallback": ["package-checks"], "noEvidence": [],
        }
        if arm == "baseline":
            assert r["policySha256"] == definition["baselinePolicySha256"]
            assert r["planDigest"] == definition["baselinePlanDigest"]
            assert r["claim"] == "TargetedComplete" and r["selection"]
        else:
            assert r["claim"] == "NoEvidenceRequired" and not r["selection"]
            if policy is not None:
                assert (policy, plan) == (r["policySha256"], r["planDigest"])
            policy, plan = r["policySha256"], r["planDigest"]

quality = {
    "reviews": [
        {"case": "PR #960", "requiredRisks": ["docs-export", "release-contract"],
         "allowedPackageChecks": [], "baselinePackageChecks": ["package-checks"], "candidatePackageChecks": []},
        {"case": "package-index", "requiredRisks": ["package/manifest@1", "package/generated-docs@1", "package/archives-and-consumers@1"],
         "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"], "candidatePackageChecks": ["package-checks"]},
        {"case": "mixed", "requiredRisks": ["package/manifest@1", "package/generated-docs@1", "package/archives-and-consumers@1"],
         "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"], "candidatePackageChecks": ["package-checks"]},
        {"case": "fallback", "requiredRisks": ["package/manifest@1", "package/generated-docs@1", "package/archives-and-consumers@1"],
         "allowedPackageChecks": ["package-checks"], "baselinePackageChecks": ["package-checks"], "candidatePackageChecks": ["package-checks"]},
        {"case": "no-evidence", "requiredRisks": [], "allowedPackageChecks": [], "baselinePackageChecks": [], "candidatePackageChecks": []}
    ],
    "baselineMissedRisks": 0, "candidateMissedRisks": 0, "requiredRisks": 11,
    "baselineUnnecessarySelections": 1, "baselineSelectedChecks": 4,
    "candidateUnnecessarySelections": 0, "candidateSelectedChecks": 3,
    "knownRegression": definition["knownRegression"],
    "retainedChecks": {"docsExportOrdinaryRun": 46167211798, "releaseContractPassed": True},
}
receipt = {
    "schemaVersion": 1, "run": run, "attempt": attempt, "studySha": study_sha,
    "definitionSha256": sha256((here / "definition.json").read_bytes()).hexdigest(),
    "baselinePolicySha256": definition["baselinePolicySha256"], "candidatePolicySha256": policy,
    "candidatePlanDigest": plan, "warmSeedSha256": seed, "parityVerified": True,
    "selectionQuality": quality, "resourceProofPassed": False,
}
Path(sys.argv[2]).write_text(json.dumps(receipt, indent=2) + "\n")
with open(env["GITHUB_OUTPUT"], "a") as stream:
    print(f"policy={policy}\nplan={plan}", file=stream)
