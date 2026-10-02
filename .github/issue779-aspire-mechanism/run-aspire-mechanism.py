#!/usr/bin/env python3
"""Private native preparation: compile pinned fixtures, run five root-owned mechanism controls, never issue Trust."""
import argparse
import hashlib
import json
import os
import platform
import pwd
import re
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import threading
import time
import uuid
from pathlib import Path

CASES = ("normal", "readiness-failure", "factory-stall", "cancel", "stuck-descendant")
PREFIX = "tests/evidencehost-consumer/"
REQUIRED = (
    "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
    PREFIX + "aspire-child-proof.py", PREFIX + "test_aspire_child_proof.py",
    PREFIX + "AspireChild/Program.cs", PREFIX + "AspireChild/AspireChild.csproj",
    PREFIX + "AspireChild/packages.linux-x64.lock.json", PREFIX + "AspireChild/README.md",
    PREFIX + "NativeHttpResource/Program.cs", PREFIX + "NativeHttpResource/NativeHttpResource.csproj",
    PREFIX + "NativeHttpResource/packages.lock.json", PREFIX + "NativeHttpResource/README.md",
)
FIXES = {"watchdog-ready-ack-and-liveness", "pump-eof-and-failure",
         "main-only-cooperative-term-and-zero-exit", "pinned-store-path-after-builder",
         "pinned-dcp-publisher-options", "bounded-startup-identity-rejection",
         "exec-startup-and-budget-diagnostics"}
MARKERS = ("CODEX_SANDBOX", "SANDBOX_MODE", "IN_SANDBOX", "IS_SANDBOX")


def sha(path):
    value = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(65536):
            value.update(block)
    return value.hexdigest()


def write_json(path, value):
    with path.open("x", encoding="utf-8") as stream:
        os.fchmod(stream.fileno(), 0o600)
        json.dump(value, stream, indent=2, sort_keys=True)
        stream.write("\n")


def capture(argv, directory, label, env=None, timeout=300, maximum=4 * 1024 * 1024):
    """Count both streams and latch pump/overflow failures; retained logs have an aggregate bound."""
    state = {"received": 0, "kept": 0, "overflow": False, "failed": False, "eof": 0}
    lock = threading.Lock()
    def pump(stream, path):
        try:
            with path.open("xb") as log:
                os.fchmod(log.fileno(), 0o600)
                while block := stream.read(4096):
                    with lock:
                        state["received"] += len(block)
                        state["overflow"] |= state["received"] > maximum
                        kept = block[:max(0, maximum - state["kept"])]
                        state["kept"] += len(kept)
                    log.write(kept)
            with lock:
                state["eof"] += 1
        except (OSError, ValueError):
            with lock:
                state["failed"] = True
    process = subprocess.Popen(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.PIPE, env=env, start_new_session=True)
    threads = [threading.Thread(target=pump, args=(stream, directory / f"{label}.{name}.log"), daemon=True)
               for name, stream in (("stdout", process.stdout), ("stderr", process.stderr))]
    for thread in threads:
        thread.start()
    timed_out = False
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        timed_out = True
        os.killpg(process.pid, signal.SIGKILL)
        process.wait(timeout=5)
    for thread in threads:
        thread.join(timeout=5)
    record = {"label": label, "argv": argv, "pid": process.pid, "exit_code": process.returncode,
              "timed_out": timed_out, **state}
    record["streams_joined"] = all(not thread.is_alive() for thread in threads) and state["eof"] == 2
    record["capture_ok"] = record["streams_joined"] and not state["failed"] and not state["overflow"] and not timed_out
    write_json(directory / f"{label}.command.json", record)
    return record


def checked(argv, timeout=10):
    return subprocess.check_output(argv, timeout=timeout, stderr=subprocess.DEVNULL, text=True).strip()


def validate_binding(source, binding):
    if binding.get("schema") != "issue779-aspire-mechanism-source-v1" or not re.fullmatch("[0-9a-f]{40}", binding.get("source_commit", "")):
        raise ValueError("source-binding-not-final")
    if binding.get("controller_fixes_verified") is not True or set(binding.get("required_fix_receipts", [])) != FIXES:
        raise ValueError("controller-fixes-not-verified")
    if checked(["git", "-C", str(source), "rev-parse", "HEAD"]) != binding["source_commit"]:
        raise ValueError("source-commit-mismatch")
    hashes = binding.get("source_sha256", {})
    if not set(REQUIRED).issubset(hashes):
        raise ValueError("required-source-binding-missing")
    for name, expected in hashes.items():
        path = Path(name)
        if path.is_absolute() or ".." in path.parts or not re.fullmatch("[0-9a-f]{64}", expected):
            raise ValueError("invalid-source-binding")
        actual = source / path
        if actual.is_symlink() or not actual.is_file() or sha(actual) != expected:
            raise ValueError("source-content-mismatch")


def native_metadata():
    if sys.platform != "linux" or platform.machine().lower() not in ("x86_64", "amd64"):
        raise ValueError("native-linux-x64-required")
    if Path("/proc/1/comm").read_text().strip() != "systemd" or not Path("/sys/fs/cgroup/cgroup.controllers").is_file():
        raise ValueError("systemd-cgroup-v2-required")
    release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines() if "=" in line)
    if release.get("ID", "").strip('"') != "ubuntu" or release.get("VERSION_ID", "").strip('"') != "24.04":
        raise ValueError("ubuntu-24.04-required")
    systemd = checked(["systemctl", "--version"]).splitlines()[0]
    version = re.match(r"systemd (\d+)", systemd)
    if not version or int(version[1]) < 255:
        raise ValueError("systemd-255-required")
    return {"kernel": platform.release(), "architecture": platform.machine(),
            "systemd": systemd, "os": "ubuntu", "os_version": "24.04"}


def runtime_path_facts(selected):
    """Resolve the host and every selected-path ancestor without weakening ProtectHome."""
    original = selected.absolute()
    links = []
    current = Path(original.anchor)
    for component in original.parts[1:]:
        current /= component
        current.lstat()
        if current.is_symlink():
            links.append({"path": str(current), "target": os.readlink(current)})
        resolved = current.resolve(strict=True)
        if any(resolved.is_relative_to(Path(root)) for root in ("/home", "/root", "/run/user")):
            raise ValueError("dotnet-path-hidden-by-ProtectHome")
    canonical = original.resolve(strict=True)
    if not canonical.is_file() or not canonical.stat().st_mode & 0o111:
        raise ValueError("canonical-dotnet-not-executable")
    return canonical, {"selected_path": str(original), "canonical_path": str(canonical), "ancestor_symlinks": links}


def run(args):
    source = args.source.resolve(strict=True)
    binding = json.loads(args.binding.read_text())
    validate_binding(source, binding)
    output = args.output.resolve(strict=False)
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    write_json(output / "source-binding.json", binding)
    metadata = native_metadata()
    selected_dotnet = shutil.which("dotnet")
    if not selected_dotnet:
        raise ValueError("dotnet-host-required")
    dotnet, runtime_facts = runtime_path_facts(Path(selected_dotnet))
    cache = output / "build-state"
    cache.mkdir(mode=0o700)
    env = {key: os.environ[key] for key in ("PATH", "HOME", "LANG", *MARKERS) if key in os.environ}
    env.update({"DOTNET_CLI_HOME": str(cache / "dotnet-home"), "NUGET_PACKAGES": str(cache / "nuget"),
                "NUGET_HTTP_CACHE_PATH": str(cache / "http-cache"), "DOTNET_NOLOGO": "1",
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1"})
    project = source / PREFIX / "AspireChild/AspireChild.csproj"
    commands = []
    for label, argv in (
        ("locked-restore", [str(dotnet), "restore", str(project), "--locked-mode"]),
        ("native-build", [str(dotnet), "build", str(project), "--no-restore", "--configuration", "Debug",
                          "--verbosity", "minimal", "-p:UseSharedCompilation=false"]),
    ):
        result = capture(argv, output, label, env=env)
        commands.append(result)
        raw = b"".join((output / f"{label}.{name}.log").read_bytes() for name in ("stdout", "stderr"))
        warnings = re.findall(rb"\bwarning\s+[A-Za-z]+\d+\b", raw, re.IGNORECASE)
        result["warning_diagnostics"] = len(warnings)
        if result["exit_code"] != 0 or not result["capture_ok"] or warnings:
            write_json(output / "terminal.json", {"trust_claim": False, "phase": label, "passed": False, "commands": commands})
            return 1
        if label == "native-build" and (b"0 Warning(s)" not in raw or b"0 Error(s)" not in raw):
            raise ValueError("zero-warning-build-summary-missing")
    validate_binding(source, binding)
    bundle = project.parent / "bin/Debug/net10.0"
    dcp = bundle / "dcp/dcp"
    with dcp.open("rb") as stream:
        header = stream.read(20)
    if header[:6] != b"\x7fELF\x02\x01" or header[18:20] != b"\x3e\x00" or not dcp.stat().st_mode & 0o111:
        raise ValueError("actual-linux-x64-dcp-required")
    lock = json.loads((project.parent / "packages.linux-x64.lock.json").read_text())
    package_graph = lock["dependencies"]["net10.0"]
    if package_graph["Aspire.Hosting.AppHost"]["resolved"] != "13.4.4" or package_graph["Aspire.Hosting.Orchestration.linux-x64"]["resolved"] != "13.4.4":
        raise ValueError("pinned-sdk-dcp-version-mismatch")
    metadata.update({"source_commit": binding["source_commit"], "dotnet_path": str(dotnet),
                     "runtime_path_facts": runtime_facts, "dcp_executable_mode": oct(stat.S_IMODE(dcp.stat().st_mode)),
                     "dotnet_sdk": checked([str(dotnet), "--version"]), "aspire_version": "13.4.4",
                     "dcp_package": package_graph["Aspire.Hosting.Orchestration.linux-x64"],
                     "dcp_sha256": sha(dcp), "managed_tool_sha256": sha(bundle / "resource/NativeHttpResource.dll"),
                     "linux_lock_sha256": sha(project.parent / "packages.linux-x64.lock.json"),
                     "run_id": os.environ.get("MECHANISM_RUN_ID", "local-candidate"),
                     "workflow_ref": os.environ.get("MECHANISM_WORKFLOW_REF", "local-candidate"),
                     "workflow_sha": os.environ.get("MECHANISM_WORKFLOW_SHA", "local-candidate"),
                     "trust_claim": False, "shared_admission": False})
    write_json(output / "native-metadata.json", metadata)
    work = Path("/var/tmp") / ("issue779-aspire-prep-" + uuid.uuid4().hex)
    root_args = ["sudo", "-n", "--", "/usr/bin/env", "-i", "PATH=/usr/sbin:/usr/bin:/sbin:/bin",
                 "/usr/bin/python3", "-B", str(Path(__file__).resolve()), "root-cases",
                 "--bundle", str(bundle), "--controller", str(source / PREFIX / "aspire-child-proof.py"),
                 "--controller-sha", binding["source_sha256"][PREFIX + "aspire-child-proof.py"],
                 "--dotnet", str(dotnet), "--work", str(work)]
    root_result = capture(root_args, output, "root-cases", timeout=500, maximum=512 * 1024)
    # Diagnostics remain root-only until copied as a bounded private archive. No arbitrary root path is accepted.
    archive = work / "private-root-logs.tar.gz"
    destination = output / "private-root-logs.tar.gz"
    with destination.open("xb") as sink:
        os.fchmod(sink.fileno(), 0o600)
        process = subprocess.Popen(["sudo", "-n", "--", "/bin/cat", str(archive)], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        total = 0
        try:
            while block := process.stdout.read(65536):
                total += len(block)
                if total > 16 * 1024 * 1024:
                    raise ValueError("private-archive-byte-limit")
                sink.write(block)
            if process.wait(timeout=10) != 0:
                raise ValueError("private-archive-unavailable")
        finally:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=5)
    root_summary = json.loads((output / "root-cases.stdout.log").read_bytes())
    write_json(output / "case-summary.json", root_summary)
    validate_binding(source, binding)
    passed = root_result["exit_code"] == 0 and root_result["capture_ok"] and root_summary.get("passed") is True
    write_json(output / "terminal.json", {"trust_claim": False, "shared_admission": False, "passed": passed,
                                          "commands": commands, "root_command": root_result,
                                          "private_archive_sha256": sha(destination)})
    return 0 if passed else 1


def group_empty(unit):
    group = checked(["systemctl", "show", unit, "--property=ControlGroup", "--value"])
    if not group:
        return True
    if not group.startswith("/system.slice/issue779-child-") or ".." in group:
        raise ValueError("unexpected-case-cgroup")
    path = Path("/sys/fs/cgroup") / group.lstrip("/")
    return not path.exists() or all(not entry.read_text().strip() for entry in path.rglob("cgroup.procs"))



def control_log_allowed(name, info):
    limits = {"stdout.log": 1024 * 1024, "stderr.log": 1024 * 1024,
              "receipt.json": 1024 * 1024, "quarantine": 1024 * 1024,
              "identity-rejection.json": 4096, "budget-diagnostic.json": 4096}
    return (name in limits and stat.S_ISREG(info.st_mode) and info.st_uid == 0
            and info.st_nlink == 1 and 0 <= info.st_size <= limits[name]
            and (name not in ("identity-rejection.json", "budget-diagnostic.json") or stat.S_IMODE(info.st_mode) == 0o600))


def control_log_identity(info):
    return (info.st_dev, info.st_ino, info.st_uid, info.st_mode, info.st_nlink,
            info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def retain_control_logs(control, logs, case):
    """Retain only fixed root-owned files; both fixed diagnostics are private, 0600 and <=4096 bytes."""
    valid, identity_retained, budget_retained = True, False, False
    for name in ("stdout.log", "stderr.log", "receipt.json", "quarantine", "identity-rejection.json", "budget-diagnostic.json"):
        path = control / name
        target = logs / f"{case}.control-{name}"
        created = False
        fd = None
        try:
            try:
                before = path.lstat()
            except FileNotFoundError:
                continue
            if not control_log_allowed(name, before):
                valid = False
                continue
            fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
            if control_log_identity(os.fstat(fd)) != control_log_identity(before):
                valid = False
                continue
            with os.fdopen(fd, "rb", closefd=False) as stream:
                data = stream.read(before.st_size + 1)
            if (len(data) != before.st_size
                    or control_log_identity(os.fstat(fd)) != control_log_identity(before)
                    or control_log_identity(path.lstat()) != control_log_identity(before)):
                valid = False
                continue
            with target.open("xb") as sink:
                created = True
                os.fchmod(sink.fileno(), 0o600)
                sink.write(data)
            identity_retained |= name == "identity-rejection.json"
            budget_retained |= name == "budget-diagnostic.json"
        except (OSError, ValueError):
            valid = False
            if created:
                target.unlink(missing_ok=True)
        finally:
            if fd is not None:
                os.close(fd)
    return valid, identity_retained, budget_retained


def root_cases(args):
    if os.geteuid() != 0:
        raise ValueError("root-controller-required")
    native_metadata()
    canonical_dotnet, _ = runtime_path_facts(args.dotnet)
    if canonical_dotnet != args.dotnet:
        raise ValueError("root-controller-dotnet-must-be-canonical")
    if args.work.parent != Path("/var/tmp") or not re.fullmatch("issue779-aspire-prep-[0-9a-f]{32}", args.work.name):
        raise ValueError("fresh-root-work-required")
    args.work.mkdir(mode=0o700, exist_ok=False)
    if sha(args.controller) != args.controller_sha:
        raise ValueError("controller-source-mismatch")
    controller = args.work / "controller.py"
    shutil.copyfile(args.controller, controller)
    controller.chmod(0o400)
    if any(path.is_symlink() for path in args.bundle.rglob("*")):
        raise ValueError("bundle-links-forbidden")
    bundle = args.work / "bundle"
    shutil.copytree(args.bundle, bundle)
    account = "evac" + uuid.uuid4().hex[:12]
    subprocess.run(["useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", account], check=True, timeout=10)
    user = pwd.getpwnam(account)
    logs = args.work / "logs"
    logs.mkdir(mode=0o700)
    summaries = []
    safe_exit = True
    try:
        for case in CASES:
            # A failure before inspection must never inherit the previous case's exit confirmation.
            safe_exit = False
            case_root = args.work / case
            case_root.mkdir(mode=0o700)
            tools, output = case_root / "tools", case_root / "output"
            tools.mkdir(mode=0o755)
            output.mkdir(mode=0o755)
            tool = tools / "protected-tool.dll"
            shutil.copyfile(bundle / "resource/NativeHttpResource.dll", tool)
            tool.chmod(0o444)
            before = set(Path("/run").glob("issue779-child-*"))
            result = capture(["/usr/bin/timeout", "--signal=TERM", "--kill-after=5s", "75s",
                              "/usr/bin/python3", "-B", str(controller), "--bundle", str(bundle),
                              "--dotnet", str(args.dotnet), "--subject-uid", str(user.pw_uid),
                              "--subject-gid", str(user.pw_gid), "--protected-tools", str(tools),
                              "--protected-output", str(output), "--case", case], logs, case,
                             timeout=85, maximum=256 * 1024)
            created = set(Path("/run").glob("issue779-child-*")) - before
            try:
                receipt = json.loads((logs / f"{case}.stdout.log").read_bytes())
            except (ValueError, OSError):
                receipt = {}
            valid = (result["exit_code"] == 0 and result["capture_ok"] and len(created) == 1
                     and receipt.get("provisional") is True and receipt.get("trust_claim") is False
                     and receipt.get("case") == case and receipt.get("owned_exit") is True
                     and receipt.get("cleanup") is True and "failure" not in receipt and "cleanup_failure" not in receipt
                     and receipt.get("protected_output_probe_absent") is True
                     and receipt.get("ready") is (case in ("normal", "cancel", "stuck-descendant")))
            if case in ("normal", "cancel"):
                valid &= receipt.get("stop_escalated") is False and receipt.get("process_exit_code") == 0
            if case == "factory-stall":
                valid &= receipt.get("stop_escalated") is True
            case_groups_empty = bool(created)
            for base in created:
                unit = base.name + ".service"
                if not group_empty(unit):
                    subprocess.run(["systemctl", "stop", "--no-block", unit], check=False, timeout=5)
                    subprocess.run(["systemctl", "kill", "--kill-whom=all", "--signal=KILL", unit], check=False, timeout=5)
                    until = time.monotonic() + 5
                    while time.monotonic() < until and not group_empty(unit):
                        time.sleep(0.1)
                case_groups_empty &= group_empty(unit)
                valid &= case_groups_empty
                control = base / "control"
                retained_ok, identity_retained, budget_retained = retain_control_logs(control, logs, case)
                valid &= retained_ok
                if receipt.get("identity_diagnostic_written") is True:
                    valid &= identity_retained
                if receipt.get("budget_diagnostic_written") is True:
                    valid &= budget_retained
            safe_exit = case_groups_empty
            # Public summary contains fixed case names, booleans, numeric exits and hashes, never raw child errors.
            summaries.append({"case": case, "passed": bool(valid), "exit_code": result["exit_code"],
                              "owned_exit": receipt.get("owned_exit") is True, "cleanup": receipt.get("cleanup") is True,
                              "ready": receipt.get("ready") is True, "stop_escalated": receipt.get("stop_escalated") is True,
                              "managed_tool_sha256": sha(tool), "received_controller_bytes": result["received"]})
            if not safe_exit:
                break
    finally:
        if safe_exit:
            subprocess.run(["userdel", account], check=False, timeout=10)
            subprocess.run(["groupdel", account], check=False, timeout=10)
        archive = args.work / "private-root-logs.tar.gz"
        with archive.open("xb") as stream:
            os.fchmod(stream.fileno(), 0o600)
            with tarfile.open(fileobj=stream, mode="w:gz") as tar:
                for path in sorted(logs.iterdir()):
                    if path.is_file() and not path.is_symlink():
                        tar.add(path, arcname="private-root-logs/" + path.name, recursive=False)
    passed = len(summaries) == len(CASES) and all(item["passed"] for item in summaries) and safe_exit
    summary = {"schema": "issue779-aspire-mechanism-cases-v1", "trust_claim": False, "shared_admission": False,
               "cases": summaries, "passed_count": sum(item["passed"] for item in summaries),
               "failed_count": sum(not item["passed"] for item in summaries), "required_count": len(CASES),
               "all_owned_groups_empty": safe_exit, "passed": passed}
    print(json.dumps(summary, sort_keys=True))
    return 0 if passed else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest="mode", required=True)
    normal = modes.add_parser("run")
    normal.add_argument("--source", type=Path, required=True)
    normal.add_argument("--binding", type=Path, required=True)
    normal.add_argument("--output", type=Path, required=True)
    root = modes.add_parser("root-cases")
    for name in ("bundle", "controller", "dotnet", "work"):
        root.add_argument("--" + name, type=Path, required=True)
    root.add_argument("--controller-sha", required=True)
    args = parser.parse_args()
    try:
        return run(args) if args.mode == "run" else root_cases(args)
    except Exception as error:
        print(json.dumps({"trust_claim": False, "shared_admission": False,
                          "passed": False, "failure": "candidate-preparation-or-execution-failed",
                          "error_class": type(error).__name__}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
