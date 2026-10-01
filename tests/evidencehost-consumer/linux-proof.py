"""Prove the provisional Linux consumer mechanism; never grant Trusted admission.

Must run as root on a disposable systemd/cgroup-v2 VM with a compiled Worker.dll.
All paths, users and units are fresh. An unsupported mechanism is a failure.
"""
import argparse
import json
import os
import pwd
import re
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
from pathlib import Path


class ProofFailure(RuntimeError):
    pass


def verify_subject_results(denials):
    expected = {"allowed-input", "cgroup-escape", "no-new-privileges", "output-write",
                "registration-read", "supervisor-read", "verifier-read", "worker-environment-read",
                "worker-fd-read", "worker-ptrace", "worker-signal", "zero-secret-projection"}
    if not isinstance(denials, dict) or set(denials) != expected:
        raise ProofFailure("subject-boundary-incomplete")
    if any(value is not True for value in denials.values()):
        raise ProofFailure("subject-boundary-failed")


def command(argv, *, acceptable=(0,), timeout=20):
    result = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, check=False,
                            env={"PATH": "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
                                 "LANG": "C.UTF-8"})
    if len(result.stdout) + len(result.stderr) > 256 * 1024:
        raise ProofFailure("fixture-control-output-limit")
    if result.returncode not in acceptable:
        # Raw child diagnostics are not authority and may contain supplied values.
        raise ProofFailure(f"control-command-failed:{Path(argv[0]).name}:{result.returncode}")
    return result


def properties(unit):
    keys = ("LoadState", "ActiveState", "SubState", "Result", "MainPID", "ControlGroup",
            "User", "Group", "KillMode", "RuntimeMaxUSec", "TimeoutStopUSec", "SendSIGKILL",
            "NoNewPrivileges", "CapabilityBoundingSet", "AmbientCapabilities", "ProtectControlGroups")
    output = command(["systemctl", "show", unit, "--no-pager", *[f"--property={key}" for key in keys]])
    return dict(line.split("=", 1) for line in output.stdout.splitlines() if "=" in line)


def group_empty(group):
    if not group.startswith("/system.slice/") or ".." in group:
        raise ProofFailure("unexpected-control-group")
    path = Path("/sys/fs/cgroup") / group.lstrip("/")
    if not path.exists():
        return True
    events = dict(line.split() for line in (path / "cgroup.events").read_text().splitlines())
    return events.get("populated") == "0"


def wait_entered(output, unit, deadline):
    while time.monotonic() < deadline:
        if (output / "entered").is_file():
            return properties(unit)
        state = properties(unit)
        if state.get("ActiveState") == "failed":
            raise ProofFailure("worker-failed-before-callback")
        time.sleep(0.05)
    raise ProofFailure("worker-entry-timeout")


def wait_stopped(unit, group, deadline):
    while time.monotonic() < deadline:
        state = properties(unit)
        if state.get("ActiveState") in ("failed", "inactive") and group_empty(group):
            return state
        time.sleep(0.05)
    raise ProofFailure("owned-exit-not-established")


def service(unit, user, argv, *, lifetime=4, writable=None, retain=False):
    # RuntimeMaxSec and TimeoutStopSec are armed by PID 1 before the first callback.
    settings = {
        "User": user, "Group": user, "Type": "exec", "KillMode": "control-group",
        "RuntimeMaxSec": str(lifetime), "TimeoutStopSec": "1", "SendSIGKILL": "yes",
        "NoNewPrivileges": "yes", "CapabilityBoundingSet": "", "AmbientCapabilities": "",
        "ProtectControlGroups": "yes", "RestrictSUIDSGID": "yes", "PrivateTmp": "yes",
        "ProtectSystem": "strict", "ProtectHome": "yes", "LimitCORE": "0",
        "TasksMax": "32", "MemoryMax": "256M", "Restart": "no",
    }
    if writable is not None:
        settings["ReadWritePaths"] = str(writable)
    if retain:
        settings["RemainAfterExit"] = "yes"
    return ["systemd-run", "--quiet", f"--unit={unit}", "--expand-environment=no",
            *[f"--property={key}={value}" for key, value in settings.items()],
            *argv]


def run(args):
    if sys.platform != "linux" or os.geteuid() != 0:
        raise ProofFailure("requires-root-on-disposable-linux-vm")
    if Path("/proc/1/comm").read_text().strip() != "systemd":
        raise ProofFailure("requires-independent-systemd-manager")
    if not Path("/sys/fs/cgroup/cgroup.controllers").is_file():
        raise ProofFailure("requires-cgroup-v2")
    if not args.worker.is_file() or args.worker.name != "Worker.dll":
        raise ProofFailure("requires-built-worker")
    version = command(["systemctl", "--version"]).stdout.splitlines()[0]
    if not re.match(r"systemd (25[5-9]|2[6-9][0-9]|[3-9][0-9]{2})\b", version):
        raise ProofFailure("requires-systemd-255-or-newer")

    tag = uuid.uuid4().hex[:12]
    # PrivateTmp replaces /tmp and /var/tmp in each service. Use a root-owned /run anchor instead.
    root = Path(tempfile.mkdtemp(prefix=f"evidence779-{tag}-", dir="/run"))
    root.chmod(0o755)
    users = []
    units = []
    report = {"schema": "issue779-linux-mechanism-proof-v1", "admission": "none",
              "status": "failed", "systemd": version, "kernel": os.uname().release,
              "machine": os.uname().machine, "scenarios": {}}
    try:
        for role in ("worker", "subject"):
            name = f"ev779{role[0]}{tag}"
            command(["useradd", "--system", "--user-group", "--no-create-home",
                     "--shell", "/usr/sbin/nologin", name])
            users.append(name)
        worker_user, subject_user = users
        worker_uid = pwd.getpwnam(worker_user).pw_uid
        worker_gid = pwd.getpwnam(worker_user).pw_gid
        subject_uid = pwd.getpwnam(subject_user).pw_uid
        subject_gid = pwd.getpwnam(subject_user).pw_gid
        if worker_uid == subject_uid or 0 in (worker_uid, subject_uid):
            raise ProofFailure("identity-separation-failed")
        tool = root / "protected-tool"
        tool.mkdir(mode=0o750)
        for source in args.worker.parent.iterdir():
            if source.is_file():
                shutil.copyfile(source, tool / source.name)
        for name in ("registration.bin", "verifier.bin"):
            (tool / name).write_text("protected-base-fixture\n")
        for path in tool.iterdir():
            os.chown(path, 0, worker_gid)
            path.chmod(0o440)
        os.chown(tool, 0, worker_gid)
        supervisor = root / "supervisor"
        supervisor.mkdir(mode=0o700)
        (supervisor / "control").write_text("protected-control\n")
        subject = root / "subject"
        subject.mkdir(mode=0o700)
        os.chown(subject, subject_uid, subject_gid)
        shutil.copyfile(Path(__file__).with_name("subject.py"), subject / "subject.py")
        allowed = subject / "allowed.txt"
        allowed.write_text("declared-input\n")
        for path in subject.iterdir():
            os.chown(path, subject_uid, subject_gid)
            path.chmod(0o400)

        for scenario in ("success", "cooperative", "configure-stall", "verifier-stall",
                         "factory-stall", "producer-stall", "dispose-stall", "fatal", "descendant"):
            output = root / scenario
            output.mkdir(mode=0o700)
            os.chown(output, worker_uid, worker_gid)
            unit = f"evidence779-{tag}-{scenario}.service"
            units.append(unit)
            argv = service(unit, worker_user,
                           ["/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent",
                            "LANG=C.UTF-8", str(args.dotnet), str(tool / "Worker.dll"), scenario, str(output)],
                           writable=output, retain=scenario == "success")
            started = time.monotonic()
            command(argv)
            initial = wait_entered(output, unit, started + 3)
            group = initial.get("ControlGroup", "")
            if (initial.get("User") != worker_user or initial.get("Group") != worker_user
                    or initial.get("KillMode") != "control-group"):
                raise ProofFailure("worker-supervisor-binding-failed")
            if (initial.get("NoNewPrivileges") != "yes" or initial.get("RuntimeMaxUSec") != "4s"
                    or initial.get("TimeoutStopUSec") != "1s" or initial.get("SendSIGKILL") != "yes"):
                raise ProofFailure("worker-watchdog-not-armed")
            if (initial.get("CapabilityBoundingSet") != "" or initial.get("AmbientCapabilities") != ""
                    or initial.get("ProtectControlGroups") != "yes"):
                raise ProofFailure("worker-cgroup-controls-not-enforced")
            if not group or group_empty(group):
                raise ProofFailure("worker-control-group-not-active")
            (output / "activate").write_text("released-by-launcher\n")

            if scenario == "cooperative":
                worker_pid = initial.get("MainPID", "0")
                if int(worker_pid) <= 0:
                    raise ProofFailure("missing-worker-identity")
                subject_unit = f"evidence779-{tag}-subject.service"
                units.append(subject_unit)
                subject_argv = service(subject_unit, subject_user,
                                       ["/usr/bin/env", "-i", "PATH=/usr/bin:/bin", "HOME=/nonexistent",
                                        "LANG=C.UTF-8", "/usr/bin/python3", str(subject / "subject.py"),
                                        str(tool), str(output), str(allowed), worker_pid,
                                        str(supervisor / "control")], lifetime=2)
                subject_argv[2:2] = ["--wait", "--pipe"]
                hostile = command(subject_argv, timeout=6)
                denials = json.loads(hostile.stdout)
                verify_subject_results(denials)
                report["scenarios"]["subject-boundary"] = denials
                command(["systemctl", "stop", unit], timeout=4)
            elif scenario == "success":
                # Keep the successful transient service loaded long enough to inspect its result.
                while time.monotonic() < started + 3:
                    state = properties(unit)
                    if state.get("SubState") == "exited" and group_empty(group):
                        break
                    time.sleep(0.05)
                else:
                    raise ProofFailure("positive-worker-did-not-exit")

            if scenario != "success":
                state = wait_stopped(unit, group, started + 10)
            observed = {path.name for path in output.iterdir()}
            if "callback-entered" not in observed:
                raise ProofFailure("worker-callback-not-exercised")
            if scenario == "success":
                if not {"entered", "disposed", "manifest", "unwound"}.issubset(observed):
                    raise ProofFailure("positive-finalization-missing")
                if state.get("Result") != "success":
                    raise ProofFailure("positive-worker-unsuccessful")
            elif scenario == "cooperative":
                if not {"joined", "disposed", "failure-manifest", "unwound"}.issubset(observed):
                    raise ProofFailure("cooperative-join-missing")
                if "manifest" in observed or state.get("Result") == "success":
                    raise ProofFailure("cancelled-worker-promoted")
            else:
                if {"manifest", "failure-manifest", "disposed", "unwound", "pumps-joined"} & observed:
                    raise ProofFailure("fatal-worker-unwound-or-published")
                if state.get("Result") in (None, "success"):
                    raise ProofFailure("fatal-worker-successful")
                if scenario == "dispose-stall" and not {"joined", "dispose-entered"}.issubset(observed):
                    raise ProofFailure("stuck-disposer-not-exercised")
                if scenario == "descendant":
                    child_pid = int((output / "child-pid").read_text())
                    if Path(f"/proc/{child_pid}").exists():
                        raise ProofFailure("owned-descendant-still-exists")
            report["scenarios"][scenario] = {"result": state.get("Result"), "markers": sorted(observed),
                                                "ownedGroupEmpty": group_empty(group),
                                                "elapsedSeconds": round(time.monotonic() - started, 3)}
        report["status"] = "passed-mechanism-only"
        return report
    finally:
        cleanup_failed = False
        for unit in reversed(units):
            try:
                previous = properties(unit)
                command(["systemctl", "stop", unit], acceptable=(0, 5), timeout=5)
                group = previous.get("ControlGroup", "")
                if group and not group_empty(group):
                    cleanup_failed = True
                command(["systemctl", "reset-failed", unit], acceptable=(0, 1, 5), timeout=5)
            except (ProofFailure, subprocess.TimeoutExpired, OSError):
                cleanup_failed = True
        if cleanup_failed:
            # Do not remove identities/output while writers might still be active.
            raise ProofFailure("fixture-teardown-failed")
        for user in reversed(users):
            command(["userdel", user], timeout=5)
        # This fresh fixture directory contains no production evidence or secrets.
        shutil.rmtree(root)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", required=True, type=Path)
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()
    try:
        report = run(args)
    except (ProofFailure, subprocess.TimeoutExpired, OSError, ValueError) as error:
        report = {"schema": "issue779-linux-mechanism-proof-v1", "status": "failed",
                  "admission": "none", "diagnostic": str(error) if isinstance(error, ProofFailure)
                  else type(error).__name__}
    # report is a fixture observation, never a gate manifest or a lease.
    args.report.write_text(json.dumps(report, indent=2, sort_keys=True) + "\n")
    return 0 if report["status"] == "passed-mechanism-only" else 1


if __name__ == "__main__":
    sys.exit(main())
