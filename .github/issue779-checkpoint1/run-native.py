#!/usr/bin/env python3
"""C1 preparation, fixed native dispatch and exact retained-result reconciliation.

The complete current source and procedure inventory bind every source-owned
adapter. Availability permits an actual run, never a successful case by itself;
each selector still requires genuine execution and retained raw evidence.
"""

from __future__ import annotations

import argparse
import importlib.util
import re
import uuid
import types
import json
import math
import os
from pathlib import Path
import stat
import subprocess
import sys
import time

import binding


HERE = Path(__file__).resolve().parent
PLAN_SCHEMA = "issue779-checkpoint1-plan-v1"
PREPARATION_SECONDS = 120
RECONCILIATION_SECONDS = 120
# Fixed source-owned dispatch. A row is usable only when its complete port is
# present and hash-bound by the procedure inventory. No input chooses a module.
ADAPTERS = {
    "None": "slot/adapter.py", **{f"N{i:02d}": "slot/adapter.py" for i in range(4, 9)},
    "N09": "n09/adapter.py", "N10": "n10/adapter.py", "N11": "n11/adapter.py",
    "N12": "n12/adapter.py", "N13": "owner-death/adapter.py",
    "N14": "owner-death/adapter.py", "N15": "n15/adapter.py", "N16": "n16/adapter.py",
}
RUN_SECONDS = 900
WORKFLOW = re.compile(r"github-actions-[1-9][0-9]*-[1-9][0-9]*\Z")
AUDIT_CATEGORIES = frozenset((
    "ELF-row-bound", "OS-before-after", "OS-node-before-after", "additional-helper-count",
    "additional-helper-overlap", "alias-before-after", "alias-changed", "alias-integer-bound",
    "alias-link-bound", "alias-metadata", "alias-root-escape", "alias-row-bound", "alias-step-bound",
    "alias-target", "audit-byte-bound", "cache-format", "cache-row-bound", "cache-soname",
    "command-deadline", "command-output-bound", "command-reserve", "deadline", "deadline-input",
    "dependency-row-bound", "dependency-shape", "dependency-unresolved-or-ambiguous",
    "deployment-before-after", "deployment-link", "directory-changed", "file-bound-or-shape",
    "file-changed", "file-growth", "file-hash-changed", "file-open-substitution", "file-row-bound",
    "identity-changed", "interpreter-shape", "metadata-command-failed", "named-file-changed",
    "node-bound", "os-file-shape", "os-metadata", "os-parent", "os-tool-not-ELF",
    "output-ancestor", "output-metadata", "output-parent-changed", "output-parent-link",
    "output-parent-substitution", "output-private-parent", "output-write", "path-shape",
    "python-alternate-bootstrap", "python-stdlib-before-after", "python-stdlib-byte-bound",
    "python-stdlib-directory-changed", "python-stdlib-empty", "python-stdlib-node-bound",
    "python-stdlib-node-kind", "reap-deadline", "required-os-tool-missing", "runpath-directory",
    "runpath-directory-missing", "runpath-shape", "sealed-directory", "sealed-file", "sealed-root",
    "tree-byte-bound", "tree-node-bound", "tree-special-node", "unsupported-ELF-ABI",
    "unsupported-ELF-header", "unsupported-RUNPATH", "unsupported-cache-abi",
    "unsupported-inherited-RPATH", "unsupported-loader-feature",
))
VERIFICATION_CATEGORIES = frozenset((
    "native-linux-root-required", "workflow-identity", "output-already-exists", "output-parent-alias",
    "source-git", "source-git-identity", "source-not-clean", "source-file", "source-complete-set",
    "read-node", "read-open-substitution", "read-growth", "read-final-substitution",
    "verification-deadline", "command-log-binding", "command-log-diagnostic",
    "image-file-binding", "image-inventory", "plan-binding", "native-run-deadline",
    "native-retained-evidence", "aggregate-retained-evidence",
))


def failure_diagnostic(error):
    """Project a failure to fixed categories and a source-owned stage, never raw text.

    A traceback is inspected only for exact fixed source filenames and function
    names; neither filenames nor stack text are returned. Audit rejection text is
    usable only for its fixed error class, trusted source frame and allowlist.
    OS errno is a direct bounded integer, without filenames or nested exceptions.
    This record cannot turn an unsuccessful run into a native result.
    """
    frames = []
    trace = error.__traceback__
    while trace is not None and len(frames) < 64:
        code = trace.tb_frame.f_code
        frames.append((code.co_filename, code.co_name))
        trace = trace.tb_next
    audit_path = str(HERE / "procedures/os_audit.py")
    audit = any(path == audit_path for path, _ in frames)
    stage = "driver"
    for path, function in frames:
        if path == str(HERE / "run-native.py"):
            stage = {"prepare": "plan-preparation", "run": "native-dispatch",
                     "aggregate": "reconciliation"}.get(function, stage)
        elif path == str(HERE / "procedures/owner-death/adapter.py"):
            stage = {"_context": "adapter-inputs", "prepare_verified_inputs": "input-staging",
                     "_copy_tree": "input-staging", "_copy_file": "input-staging",
                     "materialize": "fixture-materialization", "run_bounded": "native-dispatch"}.get(function, stage)
        elif path == str(HERE / "procedures/slot/helpers.py"):
            stage = "helper-preparation"
        elif path == audit_path:
            stage = {"initialize_tools": "audit-initialize", "build": "audit-build",
                     "inventory": "audit-inventory"}.get(function, stage)
    kind = "input"
    category = "input-or-verification-failure"
    if isinstance(error, (TimeoutError, subprocess.TimeoutExpired)):
        kind, category = "timeout", "native-deadline"
    elif isinstance(error, OSError):
        kind, category = "os", "native-os-error"
    elif isinstance(error, subprocess.SubprocessError):
        kind, category = "process", "native-process-error"
    elif audit and type(error).__name__ == "AuditError":
        kind, category = "audit", "audit-rejected"
        if len(error.args) == 1 and type(error.args[0]) is str and error.args[0] in AUDIT_CATEGORIES:
            category = "audit-" + error.args[0]
    elif isinstance(error, binding.Rejected) and len(error.args) == 1 and type(error.args[0]) is str and error.args[0] in VERIFICATION_CATEGORIES:
        category = error.args[0]
    number = getattr(error, "errno", None) if isinstance(error, OSError) else None
    return {"schema": "issue779-checkpoint1-driver-failure-v1", "category": category,
            "stage": stage, "error_kind": kind,
            "errno": number if type(number) is int and 1 <= number <= 4095 else None,
            "exit": 1, "native_acceptance": False, "coverage_credit": False}


def retain_dependency_failure(error, output):
    """Best-effort bounded private context, on the audit's original deadline.

    Only the original dependency rejection with a fixed audit source frame can
    supply these bytes. The exact closed context is copied without rewriting to
    a fresh os-audit-dependency.json under the caller's canonical owned0700
    output. Its original library paths stay private, never in failure stdout.
    This ordinary-file copy grants no authority. Missing context, invalid data,
    expiry, substitution, an occupied leaf or any I/O/close error returns false;
    partial failed files remain private and are never adopted or overwritten.
    """
    parent_fd = leaf_fd = None
    success = False
    try:
        if (type(error).__name__ != "AuditError" or error.args != ("dependency-unresolved-or-ambiguous",)
                or failure_diagnostic(error)["category"] != "audit-dependency-unresolved-or-ambiguous"):
            return False
        raw = getattr(error, "failure_context", None)
        deadline = getattr(error, "failure_deadline", None)
        if (type(raw) is not bytes or not 0 < len(raw) <= 32768
                or type(deadline) not in (int, float) or not math.isfinite(deadline)):
            return False
        binding._remaining(deadline)
        value = binding.decode(raw)
        binding.exact(value, ("schema", "category", "requester", "soname", "candidate_count",
            "runpath_count", "cache_count", "truncated", "runpath_candidates",
            "cache_candidates", "combined_candidates"), "dependency-context-fields")
        binding.require(value["schema"] == "issue779-os-audit-dependency-failure-context-v1"
            and value["category"] == "dependency-unresolved-or-ambiguous"
            and type(value["soname"]) is str
            and re.fullmatch(r"[A-Za-z0-9_.+-]{1,256}", value["soname"])
            and type(value["truncated"]) is bool, "dependency-context-shape")
        binding.absolute(value["requester"])
        for count, candidates in (("candidate_count", "combined_candidates"),
                                  ("runpath_count", "runpath_candidates"),
                                  ("cache_count", "cache_candidates")):
            binding.require(binding.integer(value[count], 0, 4096)
                and type(value[candidates]) is list
                and len(value[candidates]) == min(value[count], 8), "dependency-context-candidates")
            for path in value[candidates]:
                binding.absolute(path)
        binding.require(value["candidate_count"] != 1 and value["truncated"] == any(
            value[name] > 8 for name in ("candidate_count", "runpath_count", "cache_count")),
            "dependency-context-counts")
        output = binding.absolute(str(output))
        binding.require(output.resolve(strict=True) == output, "dependency-output-path")
        before = output.lstat()
        binding.require(stat.S_ISDIR(before.st_mode) and stat.S_IMODE(before.st_mode) == 0o700
            and before.st_uid == os.geteuid() and before.st_gid == os.getegid(), "dependency-output-owner")
        binding._remaining(deadline)
        parent_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        binding.require(binding._identity(before) == binding._identity(os.fstat(parent_fd)),
                        "dependency-output-substitution")
        leaf = "os-audit-dependency.json"
        leaf_fd = os.open(leaf, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                          0o600, dir_fd=parent_fd)
        pending = memoryview(raw)
        while pending:
            binding._remaining(deadline)
            count = os.write(leaf_fd, pending)
            binding.require(count > 0, "dependency-output-write")
            pending = pending[count:]
        os.fsync(leaf_fd)
        binding._remaining(deadline)
        opened = os.fstat(leaf_fd)
        binding.require(stat.S_ISREG(opened.st_mode) and stat.S_IMODE(opened.st_mode) == 0o600
            and opened.st_uid == os.geteuid() and opened.st_gid == os.getegid()
            and opened.st_nlink == 1 and opened.st_size == len(raw)
            and binding._identity(opened) == binding._identity(os.stat(leaf, dir_fd=parent_fd, follow_symlinks=False)),
            "dependency-output-leaf")
        binding.require(binding._identity(before)[:5] == binding._identity(os.fstat(parent_fd))[:5]
            and binding._identity(os.fstat(parent_fd)) == binding._identity(output.lstat()), "dependency-output-final")
        binding._remaining(deadline)
        success = True
    except Exception:
        # Diagnostic failure never changes the original unsuccessful native run.
        success = False
    finally:
        for fd in (leaf_fd, parent_fd):
            if fd is not None:
                try:
                    os.close(fd)
                except OSError:
                    success = False
    if success:
        try:
            binding._remaining(deadline)
        except Exception:
            success = False
    return success


def ready(selector, inventory):
    """Port availability is source metadata, never native success or authority."""
    path = "procedures/" + ADAPTERS[selector]
    return (inventory["ports"][selector]["native_ready"] is True
            and any(row.get("ported_path") == path for row in inventory["files"]))


def adapter(selector, inventory, *, deadline):
    binding.require(ready(selector, inventory), "native-procedure-port-incomplete")
    path = HERE / "procedures" / ADAPTERS[selector]
    spec = importlib.util.spec_from_file_location("c1_" + selector, path)
    binding.require(spec is not None and spec.loader is not None, "native-adapter-source")
    # Execute the exact retained bytes that were hash-checked, avoiding a second
    # pathname read by the import loader and suppressing bytecode writes.
    raw = binding.read(path, 131072, deadline=deadline)
    expected = [row["ported_sha256"] for row in inventory["files"] if row.get("ported_path") == "procedures/" + ADAPTERS[selector]]
    binding.require(expected == [binding.digest(raw)], "native-adapter-binding")
    module = types.ModuleType("c1_" + selector)
    module.__file__ = str(path)
    exec(compile(raw, str(path), "exec", dont_inherit=True), module.__dict__)
    binding.require(callable(getattr(module, "execute", None))
                    and callable(getattr(module, "verify_retained_evidence", None)), "native-adapter-api")
    return module


def save(path, raw):
    """Publish one bounded new private file; never adopt/overwrite an old result."""
    binding.require(type(raw) is bytes and len(raw) <= binding.MAX_JSON, "output-bytes")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    try:
        pending = memoryview(raw)
        while pending:
            written = os.write(fd, pending)
            binding.require(written > 0, "output-short-write")
            pending = pending[written:]
        os.fsync(fd)
    finally:
        os.close(fd)


def fresh_output(path):
    path = binding.absolute(str(path))
    binding.require(not os.path.lexists(path), "output-already-exists")
    # Parent creation is an explicit caller responsibility. No recursive mkdir
    # follows a supplied chain on behalf of the caller.
    binding.require(path.parent.resolve(strict=True) == path.parent, "output-parent-alias")
    path.mkdir(mode=0o700)
    os.chmod(path, 0o700)
    return path


def inventory_raw(deadline):
    path = HERE / "procedures/inventory.json"
    raw = binding.read(path, deadline=deadline)
    value = binding.decode(raw)
    binding.exact(value, ("schema", "historical_harness", "historical_product_source", "selectors",
                           "ports", "files", "native_execution_ready"), "procedure-inventory-fields")
    binding.require(value["schema"] == "issue779-checkpoint1-procedure-inventory-v1"
                    and value["selectors"] == list(binding.SELECTORS)
                    and type(value["ports"]) is dict and set(value["ports"]) == set(binding.SELECTORS)
                    and type(value["files"]) is list, "procedure-inventory-shape")
    for selector, row in value["ports"].items():
        binding.exact(row, ("cases", "native_ready", "procedures"), "procedure-port-fields")
        binding.require(row["cases"] == list(binding.CASE_SETS[selector])
                        and type(row["native_ready"]) is bool and type(row["procedures"]) is list,
                        "procedure-port-cases")
    copied = set()
    for row in value["files"]:
        binding.require(type(row) is dict and type(row.get("sha256")) is str
                        and binding.HEX64.fullmatch(row["sha256"])
                        and binding.integer(row.get("bytes"), 0, binding.MAX_JSON), "procedure-file-row")
        path = row.get("ported_path")
        if path is None:
            continue
        binding.relative(path)
        binding.require(path.startswith("procedures/") and path not in copied
                        and type(row.get("ported_sha256")) is str
                        and binding.HEX64.fullmatch(row["ported_sha256"]), "procedure-file-path")
        copied.add(path)
        actual = HERE / path
        binding.require(actual.resolve(strict=True) == actual, "procedure-file-alias")
        body = binding.read(actual, deadline=deadline)
        binding.require(binding.digest(body) == row["ported_sha256"], "procedure-file-binding")
    # An incomplete port is a normal preparation observation, never a pass.
    binding.require(type(value["native_execution_ready"]) is bool
                    and value["native_execution_ready"] == all(ready(s, value) for s in binding.SELECTORS),
                    "procedure-port-state")
    return raw, value


def validate_plan(plan, capture_raw, build_raw, inventory_bytes):
    binding.exact(plan, ("schema", "source", "selector", "cases", "source_capture_sha256",
                         "build_receipt_sha256", "tool_map_sha256", "runtime_map_sha256",
                         "procedure_inventory_sha256", "ready_for_native", "native_execution"), "plan-fields")
    build = binding.decode(build_raw)
    binding.validate_build(build, capture_raw, plan["source"], plan["selector"])
    procedure_inventory = binding.decode(inventory_bytes)
    binding.require(plan["schema"] == PLAN_SCHEMA
                    and plan["cases"] == list(binding.CASE_SETS[plan["selector"]])
                    and plan["source_capture_sha256"] == binding.digest(capture_raw)
                    and plan["build_receipt_sha256"] == binding.digest(build_raw)
                    and plan["tool_map_sha256"] == binding.digest(binding.canonical(build["tool"]))
                    and plan["runtime_map_sha256"] == binding.digest(binding.canonical(build["runtime"]))
                    and plan["procedure_inventory_sha256"] == binding.digest(inventory_bytes)
                    and plan["native_execution"] is False
                    and plan["ready_for_native"] is ready(plan["selector"], procedure_inventory), "plan-binding")
    return build


def prepare(args):
    """Verify real publication inputs and publish a plan, without starting native work."""
    end = time.monotonic() + PREPARATION_SECONDS
    capture_raw = binding.read(args.source_capture, deadline=end)
    build_raw = binding.read(args.build_receipt, deadline=end)
    build = binding.decode(build_raw)
    binding.verify_build(build, capture_raw, args.source, args.selector, deadline=end)
    binding.require(build["tool"]["root"] == str(args.tool)
                    and build["runtime"]["root"] == str(args.runtime), "prepare-selected-roots")
    inventory_bytes, procedure_inventory = inventory_raw(end)
    plan = {"schema": PLAN_SCHEMA, "source": args.source, "selector": args.selector,
            "cases": list(binding.CASE_SETS[args.selector]),
            "source_capture_sha256": binding.digest(capture_raw),
            "build_receipt_sha256": binding.digest(build_raw),
            "tool_map_sha256": binding.digest(binding.canonical(build["tool"])),
            "runtime_map_sha256": binding.digest(binding.canonical(build["runtime"])),
            "procedure_inventory_sha256": binding.digest(inventory_bytes),
            "ready_for_native": ready(args.selector, procedure_inventory), "native_execution": False}
    validate_plan(plan, capture_raw, build_raw, inventory_bytes)
    binding.require(time.monotonic() < end, "prepare-deadline")
    destination = fresh_output(args.output)
    save(destination / "source.json", capture_raw)
    save(destination / "build-receipt.json", build_raw)
    save(destination / "procedure-inventory.json", inventory_bytes)
    save(destination / "plan.json", binding.canonical(plan))
    return {"schema": PLAN_SCHEMA, "selector": args.selector,
            "ready_for_native": ready(args.selector, procedure_inventory), "native_execution": False}


def load_plan(path, *, deadline):
    path = binding.absolute(str(path))
    binding.require(path.name == "plan.json", "plan-filename")
    raw = binding.read(path, deadline=deadline)
    capture_raw = binding.read(path.parent / "source.json", deadline=deadline)
    build_raw = binding.read(path.parent / "build-receipt.json", deadline=deadline)
    supplied_inventory = binding.read(path.parent / "procedure-inventory.json", deadline=deadline)
    actual_inventory, _ = inventory_raw(deadline)
    binding.require(supplied_inventory == actual_inventory, "plan-procedure-source")
    plan = binding.decode(raw)
    build = validate_plan(plan, capture_raw, build_raw, actual_inventory)
    return raw, capture_raw, build_raw, plan, build


def context(directory, plan, build, execution, *, deadline):
    """Fixed data context shared with source-owned native procedure adapters."""
    binding.exact(execution, ("workflow_identity", "generation"), "execution-fields")
    binding.require(type(execution["workflow_identity"]) is str
                    and WORKFLOW.fullmatch(execution["workflow_identity"])
                    and type(execution["generation"]) is str
                    and re.fullmatch(r"[0-9a-f]{32}", execution["generation"]), "execution-identity")
    return {"source": plan["source"], "selector": plan["selector"],
            "capture_path": str(directory / "source.json"),
            "capture_sha256": plan["source_capture_sha256"],
            "build_receipt_path": str(directory / "build-receipt.json"),
            "build_receipt_sha256": plan["build_receipt_sha256"],
            "tool_root": build["tool"]["root"], "runtime_root": build["runtime"]["root"],
            "output": str(directory), "generation": execution["generation"],
            "binding_validator": str(HERE / "binding.py"),
            "binding_validator_sha256": binding.decode(binding.read(directory / "source.json", deadline=deadline))["files"][".github/issue779-checkpoint1/binding.py"]["sha256"],
            "workflow_identity": execution["workflow_identity"]}


def run(args):
    """Dispatch one fixed selector as Linux root under one original 900s clock.

    The parent workflow owns sudo. Each adapter owns native stop/join, original
    product clocks and actual evidence validation. Failure never publishes a pass.
    """
    end = time.monotonic() + RUN_SECONDS
    os.umask(0o077)
    binding.require(sys.platform == "linux" and os.geteuid() == 0, "native-linux-root-required")
    binding.require(type(args.workflow_identity) is str and WORKFLOW.fullmatch(args.workflow_identity),
                    "workflow-identity")
    plan_raw, capture_raw, build_raw, plan, build = load_plan(args.plan, deadline=end)
    inventory_bytes, inventory = inventory_raw(end)
    binding.verify_build(build, capture_raw, plan["source"], plan["selector"], deadline=end)
    selected = adapter(plan["selector"], inventory, deadline=end)
    destination = fresh_output(args.output)
    for name, raw in (("plan.json", plan_raw), ("source.json", capture_raw),
                      ("build-receipt.json", build_raw), ("procedure-inventory.json", inventory_bytes)):
        save(destination / name, raw)
    execution = {"workflow_identity": args.workflow_identity, "generation": uuid.uuid4().hex}
    save(destination / "execution.json", binding.canonical(execution))
    supplied = context(destination, plan, build, execution, deadline=end)
    actual = selected.execute(supplied, deadline=end)
    rows = actual if type(actual) is list else [actual]
    result = {"schema": "issue779-checkpoint1-result-v1", "source": plan["source"],
              "source_capture_sha256": plan["source_capture_sha256"], "selector": plan["selector"],
              "build_receipt_sha256": binding.digest(build_raw), "plan_sha256": binding.digest(plan_raw),
              "exit": 0, "skipped": 0, "cases": rows, "native_acceptance": False}
    binding.validate_result(result, capture_raw)
    for row in rows:
        evidence = binding.read(destination / (row["case"] + "-evidence.json"), deadline=end)
        binding.require(binding.digest(evidence) == row["evidence_sha256"], "native-evidence-hash")
    verified = selected.verify_retained_evidence(supplied, deadline=end)
    binding.require((verified if type(verified) is list else [verified]) == rows, "native-retained-evidence")
    binding.require(time.monotonic() < end, "native-run-deadline")
    save(destination / "result.json", binding.canonical(result))
    return result


def aggregate(args):
    """Reconcile an exact current run only after actual native procedure support exists.

    No preparation file or detached case model can pass this CLI. The pure
    aggregate grammar remains usable in data-only tests during the port.
    """
    end = time.monotonic() + RECONCILIATION_SECONDS
    capture_raw = binding.read(args.source_capture, deadline=end)
    actual_inventory, procedure_inventory = inventory_raw(end)
    capture = binding.decode(capture_raw)
    # Aggregation has no build/run staging roots from other jobs. Recheck the
    # current source itself before loading the fixed source-owned data parsers.
    binding.verify_source(capture, HERE.parents[1], deadline=end)
    binding.require(all(ready(s, procedure_inventory) for s in binding.SELECTORS), "native-procedure-port-incomplete")
    binding.require(len(args.receipts) == len(binding.SELECTORS), "aggregate-input-count")
    results = []
    for directory in args.receipts:
        directory = binding.absolute(str(directory))
        raw = binding.read(directory / "result.json", deadline=end)
        result = binding.decode(raw)
        binding.validate_result(result, capture_raw)
        plan_raw, prior_capture, build_raw, plan, _ = load_plan(directory / "plan.json", deadline=end)
        binding.require(prior_capture == capture_raw and plan["selector"] == result["selector"]
                        and result["plan_sha256"] == binding.digest(plan_raw)
                        and result["build_receipt_sha256"] == binding.digest(build_raw)
                        and plan["procedure_inventory_sha256"] == binding.digest(actual_inventory),
                        "aggregate-input-binding")
        execution = binding.decode(binding.read(directory / "execution.json", deadline=end))
        supplied = context(directory, plan, binding.decode(build_raw), execution, deadline=end)
        selected = adapter(plan["selector"], procedure_inventory, deadline=end)
        for row in result["cases"]:
            evidence = binding.read(directory / (row["case"] + "-evidence.json"), deadline=end)
            binding.require(binding.digest(evidence) == row["evidence_sha256"], "aggregate-evidence-hash")

        verified = selected.verify_retained_evidence(supplied, deadline=end)
        binding.require((verified if type(verified) is list else [verified]) == result["cases"], "aggregate-retained-evidence")
        results.append(result)
    summary = binding.aggregate(results, capture_raw)
    binding.require(time.monotonic() < end, "aggregate-deadline")
    destination = fresh_output(args.output)
    save(destination / "aggregate.json", binding.canonical(summary))
    return summary


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)
    first = commands.add_parser("prepare")
    first.add_argument("--source-capture", type=Path, required=True)
    first.add_argument("--source", required=True)
    first.add_argument("--selector", choices=binding.SELECTORS, required=True)
    first.add_argument("--tool", type=Path, required=True)
    first.add_argument("--runtime", type=Path, required=True)
    first.add_argument("--build-receipt", type=Path, required=True)
    first.add_argument("--output", type=Path, required=True)
    first.set_defaults(action=prepare)
    second = commands.add_parser("run")
    second.add_argument("--plan", type=Path, required=True)
    second.add_argument("--output", type=Path, required=True)
    second.add_argument("--workflow-identity", required=True)
    second.set_defaults(action=run)
    last = commands.add_parser("aggregate")
    last.add_argument("--source-capture", type=Path, required=True)
    last.add_argument("--receipts", type=Path, nargs="+", required=True)
    last.add_argument("--output", type=Path, required=True)
    last.set_defaults(action=aggregate)
    return result


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        value = args.action(args)
        print(json.dumps(value, sort_keys=True))
        return 0
    except Exception as error:
        # No raw path, source text, shell output or private process prefix.
        if getattr(args, "command", None) == "run":
            retain_dependency_failure(error, args.output)
        print(json.dumps(failure_diagnostic(error), sort_keys=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
