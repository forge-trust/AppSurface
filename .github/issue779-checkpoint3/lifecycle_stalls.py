"""Read settled worker diagnostics for the fixed lifecycle controls; never echo private bytes.

The worker writes its marker to its own stderr. That stream belongs to the root
collector, not to systemd-run's owner stderr. Read only the collector's existing
root-owned retained directory after the original owner and pumps have settled.
These comparisons provide fixture observations, never admission or exit authority.
"""
import hashlib
import json
import os
import stat
import time


PREFIX_LIMIT = 1024 * 1024
_DIRECTORY = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC
_LEAF = os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK
_MARKER_SCHEMA = "evidence-worker-lifecycle-stall-v1"
_CASES = ("configure-sync-stall", "registered-application-disposer-stall")
LIFECYCLE_CASES = ("configure-sync-stall", "registered-disposer-stall", "post-stop-guards")


def require(value, reason):
    """Reject with a source-owned category, without including paths or private output."""
    if not value:
        raise RuntimeError(reason)


def identity(value):
    """Compare complete custody metadata across reads and retained-name checks."""
    return (value.st_dev, value.st_ino, value.st_mode, value.st_uid, value.st_gid,
            value.st_nlink, value.st_size, value.st_mtime_ns, value.st_ctime_ns)


def _check_deadline(deadline):
    require(time.monotonic() < deadline, "lifecycle-diagnostic-deadline")


def _object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "lifecycle-diagnostic-duplicate-member")
        value[key] = item
    return value


def read_prefix_directory(directory, *, expected_uid, expected_gid, deadline):
    """Read the exact three existing diagnostic leaves through a caller-owned FD.

    This ordinary-file helper validates metadata only. The native entry below
    fixes /run and root ownership. Tests may use their own temporary directory
    and actual UID/GID; that supplies no protected path or process capability.
    The caller keeps the directory FD open. Every failure leaves original bytes
    untouched, and the original absolute observation deadline is never renewed.
    """
    _check_deadline(deadline)
    before = os.fstat(directory)
    require(stat.S_ISDIR(before.st_mode) and stat.S_IMODE(before.st_mode) == 0o700
            and (before.st_uid, before.st_gid) == (expected_uid, expected_gid),
            "lifecycle-diagnostic-directory")
    require(set(os.listdir(directory)) == {"stdout.prefix", "stderr.prefix", "state.json"},
            "lifecycle-diagnostic-inventory")
    leaves = {}
    for name in ("stdout.prefix", "stderr.prefix", "state.json"):
        _check_deadline(deadline)
        maximum = 4096 if name == "state.json" else PREFIX_LIMIT
        child = os.open(name, _LEAF, dir_fd=directory)
        try:
            selected = os.fstat(child)
            require(stat.S_ISREG(selected.st_mode) and stat.S_IMODE(selected.st_mode) == 0o600
                    and (selected.st_uid, selected.st_gid) == (expected_uid, expected_gid)
                    and selected.st_nlink == 1 and 0 <= selected.st_size <= maximum,
                    "lifecycle-diagnostic-leaf")
            data = bytearray()
            while len(data) < selected.st_size:
                _check_deadline(deadline)
                part = os.read(child, min(65536, selected.st_size - len(data)))
                require(bool(part), "lifecycle-diagnostic-short-read")
                data.extend(part)
            require(not os.read(child, 1), "lifecycle-diagnostic-growth")
            require(identity(os.fstat(child)) == identity(selected)
                    == identity(os.stat(name, dir_fd=directory, follow_symlinks=False)),
                    "lifecycle-diagnostic-name-changed")
            leaves[name] = bytes(data)
        finally:
            os.close(child)
    require(identity(os.fstat(directory)) == identity(before)
            and set(os.listdir(directory)) == set(leaves), "lifecycle-diagnostic-tree-changed")
    _check_deadline(deadline)
    require(json.loads(leaves["state.json"], object_pairs_hook=_object)
            == {"schema": "evidence-worker-failure-output-v1"}, "lifecycle-diagnostic-state")
    return leaves


def marker_from_prefix(prefix, expected_case):
    """Require one exact compile-owned marker in bounded private worker bytes."""
    require(expected_case in _CASES and type(prefix) is bytes and len(prefix) <= PREFIX_LIMIT,
            "lifecycle-stall-marker-input")
    markers = []
    for line in prefix.splitlines():
        try:
            value = json.loads(line, object_pairs_hook=_object)
        except (ValueError, UnicodeError):
            continue
        if type(value) is dict and value.get("schema") == _MARKER_SCHEMA:
            markers.append(value)
    require(len(markers) == 1, "lifecycle-stall-marker-count")
    marker = markers[0]
    require(set(marker) == {"schema", "case", "phase", "native_acceptance"}
            and marker["case"] == expected_case and marker["phase"] == "entered"
            and marker["native_acceptance"] is False, "lifecycle-stall-marker-value")
    return marker


def post_stop_marker_from_prefix(prefix):
    """Require the closed worker observations; bytes alone grant no authority."""
    require(type(prefix) is bytes and len(prefix) <= PREFIX_LIMIT, "post-stop-marker-input")
    markers = []
    for line in prefix.splitlines():
        try:
            value = json.loads(line, object_pairs_hook=_object)
        except (ValueError, UnicodeError):
            continue
        if type(value) is dict and value.get("schema") == "evidence-worker-post-stop-guards-v1":
            markers.append(value)
    require(len(markers) == 1, "post-stop-marker-count")
    value = markers[0]
    facts = ("active_admission", "current_worker", "owned_work_joined", "new_work_rejected",
             "write_rejected", "artifacts_unchanged")
    require(set(value) == {"schema", "case", "phase", "native_acceptance", *facts}
            and value["case"] == "post-stop-guards" and value["phase"] == "completed"
            and value["native_acceptance"] is False
            and all(value[name] is True for name in facts), "post-stop-marker-value")
    return value


def require_post_stop_terminal(prefix):
    """Require intentional failure after the checks, without claiming a fatal path."""
    text = b"Private post-stop admission guards completed."
    require(type(prefix) is bytes and len(prefix) <= PREFIX_LIMIT and prefix.count(text) == 1,
            "post-stop-terminal-reason")
    return "post-stop-guards-completed-unsuccessfully"


def require_fatal_reason(prefix, expected_case):
    """Require the intended production fail-stop reason, not an unrelated nonzero exit.

    These are fixed EvidenceWorkerExecution messages in the source-bound worker.
    Raw diagnostic bytes remain private; the return is a closed fixture label.
    This byte comparison alone establishes no process or native authority.
    """
    reasons = {
        "configure-sync-stall": (b"Owned Evidence callback or write remained active after its bounded grace.",
                                 "callback-unsettled"),
        "registered-application-disposer-stall": (b"Evidence cleanup did not settle within its budget.",
                                                 "registered-disposer-unsettled"),
    }
    require(expected_case in reasons and type(prefix) is bytes and len(prefix) <= PREFIX_LIMIT,
            "lifecycle-fatal-reason-input")
    text, label = reasons[expected_case]
    require(prefix.count(text) == 1, "lifecycle-fatal-reason")
    return label


def require_configure_before_application_start(observed, application_seen, generation):
    """Accept only exact empty pending-application cleanup, not startup or health authority.

    Root retains the application holder before configure. Its cleanup emits a settled
    observation even when no application process was dispatched. The native caller
    separately verifies the fatal callback reason, groups and original output pumps.
    """
    reason = "configure-stall-before-application-start"
    require(type(generation) is str and len(generation) == 32
            and all(char in "0123456789abcdef" for char in generation), reason)
    require(application_seen is False and type(observed) is list and len(observed) == 1, reason)
    value = observed[0]
    require(type(value) is dict and set(value) == {
        "schema", "phase", "lease_id", "unit", "main_code", "main_status", "group_empty_twice",
        "pumps_joined", "pending_start_joined", "received_bytes", "ledger_failure", "monitor_failed"}, reason)
    require(value["schema"] == "evidence-application-native-v1" and value["phase"] == "settled"
            and value["lease_id"] == generation
            and value["unit"] == "appsurface-evidence-application-" + generation + ".service"
            and value["main_code"] is None and value["main_status"] is None
            and value["group_empty_twice"] is True and value["pumps_joined"] is True
            and value["pending_start_joined"] is True and value["monitor_failed"] is False
            and type(value["received_bytes"]) is int and value["received_bytes"] == 0
            and value["ledger_failure"] == "None", reason)
    return {"application_started": False, "pending_holder_settled": True}


def require_joined_output(value, leaves, expected_case):
    """Bind retained prefix lengths to the original joined shared output ledger.

    The original worker pair is registered first. This does not prove a cgroup
    or process exited; native group and account checks remain mandatory callers.
    The configure stall starts no application/producer; normal SDK cleanup has
    the original worker, application, and producer pairs.
    """
    require(expected_case in (*_CASES, "post-stop-guards") and type(value) is dict
            and set(value) == {"schema", "registered_pairs", "received_bytes", "received_byte_limit",
                               "failure", "quota_exceeded", "stop_signal_failed", "pairs"},
            "lifecycle-output-shape")
    pairs = value["pairs"]
    count = 1 if expected_case == "configure-sync-stall" else 3
    require(value["schema"] == "evidence-coverage-output-v1"
            and type(value["registered_pairs"]) is int and value["registered_pairs"] == count
            and type(pairs) is list and len(pairs) == count
            and type(value["received_byte_limit"]) is int and value["received_byte_limit"] == 16 * 1024 * 1024
            and type(value["received_bytes"]) is int and 0 <= value["received_bytes"] <= value["received_byte_limit"]
            and value["failure"] == "None" and value["quota_exceeded"] is False
            and value["stop_signal_failed"] is False, "lifecycle-output-settlement")
    for pair in pairs:
        require(type(pair) is dict
                and set(pair) == {"received_bytes", "stdout_bytes", "stderr_bytes", "stdout_eof", "stderr_eof",
                                  "failure", "quota_exceeded"}
                and all(type(pair[field]) is int and 0 <= pair[field] <= value["received_byte_limit"]
                        for field in ("received_bytes", "stdout_bytes", "stderr_bytes"))
                and pair["stdout_bytes"] + pair["stderr_bytes"] == pair["received_bytes"]
                and pair["stdout_eof"] is True and pair["stderr_eof"] is True
                and pair["failure"] == "None" and pair["quota_exceeded"] is False,
                "lifecycle-output-pair")
    require(sum(pair["received_bytes"] for pair in pairs) == value["received_bytes"]
            and pairs[0]["stdout_bytes"] >= len(leaves["stdout.prefix"])
            and pairs[0]["stderr_bytes"] >= len(leaves["stderr.prefix"]), "lifecycle-output-reconciliation")
    return value


def validate_retained_receipt(receipt, files, *, source, base, deadline):
    """Validate the fixed three-case lifecycle receipt from retained bytes only.

    This shares the existing marker, fatal-reason, application-settlement and
    stream parsers with the live runner. It checks serialized observations; it
    does not reacquire process, cgroup, account or filesystem state.
    """
    require(type(receipt) is dict and receipt.get("schema") == "issue779-c3sdk-lifecycle-stalls-v1"
            and receipt.get("source") == source and receipt.get("base") == base
            and type(receipt.get("exit")) is int and receipt["exit"] == 0
            and receipt.get("native_acceptance") is False, "lifecycle-receipt-header")
    rows = receipt.get("cases")
    require(type(rows) is list and all(type(row) is dict for row in rows)
            and tuple(row.get("case") for row in rows) == LIFECYCLE_CASES,
            "lifecycle-receipt-order")
    for row in rows:
        _check_deadline(deadline)
        case = row["case"]
        generation = row.get("generation")
        require(type(generation) is str and len(generation) == 32
                and all(c in "0123456789abcdef" for c in generation)
                and row.get("passed") is True and row.get("native_acceptance") is False
                and row.get("groups_empty") is True and row.get("final_groups_empty") is True
                and row.get("fixture_paths_absent") is True, "lifecycle-case-settlement")
        owner_stdout = files.get(case + "-owner.stdout")
        owner_stderr = files.get(case + "-owner.stderr")
        worker_stdout = files.get(case + "-worker-stdout.prefix")
        worker_stderr = files.get(case + "-worker-stderr.prefix")
        state = files.get(case + "-worker-state.json")
        require(type(owner_stdout) is bytes and owner_stdout == b""
                and type(owner_stderr) is bytes and len(owner_stderr) <= 8 * 1024 * 1024
                and type(worker_stdout) is bytes and len(worker_stdout) <= PREFIX_LIMIT
                and type(worker_stderr) is bytes and len(worker_stderr) <= PREFIX_LIMIT
                and type(state) is bytes and 0 < len(state) <= 4096,
                "lifecycle-retained-files")
        state_value = json.loads(state, object_pairs_hook=_object)
        require(state_value == {"schema": "evidence-worker-failure-output-v1"},
                "lifecycle-retained-state")
        owner_events = []
        output_events = []
        for line in owner_stderr.splitlines():
            try:
                event = json.loads(line, object_pairs_hook=_object)
            except (ValueError, UnicodeError):
                continue
            if type(event) is dict and event.get("schema") == "evidence-application-native-v1":
                owner_events.append(event)
            if type(event) is dict and event.get("schema") == "evidence-coverage-output-v1":
                output_events.append(event)
        require(owner_events == row.get("application_observations"), "lifecycle-owner-observations")
        prefix_digests = row.get("worker_prefix_sha256")
        require(type(prefix_digests) is dict and prefix_digests == {
            "stdout.prefix": hashlib.sha256(worker_stdout).hexdigest(),
            "stderr.prefix": hashlib.sha256(worker_stderr).hexdigest(),
            "state.json": hashlib.sha256(state).hexdigest()}, "lifecycle-prefix-hashes")
        leaves = {"stdout.prefix": worker_stdout, "stderr.prefix": worker_stderr, "state.json": state}
        if case == "post-stop-guards":
            marker = post_stop_marker_from_prefix(worker_stderr)
            fatal = require_post_stop_terminal(worker_stderr)
        else:
            expected_marker = ("configure-sync-stall" if case == "configure-sync-stall"
                               else "registered-application-disposer-stall")
            marker = marker_from_prefix(worker_stderr, expected_marker)
            fatal = require_fatal_reason(worker_stderr, expected_marker)
        require(row.get("lifecycle_stall") == marker and row.get("fatal_reason") == fatal,
                "lifecycle-marker-binding")
        output = row.get("output")
        expected_output_case = ("configure-sync-stall" if case == "configure-sync-stall"
                                else "registered-application-disposer-stall" if case == "registered-disposer-stall"
                                else "post-stop-guards")
        require(type(output) is dict and len(output_events) == 1
                and output_events[0] == output
                and require_joined_output(output_events[0], leaves, expected_output_case) == output,
                "lifecycle-output")
        observations = row.get("application_observations")
        require(type(observations) is list and all(type(event) is dict for event in observations),
                "lifecycle-observation-shape")
        if case == "configure-sync-stall":
            require_configure_before_application_start(observations, row.get("application_seen"), generation)
        else:
            settled = [event for event in observations if event.get("phase") == "settled"]
            require(row.get("application_seen") is True and len(settled) == 1
                    and settled[0].get("group_empty_twice") is True and settled[0].get("pumps_joined") is True
                    and settled[0].get("pending_start_joined") is True,
                    "lifecycle-application-settlement")
        require(row.get("root_exit") != 0 and type(row.get("root_exit")) is int,
                "lifecycle-expected-root-failure")
        accounts = row.get("accounts")
        require(type(accounts) is list and len(accounts) == 8
                and all(type(item) is dict for item in accounts)
                and all(item.get("present") is False for item in accounts), "lifecycle-accounts")
        case_raw = files.get(case + ".json")
        require(type(case_raw) is bytes and json.loads(case_raw, object_pairs_hook=_object) == row,
                "lifecycle-case-file")
    require(type(receipt.get("staging_path_absent")) is bool and receipt["staging_path_absent"] is True,
            "lifecycle-staging-cleanup")
    _check_deadline(deadline)
    return {"schema": receipt["schema"], "source": source, "base": base,
            "cases": LIFECYCLE_CASES, "native_acceptance": False}


def read_worker_marker(generation, expected_case, *, deadline):
    """Read only the original root-retained worker prefix for one generated run.

    Call after physical owner termination. Before crediting the case, the native
    caller must also require both EOFs and reconcile these bytes against the
    original shared output receipt. Missing diagnostic retention remains a failed
    control; owner stderr, journal text, and a new worker reader are not fallbacks.
    Returns private bytes for exclusive fixture retention and a closed marker;
    neither grants a successful Evidence result or enables Trusted support.
    """
    require(type(generation) is str and len(generation) == 32
            and all(character in "0123456789abcdef" for character in generation),
            "lifecycle-diagnostic-generation")
    _check_deadline(deadline)
    name = "appsurface-evidence-worker-diagnostics-" + generation
    parent = os.open("/run", _DIRECTORY)
    try:
        selected_parent = os.fstat(parent)
        require(selected_parent.st_uid == selected_parent.st_gid == 0
                and selected_parent.st_mode & 0o7022 == 0, "lifecycle-diagnostic-parent")
        directory = os.open(name, _DIRECTORY, dir_fd=parent)
        try:
            selected = os.fstat(directory)
            leaves = read_prefix_directory(directory, expected_uid=0, expected_gid=0, deadline=deadline)
            marker = (post_stop_marker_from_prefix(leaves["stderr.prefix"]) if expected_case == "post-stop-guards"
                      else marker_from_prefix(leaves["stderr.prefix"], expected_case))
            require(identity(os.fstat(directory)) == identity(selected)
                    == identity(os.stat(name, dir_fd=parent, follow_symlinks=False)),
                    "lifecycle-diagnostic-root-changed")
        finally:
            os.close(directory)
    finally:
        os.close(parent)
    _check_deadline(deadline)
    return marker, leaves, {name: hashlib.sha256(data).hexdigest() for name, data in leaves.items()}
