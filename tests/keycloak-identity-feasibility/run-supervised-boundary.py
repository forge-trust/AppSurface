#!/usr/bin/env python3
"""Qualify a restricted container process boundary; never operate on a consumer volume."""

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid

SDK = "mcr.microsoft.com/dotnet/sdk@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b"
LABEL = "issue888-supervised"
CASES = ("success", "root-exit", "sigterm", "parent-crash", "create-crash", "start-crash", "inventory-failure", "restart-race", "clear-deadline", "clear-commit", "members", "member-root-exit", "member-parent-crash")
STOP_LIMIT = 30.0

ROOT_SCRIPT = """#!/bin/sh
set -eu
trap 'exit 143' TERM INT
setsid /bin/sh /proof/code/descendant.sh child </dev/null >/dev/null 2>&1 &
attempt=0
while [ ! -f /proof/state/child.heartbeat ] || [ ! -f /proof/state/grandchild.heartbeat ]; do
  attempt=$((attempt+1)); [ "$attempt" -lt 200 ] || exit 76; sleep 0.05
done
printf ready > /proof/state/root.ready
attempt=0
while [ ! -f /proof/state/root.release ]; do
  attempt=$((attempt+1)); [ "$attempt" -lt 600 ] || exit 77; sleep 0.1
done
if [ "$1" = root-exit ] || [ "$1" = member-root-exit ]; then exit 42; fi
exit 0
"""

DESCENDANT_SCRIPT = """#!/bin/sh
set -eu
trap '' TERM INT HUP
role="$1"
awk '{print $1, $4, $5, $6, $22}' /proc/$$/stat > /proof/state/"$role".identity
readlink /proc/$$/ns/pid > /proof/state/"$role".namespace
if [ "$role" = child ]; then
  setsid /bin/sh /proof/code/descendant.sh grandchild </dev/null >/dev/null 2>&1 &
fi
count=0
while [ "$count" -lt 600 ]; do
  count=$((count+1)); printf '%s' "$count" > /proof/state/"$role".heartbeat; sleep 0.1
done
exit 78
"""

MEMBER_SCRIPT = """#!/bin/sh
set -eu
trap '' TERM INT HUP
setsid /bin/sh /proof/code/descendant.sh "$1" </dev/null >/dev/null 2>&1 &
count=0
while [ "$count" -lt 600 ]; do count=$((count+1)); sleep 0.1; done
exit 79
"""


def has_members(config):
    return config["scenario"] in ("members", "member-root-exit", "member-parent-crash")


def member_configs(config, anchor_id):
    return [dict(config, name=config["name"] + "-" + role, role=role, anchorId=anchor_id)
            for role in ("worker", "web")]


def durable_new(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, sort_keys=True)
        stream.flush()
        os.fsync(stream.fileno())
    sync_directory(path.parent)


def sync_directory(path):
    descriptor = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def remaining(deadline, maximum=8):
    value = min(maximum, deadline - time.monotonic())
    if value <= 0:
        raise TimeoutError("shared stop deadline exhausted")
    return value


class Docker:
    def __init__(self, endpoint):
        expected = {"unix://" + str(Path.home() / ".docker/run/docker.sock"), "unix:///var/run/docker.sock"}
        if endpoint not in expected:
            raise ValueError("unqualified endpoint")
        self.prefix = ["docker", "--host", endpoint]

    def run(self, *arguments, deadline=None, allowed=(0,)):
        timeout = remaining(deadline) if deadline is not None else 10
        result = subprocess.run(self.prefix + list(arguments), stdin=subprocess.DEVNULL,
                                capture_output=True, text=True, timeout=timeout)
        if result.returncode not in allowed:
            raise RuntimeError("Docker operation unavailable: " + arguments[0])
        if len(result.stdout) > 1024 * 1024 or len(result.stderr) > 1024 * 1024:
            raise RuntimeError("Docker response exceeds bound")
        return result

    def daemon(self, deadline=None):
        return self.run("info", "--format", "{{.ID}}", deadline=deadline).stdout.strip()

    def inspect(self, name, deadline=None):
        return json.loads(self.run("inspect", name, deadline=deadline).stdout)[0]


def inspect_profile(item, config, identifier=None):
    host = item["HostConfig"]
    mounts = item["Mounts"]
    expected = {
        "/proof/code": (str(Path(config["root"]) / "code"), False),
        "/proof/state": (str(Path(config["root"]) / "state"), True),
        "/nuget": (str(Path(config["root"]) / "empty-nuget"), False),
    }
    if config.get("privateInput") is not None:
        if config["privateInput"] != str(Path(config["root"]) / "private-input.json"):
            raise RuntimeError("unqualified private input mount")
        expected["/run/appsurface/client-input.json"] = (config["privateInput"], False)
    anchor = config.get("anchorId")
    if anchor is not None and (len(anchor) != 64 or any(c not in "0123456789abcdef" for c in anchor)):
        raise RuntimeError("invalid namespace anchor")
    pid_mode_matches = host["PidMode"] == "container:" + anchor if anchor else host["PidMode"] in ("", "private")
    if (item["Name"] != "/" + config["name"] or identifier is not None and item["Id"] != identifier
            or item["Config"]["Labels"].get("appsurface.run") != config["runId"]
            or item["Config"]["Labels"].get("appsurface.feasibility") != LABEL
            or item["Image"] != config["imageId"] or item["Config"]["Image"] != SDK
            or host["Privileged"] or not pid_mode_matches
            or host["IpcMode"] not in ("private", "none") or host["NetworkMode"] != "none"
            or not host["ReadonlyRootfs"] or host["RestartPolicy"]["Name"] != "no"
            or host["AutoRemove"] or bool(host.get("Init")) != (anchor is None) or host["CapAdd"]
            or {value.upper() for value in host["CapDrop"] or []} != {"ALL"}
            or set(host["SecurityOpt"] or []) != {"no-new-privileges"}
            or host["PidsLimit"] != 64 or host["Memory"] != 256 * 1024 * 1024
            or host["Tmpfs"] != {"/tmp": "rw,mode=1777,size=16m"}
            or host["Devices"] or host.get("DeviceRequests")
            or item["Config"]["User"] != str(os.getuid()) + ":" + str(os.getgid())
            or len(mounts) != len(expected)):
        raise RuntimeError("container profile changed")
    for mount in mounts:
        if mount["Type"] != "bind" or (mount["Source"], mount["RW"]) != expected.get(mount["Destination"]):
            raise RuntimeError("unexpected mount")
    return item


def held_lock(root):
    stream = (root / "owner.lock").open("a+b")
    os.chmod(root / "owner.lock", 0o600)
    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
    return stream


def verify_stopped(docker, config, identifier, deadline):
    if docker.daemon(deadline) != config["daemonId"]:
        raise RuntimeError("daemon changed")
    by_name = docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + config["name"] + "$", deadline=deadline).stdout.strip()
    if by_name != identifier:
        raise RuntimeError("container name incarnation unavailable")
    item = inspect_profile(docker.inspect(identifier, deadline), config, identifier)
    state = item["State"]
    if state["Running"] or state["Paused"] or state["Restarting"] or state["Pid"] != 0 or state["Status"] not in ("exited", "created"):
        raise RuntimeError("boundary still live or uncertain")
    census = docker.run("top", identifier, deadline=deadline, allowed=(0, 1))
    if census.returncode != 1 or "is not running" not in census.stderr.lower() or census.stdout.strip():
        raise RuntimeError("stopped process census unavailable")
    return item


def stop_owned(docker, config, identifier, deadline):
    if docker.daemon(deadline) != config["daemonId"]:
        raise RuntimeError("daemon changed")
    item = inspect_profile(docker.inspect(identifier, deadline), config, identifier)
    if item["State"]["Running"]:
        docker.run("stop", "--time", "1", identifier, deadline=deadline)
    return verify_stopped(docker, config, identifier, deadline)


def verify_members_stopped(docker, config, anchor_id, deadline):
    members = []
    if not has_members(config):
        return members
    for member in member_configs(config, anchor_id):
        identifier = json.loads((Path(config["root"]) / (member["role"] + "-created.json")).read_text())["containerId"]
        while True:
            item = inspect_profile(docker.inspect(identifier, deadline), member, identifier)
            if not item["State"]["Running"]:
                break
            time.sleep(min(0.04, remaining(deadline)))
        members.append(verify_stopped(docker, member, identifier, deadline))
    return members


def clear_verified(root, docker, config, identifier, deadline, recovery=False):
    # The observer must independently accept stopped namespace state while the marker and lock remain held.
    durable_new(root / "preclean.ready", {"containerId": identifier, "recovery": recovery})
    while not (root / "preclean.release").exists():
        time.sleep(min(0.03, remaining(deadline)))
    if config["scenario"] == "restart-race" and not recovery:
        durable_new(root / "recheck.ready", {"pending": True})
        while not (root / "recheck.release").exists():
            time.sleep(min(0.03, remaining(deadline)))
    verify_stopped(docker, config, identifier, deadline)
    verify_members_stopped(docker, config, identifier, deadline)
    marker = json.loads((root / "active.json").read_text())
    if marker != {"ownerId": config["runId"], "daemonId": config["daemonId"], "name": config["name"]}:
        raise RuntimeError("active record changed")
    if config["scenario"] == "clear-deadline" and not recovery:
        raise TimeoutError("injected expiry before clear")
    remaining(deadline)
    (root / "active.json").unlink()
    try:
        sync_directory(root)
        if config["scenario"] == "clear-commit" and not recovery:
            raise TimeoutError("injected expiry during clear commit")
        remaining(deadline)
    except BaseException:
        # Under the same lease, restore active state before reporting an uncertain or over-bound clear.
        durable_new(root / "active.json", marker)
        raise


def controller(config_path, recovery=False, expected_owner=None):
    config = json.loads(config_path.read_text())
    root = Path(config["root"])
    docker = Docker(config["endpoint"])
    with held_lock(root):
        if recovery:
            marker = json.loads((root / "active.json").read_text())
            if marker["ownerId"] != expected_owner or expected_owner != config["runId"]:
                return 73
            if docker.daemon() != marker["daemonId"]:
                return 74
            identifier = inspect_profile(docker.inspect(config["name"]), config)["Id"]
            deadline = time.monotonic() + STOP_LIMIT
            try:
                stopped = verify_stopped(docker, config, identifier, deadline)
            except RuntimeError:
                return 74
            verify_members_stopped(docker, config, identifier, deadline)
            clear_verified(root, docker, config, identifier, deadline, recovery=True)
            durable_new(root / "recovery.json", {"kind": "RecoveryOnlyCompleted", "workloadsStarted": False,
                                                "cleared": True, "containerId": stopped["Id"], "stopSeconds": STOP_LIMIT - remaining(deadline, STOP_LIMIT)})
            return 0
        durable_new(root / "active.json", {"ownerId": config["runId"], "daemonId": config["daemonId"], "name": config["name"]})
        if docker.daemon() != config["daemonId"]:
            return 74
        create = ["create", "--name", config["name"], "--label", "appsurface.feasibility=" + LABEL,
                  "--label", "appsurface.run=" + config["runId"], "--network", "none", "--ipc", "private",
                  "--read-only", "--init", "--restart", "no", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                  "--user", str(os.getuid()) + ":" + str(os.getgid()), "--pids-limit", "64", "--memory", "256m",
                  "--tmpfs", "/tmp:rw,mode=1777,size=16m", "--env", "NUGET_PACKAGES=/nuget", "--env", "HOME=/tmp/home"]
        for source, target, writable in [(root / "code", "/proof/code", False), (root / "state", "/proof/state", True),
                                          (root / "empty-nuget", "/nuget", False)]:
            create += ["--mount", "type=bind,source=" + str(source) + ",target=" + target + ("" if writable else ",readonly")]
        identifier = docker.run(*create, "--entrypoint", "/bin/sh", SDK, "/proof/code/root.sh", config["scenario"]).stdout.strip()
        if config["scenario"] == "create-crash":
            os._exit(91)
        durable_new(root / "created.json", {"containerId": inspect_profile(docker.inspect(identifier), config, identifier)["Id"]})
        docker.run("start", identifier)
        if config["scenario"] == "start-crash":
            os._exit(92)
        if has_members(config):
            durable_new(root / "member-intents.json", {"anchorId": identifier, "names": [m["name"] for m in member_configs(config, identifier)]})
            for member in member_configs(config, identifier):
                member_create = list(create)
                member_create[member_create.index("--name") + 1] = member["name"]
                member_create.remove("--init")
                member_create += ["--pid", "container:" + identifier]
                member_id = docker.run(*member_create, "--entrypoint", "/bin/sh", SDK, "/proof/code/member.sh", member["role"]).stdout.strip()
                inspect_profile(docker.inspect(member_id), member, member_id)
                durable_new(root / (member["role"] + "-created.json"), {"containerId": member_id})
                docker.run("start", member_id)
        durable_new(root / "controller.ready", {"containerId": identifier})
        cancelled = False

        def cancel(_signum, _frame):
            nonlocal cancelled
            cancelled = True

        signal.signal(signal.SIGTERM, cancel)
        while not cancelled and not (root / "stop.request").exists():
            time.sleep(0.03)
        if config["scenario"] in ("parent-crash", "member-parent-crash"):
            os._exit(93)
        start = time.monotonic()
        deadline = start + STOP_LIMIT
        try:
            stop_owned(docker, config, identifier, deadline)
            verify_members_stopped(docker, config, identifier, deadline)
            if config["scenario"] == "inventory-failure":
                raise RuntimeError("injected inventory unavailable")
            clear_verified(root, docker, config, identifier, deadline)
            durable_new(root / "outcome.json", {"clean": True, "cancelled": cancelled,
                                               "stopSeconds": time.monotonic() - start})
            return 41 if cancelled or config["scenario"] in ("root-exit", "member-root-exit") else 0
        except (RuntimeError, TimeoutError):
            durable_new(root / "outcome.json", {"clean": False, "cancelled": cancelled,
                                               "stopSeconds": time.monotonic() - start})
            return 43


def await_file(path, process=None, timeout=20):
    deadline = time.monotonic() + timeout
    while not path.exists():
        if process is not None and process.poll() is not None:
            raise RuntimeError("controller exited before barrier: " + path.name)
        if time.monotonic() >= deadline:
            raise TimeoutError("barrier: " + path.name)
        time.sleep(0.04)


def observe_preclean(root, docker, config, identifier, check):
    check("marker-present-at-independent-observation", (root / "active.json").exists())
    with (root / "owner.lock").open("r+b") as contender:
        try:
            fcntl.flock(contender, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            check("lease-still-held-at-independent-observation", True)
        else:
            raise AssertionError("lease released before clean")
    verify_stopped(docker, config, identifier, time.monotonic() + 5)
    verify_members_stopped(docker, config, identifier, time.monotonic() + 5)
    heartbeats = {p.name: p.read_bytes() for p in (root / "state").glob("*.heartbeat")}
    time.sleep(0.3)
    check("detached-descendant-heartbeats-stopped", heartbeats == {p.name: p.read_bytes() for p in (root / "state").glob("*.heartbeat")})
    check("namespace-init-has-no-live-host-pid", docker.inspect(identifier)["State"]["Pid"] == 0)
    durable_new(root / "preclean.release", {"verified": True})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence-root", type=Path, required=True)
    parser.add_argument("--docker-endpoint", default="unix://" + str(Path.home() / ".docker/run/docker.sock"))
    parser.add_argument("--scenario", choices=CASES, action="append")
    args = parser.parse_args()
    os.umask(0o077)
    docker = Docker(args.docker_endpoint)
    daemon = docker.daemon()
    image = docker.inspect(SDK)
    if image["Os"] != "linux" or image["Architecture"] != "arm64":
        raise RuntimeError("unqualified pinned runtime")
    isolated = Path(tempfile.mkdtemp(prefix="i888-boundary-", dir="/private/tmp")).resolve()
    script = isolated / "controller.py"
    shutil.copyfile(__file__, script)
    args.evidence_root.mkdir(parents=True, exist_ok=True)
    evidence = Path(tempfile.mkdtemp(prefix="supervised-", dir=args.evidence_root)).resolve()
    results = []
    for scenario in args.scenario or CASES:
        root = isolated / scenario
        root.mkdir(mode=0o700)
        for folder in ("code", "state", "empty-nuget"):
            (root / folder).mkdir(mode=0o700)
        (root / "code/root.sh").write_text(ROOT_SCRIPT)
        (root / "code/descendant.sh").write_text(DESCENDANT_SCRIPT)
        (root / "code/member.sh").write_text(MEMBER_SCRIPT)
        run_id = uuid.uuid4().hex
        config = {"root": str(root), "runId": run_id, "name": "appsurface-issue888-supervised-" + run_id,
                  "scenario": scenario, "daemonId": daemon, "endpoint": args.docker_endpoint, "imageId": image["Id"]}
        config_path = root / "config.json"
        durable_new(config_path, config)
        checks = []

        def check(name, value):
            checks.append({"name": name, "passed": bool(value)})
            if not value:
                raise AssertionError(name)

        process = None
        identifier = None
        error = None
        maximum_stop = None
        host_stop_seconds = None
        host_clean = None
        case_evidence = evidence / scenario
        case_evidence.mkdir()
        try:
            with (root / "controller.log").open("w") as log:
                process = subprocess.Popen([sys.executable, str(script), "controller", str(config_path)],
                                           stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, cwd=isolated)
                if scenario in ("create-crash", "start-crash"):
                    process.wait(timeout=20)
                    check("crash-exit-nonzero", process.returncode in (91, 92))
                    check("launch-intent-retained-after-crash", (root / "active.json").exists())
                    identifier = inspect_profile(docker.inspect(config["name"]), config)["Id"]
                    check("container-name-resolves-id-not-durably-recorded", bool(identifier))
                else:
                    await_file(root / "controller.ready", process)
                    identifier = json.loads((root / "controller.ready").read_text())["containerId"]
                    await_file(root / "state/root.ready", process)
                    item = inspect_profile(docker.inspect(identifier), config, identifier)
                    check("restricted-live-boundary", item["State"]["Running"])
                    for bad_profile in ("pid", "privileged", "capabilities", "restart", "mount", "writable-root", "network", "seccomp", "pids-limit", "memory-limit", "tmpfs"):
                        rejected = json.loads(json.dumps(item))
                        if bad_profile == "pid": rejected["HostConfig"]["PidMode"] = "host"
                        elif bad_profile == "privileged": rejected["HostConfig"]["Privileged"] = True
                        elif bad_profile == "capabilities": rejected["HostConfig"]["CapAdd"] = ["SYS_ADMIN"]
                        elif bad_profile == "restart": rejected["HostConfig"]["RestartPolicy"]["Name"] = "always"
                        elif bad_profile == "mount": rejected["Mounts"][0]["Source"] = "/var/run/docker.sock"
                        elif bad_profile == "writable-root": rejected["HostConfig"]["ReadonlyRootfs"] = False
                        elif bad_profile == "network": rejected["HostConfig"]["NetworkMode"] = "host"
                        elif bad_profile == "seccomp": rejected["HostConfig"]["SecurityOpt"].append("seccomp=unconfined")
                        elif bad_profile == "pids-limit": rejected["HostConfig"]["PidsLimit"] = 0
                        elif bad_profile == "memory-limit": rejected["HostConfig"]["Memory"] = 0
                        elif bad_profile == "tmpfs": rejected["HostConfig"]["Tmpfs"]["/tmp"] = "rw"
                        try:
                            inspect_profile(rejected, config, identifier)
                        except RuntimeError:
                            check("unsafe-profile-rejected:" + bad_profile, True)
                        else:
                            raise AssertionError("unsafe profile accepted: " + bad_profile)
                    identities = {role: (root / ("state/" + role + ".identity")).read_text().split()
                                  for role in ("child", "grandchild")}
                    check("detached-descendants-in-distinct-sessions", identities["child"][3] != identities["grandchild"][3])
                    check("descendants-share-pid-namespace", (root / "state/child.namespace").read_text() == (root / "state/grandchild.namespace").read_text())
                    if has_members(config):
                        for member in member_configs(config, identifier):
                            await_file(root / ("state/" + member["role"] + ".heartbeat"), process)
                            member_id = json.loads((root / (member["role"] + "-created.json")).read_text())["containerId"]
                            check("member-live-in-exact-anchor:" + member["role"], inspect_profile(docker.inspect(member_id), member, member_id)["State"]["Running"])
                            check("member-descendant-shares-anchor-namespace:" + member["role"],
                                  (root / ("state/" + member["role"] + ".namespace")).read_text() == (root / "state/child.namespace").read_text())
                    if scenario in ("success", "root-exit", "member-root-exit"):
                        (root / "state/root.release").write_text("release")
                        end = time.monotonic() + 10
                        while docker.inspect(identifier)["State"]["Running"]:
                            remaining(end)
                            time.sleep(0.05)
                    if scenario == "sigterm":
                        process.send_signal(signal.SIGTERM)
                    else:
                        (root / "stop.request").write_text("stop")
                    if scenario not in ("parent-crash", "member-parent-crash", "inventory-failure"):
                        await_file(root / "preclean.ready", process)
                        observe_preclean(root, docker, config, identifier, check)
                        if scenario == "restart-race":
                            await_file(root / "recheck.ready", process)
                            docker.run("start", identifier)
                            check("explicit-restart-observed-before-final-recheck", docker.inspect(identifier)["State"]["Running"])
                            durable_new(root / "recheck.release", {"restarted": True})
                    process.wait(timeout=STOP_LIMIT)
                    check("bounded-parent-exit", process.returncode in (0, 41, 43, 93))
                uncertain = scenario in ("parent-crash", "member-parent-crash", "create-crash", "start-crash", "inventory-failure", "restart-race", "clear-deadline", "clear-commit")
                if uncertain:
                    check("uncertain-host-exits-nonzero", process.returncode != 0)
                    check("uncertain-stop-keeps-active", (root / "active.json").exists())
                    marker_before = (root / "active.json").read_bytes()
                    shutil.copyfile(root / "active.json", case_evidence / "active-at-host-exit.json")
                    if (root / "outcome.json").exists():
                        failed_outcome = json.loads((root / "outcome.json").read_text())
                        host_stop_seconds = failed_outcome["stopSeconds"]
                        host_clean = failed_outcome["clean"]
                        check("failed-host-stop-bounded-and-not-clean", host_stop_seconds < STOP_LIMIT and not host_clean)
                    wrong = subprocess.run([sys.executable, str(script), "recover", str(config_path), uuid.uuid4().hex],
                                           capture_output=True, timeout=5, cwd=isolated)
                    check("wrong-owner-recovery-rejected", wrong.returncode == 73 and (root / "active.json").read_bytes() == marker_before)
                    if docker.inspect(identifier)["State"]["Running"]:
                        live = subprocess.run([sys.executable, str(script), "recover", str(config_path), run_id],
                                              capture_output=True, timeout=10, cwd=isolated)
                        check("live-boundary-recovery-does-not-stop-or-clear", live.returncode == 74
                              and (root / "active.json").read_bytes() == marker_before
                              and docker.inspect(identifier)["State"]["Running"])
                    # Fixture cleanup is separate from recovery authority: stop only the validated task-owned container.
                    stop_owned(docker, config, identifier, time.monotonic() + STOP_LIMIT)
                    for barrier in ("preclean.ready", "preclean.release"):
                        (root / barrier).unlink(missing_ok=True)
                    recovery = subprocess.Popen([sys.executable, str(script), "recover", str(config_path), run_id],
                                                stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, cwd=isolated)
                    await_file(root / "preclean.ready", recovery)
                    observe_preclean(root, docker, config, identifier, check)
                    recovery.wait(timeout=STOP_LIMIT)
                    check("recovery-only-completed", recovery.returncode == 0 and json.loads((root / "recovery.json").read_text())["workloadsStarted"] is False)
                    maximum_stop = json.loads((root / "recovery.json").read_text())["stopSeconds"]
                else:
                    outcome = json.loads((root / "outcome.json").read_text())
                    host_clean = outcome["clean"]
                    host_stop_seconds = outcome["stopSeconds"]
                    check("clean-requires-independent-stopped-observation", outcome["clean"])
                    maximum_stop = outcome["stopSeconds"]
                    check("signal-or-required-failure-exits-nonzero", process.returncode == (41 if scenario in ("sigterm", "root-exit", "member-root-exit") else 0))
                check("whole-stop-bound", maximum_stop < STOP_LIMIT)
                check("active-cleared-after-observation", not (root / "active.json").exists())
                check("owner-lock-file-preserved", (root / "owner.lock").exists())
                check("empty-cache-remains-empty", not any((root / "empty-nuget").iterdir()))
                verify_stopped(docker, config, identifier, time.monotonic() + 5)
                for member in verify_members_stopped(docker, config, identifier, time.monotonic() + 5):
                    docker.run("rm", member["Id"])
                docker.run("rm", identifier)
                check("exact-owned-container-removed-after-stop", not docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + config["name"] + "$").stdout.strip())
        except Exception as failure:
            error = type(failure).__name__ + ": " + str(failure)[:240]
        finally:
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=5)
            # On failure preserve active state and stopped container records, rather than claiming recovery.
            if identifier is None and (root / "active.json").exists():
                try:
                    candidate = docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + config["name"] + "$").stdout.strip()
                    if candidate:
                        identifier = inspect_profile(docker.inspect(candidate), config, candidate)["Id"]
                except Exception:
                    pass
            if identifier is not None:
                try:
                    names = docker.run("ps", "-aq", "--no-trunc", "--filter", "name=^/" + config["name"] + "$").stdout.strip()
                    if names == identifier:
                        stop_owned(docker, config, identifier, time.monotonic() + STOP_LIMIT)
                except Exception:
                    pass
            for name in ("controller.log", "outcome.json", "recovery.json", "created.json", "preclean.ready", "preclean.release", "active.json", "member-intents.json", "worker-created.json", "web-created.json"):
                if (root / name).exists():
                    shutil.copyfile(root / name, case_evidence / name)
        result = {"scenario": scenario, "passed": error is None, "error": error, "assertions": checks,
                  "hostExitCode": process.returncode if process else None,
                  "hostStopSeconds": host_stop_seconds, "hostClean": host_clean,
                  "stopSeconds": maximum_stop, "containerId": identifier, "retainedRoot": str(root) if error else None}
        durable_new(case_evidence / "receipt.json", result)
        results.append(result)
        print(scenario + ": " + ("passed" if error is None else error), flush=True)
        if error:
            break
    receipt = {"schema": 1, "kind": "supervised-container-boundary-primitive", "passed": all(case["passed"] for case in results),
               "image": SDK, "imageId": image["Id"], "daemonId": daemon, "stopBoundSeconds": STOP_LIMIT,
               "sourceSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "cases": results,
               "noClaim": ["Aspire integration", "credential transport across container boundary", "packaged worker closure", "browser acceptance", "production API acceptance"]}
    durable_new(evidence / "receipt.json", receipt)
    if receipt["passed"]:
        shutil.rmtree(isolated)
    print("Evidence: " + str(evidence), flush=True)
    return 0 if receipt["passed"] else 1


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "controller":
        raise SystemExit(controller(Path(sys.argv[2])))
    if len(sys.argv) == 4 and sys.argv[1] == "recover":
        raise SystemExit(controller(Path(sys.argv[2]), recovery=True, expected_owner=sys.argv[3]))
    raise SystemExit(main())
