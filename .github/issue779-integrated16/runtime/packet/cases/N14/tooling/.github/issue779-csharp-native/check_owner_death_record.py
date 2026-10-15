#!/usr/bin/env python3
"""Strict data-only N13/N14 receipt parser; it grants no native credit."""
import json
import sys
from pathlib import Path

TOP = {"schema", "case", "generation", "phase_line", "accepted_reply_committed",
       "original_request_pending", "owner", "worker", "signal", "systemd_bound",
       "termination", "pipes", "positive_files_before", "positive_files_after",
       "accounts", "workspace", "owner_death_outcome_preserved", "native_credit"}
IDENTITY = {"pid", "starttime", "uid", "gid", "unit", "cgroup"}

def need(value, reason):
    if not value:
        raise ValueError(reason)

def shape(value, keys, reason):
    need(type(value) is dict and set(value) == keys, reason)

def ident(value):
    shape(value, IDENTITY, "identity-shape")
    need(type(value["pid"]) is int and value["pid"] > 0
         and type(value["starttime"]) is int and value["starttime"] > 0, "pid-starttime")
    need(type(value["uid"]) is int and value["uid"] >= 0
         and type(value["gid"]) is int and value["gid"] >= 0, "uid-gid")
    need(type(value["unit"]) is str and value["unit"]
         and type(value["cgroup"]) is str and value["cgroup"], "unit-cgroup")

def validate(record):
    shape(record, TOP, "receipt-shape")
    case = record["case"]
    need(record["schema"] == "issue779-owner-death-native-fixture-v1"
         and case in ("N13", "N14"), "schema-case")
    generation = record["generation"]
    need(type(generation) is str and len(generation) == 32
         and all(char in "0123456789abcdef" for char in generation), "generation")
    need(record["phase_line"] == f"NATIVE_PRIVATE_PHASE:{case}:ACCEPTED_BLOCKED_WORK"
         and record["accepted_reply_committed"] is True
         and record["original_request_pending"] is True, "accepted-phase")
    owner, worker = record["owner"], record["worker"]
    ident(owner); ident(worker)
    need(owner["pid"] != worker["pid"] and owner["uid"] == 0 and owner["gid"] == 0
         and worker["uid"] > 0 and worker["gid"] > 0, "roles")
    need(owner["unit"] == f"appsurface-evidence-owner-{generation}.service"
         and worker["unit"] == f"appsurface-evidence-worker-{generation}.service"
         and owner["cgroup"] == "/system.slice/" + owner["unit"]
         and worker["cgroup"] == "/system.slice/" + worker["unit"], "generated-unit-binding")
    signal = record["signal"]
    shape(signal, {"name", "target_pid", "pre_signal_owner", "syscall_succeeded",
                   "elapsed_seconds", "from_activation_usec"}, "signal-shape")
    need(signal["name"] == ("SIGKILL" if case == "N13" else "SIGSTOP")
         and signal["target_pid"] == owner["pid"] and signal["pre_signal_owner"] == owner
         and signal["syscall_succeeded"] is True, "signal-binding")
    need(type(signal["elapsed_seconds"]) is int and 0 <= signal["elapsed_seconds"] <= 120
         and type(signal["from_activation_usec"]) is int
         and 0 <= signal["from_activation_usec"] < 120000000
         and signal["elapsed_seconds"] == signal["from_activation_usec"] // 1000000,
         "signal-time-units")
    policy = record["systemd_bound"]
    shape(policy, {"active_enter_usec", "runtime_max_usec", "runtime_max_show",
                   "timeout_stop_usec", "timeout_stop_show", "final_kill_policy",
                   "timer_rearmed", "new_acceptance"} | ({"kill_signal"} if case == "N14" else set()), "systemd-policy-shape")
    need(type(policy["active_enter_usec"]) is int and policy["active_enter_usec"] > 0,
         "activation-time")
    runtime_values = {"2min": 120000000, "120000000": 120000000}
    timeout_values = {"5s": 5000000, "5000000": 5000000}
    need(type(policy["runtime_max_usec"]) is int
         and type(policy["timeout_stop_usec"]) is int
         and type(policy["runtime_max_show"]) is str
         and runtime_values.get(policy["runtime_max_show"]) == policy["runtime_max_usec"]
         and type(policy["timeout_stop_show"]) is str
         and timeout_values.get(policy["timeout_stop_show"]) == policy["timeout_stop_usec"],
         "systemd-duration-rendering")
    need(policy["runtime_max_usec"] == 120000000 and policy["timeout_stop_usec"] == 5000000
         and policy["final_kill_policy"] == "SendSIGKILL=yes;FinalKillSignal=9;KillMode=control-group"
         and policy["timer_rearmed"] is False and policy["new_acceptance"] is False,
         "original-policy")
    terminal = record["termination"]
    shape(terminal, {"owner_exited", "worker_exited", "owner_unit_terminal", "worker_unit_terminal",
                     "owner_active_state", "worker_active_state", "owner_sub_state", "worker_sub_state",
                     "owner_terminal_main_pid", "worker_terminal_main_pid", "owner_cgroup_empty",
                     "worker_cgroup_empty", "elapsed_seconds", "from_activation_usec",
                     "owner_exec_main_code", "owner_exec_main_status"} | ({"owner_result"} if case == "N14" else set()), "termination-shape")
    need(all(terminal[key] is True for key in ("owner_exited", "worker_exited", "owner_unit_terminal",
         "worker_unit_terminal", "owner_cgroup_empty", "worker_cgroup_empty")), "physical-termination")
    need(type(terminal["owner_active_state"]) is str and terminal["owner_active_state"] in ("inactive", "failed")
         and type(terminal["worker_active_state"]) is str
         and terminal["worker_active_state"] in ("inactive", "failed")
         and type(terminal["owner_sub_state"]) is str
         and terminal["owner_sub_state"] in ("dead", "failed")
         and type(terminal["worker_sub_state"]) is str
         and terminal["worker_sub_state"] in ("dead", "failed")
         and type(terminal["owner_terminal_main_pid"]) is int
         and terminal["owner_terminal_main_pid"] == 0
         and type(terminal["worker_terminal_main_pid"]) is int
         and terminal["worker_terminal_main_pid"] == 0, "systemd-terminal-state")
    need(type(terminal["elapsed_seconds"]) is int and 0 <= terminal["elapsed_seconds"] <= 125
         and type(terminal["from_activation_usec"]) is int
         and terminal["from_activation_usec"] >= 0
         and terminal["elapsed_seconds"] == terminal["from_activation_usec"] // 1000000
         and type(terminal["owner_exec_main_code"]) is int
         and terminal["owner_exec_main_code"] == 2
         and type(terminal["owner_exec_main_status"]) is int
         and terminal["owner_exec_main_status"] in ((9,) if case == "N13" else (9, 15)), "termination-time-and-signal")
    if case == "N14":
        need(type(policy["kill_signal"]) is int and policy["kill_signal"] == 15
             and terminal["owner_result"] == "timeout"
             and terminal["from_activation_usec"] >= policy["runtime_max_usec"],
             "original-runtime-timeout-required")
    bound = policy["runtime_max_usec"] + (policy["timeout_stop_usec"] if case == "N14" else 0)
    need(terminal["from_activation_usec"] <= bound, "termination-outside-original-systemd-bound")
    pipes = record["pipes"]
    shape(pipes, {"root_stdout_eof", "root_stderr_eof", "stdout_bytes", "stderr_bytes",
                  "stdout_sha256", "stderr_sha256", "launch_exit"}, "pipes-shape")
    need(pipes["root_stdout_eof"] is True and pipes["root_stderr_eof"] is True
         and type(pipes["launch_exit"]) is int and 0 <= pipes["launch_exit"] <= 255,
         "pipe-join")
    need(type(pipes["stdout_bytes"]) is int
         and pipes["stdout_bytes"] == len((record["phase_line"] + "\n").encode("ascii"))
         and type(pipes["stderr_bytes"]) is int and 0 <= pipes["stderr_bytes"] <= 131072,
         "pipe-bounds")
    for key in ("stdout_sha256", "stderr_sha256"):
        value = pipes[key]
        need(type(value) is str and len(value) == 64
             and all(char in "0123456789abcdef" for char in value), "pipe-hash")
    need(type(record["positive_files_before"]) is int and record["positive_files_before"] == 0
         and type(record["positive_files_after"]) is int and record["positive_files_after"] == 0,
         "positive-output")
    accounts = record["accounts"]
    shape(accounts, {"generation_bound", "retained", "reusable", "names", "worker_uid",
                     "worker_gid", "subject_uid", "subject_gid", "results_gid"}, "accounts-shape")
    need(accounts["generation_bound"] is True and accounts["retained"] is True
         and accounts["reusable"] is False, "accounts-quarantined")
    need(accounts["names"] == ["evw" + generation[:28], "evs" + generation[:28],
                               "evr" + generation[:28]], "account-generation-names")
    for key in ("worker_uid", "worker_gid", "subject_uid", "subject_gid", "results_gid"):
        need(type(accounts[key]) is int and accounts[key] > 0, "account-numeric-identities")
    need(accounts["worker_uid"] == worker["uid"] and accounts["worker_gid"] == worker["gid"]
         and accounts["worker_uid"] != accounts["subject_uid"]
         and accounts["results_gid"] not in (accounts["worker_gid"], accounts["subject_gid"]),
         "account-process-binding")
    workspace = record["workspace"]
    shape(workspace, {"generation_bound", "quarantined", "retained"}, "workspace-shape")
    need(all(workspace[key] is True for key in workspace), "workspace-quarantined")
    need(record["owner_death_outcome_preserved"] is True and record["native_credit"] is False,
         "outcome-authority")
    return True

def main():
    try:
        need(len(sys.argv) == 2, "argument-count")
        raw = Path(sys.argv[1]).read_bytes()
        need(len(raw) <= 65536, "receipt-size")
        validate(json.loads(raw))
    except (OSError, IndexError, json.JSONDecodeError, ValueError) as error:
        print(f"OWNER_DEATH_RECORD_REJECTED:{error}", file=sys.stderr)
        return 2
    print("OWNER_DEATH_RECORD_VALID_DATA_ONLY:NO_NATIVE_CREDIT")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
