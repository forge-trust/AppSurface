#!/usr/bin/env python3
"""Run the private BCL-only sealed-memory probe from an isolated prepared payload."""

import argparse
import fcntl
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import select
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile

from worker_payload import read_worker_payload, worker_payload_target

spec = importlib.util.spec_from_file_location("boundary", Path(__file__).with_name("run-supervised-boundary.py"))
boundary = importlib.util.module_from_spec(spec)
spec.loader.exec_module(boundary)


def observe_stop(path, deadline):
    """Independently verify exact stopped resources while their owner's lease and marker remain held."""
    observation = json.loads(Path(path).read_text())
    config = observation["config"]
    root = Path(config["root"])
    if root.parent != Path("/private/tmp") or not root.name.startswith("i888-memory-run-") or root.stat().st_uid != os.getuid():
        raise ValueError("unqualified observation root")
    docker = boundary.Docker("unix://" + str(Path.home() / ".docker/run/docker.sock"))
    with (root / "owner.lock").open("a+b") as contender:
        try:
            fcntl.flock(contender, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            pass
        else:
            raise RuntimeError("owner lease was released before stopped observation")
    expected = {"ownerId": config["runId"], "daemonId": config["daemonId"], "name": config["name"]}
    if json.loads((root / "active.json").read_text()) != expected:
        raise RuntimeError("active owner changed before stopped observation")
    if observation.get("injectObserverFailure"):
        print(json.dumps({"code": "InjectedStoppedObservationUnavailable", "leaseHeld": True, "activeHeld": True}))
        return 75
    states = []
    for member, identifier in observation["resources"]:
        item = boundary.verify_stopped(docker, member, identifier, deadline)
        states.append({"containerId": identifier, "status": item["State"]["Status"], "pid": item["State"]["Pid"]})
    if json.loads((root / "active.json").read_text()) != expected:
        raise RuntimeError("active owner changed during stopped observation")
    boundary.remaining(deadline)
    print(json.dumps({"independentProcess": True, "leaseHeld": True, "activeHeld": True, "states": states}))
    return 0


def joined_readers(docker, config, root, command, value, check, evidence, resources, attachments, assembly):
    attachment = subprocess.Popen(docker.prefix + ["start", "--attach", "--interactive", resources[0][1]],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    attachments.append(attachment)
    if not config.get("workerAnchor"):
        attachment.stdin.write(json.dumps({"secret": value, "authority": "https://identity.example/realms/sample", "client": "sample-web"}))
    attachment.stdin.close()
    attachment.stdin = None
    try:
        if config.get("workerAnchor"):
            deadline = time.monotonic() + 15
            metadata_file = root / "state/client-reference.json"
            while True:
                if attachment.poll() is not None:
                    raise RuntimeError("packaged worker anchor exited before metadata")
                if metadata_file.exists():
                    try:
                        ready = {"stage": "anchor-ready", "descriptor": json.loads(metadata_file.read_text())}
                        break
                    except json.JSONDecodeError:
                        pass
                time.sleep(min(0.03, boundary.remaining(deadline)))
            check("packaged-worker-bound-to-requested-run", ready["descriptor"]["RunId"] == config["runId"])
            check("packaged-worker-bound-to-requested-client", ready["descriptor"]["ClientId"] == "sample-web"
                  and ready["descriptor"]["Authority"] == "https://identity.example/realms/sample")
            private = Path(config["privateInput"])
            os.chmod(private, 0o600)
            private.write_text(json.dumps({"RunId": config["runId"], "Authority": "https://identity.example/realms/sample", "ClientId": "sample-web", "ClientSecret": "rotated-" + secrets.token_urlsafe(48)}))
            os.chmod(private, 0o400)
            check("bootstrap-source-rotated-after-memory-capture", value not in private.read_text())
        else:
            if not select.select([attachment.stdout], [], [], 10)[0]:
                raise TimeoutError("anchor descriptor unavailable")
            ready_line = attachment.stdout.readline()
            check("no-secret-in-anchor-output", value not in ready_line)
            ready = json.loads(ready_line)
        expected_keys = {"runDirectory", "runId", "authority", "clientId", "memoryIdentity"} if assembly != "SealedMemoryProbe" else {"parentPid", "parentStartTicks", "fd", "inode", "run", "authority", "client"}
        keys = {key[0].lower() + key[1:] for key in ready["descriptor"]}
        check("anchor-published-only-descriptor", ready["stage"] == "anchor-ready" and keys == expected_keys)
        descriptor = json.dumps(ready["descriptor"])
        check("no-secret-in-published-reference", value not in descriptor)
        (evidence / "reference.json").write_text(descriptor + "\n")
        for role in ("worker", "web", "foreign"):
            member = dict(config, name=config["name"] + "-" + role)
            member.pop("privateInput", None)
            create = list(command)
            if config.get("privateInput"):
                slot = create.index("type=bind,source=" + config["privateInput"] + ",target=/run/appsurface/client-input.json,readonly")
                del create[slot - 1:slot + 1]
            create[create.index("--name") + 1] = member["name"]
            if role != "foreign":
                member["anchorId"] = resources[0][1]
                create.remove("--init")
                create += ["--pid", "container:" + member["anchorId"]]
            identifier = docker.run(*create, "--entrypoint", "/usr/share/dotnet/dotnet", boundary.SDK,
                                    "/proof/code/" + assembly + ".dll", descriptor).stdout.strip()
            resources.append((member, identifier))
            boundary.durable_new(root / (role + "-created.json"), {"containerId": identifier})
            item = boundary.inspect_profile(docker.inspect(identifier), member, identifier)
            check("secret-not-in-member-declaration:" + role, value not in json.dumps(item))
            # This separate synthetic oracle is test input, not an application credential channel.
            result = subprocess.run(docker.prefix + ["start", "--attach", "--interactive", identifier],
                                    input=value, capture_output=True, text=True, timeout=10)
            check("secret-not-in-member-output:" + role, value not in result.stdout and value not in result.stderr)
            (evidence / (role + ".log")).write_text(result.stdout + result.stderr)
            observation = json.loads(result.stdout)
            if role == "foreign":
                check("foreign-namespace-reader-rejected", result.returncode == 20 and observation["accepted"] is False
                      and observation["failureStage"] in ("parent-identity", "seals", "file-identity"))
            else:
                check("member-read-same-sealed-capture:" + role, result.returncode == 0
                      and observation["accepted"] is True and observation["failureStage"] == "none")
            boundary.verify_stopped(docker, member, identifier, time.monotonic() + 5)
        return {"stage": "joined-readers-complete", "passed": True, "childrenStopped": True, "testCount": 2}
    finally:
        # The caller owns stop, attachment reaping, all boundary checks and clear under one shared deadline.
        pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared-root", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--joined-members", action="store_true")
    parser.add_argument("--assembly", choices=("SealedMemoryProbe", "ForgeTrust.AppSurface.Auth.Keycloak.Tests"), default="SealedMemoryProbe")
    parser.add_argument("--package", type=Path)
    parser.add_argument("--worker-anchor", action="store_true")
    parser.add_argument("--adapter-package", type=Path)
    injection = parser.add_mutually_exclusive_group()
    injection.add_argument("--inject-stop-observer-failure", action="store_true",
                           help="Retain active state and private input when the independent stopped observer is unavailable.")
    injection.add_argument("--inject-clear-commit-failure", action="store_true",
                           help="Retain active state after an injected failure in marker-clear commit.")
    args = parser.parse_args()
    if (args.inject_stop_observer_failure or args.inject_clear_commit_failure) and not args.worker_anchor:
        parser.error("injected worker-anchor controls require --worker-anchor")
    execution_started = time.monotonic()
    os.umask(0o077)
    prepared = args.prepared_root.resolve()
    if prepared.parent != Path("/private/tmp") or not prepared.name.startswith("i888-memory-") or prepared.stat().st_uid != os.getuid():
        raise ValueError("unqualified private fixture root")
    root = Path(tempfile.mkdtemp(prefix="i888-memory-run-", dir="/private/tmp")).resolve()
    shutil.copytree(prepared / "code", root / "code")
    for name in ("state", "empty-nuget"):
        (root / name).mkdir(mode=0o700)
    args.evidence.mkdir(parents=True, exist_ok=False)
    docker = boundary.Docker("unix://" + str(Path.home() / ".docker/run/docker.sock"))
    run_id = uuid.uuid4().hex
    config = {"root": str(root), "runId": run_id, "name": "appsurface-issue888-memory-" + run_id,
              "scenario": "memory", "daemonId": docker.daemon(), "imageId": docker.inspect(boundary.SDK)["Id"]}
    checks = []
    identifier = None
    fault = None
    stop_seconds = None
    summary = None
    resources = []
    attachments = []
    observer_result = None
    retained_at_observer_failure = None
    source_folder = "sealed-memory-probe" if args.assembly == "SealedMemoryProbe" else "memory-library-probe"
    value = secrets.token_urlsafe(48)
    if args.worker_anchor:
        if not args.joined_members or args.adapter_package is None or args.assembly != "ForgeTrust.AppSurface.Auth.Keycloak.Tests":
            raise ValueError("packaged anchor proof requires joined members and exact adapter package")
        private = root / "private-input.json"
        boundary.durable_new(private, {"RunId": run_id, "Authority": "https://identity.example/realms/sample", "ClientId": "sample-web", "ClientSecret": value})
        os.chmod(private, 0o400)
        config["privateInput"] = str(private)
        config["workerAnchor"] = True

    def check(name, condition):
        checks.append({"name": name, "passed": bool(condition)})
        if not condition:
            raise AssertionError(name)

    with boundary.held_lock(root):
        boundary.durable_new(root / "active.json", {"ownerId": run_id, "daemonId": config["daemonId"], "name": config["name"]})
        try:
            check("empty-cache-before-execution", not any((root / "empty-nuget").iterdir()))
            check("compiled-probe-source-current", (prepared / "source/Program.cs").read_bytes()
                  == Path(__file__).with_name(source_folder).joinpath("Program.cs").read_bytes())
            dependencies = json.loads((root / ("code/" + args.assembly + ".deps.json")).read_text())["libraries"]
            check("payload-has-no-Aspire-dependencies", not any("Aspire" in name for name in dependencies))
            if args.assembly == "SealedMemoryProbe":
                check("payload-has-no-package-dependencies", set(dependencies) == {"SealedMemoryProbe/1.0.0"})
            else:
                if args.package is None:
                    raise ValueError("extracted-library proof requires the exact package")
                with zipfile.ZipFile(args.package) as package:
                    expected = package.read("lib/net10.0/ForgeTrust.AppSurface.Auth.Keycloak.dll")
                check("reader-is-extracted-package-byte-for-byte", (root / "code/ForgeTrust.AppSurface.Auth.Keycloak.dll").read_bytes() == expected)
            if args.worker_anchor:
                closure = read_worker_payload(args.adapter_package)
                check("complete-worker-payload-is-extracted-candidate", all(
                    worker_payload_target(root / "code", name).read_bytes() == data for name, data in closure.items()))
            command = ["create", "--name", config["name"], "--label", "appsurface.feasibility=" + boundary.LABEL,
                       "--label", "appsurface.run=" + run_id, "--network", "none", "--ipc", "private", "--read-only",
                       "--init", "--interactive", "--restart", "no", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                       "--user", str(os.getuid()) + ":" + str(os.getgid()), "--pids-limit", "64", "--memory", "256m",
                       "--tmpfs", "/tmp:rw,mode=1777,size=16m", "--env", "NUGET_PACKAGES=/nuget", "--env", "HOME=/tmp/home"]
            for folder, target, writable in [("code", "/proof/code", False), ("state", "/proof/state", True), ("empty-nuget", "/nuget", False)]:
                command += ["--mount", "type=bind,source=" + str(root / folder) + ",target=" + target + ("" if writable else ",readonly")]
            if args.worker_anchor:
                command += ["--mount", "type=bind,source=" + config["privateInput"] + ",target=/run/appsurface/client-input.json,readonly"]
            arguments = ["/proof/code/" + args.assembly + ".dll"] + (["anchor"] if args.joined_members else [])
            if args.worker_anchor:
                arguments = ["/proof/code/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker.dll",
                             "--appsurface-keycloak-memory-anchor", "/run/appsurface/client-input.json", "/proof/state/client-reference.json"]
            identifier = docker.run(*command, "--entrypoint", "/usr/share/dotnet/dotnet", boundary.SDK, *arguments).stdout.strip()
            resources.append((config, identifier))
            boundary.durable_new(root / "created.json", {"containerId": identifier})
            item = boundary.inspect_profile(docker.inspect(identifier), config, identifier)
            check("secret-not-in-container-declaration", value not in json.dumps(item))
            if args.joined_members:
                summary = joined_readers(docker, config, root, command, value, check, args.evidence, resources, attachments, args.assembly)
            else:
                result = subprocess.run(docker.prefix + ["start", "--attach", "--interactive", identifier],
                                    input=json.dumps({"secret": value, "authority": "https://identity.example/realms/sample", "client": "sample-web"}),
                                    capture_output=True, text=True, timeout=25)
                check("secret-not-in-runtime-output", value not in result.stdout and value not in result.stderr)
                (args.evidence / "runtime.log").write_text(result.stdout + result.stderr)
                summary = json.loads(result.stdout)
                check("sealed-memory-probe-exits-zero", result.returncode == 0)
                check("worker-web-and-adversarial-reader-checks", summary["passed"] and summary["childrenStopped"] and summary["testCount"] >= 15)
        except Exception as error:
            fault = type(error).__name__ + ": " + str(error)[:160]
        finally:
            if identifier is not None:
                start = time.monotonic()
                deadline = start + 30
                try:
                    boundary.stop_owned(docker, config, identifier, deadline)
                    for attachment in attachments:
                        attachment.wait(timeout=boundary.remaining(deadline, 5))
                        stdout, stderr = attachment.communicate(timeout=boundary.remaining(deadline))
                        check("secret-not-in-anchor-runtime-output", value not in stdout and value not in stderr)
                        (args.evidence / "anchor.log").write_text(stdout + stderr)
                    boundary.verify_stopped(docker, config, identifier, deadline)
                    for member, member_id in resources[1:]:
                        boundary.verify_stopped(docker, member, member_id, deadline)
                    check("active-remains-through-independent-stop-check", (root / "active.json").exists())
                    check("all-container-readers-stopped", docker.inspect(identifier, deadline)["State"]["Pid"] == 0)
                    check("empty-cache-after-execution", not any((root / "empty-nuget").iterdir()))
                    marker = json.loads((root / "active.json").read_text())
                    check("exact-marker-owner-before-clear", marker["ownerId"] == run_id and marker["daemonId"] == docker.daemon(deadline))
                    observation = root / "stopped-observation.json"
                    boundary.durable_new(observation, {"config": config, "resources": resources,
                                                       "injectObserverFailure": args.inject_stop_observer_failure})
                    observed = subprocess.run([sys.executable, str(Path(__file__).resolve()), "--observe-stop", str(observation), str(deadline)],
                                              capture_output=True, text=True, timeout=boundary.remaining(deadline, 15))
                    if args.inject_stop_observer_failure:
                        observer_result = {**json.loads(observed.stdout), "exitCode": observed.returncode}
                        check("exact-injected-observer-failure", observer_result == {
                            "exitCode": 75, "code": "InjectedStoppedObservationUnavailable", "leaseHeld": True, "activeHeld": True})
                        retained_at_observer_failure = {
                            "active": json.loads((root / "active.json").read_text()) == marker,
                            "privateInput": Path(config["privateInput"]).is_file()}
                        check("active-and-private-input-retained-at-observer-failure", all(retained_at_observer_failure.values()))
                        boundary.durable_new(args.evidence / "observer-failure.json", {
                            "observer": observer_result, "retained": retained_at_observer_failure})
                    check("independent-stop-before-clean", observed.returncode == 0)
                    preclean = json.loads(observed.stdout)
                    check("lease-and-active-held-in-independent-observer", preclean["independentProcess"]
                          and preclean["leaseHeld"] and preclean["activeHeld"] and len(preclean["states"]) == len(resources))
                    boundary.durable_new(args.evidence / "preclean.json", preclean)
                    for member, member_id in reversed(resources):
                        docker.run("rm", member_id, deadline=deadline)
                        remaining_ids = docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + member["name"] + "$", deadline=deadline).stdout.strip()
                        check("exact-container-removed-before-clear:" + member["name"].removeprefix(config["name"]), not remaining_ids)
                    if config.get("privateInput"):
                        Path(config["privateInput"]).unlink()
                        boundary.sync_directory(root)
                        check("private-input-removed-after-stopped-observation", not Path(config["privateInput"]).exists())
                    boundary.remaining(deadline)
                    (root / "active.json").unlink()
                    try:
                        boundary.sync_directory(root)
                        if args.inject_clear_commit_failure:
                            raise TimeoutError("injected clear commit expiry")
                        boundary.remaining(deadline)
                        stop_seconds = time.monotonic() - start
                        check("whole-stop-clean-bound", stop_seconds < 30)
                    except BaseException:
                        boundary.durable_new(root / "active.json", marker)
                        raise
                except Exception as error:
                    fault = fault or type(error).__name__ + ": " + str(error)[:160]
                finally:
                    # SIGKILL only these locally created Docker attachment clients; it does not authorize an
                    # unverified container stop. Preserve active state when stop observation was unavailable.
                    for attachment in attachments:
                        if attachment.poll() is None:
                            try:
                                attachment.kill()
                            except ProcessLookupError:
                                pass
                            try:
                                attachment.communicate(timeout=boundary.remaining(deadline, 2))
                            except (subprocess.TimeoutExpired, TimeoutError):
                                try:
                                    attachment.wait(timeout=boundary.remaining(deadline, 1))
                                except (subprocess.TimeoutExpired, TimeoutError):
                                    fault = fault or "AttachmentReapUnavailable"
                    if fault is not None:
                        stop_seconds = time.monotonic() - start
    source_paths = ["run-sealed-memory.py", "run-supervised-boundary.py", "worker_payload.py", source_folder + "/Program.cs"]
    if args.assembly != "SealedMemoryProbe":
        source_paths += ["../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/KeycloakClientCredentialMemoryCapture.cs",
                         "../../Auth/ForgeTrust.AppSurface.Auth.Keycloak/KeycloakClientCredentialReference.cs"]
    if args.worker_anchor:
        source_paths += ["../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/realm-ready/Program.cs",
                         "../../Auth/ForgeTrust.AppSurface.Auth.Aspire.Keycloak/realm-ready/ForgeTrust.AppSurface.Auth.Aspire.Keycloak.RealmReadyWorker.csproj"]
    sources = {name: hashlib.sha256((Path(__file__).parent / name).read_bytes()).hexdigest() for name in source_paths}
    receipt = {"schema": 1, "kind": "private-linux-sealed-memory-primitive", "joinedMembers": args.joined_members,
               "workerAnchor": args.worker_anchor, "passed": fault is None, "fault": fault,
               "injectedStopObserverFailure": args.inject_stop_observer_failure, "runtimeRoot": str(root),
               "injectedClearCommitFailure": args.inject_clear_commit_failure,
               "observerResult": observer_result, "retainedAtObserverFailure": retained_at_observer_failure,
               "activeMarkerRetained": (root / "active.json").exists(),
               "assertions": checks, "probe": summary, "image": boundary.SDK, "imageId": config["imageId"],
               "daemonId": config["daemonId"], "stopSeconds": stop_seconds, "containerId": identifier,
               "executionSeconds": time.monotonic() - execution_started,
               "packageSha256": hashlib.sha256(args.package.read_bytes()).hexdigest() if args.package else None,
               "adapterPackageSha256": hashlib.sha256(args.adapter_package.read_bytes()).hexdigest() if args.adapter_package else None,
               "sourceHashes": sources, "noClaim": ["public API/platform acceptance", "actual OIDC exchange", "production credential store", "secure memory erasure"]
               + ([] if args.joined_members else ["cross-container reader access"])}
    boundary.durable_new(args.evidence / "receipt.json", receipt)
    if fault is None:
        shutil.rmtree(root)
    print(json.dumps({"passed": receipt["passed"], "fault": fault, "checks": len(checks), "stopSeconds": stop_seconds}))
    return 0 if fault is None else 1


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "--observe-stop":
        raise SystemExit(observe_stop(sys.argv[2], float(sys.argv[3])))
    raise SystemExit(main())
