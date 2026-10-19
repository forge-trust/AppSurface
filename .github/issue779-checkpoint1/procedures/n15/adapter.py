"""Actual N15 pending-start owner-death bridge; no build or native-authority API.

The parent root dispatcher supplies the fixed context and one absolute outer end.
N15 samples its original 240s job clock before its verification/staging, retains
Start30/the same pre-admission ABI clock and Cleanup60, and never renews them.
"""
from __future__ import annotations

import math
import os
from pathlib import Path
import re
import stat
import time
import types
from datetime import datetime, timedelta, timezone

import binding

HERE = Path(__file__).resolve().parent
C1 = HERE.parents[1]
PREFIX = ".github/issue779-checkpoint1/"
LOCAL = "procedures/n15/"
SHELL = "checkpoint-n15-pending-start-death.sh"
PARSER = "procedures/parsers/check_n15_records.py"
INSTALL = (SHELL, "observe_n15_pending_start.py", "n15-guard-reactivation-retry.sh",
           "finalize_n15_observation.py", "retain-native.sh")
CONTEXT = {"source", "selector", "capture_path", "capture_sha256", "build_receipt_path",
           "build_receipt_sha256", "tool_root", "runtime_root", "output", "generation",
           "binding_validator", "binding_validator_sha256", "workflow_identity"}
MAX_ARCHIVE = 33619968


def need(ok, reason):
    binding.require(ok, "n15-" + reason)


def load(rel, capture, *, deadline):
    """Execute only the exact fixed module bytes matched to the full current capture."""
    raw = binding.read(C1 / rel, 131072, deadline=deadline)
    need(binding.digest(raw) == capture["files"][PREFIX + rel]["sha256"], "module-source")
    module = types.ModuleType("c1_n15_" + Path(rel).stem)
    module.__file__ = str(C1 / rel)
    exec(compile(raw, module.__file__, "exec", dont_inherit=True), module.__dict__)
    return module


def _inputs(context, *, deadline, actual):
    binding.exact(context, CONTEXT, "n15-context")
    need(context["selector"] == "N15" and re.fullmatch(r"[0-9a-f]{32}", context["generation"])
         and re.fullmatch(r"github-actions-[1-9][0-9]*-[1-9][0-9]*", context["workflow_identity"]), "identity")
    capture_raw = binding.read(context["capture_path"], deadline=deadline)
    build_raw = binding.read(context["build_receipt_path"], deadline=deadline)
    need(binding.digest(capture_raw) == context["capture_sha256"]
         and binding.digest(build_raw) == context["build_receipt_sha256"], "input-bytes")
    build = binding.decode(build_raw)
    capture = binding.validate_build(build, capture_raw, context["source"], "N15")
    if actual:
        binding.verify_build(build, capture_raw, context["source"], "N15", deadline=deadline)
        need(Path(build["source_root"]) / PREFIX.rstrip("/") == C1
             and Path(context["binding_validator"]) == C1 / "binding.py"
             and context["binding_validator_sha256"] == capture["files"][PREFIX + "binding.py"]["sha256"]
             and context["tool_root"] == build["tool"]["root"]
             and context["runtime_root"] == build["runtime"]["root"], "fixed-roots")
    return capture_raw, build_raw, capture, build


def _command(output, ordinal, expected_phase, expected, *, deadline):
    """Re-read the actual root utility terminal and both original joined logs."""
    path = output / "commands" / f"command-{ordinal:02d}.json"
    raw = binding.read(path, 131072, deadline=deadline)
    need(binding.digest(raw) == expected["sha256"] and len(raw) == expected["bytes"], "command-record-binding")
    row = binding.decode(raw)
    need(type(row.get("exit")) is int and row["exit"] == 0
         and type(row.get("ordinal")) is int and row["ordinal"] == ordinal and row.get("phase") == expected_phase
         and all(row.get(k) is True for k in ("waited", "group_absent", "root_start_guard_created",
                   "root_start_guard_closed", "root_utility_cgroup_empty"))
         and row.get("forced_cleanup") is False and row.get("timed_out") is False
         and row.get("failure") is None, "command-terminal")
    need(type(row.get("root_utility_unit")) is str
         and re.fullmatch(r"issue779-native-tool-[0-9a-f]{32}-" + f"{ordinal:02d}" + r"\.service", row["root_utility_unit"]), "utility-unit")
    logs = row.get("logs")
    need(type(logs) is list and len(logs) == 2, "command-logs")
    for pin, suffix in zip(logs, ("stdout", "stderr")):
        binding.exact(pin, ("name", "sha256", "bytes"), "n15-command-log")
        name = f"{ordinal:02d}-{suffix}.log"
        need(pin["name"] == name and binding.integer(pin["bytes"], 0, 8388608), "command-log-bound")
        data = binding.read(output / "commands" / name, 8388608, deadline=deadline)
        need(len(data) == pin["bytes"] and binding.digest(data) == pin["sha256"], "command-log-bytes")


def verify_retained_evidence(context, *, deadline):
    """Mandatory actual archive/command-leaf readback and original record predicates."""
    capture_raw, build_raw, capture, _ = _inputs(context, deadline=deadline, actual=False)
    output = Path(context["output"])
    raw = binding.read(output / "N15-evidence.json", deadline=deadline)
    value = binding.decode(raw)
    binding.exact(value, ("schema", "source", "selector", "generation", "workflow_identity",
        "source_capture_sha256", "build_receipt_sha256", "procedure_sha256", "archive_sha256", "archive_bytes",
        "original_job_deadline_utc", "original_job_deadline_boot_ms", "original_work_deadline_boot_ms",
        "base_revision", "commands"), "n15-evidence-fields")
    need(value["schema"] == "issue779-checkpoint1-n15-evidence-v1"
         and value["source"] == context["source"] and value["selector"] == "N15"
         and value["generation"] == context["generation"] and value["workflow_identity"] == context["workflow_identity"]
         and value["source_capture_sha256"] == binding.digest(capture_raw)
         and value["build_receipt_sha256"] == binding.digest(build_raw)
         and value["procedure_sha256"] == capture["files"][PREFIX + LOCAL + SHELL]["sha256"], "evidence-identity")
    need(binding.integer(value["archive_bytes"], 1, MAX_ARCHIVE)
         and binding.integer(value["original_job_deadline_boot_ms"], 1, 10**15-1)
         and binding.integer(value["original_work_deadline_boot_ms"], 1, 10**15-1)
         and value["original_work_deadline_boot_ms"] == value["original_job_deadline_boot_ms"] - 60000
         and type(value["base_revision"]) is str and binding.HEX40.fullmatch(value["base_revision"]), "original-metadata")
    archive = binding.read(output / "native.tar", MAX_ARCHIVE, deadline=deadline)
    need(binding.digest(archive) == value["archive_sha256"] and len(archive) == value["archive_bytes"], "archive-binding")
    records = load(LOCAL + "records.py", capture, deadline=deadline)
    parser_raw = binding.read(C1 / PARSER, 131072, deadline=deadline)
    need(binding.digest(parser_raw) == capture["files"][PREFIX + PARSER]["sha256"], "parser-binding")
    members = records.inspect_archive(archive)
    records.validate_n15_archive(members, parser_raw, context["generation"], context["build_receipt_sha256"],
        context["workflow_identity"], deadline, value["original_job_deadline_utc"],
        context["source"], context["capture_sha256"], value["base_revision"])
    need(type(value["commands"]) is list and len(value["commands"]) == 2, "command-count")
    for ordinal, phase in enumerate(("actual-csharp-n15-owner-launch-observe-and-replay", "n15-private-retention")):
        expected = value["commands"][ordinal]
        binding.exact(expected, ("sha256", "bytes"), "n15-command-record-pin")
        _command(output, ordinal, phase, expected, deadline=deadline)
    return {"case": "N15", "passed": True, "skipped": False,
            "procedure_sha256": value["procedure_sha256"], "evidence_sha256": binding.digest(raw)}


def execute(context, *, deadline):
    """Run the original pending-start observer, pidfd owner kill, replay and finalizer."""
    start = time.monotonic()
    start_utc = datetime.now(timezone.utc)
    start_boot = time.clock_gettime_ns(time.CLOCK_BOOTTIME) // 1000000
    original_job_end = start + 240.0
    original_boot_end = start_boot + 240000
    original_work_end = original_job_end - 60.0
    original_utc_end = (start_utc + timedelta(seconds=240)).strftime("%Y-%m-%dT%H:%M:%SZ")
    need(os.geteuid() == os.getegid() == 0 and os.uname().sysname == "Linux", "root-linux-required")
    need(type(deadline) is float and math.isfinite(deadline) and 850 < deadline - start <= 900, "original-outer-budget")
    os.umask(0o077)
    capture_raw, build_raw, capture, build = _inputs(context, deadline=deadline, actual=True)
    for name in ("adapter.py", "materialize.py", "records.py", "process.py", *INSTALL):
        raw = binding.read(HERE / name, 131072, deadline=deadline)
        need(binding.digest(raw) == capture["files"][PREFIX + LOCAL + name]["sha256"], "current-procedure")
    output = Path(context["output"])
    need(output.resolve(strict=True) == output and stat.S_IMODE(output.stat().st_mode) == 0o700
         and output.stat().st_uid == output.stat().st_gid == 0
         and {p.name for p in output.iterdir()} == {"plan.json", "source.json", "build-receipt.json", "procedure-inventory.json", "execution.json"}, "existing-output")
    owner = load("procedures/owner-death/adapter.py", capture, deadline=deadline)
    prepared = owner.prepare_verified_inputs(context, binding, capture, build, capture_raw, build_raw, deadline=min(deadline, original_work_end))
    control, retained = prepared["control"], prepared["retained"]
    reviewed = retained / "reviewed"
    reviewed.mkdir(mode=0o700)
    for name in INSTALL:
        raw = binding.read(HERE / name, 131072, deadline=deadline)
        owner.save(reviewed / name, raw)
        os.chmod(reviewed / name, 0o500 if name == "retain-native.sh" else 0o444)
    parser_raw = binding.read(C1 / PARSER, 131072, deadline=deadline)
    need(binding.digest(parser_raw) == capture["files"][PREFIX + PARSER]["sha256"], "current-parser")
    owner.save(reviewed / "check_n15_records.py", parser_raw)
    os.chmod(reviewed / "check_n15_records.py", 0o444)
    os.chmod(reviewed, 0o555)
    auditor = load("procedures/os_audit.py", capture, deadline=deadline)
    empty_helper = auditor.create_empty_helper(retained / "empty-audit-helper", deadline=min(deadline, original_work_end))
    audit = auditor.Audit(min(original_work_end - 5, deadline - 5, time.monotonic() + 60))
    audit.initialize_tools()
    audit_raw = audit.build(str(prepared["roots"]["runtime"]), str(prepared["roots"]["tool"]), False, str(empty_helper))
    owner.save(control / "os-audit.json", audit_raw)
    staging = load(LOCAL + "materialize.py", capture, deadline=deadline)
    staged = staging.materialize(context, prepared, owner, deadline=min(deadline, original_work_end),
                                 job_utc=original_utc_end, boot_end=original_boot_end)
    records = load(LOCAL + "records.py", capture, deadline=deadline)
    fixture = records.fixture_arguments(retained, context["generation"], original_boot_end)
    fixture += ["--source-commit", context["source"], "--capture-sha256", context["capture_sha256"]]
    native = load(LOCAL + "process.py", capture, deadline=deadline)
    commands = output / "commands"
    commands.mkdir(mode=0o700)
    runner = native.Runner(commands, deadline)
    runner.phase = "actual-csharp-n15-owner-launch-observe-and-replay"
    original_error = None
    try:
        runner.run(native.ROOT_PREFIX + ["-c", records.DISPATCH, "--", context["generation"],
            capture["files"][PREFIX + LOCAL + SHELL]["sha256"], *fixture], min(deadline - 5, original_job_end))
    except BaseException as error:
        original_error = error
    # Diagnostic retention cannot replace or erase the original fixture failure.
    retention_error = None
    try:
        runner.phase = "n15-private-retention"
        retain_end = min(deadline - 5, time.monotonic() + 30)
        runner.run(native.ROOT_PREFIX + [str(reviewed / "retain-native.sh"), "--execute",
            "--generation", context["generation"], "--deadline-monotonic-ms", native.root_deadline(retain_end),
            "--reviewed-script-sha256", capture["files"][PREFIX + LOCAL + "retain-native.sh"]["sha256"]], retain_end)
        archive_path = control / "native.tar"
        before = archive_path.lstat()
        need(stat.S_ISREG(before.st_mode) and before.st_uid == before.st_gid == 0
             and stat.S_IMODE(before.st_mode) == 0o600 and before.st_nlink == 1, "actual-archive-custody")
        archive = binding.read(archive_path, MAX_ARCHIVE, deadline=deadline)
        native.save(output / "native.tar", archive, deadline)
    except BaseException as error:
        retention_error = error
    if original_error is not None: raise original_error
    if retention_error is not None: raise retention_error
    # Parent revision is the actual immutable current source parent read by staging.
    request = binding.decode(binding.read(staged["request"], 65536, deadline=deadline))
    command_pins = []
    for ordinal in range(2):
        raw = binding.read(commands / f"command-{ordinal:02d}.json", 131072, deadline=deadline)
        command_pins.append({"sha256": binding.digest(raw), "bytes": len(raw)})
    evidence = {"schema": "issue779-checkpoint1-n15-evidence-v1", "source": context["source"], "selector": "N15",
        "generation": context["generation"], "workflow_identity": context["workflow_identity"],
        "source_capture_sha256": context["capture_sha256"], "build_receipt_sha256": context["build_receipt_sha256"],
        "procedure_sha256": capture["files"][PREFIX + LOCAL + SHELL]["sha256"],
        "archive_sha256": binding.digest(archive), "archive_bytes": len(archive),
        "original_job_deadline_utc": original_utc_end, "original_job_deadline_boot_ms": original_boot_end,
        "original_work_deadline_boot_ms": original_boot_end - 60000, "base_revision": request["base_revision"],
        "commands": command_pins}
    owner.save(output / "N15-evidence.json", binding.canonical(evidence))
    return verify_retained_evidence(context, deadline=deadline)
