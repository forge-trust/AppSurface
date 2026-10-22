#!/usr/bin/env python3
"""Bind, execute and finalize one genuine C1 standard Coverlet measurement.

Only the external pinned collector instruments, restores and merges counters.
This fixture verifies data and actual retained files; it grants no native or
coverage acceptance. The unchanged native driver owns its real procedures.
"""

from __future__ import annotations

import argparse
import copy
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import time
import xml.etree.ElementTree as ET

import binding


HERE = Path(__file__).resolve().parent
SOURCE_ROOT = HERE.parents[1]
REPORT_LIMIT = 32 * 1024 * 1024
WORKFLOW = re.compile(r"github-actions-[1-9][0-9]*-[1-9][0-9]*\Z")
COMMON_PATHS = ("source_capture", "build_receipt", "collector", "evidence", "prior_report", "prior_binding")
BASE_FIELDS = ("schema", "source", "selector", "workflow_identity", "paths", "source_capture_sha256",
               "original_build_sha256", "original_tool", "collector_sha256", "prior_report_sha256",
               "prior_binding_sha256", "evidence_owner", "native_acceptance", "coverage_credit")


def require(condition, category):
    binding.require(condition, category)


def check(end):
    binding._remaining(end)


def identity(s):
    return binding._identity(s)


def parent_identity(s):
    return s.st_dev, s.st_ino, s.st_mode, s.st_uid, s.st_gid


def canonical_path(value):
    """Require an explicit absolute spelling; reads additionally retain parents."""
    path = binding.absolute(str(value))
    require(path.resolve(strict=True) == path, "measurement-path-alias")
    return path


def read(path, maximum, end):
    """Read one stable regular single-link leaf through no-follow parent FDs."""
    path = binding.absolute(str(path))
    check(end)
    fds = [os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)]
    parents = []
    try:
        for name in path.parts[1:-1]:
            owner = fds[-1]
            before = os.stat(name, dir_fd=owner, follow_symlinks=False)
            require(stat.S_ISDIR(before.st_mode), "measurement-parent")
            fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=owner)
            fds.append(fd)
            expected = parent_identity(before)
            require(parent_identity(os.fstat(fd)) == expected, "measurement-parent-open")
            parents.append((owner, name, fd, expected))
            check(end)
        owner = fds[-1]
        before = os.stat(path.name, dir_fd=owner, follow_symlinks=False)
        require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1
                and 0 <= before.st_size <= maximum, "measurement-leaf")
        fd = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK | os.O_CLOEXEC, dir_fd=owner)
        fds.append(fd)
        require(identity(os.fstat(fd)) == identity(before), "measurement-open")
        body = bytearray()
        while True:
            check(end)
            chunk = os.read(fd, min(65536, maximum + 1 - len(body)))
            if not chunk:
                break
            body.extend(chunk)
            require(len(body) <= before.st_size, "measurement-growth")
        require(len(body) == before.st_size and identity(before) == identity(os.fstat(fd))
                == identity(os.stat(path.name, dir_fd=owner, follow_symlinks=False)), "measurement-changed")
        for owner, name, fd, expected in parents:
            require(parent_identity(os.fstat(fd)) == expected
                    == parent_identity(os.stat(name, dir_fd=owner, follow_symlinks=False)), "measurement-parent-changed")
        check(end)
        return bytes(body)
    finally:
        for fd in reversed(fds):
            os.close(fd)


def save(path, value, end):
    """Publish a bounded exclusive 0600 fixture record, never overwrite evidence."""
    check(end)
    raw = binding.canonical(value)
    require(len(raw) <= binding.MAX_JSON, "measurement-record-bound")
    parent = canonical_path(path.parent)
    fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        before = os.fstat(fd)
        leaf = os.open(path.name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                       0o600, dir_fd=fd)
        with os.fdopen(leaf, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
            require(stat.S_IMODE(os.fstat(stream.fileno()).st_mode) == 0o600, "measurement-record-mode")
        require(parent_identity(before) == parent_identity(os.fstat(fd))
                == parent_identity(parent.lstat()), "measurement-record-parent")
    finally:
        os.close(fd)
    check(end)


def report_json(raw):
    """Validate bounded standard JSON structure and typed counters; never alter them."""
    require(type(raw) is bytes and 0 < len(raw) <= REPORT_LIMIT, "measurement-report-bound")
    def invalid(_):
        raise binding.Rejected("measurement-report-number")
    try:
        value = json.loads(raw.decode("utf-8", errors="strict"), object_pairs_hook=binding._pairs,
                           parse_constant=invalid)
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise binding.Rejected("measurement-report-encoding") from None
    expected = {m + ".dll" for m in binding.MEASUREMENT_MODULES}
    require(type(value) is dict and len(value) == 6
            and {Path(k).name for k in value} == expected, "measurement-report-modules")
    for documents in value.values():
        require(type(documents) is dict, "measurement-report-documents")
        for types in documents.values():
            require(type(types) is dict, "measurement-report-types")
            for methods in types.values():
                require(type(methods) is dict, "measurement-report-methods")
                for data in methods.values():
                    require(type(data) is dict and set(data) == {"Lines", "Branches"}
                            and type(data["Lines"]) is dict and type(data["Branches"]) is list,
                            "measurement-report-method")
                    for line, hits in data["Lines"].items():
                        require(line.isascii() and line.isdecimal() and 0 < int(line) < 2**31
                                and binding.integer(hits, 0, 2**63 - 1), "measurement-report-line")
                    for branch in data["Branches"]:
                        binding.exact(branch, ("Line", "Offset", "EndOffset", "Path", "Ordinal", "Hits"),
                                      "measurement-report-branch-fields")
                        require(all(binding.integer(n, 0, 2**63 - 1) for n in branch.values()),
                                "measurement-report-branch-number")
    return value


def report_xml(raw):
    """Validate collector XML without fetching a DTD or rewriting condition detail."""
    require(type(raw) is bytes and 0 < len(raw) <= REPORT_LIMIT, "measurement-xml-bound")
    require(b"\x00" not in raw and b"<!DOCTYPE" not in raw.upper()
            and b"<!ENTITY" not in raw.upper(), "measurement-xml-declaration")
    try:
        raw.decode("utf-8", errors="strict")
        value = ET.fromstring(raw)
    except (UnicodeError, ET.ParseError, RecursionError):
        raise binding.Rejected("measurement-xml-encoding") from None
    require(value.tag == "coverage" and value.find("packages") is not None, "measurement-xml-root")
    for key in ("lines-covered", "lines-valid", "branches-covered", "branches-valid"):
        number = value.get(key, "")
        require(number.isascii() and number.isdecimal() and int(number) < 2**63, "measurement-xml-counter")
    for line in value.iter("line"):
        for key in ("number", "hits"):
            number = line.get(key, "")
            require(number.isascii() and number.isdecimal() and int(number) < 2**63,
                    "measurement-xml-line")
    return value


def environment():
    """Use the same fixed native environment as the existing workflow."""
    return dict(item.split("=", 1) for item in binding.FIXED_ENVIRONMENT)


def inputs(args, end):
    require(sys.platform == "linux", "measurement-linux")
    require(args.selector in binding.SELECTORS and type(args.source) is str
            and binding.HEX40.fullmatch(args.source) and WORKFLOW.fullmatch(args.workflow_identity),
            "measurement-selection")
    paths = {key: str(canonical_path(getattr(args, key))) for key in COMMON_PATHS}
    require(paths["source_capture"] == str(args.evidence / "source.json")
            and paths["build_receipt"] == str(args.evidence / "build.json")
            and str(args.collector).endswith(binding.MEASUREMENT_COLLECTOR_SUFFIX), "measurement-fixed-input")
    require(args.prior_report != args.prior_binding
            and not binding.overlaps(args.evidence, args.prior_report)
            and not binding.overlaps(args.evidence, args.prior_binding), "measurement-prior-overlap")
    evidence = args.evidence.lstat()
    require(stat.S_ISDIR(evidence.st_mode) and stat.S_IMODE(evidence.st_mode) == 0o700,
            "measurement-evidence")
    capture_raw = read(args.source_capture, binding.MAX_JSON, end)
    original_raw = read(args.build_receipt, binding.MAX_JSON, end)
    original = binding.decode(original_raw)
    require(type(original) is dict and original.get("schema") == "issue779-checkpoint1-build-v1",
            "measurement-original-v1")
    binding.validate_build(original, capture_raw, args.source, args.selector)
    require(original["source_root"] == str(SOURCE_ROOT), "measurement-source-root")
    collector_raw = read(args.collector, REPORT_LIMIT, end)
    require(collector_raw.startswith(b"MZ"), "measurement-collector-image")
    prior_raw = read(args.prior_report, REPORT_LIMIT, end)
    prior_binding_raw = read(args.prior_binding, binding.MAX_JSON, end)
    # The parent owns semantic predecessor validation. This fixture requires
    # bounded regular JSON and rechecks the exact bytes at every phase.
    report_json(prior_raw)
    binding.decode(prior_binding_raw)
    baseline = {"schema": "issue779-c1-measurement-original-v1", "source": args.source,
                "selector": args.selector, "workflow_identity": args.workflow_identity, "paths": paths,
                "source_capture_sha256": binding.digest(capture_raw),
                "original_build_sha256": binding.digest(original_raw), "original_tool": original["tool"],
                "collector_sha256": binding.digest(collector_raw), "prior_report_sha256": binding.digest(prior_raw),
                "prior_binding_sha256": binding.digest(prior_binding_raw),
                "evidence_owner": {"uid": evidence.st_uid, "gid": evidence.st_gid},
                "native_acceptance": False, "coverage_credit": False}
    check(end)
    return baseline, original, capture_raw


def baseline(args, end):
    """Require literal unchanged common inputs and all predecessor/raw bindings."""
    current, original, capture_raw = inputs(args, end)
    saved_raw = read(args.evidence / "measurement-original.json", binding.MAX_JSON, end)
    saved = binding.decode(saved_raw)
    binding.exact(saved, BASE_FIELDS, "measurement-original-fields")
    require(saved == current, "measurement-original-changed")
    return saved, original, capture_raw, saved_raw


def bind(args):
    end = time.monotonic() + 240
    initial, original, capture_raw = inputs(args, end)
    require(args.evidence.lstat().st_uid == os.geteuid(), "measurement-evidence-owner")
    binding.verify_build(original, capture_raw, args.source, args.selector, deadline=end)
    for name in ("measurement-original.json", "build-measured.json", "measurement-target.json",
                 "measurement-binding.json", "plan", "native", "coverage.json", "coverage.cobertura.xml",
                 "measurement-prepare.log", "measurement-native.log"):
        require(not os.path.lexists(args.evidence / name), "measurement-fresh-output")
    # Bind only after verification, and re-read predecessors after the work.
    require(inputs(args, end)[0] == initial, "measurement-bind-changed")
    save(args.evidence / "measurement-original.json", initial, end)
    return initial


def measured_build(original, original_raw_sha, actual_tool, collector, collector_sha):
    """Construct only the existing strict v2 data grammar from an actual inventory."""
    value = copy.deepcopy(original)
    value["schema"] = "issue779-checkpoint1-build-v2"
    value["tool"] = actual_tool
    value["measurement"] = {"schema": "issue779-c1-measurement-build-v1",
        "original_build_path": str(Path(original["commands"][0]["log_path"]).parent / "build.json"),
        "original_build_sha256": original_raw_sha, "original_tool": original["tool"],
        "instrumented_modules": {name: actual_tool["files"][name]["sha256"]
                                  for name in sorted(binding.MEASUREMENT_INSTRUMENTED_NAMES)},
        "collector_path": str(collector), "collector_sha256": collector_sha}
    return value


def driver():
    spec = importlib.util.spec_from_file_location("c1_measurement_native_driver", HERE / "run-native.py")
    require(spec is not None and spec.loader is not None, "measurement-driver-loader")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def retained(args, measured_raw, capture_raw, end):
    """Verify actual retained plans, execution and row bytes after the unchanged driver."""
    plan_raw = read(args.evidence / "plan/plan.json", binding.MAX_JSON, end)
    plan = binding.decode(plan_raw)
    inventory_raw = read(HERE / "procedures/inventory.json", binding.MAX_JSON, end)
    driver().validate_plan(plan, capture_raw, measured_raw, inventory_raw)
    require(plan["source"] == args.source and plan["selector"] == args.selector
            and plan["ready_for_native"] is True, "measurement-plan-identity")
    for part in ("plan", "native"):
        for name, raw in (("source.json", capture_raw), ("build-receipt.json", measured_raw),
                          ("procedure-inventory.json", inventory_raw), ("plan.json", plan_raw)):
            require(read(args.evidence / part / name, binding.MAX_JSON, end) == raw,
                    "measurement-retained-input")
    native_raw = read(args.evidence / "native/result.json", binding.MAX_JSON, end)
    native = binding.validate_result(binding.decode(native_raw), capture_raw)
    require(native["selector"] == args.selector and native["plan_sha256"] == binding.digest(plan_raw)
            and native["build_receipt_sha256"] == binding.digest(measured_raw), "measurement-result-binding")
    execution_raw = read(args.evidence / "native/execution.json", binding.MAX_JSON, end)
    execution = binding.decode(execution_raw)
    binding.exact(execution, ("workflow_identity", "generation"), "measurement-execution-fields")
    require(execution["workflow_identity"] == args.workflow_identity
            and type(execution["generation"]) is str and re.fullmatch(r"[0-9a-f]{32}", execution["generation"]),
            "measurement-execution")
    for row in native["cases"]:
        raw = read(args.evidence / "native" / (row["case"] + "-evidence.json"), binding.MAX_JSON, end)
        require(binding.digest(raw) == row["evidence_sha256"], "measurement-case-evidence")
    check(end)
    return {"plan_sha256": binding.digest(plan_raw), "native_result_sha256": binding.digest(native_raw),
            "execution_sha256": binding.digest(execution_raw),
            "cases": [row["case"] for row in native["cases"]]}


def command_argv(args, original):
    """Fixed source-owned commands; no artifact supplies executable arguments."""
    common = ["/usr/bin/python3", "-B", str(HERE / "run-native.py")]
    return [common + ["prepare", "--source-capture", str(args.source_capture), "--source", args.source,
            "--selector", args.selector, "--tool", original["tool"]["root"],
            "--runtime", original["runtime"]["root"], "--build-receipt", str(args.evidence / "build-measured.json"),
            "--output", str(args.evidence / "plan")],
            common + ["run", "--plan", str(args.evidence / "plan/plan.json"),
            "--output", str(args.evidence / "native"), "--workflow-identity", args.workflow_identity]]


def command_deadline(end, phase, now):
    """Keep original command limits and reserve a minute for retained verification.

    Preparation must also leave the entire original 900-second native run. This
    is a wrapper scheduling check, not an extension of either procedure clock.
    """
    require(phase in ("prepare", "run"), "measurement-command-phase")
    allowance = 150 if phase == "prepare" else 900
    reserve = 60 + (900 if phase == "prepare" else 0)
    require(end - now >= allowance + reserve, "measurement-command-admission")
    return now + allowance


def target(args):
    # Preparation was a separate 150s command before the original 900s run.
    # Retain both allowances inside one finite target observation budget.
    end = time.monotonic() + 1150
    require(os.geteuid() == 0, "measurement-target-root")
    saved, original, capture_raw, saved_raw = baseline(args, end)
    tool = binding.inventory(Path(original["tool"]["root"]), original["tool"]["entry"], deadline=end)
    measured = measured_build(original, saved["original_build_sha256"], tool, args.collector, saved["collector_sha256"])
    binding.verify_build(measured, capture_raw, args.source, args.selector, deadline=end)
    save(args.evidence / "build-measured.json", measured, end)
    # Reuse the existing bounded reader/group cleanup, not a new native runner.
    import publish
    commands = []
    for phase, argv in zip(("prepare", "run"), command_argv(args, original)):
        command_end = command_deadline(end, phase, time.monotonic())
        row = publish.command(argv, phase, args.evidence / ("measurement-" + ("prepare" if phase == "prepare" else "native") + ".log"),
                              SOURCE_ROOT, environment(), command_end)
        commands.append(row)
        require(row["exit"] == 0, "measurement-target-command")
    measured_raw = read(args.evidence / "build-measured.json", binding.MAX_JSON, end)
    require(measured_raw == binding.canonical(measured), "measurement-measured-changed")
    facts = retained(args, measured_raw, capture_raw, end)
    require(baseline(args, end)[3] == saved_raw, "measurement-target-input-changed")
    result = {"schema": "issue779-c1-measurement-target-v1", "source": args.source,
              "selector": args.selector, "workflow_identity": args.workflow_identity,
              "original_binding_sha256": binding.digest(saved_raw), "measured_build_sha256": binding.digest(measured_raw),
              "commands": commands, "exit": 0, "native_acceptance": False, "coverage_credit": False, **facts}
    save(args.evidence / "measurement-target.json", result, end)
    return result


def validate_commands(commands, args, original, end):
    require(type(commands) is list and len(commands) == 2, "measurement-command-count")
    for row, phase, argv in zip(commands, ("prepare", "run"), command_argv(args, original)):
        binding.exact(row, ("phase", "argv", "exit", "timed_out", "joined", "log_path", "log_sha256", "log_bytes"),
                      "measurement-command-fields")
        name = "measurement-prepare.log" if phase == "prepare" else "measurement-native.log"
        require(row["phase"] == phase and row["argv"] == argv and type(row["exit"]) is int and row["exit"] == 0
                and row["joined"] is True and row["timed_out"] is False
                and row["log_path"] == str(args.evidence / name)
                and binding.integer(row["log_bytes"], 0, binding.MAX_LOG), "measurement-command-terminal")
        raw = read(args.evidence / name, binding.MAX_LOG, end)
        require(len(raw) == row["log_bytes"] and binding.digest(raw) == row["log_sha256"], "measurement-command-log")


def finalize(args):
    end = time.monotonic() + 240
    require(os.geteuid() == 0, "measurement-finalize-root")
    require(type(args.collector_exit) is int and args.collector_exit == 0, "measurement-collector-exit")
    saved, original, capture_raw, saved_raw = baseline(args, end)
    # This is deliberately the original full v1 inventory, including modes,
    # unselected files, runtime, helpers, source and original command logs.
    binding.verify_build(original, capture_raw, args.source, args.selector, deadline=end)
    restored = binding.inventory(Path(original["tool"]["root"]), original["tool"]["entry"], deadline=end)
    require(restored == original["tool"], "measurement-restoration")
    measured_raw = read(args.evidence / "build-measured.json", binding.MAX_JSON, end)
    measured = binding.decode(measured_raw)
    binding.validate_build(measured, capture_raw, args.source, args.selector)
    projected = {key: value for key, value in measured.items() if key not in ("schema", "tool", "measurement")}
    require(projected == {key: value for key, value in original.items() if key not in ("schema", "tool")}
            and measured["measurement"]["original_tool"] == original["tool"]
            and measured["measurement"]["original_build_sha256"] == saved["original_build_sha256"]
            and measured["measurement"]["collector_path"] == str(args.collector)
            and measured["measurement"]["collector_sha256"] == saved["collector_sha256"], "measurement-measured-original")
    facts = retained(args, measured_raw, capture_raw, end)
    terminal_raw = read(args.evidence / "measurement-target.json", binding.MAX_JSON, end)
    terminal = binding.decode(terminal_raw)
    expected_fields = ("schema", "source", "selector", "workflow_identity", "original_binding_sha256",
                       "measured_build_sha256", "commands", "exit", "native_acceptance", "coverage_credit", *facts)
    binding.exact(terminal, expected_fields, "measurement-target-fields")
    require(terminal["schema"] == "issue779-c1-measurement-target-v1" and terminal["source"] == args.source
            and terminal["selector"] == args.selector and terminal["workflow_identity"] == args.workflow_identity
            and terminal["original_binding_sha256"] == binding.digest(saved_raw)
            and terminal["measured_build_sha256"] == binding.digest(measured_raw)
            and type(terminal["exit"]) is int and terminal["exit"] == 0
            and terminal["native_acceptance"] is False and terminal["coverage_credit"] is False
            and all(terminal[k] == v for k, v in facts.items()), "measurement-target-terminal")
    validate_commands(terminal["commands"], args, original, end)
    json_raw = read(args.evidence / "coverage.json", REPORT_LIMIT, end)
    xml_raw = read(args.evidence / "coverage.cobertura.xml", REPORT_LIMIT, end)
    report_json(json_raw)
    report_xml(xml_raw)
    require(baseline(args, end)[3] == saved_raw, "measurement-final-input-changed")
    require(read(args.evidence / "build-measured.json", binding.MAX_JSON, end) == measured_raw
            and read(args.evidence / "measurement-target.json", binding.MAX_JSON, end) == terminal_raw
            and read(args.evidence / "coverage.json", REPORT_LIMIT, end) == json_raw
            and read(args.evidence / "coverage.cobertura.xml", REPORT_LIMIT, end) == xml_raw,
            "measurement-final-output-changed")
    require(retained(args, measured_raw, capture_raw, end) == facts, "measurement-final-evidence-changed")
    validate_commands(terminal["commands"], args, original, end)
    binding.verify_build(original, capture_raw, args.source, args.selector, deadline=end)
    result = {"schema": "issue779-c1-final-measurement-v1", "source": args.source, "selector": args.selector,
              "workflow_identity": args.workflow_identity, "collector": "coverlet.console/10.0.1", "collector_exit": 0,
              "collector_sha256": saved["collector_sha256"], "source_capture_sha256": binding.digest(capture_raw),
              "original_binding_sha256": binding.digest(saved_raw), "original_build_sha256": saved["original_build_sha256"],
              "measured_build_sha256": binding.digest(measured_raw), "target_sha256": binding.digest(terminal_raw),
              "prior_report_sha256": saved["prior_report_sha256"], "prior_binding_sha256": saved["prior_binding_sha256"],
              "report_json_sha256": binding.digest(json_raw), "report_cobertura_sha256": binding.digest(xml_raw),
              "instrumented_modules": measured["measurement"]["instrumented_modules"],
              "restored_tool_sha256": binding.digest(binding.canonical(restored)), "restored": True,
              "commands": terminal["commands"], "native_acceptance": False, "coverage_credit": False, **facts}
    save(args.evidence / "measurement-binding.json", result, end)
    return result


def parser():
    value = argparse.ArgumentParser(description=__doc__)
    commands = value.add_subparsers(dest="command", required=True)
    for name, action in (("bind", bind), ("target", target), ("finalize", finalize)):
        command = commands.add_parser(name)
        for path in COMMON_PATHS:
            command.add_argument("--" + path.replace("_", "-"), type=Path, required=True)
        command.add_argument("--source", required=True)
        command.add_argument("--selector", choices=binding.SELECTORS, required=True)
        command.add_argument("--workflow-identity", required=True)
        if name == "finalize":
            command.add_argument("--collector-exit", type=int, required=True)
        command.set_defaults(action=action)
    return value


def main(argv=None):
    args = parser().parse_args(argv)
    os.umask(0o077)
    try:
        args.action(args)
        return 0
    except Exception:
        # Supplied paths, exception text and private native output stay private.
        print("C1_MEASUREMENT_REJECTED", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
