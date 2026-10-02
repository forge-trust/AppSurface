"""Pure validation, protocol, peer-credential and quota tests for the Linux launcher."""
import importlib.util
import base64
import hashlib
import io
import json
import os
import stat
import struct
import tempfile
import threading
import time
import unittest
from argparse import Namespace
from contextlib import ExitStack
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("evidencehost_linux_launcher", Path(__file__).parents[2] / "scripts" / "evidencehost-linux-launcher.py")
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)


def portable_openat2(dir_fd, path, flags, resolve_flags=launcher.OPENAT2_RESOLVE):
    """Use real no-follow descriptors while mocking only Linux openat2 resolution in tests."""
    del resolve_flags
    if not isinstance(path, str) or not path or path.startswith("/") or "\0" in path:
        raise launcher.LauncherError("artifact-path-invalid")
    parts = path.split("/")
    if any(part in ("", ".", "..") for part in parts):
        raise launcher.LauncherError("artifact-path-invalid")
    current_fd = os.dup(dir_fd)
    try:
        for part in parts[:-1]:
            child_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                               dir_fd=current_fd)
            os.close(current_fd)
            current_fd = child_fd
        portable_flags = flags & ~launcher.O_PATH
        return os.open(parts[-1], portable_flags | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=current_fd)
    finally:
        os.close(current_fd)


def artifact_broker(temp_root, contents=b"artifact-data"):
    scratch = Path(temp_root)
    output_root = scratch / "test-output"
    result_root = output_root / "run-1"
    result_root.mkdir(parents=True)
    artifact = result_root / "report.bin"
    artifact.write_bytes(contents)
    test_output_fd = os.open(output_root, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
    uid, gid = os.getuid(), os.getgid()
    broker = launcher.Broker(
        {}, uid, os.getpid(), gid, uid, gid, gid, scratch, (), scratch,
        Path(launcher.sys.executable).resolve(), "test-evidence", time.monotonic() + 60,
        60, 2, 4, test_output_fd)
    broker.allowed_results_roots.add("run-1")
    return broker, result_root, artifact


class BrokerProtocolConnection:
    """One-request socket double that supplies the launcher's pinned worker credentials."""
    def __init__(self, broker, request):
        self.broker = broker
        self.request = request.encode() + b"\n"
        self.response = bytearray()

    def getsockopt(self, level, option, length):
        del level, option
        assert length == struct.calcsize("3i")
        return struct.pack("3i", self.broker.worker_pid, self.broker.worker_uid, self.broker.worker_gid)

    def settimeout(self, timeout):
        del timeout

    def recv(self, size):
        request, self.request = self.request, b""
        return request[:size]

    def sendall(self, response):
        self.response.extend(response)

    def close(self):
        pass

    def decoded_response(self):
        return json.loads(self.response)


def broker_request(broker, request):
    connection = BrokerProtocolConnection(broker, json.dumps(request, separators=(",", ":")))
    with patch.object(launcher.socket, "SO_PEERCRED", 17, create=True):
        broker.handle(connection)
    return connection.decoded_response()


class PrivateFailureDiagnosticTests(unittest.TestCase):
    def test_worker_journal_reader_binds_unit_and_caps_real_pipe_bytes(self):
        class JournalProcess:
            def __init__(self, data, code):
                read_fd, write_fd = os.pipe()
                self.stdout = os.fdopen(read_fd, "rb")
                os.write(write_fd, data)
                os.close(write_fd)
                self.code = code
                self.killed = False

            def wait(self, timeout):
                self.timeout = timeout
                return self.code

            def poll(self):
                return self.code

            def kill(self):
                self.killed = True

        unit = "evidencehost-012345abcdef-worker.service"
        for data, code, expected in ((b"ASEVD402: secret-779\n", 0, "collected"),
                                     (b"", 0, "missing"), (b"", 1, "unavailable"),
                                     (b"x" * 4097, 0, "truncated")):
            with self.subTest(expected=expected):
                process = JournalProcess(data, code)
                with patch.object(launcher.subprocess, "Popen", return_value=process) as spawn:
                    state, received = launcher._read_worker_journal(unit)
                self.assertEqual(state, expected)
                self.assertEqual(received, data[:4096])
                self.assertLessEqual(process.timeout, 5)
                self.assertEqual(spawn.call_args.args[0], ["/usr/bin/journalctl", "--unit=" + unit,
                    "--no-pager", "--output=cat", "--quiet", "--lines=32"])
                self.assertEqual(spawn.call_args.kwargs["stderr"], launcher.subprocess.DEVNULL)
                self.assertTrue(process.stdout.closed)
        with patch.object(launcher.subprocess, "Popen") as spawn:
            self.assertEqual(launcher._read_worker_journal("other-secret-779.service"), ("unavailable", b""))
            spawn.assert_not_called()
        with patch.object(launcher.subprocess, "Popen", side_effect=OSError(2, "secret-779")):
            self.assertEqual(launcher._read_worker_journal(unit), ("unavailable", b""))

    def test_worker_journal_read_error_and_deadline_reap_without_exception_echo(self):
        for read_error in (False, True):
            with self.subTest(read_error=read_error):
                read_fd, write_fd = os.pipe()
                os.write(write_fd, b"secret-779")
                os.close(write_fd)
                with os.fdopen(read_fd, "rb") as stream, patch.object(launcher.subprocess, "Popen") as spawn:
                    process = spawn.return_value
                    process.stdout = stream
                    process.poll.return_value = None
                    process.wait.return_value = 0
                    with patch.object(launcher.selectors, "DefaultSelector") as selector, \
                         patch.object(launcher.os, "read", side_effect=OSError(5, "secret-779")):
                        selector.return_value.__enter__.return_value.select.return_value = [True] if read_error else []
                        state, data = launcher._read_worker_journal("evidencehost-012345abcdef-worker.service")
                    self.assertEqual((state, data), ("read-error" if read_error else "unavailable", b""))
                    process.kill.assert_called_once_with()
                    self.assertLessEqual(process.wait.call_args.kwargs["timeout"], 5)

    def test_protocol_failure_private_journal_canary_and_locked_checkpoints(self):
        with tempfile.TemporaryDirectory() as directory:
            broker, _, _ = artifact_broker(directory)
            broker.unit_prefix = "evidencehost-012345abcdef"
            broker.ready_seen = broker.work_closed = True
            broker.active_handlers = 2
            broker.active_runs = 1
            parent = Path(directory)
            fd = launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
            error = launcher.worker_exit_failure("worker-protocol-incomplete", {"ExecMainCode": "1", "ExecMainStatus": "1"})
            raw = b"secret-779 ASEVD402: control\nASEVD999: unknown\nXASEVD420: fake\nASEVD402: duplicate\n"
            try:
                with patch.object(launcher.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFDIR | 0o700, st_uid=0)), \
                     patch.object(launcher, "_read_worker_journal", return_value=("collected", raw)) as query:
                    launcher.capture_worker_protocol_failure(error, broker, fd)
                query.assert_called_once_with("evidencehost-012345abcdef-worker.service")
                record = launcher.failure_diagnostic(error)
                self.assertEqual(launcher.validate_failure_diagnostic(record), record)
                self.assertEqual(record["worker_journal_codes"], ["ASEVD402"])
                self.assertTrue(record["worker_journal_written"])
                self.assertEqual(record["worker_journal_bytes"], len(raw))
                self.assertEqual((record["broker_ready_seen"], record["broker_wait_completed"], record["broker_exited"],
                                  record["broker_work_closed"], record["broker_active_handlers"], record["broker_active_runs"]),
                                 (True, False, False, True, 2, 1))
                self.assertEqual((record["worker_main_code"], record["worker_main_status"]), (1, 1))
                self.assertNotIn("secret-779", json.dumps(record))
                private = parent / launcher.WORKER_JOURNAL_FILE
                self.assertEqual(private.read_bytes(), raw)
                self.assertEqual(stat.S_IMODE(private.stat().st_mode), 0o600)
                launcher.write_failure_diagnostic(fd, error)
                self.assertNotIn(b"secret-779", (parent / launcher.FAILURE_DIAGNOSTIC_FILE).read_bytes())
            finally:
                os.close(fd)
                broker.active_handlers = broker.active_runs = 0
                broker.close_artifact_handles()

    def test_protocol_capture_errors_and_exclusive_private_file_preserve_original_failure(self):
        for condition in ("symlink", "occupied", "oversize", "read-error", "missing", "unprotected"):
            with self.subTest(condition=condition), tempfile.TemporaryDirectory() as directory:
                broker, _, _ = artifact_broker(directory)
                broker.unit_prefix = "evidencehost-012345abcdef"
                parent = Path(directory)
                target = parent / "target"
                target.write_bytes(b"preserved")
                destination = parent / launcher.WORKER_JOURNAL_FILE
                if condition == "symlink":
                    destination.symlink_to(target)
                elif condition == "occupied":
                    destination.write_bytes(b"preserved")
                fd = launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
                error = launcher.worker_exit_failure("worker-protocol-incomplete", {"ExecMainCode": "1", "ExecMainStatus": "1"})
                try:
                    result = ("missing", b"") if condition == "missing" else ("collected", b"x" * (4097 if condition == "oversize" else 4))
                    with patch.object(launcher.os, "fstat", return_value=SimpleNamespace(
                            st_mode=stat.S_IFDIR | (0o777 if condition == "unprotected" else 0o700), st_uid=0)), \
                         patch.object(launcher, "_read_worker_journal", return_value=result,
                                      side_effect=OSError(5, "secret-779") if condition == "read-error" else None) as query:
                        launcher.capture_worker_protocol_failure(error, broker, fd)
                    record = launcher.failure_diagnostic(error)
                    self.assertEqual(launcher.validate_failure_diagnostic(record), record)
                    self.assertEqual((record["cause"], record["worker_main_status"]), ("worker-protocol-incomplete", 1))
                    self.assertNotIn("secret-779", json.dumps(record))
                    self.assertEqual(record["worker_journal_written"], condition == "missing")
                    if condition in ("symlink", "occupied"):
                        self.assertEqual(destination.read_bytes(), b"preserved")
                    if condition == "unprotected":
                        query.assert_not_called()
                    self.assertEqual(target.read_bytes(), b"preserved")
                finally:
                    os.close(fd)
                    broker.close_artifact_handles()

    def test_protocol_diagnostic_schema_has_exact_types_bounds_and_no_default_journal(self):
        with tempfile.TemporaryDirectory() as directory:
            broker, _, _ = artifact_broker(directory)
            error = launcher.worker_exit_failure("worker-protocol-incomplete", {})
            try:
                with patch.object(launcher, "_read_worker_journal") as query:
                    launcher.capture_worker_protocol_failure(error, broker, None)
                query.assert_not_called()
                good = launcher.failure_diagnostic(error)
                self.assertNotIn("worker_journal_state", good)
                for extra in ({"broker_ready_seen": 1}, {"broker_active_handlers": True},
                              {"broker_active_handlers": 4097}, {"broker_active_runs": 2},
                              {"worker_journal_bytes": 4097}, {"worker_journal_written": 1},
                              {"worker_journal_state": ["secret-779"]}, {"worker_journal_codes": ["ASEVD999"]},
                              {"worker_journal_codes": [["secret-779"]]}, {"cause": "worker-unsuccessful"}):
                    with self.subTest(extra=extra), self.assertRaises(launcher.LauncherError):
                        launcher.validate_failure_diagnostic({**good, **extra})
            finally:
                broker.close_artifact_handles()

    def test_worker_start_success_does_not_query_status(self):
        unit = "evidencehost-012345abcdef-worker.service"
        argv = ["/usr/bin/systemd-run", "--unit=" + unit, "secret-779"]
        result = launcher.subprocess.CompletedProcess(argv, 0, b"secret-779", b"secret-779")
        with patch.object(launcher.subprocess, "run", return_value=result) as command:
            launcher._start_worker_unit(argv, unit)
        self.assertEqual(command.call_count, 1)
        self.assertEqual(command.call_args.args[0], argv)

    def test_worker_start_failure_distinguishes_absent_rejected_and_started_unit_with_safe_status(self):
        unit = "evidencehost-012345abcdef-worker.service"
        start = launcher.subprocess.CompletedProcess([], 1, b"secret-779", b"secret-779")
        cases = (
            (1, b"LoadState=not-found\n", "worker-start-unit-absent", "not-found", None, None),
            (0, b"LoadState=bad-setting\n", "worker-start-unit-rejected", "bad-setting", None, None),
            (0, b"LoadState=loaded\nResult=exit-code\nExecMainCode=1\nExecMainStatus=226\n",
             "worker-start-unit-failed", "loaded", "exit-code", (1, 226)),
            (0, b"LoadState=loaded\nResult=resources\nExecMainCode=0\nExecMainStatus=0\n",
             "worker-start-unit-failed", "loaded", "resources", (0, 0)),
            (0, b"LoadState=loaded\nResult=success\n", "worker-start-command-failed", "loaded", "success", None),
            (0, b"LoadState=secret-779\nResult=secret-779\nExecMainStatus=999\n",
             "worker-start-status-unavailable", None, None, None),
            (0, b"LoadState=loaded\nLoadState=not-found\n", "worker-start-status-unavailable", None, None, None),
            (0, b"x" * 4097, "worker-start-status-unavailable", None, None, None),
            (0, b"\xff", "worker-start-status-unavailable", None, None, None),
            (3, b"LoadState=loaded\nResult=exit-code\n", "worker-start-status-unavailable", None, None, None),
        )
        for code, output, cause, load_state, result_state, numbers in cases:
            with self.subTest(cause=cause, output=output[:100]):
                query = launcher.subprocess.CompletedProcess([], code, output, b"secret-779")
                with patch.object(launcher.subprocess, "run", side_effect=[start, query]) as command:
                    with self.assertRaises(launcher.LauncherError) as failure:
                        launcher._start_worker_unit(["/usr/bin/systemd-run", "secret-779"], unit)
                record = launcher.failure_diagnostic(failure.exception)
                self.assertEqual((record["cause"], record["operation"], record["exit_code"]), (cause, "worker-start", 1))
                self.assertEqual(record.get("worker_load_state"), load_state)
                self.assertEqual(record.get("worker_result"), result_state)
                if numbers:
                    self.assertEqual((record["worker_main_code"], record["worker_main_status"]), numbers)
                self.assertNotIn("secret-779", json.dumps(record))
                self.assertEqual(launcher.validate_failure_diagnostic(record), record)
                self.assertEqual(command.call_args.args[0], ["/usr/bin/systemctl", "show", unit, "--no-pager",
                    "--property=LoadState", "--property=Result", "--property=ExecMainCode", "--property=ExecMainStatus"])
                self.assertEqual(command.call_args.kwargs["timeout"], 5)

    def test_worker_start_query_failure_or_spawn_error_does_not_replace_original_failure_with_exception_text(self):
        unit = "evidencehost-012345abcdef-worker.service"
        start = launcher.subprocess.CompletedProcess([], 1, b"secret-779", b"secret-779")
        for error in (OSError(13, "secret-779"), launcher.subprocess.TimeoutExpired("secret-779", 5)):
            with self.subTest(error=type(error).__name__):
                with patch.object(launcher.subprocess, "run", side_effect=[start, error]):
                    with self.assertRaises(launcher.LauncherError) as failure:
                        launcher._start_worker_unit(["/usr/bin/systemd-run"], unit)
                record = launcher.failure_diagnostic(failure.exception)
                self.assertEqual(record["cause"], "worker-start-status-unavailable")
                self.assertEqual(record["exit_code"], 1)
                self.assertNotIn("secret-779", json.dumps(record))
        with patch.object(launcher.subprocess, "run", side_effect=OSError(2, "secret-779")) as command:
            with self.assertRaises(launcher.LauncherError) as failure:
                launcher._start_worker_unit(["/usr/bin/systemd-run"], unit)
        self.assertEqual(command.call_count, 1)
        self.assertEqual(launcher.failure_diagnostic(failure.exception)["errno"], 2)
        with patch.object(launcher.subprocess, "run") as command:
            with self.assertRaises(launcher.LauncherError):
                launcher._start_worker_unit(["/usr/bin/systemd-run"], "other-secret-779.service")
            command.assert_not_called()

    def test_worker_start_status_schema_rejects_unknown_types_values_and_operations(self):
        error = launcher.worker_exit_failure("worker-start-unit-failed", {})
        error.operation = "worker-start"
        error.worker_load_state = ["secret-779"]
        error.worker_result = {"secret-779": True}
        error.worker_main_status = True
        record = launcher.failure_diagnostic(error)
        self.assertNotIn("secret-779", json.dumps(record))
        self.assertEqual(launcher.validate_failure_diagnostic(record), record)
        for extra in ({"worker_result": ["exit-code"]}, {"worker_result": "secret-779"},
                      {"worker_load_state": "secret-779"}, {"worker_main_code": True},
                      {"worker_main_status": 256}, {"operation": "systemd-run", "worker_result": "exit-code"}):
            with self.subTest(extra=extra), self.assertRaises(launcher.LauncherError):
                launcher.validate_failure_diagnostic({**record, **extra})

    def test_existing_copy_guard_literals_are_exact_safe_diagnostic_causes(self):
        for cause in ("subject-entry-invalid", "subject-root-symlink", "subject-copy-path-invalid",
                      "subject-copy-path-overlap", "subject-copy-depth-limit", "subject-copy-entry-limit",
                      "subject-copy-byte-limit", "subject-changed-during-copy"):
            with self.subTest(cause=cause):
                record = launcher.failure_diagnostic(launcher.LauncherError(cause))
                self.assertEqual(record["cause"], cause)
                self.assertEqual(launcher.validate_failure_diagnostic(record), record)

    def test_account_creation_and_cleanup_use_absolute_host_utilities_and_track_ownership(self):
        users, groups = [], []
        result = launcher.subprocess.CompletedProcess([], 0, b"", b"")
        with patch.object(launcher.subprocess, "run", return_value=result) as run:
            launcher._create_run_accounts("worker", "subject", "results", users, groups)
            launcher._delete_run_accounts(users, groups)
        commands = [call.args[0] for call in run.call_args_list]
        self.assertEqual(commands, [
            ["/usr/sbin/useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "worker"],
            ["/usr/sbin/useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "subject"],
            ["/usr/sbin/groupadd", "--system", "results"],
            ["/usr/sbin/userdel", "subject"], ["/usr/sbin/userdel", "worker"], ["/usr/sbin/groupdel", "results"],
        ])
        self.assertEqual((users, groups), (["worker", "subject"], ["results"]))
        self.assertTrue(all(call.kwargs["env"] == launcher.ENV for call in run.call_args_list))
        users, groups = [], []
        with patch.object(launcher.subprocess, "run", side_effect=[result, OSError(2, "secret-779")]):
            with self.assertRaises(launcher.LauncherError):
                launcher._create_run_accounts("worker", "subject", "results", users, groups)
        self.assertEqual((users, groups), (["worker"], []))

    def test_host_spawn_failures_keep_only_fixed_cause_known_operation_and_exact_bounded_errno(self):
        cases = (("/usr/sbin/useradd", 2, "useradd", 2),
                 ("/usr/sbin/groupdel", 13, "groupdel", 13),
                 ("/private/secret-779", 2, None, 2),
                 ("/usr/sbin/useradd", True, "useradd", None),
                 ("/usr/sbin/useradd", 4096, "useradd", None))
        for executable, number, operation, expected_errno in cases:
            with self.subTest(executable=executable, errno=number):
                error = OSError("secret-779")
                error.errno = number
                with patch.object(launcher.subprocess, "run", side_effect=error):
                    with self.assertRaises(launcher.LauncherError) as failure:
                        launcher._systemd([executable, "secret-779"])
                record = launcher.failure_diagnostic(failure.exception)
                self.assertEqual(record["cause"], "host-command-start-failed")
                self.assertEqual(record.get("operation"), operation)
                self.assertEqual(record.get("errno"), expected_errno)
                self.assertNotIn("secret-779", str(failure.exception))
                self.assertNotIn("secret-779", json.dumps(record))
                self.assertEqual(launcher.validate_failure_diagnostic(record), record)

    def test_worker_exit_reason_contains_only_bounded_numeric_systemd_fields(self):
        for cause in ("worker-protocol-incomplete", "worker-unsuccessful"):
            with self.subTest(cause=cause):
                record = launcher.failure_diagnostic(launcher.worker_exit_failure(cause,
                    {"ExecMainCode": "1", "ExecMainStatus": "203", "Result": "secret-779"}))
                self.assertEqual(record["cause"], cause)
                self.assertEqual(record["operation"], "worker-exit")
                self.assertEqual((record["worker_main_code"], record["worker_main_status"]), (1, 203))
                self.assertEqual(launcher.validate_failure_diagnostic(record), record)
                self.assertNotIn("secret-779", json.dumps(record))
        record = launcher.failure_diagnostic(launcher.worker_exit_failure("worker-protocol-incomplete",
            {"ExecMainCode": "secret-779", "ExecMainStatus": "999"}))
        self.assertNotIn("worker_main_code", record)
        self.assertNotIn("worker_main_status", record)
        error = launcher.worker_exit_failure("worker-protocol-incomplete", {})
        error.worker_main_code = True
        error.worker_main_status = 256
        record = launcher.failure_diagnostic(error)
        self.assertNotIn("worker_main_code", record)
        self.assertNotIn("worker_main_status", record)
        with self.assertRaises(launcher.LauncherError):
            launcher.validate_failure_diagnostic({**record, "worker_main_status": True})

    def test_systemd_failure_reduces_child_output_to_fixed_operation_and_numeric_exit(self):
        result = launcher.subprocess.CompletedProcess(["systemd-run"], 3, b"secret-779", b"secret-779")
        with patch.object(launcher.subprocess, "run", return_value=result):
            with self.assertRaises(launcher.LauncherError) as failure:
                launcher._systemd(["systemd-run", "secret-779"])
        record = launcher.failure_diagnostic(failure.exception)
        self.assertEqual((record["cause"], record["operation"], record["exit_code"]),
                         ("systemd-operation-failed", "systemd-run", 3))
        self.assertNotIn("secret-779", json.dumps(record))

    def test_private_record_is_exclusive_0600_and_retains_only_host_categories(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            fd = launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
            error = launcher.LauncherError("systemd-operation-failed", operation="systemd-run", exit_code=1)
            try:
                launcher.write_failure_diagnostic(fd, error)
                with self.assertRaises(FileExistsError):
                    launcher.write_failure_diagnostic(fd, launcher.LauncherError("worker-start-failed"))
            finally:
                os.close(fd)
            path = parent / launcher.FAILURE_DIAGNOSTIC_FILE
            self.assertEqual(stat.S_IMODE(path.stat().st_mode), 0o600)
            record = launcher.read_failure_diagnostic(parent, expected_owner_uid=os.geteuid())
            self.assertEqual(record, {"schema": launcher.FAILURE_DIAGNOSTIC_SCHEMA,
                                     "error_class": "LauncherError", "cause": "systemd-operation-failed",
                                     "operation": "systemd-run", "exit_code": 1})

    def test_symlinks_unprotected_parents_and_unsafe_records_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            link = parent / "linked-parent"
            link.symlink_to(parent, target_is_directory=True)
            for candidate, owner in ((Path("relative"), os.geteuid()), (link, os.geteuid()),
                                     (parent, os.geteuid() + 1)):
                with self.subTest(candidate=candidate, owner=owner), self.assertRaises((launcher.LauncherError, OSError)):
                    launcher.open_diagnostic_directory(candidate, expected_owner_uid=owner)
            parent.chmod(0o777)
            with self.assertRaises(launcher.LauncherError):
                launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
            parent.chmod(0o700)
            destination = parent / launcher.FAILURE_DIAGNOSTIC_FILE
            target = parent / "target"
            target.write_text("preserved")
            destination.symlink_to(target)
            fd = launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
            try:
                with self.assertRaises(OSError):
                    launcher.write_failure_diagnostic(fd, launcher.LauncherError("worker-start-failed"))
            finally:
                os.close(fd)
            with self.assertRaises(OSError):
                launcher.read_failure_diagnostic(parent, expected_owner_uid=os.geteuid())
            self.assertEqual(target.read_text(), "preserved")
            destination.unlink()
            destination.write_text("x" * (launcher.FAILURE_DIAGNOSTIC_LIMIT + 1))
            destination.chmod(0o600)
            with self.assertRaises(launcher.LauncherError):
                launcher.read_failure_diagnostic(parent, expected_owner_uid=os.geteuid())

    def test_exception_canaries_and_unknown_json_fields_never_become_safe_diagnostics(self):
        for error in (launcher.LauncherError("secret-779"),
                      launcher.LauncherError("secret-779", operation=["secret-779"]), OSError(13, "secret-779"),
                      launcher.subprocess.SubprocessError("secret-779"), ValueError("secret-779")):
            with self.subTest(error=type(error).__name__):
                record = launcher.failure_diagnostic(error)
                self.assertNotIn("secret-779", json.dumps(record))
                self.assertEqual(record["cause"], "unclassified-host-failure")
        good = launcher.failure_diagnostic(launcher.LauncherError("worker-protocol-incomplete"))
        for record in ({**good, "cause": "secret-779"}, {**good, "stderr": "secret-779"},
                       {**good, "operation": ["secret-779"]}, {**good, "errno": True}):
            with self.subTest(record=record), self.assertRaises(launcher.LauncherError):
                launcher.validate_failure_diagnostic(record)

    def test_main_private_capture_preserves_public_negative_and_rejects_occupied_slot(self):
        with tempfile.TemporaryDirectory() as directory:
            parent = Path(directory)
            original_open = launcher.open_diagnostic_directory
            def portable_open(path):
                return original_open(path, expected_owner_uid=os.geteuid())
            with patch.object(launcher, "open_diagnostic_directory", side_effect=portable_open), \
                 patch.object(launcher, "launch", side_effect=launcher.LauncherError("worker-protocol-incomplete")) as launch, \
                 patch.object(launcher.sys, "stderr", io.StringIO()) as stderr:
                with patch.object(launcher, "parser") as parser:
                    parser.return_value.parse_args.return_value = Namespace(diagnostic_directory=str(parent))
                    self.assertEqual(launcher.main([]), 1)
                    self.assertEqual(json.loads(stderr.getvalue()), {"status": "failed", "diagnostic": "launcher-failed"})
                    self.assertEqual(launcher.main([]), 1)
                    self.assertEqual(launch.call_count, 1)
            record = launcher.read_failure_diagnostic(parent, expected_owner_uid=os.geteuid())
            self.assertEqual(record["cause"], "worker-protocol-incomplete")


class LauncherValidationTests(unittest.TestCase):
    def test_actual_worker_start_argv_allows_required_openat2_and_preserves_isolation(self):
        # Execute the production launch path up to its unit-start boundary. Native root,
        # account and systemd operations are explicit doubles; argv emission is real.
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp).resolve()
            tool, subject, output = base / "tool", base / "subject", base / "output"
            for directory in (tool, subject, output):
                directory.mkdir()
            policy = tool / "policy.json"
            policy.write_text("{}")
            (tool / "ForgeTrust.AppSurface.Cli.dll").write_bytes(b"fixture")
            args = Namespace(mode="observation", run_id="100/1", job_seconds=90,
                base_revision="b" * 40, subject_revision="c" * 40, workflow_identity="fixture",
                observation_profile=[], observation_producer=[], solution=str(subject / "fixture.sln"),
                path=[], output_slot="result")
            real_read_text, real_is_file = Path.read_text, Path.is_file
            real_mkdtemp = tempfile.mkdtemp
            stopped = launcher.LauncherError("worker-start-failed")
            with ExitStack() as patches:
                patches.enter_context(patch.object(launcher.sys, "platform", "linux"))
                patches.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
                patches.enter_context(patch.object(Path, "read_text", lambda path, *a, **kw:
                    "systemd" if str(path) == "/proc/1/comm" else real_read_text(path, *a, **kw)))
                patches.enter_context(patch.object(Path, "is_file", lambda path:
                    True if str(path) == "/sys/fs/cgroup/cgroup.controllers" else real_is_file(path)))
                patches.enter_context(patch.object(launcher, "ensure_openat2_supported"))
                patches.enter_context(patch.object(launcher, "validate_args", return_value=(tool, subject, policy, output)))
                patches.enter_context(patch.object(launcher, "protected_diff_snapshot", return_value=(None, None)))
                patches.enter_context(patch.object(launcher, "validate_budgets", return_value={}))
                patches.enter_context(patch.object(launcher, "declared_subject_inputs", return_value=(args.solution, [])))
                patches.enter_context(patch.object(launcher.tempfile, "mkdtemp", side_effect=lambda **kw:
                    real_mkdtemp(prefix=kw["prefix"], dir=base)))
                patches.enter_context(patch.object(launcher, "_create_run_accounts"))
                patches.enter_context(patch.object(launcher.pwd, "getpwnam", return_value=SimpleNamespace(pw_uid=1234, pw_gid=1235)))
                # Supply distinct exact identities to the existing separation check.
                launcher.pwd.getpwnam.side_effect = [SimpleNamespace(pw_uid=1234, pw_gid=1235),
                                                   SimpleNamespace(pw_uid=2345, pw_gid=2346)]
                patches.enter_context(patch.object(launcher.grp, "getgrnam", return_value=SimpleNamespace(gr_gid=3456)))
                patches.enter_context(patch.object(launcher.os, "chown"))
                patches.enter_context(patch.object(launcher, "prepare_scratch_layout"))
                patches.enter_context(patch.object(launcher, "_copy_subject_tree", side_effect=lambda source, destination, *ids: destination))
                patches.enter_context(patch.object(launcher, "open_test_output_root", return_value=-1))
                patches.enter_context(patch.object(launcher, "prepare_tool_root"))
                listener = patches.enter_context(patch.object(launcher.socket, "socket"))
                listener.return_value.bind.side_effect = lambda path: Path(path).touch()
                patches.enter_context(patch.object(launcher.shutil, "which", return_value=launcher.sys.executable))
                patches.enter_context(patch.object(launcher, "_systemd", return_value=launcher.subprocess.CompletedProcess([], 0, b"systemd 255\n", b"")))
                patches.enter_context(patch.object(launcher.subprocess, "run", return_value=launcher.subprocess.CompletedProcess([], 0)))
                start = patches.enter_context(patch.object(launcher, "_start_worker_unit", side_effect=stopped))
                with self.assertRaises(launcher.LauncherError) as failure:
                    launcher.launch_with_completion(args)
                self.assertIs(failure.exception, stopped)
            start.assert_called_once()
            argv, unit = start.call_args.args
            properties = [value.removeprefix("--property=").split("=", 1)
                          for value in argv if value.startswith("--property=")]
            self.assertEqual(len(properties), len(dict(properties)))
            emitted = dict(properties)
            worker = unit.removeprefix("evidencehost-").removesuffix("-worker.service")
            self.assertEqual(emitted["User"], "evw" + worker)
            self.assertEqual(emitted["Group"], emitted["User"])
            self.assertEqual(emitted["RestrictSUIDSGID"], "no")
            expected = {"Type": "exec", "KillMode": "control-group", "RuntimeMaxSec": "90",
                "TimeoutStopSec": "2", "SendSIGKILL": "yes", "NoNewPrivileges": "yes",
                "CapabilityBoundingSet": "", "AmbientCapabilities": "", "ProtectControlGroups": "yes",
                "PrivateTmp": "yes", "ProtectSystem": "strict", "ProtectHome": "yes", "LimitCORE": "0",
                "TasksMax": "64", "MemoryMax": "1G", "Restart": "no", "RemainAfterExit": "yes"}
            for name, value in expected.items():
                self.assertEqual(emitted[name], value, name)
            self.assertEqual(set(emitted), {*expected, "User", "Group", "RestrictSUIDSGID",
                "ReadOnlyPaths", "ReadWritePaths", "InaccessiblePaths"})
            readonly = emitted["ReadOnlyPaths"].split()
            self.assertEqual(readonly[0], str(tool))
            prepared_subject = Path(readonly[1])
            self.assertEqual(prepared_subject.name, "subject")
            self.assertTrue(prepared_subject.parent.is_relative_to(base))
            self.assertEqual(emitted["InaccessiblePaths"], str(prepared_subject.parent / "test-output"))
            self.assertEqual(emitted["ReadWritePaths"], str(output / ("run-" + worker)))
            self.assertIn("--expand-environment=no", argv)
            self.assertIn("--unit=" + unit, argv)

    def test_trusted_allowlist_rejection_has_its_exact_safe_diagnostic(self):
        with patch.object(launcher, "parser") as parser, \
                patch.object(launcher, "launch", side_effect=launcher.LauncherError("trusted-proof-not-allowlisted")), \
                patch.object(launcher.sys, "stderr", io.StringIO()) as stderr:
            parser.return_value.parse_args.return_value = Namespace(diagnostic_directory=None)
            self.assertEqual(launcher.main([]), 1)
            self.assertEqual(json.loads(stderr.getvalue()), {"status": "failed", "diagnostic": "ASEVD407"})

    def test_other_launcher_failures_never_echo_exception_canaries(self):
        for error in (launcher.LauncherError("secret-779"),
                      launcher.LauncherError("host-command-start-failed", operation="useradd", errno=2),
                      OSError("secret-779"), ValueError("secret-779")):
            with patch.object(launcher, "parser") as parser, patch.object(launcher, "launch", side_effect=error), \
                    patch.object(launcher.sys, "stderr", io.StringIO()) as stderr:
                parser.return_value.parse_args.return_value = Namespace(diagnostic_directory=None)
                self.assertEqual(launcher.main([]), 1)
                self.assertEqual(json.loads(stderr.getvalue()), {"status": "failed", "diagnostic": "launcher-failed"})

    def test_valid_protected_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            tool, subject, output = (root / x for x in ("tool", "subject", "output"))
            tool.mkdir(mode=0o755); subject.mkdir(); output.mkdir(mode=0o755)
            tool = tool.resolve()
            policy = tool / "policy.json"; policy.write_text("{}")
            policy.chmod(0o440)
            args = Namespace(mode="observation", job_seconds=10, output_slot="job-1", tool_root=str(tool),
                             subject_root=str(subject), policy_file=str(policy), output_parent=str(output))
            self.assertEqual(launcher.validate_args(args, root_uid=tool.stat().st_uid),
                             (tool.resolve(), subject.resolve(), policy.resolve(), output.resolve()))

    def test_invalid_deadline_mode_and_slot_reject(self):
        for field, value in (("job_seconds", 0), ("job_seconds", 3601), ("mode", "unknown"), ("output_slot", "../x")):
            with self.subTest(field=field, value=value):
                args = Namespace(mode="trusted", job_seconds=10, output_slot="job", tool_root="/tmp", subject_root="/tmp",
                                 policy_file="/tmp", output_parent="/tmp")
                setattr(args, field, value)
                with self.assertRaises(launcher.LauncherError): launcher.validate_args(args)
    def test_parent_slot_name_is_not_preallocated_by_launcher_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); tool=(root/"tool").resolve(); subject=root/"subject"; output=root/"out"
            tool.mkdir(); subject.mkdir(); output.mkdir(); (output/"slot").mkdir()
            policy=tool/"p"; policy.write_text("p"); policy.chmod(0o400)
            args=Namespace(mode="observation",job_seconds=1,output_slot="slot",tool_root=str(tool),subject_root=str(subject),policy_file=str(policy),output_parent=str(output))
            self.assertEqual(launcher.validate_args(args, root_uid=root.stat().st_uid)[-1], output.resolve())

    def test_output_parent_identity_matches_csharp_fields(self):
        with tempfile.TemporaryDirectory() as temp:
            identity = launcher.output_parent_identity(Path(temp))
            self.assertEqual(set(identity), {"device_major", "device_minor", "inode", "uid", "gid"})
            self.assertGreater(identity["inode"], 0)

    def test_worker_can_read_source_but_not_reach_raw_test_output(self):
        tool = Path("/tool")
        subject = Path("/scratch/subject")
        test_output = Path("/scratch/test-output")
        output = Path("/output")
        properties = launcher.worker_unit_properties("evidence-worker", tool, subject,
                                                     test_output, output, 90)
        self.assertNotIn("SupplementaryGroups", properties)
        self.assertEqual(properties["ReadOnlyPaths"], "/tool /scratch/subject")
        self.assertEqual(properties["ReadWritePaths"], "/output")
        self.assertEqual(properties["InaccessiblePaths"], "/scratch/test-output")
        self.assertNotIn("test-output", properties["ReadWritePaths"])

    def test_worker_cli_home_uses_private_tmp_without_changing_home_or_other_environment(self):
        self.assertEqual(launcher.WORKER_ENV["HOME"], launcher.ENV["HOME"])
        self.assertEqual(launcher.WORKER_ENV["DOTNET_CLI_HOME"], "/tmp")
        self.assertEqual(launcher.worker_unit_properties(
            "worker", Path("/tool"), Path("/scratch/subject"),
            Path("/scratch/test-output"), Path("/output"), 90)["PrivateTmp"], "yes")

    def test_scratch_layout_keeps_results_group_separate_from_worker_traversal(self):
        with tempfile.TemporaryDirectory() as temp:
            scratch = Path(temp) / "scratch"
            scratch.mkdir()
            uid, gid = os.getuid(), os.getgid()
            launcher.prepare_scratch_layout(scratch, uid, gid, gid, gid)
            scratch_stat = scratch.stat()
            self.assertEqual(stat.S_IMODE(scratch_stat.st_mode), 0o710)
            self.assertEqual(scratch_stat.st_uid, uid)
            self.assertEqual(scratch_stat.st_gid, gid)
            self.assertEqual(stat.S_IMODE((scratch / "dotnet").stat().st_mode), 0o700)
            results_stat = (scratch / "test-output").stat()
            self.assertEqual(stat.S_IMODE(results_stat.st_mode) & 0o777, 0o770)
            self.assertEqual(results_stat.st_gid, gid)

    def test_copied_subject_source_is_worker_group_readable_and_subject_owned(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            source = root / "checkout"
            destination = root / "scratch" / "subject"
            destination.parent.mkdir()
            (source / "src").mkdir(parents=True)
            plain = source / "src" / "app.cs"
            executable = source / "tool.sh"
            plain.write_text("public class App {}\n")
            executable.write_text("#!/bin/sh\n")
            executable.chmod(0o700)
            uid, gid = os.getuid(), os.getgid()
            with patch.object(launcher, "openat2", side_effect=portable_openat2):
                copied = launcher._copy_subject_tree(source, destination, uid, gid, gid)
            copied_plain = copied / "src" / "app.cs"
            copied_executable = copied / "tool.sh"
            self.assertEqual(stat.S_IMODE(copied.stat().st_mode), 0o750)
            self.assertEqual(stat.S_IMODE((copied / "src").stat().st_mode), 0o750)
            self.assertEqual(stat.S_IMODE(copied_plain.stat().st_mode), 0o640)
            self.assertEqual(stat.S_IMODE(copied_executable.stat().st_mode), 0o750)
            self.assertEqual(copied_plain.stat().st_uid, uid)
            self.assertEqual(copied_plain.stat().st_gid, gid)
            self.assertEqual(plain.read_text(), "public class App {}\n")

    def test_diff_file_argument_is_root_selectable(self):
        parsed = launcher.parser().parse_args([
            "--tool-root", "/tool", "--subject-root", "/subject", "--policy-file", "/tool/policy.json",
            "--job-seconds", "1200", "--mode", "observation", "--output-parent", "/output",
            "--output-slot", "slot", "--base-revision", "base", "--subject-revision", "head",
            "--workflow-identity", "workflow", "--run-id", "1/1", "--solution", "src/app.sln",
            "--diff-file", "/tool/policy/diff.snapshot",
        ])
        self.assertEqual(parsed.diff_file, "/tool/policy/diff.snapshot")

    def test_protected_diff_snapshot_returns_exact_paired_path_and_digest(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp).resolve()
            tool = root / "tool"
            policy_dir = tool / "policy"
            policy_dir.mkdir(parents=True)
            diff_file = policy_dir / "diff.snapshot"
            contents = b"diff --git a/src/a.cs b/src/a.cs\n+changed\n"
            diff_file.write_bytes(contents)
            diff_file.chmod(0o440)
            with patch.object(launcher, "openat2", side_effect=portable_openat2):
                path, digest = launcher.protected_diff_snapshot(tool, str(diff_file.resolve()), root_uid=os.getuid())
                absent = launcher.protected_diff_snapshot(tool, None, root_uid=os.getuid())
            self.assertEqual((path, digest), (str(diff_file), hashlib.sha256(contents).hexdigest()))
            self.assertEqual(absent, (None, None))
            self.assertEqual(diff_file.read_bytes(), contents)
            self.assertEqual(len(digest), 64)

    def test_protected_diff_snapshot_rejects_untrusted_path_shapes_and_permissions(self):
        scenarios = ("outside", "symlink", "hardlink", "writable_file", "writable_parent", "oversized")
        for scenario in scenarios:
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as temp:
                root = Path(temp).resolve()
                tool = root / "tool"
                policy_dir = tool / "policy"
                policy_dir.mkdir(parents=True)
                diff_file = policy_dir / "diff.snapshot"
                diff_file.write_bytes(b"small diff\n")
                diff_file.chmod(0o440)
                requested = diff_file
                if scenario == "outside":
                    requested = root / "outside" / "diff.snapshot"
                    requested.parent.mkdir()
                    requested.write_bytes(b"outside\n")
                    requested.chmod(0o440)
                elif scenario == "symlink":
                    requested = policy_dir / "link.snapshot"
                    requested.symlink_to(diff_file.name)
                elif scenario == "hardlink":
                    os.link(diff_file, policy_dir / "alias.snapshot")
                elif scenario == "writable_file":
                    diff_file.chmod(0o600)
                elif scenario == "writable_parent":
                    policy_dir.chmod(0o777)
                elif scenario == "oversized":
                    diff_file.chmod(0o600)
                    with diff_file.open("r+b") as stream:
                        stream.truncate(launcher.MAX_PROTECTED_DIFF_BYTES + 1)
                    diff_file.chmod(0o440)
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     self.assertRaises(launcher.LauncherError):
                    launcher.protected_diff_snapshot(tool, str(requested), root_uid=os.getuid())

    def test_protected_diff_snapshot_accepts_exact_twenty_mib_limit(self):
        with tempfile.TemporaryDirectory() as temp:
            tool = Path(temp).resolve() / "tool"
            policy_dir = tool / "policy"
            policy_dir.mkdir(parents=True)
            diff_file = policy_dir / "diff.snapshot"
            with diff_file.open("wb") as stream:
                stream.truncate(launcher.MAX_PROTECTED_DIFF_BYTES)
            diff_file.chmod(0o440)
            with patch.object(launcher, "openat2", side_effect=portable_openat2):
                path, digest = launcher.protected_diff_snapshot(tool, str(diff_file.resolve()), root_uid=os.getuid())
            self.assertEqual(path, str(diff_file))
            self.assertEqual(len(digest), 64)

    def test_policy_digest_hashes_exact_file_bytes(self):
        with tempfile.TemporaryDirectory() as temp:
            policy = Path(temp) / "policy.json"
            contents = b'{ "version" : 1 }\n'
            policy.write_bytes(contents)
            self.assertEqual(launcher.policy_sha256(policy), hashlib.sha256(contents).hexdigest())

    def test_stage_budgets_are_bounded_and_fit_no_aspire_job_deadline(self):
        values = {name: maximum for name, maximum in launcher.STAGE_LIMITS.items()}
        args = Namespace(job_seconds=1200, **values)
        self.assertEqual(launcher.validate_budgets(args), values)
        reduced = dict(values, admission_seconds=10, collection_seconds=10,
                       cleanup_seconds=10, stopping_seconds=5)
        self.assertEqual(launcher.validate_budgets(Namespace(job_seconds=1200, **reduced)), reduced)
        invalid = dict(values, stopping_seconds=31)
        with self.assertRaises(launcher.LauncherError):
            launcher.validate_budgets(Namespace(job_seconds=1200, **invalid))
        with self.assertRaises(launcher.LauncherError):
            launcher.validate_budgets(Namespace(job_seconds=689, **values))
        self.assertEqual(launcher.validate_budgets(Namespace(job_seconds=690, **values)), values)
        self.assertEqual(launcher.validate_budgets(Namespace(job_seconds=719, **values)), values)


class ProtocolTests(unittest.TestCase):
    def test_request_shapes_and_bounds(self):
        self.assertEqual(launcher.validate_request(b'{"op":"ready"}\n'), {"op":"ready"})
        valid={"op":"run","executable":"/usr/bin/dotnet","arguments":["test","x.csproj"],"working_directory":"/tmp"}
        self.assertEqual(launcher.validate_request(json.dumps(valid).encode()+b"\n"),valid)
        for raw in (b"", b'{bad}\n', b'{"op":"exit"}', b'{"op":"ready","extra":1}\n', b"x"*(launcher.MAX_REQUEST+1)):
            with self.subTest(raw=raw[:16]), self.assertRaises(launcher.LauncherError): launcher.validate_request(raw)

    def test_ready_is_accepted_before_stop_and_rejected_after_stop(self):
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp)
            try:
                ready = broker_request(broker, {"op": "ready"})
                self.assertTrue(ready["ok"])
                self.assertEqual(ready["descriptor"], broker.descriptor)
                self.assertTrue(broker.ready_seen)

                stopped = broker_request(broker, {"op": "stop"})
                self.assertTrue(stopped["ok"])
                with patch.object(launcher, "openat2", side_effect=portable_openat2):
                    listing = broker_request(broker, {"op": "artifacts", "relative_root": "run-1"})
                    self.assertTrue(listing["ok"])
                    self.assertEqual(listing["artifacts"], [{"path": "report.bin", "length_bytes": 13}])
                    artifact = broker_request(broker, {
                        "op": "artifact", "relative_root": "run-1", "relative_path": "report.bin", "offset": 0,
                    })
                self.assertTrue(artifact["ok"])
                self.assertEqual(base64.b64decode(artifact["bytes_base64"]), b"artifact-data")
                self.assertTrue(artifact["end"])
                broker.ready_seen = False
                rejected = broker_request(broker, {"op": "ready"})
                self.assertFalse(rejected["ok"])
                self.assertFalse(broker.ready_seen)
            finally:
                broker.close_artifact_handles()

    def test_ready_is_rejected_after_wait_and_exit(self):
        for terminal_operation in ("wait", "exit"):
            with self.subTest(terminal_operation=terminal_operation), tempfile.TemporaryDirectory() as temp:
                broker, _root, _artifact = artifact_broker(temp)
                try:
                    waited = broker_request(broker, {"op": "wait"})
                    self.assertEqual(waited, {"ok": True, "owned_exit": True})
                    if terminal_operation == "exit":
                        exited = broker_request(broker, {"op": "exit"})
                        self.assertTrue(exited["ok"])
                        self.assertTrue(broker.exited)
                    broker.ready_seen = False
                    rejected = broker_request(broker, {"op": "ready"})
                    self.assertFalse(rejected["ok"])
                    self.assertFalse(broker.ready_seen)
                finally:
                    broker.close_artifact_handles()

    def test_run_rejects_bad_arguments(self):
        for value in ({"op":"run","executable":"/usr/bin/dotnet","arguments":[],"working_directory":"/tmp"},
                      {"op":"run","executable":"/usr/bin/dotnet","arguments":["test",2],"working_directory":"/tmp"},
                      {"op":"run","executable":"/usr/bin/dotnet","arguments":["test"],"working_directory":"/tmp","extra":True}):
            with self.assertRaises(launcher.LauncherError): launcher.validate_request(json.dumps(value).encode()+b"\n")

    def test_artifact_request_shapes_and_rejects_duplicate_casefolded_fields(self):
        listing = {"op": "artifacts", "relative_root": "coverage-run"}
        read = {"op": "artifact", "relative_root": "coverage-run",
                "relative_path": "nested/report.xml", "offset": 0}
        self.assertEqual(launcher.validate_request(json.dumps(listing).encode() + b"\n"), listing)
        self.assertEqual(launcher.validate_request(json.dumps(read).encode() + b"\n"), read)
        malformed = (
            b'{"op":"ready","op":"exit"}\n',
            b'{"op":"ready","OP":"exit"}\n',
            b'{"op":false}\n',
            b'{"op":"artifact","relative_root":"r","relative_path":"x","offset":true}\n',
            b'{"op":"artifact","relative_root":"r","relative_path":"../x","offset":0}\n',
            b'{"op":"artifacts","relative_root":"../r"}\n',
        )
        for raw in malformed:
            with self.subTest(raw=raw), self.assertRaises(launcher.LauncherError):
                launcher.validate_request(raw)

    def test_artifact_relative_paths_reject_ambiguous_and_unencodable_names(self):
        self.assertEqual(launcher.validate_artifact_relative_path("nested/report.xml"), "nested/report.xml")
        for value in ("", "/a", "a//b", "a/./b", "a/../b", "a\\b", "a\x00b", "\ud800"):
            with self.subTest(value=repr(value)), self.assertRaises(launcher.LauncherError):
                launcher.validate_artifact_relative_path(value)

    def test_dotnet_test_results_directory_is_one_safe_child_token(self):
        with tempfile.TemporaryDirectory() as temp:
            scratch = Path(temp)
            (scratch / "test-output").mkdir()
            self.assertEqual(launcher.validate_test_results_path(
                scratch, str(scratch / "test-output" / "run-1")), "run-1")
            self.assertEqual(launcher.dotnet_test_results_argument(
                ["test", "suite.sln", "--results-directory", "/tmp/test-output/run-1"]),
                "/tmp/test-output/run-1")
            for invalid in (str(scratch / "outside"), str(scratch / "test-output" / "nested" / "run"),
                            str(scratch / "test-output" / "bad name"), str(scratch / "test-output" / "../outside")):
                with self.subTest(path=invalid), self.assertRaises(launcher.LauncherError):
                    launcher.validate_test_results_path(scratch, invalid)
        for arguments in (["test"], ["test", "--results-directory"],
                          ["test", "--results-directory", "/x", "--results-directory", "/y"]):
            with self.subTest(arguments=arguments), self.assertRaises(launcher.LauncherError):
                launcher.dotnet_test_results_argument(arguments)

    def test_artifact_request_shape_and_invalid_types_are_checked_before_dispatch(self):
        listing = {"op": "artifacts", "relative_root": "coverage-run"}
        read = {"op": "artifact", "relative_root": "coverage-run",
                "relative_path": "nested/report.xml", "offset": 0}
        self.assertEqual(launcher.validate_request(json.dumps(listing).encode() + b"\n"), listing)
        self.assertEqual(launcher.validate_request(json.dumps(read).encode() + b"\n"), read)
        malformed = (
            b'{"op":"ready","op":"exit"}\n',
            b'{"op":"ready","OP":"exit"}\n',
            b'{"op":false}\n',
            b'{"op":"artifact","relative_root":"r","relative_path":"x","offset":true}\n',
            b'{"op":"artifact","relative_root":"r","relative_path":"../x","offset":0}\n',
            b'{"op":"artifacts","relative_root":"../r"}\n',
        )
        for raw in malformed:
            with self.subTest(raw=raw), self.assertRaises(launcher.LauncherError):
                launcher.validate_request(raw)

    def test_artifact_relative_paths_reject_ambiguous_and_unencodable_names(self):
        self.assertEqual(launcher.validate_artifact_relative_path("nested/report.xml"), "nested/report.xml")
        for value in ("", "/a", "a//b", "a/./b", "a/../b", "a\\b", "a\x00b", "\ud800"):
            with self.subTest(value=repr(value)), self.assertRaises(launcher.LauncherError):
                launcher.validate_artifact_relative_path(value)

    def test_peer_credentials_shape_via_socket_option_seam(self):
        self.assertEqual(launcher.parse_peer_credentials(struct.pack("3i", 7, 8, 9)), (7, 8, 9))
        with self.assertRaises(launcher.LauncherError): launcher.parse_peer_credentials(b"short")

    def test_control_socket_requires_exact_worker_pid_uid_and_gid(self):
        credentials = struct.pack("3i", 707, 1007, 1007)
        self.assertTrue(launcher.peer_is_worker(credentials, 707, 1007, 1007))
        self.assertFalse(launcher.peer_is_worker(credentials, 708, 1007, 1007))
        self.assertFalse(launcher.peer_is_worker(credentials, 707, 1008, 1007))
        self.assertFalse(launcher.peer_is_worker(credentials, 707, 1007, 1008))

    def test_job_deadline_is_captured_as_a_fixed_pair(self):
        from unittest.mock import patch
        with patch.object(launcher.time, "time", return_value=1000.0), \
             patch.object(launcher.time, "monotonic", return_value=250.0):
            wall_deadline, monotonic_deadline = launcher.capture_job_deadline(120)
        self.assertEqual(wall_deadline, "1970-01-01T00:18:40Z")
        self.assertEqual(monotonic_deadline, 370.0)

    def test_ready_allowance_is_positive_and_bounded_by_frozen_deadline(self):
        descriptor = {"job_deadline_utc": "unchanged"}
        response = launcher.ready_response(descriptor, deadline=370.0, original_seconds=120, now=250.0)
        self.assertEqual(response["job_remaining_seconds"], 120.0)
        self.assertIs(response["descriptor"], descriptor)
        response = launcher.ready_response(descriptor, deadline=280.0, original_seconds=120, now=250.0)
        self.assertEqual(response["job_remaining_seconds"], 30.0)
        for deadline in (250.0, 249.0):
            with self.subTest(deadline=deadline), self.assertRaisesRegex(
                    launcher.LauncherError, "job-deadline-expired"):
                launcher.ready_response(descriptor, deadline=deadline, original_seconds=120, now=250.0)
        for allowance in (0, -1, True):
            with self.subTest(allowance=allowance), self.assertRaises(launcher.LauncherError):
                launcher.job_remaining_seconds(300.0, allowance, now=250.0)

    def test_declared_changed_paths_are_relative_and_may_be_deleted(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            subject = root / "subject"
            subject.mkdir()
            solution = subject / "src" / "app.sln"
            solution.parent.mkdir(); solution.write_text("")
            self.assertEqual(launcher.declared_subject_inputs(
                subject, "src/app.sln", ["inputs/./diff.patch", "deleted/old.cs"]),
                (str(solution.resolve()), ["inputs/diff.patch", "deleted/old.cs"]))
            with self.assertRaises(launcher.LauncherError):
                launcher.declared_subject_inputs(subject, "../outside.sln", [])
            for invalid in ("../outside.cs", "/absolute.cs", "C:\\outside.cs", ""):
                with self.subTest(path=invalid), self.assertRaises(launcher.LauncherError):
                    launcher.declared_subject_inputs(subject, "src/app.sln", [invalid])
            outside = root / "external" / "outside"
            outside.parent.mkdir()
            outside.mkdir()
            (subject / "escape").symlink_to(outside, target_is_directory=True)
            with self.assertRaises(launcher.LauncherError):
                launcher.declared_subject_inputs(subject, "src/app.sln", ["escape/new.cs"])

    def test_response_is_jsonl_and_capped_with_truncation_marker(self):
        payload = launcher.encode_response({"ok": True, "stdout": "\0" * (launcher.MAX_PREFIX),
                                            "stderr": "\0" * (launcher.MAX_PREFIX),
                                            "exit_code": 0, "output_truncated": False})
        self.assertLessEqual(len(payload), 3 * 1024 * 1024)
        self.assertTrue(payload.endswith(b"\n"))
        decoded = json.loads(payload)
        self.assertTrue(decoded["output_truncated"])


class OutputBudgetTests(unittest.TestCase):
    def test_prefixes_bounded_and_aggregate_counts_discarded_bytes(self):
        quota = launcher.OutputQuota()
        budget=launcher.OutputBudget(quota)
        budget.add("stdout", b"a"*(launcher.MAX_PREFIX+10))
        budget.add("stderr", b"b"*(launcher.MAX_PREFIX+10))
        self.assertEqual(len(budget.stdout),launcher.MAX_PREFIX)
        self.assertEqual(len(budget.stderr),launcher.MAX_PREFIX)
        self.assertEqual(budget.total,2*launcher.MAX_PREFIX+20)
        self.assertFalse(budget.exceeded.is_set())
        second=launcher.OutputBudget(quota)
        second.add("stdout",b"c"*(launcher.MAX_JOB_OUTPUT-budget.total+1))
        self.assertTrue(second.exceeded.is_set())
        self.assertEqual(quota.total, launcher.MAX_JOB_OUTPUT + 1)

    def test_compiled_trusted_proof_allowlist_is_empty(self):
        self.assertEqual(launcher.TRUSTED_PROOF_DIGEST_ALLOWLIST, frozenset())
        self.assertFalse(launcher.trusted_proof_admitted(hashlib.sha256(b"accepted").hexdigest()))
        self.assertFalse(launcher.trusted_proof_admitted(""))

    def test_confirmed_subject_response_reports_exact_received_bytes_after_prefix_truncation(self):
        with tempfile.TemporaryDirectory() as temp:
            subject_root = Path(temp).resolve() / "subject"
            subject_root.mkdir()
            dotnet = Path("/usr/bin/dotnet").resolve()
            broker = launcher.Broker(
                {}, os.getuid(), os.getpid(), os.getgid(), os.getuid(), os.getgid(), os.getgid(), subject_root,
                (), Path(temp), dotnet, "test-evidence", time.monotonic() + 60,
                60, 2, 4, os.open(Path(temp), os.O_RDONLY | os.O_DIRECTORY),
            )

            class FakeProcess:
                def __init__(self):
                    self.stdout = io.BytesIO(b"out" * (launcher.MAX_PREFIX // 3 + 5))
                    self.stderr = io.BytesIO(b"err" * (launcher.MAX_PREFIX // 3 + 7))
                    self.returncode = 17

                def poll(self):
                    return None

                def wait(self, timeout=None):
                    del timeout
                    return self.returncode

            process = FakeProcess()
            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(launcher.subprocess, "Popen", return_value=process) as spawn, \
                     patch.object(launcher.subprocess, "run", return_value=launcher.subprocess.CompletedProcess([], 0, b"", b"")), \
                     patch.object(broker, "_unit_properties", return_value={
                         "LoadState": "loaded", "ActiveState": "failed", "SubState": "failed", "MainPID": "0",
                         "User": str(os.getuid()), "Group": str(os.getgid()), "KillMode": "control-group",
                         "ControlGroup": "/system.slice/test-evidence-s-0.service", "Result": "exit-code",
                         "ExecMainCode": "1", "ExecMainStatus": "17",
                     }), \
                     patch.object(broker, "_group_empty", return_value=True):
                    result = broker._run({
                        "executable": str(dotnet), "arguments": ["build", "src/app.sln"],
                        "working_directory": str(subject_root),
                    })
                expected_stdout = len(b"out" * (launcher.MAX_PREFIX // 3 + 5))
                expected_stderr = len(b"err" * (launcher.MAX_PREFIX // 3 + 7))
                self.assertEqual(result["received_bytes"], expected_stdout + expected_stderr)
                self.assertEqual(result["exit_code"], 17)
                self.assertEqual(broker.subject_commands_started, 1)
                self.assertEqual(broker.command_output_receipts,
                                 [(expected_stdout + expected_stderr, expected_stdout, expected_stderr)])
                self.assertTrue(result["output_truncated"])
                self.assertEqual(len(result["stdout"].encode()), launcher.MAX_PREFIX)
                self.assertEqual(len(result["stderr"].encode()), launcher.MAX_PREFIX)
                argv = spawn.call_args.args[0]
                emitted = dict(value.removeprefix("--property=").split("=", 1)
                               for value in argv if value.startswith("--property="))
                self.assertEqual(emitted["RestrictSUIDSGID"], "yes")
                self.assertEqual(emitted["NoNewPrivileges"], "yes")
                self.assertEqual(emitted["CapabilityBoundingSet"], "")
                self.assertEqual(emitted["AmbientCapabilities"], "")
                self.assertEqual(emitted["User"], str(broker.subject_uid))
                self.assertEqual(emitted["Group"], str(broker.subject_gid))
                self.assertEqual(emitted["SupplementaryGroups"], str(broker.results_gid))
            finally:
                broker.close_artifact_handles()


class SubjectOutputPumpTests(unittest.TestCase):
    class Reads:
        """Real pump input that permits short reads, EOF, and controlled read exceptions."""
        def __init__(self, *values):
            self.values = iter(values)

        def read(self, size):
            del size
            value = next(self.values, b"")
            if isinstance(value, Exception):
                raise value
            return value

    def exercise(self, stdout, stderr):
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp)
            broker.subject_root = broker.scratch = Path(temp).resolve()
            process = type("Process", (), {"stdout": stdout, "stderr": stderr, "returncode": 0,
                                           "poll": lambda self: None,
                                           "wait": lambda self, timeout=None: 0})()
            request = {"op": "run", "executable": str(broker.dotnet),
                       "arguments": ["test", "fixture.csproj", "--results-directory",
                                     str(broker.scratch / "test-output" / "run-pump")],
                       "working_directory": str(broker.subject_root)}
            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(launcher.subprocess, "Popen", return_value=process), \
                     patch.object(launcher.subprocess, "run", return_value=launcher.subprocess.CompletedProcess([], 0, b"", b"")) as stop, \
                     patch.object(broker, "_unit_properties", return_value={
                         "LoadState": "loaded", "ActiveState": "active", "SubState": "exited", "MainPID": "0",
                         "User": str(os.getuid()), "Group": str(os.getgid()), "KillMode": "control-group",
                         "ControlGroup": "/system.slice/test-evidence-s-0.service", "Result": "success",
                         "ExecMainCode": "1", "ExecMainStatus": "0"}), \
                     patch.object(broker, "_group_empty", return_value=True):
                    response = broker_request(broker, request)
                    registered = "run-pump" in broker.allowed_results_roots
                    closed, failed = broker.work_closed, broker.subject_output_failed
                    if failed:
                        self.assertFalse(broker._wait_for_owned_exit())
                        self.assertFalse(broker._all_subject_groups_empty())
                        self.assertFalse(broker_request(broker, {"op": "ready"})["ok"])
                        broker.wait_completed = True
                        self.assertFalse(broker_request(broker, {"op": "exit"})["ok"])
                        self.assertFalse(broker.exited)
                        self.assertFalse(broker_request(broker, {"op": "artifacts", "relative_root": "run-pump"})["ok"])
                    return response, registered, closed, failed, stop.call_count, broker.output_quota.total
            finally:
                broker.close_artifact_handles()

    def test_read_failure_in_either_owned_pipe_rejects_success_registration_and_exit_without_echo(self):
        for name in ("stdout", "stderr"):
            for error in (OSError("secret-779"), ValueError("secret-779")):
                with self.subTest(name=name, error=type(error).__name__):
                    streams = {"stdout": io.BytesIO(b"out"), "stderr": io.BytesIO(b"err")}
                    streams[name] = self.Reads(b"partial", error)
                    response, registered, closed, failed, stops, _received = self.exercise(**streams)
                    self.assertEqual(response, {"ok": False, "error": "broker-request-failed"})
                    self.assertFalse(registered)
                    self.assertTrue(closed and failed)
                    self.assertGreater(stops, 0)
                    self.assertNotIn("secret-779", json.dumps(response))

    def test_short_reads_require_actual_eof_and_count_all_bytes_before_truncation(self):
        for maximum, truncated in ((64, False), (4, True)):
            with self.subTest(maximum=maximum), patch.object(launcher, "MAX_PREFIX", maximum):
                response, registered, closed, failed, stops, received = self.exercise(
                    self.Reads(b"a", b"bc", b"defgh", b""), self.Reads(b"i", b"j", b""))
            self.assertTrue(response["ok"])
            self.assertEqual((response["received_bytes"], received), (10, 10))
            self.assertEqual(response["stdout"], "abcdefgh"[:maximum])
            self.assertEqual(response["stderr"], "ij")
            self.assertEqual(response["output_truncated"], truncated)
            self.assertTrue(registered)
            self.assertFalse(closed or failed)
            self.assertEqual(stops, 1, "A finished retained unit must be stopped before joining systemd-run.")

    def test_budget_mismatch_after_clean_eof_is_terminal_and_cannot_register_results(self):
        for owner, method in ((launcher.OutputBudget, "add"), (launcher.OutputQuota, "count")):
            with self.subTest(owner=owner.__name__), patch.object(owner, method, return_value=None):
                response, registered, closed, failed, stops, _received = self.exercise(io.BytesIO(b"out"), io.BytesIO(b"err"))
            self.assertFalse(response["ok"])
            self.assertFalse(registered)
            self.assertTrue(closed and failed)
            self.assertGreater(stops, 0)

    def test_normal_eof_after_quota_stop_preserves_budget_failure_and_does_not_register_results(self):
        with patch.object(launcher, "MAX_JOB_OUTPUT", 3):
            response, registered, closed, failed, stops, received = self.exercise(
                self.Reads(b"ab", b"cd", b""), io.BytesIO(b""))
        self.assertEqual(response, {"ok": False, "code": "ASEVD420"})
        self.assertFalse(registered)
        self.assertTrue(closed)
        self.assertFalse(failed)
        self.assertGreater(stops, 0)
        self.assertEqual(received, 4)


class SubjectUnitCompletionTests(unittest.TestCase):
    """Control completion ordering with real pumps and a retained-unit process double."""
    def exercise(self, *, status=0, change=None, launcher_status=None, wait_timeout=False, group_empty=True):
        events = []
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp)
            broker.subject_root = broker.scratch = Path(temp).resolve()
            group = "/system.slice/test-evidence-s-0.service"
            stopped = False

            class Process:
                stdout = io.BytesIO(b"out")
                stderr = io.BytesIO(b"err")
                returncode = None
                first_wait = True

                def poll(self):
                    return self.returncode

                def wait(self, timeout=None):
                    events.append(("wait", timeout))
                    if not stopped or wait_timeout and self.first_wait:
                        self.first_wait = False
                        raise launcher.subprocess.TimeoutExpired("systemd-run", timeout)
                    if self.returncode is None:
                        self.returncode = status if launcher_status is None else launcher_status
                    return self.returncode

                def kill(self):
                    events.append(("kill",))
                    self.returncode = -9

            process = Process()
            queries = 0

            def inspect(unit, timeout=5):
                nonlocal queries
                if stopped:
                    raise AssertionError("Completed unit was queried after stop/GC.")
                self.assertEqual(unit, "test-evidence-s-0.service")
                self.assertGreater(timeout, 0)
                queries += 1
                events.append(("inspect", queries))
                props = {"LoadState": "loaded", "ActiveState": "active" if status == 0 else "failed",
                         "SubState": "exited" if status == 0 else "failed", "MainPID": "0",
                         "User": str(broker.subject_uid), "Group": str(broker.subject_gid),
                         "KillMode": "control-group", "ControlGroup": "", "Result": "success" if status == 0 else "exit-code",
                         "ExecMainCode": "1", "ExecMainStatus": str(status)}
                if change == "running-first" and queries == 1:
                    props.update(MainPID="12345", SubState="running", ControlGroup=group, ExecMainCode="0")
                elif change == "cancelled":
                    broker.work_closed = True
                elif change == "expired":
                    broker.deadline = time.monotonic() - 1
                elif isinstance(change, dict):
                    props.update(change)
                    # A launcher exiting without a valid terminal main receipt must not establish success.
                    process.returncode = 0
                return props

            def stop(argv, **kwargs):
                nonlocal stopped
                self.assertIn(argv[0], ("systemctl", "/usr/bin/systemctl"))
                self.assertEqual(argv[1], "stop")
                self.assertEqual(argv[2:], ["test-evidence-s-0.service"])
                self.assertGreater(kwargs["timeout"], 0)
                events.append(("stop", kwargs["timeout"]))
                stopped = True
                return launcher.subprocess.CompletedProcess(argv, 0, b"", b"")

            def empty(observed):
                self.assertEqual(observed, group)
                self.assertTrue(stopped)
                self.assertIsNotNone(process.returncode)
                events.append(("physical-empty",))
                return group_empty

            request = {"op": "run", "executable": str(broker.dotnet),
                       "arguments": ["test", "fixture.csproj", "--results-directory",
                                     str(broker.scratch / "test-output" / "run-completion")],
                       "working_directory": str(broker.subject_root)}
            if not group_empty:
                broker.stopping_seconds = 0.02
            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(launcher.subprocess, "Popen", return_value=process), \
                     patch.object(launcher.subprocess, "run", side_effect=stop), \
                     patch.object(broker, "_unit_properties", side_effect=inspect), \
                     patch.object(broker, "_group_empty", side_effect=empty):
                    response = broker_request(broker, request)
                    if not response["ok"]:
                        self.assertFalse(broker_request(broker, {"op": "ready"})["ok"])
                        self.assertFalse(broker._wait_for_owned_exit())
                return response, events, "run-completion" in broker.allowed_results_roots, broker.command_output_receipts
            finally:
                broker.close_artifact_handles()

    def test_running_then_exited_main_is_captured_before_stop_and_launcher_wait_even_if_cgroup_pruned(self):
        response, events, registered, receipts = self.exercise(change="running-first")
        self.assertTrue(response["ok"])
        self.assertEqual(response["exit_code"], 0)
        self.assertEqual(response["received_bytes"], 6)
        self.assertEqual(receipts, [(6, 3, 3)])
        self.assertTrue(registered)
        operations = [event[0] for event in events]
        self.assertEqual(operations, ["inspect", "inspect", "stop", "wait", "physical-empty"])
        self.assertLessEqual(events[2][1], 2)
        self.assertLessEqual(events[3][1], events[2][1])

    def test_actual_nonzero_main_status_is_preserved_after_stop(self):
        response, events, registered, receipts = self.exercise(status=17)
        self.assertTrue(response["ok"])
        self.assertEqual(response["exit_code"], 17)
        self.assertTrue(registered)
        self.assertEqual(receipts, [(6, 3, 3)])
        self.assertEqual([event[0] for event in events], ["inspect", "stop", "wait", "physical-empty"])

    def test_missing_terminal_receipt_or_wrong_identity_and_group_never_registers_results(self):
        for change in ({"User": "0"}, {"Group": "0"}, {"KillMode": "process"},
                       {"ControlGroup": "/system.slice/foreign.service"}, {"LoadState": "not-found"},
                       {"ExecMainCode": "0"}, {"ExecMainStatus": "256"}, {"ExecMainStatus": "secret-779"},
                       {"MainPID": "12345", "ExecMainCode": "0"}, {"Result": "exit-code"}):
            with self.subTest(change=change):
                response, _events, registered, receipts = self.exercise(change=change)
                self.assertEqual(response, {"ok": False, "error": "broker-request-failed"})
                self.assertFalse(registered)
                self.assertEqual(receipts, [])
                self.assertNotIn("secret-779", json.dumps(response))

    def test_cancelled_expired_or_launcher_status_mismatch_cannot_become_success(self):
        for arguments in ({"change": "cancelled"}, {"change": "expired"}, {"launcher_status": 1}):
            with self.subTest(arguments=arguments):
                response, _events, registered, receipts = self.exercise(**arguments)
                self.assertFalse(response["ok"])
                self.assertFalse(registered)
                self.assertEqual(receipts, [])

    def test_launcher_wait_timeout_kills_and_reaps_before_rejecting_completion(self):
        response, events, registered, receipts = self.exercise(wait_timeout=True)
        self.assertFalse(response["ok"])
        self.assertFalse(registered)
        self.assertEqual(receipts, [])
        self.assertEqual([event[0] for event in events[:5]], ["inspect", "stop", "wait", "kill", "wait"])

    def test_finished_main_and_joined_pumps_do_not_substitute_for_physical_empty_group(self):
        response, events, registered, receipts = self.exercise(group_empty=False)
        self.assertFalse(response["ok"])
        self.assertFalse(registered)
        self.assertEqual(receipts, [])
        self.assertIn("physical-empty", [event[0] for event in events])


class ArtifactBrokerTests(unittest.TestCase):
    def test_listing_retains_descriptor_for_sequential_bounded_chunks_after_stop(self):
        content = bytes((index % 251 for index in range(launcher.MAX_ARTIFACT_CHUNK_BYTES + 19)))
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, artifact = artifact_broker(temp, content)
            try:
                broker.work_closed = True
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(broker, "_all_subject_groups_empty", return_value=True):
                    listing = broker._list_artifacts("run-1")
                    key = ("run-1", "report.bin")
                    descriptor = broker.artifact_handles[key].fd
                    self.assertEqual(listing, {"ok": True, "artifacts": [
                        {"path": "report.bin", "length_bytes": len(content)}]})
                    self.assertTrue(stat.S_ISREG(os.fstat(descriptor).st_mode))
                    first = broker._read_artifact("run-1", "report.bin", 0)
                    self.assertEqual(base64.b64decode(first["bytes_base64"]), content[:launcher.MAX_ARTIFACT_CHUNK_BYTES])
                    self.assertFalse(first["end"])
                    self.assertEqual(broker.artifact_handles[key].fd, descriptor)
                    second = broker._read_artifact("run-1", "report.bin", launcher.MAX_ARTIFACT_CHUNK_BYTES)
                    self.assertEqual(base64.b64decode(second["bytes_base64"]), content[launcher.MAX_ARTIFACT_CHUNK_BYTES:])
                    self.assertTrue(second["end"])
                    self.assertEqual(sum(len(base64.b64decode(item["bytes_base64"]))
                                         for item in (first, second)), len(content))
                    with self.assertRaises(launcher.LauncherError):
                        broker._read_artifact("run-1", "report.bin", len(content))
            finally:
                broker.close_artifact_handles()

    def test_read_rejects_same_length_mutation_and_replaced_name(self):
        for replace_name in (False, True):
            with self.subTest(replace_name=replace_name), tempfile.TemporaryDirectory() as temp:
                broker, _root, artifact = artifact_broker(temp, b"original-content")
                try:
                    with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                         patch.object(broker, "_all_subject_groups_empty", return_value=True):
                        broker._list_artifacts("run-1")
                        if replace_name:
                            replacement = artifact.with_name("replacement")
                            replacement.write_bytes(b"original-content")
                            os.replace(replacement, artifact)
                        else:
                            artifact.write_bytes(b"mutated--content")
                        with self.assertRaises(launcher.LauncherError):
                            broker._read_artifact("run-1", "report.bin", 0)
                finally:
                    broker.close_artifact_handles()

    def test_listing_rejects_hardlinks_symlinks_and_oversized_artifact(self):
        cases = ("hardlink", "symlink", "oversized")
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as temp:
                broker, _root, artifact = artifact_broker(temp, b"ok")
                try:
                    if case == "hardlink":
                        os.link(artifact, artifact.with_name("alias"))
                    elif case == "symlink":
                        artifact.unlink()
                        artifact.symlink_to("outside")
                    else:
                        with artifact.open("r+b") as stream:
                            stream.truncate(launcher.MAX_ARTIFACT_FILE_BYTES + 1)
                    with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                         patch.object(broker, "_all_subject_groups_empty", return_value=True), \
                         self.assertRaises(launcher.LauncherError):
                        broker._list_artifacts("run-1")
                finally:
                    broker.close_artifact_handles()

    def test_descriptor_close_waits_until_active_read_finishes(self):
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp, b"close-after-read")
            entered = threading.Event()
            release = threading.Event()
            closed = threading.Event()
            original_read = broker._read_artifact

            def active_read():
                with broker.condition:
                    broker.active_handlers += 1
                entered.set()
                try:
                    release.wait(timeout=2)
                    with broker.condition:
                        return broker.artifact_handles[("run-1", "report.bin")].fd
                finally:
                    with broker.condition:
                        broker.active_handlers -= 1
                        broker.condition.notify_all()

            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(broker, "_all_subject_groups_empty", return_value=True):
                    broker._list_artifacts("run-1")
                result = []
                reader = threading.Thread(target=lambda: result.append(active_read()))
                closer = threading.Thread(target=lambda: (broker.close_artifact_handles(), closed.set()))
                reader.start()
                self.assertTrue(entered.wait(timeout=1))
                closer.start()
                self.assertFalse(closed.wait(timeout=0.05))
                held_fd = result[0] if result else broker.artifact_handles[("run-1", "report.bin")].fd
                self.assertTrue(stat.S_ISREG(os.fstat(held_fd).st_mode))
                release.set()
                reader.join(timeout=1)
                closer.join(timeout=1)
                self.assertFalse(reader.is_alive())
                self.assertFalse(closer.is_alive())
                self.assertTrue(closed.is_set())
                with self.assertRaises(OSError):
                    os.fstat(held_fd)
            finally:
                release.set()
                if not closed.is_set():
                    broker.close_artifact_handles()

    def test_wait_waits_for_run_handler_notification_before_owned_exit(self):
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp)
            broker.active_runs = 1
            broker.stopping_seconds = 1
            broker.cleanup_seconds = 2
            broker.deadline = time.monotonic() + 2
            waiting = threading.Event()
            original_wait = broker.condition.wait

            def observed_wait(timeout=None):
                waiting.set()
                return original_wait(timeout)

            broker.condition.wait = observed_wait
            finished = threading.Event()
            result = []
            with patch.object(broker, "_all_subject_groups_empty", return_value=True):
                thread = threading.Thread(target=lambda: (result.append(broker._wait_for_owned_exit()), finished.set()))
                thread.start()
                try:
                    self.assertTrue(waiting.wait(timeout=1))
                    self.assertFalse(finished.is_set())
                    with broker.condition:
                        broker.active_runs = 0
                        broker.run_change_generation += 1
                        broker.condition.notify_all()
                    self.assertTrue(finished.wait(timeout=1))
                    self.assertEqual(result, [True])
                finally:
                    thread.join(timeout=1)
                    broker.close_artifact_handles()

    def test_wait_deadline_returns_false_when_run_handler_does_not_settle(self):
        with tempfile.TemporaryDirectory() as temp:
            broker, _root, _artifact = artifact_broker(temp)
            broker.active_runs = 1
            broker.stopping_seconds = 1
            broker.cleanup_seconds = 1
            broker.deadline = time.monotonic() + 0.04
            started = time.monotonic()
            try:
                self.assertFalse(broker._wait_for_owned_exit())
                self.assertLess(time.monotonic() - started, 0.5)
            finally:
                broker.active_runs = 0
                broker.close_artifact_handles()


class RootCompletionTests(unittest.TestCase):
    """Real retained directory handles; ownership/protocol inputs are explicitly fixture state."""
    def completion_fixture(self, root):
        broker, _, _ = artifact_broker(root)
        parent = Path(root) / "final-parent"
        output = parent / "result"
        output.mkdir(parents=True, mode=0o700)
        os.chmod(parent, 0o700)
        os.chmod(output, 0o700)
        broker.descriptor = {"cgroup": "/system.slice/fixture-worker.service",
                             "output_parent_identity": launcher.output_parent_identity(parent),
                             "run_id": "100/1", "paths": ["declared.cs"], "proof_digest": ""}
        broker.ready_seen = broker.exited = broker.wait_completed = broker.work_closed = True
        broker.subject_commands_started = 1
        broker.command_output_receipts = [(7, 4, 3)]
        broker.output_quota.count(7)
        properties = {"Result": "success", "User": "fixture-worker", "KillMode": "control-group",
                      "ExecMainCode": "1", "ExecMainStatus": "0", "ControlGroup": broker.descriptor["cgroup"]}
        return broker, output, properties

    def complete(self, broker, output, properties):
        with patch.object(broker, "_group_empty", return_value=True), \
                patch.object(broker, "_all_subject_groups_empty", return_value=True):
            return launcher._completion_after_owned_exit(broker, output, "fixture-worker", properties)

    def test_retains_original_output_and_defensively_copies_root_facts_after_path_replacement(self):
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            try:
                (output / "declared.bin").write_bytes(b"original")
                with self.complete(broker, output, properties) as completion:
                    identity = completion.output_identity
                    facts = completion.descriptor
                    facts["paths"].append("forged.cs")
                    broker.descriptor["paths"].append("later.cs")
                    identity["inode"] = 0
                    self.assertEqual(completion.descriptor["paths"], ["declared.cs"])
                    self.assertNotEqual(completion.output_identity["inode"], 0)
                    output.rename(output.with_name("retained"))
                    output.mkdir(mode=0o700)
                    (output / "declared.bin").write_bytes(b"replacement")
                    fd = completion.duplicate_output_directory()
                    parent_fd = completion.duplicate_output_parent()
                    try:
                        artifact = os.open("declared.bin", os.O_RDONLY | os.O_NOFOLLOW, dir_fd=fd)
                        try:
                            self.assertEqual(os.read(artifact, 32), b"original")
                        finally:
                            os.close(artifact)
                        self.assertEqual(os.fstat(fd).st_ino, completion.output_identity["inode"])
                        self.assertEqual(os.fstat(parent_fd).st_ino, completion.output_parent_identity["inode"])
                        self.assertEqual(completion.subject_output_receipts, ((7, 4, 3),))
                    finally:
                        os.close(fd)
                        os.close(parent_fd)
                completion.close()
                with self.assertRaises(launcher.LauncherError):
                    completion.duplicate_output_directory()
                with self.assertRaises(launcher.LauncherError):
                    completion.duplicate_output_parent()
                with self.assertRaises(launcher.LauncherError):
                    completion.__enter__()
            finally:
                broker.close_artifact_handles()

    def test_requires_every_protocol_ack_and_no_active_handler_write_or_failed_pump(self):
        values = (("ready_seen", False), ("exited", False), ("wait_completed", False),
                  ("work_closed", False), ("active_runs", 1), ("active_handlers", 1),
                  ("active_artifact_operations", 1), ("subject_output_failed", True),
                  ("deadline", 0))
        for name, value in values:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                try:
                    setattr(broker, name, value)
                    with self.assertRaisesRegex(launcher.LauncherError, "completion-ownership-unconfirmed"):
                        self.complete(broker, output, properties)
                finally:
                    broker.active_runs = broker.active_handlers = broker.active_artifact_operations = 0
                    broker.close_artifact_handles()

    def test_requires_each_started_command_eofs_exact_shared_bytes_and_no_quota_overflow(self):
        cases = (((7, 4, 2),), ((6, 3, 3),), (), ((7, 4, 3), (0, 0, 0)), ((7, -1, 8),))
        for receipts in cases:
            with self.subTest(receipts=receipts), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                try:
                    broker.command_output_receipts = list(receipts)
                    with self.assertRaisesRegex(launcher.LauncherError, "completion-output-unconfirmed"):
                        self.complete(broker, output, properties)
                finally:
                    broker.close_artifact_handles()
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            try:
                broker.output_quota.exceeded.set()
                with self.assertRaisesRegex(launcher.LauncherError, "completion-output-unconfirmed"):
                    self.complete(broker, output, properties)
            finally:
                broker.close_artifact_handles()

    def test_worker_nonzero_exit_or_active_descendant_rejects_completion(self):
        cases = ({"Result": "exit-code"}, {"User": "other"}, {"KillMode": "process"},
                 {"ExecMainCode": "0"}, {"ExecMainStatus": "1"})
        for changed in cases:
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                try:
                    with self.assertRaisesRegex(launcher.LauncherError, "completion-ownership-unconfirmed"):
                        self.complete(broker, output, {**properties, **changed})
                finally:
                    broker.close_artifact_handles()
        for method in ("_group_empty", "_all_subject_groups_empty"):
            with self.subTest(method=method), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                try:
                    with patch.object(broker, "_group_empty", return_value=method != "_group_empty"), \
                            patch.object(broker, "_all_subject_groups_empty", return_value=method != "_all_subject_groups_empty"):
                        with self.assertRaisesRegex(launcher.LauncherError, "completion-ownership-unconfirmed"):
                            launcher._completion_after_owned_exit(broker, output, "fixture-worker", properties)
                finally:
                    broker.close_artifact_handles()

    def test_rejects_parent_substitution_output_symlink_and_nonprivate_mode(self):
        for attack in ("parent", "symlink", "mode"):
            with self.subTest(attack=attack), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                try:
                    if attack == "parent":
                        output.parent.rename(output.parent.with_name("old-parent"))
                        output.mkdir(parents=True, mode=0o700)
                        os.chmod(output.parent, 0o700)
                    elif attack == "symlink":
                        output.rename(output.with_name("old-output"))
                        output.symlink_to(output.with_name("old-output"), target_is_directory=True)
                    else:
                        os.chmod(output, 0o750)
                    with self.assertRaises((launcher.LauncherError, OSError)):
                        self.complete(broker, output, properties)
                finally:
                    broker.close_artifact_handles()

    def test_path_compatible_launch_closes_retained_completion_and_propagates_failures(self):
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            try:
                completion = self.complete(broker, output, properties)
                with patch.object(launcher, "launch_with_completion", return_value=completion):
                    self.assertEqual(launcher.launch(Namespace()), output)
                with self.assertRaises(launcher.LauncherError):
                    completion.duplicate_output_directory()
                with patch.object(launcher, "launch_with_completion", side_effect=launcher.LauncherError("worker-unsuccessful")):
                    with self.assertRaisesRegex(launcher.LauncherError, "worker-unsuccessful"):
                        launcher.launch(Namespace())
            finally:
                broker.close_artifact_handles()

    def test_pretransfer_cleanup_error_closes_both_real_descriptors_and_quarantines_accounts(self):
        for stage in ("listener", "broker", "scratch"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                completion = self.complete(broker, output, properties)
                identities = (completion.output_identity, completion.output_parent_identity)
                closed = []
                real_close = os.close
                def record_close(fd):
                    identity = launcher._identity_from_stat(os.fstat(fd))
                    if identity in identities:
                        closed.append(fd)
                    real_close(fd)
                error = OSError(5, "cleanup-canary")
                listener = SimpleNamespace(close=lambda: None)
                control, scratch = Path(root) / "control", Path(root) / "scratch"
                control.mkdir()
                scratch.mkdir()
                try:
                    with patch.object(launcher.os, "close", side_effect=record_close), \
                         patch.object(launcher, "_delete_run_accounts") as delete, \
                         patch.object(listener, "close", side_effect=error if stage == "listener" else None), \
                         patch.object(broker, "close_artifact_handles", side_effect=error if stage == "broker" else None), \
                         patch.object(launcher.shutil, "rmtree", side_effect=error if stage == "scratch" else None):
                        with self.assertRaises(OSError) as failure:
                            launcher._finish_launch_transfer(completion, ["worker", "subject"], ["results"],
                                listener, [], broker, -1, control, scratch)
                        self.assertIs(failure.exception, error)
                        delete.assert_not_called()
                    self.assertEqual(len(closed), 2)
                    for fd in closed:
                        with self.assertRaises(OSError):
                            os.fstat(fd)
                    self.assertTrue(output.is_dir())
                    with self.assertRaises(launcher.LauncherError):
                        completion.duplicate_output_directory()
                finally:
                    completion.close()
                    broker.close_artifact_handles()

    def test_successful_transfer_retains_accounts_during_collection_then_close_deletes_once(self):
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            completion = self.complete(broker, output, properties)
            (output / "collected.bin").write_bytes(b"protected-output")
            control, scratch = Path(root) / "control", Path(root) / "scratch"
            control.mkdir()
            scratch.mkdir()
            users, groups = ["worker", "subject"], ["results"]
            try:
                result = launcher.subprocess.CompletedProcess([], 0, b"", b"")
                with patch.object(launcher.subprocess, "run", return_value=result) as command:
                    retained = launcher._finish_launch_transfer(completion, users, groups, None, [], broker, -1, control, scratch)
                    self.assertIs(retained, completion)
                    self.assertFalse(control.exists() or scratch.exists())
                    command.assert_not_called()
                    users.append("later-user")
                    groups.append("later-group")
                    fd = retained.duplicate_output_directory()
                    try:
                        artifact = os.open("collected.bin", os.O_RDONLY | os.O_NOFOLLOW, dir_fd=fd)
                        try:
                            self.assertEqual(os.read(artifact, 64), b"protected-output")
                        finally:
                            os.close(artifact)
                    finally:
                        os.close(fd)
                    command.assert_not_called()
                    retained.close()
                    retained.close()
                    self.assertEqual([call.args[0] for call in command.call_args_list],
                        [["/usr/sbin/userdel", "subject"], ["/usr/sbin/userdel", "worker"],
                         ["/usr/sbin/groupdel", "results"]])
            finally:
                completion.close()
                broker.close_artifact_handles()

    def test_account_cleanup_failure_is_latched_and_legacy_path_cannot_hide_it(self):
        for legacy in (False, True):
            with self.subTest(legacy=legacy), tempfile.TemporaryDirectory() as root:
                broker, output, properties = self.completion_fixture(root)
                completion = self.complete(broker, output, properties)
                completion._retain_run_accounts(["worker"], ["results"])
                result = launcher.subprocess.CompletedProcess([], 6, b"cleanup-canary", b"cleanup-canary")
                try:
                    with patch.object(launcher.subprocess, "run", return_value=result) as command, \
                         patch.object(launcher, "launch_with_completion", return_value=completion):
                        with self.assertRaisesRegex(launcher.LauncherError, "systemd-operation-failed") as failure:
                            launcher.launch(Namespace()) if legacy else completion.close()
                        with self.assertRaises(launcher.LauncherError) as repeated:
                            completion.close()
                        self.assertIs(repeated.exception, failure.exception)
                        command.assert_called_once()
                        self.assertNotIn("cleanup-canary", str(failure.exception))
                        with self.assertRaises(launcher.LauncherError):
                            completion.duplicate_output_directory()
                        with self.assertRaises(launcher.LauncherError):
                            completion.duplicate_output_parent()
                finally:
                    broker.close_artifact_handles()

    def test_close_attempts_second_descriptor_and_accounts_after_first_close_error(self):
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            completion = self.complete(broker, output, properties)
            completion._retain_run_accounts(["worker"], ["results"])
            real_close = os.close
            closed = []
            error = OSError(5, "close-canary")
            def failing_close(fd):
                real_close(fd)
                closed.append(fd)
                if len(closed) == 1:
                    raise error
            try:
                with patch.object(launcher.os, "close", side_effect=failing_close), \
                     patch.object(launcher, "_delete_run_accounts") as delete:
                    with self.assertRaises(OSError) as failure:
                        completion.close()
                    self.assertIs(failure.exception, error)
                    self.assertEqual(len(closed), 2)
                    delete.assert_called_once_with(("worker",), ("results",), strict=True)
                    with self.assertRaises(OSError):
                        completion.close()
                    delete.assert_called_once()
                for fd in closed:
                    with self.assertRaises(OSError):
                        os.fstat(fd)
            finally:
                broker.close_artifact_handles()

    def test_legacy_path_deletes_retained_accounts_before_returning(self):
        with tempfile.TemporaryDirectory() as root:
            broker, output, properties = self.completion_fixture(root)
            completion = self.complete(broker, output, properties)
            completion._retain_run_accounts(["worker"], ["results"])
            try:
                with patch.object(launcher, "launch_with_completion", return_value=completion), \
                     patch.object(launcher, "_delete_run_accounts") as delete:
                    self.assertEqual(launcher.launch(Namespace()), output)
                    delete.assert_called_once_with(("worker",), ("results",), strict=True)
                    completion.close()
                    delete.assert_called_once()
                with self.assertRaises(launcher.LauncherError):
                    completion.duplicate_output_parent()
            finally:
                broker.close_artifact_handles()


if __name__ == "__main__": unittest.main()
