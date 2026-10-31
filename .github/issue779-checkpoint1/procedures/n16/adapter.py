"""Fixed current-source N16 accepted-work procedure and actual retained evidence.

Linux root entry is owned by the parent workflow. This module reuses the complete
current-input materializer, then invokes the retained N16 shell under its original
600s fixture/30s cleanup and 240s root-unit clocks. It never builds, admits a case,
changes product budgets, or creates a substitute native record.
"""
from __future__ import annotations

import base64
import math
import os
from pathlib import Path
import re
import stat
import subprocess
import time
import types

import binding

HERE = Path(__file__).resolve().parent
BASE = HERE.parents[1]
PREFIX = ".github/issue779-checkpoint1/"
SHELL = "procedures/n16/checkpoint-n16-accepted-work.sh"
PARSER = "procedures/parsers/check_n16_records.py"
CUSTODY = "procedures/n16/n16_retained_custody.py"
RESOURCES = ("procedures/owner-death/adapter.py", "procedures/owner-death/workspace.py",
             "procedures/native_process.py", "procedures/os_audit.py", "procedures/n16/records.py",
             SHELL, PARSER, CUSTODY, "procedures/n16/adapter.py")
CONTEXT = {"source", "selector", "capture_path", "capture_sha256", "build_receipt_path",
           "build_receipt_sha256", "tool_root", "runtime_root", "output", "generation",
           "binding_validator", "binding_validator_sha256", "workflow_identity"}
LEAVES = {"n16.stdout": 0, "n16.stderr": 114688, "n16-record-summary.json": 4096,
          "n16-fixture-result.json": 4096, "request.json": 65536, "request-policy.sha256": 4096,
          "fixture-stdout.log": 8388608, "fixture-stderr.log": 8388608,
          "command-00.json": 131072, "00-stdout.log": 8388608, "00-stderr.log": 8388608}


def need(ok, category):
    binding.require(ok, "n16-" + category)


def load(rel, capture, *, deadline):
    """Compile only the bytes just read and matched to the complete current capture."""
    raw = binding.read(BASE / rel, 131072, deadline=deadline)
    need(binding.digest(raw) == capture["files"][PREFIX + rel]["sha256"], "module-source")
    module = types.ModuleType("c1_n16_" + Path(rel).stem)
    module.__file__ = str(BASE / rel)
    exec(compile(raw, module.__file__, "exec", dont_inherit=True), module.__dict__)
    return module


# The dispatcher verifies current source before loading this fixed adapter. These
# source-owned parsers have no native I/O at import; retain exact bytes for binding.
_RECORD_RAW = (HERE / "records.py").read_bytes()
_PARSER_RAW = (BASE / PARSER).read_bytes()
_RECORDS = types.ModuleType("c1_n16_records")
_PARSER = types.ModuleType("c1_n16_native_record_parser")
exec(compile(_RECORD_RAW, str(HERE / "records.py"), "exec", dont_inherit=True), _RECORDS.__dict__)
exec(compile(_PARSER_RAW, str(BASE / PARSER), "exec", dont_inherit=True), _PARSER.__dict__)


def validate_nodes(nodes, capture, build):
    """Bind truthful retained inventory metadata to every current source/image hash."""
    binding.exact(nodes, ("source", "tool", "runtime"), "n16-node-roles")
    for role, node in nodes.items():
        binding.exact(node, ("files", "directories"), "n16-node-fields")
        need(type(node["files"]) is dict and type(node["directories"]) is dict, "node-types")
        image = capture["files"] if role == "source" else build[role]["files"]
        need(set(node["files"]) == set(image), "node-file-set")
        directories = {"."}
        for name, row in node["files"].items():
            binding.exact(row, ("sha256", "bytes", "mode"), "n16-node-row")
            parts = name.split("/")
            directories.update("/".join(parts[:n]) for n in range(1, len(parts)))
            original = image[name]
            mode = original["mode"] if role == "source" else ("0555" if int(original["mode"], 8) & 0o111 else "0444")
            need(row["sha256"] == original["sha256"] and row["mode"] == mode
                 and binding.integer(row["bytes"], 0, binding.MAX_FILE), "node-file-binding")
            if role != "source": need(row["bytes"] == original["bytes"], "node-file-bytes")
        if role != "source": directories = {".", *build[role]["directories"]}
        need(set(node["directories"]) == directories, "node-directory-set")
        need(all(row == {"mode": "0700" if role == "source" else "0555"}
                 for row in node["directories"].values()), "node-directory-mode")


def artifacts(nodes):
    """Derive inventory output cardinalities, never product or coverage counters."""
    return {role: {"file_count": len(node["files"]),
                   "node_count": len(node["files"]) + len(node["directories"]),
                   "total_bytes": sum(row["bytes"] for row in node["files"].values())}
            for role, node in nodes.items()}


def validate_evidence(context, evidence_raw, *, capture_raw):
    """Pure retained-data reconciliation; detached models establish no native facts."""
    capture = binding.validate_capture(binding.decode(capture_raw), context["source"])
    need(binding.digest(capture_raw) == context["capture_sha256"], "capture-hash")
    value = binding.decode(evidence_raw)
    binding.exact(value, ("schema", "source", "selector", "input_generation", "workflow_identity",
                         "source_capture_sha256", "build_receipt_sha256", "procedure_sha256",
                         "base_revision", "build_raw", "nodes", "files"), "n16-evidence-fields")
    need(value["schema"] == "issue779-checkpoint1-n16-evidence-v1" and context["selector"] == "N16"
         and value["source"] == context["source"] and value["selector"] == "N16"
         and value["input_generation"] == context["generation"]
         and value["workflow_identity"] == context["workflow_identity"]
         and value["source_capture_sha256"] == context["capture_sha256"]
         and value["build_receipt_sha256"] == context["build_receipt_sha256"], "evidence-identity")
    need(value["procedure_sha256"] == capture["files"][PREFIX + SHELL]["sha256"]
         and binding.digest(_RECORD_RAW) == capture["files"][PREFIX + "procedures/n16/records.py"]["sha256"]
         and binding.digest(_PARSER_RAW) == capture["files"][PREFIX + PARSER]["sha256"], "evidence-procedure")
    need(type(value["base_revision"]) is str and binding.HEX40.fullmatch(value["base_revision"]), "base-revision")
    build_raw = base64.b64decode(value["build_raw"], validate=True)
    need(binding.digest(build_raw) == context["build_receipt_sha256"], "evidence-build-bytes")
    build = binding.decode(build_raw)
    binding.validate_build(build, capture_raw, context["source"], "N16")
    validate_nodes(value["nodes"], capture, build)
    need(type(value["files"]) is dict and set(value["files"]) == set(LEAVES), "evidence-leaves")
    files = {}
    for name, row in value["files"].items():
        binding.exact(row, ("base64", "sha256", "bytes"), "n16-retained-row")
        need(binding.integer(row["bytes"], 0, LEAVES[name]) and type(row["base64"]) is str, "retained-bound")
        raw = base64.b64decode(row["base64"], validate=True)
        need(len(raw) == row["bytes"] and binding.digest(raw) == row["sha256"], "retained-binding")
        files[name] = raw
    command = binding.decode(files["command-00.json"])
    need(type(command.get("exit")) is int and command["exit"] == 0
         and command.get("ordinal") == 0 and command.get("phase") == "actual-csharp-n16"
         and all(command.get(k) is True for k in ("waited", "group_absent", "root_start_guard_created",
                   "root_start_guard_closed", "root_utility_cgroup_empty"))
         and command.get("forced_cleanup") is False and command.get("timed_out") is False
         and command.get("failure") is None, "actual-command-terminal")
    need(type(command.get("root_utility_unit")) is str
         and re.fullmatch(r"issue779-native-tool-[0-9a-f]{32}-00\.service", command["root_utility_unit"]), "actual-utility-unit")
    need(command.get("logs") == [{"name": name, "bytes": len(files[name]), "sha256": binding.digest(files[name])}
                                 for name in ("00-stdout.log", "00-stderr.log")], "actual-command-log-binding")
    result = binding.decode(files["n16-fixture-result.json"])
    generation = result.get("generation")
    _RECORDS.validate_fixture_result(result, generation, context["build_receipt_sha256"],
                                    context["source"], context["capture_sha256"])
    _RECORDS.validate_fixture_output(files["fixture-stdout.log"], files["fixture-stderr.log"], artifacts(value["nodes"]))
    need(files["n16.stdout"] == b"", "root-stdout")
    summary = _PARSER.validate_bytes(files["n16.stderr"], generation, 1, context["source"],
                                     context["capture_sha256"], context["build_receipt_sha256"])
    need(summary == binding.decode(files["n16-record-summary.json"])
         and summary["stderr_sha256"] == result["stderr_sha256"], "record-summary")
    need(all(summary[k] == result[k] for k in ("worker_unit", "worker_pid", "worker_uid", "worker_gid", "results_gid")), "record-identities")
    _RECORDS.validate_request_binding(files["request.json"], files["request-policy.sha256"], generation,
                                       context["workflow_identity"], context["source"], value["base_revision"])
    return {"case": "N16", "passed": True, "skipped": False,
            "procedure_sha256": value["procedure_sha256"], "evidence_sha256": binding.digest(evidence_raw)}


def verify_retained_evidence(context, *, deadline):
    """Re-read each finite retained raw leaf before applying the pure record parser."""
    output = Path(context["output"])
    raw = binding.read(output / "N16-evidence.json", deadline=deadline)
    value = binding.decode(raw)
    need(type(value.get("files")) is dict and set(value["files"]) == set(LEAVES), "retained-leaves")
    for name, row in value["files"].items():
        leaf = output / name if name in ("command-00.json", "00-stdout.log", "00-stderr.log") else output / ("N16-" + name)
        data = binding.read(leaf, LEAVES[name], deadline=deadline)
        need(len(data) == row["bytes"] and binding.digest(data) == row["sha256"], "actual-retained-leaf")
    capture_raw = binding.read(context["capture_path"], deadline=deadline)
    return validate_evidence(context, raw, capture_raw=capture_raw)


FAILURE_DIAGNOSTIC_CAPS = {"fixture-stdout.log": LEAVES["fixture-stdout.log"],
                           "fixture-stderr.log": LEAVES["fixture-stderr.log"]}


def _file_identity(value):
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid,
            value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)


def _same_directory(path, expected, uid, gid):
    try:
        current = path.lstat()
    except OSError:
        return False
    fact = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_uid,
                          value.st_gid)
    return (stat.S_ISDIR(expected.st_mode) and expected.st_uid == uid and expected.st_gid == gid
            and stat.S_IMODE(expected.st_mode) == 0o700 and fact(expected) == fact(current))


def _same_open_directory(path, expected, fd, uid, gid):
    try:
        opened = os.fstat(fd)
    except OSError:
        return False
    fact = lambda value: (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid)
    return _same_directory(path, expected, uid, gid) and fact(expected) == fact(opened)


def _read_failure_log(control_fd, name, uid, gid, deadline):
    """Read one fixed N16 failure log with existing byte bounds and stable custody."""
    try:
        before = os.stat(name, dir_fd=control_fd, follow_symlinks=False)
    except FileNotFoundError:
        return None
    need(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
         and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o600
         and 0 <= before.st_size <= FAILURE_DIAGNOSTIC_CAPS[name], "failure-log-custody")
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=control_fd)
    try:
        opened = os.fstat(fd)
        need(_file_identity(before) == _file_identity(opened), "failure-log-open-substitution")
        body = bytearray()
        while len(body) <= before.st_size:
            binding.require(time.monotonic() < deadline, "failure-log-read-deadline")
            chunk = os.read(fd, min(65536, before.st_size + 1 - len(body)))
            if not chunk:
                break
            body.extend(chunk)
            need(len(body) <= before.st_size, "failure-log-growth")
        after_fd = os.fstat(fd)
        after_name = os.stat(name, dir_fd=control_fd, follow_symlinks=False)
        need(len(body) == before.st_size and _file_identity(before) == _file_identity(after_fd)
             == _file_identity(after_name), "failure-log-changed")
        return bytes(body)
    finally:
        os.close(fd)


def _retain_failure_logs_owned(control, output, command, *, deadline, uid, gid):
    """Copy only settled N16's two fixed failure logs; never form positive evidence.

    Missing source logs remain explicitly absent, not synthetic empty files. The
    manifest is written last so a partial copy has no complete-retention marker.
    """
    require = binding.require
    require(type(uid) is int and type(gid) is int and uid >= 0 and gid >= 0, "failure-owner")
    require(type(command) is dict and type(command.get("ordinal")) is int and command["ordinal"] == 0
            and command.get("phase") == "actual-csharp-n16" and type(command.get("exit")) is int
            and command["exit"] != 0 and all(command.get(key) is True for key in
            ("waited", "group_absent", "root_start_guard_created", "root_start_guard_closed",
             "root_utility_cgroup_empty")), "failure-command-not-joined")
    control, output = Path(control), Path(output)
    require(control.is_absolute() and output.is_absolute() and control != output
            and control not in output.parents and output not in control.parents
            and control.resolve(strict=True) == control and output.resolve(strict=True) == output,
            "failure-directory-path")
    control_before, output_before = control.lstat(), output.lstat()
    require(_same_directory(control, control_before, uid, gid)
            and _same_directory(output, output_before, uid, gid), "failure-directory-custody")
    output_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        require(_file_identity(output_before) == _file_identity(os.fstat(output_fd))
                and _same_directory(output, output_before, uid, gid), "failure-output-open-substitution")
        control_fd = os.open(control, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            require(_file_identity(control_before) == _file_identity(os.fstat(control_fd))
                    and _same_directory(control, control_before, uid, gid), "failure-control-open-substitution")
            captured = {name: _read_failure_log(control_fd, name, uid, gid, deadline)
                        for name in FAILURE_DIAGNOSTIC_CAPS}
            require(_file_identity(control_before) == _file_identity(os.fstat(control_fd))
                    and _same_directory(control, control_before, uid, gid), "failure-control-changed")
        finally:
            os.close(control_fd)
        require(_file_identity(output_before) == _file_identity(os.fstat(output_fd))
                and _same_directory(output, output_before, uid, gid), "failure-output-changed")
        require(time.monotonic() < deadline, "failure-retention-deadline")
        if not any(raw is not None for raw in captured.values()):
            entries = {name: {"present": False} for name in FAILURE_DIAGNOSTIC_CAPS}
        else:
            entries = {}
            for name, raw in captured.items():
                entry = {"present": raw is not None}
                if raw is not None:
                    require(time.monotonic() < deadline, "failure-retention-deadline")
                    destination = "N16-failure-" + name
                    fd = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                                 0o600, dir_fd=output_fd)
                    try:
                        before = os.fstat(fd)
                        require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                                and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o600,
                                "failure-output-shape")
                        view = memoryview(raw)
                        while view:
                            require(time.monotonic() < deadline, "failure-output-deadline")
                            count = os.write(fd, view)
                            require(count > 0, "failure-output-short-write")
                            view = view[count:]
                        os.fsync(fd)
                        after = os.fstat(fd)
                        named = os.stat(destination, dir_fd=output_fd, follow_symlinks=False)
                        require(stat.S_ISREG(after.st_mode) and after.st_uid == uid and after.st_gid == gid
                                and after.st_nlink == 1 and stat.S_IMODE(after.st_mode) == 0o600
                                and _file_identity(before)[:5] == _file_identity(after)[:5]
                                and _file_identity(after) == _file_identity(named), "failure-output-changed")
                    finally:
                        os.close(fd)
                    entry.update({"bytes": len(raw), "sha256": binding.digest(raw)})
                entries[name] = entry
        manifest = binding.canonical({"schema": "issue779-n16-failure-logs-v1", "files": entries})
        manifest_name = "N16-failure-logs.json"
        require(time.monotonic() < deadline, "failure-retention-deadline")
        fd = os.open(manifest_name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                     0o600, dir_fd=output_fd)
        try:
            before = os.fstat(fd)
            require(stat.S_ISREG(before.st_mode) and before.st_uid == uid and before.st_gid == gid
                    and before.st_nlink == 1 and stat.S_IMODE(before.st_mode) == 0o600,
                    "failure-manifest-custody")
            view = memoryview(manifest)
            while view:
                require(time.monotonic() < deadline, "failure-manifest-deadline")
                count = os.write(fd, view)
                require(count > 0, "failure-manifest-short-write")
                view = view[count:]
            os.fsync(fd)
            after = os.fstat(fd)
            named = os.stat(manifest_name, dir_fd=output_fd, follow_symlinks=False)
            require(stat.S_ISREG(after.st_mode) and after.st_uid == uid and after.st_gid == gid
                    and after.st_nlink == 1 and stat.S_IMODE(after.st_mode) == 0o600
                    and _file_identity(before)[:5] == _file_identity(after)[:5]
                    and _file_identity(after) == _file_identity(named), "failure-manifest-changed")
        finally:
            os.close(fd)
        require(_same_directory(control, control_before, uid, gid)
                and _same_open_directory(output, output_before, output_fd, uid, gid)
                and time.monotonic() < deadline, "failure-directory-changed")
        return True
    finally:
        os.close(output_fd)
def retain_failure_logs(control, output, command, *, deadline):
    """Root-only production boundary for fixed, joined N16 failure logs."""
    binding.require(os.geteuid() == 0 and os.getegid() == 0, "failure-retention-root-required")
    return _retain_failure_logs_owned(control, output, command, deadline=deadline, uid=0, gid=0)


def run_dispatch_with_failure_retention(runner, argv, control, output, *, deadline):
    """Run once; bounded log retention cannot replace the original runner failure."""
    run_deadline = min(deadline - 60, time.monotonic() + 610)
    try:
        return runner.run(argv, run_deadline)
    except BaseException:
        try:
            if runner.records:
                retention_deadline = min(deadline - 5, time.monotonic() + 30)
                if retention_deadline > time.monotonic():
                    retain_failure_logs(control, output, runner.records[-1],
                                        deadline=retention_deadline)
        except BaseException:
            # Diagnostics are best-effort and cannot replace the native failure.
            pass
        raise


def execute(context, *, deadline):
    """Run the actual N16 shell once, then validate its real private data and pipes."""
    binding.exact(context, CONTEXT, "n16-context")
    need(os.geteuid() == 0 and os.uname().sysname == "Linux", "linux-root-required")
    need(type(deadline) is float and math.isfinite(deadline) and 0 < deadline - time.monotonic() <= 900, "deadline")
    need(context["selector"] == "N16" and re.fullmatch(r"[0-9a-f]{32}", context["generation"])
         and re.fullmatch(r"github-actions-[1-9][0-9]*-[1-9][0-9]*", context["workflow_identity"]), "identity")
    capture_raw = binding.read(context["capture_path"], deadline=deadline)
    build_raw = binding.read(context["build_receipt_path"], deadline=deadline)
    need(binding.digest(capture_raw) == context["capture_sha256"]
         and binding.digest(build_raw) == context["build_receipt_sha256"], "input-hashes")
    build = binding.decode(build_raw)
    capture = binding.verify_build(build, capture_raw, context["source"], "N16", deadline=deadline)
    need(BASE == Path(build["source_root"]) / PREFIX.rstrip("/")
         and Path(context["binding_validator"]) == BASE / "binding.py"
         and context["binding_validator_sha256"] == capture["files"][PREFIX + "binding.py"]["sha256"]
         and context["tool_root"] == build["tool"]["root"] and context["runtime_root"] == build["runtime"]["root"], "fixed-source-root")
    for rel in RESOURCES:
        need(binding.digest(binding.read(BASE / rel, 131072, deadline=deadline)) == capture["files"][PREFIX + rel]["sha256"], "resource-source")
    output = Path(context["output"])
    need(output.resolve(strict=True) == output and stat.S_IMODE(output.stat().st_mode) == 0o700
         and output.stat().st_uid == output.stat().st_gid == 0
         and {p.name for p in output.iterdir()} == {"plan.json", "source.json", "build-receipt.json", "procedure-inventory.json", "execution.json"}, "existing-output")
    stage = load("procedures/owner-death/adapter.py", capture, deadline=deadline)
    prepared = stage.prepare_verified_inputs(context, binding, capture, build, capture_raw, build_raw,
                                             deadline=deadline, source_sealed=False)
    validate_nodes(prepared["nodes"], capture, build)
    retained, control = prepared["retained"], prepared["control"]
    reviewed = retained / "reviewed"
    reviewed.mkdir(mode=0o700)
    for rel in (SHELL, PARSER, CUSTODY):
        raw = binding.read(BASE / rel, 131072, deadline=deadline)
        stage.save(reviewed / Path(rel).name, raw)
        os.chmod(reviewed / Path(rel).name, 0o444)
    os.chmod(reviewed, 0o555)
    process = load("procedures/native_process.py", capture, deadline=deadline)
    auditor = load("procedures/os_audit.py", capture, deadline=deadline)
    empty_helper = auditor.create_empty_helper(retained / "empty-audit-helper", deadline=deadline)
    # Audit is a metadata-only library; every original OS/ELF/stdlib predicate is
    # retained. Root entry lets it inspect these root-private copies directly.
    audit = auditor.Audit(min(deadline - 5, time.monotonic() + 60))
    audit.initialize_tools()
    audit_raw = audit.build(str(prepared["roots"]["runtime"]), str(prepared["roots"]["tool"]), False, str(empty_helper))
    stage.save(control / "os-audit.json", audit_raw)
    parent = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "safe.directory=" + build["source_root"],
                             "rev-parse", context["source"] + "^"], cwd=build["source_root"], stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            timeout=min(15, max(.001, deadline - time.monotonic())), check=False)
    need(parent.returncode == 0 and len(parent.stdout) <= 41, "actual-git-parent")
    base = parent.stdout.decode("ascii").strip()
    need(binding.HEX40.fullmatch(base), "actual-git-parent")
    args = ["--execute", "--reviewed-script-sha256", capture["files"][PREFIX + SHELL]["sha256"]]
    for role, flag in (("source", "source"), ("tool", "payload"), ("runtime", "runtime")):
        for suffix, path in (("root", prepared["roots"][role]), ("manifest", control / (role + ".tsv")), ("nodes", control / (role + "-nodes.json"))):
            args += ["--" + flag + "-" + suffix, str(path)]
            if suffix != "root": args += ["--" + flag + "-" + suffix + "-sha256", binding.digest(binding.read(path, deadline=deadline))]
    args += ["--binding-validator", str(control / "binding.py"), "--binding-validator-sha256", context["binding_validator_sha256"],
             "--binding-deadline-monotonic", str(deadline), "--build-receipt", str(control / "build-receipt.json"),
             "--build-receipt-sha256", context["build_receipt_sha256"], "--source-capture", str(control / "source.json"),
             "--source-capture-sha256", context["capture_sha256"], "--source-count", str(capture["count"]),
             "--root-projection", str(control / "build-receipt.json"), "--root-projection-sha256", context["build_receipt_sha256"],
             "--os-audit", str(control / "os-audit.json"), "--os-audit-sha256", binding.digest(audit_raw),
             "--source-revision", context["source"], "--base-revision", base, "--workflow-identity", context["workflow_identity"],
             "--entry", "ForgeTrust.AppSurface.Cli.dll", "--entry-sha256", build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"],
             "--runtime-host", "dotnet", "--n16-parser-root", str(reviewed), "--n16-parser-sha256", binding.digest(_PARSER_RAW),
             "--n16-custody-sha256", capture["files"][PREFIX + CUSTODY]["sha256"]]
    need(deadline - time.monotonic() > 665, "fixture-plus-retention-budget")
    runner = process.Runner(output, deadline)
    runner.phase = "actual-csharp-n16"
    run_dispatch_with_failure_retention(
        runner,
        process.ROOT_PREFIX + ["-c", process.DISPATCH, "--", context["generation"],
                               capture["files"][PREFIX + SHELL]["sha256"], *args],
        control, output, deadline=deadline)
    private_parent = Path("/run/appsurface-evidence-fixture")
    need(private_parent.resolve(strict=True) == private_parent and private_parent.stat().st_uid == private_parent.stat().st_gid == 0
         and stat.S_IMODE(private_parent.stat().st_mode) == 0o700, "actual-private-parent")
    children = list(private_parent.iterdir())
    need(len(children) == 1 and re.fullmatch(r"[0-9a-f]{32}", children[0].name)
         and children[0].resolve(strict=True) == children[0], "actual-private-generation")
    private = children[0]
    actual = {}
    for name, limit in LEAVES.items():
        if name.startswith("fixture-"): path = control / name
        elif name in ("command-00.json", "00-stdout.log", "00-stderr.log"): path = output / name
        else: path = private / ("logs" if name in ("n16.stdout", "n16.stderr") else "") / name
        before = path.lstat()
        need(stat.S_ISREG(before.st_mode) and before.st_uid == before.st_gid == 0 and before.st_nlink == 1
             and stat.S_IMODE(before.st_mode) == 0o600, "actual-retained-custody")
        raw = binding.read(path, limit, deadline=deadline)
        if path.parent != output: stage.save(output / ("N16-" + name), raw)
        actual[name] = {"base64": base64.b64encode(raw).decode("ascii"), "sha256": binding.digest(raw), "bytes": len(raw)}
    value = {"schema": "issue779-checkpoint1-n16-evidence-v1", "source": context["source"], "selector": "N16",
             "input_generation": context["generation"], "workflow_identity": context["workflow_identity"],
             "source_capture_sha256": context["capture_sha256"], "build_receipt_sha256": context["build_receipt_sha256"],
             "procedure_sha256": capture["files"][PREFIX + SHELL]["sha256"], "base_revision": base,
             "build_raw": base64.b64encode(build_raw).decode("ascii"), "nodes": prepared["nodes"], "files": actual}
    evidence_raw = binding.canonical(value)
    row = validate_evidence(context, evidence_raw, capture_raw=capture_raw)
    stage.save(output / "N16-evidence.json", evidence_raw)
    need(verify_retained_evidence(context, deadline=deadline) == row, "retained-reconciliation")
    return row
