#!/usr/bin/env python3
"""Validate a doctor-v1 envelope and preserve its process result for a caller-owned workflow.

Usage: python3 consume-doctor-report.py REPORT.json DOCTOR_EXIT
The report is a captured diagnosis, not deployment approval or application proof.
"""

import json
import sys


def consume(report, process_exit):
    """Check the version/exit/check intent before presenting bounded next actions."""
    exits = {
        "passed": 0,
        "findings": 2,
        "invalid-input": 3,
        "unavailable": 4,
        "canceled": 1,
        "failed": 1,
    }
    if type(report) is not dict or (type(report.get("schemaVersion")) is not int or report["schemaVersion"] != 1):
        raise ValueError("Unsupported doctor schema version.")
    status = report.get("status")
    if type(status) is not str or status not in exits or type(report.get("exitCode")) is not int:
        raise ValueError("Invalid doctor status/exit shape.")
    if report["exitCode"] != exits[status] or process_exit != report["exitCode"]:
        raise ValueError("Doctor envelope and process exit disagree.")
    checks = report.get("requestedChecks")
    names = ["credential", "schema", "epoch", "retention", "worker"]
    if type(checks) is not list or len(checks) != len(names):
        raise ValueError("Incomplete doctor check set.")
    for name, check in zip(names, checks):
        if type(check) is not dict or check.get("name") != name or type(check.get("requested")) is not bool:
            raise ValueError("Invalid doctor check shape.")
        if check.get("status") not in {"passed", "finding", "not-checked", "not-requested"}:
            raise ValueError("Unknown doctor check status.")
        if check["requested"] != (check["status"] != "not-requested"):
            raise ValueError("Doctor check intent contradicts its status.")
        if status == "passed" and name != "worker" and not check["requested"]:
            raise ValueError("Clean doctor envelope omits a required store/runtime check.")
        if status == "passed" and check["requested"] and check["status"] != "passed":
            raise ValueError("Clean doctor envelope has an incomplete requested check.")
    findings = report.get("findings")
    if type(findings) is not list or len(findings) > 12 or (status == "passed") != (len(findings) == 0):
        raise ValueError("Doctor findings contradict aggregate status.")
    action = report.get("nextAction")
    if type(action) is not dict:
        raise ValueError("Missing doctor action.")
    if status == "passed":
        if action.get("kind") != "application-verifier" or action.get("command") is not None:
            raise ValueError("Clean doctor action must hand off application verification.")
        if action.get("requiredInputs") != ["consumer verifier command"]:
            raise ValueError("Invalid application verifier handoff.")
        print("Store/runtime checks passed. Supply and run your application's composition verifier.")
    else:
        command = action.get("command")
        if action.get("kind") != "command" or type(command) is not dict:
            raise ValueError("A doctor finding must supply its next command.")
        if command.get("executable") != "appsurface" or type(command.get("arguments")) is not list:
            raise ValueError("Invalid doctor command shape.")
        if len(command["arguments"]) > 14 or any(type(argument) is not str for argument in command["arguments"]):
            raise ValueError("Invalid doctor command arguments.")
        for finding in findings:
            if type(finding) is not dict or type(finding.get("code")) is not str:
                raise ValueError("Invalid doctor finding.")
        # JSON argv stays separate; the consumer does not automatically execute an operator action.
        print(json.dumps({"codes": [finding["code"] for finding in findings], "nextAction": action}, sort_keys=True))
    return process_exit


def main(arguments):
    if len(arguments) != 2:
        print("Usage: consume-doctor-report.py REPORT.json DOCTOR_EXIT", file=sys.stderr)
        return 1
    try:
        with open(arguments[0], encoding="utf-8") as report_file:
            report = json.load(report_file)
        return consume(report, int(arguments[1]))
    except (OSError, ValueError, TypeError, KeyError):
        print("Doctor report could not be validated. Inspect the retained process result and matching CLI contract.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
