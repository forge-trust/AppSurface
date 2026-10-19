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
import os
from pathlib import Path
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
        category = str(error) if isinstance(error, binding.Rejected) else "input-or-verification-failure"
        print(json.dumps({"schema": "issue779-checkpoint1-driver-failure-v1", "category": category,
                          "exit": 1, "native_acceptance": False, "coverage_credit": False}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
