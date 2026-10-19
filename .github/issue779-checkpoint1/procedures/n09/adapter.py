"""Current-source N09 retained-slot procedure with original native predicates.

The root dispatcher verifies the complete current source before this import.
Staging, the same-host OS audit, shell dispatch, joined retention and readback all
share the original outer deadline. Detached records never establish admission.
"""
from pathlib import Path
import os
import pwd
import re
import stat
import subprocess
import time
import types

import binding

HERE = Path(__file__).resolve().parent
C1 = HERE.parents[1]
PREFIX = ".github/issue779-checkpoint1/"
LOCAL = "procedures/n09/"
SHELL = "checkpoint-n09-retained-slot.sh"
INPUTS = {"plan.json", "source.json", "build-receipt.json", "procedure-inventory.json", "execution.json"}
CONTEXT = {"source", "selector", "capture_path", "capture_sha256", "build_receipt_path",
           "build_receipt_sha256", "tool_root", "runtime_root", "output", "generation",
           "binding_validator", "binding_validator_sha256", "workflow_identity"}
PARSERS = ("check_kernel_record.py", "check_cancellation_records.py", "cancellation_adapter.py", "check_n09_records.py")
MAX_ARCHIVE = 33619968


def need(ok, category):
    binding.require(ok, "n09-" + category)


def load(rel, capture, *, deadline):
    """Import only fixed bounded bytes bound to this complete current capture."""
    raw = binding.read(C1 / rel, 131072, deadline=deadline)
    need(binding.digest(raw) == capture["files"][PREFIX + rel]["sha256"], "source-module")
    module = types.ModuleType("c1_n09_" + Path(rel).stem)
    module.__file__ = str(C1 / rel)
    exec(compile(raw, module.__file__, "exec", dont_inherit=True), module.__dict__)
    return module


def inputs(context, *, deadline, actual):
    binding.exact(context, CONTEXT, "n09-context")
    need(context["selector"] == "N09" and re.fullmatch("[0-9a-f]{32}", context["generation"])
         and re.fullmatch("github-actions-[1-9][0-9]*-[1-9][0-9]*", context["workflow_identity"]), "identity")
    capture_raw = binding.read(context["capture_path"], deadline=deadline)
    build_raw = binding.read(context["build_receipt_path"], deadline=deadline)
    need(binding.digest(capture_raw) == context["capture_sha256"]
         and binding.digest(build_raw) == context["build_receipt_sha256"], "input-bytes")
    build = binding.decode(build_raw)
    capture = binding.validate_build(build, capture_raw, context["source"], "N09")
    if actual:
        need(Path(build["source_root"]) / PREFIX.rstrip("/") == C1
             and Path(context["binding_validator"]) == C1 / "binding.py"
             and context["binding_validator_sha256"] == capture["files"][PREFIX + "binding.py"]["sha256"]
             and context["tool_root"] == build["tool"]["root"]
             and context["runtime_root"] == build["runtime"]["root"], "fixed-roots")
        binding.verify_build(build, capture_raw, context["source"], "N09", deadline=deadline)
    return capture_raw, build_raw, capture, build


def validate_evidence(context, evidence_raw, *, capture_raw, build_raw, archive_raw, deadline):
    """Repeat the original data predicates using the actual retained USTAR bytes.

    This parser performs no launch. Its mandatory caller additionally rereads the
    original command records and both streams via verify_retained_evidence.
    """
    need(binding.digest(capture_raw) == context["capture_sha256"]
         and binding.digest(build_raw) == context["build_receipt_sha256"], "evidence-inputs")
    build = binding.decode(build_raw)
    capture = binding.validate_build(build, capture_raw, context["source"], "N09")
    value = binding.decode(evidence_raw)
    binding.exact(value, ("schema", "source", "selector", "generation", "workflow_identity",
        "source_capture_sha256", "build_receipt_sha256", "procedure_sha256", "base_revision",
        "archive_sha256", "archive_bytes", "commands"), "n09-evidence-fields")
    need(value["schema"] == "issue779-checkpoint1-n09-evidence-v1" and value["source"] == context["source"]
         and value["selector"] == "N09" and value["generation"] == context["generation"]
         and value["workflow_identity"] == context["workflow_identity"]
         and value["source_capture_sha256"] == binding.digest(capture_raw)
         and value["build_receipt_sha256"] == binding.digest(build_raw)
         and value["procedure_sha256"] == capture["files"][PREFIX + LOCAL + SHELL]["sha256"]
         and type(value["base_revision"]) is str and re.fullmatch("[0-9a-f]{40}", value["base_revision"]), "evidence-identity")
    need(type(archive_raw) is bytes and binding.integer(value["archive_bytes"], 1, MAX_ARCHIVE)
         and value["archive_bytes"] == len(archive_raw)
         and value["archive_sha256"] == binding.digest(archive_raw), "archive-binding")
    utilities = load("procedures/slot/bridge.py", capture, deadline=deadline)
    utilities.validate_commands(value["commands"])
    records = load(LOCAL + "records.py", capture, deadline=deadline)
    parser = load(LOCAL + "check_n09_records.py", capture, deadline=deadline)
    records.validate(archive_raw, source=context["source"], base=value["base_revision"],
        entry_sha=build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"], parser=parser)
    return {"case": "N09", "passed": True, "skipped": False,
            "procedure_sha256": value["procedure_sha256"], "evidence_sha256": binding.digest(evidence_raw)}


def verify_retained_evidence(context, *, deadline):
    """Reopen actual archive, command metadata and both stream files; start nothing."""
    capture_raw, build_raw, capture, _ = inputs(context, deadline=deadline, actual=False)
    output = Path(context["output"])
    raw = binding.read(output / "N09-evidence.json", deadline=deadline)
    value = binding.decode(raw)
    utilities = load("procedures/slot/bridge.py", capture, deadline=deadline)
    utilities.verify_command_logs(output, value["commands"], deadline=deadline)
    archive = binding.read(output / "native-private.tar", MAX_ARCHIVE, deadline=deadline)
    return validate_evidence(context, raw, capture_raw=capture_raw, build_raw=build_raw,
                             archive_raw=archive, deadline=deadline)


def execute(context, *, deadline):
    """Run the original retained-slot attack and require physical cleanup records."""
    need(os.geteuid() == os.getegid() == 0 and os.uname().sysname == "Linux", "root-linux-required")
    need(850 < deadline - time.monotonic() <= 900, "original-outer-budget")
    os.umask(0o077)
    capture_raw, build_raw, capture, build = inputs(context, deadline=deadline, actual=True)
    output = Path(context["output"])
    info = output.lstat()
    need(output.resolve(strict=True) == output and stat.S_ISDIR(info.st_mode)
         and info.st_uid == info.st_gid == 0 and stat.S_IMODE(info.st_mode) == 0o700
         and set(os.listdir(output)) == INPUTS, "dispatcher-output")
    initial = {}
    for name in INPUTS:
        info = (output / name).lstat()
        need(stat.S_ISREG(info.st_mode) and info.st_uid == info.st_gid == 0
             and stat.S_IMODE(info.st_mode) == 0o600 and info.st_nlink == 1, "dispatcher-input")
        initial[name] = binding.read(output / name, deadline=deadline)
    need(initial["source.json"] == capture_raw and initial["build-receipt.json"] == build_raw,
         "dispatcher-input-copy")
    plan, execution = binding.decode(initial["plan.json"]), binding.decode(initial["execution.json"])
    need(plan["source"] == context["source"] and plan["selector"] == "N09"
         and plan["source_capture_sha256"] == context["capture_sha256"]
         and plan["build_receipt_sha256"] == context["build_receipt_sha256"]
         and plan["procedure_inventory_sha256"] == binding.digest(initial["procedure-inventory.json"])
         and execution == {"generation": context["generation"], "workflow_identity": context["workflow_identity"]},
         "dispatcher-plan")
    owner = load("procedures/owner-death/adapter.py", capture, deadline=deadline)
    prepared = owner.prepare_verified_inputs(context, binding, capture, build, capture_raw, build_raw,
                                              deadline=deadline, source_sealed=False)
    control, retained = prepared["control"], prepared["retained"]
    reviewed = retained / "reviewed"
    reviewed.mkdir(mode=0o700)
    for name in (SHELL, "dispatch.sh", "retain-native.sh", *PARSERS):
        raw = binding.read(HERE / name, 131072, deadline=deadline)
        need(binding.digest(raw) == capture["files"][PREFIX + LOCAL + name]["sha256"], "procedure-binding")
        owner.save(reviewed / name, raw)
        os.chmod(reviewed / name, 0o500 if name == "retain-native.sh" else 0o444)
    pins = "".join(name + "\t" + capture["files"][PREFIX + LOCAL + name]["sha256"] + "\n" for name in PARSERS[:3])
    owner.save(reviewed / "projection-parser-pins.tsv", pins.encode())
    os.chmod(reviewed / "projection-parser-pins.tsv", 0o444)
    audit_module = load("procedures/os_audit.py", capture, deadline=deadline)
    empty_helper = audit_module.create_empty_helper(retained / "empty-audit-helper", deadline=deadline)
    auditor = audit_module.Audit(min(deadline - 5, time.monotonic() + 60))
    auditor.initialize_tools()
    audit = auditor.build(str(prepared["roots"]["runtime"]), str(prepared["roots"]["tool"]), False, str(empty_helper))
    owner.save(control / "os-audit.json", audit)
    parent = subprocess.run(["git", "--no-optional-locks", "-c", "core.fsmonitor=false",
        "-c", "safe.directory=" + build["source_root"], "rev-parse", context["source"] + "^"], cwd=build["source_root"],
        stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        timeout=min(15, deadline - time.monotonic()), check=False)
    need(parent.returncode == 0 and re.fullmatch(b"[0-9a-f]{40}\n", parent.stdout), "current-parent")
    base = parent.stdout.decode().strip()
    actor = Path(build["source_root"]).lstat()
    uid, gid = actor.st_uid, actor.st_gid
    need(0 < uid < 2**32-1 and 0 < gid < 2**32-1 and pwd.getpwuid(uid).pw_gid == gid, "actual-n02-actor")
    procedure_sha = capture["files"][PREFIX + LOCAL + SHELL]["sha256"]
    fixture = ["--execute", "--reviewed-script-sha256", procedure_sha]
    for role, flag in (("source", "source"), ("tool", "payload"), ("runtime", "runtime")):
        map_path, node_path = control / (role + ".tsv"), control / (role + "-nodes.json")
        fixture += ["--" + flag + "-root", str(prepared["roots"][role]),
            "--" + flag + "-manifest", str(map_path), "--" + flag + "-manifest-sha256", binding.digest(binding.read(map_path, deadline=deadline)),
            "--" + flag + "-nodes", str(node_path), "--" + flag + "-nodes-sha256", binding.digest(binding.read(node_path, deadline=deadline))]
    fixture += ["--source-review", str(control / "source.json"), "--source-review-sha256", context["capture_sha256"],
        "--source-capture", str(control / "source.json"), "--source-capture-sha256", context["capture_sha256"],
        "--build-receipt", str(control / "build-receipt.json"), "--build-receipt-sha256", context["build_receipt_sha256"],
        "--binding-validator", str(control / "binding.py"), "--binding-validator-sha256", context["binding_validator_sha256"],
        "--binding-deadline-monotonic", repr(deadline), "--os-audit", str(control / "os-audit.json"), "--os-audit-sha256", binding.digest(audit),
        "--source-revision", context["source"], "--base-revision", base, "--workflow-identity", context["workflow_identity"],
        "--entry", "ForgeTrust.AppSurface.Cli.dll", "--entry-sha256", build["tool"]["files"]["ForgeTrust.AppSurface.Cli.dll"]["sha256"],
        "--runtime-host", "dotnet", "--n02-uid", str(uid), "--n02-gid", str(gid),
        "--n09-parser-root", str(reviewed), "--n09-parser-sha256", capture["files"][PREFIX + LOCAL + PARSERS[3]]["sha256"],
        "--projection-parser-root", str(reviewed), "--projection-parser-pins", str(reviewed / "projection-parser-pins.tsv")]
    need(deadline - time.monotonic() > 665, "original-fixture-retention-reserve")
    native = load("procedures/native_process.py", capture, deadline=deadline)
    commands = output / "commands"
    commands.mkdir(mode=0o700)
    runner = native.Runner(commands, deadline)
    original_error, archive = None, None
    try:
        runner.phase = "actual-csharp-n09"
        dispatch = binding.read(reviewed / "dispatch.sh", 131072, deadline=deadline).decode()
        code, _ = runner.run(native.ROOT_PREFIX + ["-c", dispatch, "--", context["generation"], procedure_sha] + fixture,
                             min(deadline - 35, time.monotonic() + 630), allow_failure=True)
        need(code == 0, "native-fixture-exit")
    except BaseException as error:
        original_error = error
    try:
        runner.phase = "bounded-private-retention"
        end = min(deadline - 5, time.monotonic() + 30)
        _, marker = runner.run(native.ROOT_PREFIX + [str(reviewed / "retain-native.sh"), "--execute",
            "--generation", context["generation"], "--deadline-monotonic-ms", native.root_deadline(end),
            "--reviewed-script-sha256", capture["files"][PREFIX + LOCAL + "retain-native.sh"]["sha256"]], end)
        need(marker.startswith(b"NATIVE_RETENTION_COMPLETED:DATA_ONLY:FILES="), "retention-marker")
        archive = binding.read(control / "native.tar", MAX_ARCHIVE, deadline=end)
        utilities = load("procedures/slot/bridge.py", capture, deadline=deadline)
        utilities.save_archive(output / "native-private.tar", archive, deadline=deadline)
    except BaseException:
        if original_error is None:
            raise
    if original_error is not None:
        raise original_error
    need(archive is not None, "retention-missing")
    value = {"schema": "issue779-checkpoint1-n09-evidence-v1", "source": context["source"], "selector": "N09",
        "generation": context["generation"], "workflow_identity": context["workflow_identity"],
        "source_capture_sha256": context["capture_sha256"], "build_receipt_sha256": context["build_receipt_sha256"],
        "base_revision": base, "procedure_sha256": procedure_sha, "archive_sha256": binding.digest(archive),
        "archive_bytes": len(archive), "commands": runner.records}
    raw = binding.canonical(value)
    result = validate_evidence(context, raw, capture_raw=capture_raw, build_raw=build_raw, archive_raw=archive, deadline=deadline)
    owner.save(output / "N09-evidence.json", raw)
    return result
