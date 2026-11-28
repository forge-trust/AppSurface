#!/usr/bin/env python3
"""Remove only the exact stopped resources of an injected failed anchor fixture; retain its failed active marker.

This is fixture-owned cleanup, not the product recovery API. It never starts or stops a workload, never clears
active state, and refuses other failed-run kinds. An independent stopped observer must accept all resources first.
"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

spec = importlib.util.spec_from_file_location("memory", Path(__file__).with_name("run-sealed-memory.py"))
memory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(memory)
boundary = memory.boundary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--receipt", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    failed = json.loads(args.receipt.read_text())
    if (failed["passed"] or not failed.get("workerAnchor") or not failed.get("injectedStopObserverFailure")
            or failed["fault"] != "AssertionError: independent-stop-before-clean"
            or failed.get("observerResult") != {"exitCode": 75, "code": "InjectedStoppedObservationUnavailable", "leaseHeld": True, "activeHeld": True}
            or failed.get("retainedAtObserverFailure") != {"active": True, "privateInput": True}):
        raise ValueError("not the expected failed observer fixture")
    root = Path(failed["runtimeRoot"])
    if (root.is_symlink() or root.resolve() != root or root.parent != Path("/private/tmp")
            or not root.name.startswith("i888-memory-run-") or root.stat().st_uid != os.getuid()):
        raise ValueError("unqualified private fixture root")
    observation = json.loads((root / "stopped-observation.json").read_text())
    config = observation["config"]
    resources = observation["resources"]
    if (not observation["injectObserverFailure"] or config["root"] != str(root)
            or config["privateInput"] != str(root / "private-input.json")
            or config["name"] != "appsurface-issue888-memory-" + config["runId"]
            or config["daemonId"] != failed["daemonId"] or len(resources) != 4):
        raise ValueError("fixture identity changed")
    names = {config["name"] + suffix for suffix in ("", "-worker", "-web", "-foreign")}
    if {member["name"] for member, _ in resources} != names:
        raise ValueError("unexpected fixture resources")
    for member, _ in resources:
        if any(member[key] != config[key] for key in ("root", "runId", "daemonId", "imageId")):
            raise ValueError("foreign fixture resource")
    args.evidence.mkdir(parents=True, exist_ok=False)
    docker = boundary.Docker("unix://" + str(Path.home() / ".docker/run/docker.sock"))
    expected = {"ownerId": config["runId"], "daemonId": config["daemonId"], "name": config["name"]}
    started = time.monotonic()
    deadline = started + 30
    with boundary.held_lock(root):
        if json.loads((root / "active.json").read_text()) != expected or not Path(config["privateInput"]).is_file():
            raise RuntimeError("failure did not retain active owner and private input")
        boundary.durable_new(args.evidence / "retained-at-failure.json", {
            "activeOwner": expected, "privateInputExists": True, "originalPassed": failed["passed"],
            "originalFault": failed["fault"], "originalReceiptSha256": hashlib.sha256(args.receipt.read_bytes()).hexdigest()})
        # Keep the original injected observation untouched. Only this separate cleanup observation is uninjected.
        observation["injectObserverFailure"] = False
        cleanup_observation = root / "fixture-cleanup-observation.json"
        boundary.durable_new(cleanup_observation, observation)
        observed = subprocess.run([sys.executable, str(Path(__file__).with_name("run-sealed-memory.py").resolve()),
                                   "--observe-stop", str(cleanup_observation), str(deadline)],
                                  capture_output=True, text=True, timeout=boundary.remaining(deadline, 15))
        if observed.returncode != 0:
            raise RuntimeError("independent cleanup observation unavailable")
        preclean = json.loads(observed.stdout)
        boundary.durable_new(args.evidence / "preclean.json", preclean)
        for member, identifier in reversed(resources):
            boundary.verify_stopped(docker, member, identifier, deadline)
            docker.run("rm", identifier, deadline=deadline)
            if docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + member["name"] + "$", deadline=deadline).stdout.strip():
                raise RuntimeError("fixture removal unavailable")
        Path(config["privateInput"]).unlink()
        boundary.sync_directory(root)
        if json.loads((root / "active.json").read_text()) != expected:
            raise RuntimeError("failed active marker changed")
        boundary.remaining(deadline)
        result = {"schema": 1, "kind": "exact-stopped-fixture-cleanup", "passed": True,
                  "workloadsStarted": False, "workloadsStopped": False, "activeMarkerRetained": True,
                  "originalFailurePreserved": True, "privateInputRemovedAfterObservation": True,
                  "resourcesRemoved": [identifier for _, identifier in resources],
                  "seconds": time.monotonic() - started,
                  "sourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
        boundary.durable_new(args.evidence / "receipt.json", result)
    print(json.dumps(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
