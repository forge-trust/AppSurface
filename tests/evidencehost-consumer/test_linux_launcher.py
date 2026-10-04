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
                os.set_blocking(write_fd, False)
                self.code = code
                self.killed = False
                self.stop_writer = threading.Event()
                self.writer_errors = []

                def write():
                    offset = 0
                    deadline = time.monotonic() + 5
                    try:
                        while offset < len(data) and not self.stop_writer.is_set():
                            if time.monotonic() >= deadline:
                                self.writer_errors.append("writer-deadline")
                                break
                            try:
                                offset += os.write(write_fd, data[offset:])
                            except BlockingIOError:
                                self.stop_writer.wait(0.01)
                            except BrokenPipeError:
                                break
                    finally:
                        os.close(write_fd)

                self.writer = threading.Thread(target=write, daemon=True)
                self.writer.start()

            def wait(self, timeout):
                self.timeout = timeout
                self.writer.join(timeout)
                if self.writer.is_alive():
                    raise launcher.subprocess.TimeoutExpired("journal-fixture", timeout)
                return self.code

            def poll(self):
                return None if self.writer.is_alive() else self.code

            def kill(self):
                self.killed = True
                self.stop_writer.set()

        unit = "evidencehost-012345abcdef-worker.service"
        for data, code, expected in ((b"ASEVD402: secret-779\n", 0, "collected"),
                                     (b"", 0, "missing"), (b"", 1, "unavailable"),
                                     (b"x" * 16385, 0, "truncated"),
                                     (b"fatal-header-test-canary\n" + b"managed-stack-frame\n" * 64, 0, "collected")):
            with self.subTest(expected=expected):
                processes = []
                def start(argv, **kwargs):
                    del kwargs
                    lines = int(argv[-1].split("=")[1])
                    process = JournalProcess(b"".join(data.splitlines(keepends=True)[-lines:]), code)
                    processes.append(process)
                    return process
                try:
                    with patch.object(launcher.subprocess, "Popen", side_effect=start) as spawn:
                        state, received = launcher._read_worker_journal(unit)
                finally:
                    for process in processes:
                        process.kill()
                        process.writer.join(1)
                        if not process.stdout.closed:
                            process.stdout.close()
                self.assertEqual(1, len(processes))
                process = processes[0]
                self.assertFalse(process.writer.is_alive())
                self.assertEqual([], process.writer_errors)
                self.assertEqual(state, expected)
                self.assertEqual(received, data[:16384])
                self.assertLessEqual(process.timeout, 5)
                self.assertEqual(spawn.call_args.args[0], ["/usr/bin/journalctl", "--unit=" + unit,
                    "--no-pager", "--output=cat", "--quiet", "--lines=256"])
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
            raw += b"x" * (launcher.WORKER_JOURNAL_LIMIT - len(raw))
            self.assertEqual((4096, 16384), (launcher.FAILURE_DIAGNOSTIC_LIMIT, len(raw)))
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
                safe_json = (parent / launcher.FAILURE_DIAGNOSTIC_FILE).read_bytes()
                self.assertNotIn(b"secret-779", safe_json)
                self.assertLessEqual(len(safe_json), launcher.FAILURE_DIAGNOSTIC_LIMIT)
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
                    result = ("missing", b"") if condition == "missing" else ("collected", b"x" * (16385 if condition == "oversize" else 4))
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
                              {"worker_journal_bytes": 16385}, {"worker_journal_written": 1},
                              {"worker_journal_state": ["secret-779"]}, {"worker_journal_codes": ["ASEVD999"]},
                              {"worker_journal_codes": [["secret-779"]]}, {"cause": "worker-timeout"}):
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
        with patch.object(launcher, "MAX_JOB_OUTPUT", 3), patch.object(launcher._application, "MAX_JOB_OUTPUT", 3):
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


    def test_subject_managed_processor_count_is_single_fixed_env_assignment_with_original_limits(self):
        actual_run = launcher.Broker._run
        emitted = []

        def capture(broker, request):
            response = actual_run(broker, request)
            # exercise owns the existing Popen process double. Inspect the argv
            # actually emitted by _run while that scoped double is still active.
            call = launcher.subprocess.Popen.call_args
            emitted.append((list(call.args[0]), str(broker.dotnet)))
            return response

        with patch.object(launcher.Broker, "_run", autospec=True, side_effect=capture):
            response, _events, registered, receipts = self.exercise()
        self.assertTrue(response["ok"])
        self.assertTrue(registered)
        self.assertEqual([(6, 3, 3)], receipts)
        self.assertEqual(1, len(emitted))
        argv, dotnet = emitted[0]
        env_index = argv.index("/usr/bin/env")
        self.assertEqual("-i", argv[env_index + 1])
        dotnet_index = argv.index(dotnet, env_index + 2)
        assignments = argv[env_index + 2:dotnet_index]
        self.assertEqual(["DOTNET_PROCESSOR_COUNT=1"],
                         [value for value in assignments if value.startswith("DOTNET_PROCESSOR_COUNT=")])
        self.assertEqual(1, argv.count("DOTNET_PROCESSOR_COUNT=1"))
        self.assertEqual(["DOTNET_EnableDiagnostics_IPC=0"],
                         [value for value in assignments if value.startswith("DOTNET_EnableDiagnostics_IPC=")])
        self.assertEqual(1, argv.count("DOTNET_EnableDiagnostics_IPC=0"))
        self.assertEqual(["--property=TasksMax=64"],
                         [value for value in argv if value.startswith("--property=TasksMax=")])
        self.assertEqual(["--property=MemoryMax=1G"],
                         [value for value in argv if value.startswith("--property=MemoryMax=")])
        self.assertEqual("test", argv[dotnet_index + 1])

    def test_subject_fixed_processor_count_overrides_host_and_launcher_environment(self):
        actual_run = launcher.Broker._run
        emitted = []

        def capture(broker, request):
            response = actual_run(broker, request)
            call = launcher.subprocess.Popen.call_args
            emitted.append((list(call.args[0]), dict(call.kwargs["env"]), str(broker.dotnet)))
            return response

        with patch.dict(os.environ, {"DOTNET_PROCESSOR_COUNT": "4096", "DOTNET_EnableDiagnostics_IPC": "1"}), \
             patch.dict(launcher.ENV, {"DOTNET_PROCESSOR_COUNT": "4096", "DOTNET_EnableDiagnostics_IPC": "1"}), \
             patch.object(launcher.Broker, "_run", autospec=True, side_effect=capture):
            response, _events, registered, receipts = self.exercise()
        self.assertTrue(response["ok"])
        self.assertTrue(registered)
        self.assertEqual([(6, 3, 3)], receipts)
        self.assertEqual(1, len(emitted))
        argv, host_environment, dotnet = emitted[0]
        self.assertEqual("4096", host_environment["DOTNET_PROCESSOR_COUNT"])
        self.assertEqual("1", host_environment["DOTNET_EnableDiagnostics_IPC"])
        env_index = argv.index("/usr/bin/env")
        self.assertEqual("-i", argv[env_index + 1])
        dotnet_index = argv.index(dotnet, env_index + 2)
        self.assertEqual(["DOTNET_PROCESSOR_COUNT=1"],
                         [value for value in argv[env_index + 2:dotnet_index]
                          if value.startswith("DOTNET_PROCESSOR_COUNT=")])
        self.assertNotIn("DOTNET_PROCESSOR_COUNT=4096", argv)
        self.assertEqual(["DOTNET_EnableDiagnostics_IPC=0"],
                         [value for value in argv[env_index + 2:dotnet_index]
                          if value.startswith("DOTNET_EnableDiagnostics_IPC=")])
        self.assertEqual(1, argv.count("DOTNET_EnableDiagnostics_IPC=0"))
        self.assertNotIn("DOTNET_EnableDiagnostics_IPC=1", argv)
        self.assertEqual(1, argv.count("--property=TasksMax=64"))
        self.assertEqual(1, argv.count("--property=MemoryMax=1G"))


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


class WorkerTerminalDiagnosticControls(unittest.TestCase):
    """Closed terminal status and bounded private capture, without native unit authority."""
    def test_unsuccessful_worker_after_completed_protocol_retains_private_cause_and_numeric_status(self):
        with tempfile.TemporaryDirectory() as directory:
            broker, _, _ = artifact_broker(directory)
            broker.unit_prefix = "evidencehost-012345abcdef"
            broker.ready_seen = broker.wait_completed = broker.exited = broker.work_closed = True
            parent = Path(directory)
            fd = launcher.open_diagnostic_directory(parent, expected_owner_uid=os.geteuid())
            raw = b"canary-terminal ASEVD409: allocation\n"
            properties = {"Result": "exit-code", "User": "worker", "KillMode": "control-group",
                          "ExecMainCode": "1", "ExecMainStatus": "1"}
            try:
                with patch.object(launcher.os, "fstat", return_value=SimpleNamespace(st_mode=stat.S_IFDIR | 0o700, st_uid=0)), \
                     patch.object(launcher, "_read_worker_journal", return_value=("collected", raw)) as query:
                    with self.assertRaises(launcher.LauncherError) as failed:
                        launcher.require_successful_worker(properties, "worker", broker, fd)
                query.assert_called_once_with("evidencehost-012345abcdef-worker.service")
                record = launcher.failure_diagnostic(failed.exception)
                self.assertEqual(record, launcher.validate_failure_diagnostic(record))
                self.assertEqual((record["cause"], record["worker_main_code"], record["worker_main_status"]),
                                 ("worker-unsuccessful", 1, 1))
                self.assertTrue(record["broker_exited"] and record["broker_wait_completed"])
                self.assertEqual(["ASEVD409"], record["worker_journal_codes"])
                self.assertTrue(record["worker_journal_written"])
                self.assertNotIn("canary-terminal", json.dumps(record))
                self.assertEqual(raw, (parent / launcher.WORKER_JOURNAL_FILE).read_bytes())
                self.assertEqual(0o600, stat.S_IMODE((parent / launcher.WORKER_JOURNAL_FILE).stat().st_mode))
            finally:
                os.close(fd); broker.close_artifact_handles()

    def test_terminal_success_does_not_capture_and_each_failure_guard_stays_failed(self):
        with tempfile.TemporaryDirectory() as directory:
            broker, _, _ = artifact_broker(directory)
            properties = {"Result": "success", "User": "worker", "KillMode": "control-group"}
            try:
                with patch.object(launcher, "_read_worker_journal") as query:
                    launcher.require_successful_worker(properties, "worker", broker, None)
                query.assert_not_called()
                for change in ({"Result": "exit-code"}, {"User": "foreign"}, {"KillMode": "process"}):
                    with self.subTest(change=change), self.assertRaises(launcher.LauncherError) as failed:
                        launcher.require_successful_worker({**properties, **change}, "worker", broker, None)
                    self.assertEqual("worker-unsuccessful", str(failed.exception))
            finally: broker.close_artifact_handles()


class ApplicationPreparationDeadlineControls(unittest.TestCase):
    """Preparation consumes the original job allowance before any workspace or account work."""
    def test_remaining_allowance_shrinks_and_host_command_bound_never_renews_deadline(self):
        with patch.object(launcher.time, "monotonic", return_value=10):
            self.assertEqual(5, launcher.application_preparation_remaining(15))
            self.assertEqual(5, launcher.application_preparation_remaining(15, 8))
            self.assertEqual(2, launcher.application_preparation_remaining(15, 2))
        with patch.object(launcher.time, "monotonic", return_value=14):
            self.assertEqual(1, launcher.application_preparation_remaining(15, 8))
        for now in (15, 16):
            with self.subTest(now=now), patch.object(launcher.time, "monotonic", return_value=now), \
                 self.assertRaisesRegex(launcher.LauncherError, "^application-preparation-deadline$"):
                launcher.application_preparation_remaining(15, 8)

    def test_expired_preparation_rejects_before_creating_a_workspace_or_opening_a_bundle(self):
        with patch.object(launcher.time, "monotonic", return_value=15), \
             patch.object(launcher.Path, "mkdir") as mkdir, \
             patch.object(launcher._application, "audit_bundle") as audit:
            with self.assertRaisesRegex(launcher.LauncherError, "^application-preparation-deadline$"):
                launcher._ApplicationWorkspaceOwner(None, None, Path("/tool"), Path("/output"),
                    Path("/control"), Path("/producer"), Path("/subject"), 15)
        mkdir.assert_not_called(); audit.assert_not_called()


class ApplicationIntegrationControls(unittest.TestCase):
    """Data/procedure controls only; no compiled registration, application lease or native proof."""
    def test_two_application_request_shapes_reject_extra_alias_missing_and_wrong_scalar_fields(self):
        start = {"op": "application-start", "application_id": "native-app", "entry_digest": "a" * 64}
        wait = {"op": "resource-wait", "lease_id": "b" * 32, "resource_id": "native-http"}
        for request in (start, wait):
            self.assertEqual(request, launcher.validate_request(json.dumps(request).encode() + b"\n"))
            for key in tuple(request):
                missing = dict(request); del missing[key]
                with self.subTest(kind="missing", key=key), self.assertRaises(launcher.LauncherError):
                    launcher.validate_request(json.dumps(missing).encode() + b"\n")
                for value in (None, True, 1, [], {}):
                    wrong = {**request, key: value}
                    with self.subTest(kind="type", key=key, value=value), self.assertRaises(launcher.LauncherError):
                        launcher.validate_request(json.dumps(wrong).encode() + b"\n")
            with self.assertRaises(launcher.LauncherError):
                launcher.validate_request(json.dumps({**request, "argv": []}).encode() + b"\n")
            alias = json.dumps(request)[:-1] + ',"Op":"' + request["op"] + '"}'
            with self.assertRaises(launcher.LauncherError):
                launcher.validate_request(alias.encode() + b"\n")
        for request in ({**start, "entry_digest": "A" * 64}, {**start, "application_id": "../canary"},
                        {**wait, "lease_id": "b" * 31}, {**wait, "resource_id": "canary/path"}):
            with self.assertRaises(launcher.LauncherError):
                launcher.validate_request(json.dumps(request).encode() + b"\n")

    def test_application_selection_requires_complete_identity_and_empty_table_remains_closed(self):
        with tempfile.TemporaryDirectory() as root:
            policy = Path(root) / "policy.json"; policy.write_bytes(b"{}")
            self.assertIsNone(launcher.select_root_application(Namespace(), policy))
            valid = {"application_id": "native-app", "application_entry_digest": "a" * 64,
                     "application_profile": "native-profile"}
            with self.assertRaisesRegex(launcher._application.ApplicationError, "^ASEVD407$"):
                launcher.select_root_application(Namespace(**valid), policy)
            for key in valid:
                invalid = dict(valid); del invalid[key]
                with self.subTest(key=key), self.assertRaisesRegex(launcher._application.ApplicationError, "^ASEVD404$"):
                    launcher.select_root_application(Namespace(**invalid), policy)
            self.assertEqual((), launcher._application._COMPILED_REGISTRATIONS)

    def test_actual_app_pumps_and_producer_budget_use_one_counter_and_separate_receipts(self):
        app = launcher._application
        quota = launcher.OutputQuota()
        producer = launcher.OutputBudget(quota)
        producer.add("stdout", b"producer")
        state = app.OwnershipState()
        pumps = app.OutputPumps(1024, quota, state, threading.Event())
        pumps.start(SimpleNamespace(stdout=io.BytesIO(b"app"), stderr=io.BytesIO(b"error")))
        app_receipt = pumps.join(time.monotonic() + 2)
        self.assertEqual((8, 3, 5), app_receipt)
        self.assertEqual(16, quota.received_bytes())
        self.assertIs(quota, producer.quota)
        launcher.validate_completion_output(((8, 8, 0),), 1, quota.received_bytes(), app_receipt, True)

    def test_job_quota_overflow_in_application_output_latches_actual_shared_counter(self):
        app = launcher._application
        quota = launcher.OutputQuota(); quota.count(launcher.MAX_JOB_OUTPUT)
        state = app.OwnershipState(); abort = threading.Event()
        pumps = app.OutputPumps(1024, quota, state, abort)
        pumps.start(SimpleNamespace(stdout=io.BytesIO(b"x"), stderr=io.BytesIO()))
        with self.assertRaises(app.ApplicationError): pumps.join(time.monotonic() + 2)
        self.assertTrue(quota.exceeded.is_set()); self.assertTrue(state.failed); self.assertTrue(abort.is_set())

    def test_aggregate_receipts_reject_missing_app_and_each_byte_or_type_mismatch(self):
        launcher.validate_completion_output(((7, 4, 3),), 1, 12, (5, 2, 3), True)
        launcher.validate_completion_output(((7, 4, 3),), 1, 7, None, False)
        cases = ((((7, 4, 3),), 1, 7, None, True), (((7, 4, 3),), 1, 12, (5, 2, 3), False),
                 (((7, 4, 3),), 1, 11, (5, 2, 3), True), (((7, 4, 3),), 1, 12, (5, 2, 2), True),
                 (((7, 4, 3),), 1, 12, (5, -1, 6), True), (((7, 4, 3),), 1, 12, [5, 2, 3], True),
                 (((7, 4, 3),), 1, 12, (5, True, 4), True), (((7, 4, 3),), 2, 12, (5, 2, 3), True),
                 (((7, 4, 3),), 1, launcher.MAX_JOB_OUTPUT + 1, (5, 2, 3), True))
        for values in cases:
            with self.subTest(values=values), self.assertRaisesRegex(launcher.LauncherError, "completion-output-unconfirmed"):
                launcher.validate_completion_output(*values)

    def test_stop_wait_deadline_cannot_reset_after_first_closure(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                with patch.object(launcher.time, "monotonic", return_value=10):
                    first = broker._close_work_gate()
                with patch.object(launcher.time, "monotonic", return_value=11):
                    self.assertEqual(first, broker._close_work_gate())
                self.assertEqual(12, first); self.assertTrue(broker.work_closed)
            finally: broker.close_artifact_handles()

    def test_v1_broker_rejects_application_request_without_running_any_application(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                broker.ready_seen = True
                response = broker_request(broker, {"op": "application-start",
                    "application_id": "native-app", "entry_digest": "a" * 64})
                self.assertEqual({"ok": False, "code": "ASEVD407"}, response)
                self.assertEqual(0, broker.active_application_operations)
                self.assertIsNone(broker.application); self.assertIsNone(broker.application_output_receipt)
            finally: broker.close_artifact_handles()

    def test_producer_artifact_idle_interval_does_not_join_or_include_app_operations(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                broker.active_application_operations = 1  # Data-only lifecycle shape, not a lease.
                with patch.object(broker, "_all_subject_groups_empty", return_value=True) as producer_guard:
                    broker._begin_artifact_operation()
                producer_guard.assert_called_once()
                self.assertEqual(1, broker.active_artifact_operations)
                broker._end_artifact_operation()
                self.assertEqual(0, broker.active_artifact_operations)
            finally:
                broker.active_application_operations = 0
                broker.close_artifact_handles()


class FreshResultsOwnershipControls(unittest.TestCase):
    def exercise(self, *, existing_owner=None, wrong_gid=False, replacement=None, chown_error=False):
        with tempfile.TemporaryDirectory() as directory:
            scratch = Path(directory)
            output = scratch / "test-output"
            output.mkdir(mode=0o2770)
            output.chmod(0o2770)
            selected = output / "coverage-fresh"
            creator, worker, subject, gid = os.geteuid(), os.geteuid() + 1, os.geteuid() + 2, os.getegid()
            owners = {}
            modes = {}
            if existing_owner is not None:
                selected.mkdir(mode=0o700)
                entry = selected.stat()
                owners[(entry.st_dev, entry.st_ino)] = {"creator": creator, "worker": worker, "subject": subject}[existing_owner]
            parent_fd = os.open(output, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
            broker = SimpleNamespace(scratch=scratch, test_output_fd=parent_fd,
                                     worker_uid=worker, subject_uid=subject, results_gid=gid)
            original_fstat, original_stat, original_fchmod = os.fstat, os.stat, os.fchmod
            calls = []
            named_calls = 0

            def translated(info):
                fields = {name: getattr(info, name) for name in
                          ("st_dev", "st_ino", "st_mode", "st_nlink", "st_uid", "st_gid",
                           "st_size", "st_mtime_ns", "st_ctime_ns")}
                fields["st_uid"] = owners.get((info.st_dev, info.st_ino), info.st_uid)
                if (info.st_dev, info.st_ino) in modes:
                    fields["st_mode"] = stat.S_IFMT(info.st_mode) | modes[(info.st_dev, info.st_ino)]
                if wrong_gid:
                    fields["st_gid"] = gid + 1
                return SimpleNamespace(**fields)

            def retained(fd):
                return translated(original_fstat(fd))

            def named(path, *args, **kwargs):
                nonlocal named_calls
                info = translated(original_stat(path, *args, **kwargs))
                if path == "coverage-fresh":
                    named_calls += 1
                    if named_calls == replacement:
                        info.st_ino += 1
                return info

            def transfer(fd, uid, group):
                calls.append((fd, uid, group))
                if chown_error:
                    raise PermissionError("test-ownership-transfer")
                info = original_fstat(fd)
                owners[(info.st_dev, info.st_ino)] = uid

            def permissions(fd, mode):
                self.assertEqual(0o2770, mode)
                original_fchmod(fd, mode)
                # The macOS sandbox strips setgid; record the privileged Linux mode call.
                info = original_fstat(fd)
                modes[(info.st_dev, info.st_ino)] = mode

            failed = existing_owner == "creator" or wrong_gid or replacement is not None or chown_error
            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(launcher.os, "fstat", side_effect=retained), \
                     patch.object(launcher.os, "stat", side_effect=named), \
                     patch.object(launcher.os, "fchown", side_effect=transfer), \
                     patch.object(launcher.os, "fchmod", side_effect=permissions):
                    if failed:
                        with self.assertRaises((launcher.LauncherError, PermissionError)):
                            launcher.Broker._validate_results_root(broker, ["test", "--results-directory", str(selected)])
                    else:
                        self.assertEqual("coverage-fresh", launcher.Broker._validate_results_root(
                            broker, ["test", "--results-directory", str(selected)]))
                self.assertEqual(len(calls), 1 if not failed or replacement == 2 or chown_error else 0)
                for fd, uid, group in calls:
                    self.assertEqual((subject, gid), (uid, group))
                    with self.assertRaises(OSError):
                        original_fstat(fd)
                if not failed:
                    self.assertEqual(0o770, stat.S_IMODE(selected.stat().st_mode) & 0o777)
                    info = selected.stat()
                    self.assertEqual(0o2770, modes[(info.st_dev, info.st_ino)])
                return calls
            finally:
                os.close(parent_fd)

    def test_fresh_creator_owned_directory_hands_off_distinct_subject(self):
        self.exercise()

    def test_existing_worker_and_subject_owners_remain_allowed(self):
        for owner in ("worker", "subject"):
            with self.subTest(owner=owner):
                self.exercise(existing_owner=owner)

    def test_existing_creator_owner_is_not_a_fresh_directory(self):
        self.exercise(existing_owner="creator")

    def test_wrong_group_rejects_fresh_and_existing_directories(self):
        for owner in (None, "subject"):
            with self.subTest(owner=owner):
                self.exercise(existing_owner=owner, wrong_gid=True)

    def test_named_replacement_before_or_after_transfer_rejects(self):
        for query in (1, 2):
            with self.subTest(query=query):
                self.exercise(replacement=query)

    def test_failed_privileged_transfer_closes_retained_fd(self):
        self.exercise(chown_error=True)


class SubjectFailurePrefixControls(unittest.TestCase):
    """Real pipes/FDs with portable ownership, never a root execution lease."""
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.broker, _, _ = artifact_broker(self.root)
        self.broker.subject_root = self.broker.scratch = self.root
        self.broker.descriptor = {"cgroup": "/system.slice/test-evidence-worker.service"}
        self.addCleanup(self.broker.close_artifact_handles)
        self.directory = self.root / "diagnostics"
        self.directory.mkdir(mode=0o700)
        self.fd = os.open(self.directory, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        self.addCleanup(os.close, self.fd)

    def run_command(self, status=1, stdout=b"out\x00\xff", stderr=b"secret-canary\xfe", *, failure=None):
        events = []
        process = SimpleNamespace(stdout=io.BytesIO(stdout), stderr=io.BytesIO(stderr), returncode=None)
        if failure == "pump":
            process.stderr = SubjectOutputPumpTests.Reads(stderr, OSError("private-read-canary"))

        def wait(timeout=None):
            self.assertIn("stop", events)
            events.append("wait")
            if failure == "wait":
                raise launcher.subprocess.TimeoutExpired("private-command", timeout)
            process.returncode = status
            return status

        process.wait = wait
        process.poll = lambda: process.returncode
        process.kill = lambda: setattr(process, "returncode", -9)

        def stop(argv, **kwargs):
            del argv, kwargs
            events.append("stop")
            return launcher.subprocess.CompletedProcess([], 0, b"", b"")

        def group_empty(group):
            del group
            self.assertIn("wait", events)
            events.append("physical-empty")
            if failure == "group":
                raise OSError("private-group-canary")
            return True

        props = {"LoadState": "loaded", "ActiveState": "active" if status == 0 else "failed",
                 "SubState": "exited" if status == 0 else "failed", "MainPID": "0",
                 "User": str(self.broker.subject_uid), "Group": str(self.broker.subject_gid),
                 "KillMode": "control-group", "ControlGroup": "",
                 "Result": "success" if status == 0 else "exit-code", "ExecMainCode": "1",
                 "ExecMainStatus": str(status)}
        with ExitStack() as stack:
            stack.enter_context(patch.object(launcher.subprocess, "Popen", return_value=process))
            stack.enter_context(patch.object(launcher.subprocess, "run", side_effect=stop))
            stack.enter_context(patch.object(self.broker, "_unit_properties", return_value=props))
            stack.enter_context(patch.object(self.broker, "_group_empty", side_effect=group_empty))
            if failure == "quota":
                stack.enter_context(patch.object(launcher._application, "MAX_JOB_OUTPUT", 1))
            if failure == "counts":
                stack.enter_context(patch.object(launcher.OutputQuota, "count", return_value=None))
            if failure:
                with self.assertRaises(launcher.LauncherError):
                    self.broker._run({"executable": str(self.broker.dotnet), "arguments": ["build", "fixture.csproj"],
                                      "working_directory": str(self.root)})
                return events
            response = self.broker._run({"executable": str(self.broker.dotnet), "arguments": ["build", "fixture.csproj"],
                                         "working_directory": str(self.root)})
        self.assertEqual(status, response["exit_code"])
        self.assertEqual(["stop", "wait", "physical-empty"], events)
        return response

    def write_portable(self, prefixes=None):
        return launcher._capture_subject_failure_prefixes_fd(
            self.fd, self.broker.failed_subject_prefixes if prefixes is None else prefixes,
            expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid())

    def closed(self):
        self.broker.ready_seen = self.broker.wait_completed = self.broker.exited = self.broker.work_closed = True

    def test_confirmed_failure_preserves_exact_binary_prefixes_and_private_modes(self):
        self.run_command()
        self.assertEqual((b"out\x00\xff", b"secret-canary\xfe"), self.broker.failed_subject_prefixes)
        self.assertTrue(self.write_portable())
        child = self.directory / launcher.SUBJECT_FAILURE_DIRECTORY
        self.assertEqual(0o700, stat.S_IMODE(child.stat().st_mode))
        self.assertEqual(set(launcher.SUBJECT_FAILURE_NAMES), set(os.listdir(child)))
        for name, content in zip(launcher.SUBJECT_FAILURE_NAMES, self.broker.failed_subject_prefixes):
            path = child / name
            self.assertEqual(content, path.read_bytes())
            info = path.stat()
            self.assertEqual((os.geteuid(), os.getegid(), 0o600, 1),
                             (info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode), info.st_nlink))
        record = launcher.failure_diagnostic(launcher.LauncherError("worker-unsuccessful"))
        self.assertNotIn("secret-canary", json.dumps(record))
        self.assertNotIn("subject_failure", json.dumps(record))

    def test_success_has_no_pair_and_cannot_create_capture(self):
        self.run_command(status=0)
        self.closed()
        self.assertIsNone(self.broker.failed_subject_prefixes)
        with patch.object(launcher, "_capture_subject_failure_prefixes_fd") as write:
            self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
        write.assert_not_called()
        self.assertFalse((self.directory / launcher.SUBJECT_FAILURE_DIRECTORY).exists())

    def test_only_last_confirmed_nonzero_replaces_pair(self):
        self.run_command(stdout=b"first", stderr=b"first-error")
        self.run_command(status=0, stdout=b"successful", stderr=b"")
        self.assertEqual((b"first", b"first-error"), self.broker.failed_subject_prefixes)
        self.run_command(status=17, stdout=b"last\x00", stderr=b"last\xff")
        self.assertEqual((b"last\x00", b"last\xff"), self.broker.failed_subject_prefixes)
        self.assertTrue(self.write_portable())
        self.assertEqual(b"last\xff", (self.directory / launcher.SUBJECT_FAILURE_DIRECTORY / "stderr.prefix").read_bytes())
        self.run_command(stdout=b"unconfirmed", failure="group")
        self.assertEqual((b"last\x00", b"last\xff"), self.broker.failed_subject_prefixes)

    def test_prefix_cap_does_not_change_received_quota_or_response_cap(self):
        out = b"a" * (launcher.MAX_PREFIX + 11)
        err = b"b" * (launcher.MAX_PREFIX + 13)
        response = self.run_command(stdout=out, stderr=err)
        self.assertEqual(len(out) + len(err), self.broker.output_quota.received_bytes())
        self.assertEqual(launcher.MAX_PREFIX, len(response["stdout"]))
        self.assertEqual((519168, 519168), tuple(map(len, self.broker.failed_subject_prefixes)))
        self.assertTrue(self.write_portable())
        self.assertEqual(2 * 519168, sum((self.directory / launcher.SUBJECT_FAILURE_DIRECTORY / name).stat().st_size
                                       for name in launcher.SUBJECT_FAILURE_NAMES))

    def test_unconfirmed_wait_pump_counts_quota_or_group_never_retains_pair(self):
        for failure in ("wait", "pump", "counts", "quota", "group"):
            with self.subTest(failure=failure):
                # Each failure starts from a distinct actual broker and quota.
                old = self.broker
                self.broker, _, _ = artifact_broker(self.root / failure)
                self.broker.subject_root = self.broker.scratch = self.root
                try:
                    self.run_command(failure=failure)
                    self.assertIsNone(self.broker.failed_subject_prefixes)
                finally:
                    self.broker.close_artifact_handles()
                    self.broker = old

    def test_closed_positive_reaches_fd_writer_once_without_exposing_capture_state(self):
        self.run_command()
        self.closed()
        with patch.object(self.broker, "_group_empty", return_value=True) as group, \
             patch.object(launcher, "_capture_subject_failure_prefixes_fd", return_value=True) as write:
            self.assertTrue(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
        group.assert_called_once_with(self.broker.descriptor["cgroup"])
        write.assert_called_once_with(self.fd, (b"out\x00\xff", b"secret-canary\xfe"))

    def test_each_invalid_checkpoint_or_active_operation_blocks_writer(self):
        self.run_command()
        self.closed()
        changes = [(name, False) for name in ("ready_seen", "wait_completed", "exited", "work_closed")]
        changes += [(name, value) for name in ("active_handlers", "active_runs", "active_artifact_operations",
                                               "active_application_operations") for value in (1, False)]
        changes += [("subject_output_failed", True), ("application_work_failed", True)]
        for name, value in changes:
            with self.subTest(name=name, value=value), \
                 patch.object(self.broker, name, value), \
                 patch.object(launcher, "_capture_subject_failure_prefixes_fd") as write:
                self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
                write.assert_not_called()

    def test_missing_owned_exit_exceeded_quota_or_live_worker_blocks_capture(self):
        self.run_command()
        self.closed()
        for owned in (False, None, 1):
            with self.subTest(owned=owned), patch.object(launcher, "_capture_subject_failure_prefixes_fd") as write:
                self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, owned))
                write.assert_not_called()
        self.broker.output_quota.exceeded.set()
        with patch.object(launcher, "_capture_subject_failure_prefixes_fd") as write:
            self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
            write.assert_not_called()
        self.broker.output_quota.exceeded.clear()
        with patch.object(self.broker, "_group_empty", return_value=False), \
             patch.object(launcher, "_capture_subject_failure_prefixes_fd") as write:
            self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
            write.assert_not_called()

    def test_invalid_pair_and_wrong_parent_owner_or_permissions_create_nothing(self):
        for pair in (None, [b"a", b"b"], (b"a",), (bytearray(b"a"), b"b"),
                     (b"a" * (launcher.MAX_SUBJECT_FAILURE_PREFIX + 1), b"b")):
            with self.subTest(pair_type=type(pair).__name__):
                self.assertFalse(launcher._capture_subject_failure_prefixes_fd(
                    self.fd, pair, expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid()))
        self.assertFalse(launcher._capture_subject_failure_prefixes_fd(
            self.fd, (b"a", b"b"), expected_owner_uid=os.geteuid() + 1, expected_owner_gid=os.getegid()))
        os.chmod(self.directory, 0o770)
        self.assertFalse(self.write_portable((b"a", b"b")))
        self.assertFalse((self.directory / launcher.SUBJECT_FAILURE_DIRECTORY).exists())

    def test_existing_directory_or_symlink_is_not_adopted_or_overwritten(self):
        child = self.directory / launcher.SUBJECT_FAILURE_DIRECTORY
        for kind in ("directory", "symlink"):
            with self.subTest(kind=kind):
                if kind == "directory":
                    child.mkdir(mode=0o700)
                    (child / "stdout.prefix").write_bytes(b"existing")
                else:
                    child.symlink_to(self.root / "test-output", target_is_directory=True)
                self.assertFalse(self.write_portable((b"new", b"new")))
                if kind == "directory":
                    self.assertEqual(b"existing", (child / "stdout.prefix").read_bytes())
                    (child / "stdout.prefix").unlink(); child.rmdir()
                else:
                    self.assertTrue(child.is_symlink()); child.unlink()

    def test_file_symlink_and_hardlink_collisions_preserve_target(self):
        target = self.root / "target"
        target.write_bytes(b"untouched-canary")
        real_open = os.open
        for kind in ("symlink", "hardlink"):
            with self.subTest(kind=kind):
                child = self.directory / launcher.SUBJECT_FAILURE_DIRECTORY
                def collision(path, flags, *args, **kwargs):
                    if path == "stdout.prefix":
                        if kind == "symlink": (child / path).symlink_to(target)
                        else: os.link(target, child / path)
                    return real_open(path, flags, *args, **kwargs)
                with patch.object(launcher.os, "open", side_effect=collision):
                    self.assertFalse(self.write_portable((b"new", b"new")))
                self.assertEqual(b"untouched-canary", target.read_bytes())
                (child / "stdout.prefix").unlink(); child.rmdir()

    def test_short_write_error_closes_fds_and_removes_owned_partial_files(self):
        real_open = os.open; descriptors = []
        def opened(*args, **kwargs):
            fd = real_open(*args, **kwargs); descriptors.append(fd); return fd
        with patch.object(launcher.os, "open", side_effect=opened), \
             patch.object(launcher.os, "write", return_value=0):
            self.assertFalse(self.write_portable((b"a", b"b")))
        self.assertFalse((self.directory / launcher.SUBJECT_FAILURE_DIRECTORY).exists())
        for fd in descriptors:
            with self.assertRaises(OSError): os.fstat(fd)

    def test_named_file_substitution_is_rejected_without_deleting_replacement(self):
        real_write = os.write; replaced = False
        child = self.directory / launcher.SUBJECT_FAILURE_DIRECTORY
        def write(fd, data):
            nonlocal replaced
            count = real_write(fd, data)
            if not replaced:
                replaced = True
                (child / "stdout.prefix").unlink()
                (child / "stdout.prefix").write_bytes(b"replacement-canary")
                os.chmod(child / "stdout.prefix", 0o600)
            return count
        with patch.object(launcher.os, "write", side_effect=write):
            self.assertFalse(self.write_portable((b"a", b"b")))
        self.assertEqual(b"replacement-canary", (child / "stdout.prefix").read_bytes())

    def test_named_directory_substitution_does_not_adopt_or_delete_replacement(self):
        real_write = os.write; replaced = False
        child = self.directory / launcher.SUBJECT_FAILURE_DIRECTORY
        retained = self.directory / "retained-original"
        def write(fd, data):
            nonlocal replaced
            count = real_write(fd, data)
            if not replaced:
                replaced = True
                child.rename(retained)
                child.mkdir(mode=0o700)
                (child / "replacement").write_bytes(b"untouched")
            return count
        with patch.object(launcher.os, "write", side_effect=write):
            self.assertFalse(self.write_portable((b"a", b"b")))
        self.assertEqual(b"untouched", (child / "replacement").read_bytes())
        self.assertTrue(retained.is_dir())

    def test_file_mode_change_and_parent_identity_change_reject_capture(self):
        for change in ("file-mode", "parent-mode"):
            with self.subTest(change=change):
                real_write = os.write
                def write(fd, data):
                    count = real_write(fd, data)
                    os.fchmod(fd, 0o660) if change == "file-mode" else os.chmod(self.directory, 0o750)
                    return count
                with patch.object(launcher.os, "write", side_effect=write):
                    self.assertFalse(self.write_portable((b"a", b"b")))
                os.chmod(self.directory, 0o700)
                self.assertFalse((self.directory / launcher.SUBJECT_FAILURE_DIRECTORY).exists())

    def test_capture_io_failure_preserves_original_failure_and_closed_public_schema(self):
        self.run_command(); self.closed()
        error = launcher.LauncherError("worker-unsuccessful")
        before = launcher.failure_diagnostic(error)
        with patch.object(self.broker, "_group_empty", return_value=True), \
             patch.object(launcher, "_capture_subject_failure_prefixes_fd", side_effect=OSError("secret-canary")):
            self.assertFalse(launcher.capture_subject_failure_prefixes(self.broker, self.fd, True))
        self.assertEqual(before, launcher.failure_diagnostic(error))
        self.assertNotIn("secret-canary", json.dumps(before))


class OwnedExitDiagnosticControls(unittest.TestCase):
    """Closed negative/procedure and real-FD data controls; no positive protected app lease."""
    def test_negative_wait_ack_latches_first_failure_without_public_schema_change(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                broker.subject_output_failed = True
                response = broker_request(broker, {"op": "wait"})
                self.assertEqual({"ok": True, "owned_exit": False}, response)
                first = broker.owned_exit_diagnostic
                record = launcher.validate_owned_exit_diagnostic(json.loads(first))
                self.assertEqual("output-latched", record["category"])
                self.assertTrue(record["broker_subject_output_failed"])
                broker._latch_owned_exit_failure("deadline", OSError("private-canary"))
                self.assertEqual(first, broker.owned_exit_diagnostic)
                self.assertFalse(broker.wait_completed)
                self.assertNotIn("private-canary", first.decode())
                error = launcher.LauncherError("worker-protocol-incomplete")
                self.assertNotIn("join_phase", launcher.failure_diagnostic(error))
            finally: broker.close_artifact_handles()

    def test_app_join_fault_inspection_and_deadline_preserve_negative_decisions(self):
        for category in ("app-join-fault", "inspection", "deadline"):
            with self.subTest(category=category), tempfile.TemporaryDirectory() as root:
                broker, _, _ = artifact_broker(root)
                try:
                    broker.deadline = 10
                    if category == "app-join-fault":
                        class FailedJoinProcedure:
                            def join(self, deadline): raise OSError("private-canary")
                        broker.application = FailedJoinProcedure()  # Negative procedure only; no lease.
                    failure = OSError("private-canary") if category == "inspection" else None
                    with patch.object(launcher.time, "monotonic", return_value=10), patch.object(
                            broker, "_all_owned_work_stopped", side_effect=failure, return_value=False):
                        self.assertFalse(broker._wait_for_owned_exit())
                    record = json.loads(broker.owned_exit_diagnostic)
                    self.assertEqual(category, record["category"])
                    self.assertEqual("none" if category == "deadline" else "os", record["error_class"])
                    self.assertNotIn("private-canary", broker.owned_exit_diagnostic.decode())
                finally: broker.application = None; broker.close_artifact_handles()

    def test_error_class_and_schema_never_copy_exception_args_or_unknown_types(self):
        class Unknown(OSError): pass
        for error, expected in ((Unknown("private-canary"), "unknown"),
                (launcher._application.ApplicationError(), "application"),
                (launcher.subprocess.TimeoutExpired("private-canary", 1), "subprocess-timeout"),
                (launcher.LauncherError("private-canary"), "launcher")):
            self.assertEqual(expected, launcher._owned_exit_error_class(error))
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                broker._latch_owned_exit_failure("deadline")
                good = json.loads(broker.owned_exit_diagnostic)
                for change in ({"join_phase": ["private-canary"]}, {"category": "private-canary"},
                               {"application_process_code": True}, {"application_process_code": 256},
                               {"broker_active_runs": -1}, {"application_stdout_eof": 1}, {"extra": "canary"}):
                    with self.subTest(change=change), self.assertRaises(ValueError):
                        launcher.validate_owned_exit_diagnostic({**good, **change})
            finally: broker.close_artifact_handles()

    def test_private_exclusive_fd_capture_and_existing_symlink_do_not_change_failure(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            directory = Path(root)/"diagnostics"; directory.mkdir(mode=0o700)
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                broker._latch_owned_exit_failure("deadline")
                kwargs = dict(expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid())
                capture = launcher._capture_owned_exit_diagnostic_fd
                self.assertTrue(capture(fd, broker.owned_exit_diagnostic, **kwargs))
                path = directory/launcher.OWNED_EXIT_DIAGNOSTIC_FILE
                self.assertEqual(broker.owned_exit_diagnostic, path.read_bytes())
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                self.assertEqual(1, path.stat().st_nlink)
                self.assertFalse(capture(fd, broker.owned_exit_diagnostic, **kwargs))
                path.unlink(); outside = Path(root)/"outside"; outside.write_bytes(b"private-canary")
                path.symlink_to(outside)
                self.assertFalse(capture(fd, broker.owned_exit_diagnostic, **kwargs))
                self.assertEqual(b"private-canary", outside.read_bytes())
                path.unlink()
                for bad in (b"x"*4097, b"{}", b'{"schema":1,"schema":1}'):
                    self.assertFalse(capture(fd, bad, **kwargs))
                    self.assertFalse(path.exists())
                directory.chmod(0o777)
                self.assertFalse(capture(fd, broker.owned_exit_diagnostic, **kwargs))
                directory.chmod(0o700)
                self.assertFalse(capture(fd, broker.owned_exit_diagnostic,
                    expected_owner_uid=os.geteuid()+1, expected_owner_gid=os.getegid()))
                with patch.object(launcher.os, "write", side_effect=OSError("private-canary")):
                    self.assertFalse(capture(fd, broker.owned_exit_diagnostic, **kwargs))
            finally: os.close(fd); broker.close_artifact_handles()

    def test_capture_absence_and_successful_worker_do_no_new_io(self):
        with patch.object(launcher.os, "fstat") as inspect, patch.object(launcher.os, "open") as opened:
            self.assertFalse(launcher._capture_owned_exit_diagnostic_fd(None, b"{}"))
            self.assertFalse(launcher._capture_owned_exit_diagnostic_fd(123, None))
            inspect.assert_not_called(); opened.assert_not_called()
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                with patch.object(launcher, "_capture_owned_exit_diagnostic_fd") as capture:
                    launcher.require_successful_worker({"Result": "success", "User": "worker",
                        "KillMode": "control-group"}, "worker", broker, 123)
                capture.assert_not_called()
                self.assertIsNone(broker.owned_exit_diagnostic)
                with patch.object(broker, "_all_owned_work_stopped", return_value=True):
                    self.assertEqual({"ok": True, "owned_exit": True}, broker_request(broker, {"op": "wait"}))
                self.assertIsNone(broker.owned_exit_diagnostic)
                broker._latch_owned_exit_failure("app-join-fault", OSError("private-canary"))
                with patch.object(launcher, "_capture_owned_exit_diagnostic_fd", return_value=False) as capture:
                    with self.assertRaises(launcher.LauncherError) as caught:
                        launcher.require_successful_worker({"Result": "exit-code", "User": "worker",
                            "KillMode": "control-group"}, "worker", broker, None)
                capture.assert_called_once_with(None, broker.owned_exit_diagnostic)
                self.assertEqual("worker-unsuccessful", launcher.failure_diagnostic(caught.exception)["cause"])
                self.assertNotIn("private-canary", json.dumps(launcher.failure_diagnostic(caught.exception)))
            finally: broker.close_artifact_handles()


class StartupRecordControls(unittest.TestCase):
    """Closed private record/FD and negative protocol controls only; no fabricated app lease."""
    def test_startup_schema_exact_fields_bounds_canary_and_private_capture(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            directory = Path(root)/"diagnostics"; directory.mkdir(mode=0o700)
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                broker._latch_owned_exit_failure("deadline")
                base = json.loads(broker.owned_exit_diagnostic)
                self.assertEqual("issue779-owned-exit-diagnostic-v2", base["schema"])
                state = launcher._application.OwnershipState()
                state.startup_diagnostics.phase = "dotnet-validation"
                state.startup_diagnostics.capture(PermissionError(13, "private-startup-canary"))
                record = {**base, **state.startup_diagnostics.snapshot()}
                self.assertEqual(record, launcher.validate_owned_exit_diagnostic(record))
                data = json.dumps(record, sort_keys=True).encode(); self.assertLessEqual(len(data), 4096)
                self.assertTrue(launcher._capture_owned_exit_diagnostic_fd(fd, data,
                    expected_owner_uid=os.geteuid(), expected_owner_gid=os.getegid()))
                self.assertEqual(data, (directory/launcher.OWNED_EXIT_DIAGNOSTIC_FILE).read_bytes())
                self.assertNotIn("private-startup-canary", data.decode())
                for changes in ({"startup_phase": ["canary"]}, {"startup_phase": "canary"},
                        {"startup_error_class": "PermissionError-canary"}, {"startup_errno": True},
                        {"startup_errno": 0}, {"startup_errno": 4096},
                        {"schema": "issue779-owned-exit-diagnostic-v1"}):
                    with self.subTest(changes=changes), self.assertRaises(ValueError):
                        launcher.validate_owned_exit_diagnostic({**record, **changes})
                missing = dict(record); del missing["startup_phase"]
                with self.assertRaises(ValueError): launcher.validate_owned_exit_diagnostic(missing)
            finally: os.close(fd); broker.close_artifact_handles()

    def test_startup_observations_cannot_change_negative_ack_or_add_success_io(self):
        with tempfile.TemporaryDirectory() as root:
            broker, _, _ = artifact_broker(root)
            try:
                broker.application_work_failed = True
                with patch.object(launcher, "_capture_owned_exit_diagnostic_fd") as capture:
                    self.assertEqual({"ok": True, "owned_exit": False}, broker_request(broker, {"op": "wait"}))
                    capture.assert_not_called()
                self.assertFalse(broker.wait_completed)
                record = json.loads(broker.owned_exit_diagnostic)
                self.assertEqual("output-latched", record["category"])
                self.assertEqual("not-started", record["startup_phase"])
                self.assertIsNone(record["startup_errno"])
                with patch.object(launcher, "_capture_owned_exit_diagnostic_fd") as capture:
                    launcher.require_successful_worker({"Result": "success", "User": "worker",
                        "KillMode": "control-group"}, "worker", broker, 123)
                    capture.assert_not_called()
            finally: broker.close_artifact_handles()


class PrivateCollectorSamplingOrchestrationControls(unittest.TestCase):
    """Procedure and real-FD controls; all root/kernel/sampler facts are doubles.

    These controls create no protected supervisor, admission or positive proof.
    """
    exercise = SubjectUnitCompletionTests.exercise

    def test_pending_sampler_precedes_launch_and_only_observes_validated_running_unit(self):
        calls = []
        sampler = unittest.mock.Mock()
        sampler.finish.return_value = None

        def pending(*args):
            self.assertFalse(launcher.subprocess.Popen.called)
            calls.append('pending')
            return sampler

        def observe():
            self.assertTrue(launcher.subprocess.Popen.called)
            calls.append('observe')

        sampler.observe.side_effect = observe
        sampler.finish.side_effect = lambda: calls.append('finish')
        with patch.object(launcher._collector, 'CollectorStartupSampler', side_effect=pending):
            response, _, registered, receipts = self.exercise(change='running-first')
        self.assertEqual(['pending', 'observe', 'finish'], calls)
        self.assertTrue(response['ok'])
        self.assertEqual(0, response['exit_code'])
        self.assertTrue(registered)
        self.assertEqual([(6, 3, 3)], receipts)

    def test_unvalidated_unit_never_reaches_observer(self):
        for change in ({'User': '0'}, {'Group': '0'}, {'ControlGroup': '/system.slice/foreign.service'}):
            with self.subTest(change=change):
                sampler = unittest.mock.Mock()
                sampler.finish.return_value = None
                with patch.object(launcher._collector, 'CollectorStartupSampler', return_value=sampler):
                    response, _, registered, receipts = self.exercise(change=change)
                sampler.observe.assert_not_called()
                self.assertFalse(response['ok'])
                self.assertFalse(registered)
                self.assertEqual([], receipts)

    def test_sampler_creation_observation_or_finish_error_cannot_replace_command_result(self):
        for stage in ('create', 'observe', 'finish'):
            with self.subTest(stage=stage):
                sampler = unittest.mock.Mock()
                sampler.finish.return_value = None
                if stage != 'create':
                    getattr(sampler, stage).side_effect = OSError('diagnostic-canary')
                with patch.object(launcher._collector, 'CollectorStartupSampler',
                                  side_effect=OSError('diagnostic-canary') if stage == 'create' else None,
                                  return_value=sampler):
                    response, _, registered, receipts = self.exercise(status=17, change='running-first')
                self.assertTrue(response['ok'])
                self.assertEqual(17, response['exit_code'])
                self.assertTrue(registered)
                self.assertEqual([(6, 3, 3)], receipts)
                self.assertNotIn('diagnostic-canary', json.dumps(response))

    def prepare_capture(self):
        temporary = tempfile.TemporaryDirectory(prefix='collector-capture-')
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        broker, _, _ = artifact_broker(root)
        self.addCleanup(broker.close_artifact_handles)
        broker.descriptor = {'cgroup': '/system.slice/worker-fixture.service'}
        for field in ('ready_seen', 'wait_completed', 'exited', 'work_closed'):
            setattr(broker, field, True)
        broker.units = [('test-evidence-s-0.service', '/system.slice/test-evidence-s-0.service')]
        broker.private_collector_snapshot = json.dumps({
            'unit': broker.units[0][0], 'cgroup': broker.units[0][1],
            'subject_uid': broker.subject_uid, 'subject_gid': broker.subject_gid}).encode()
        destination = root / 'private-diagnostics'
        destination.mkdir(mode=0o700)
        fd = os.open(destination, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        self.addCleanup(os.close, fd)
        return broker, destination, fd

    def test_private_snapshot_write_occurs_only_after_real_resource_close(self):
        broker, directory, fd = self.prepare_capture()
        source_fd = broker.test_output_fd
        actual_fstat = os.fstat

        def root_stat(descriptor):
            info = actual_fstat(descriptor)
            fields = list(info)
            fields[4] = 0
            return os.stat_result(fields)

        def traces(*args, **kwargs):
            with self.assertRaises(OSError):
                actual_fstat(source_fd)
            path = directory / 'subject-collector-startup.json'
            self.assertEqual(broker.private_collector_snapshot, path.read_bytes())
            self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
            return True

        with ExitStack() as stack:
            stack.enter_context(patch.object(launcher.os, 'geteuid', return_value=0))
            stack.enter_context(patch.object(launcher.os, 'fstat', side_effect=root_stat))
            stack.enter_context(patch.object(broker, '_group_empty', return_value=True))
            stack.enter_context(patch.object(broker, '_all_owned_work_stopped', return_value=True))
            stack.enter_context(patch.object(launcher._collector, 'validate_snapshot', return_value=True))
            trace_writer = stack.enter_context(patch.object(launcher._vstest, 'capture_traces', side_effect=traces))
            launcher._close_failed_launch_resources(None, [], broker, -1, fd, True, RuntimeError('original'))
        trace_writer.assert_called_once()
        self.assertEqual(-1, broker.test_output_fd)
        os.fstat(fd)

    def test_invalid_checkpoint_identity_payload_or_deadline_prevents_snapshot_open(self):
        broker, _, fd = self.prepare_capture()
        original = broker.private_collector_snapshot
        checkpoint = launcher._vstest_failure_checkpoint(broker, True)
        changes = [('ready_seen', False), ('wait_completed', False), ('exited', False),
                   ('work_closed', False), ('active_runs', 1), ('active_handlers', 1),
                   ('subject_output_failed', True), ('application_work_failed', True)]
        for field, value in changes:
            with self.subTest(field=field), ExitStack() as stack:
                old = getattr(broker, field)
                stack.callback(setattr, broker, field, old)
                setattr(broker, field, value)
                stack.enter_context(patch.object(launcher.os, 'geteuid', return_value=0))
                opened = stack.enter_context(patch.object(launcher.os, 'open'))
                self.assertFalse(launcher._capture_private_collector_snapshot(
                    broker, fd, True, (-1, checkpoint, time.monotonic()+1)))
                opened.assert_not_called()
        for kind in ('unowned', 'null-checkpoint', 'expired', 'foreign-unit', 'wrong-uid', 'bad-json', 'invalid-schema'):
            with self.subTest(kind=kind), ExitStack() as stack:
                stack.callback(setattr, broker, 'private_collector_snapshot', original)
                identity = json.loads(original)
                if kind == 'foreign-unit': identity['unit'] = 'foreign.service'
                if kind == 'wrong-uid': identity['subject_uid'] = broker.subject_uid + 1
                broker.private_collector_snapshot = b'canary' if kind == 'bad-json' else json.dumps(identity).encode()
                stack.enter_context(patch.object(launcher.os, 'geteuid', return_value=0))
                stack.enter_context(patch.object(launcher._collector, 'validate_snapshot', return_value=kind != 'invalid-schema'))
                stack.enter_context(patch.object(broker, '_group_empty', return_value=True))
                stack.enter_context(patch.object(broker, '_all_owned_work_stopped', return_value=True))
                opened = stack.enter_context(patch.object(launcher.os, 'open'))
                self.assertFalse(launcher._capture_private_collector_snapshot(
                    broker, fd, kind != 'unowned',
                    (-1, None if kind == 'null-checkpoint' else checkpoint,
                     time.monotonic()-1 if kind == 'expired' else time.monotonic()+1)))
                opened.assert_not_called()

    def test_ordinary_success_never_opens_private_snapshot_destination(self):
        broker, _, fd = self.prepare_capture()
        with patch.object(launcher, '_capture_private_collector_snapshot') as capture:
            launcher._close_failed_launch_resources(None, [], broker, -1, fd, True, None)
        capture.assert_not_called()


class PrivateVstestDiagnosticOrchestrationControls(unittest.TestCase):
    """Portable real-FD procedure controls, no actual root/systemd/exit proof.

    Root identity, kernel group inspection and the diagnostic module are doubles.
    The broker, its condition, duplicate and close operations use real objects/FDs.
    """
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="vstest-orchestration-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.broker, _, _ = artifact_broker(self.root)
        self.addCleanup(self.broker.close_artifact_handles)
        self.broker.descriptor = {"cgroup": "/system.slice/fixture.service"}
        for field in ("ready_seen", "wait_completed", "exited", "work_closed"):
            setattr(self.broker, field, True)
        directory = self.root / "private-diagnostics"
        directory.mkdir(mode=0o700)
        self.fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | os.O_CLOEXEC)
        self.addCleanup(os.close, self.fd)

    def test_duplicate_under_lock_precedes_real_close_and_capture_borrows_destination(self):
        events, duplicates = [], []
        original_dup = os.dup
        original_close = launcher._close_launch_resources
        source_fd = self.broker.test_output_fd

        def duplicate(fd):
            self.assertEqual(source_fd, fd)
            acquired = self.broker.lock.acquire(blocking=False)
            if acquired: self.broker.lock.release()
            self.assertFalse(acquired)
            events.append("duplicate")
            duplicates.append(original_dup(fd))
            return duplicates[-1]

        def close(*args):
            events.append("close")
            original_close(*args)
            self.assertEqual(-1, self.broker.test_output_fd)
            with self.assertRaises(OSError): os.fstat(source_fd)

        def capture(fd, tokens, destination_fd, subject_uid, results_gid, *, deadline):
            events.append("capture")
            self.assertEqual(duplicates, [fd])
            self.assertEqual(("run-1",), tokens)
            self.assertEqual(self.fd, destination_fd)
            self.assertEqual((self.broker.subject_uid, self.broker.results_gid), (subject_uid, results_gid))
            self.assertIn("run-1", os.listdir(fd))
            os.fstat(destination_fd)
            self.assertGreater(deadline, time.monotonic())
            return True

        with ExitStack() as stack:
            stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
            stack.enter_context(patch.object(self.broker, "_group_empty", return_value=True))
            stack.enter_context(patch.object(self.broker, "_all_owned_work_stopped", return_value=True))
            stack.enter_context(patch.object(launcher.os, "dup", side_effect=duplicate))
            stack.enter_context(patch.object(launcher, "_close_launch_resources", side_effect=close))
            writer = stack.enter_context(patch.object(launcher._vstest, "capture_traces", side_effect=capture))
            launcher._close_failed_launch_resources(None, [], self.broker, -1, self.fd, True,
                                                   RuntimeError("original-failure"))
        self.assertEqual(["duplicate", "close", "capture"], events)
        writer.assert_called_once()
        with self.assertRaises(OSError): os.fstat(duplicates[0])
        os.fstat(self.fd)

    def test_each_open_checkpoint_busy_counter_or_output_failure_rejects_before_duplicate(self):
        changes = [(field, False) for field in ("ready_seen", "wait_completed", "exited", "work_closed")]
        changes += [(field, value) for field in ("active_handlers", "active_runs", "active_artifact_operations",
                                                "active_application_operations") for value in (1, True)]
        changes += [("subject_output_failed", True), ("subject_output_failed", None),
                    ("application_work_failed", True), ("application_work_failed", None),
                    ("allowed_results_roots", set()), ("allowed_results_roots", {"run-1", "run-2"}),
                    ("allowed_results_roots", ("run-1",))]
        for field, value in changes:
            with self.subTest(field=field, value=value), ExitStack() as stack:
                old = getattr(self.broker, field)
                stack.callback(setattr, self.broker, field, old)
                setattr(self.broker, field, value)
                stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
                group = stack.enter_context(patch.object(self.broker, "_group_empty"))
                duplicate = stack.enter_context(patch.object(launcher.os, "dup"))
                self.assertIsNone(launcher._retain_vstest_failure_input(self.broker, self.fd, True))
                group.assert_not_called()
                duplicate.assert_not_called()
        self.broker.output_quota.exceeded.set()
        try:
            with patch.object(launcher.os, "geteuid", return_value=0), patch.object(launcher.os, "dup") as duplicate:
                self.assertIsNone(launcher._retain_vstest_failure_input(self.broker, self.fd, True))
                duplicate.assert_not_called()
        finally: self.broker.output_quota.exceeded.clear()

    def test_root_wait_destination_and_physical_group_guards_precede_duplicate(self):
        for uid, owned, destination, worker_empty, all_empty in (
                (123, True, self.fd, True, True), (0, False, self.fd, True, True),
                (0, True, None, True, True), (0, True, True, True, True),
                (0, True, self.fd, False, True), (0, True, self.fd, True, False)):
            with self.subTest(uid=uid, owned=owned, destination=destination,
                              worker_empty=worker_empty, all_empty=all_empty), ExitStack() as stack:
                stack.enter_context(patch.object(launcher.os, "geteuid", return_value=uid))
                stack.enter_context(patch.object(self.broker, "_group_empty", return_value=worker_empty))
                stack.enter_context(patch.object(self.broker, "_all_owned_work_stopped", return_value=all_empty))
                duplicate = stack.enter_context(patch.object(launcher.os, "dup"))
                self.assertIsNone(launcher._retain_vstest_failure_input(self.broker, destination, owned))
                duplicate.assert_not_called()

    def test_capture_exception_never_replaces_original_failure_and_duplicate_always_closes(self):
        for failure in (OSError("private-capture-canary"), KeyboardInterrupt("private-capture-canary")):
            with self.subTest(error_class=type(failure).__name__), tempfile.TemporaryDirectory(dir=self.root) as temp:
                broker, _, _ = artifact_broker(temp)
                broker.descriptor = dict(self.broker.descriptor)
                for field in ("ready_seen", "wait_completed", "exited", "work_closed"): setattr(broker, field, True)
                original = RuntimeError("original-private-failure")
                seen = []
                def capture(fd, *args, **kwargs):
                    seen.append(fd)
                    os.fstat(fd)
                    raise failure
                try:
                    with ExitStack() as stack:
                        stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
                        stack.enter_context(patch.object(broker, "_group_empty", return_value=True))
                        stack.enter_context(patch.object(broker, "_all_owned_work_stopped", return_value=True))
                        stack.enter_context(patch.object(launcher._vstest, "capture_traces", side_effect=capture))
                        with self.assertRaises(RuntimeError) as caught:
                            try: raise original
                            finally:
                                launcher._close_failed_launch_resources(None, [], broker, -1, self.fd, True, original)
                    self.assertIs(original, caught.exception)
                    self.assertEqual(1, len(seen))
                    self.assertEqual(-1, broker.test_output_fd)
                    with self.assertRaises(OSError): os.fstat(seen[0])
                finally: broker.close_artifact_handles()

    def test_resource_close_failure_skips_capture_and_closes_retained_duplicate(self):
        duplicates = []
        original_dup = os.dup
        original_close = launcher._close_launch_resources
        def duplicate(fd):
            duplicates.append(original_dup(fd))
            return duplicates[-1]
        def fail_close(*args):
            original_close(*args)
            raise OSError("resource-close-failure-canary")
        with ExitStack() as stack:
            stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
            stack.enter_context(patch.object(self.broker, "_group_empty", return_value=True))
            stack.enter_context(patch.object(self.broker, "_all_owned_work_stopped", return_value=True))
            stack.enter_context(patch.object(launcher.os, "dup", side_effect=duplicate))
            stack.enter_context(patch.object(launcher, "_close_launch_resources", side_effect=fail_close))
            writer = stack.enter_context(patch.object(launcher._vstest, "capture_traces"))
            with self.assertRaises(OSError):
                launcher._close_failed_launch_resources(None, [], self.broker, -1, self.fd, True,
                                                       RuntimeError("original-failure"))
            writer.assert_not_called()
        self.assertEqual(1, len(duplicates))
        with self.assertRaises(OSError): os.fstat(duplicates[0])

    def test_postclose_checkpoint_drift_blocks_capture_but_duplicate_still_closes(self):
        original_close = launcher._close_launch_resources
        original_dup = os.dup
        duplicates = []
        def duplicate(fd):
            duplicates.append(original_dup(fd))
            return duplicates[-1]
        def close(*args):
            original_close(*args)
            self.broker.active_application_operations = 1
        with ExitStack() as stack:
            stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
            stack.enter_context(patch.object(self.broker, "_group_empty", return_value=True))
            stack.enter_context(patch.object(self.broker, "_all_owned_work_stopped", return_value=True))
            stack.enter_context(patch.object(launcher.os, "dup", side_effect=duplicate))
            stack.enter_context(patch.object(launcher, "_close_launch_resources", side_effect=close))
            writer = stack.enter_context(patch.object(launcher._vstest, "capture_traces"))
            launcher._close_failed_launch_resources(None, [], self.broker, -1, self.fd, True,
                                                   RuntimeError("original-failure"))
            writer.assert_not_called()
        self.broker.active_application_operations = 0
        self.assertEqual(1, len(duplicates))
        with self.assertRaises(OSError): os.fstat(duplicates[0])


    def test_no_original_failure_or_duplicate_error_cannot_skip_close_or_invoke_reader(self):
        for scenario in ("no-original-failure", "duplicate-error"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory(dir=self.root) as temp:
                broker, _, _ = artifact_broker(temp)
                broker.descriptor = dict(self.broker.descriptor)
                for field in ("ready_seen", "wait_completed", "exited", "work_closed"): setattr(broker, field, True)
                source_fd = broker.test_output_fd
                try:
                    with ExitStack() as stack:
                        stack.enter_context(patch.object(launcher.os, "geteuid", return_value=0))
                        stack.enter_context(patch.object(broker, "_group_empty", return_value=True))
                        stack.enter_context(patch.object(broker, "_all_owned_work_stopped", return_value=True))
                        duplicate = stack.enter_context(patch.object(launcher.os, "dup", side_effect=OSError("duplicate-canary")))
                        writer = stack.enter_context(patch.object(launcher._vstest, "capture_traces"))
                        launcher._close_failed_launch_resources(None, [], broker, -1, self.fd, True,
                            None if scenario == "no-original-failure" else RuntimeError("original-failure"))
                        self.assertEqual(0 if scenario == "no-original-failure" else 1, duplicate.call_count)
                        writer.assert_not_called()
                    self.assertEqual(-1, broker.test_output_fd)
                    with self.assertRaises(OSError): os.fstat(source_fd)
                    os.fstat(self.fd)
                finally: broker.close_artifact_handles()


if __name__ == "__main__": unittest.main()
