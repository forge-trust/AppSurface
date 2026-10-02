"""Portable controls and resource HTTP probes, never native acceptance."""
import http.client
import importlib.util
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("aspire_child_proof", Path(__file__).with_name("aspire-child-proof.py"))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class ProofControls(unittest.TestCase):
    def test_combined_budget_latches_without_upgrade(self):
        budget = proof.OutputBudget(5)
        self.assertTrue(budget.consume(5))
        self.assertFalse(budget.consume(1))
        self.assertFalse(budget.consume(0))
        self.assertEqual(6, budget.count)

    def test_closed_command_restricts_identity_writes_and_protected_paths(self):
        argv = proof.service_command("issue779-child-test.service", Path("/run/payload"), Path("/run/scratch"),
                                     Path("/opt/dotnet/dotnet"), 2001, 2002, Path("/protected/tools"),
                                     Path("/protected/output"), Path("/run/control"), "normal")
        self.assertIn("--property=User=2001", argv)
        self.assertIn("--property=Group=2002", argv)
        self.assertIn("--property=KillMode=control-group", argv)
        self.assertIn("--property=ReadWritePaths=/run/scratch", argv)
        self.assertIn("--property=InaccessiblePaths=/protected/tools /protected/output /run/control", argv)
        self.assertIn("--property=PrivateNetwork=yes", argv)
        self.assertIn("-i", argv)
        self.assertEqual(1, argv.count("/run/payload/AspireChild.dll"))

    def test_root_selected_store_base_overrides_ambient_path_in_cleared_environment(self):
        import json
        scratch = Path("/run/issue779-child-selected/scratch")
        with mock.patch.dict(os.environ, {"ASPIRE__STORE__PATH": "/ambient/redirect",
                                          "ISSUE779_AMBIENT_SENTINEL": "must-not-cross"}):
            argv = proof.service_command("issue779-child-selected.service", Path("/run/payload"), scratch,
                                         Path("/opt/dotnet/dotnet"), 2001, 2002, Path("/protected/tools"),
                                         Path("/protected/output"), Path("/run/control"), "normal")
            self.assertIn(f"--property=ReadWritePaths={scratch}", argv)
            environment_start = argv.index("/usr/bin/env")
            application_start = argv.index("/opt/dotnet/dotnet", environment_start)
            environment_command = argv[environment_start:application_start]
            self.assertEqual(["/usr/bin/env", "-i"], environment_command[:2])
            self.assertEqual([f"ASPIRE__STORE__PATH={scratch / '.aspire-store'}"],
                             [value for value in environment_command if value.startswith("ASPIRE__STORE__PATH=")])
            result = subprocess.run(environment_command + [sys.executable, "-c",
                                    "import json, os; print(json.dumps({"
                                    "'store': os.environ.get('ASPIRE__STORE__PATH'),"
                                    "'ambient': os.environ.get('ISSUE779_AMBIENT_SENTINEL')}))"],
                                    check=True, capture_output=True, timeout=2)
        self.assertEqual({"store": str(scratch / ".aspire-store"), "ambient": None},
                         json.loads(result.stdout))

    def test_missing_dcp_is_a_failure_not_fake_readiness(self):
        with tempfile.TemporaryDirectory() as name:
            with self.assertRaisesRegex(ValueError, "Missing real SDK payload"):
                proof.prepare_payload(Path(name), Path(name) / "staged")

    def test_cooperative_command_signals_only_main_without_starting_stop_timer(self):
        with mock.patch.object(proof, "command") as run:
            proof.stop_unit("selected")
            run.assert_called_once_with(["systemctl", "kill", "--kill-whom=main", "--signal=TERM", "selected"], check=False)

    def test_forced_command_requests_stop_then_kills_entire_cgroup(self):
        with mock.patch.object(proof, "command") as run:
            proof.stop_unit("selected", force=True)
            self.assertEqual([
                mock.call(["systemctl", "stop", "--no-block", "selected"], check=False),
                mock.call(["systemctl", "kill", "--kill-whom=all", "--signal=KILL", "selected"], check=False),
            ], run.call_args_list)

    def test_force_kill_is_attempted_when_stop_request_times_out(self):
        with mock.patch.object(proof, "command", side_effect=[subprocess.TimeoutExpired("systemctl", 5), mock.Mock()]) as run:
            with self.assertRaises(subprocess.TimeoutExpired):
                proof.stop_unit("selected", force=True)
            self.assertEqual(mock.call(["systemctl", "kill", "--kill-whom=all", "--signal=KILL", "selected"], check=False), run.call_args_list[-1])

    def test_normal_and_cancel_reject_bad_exit_despite_empty_cgroup(self):
        for case in ("normal", "cancel"):
            with self.subTest(case=case, exit_code=0):
                receipt = {"owned_exit": True, "stop_escalated": False}
                proof.record_stop_result(receipt, case, 0)
                self.assertNotIn("failure", receipt)
            for code in (1, -9, None):
                with self.subTest(case=case, exit_code=code):
                    receipt = {"owned_exit": True, "stop_escalated": False}
                    proof.record_stop_result(receipt, case, code)
                    self.assertEqual("cooperative-stop-nonzero-exit", receipt["failure"])
                    self.assertEqual(code, receipt["process_exit_code"])

    def test_cooperative_stop_waits_and_joins_without_kill(self):
        process = mock.Mock()
        process.poll.return_value = 0
        pump = mock.Mock()
        pump.is_alive.return_value = False
        with mock.patch.object(proof, "stop_unit") as stop, mock.patch.object(proof, "cgroup_pids", return_value=set()):
            self.assertEqual((True, False), proof.stop_and_join("selected", process, "/selected", [pump]))
            stop.assert_called_once_with("selected")
            process.wait.assert_called_once()
            pump.join.assert_called_once()

    def test_stalled_stop_escalates_only_after_grace(self):
        process = mock.Mock()
        process.poll.return_value = None
        ticks = iter([0, 0.1, 6])
        with mock.patch.object(proof, "stop_unit") as stop, mock.patch.object(proof.time, "monotonic", side_effect=lambda: next(ticks)), \
                mock.patch.object(proof.time, "sleep") as sleep, mock.patch.object(proof, "cgroup_pids", return_value=set()):
            self.assertEqual((True, True), proof.stop_and_join("selected", process, "/selected", []))
            self.assertEqual([mock.call("selected"), mock.call("selected", force=True)], stop.call_args_list)
            sleep.assert_called_once()

    def test_unjoined_pump_never_confirms_owned_exit(self):
        process = mock.Mock()
        process.poll.return_value = 0
        pump = mock.Mock()
        pump.is_alive.return_value = True
        with mock.patch.object(proof, "stop_unit"), mock.patch.object(proof, "cgroup_pids", return_value=set()):
            self.assertEqual((False, False), proof.stop_and_join("selected", process, "/selected", [pump]))

    def test_teardown_output_burst_latches_final_quota_failure(self):
        budget = proof.OutputBudget(5)
        budget.consume(5)
        receipt = {}
        pump = mock.Mock()
        pump.join.side_effect = lambda **_: budget.consume(1)
        pump.is_alive.return_value = False
        process = mock.Mock()
        process.poll.return_value = 0
        with mock.patch.object(proof, "stop_unit"), mock.patch.object(proof, "cgroup_pids", return_value=set()):
            proof.stop_and_join("selected", process, "/selected", [pump])
        proof.final_output_receipt(receipt, budget)
        self.assertEqual("output-quota-exceeded", receipt["failure"])
        self.assertEqual(6, receipt["output_bytes"])

    def test_cgroup_identity_rejects_unselected_unit(self):
        with self.assertRaises(ValueError):
            proof.cgroup_pids("/system.slice/other.service")

    def test_stalled_controller_watchdog_quarantines_before_stop(self):
        with tempfile.TemporaryDirectory() as name:
            control = Path(name)
            done = mock.Mock()
            done.wait.return_value = False
            def stop(unit, force=False):
                self.assertTrue((control / "quarantine").is_file())
                self.assertEqual("issue779-child-test.service", unit)
                self.assertTrue(force)
            with mock.patch.object(proof.os, "setsid"), mock.patch.object(proof, "stop_unit", side_effect=stop):
                proof.watchdog("issue779-child-test.service", 0, done, control, mock.Mock())


class PortableResourceHttpProbes(unittest.TestCase):
    """Exercise the existing resource DLL with fake inputs and ordinary file modes.

    These cases run no Aspire host or native broker and establish no native
    protection claim. Root CI drops the resource to UID/GID 65534 so denied
    fixture directories remain inaccessible to the process under test.
    """

    def test_denied_tools_and_output_with_declared_input_returns_exact_body(self):
        status, body, _ = self._probe()
        self.assertEqual(200, status)
        self.assertEqual(b"native-http-ready", body)

    def test_accessible_managed_tool_returns_unavailable(self):
        status, _, _ = self._probe(tools_accessible=True)
        self.assertEqual(503, status)

    def test_accessible_output_creates_probe_and_returns_unavailable(self):
        status, _, output_created = self._probe(output_accessible=True)
        self.assertEqual(503, status)
        self.assertTrue(output_created)

    def test_mismatched_declared_input_returns_unavailable(self):
        status, _, _ = self._probe(input_text="incorrect-declared-input\n")
        self.assertEqual(503, status)

    def _probe(self, *, tools_accessible=False, output_accessible=False,
               input_text="declared-native-input\n"):
        self.assertTrue(os.name == "posix" and hasattr(socket, "AF_UNIX"), "Requires POSIX Unix sockets")
        dotnet = shutil.which("dotnet")
        self.assertIsNotNone(dotnet, "The portable resource probes require an installed dotnet host")
        resource = Path(__file__).parent / "NativeHttpResource/bin/Debug/net10.0/NativeHttpResource.dll"
        self.assertTrue(resource.is_file(), "Build the resource fixture before running these existing-DLL probes")
        # Keep the Unix socket path below macOS's path-length limit.
        with tempfile.TemporaryDirectory(prefix="proof-http-", dir="/tmp") as name:
            root = Path(name)
            root.chmod(0o755)
            stage = root / "resource"
            shutil.copytree(resource.parent, stage)
            for entry in stage.rglob("*"):
                entry.chmod(0o555)
            stage.chmod(0o555)
            tools = root / "tools"
            output = root / "output"
            scratch = root / "scratch"
            for directory in (tools, output, scratch):
                directory.mkdir(mode=0o700)
            shutil.copyfile(resource, tools / "protected-tool.dll")
            (tools / "protected-tool.dll").chmod(0o444)
            allowed_input = root / "declared-input.txt"
            allowed_input.write_text(input_text, encoding="utf-8")
            allowed_input.chmod(0o444)
            drop_identity = None
            if os.geteuid() == 0:
                os.chown(scratch, 65534, 65534)
                if output_accessible:
                    os.chown(output, 65534, 65534)

                def drop_identity():
                    os.setgroups([])
                    os.setgid(65534)
                    os.setuid(65534)

                denied_mode = 0o700
            else:
                denied_mode = 0o000
            tools.chmod(0o755 if tools_accessible else denied_mode)
            output.chmod(0o700 if output_accessible else denied_mode)
            socket_path = scratch / "resource.sock"
            environment = os.environ.copy()
            environment.update({
                "PROOF_PROTECTED_TOOLS": str(tools),
                "PROOF_PROTECTED_OUTPUT": str(output),
                "PROOF_ALLOWED_INPUT": str(allowed_input),
                "DOTNET_CLI_HOME": str(scratch),
                "DOTNET_NOLOGO": "1",
                "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
                "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            })
            process = None
            try:
                with (root / "resource.log").open("xb") as log:
                    os.fchmod(log.fileno(), 0o600)
                    process = subprocess.Popen(
                        [dotnet, str(stage / resource.name), "--socket", str(socket_path), "--case", "normal"],
                        cwd=scratch, env=environment, stdin=subprocess.DEVNULL,
                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, preexec_fn=drop_identity,
                    )
                    log_state = {"written": 0, "exceeded": False, "failed": False}
                    pump = threading.Thread(target=self._pump_private_log,
                                            args=(process.stdout, log, log_state), daemon=True)
                    pump.start()
                    try:
                        status, body = self._wait_for_http(process, socket_path)
                        output_created = (output / "native-resource-output-probe").is_file() if output_accessible else False
                    finally:
                        try:
                            self._stop_resource(process)
                        finally:
                            pump.join(timeout=0.5)
                            self.assertFalse(pump.is_alive(), "The resource log pump did not join")
                            process.stdout.close()
                    self.assertFalse(log_state["failed"], "The private resource log pump failed")
                    self.assertFalse(log_state["exceeded"], "Resource output exceeded the 8192-byte private log bound")
                    return status, body, output_created
            finally:
                try:
                    if process is not None:
                        self._stop_resource(process)
                finally:
                    # Restore only owned fixture directories for temporary cleanup.
                    tools.chmod(0o700)
                    output.chmod(0o700)
                    stage.chmod(0o700)
                    for entry in stage.rglob("*"):
                        if entry.is_dir():
                            entry.chmod(0o700)

    def _wait_for_http(self, process, socket_path):
        deadline = time.monotonic() + 5
        last_error = ("none", None)
        while time.monotonic() < deadline:
            self.assertIsNone(process.poll(), "The resource exited before its HTTP probe responded")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                break
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
                    connection.settimeout(min(0.25, remaining))
                    connection.connect(str(socket_path))
                    connection.sendall(b"GET /health HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n")
                    with http.client.HTTPResponse(connection) as response:
                        response.begin()
                        return response.status, response.read(8192)
            except (OSError, http.client.HTTPException) as error:
                last_error = (type(error).__name__, getattr(error, "errno", None))
                time.sleep(min(0.025, max(0, deadline - time.monotonic())))
        self.fail(f"The resource HTTP probe did not respond within 5 seconds; error_class={last_error[0]}, errno={last_error[1]}")

    @staticmethod
    def _pump_private_log(stream, log, state):
        try:
            while chunk := stream.read(1024):
                remaining = 8192 - state["written"]
                kept = chunk[:remaining]
                log.write(kept)
                state["written"] += len(kept)
                if len(chunk) > remaining:
                    state["exceeded"] = True
        except (OSError, ValueError):
            state["failed"] = True

    @staticmethod
    def _stop_resource(process):
        if process.poll() is None:
            try:
                process.terminate()
            except ProcessLookupError:
                pass
        try:
            process.wait(timeout=2)
        except subprocess.TimeoutExpired:
            try:
                process.kill()
            except ProcessLookupError:
                pass
            process.wait(timeout=2)


class WatchdogAndPumpControls(unittest.TestCase):
    """Portable process/pipe and output controls; no service or native proof runs."""

    @staticmethod
    def _monitor(pid=12345):
        monitor = mock.Mock()
        monitor.pid = pid
        monitor.exitcode = None
        monitor.is_alive.return_value = True
        return monitor

    @staticmethod
    def _reader(payload, available=True):
        reader = mock.Mock()
        reader.poll.return_value = available
        reader.recv_bytes.return_value = payload
        return reader

    @staticmethod
    def _finish_process(process):
        process.join(timeout=0.5)
        if process.is_alive():
            process.terminate()
            process.join(timeout=0.5)
        if process.is_alive():
            process.kill()
            process.join(timeout=0.5)
        if not process.is_alive():
            process.close()

    def test_real_fork_watchdog_ack_and_clean_root_disarm(self):
        import multiprocessing
        self.assertTrue(os.name == "posix", "Requires portable POSIX fork")
        context = multiprocessing.get_context("fork")
        with tempfile.TemporaryDirectory(prefix="watchdog-control-") as name:
            control = Path(name)
            control.chmod(0o700)
            reader, writer = context.Pipe(duplex=False)
            done = context.Event()
            monitor = context.Process(target=proof.watchdog, args=(
                "issue779-watchdog-portable-test.service", time.monotonic() + 30,
                done, control, writer,
            ))
            # The fork inherits this guard; even an unexpected deadline cannot
            # invoke a real service command from this portable control.
            with mock.patch.object(proof, "stop_unit", side_effect=AssertionError("No service execution")):
                monitor.start()
                writer.close()
                try:
                    proof.await_watchdog_ack(monitor, reader, timeout=1)
                    proof.require_watchdog_alive(monitor)
                    self.assertTrue(monitor.is_alive())
                    self.assertFalse(done.is_set())
                    proof.disarm_watchdog(monitor, done, reader)
                    self.assertEqual(0, monitor.exitcode)
                    self.assertFalse(monitor.is_alive())
                    self.assertFalse((control / "quarantine").exists())
                finally:
                    done.set()
                    self._finish_process(monitor)
                    reader.close()

    def test_real_premature_zero_and_nonzero_exits_are_rejected(self):
        import multiprocessing
        import struct
        self.assertTrue(os.name == "posix", "Requires portable POSIX fork")
        context = multiprocessing.get_context("fork")
        for code in (0, 1):
            with self.subTest(exit_code=code):
                monitor = context.Process(target=os._exit, args=(code,))
                monitor.start()
                try:
                    monitor.join(timeout=1)
                    self.assertFalse(monitor.is_alive())
                    self.assertEqual(code, monitor.exitcode)
                    reader = self._reader(struct.pack("!I", monitor.pid))
                    with self.assertRaises(proof.WatchdogFailure):
                        proof.require_watchdog_alive(monitor)
                    with self.assertRaises(proof.WatchdogFailure):
                        proof.await_watchdog_ack(monitor, reader, timeout=0.005)
                    done = mock.Mock()
                    with self.assertRaises(proof.WatchdogFailure):
                        proof.disarm_watchdog(monitor, done, reader)
                    done.set.assert_not_called()
                finally:
                    self._finish_process(monitor)

    def test_missing_startup_ack_is_bounded_and_rejected(self):
        reader = self._reader(b"", available=False)
        with self.assertRaises(proof.WatchdogFailure):
            proof.await_watchdog_ack(self._monitor(), reader, timeout=0.005)
        reader.recv_bytes.assert_not_called()

    def test_wrong_startup_pid_is_rejected(self):
        import struct
        with self.assertRaises(proof.WatchdogFailure):
            proof.await_watchdog_ack(self._monitor(), self._reader(struct.pack("!I", 54321)), timeout=0.005)

    def test_malformed_startup_ack_is_rejected(self):
        with self.assertRaises(proof.WatchdogFailure):
            proof.await_watchdog_ack(self._monitor(), self._reader(b"bad"), timeout=0.005)

    def test_startup_ack_read_has_a_64_byte_bound(self):
        import struct
        reader = self._reader(struct.pack("!I", 12345))
        proof.await_watchdog_ack(self._monitor(), reader, timeout=0.005)
        reader.recv_bytes.assert_called_once()
        args, kwargs = reader.recv_bytes.call_args
        self.assertEqual(64, args[0] if args else kwargs["maxlength"])

    def test_disarm_keeps_monitor_live_until_root_sets_done(self):
        monitor = self._monitor()
        done = mock.Mock()
        reader = self._reader(b"disarmed")

        def set_done():
            self.assertIsNone(monitor.exitcode)
            self.assertTrue(monitor.is_alive())
            monitor.join.assert_not_called()

        def joined(*args, **kwargs):
            monitor.exitcode = 0
            monitor.is_alive.return_value = False

        done.set.side_effect = set_done
        monitor.join.side_effect = joined
        proof.disarm_watchdog(monitor, done, reader)
        done.set.assert_called_once()
        monitor.join.assert_called_once()

    def test_disarm_rejects_nonzero_final_exit(self):
        monitor = self._monitor()

        def joined(*args, **kwargs):
            monitor.exitcode = 1
            monitor.is_alive.return_value = False

        monitor.join.side_effect = joined
        with self.assertRaises(proof.WatchdogFailure):
            proof.disarm_watchdog(monitor, mock.Mock(), self._reader(b"disarmed"))

    def test_disarm_rejects_missing_or_wrong_ack(self):
        for payload, available in ((b"", False), (b"wrong", True)):
            with self.subTest(available=available):
                with self.assertRaises(proof.WatchdogFailure):
                    proof.disarm_watchdog(self._monitor(), mock.Mock(), self._reader(payload, available))

    def test_watchdog_ack_failure_quarantines_with_fixed_category_and_exit_one(self):
        with tempfile.TemporaryDirectory(prefix="watchdog-control-") as name:
            control = Path(name)
            ack = mock.Mock()
            ack.send_bytes.side_effect = OSError("private-ack-message")
            with mock.patch.object(proof.os, "setsid"), mock.patch.object(proof, "stop_unit") as stop:
                with self.assertRaises(SystemExit) as raised:
                    proof.watchdog("issue779-watchdog-portable-test.service", time.monotonic() + 30,
                                   mock.Mock(), control, ack)
            self.assertEqual(1, raised.exception.code)
            self.assertEqual("watchdog-failed\n", (control / "quarantine").read_text())
            ack.close.assert_called_once()
            stop.assert_not_called()

    def test_actual_bytesio_pumps_drain_to_eof_and_match_combined_count(self):
        import io
        budget = proof.OutputBudget(100)
        states = [proof.PumpState("stdout"), proof.PumpState("stderr")]
        with tempfile.TemporaryDirectory(prefix="pump-control-") as name:
            for state, payload in zip(states, (b"out", b"error")):
                destination = Path(name) / state.name
                proof.pump(io.BytesIO(payload), destination, budget, state)
                self.assertEqual(payload, destination.read_bytes())
                snapshot = state.snapshot()
                self.assertEqual(len(payload), snapshot["bytes"])
                self.assertTrue(snapshot["eof"])
                self.assertTrue(snapshot["finished"])
                self.assertFalse(snapshot["failed"])
                self.assertIsNone(snapshot["error_class"])
        receipt = {}
        proof.final_output_receipt(receipt, budget, states)
        self.assertEqual(8, budget.count)
        self.assertEqual(8, receipt["output_bytes"])
        self.assertTrue(receipt["output_complete"])
        self.assertNotIn("failure", receipt)

    def test_read_error_after_bytes_records_only_fixed_error_class(self):
        import io
        import json

        class ReadError:
            def __init__(self):
                self.first = True

            def read(self, size):
                if self.first:
                    self.first = False
                    return b"partial"
                raise OSError("private-read-message-must-not-enter-json")

            def close(self):
                pass

        budget = proof.OutputBudget(100)
        states = [proof.PumpState("stdout"), proof.PumpState("stderr")]
        with tempfile.TemporaryDirectory(prefix="pump-control-") as name:
            destination = Path(name) / "stdout"
            proof.pump(ReadError(), destination, budget, states[0])
            proof.pump(io.BytesIO(b""), Path(name) / "stderr", budget, states[1])
            self.assertEqual(b"partial", destination.read_bytes())
        snapshot = states[0].snapshot()
        self.assertEqual(7, snapshot["bytes"])
        self.assertTrue(snapshot["failed"])
        self.assertTrue(snapshot["finished"])
        self.assertFalse(snapshot["eof"])
        self.assertEqual("OSError", snapshot["error_class"])
        receipt = {}
        proof.final_output_receipt(receipt, budget, states)
        self.assertFalse(receipt["output_complete"])
        self.assertEqual("output-pump-incomplete", receipt["failure"])
        self.assertNotIn("private-read-message", json.dumps(receipt))
        self.assertNotIn("private-read-message", json.dumps(snapshot))

    def test_output_write_failure_is_not_complete_capture(self):
        import io
        import json
        budget = proof.OutputBudget(100)
        state = proof.PumpState("stdout")
        destination = mock.MagicMock()
        destination.open.return_value.__enter__.return_value.write.side_effect = OSError("private-write-message")
        proof.pump(io.BytesIO(b"out"), destination, budget, state)
        snapshot = state.snapshot()
        self.assertEqual(3, snapshot["bytes"])
        self.assertEqual(3, budget.count)
        self.assertTrue(snapshot["failed"])
        self.assertTrue(snapshot["finished"])
        self.assertFalse(snapshot["eof"])
        self.assertEqual("OSError", snapshot["error_class"])
        with tempfile.TemporaryDirectory(prefix="pump-control-") as name:
            other = proof.PumpState("stderr")
            proof.pump(io.BytesIO(b""), Path(name) / "stderr", budget, other)
        receipt = {}
        proof.final_output_receipt(receipt, budget, [state, other])
        self.assertFalse(receipt["output_complete"])
        self.assertEqual("output-pump-incomplete", receipt["failure"])
        self.assertNotIn("private-write-message", json.dumps(receipt))
        self.assertNotIn("private-write-message", json.dumps(snapshot))

    def test_finished_state_without_eof_is_rejected(self):
        state = mock.Mock()
        state.name = "stdout"
        state.snapshot.return_value = {
            "name": "stdout", "bytes": 0, "eof": False, "failed": False,
            "finished": True, "error_class": None,
        }
        other = mock.Mock()
        other.name = "stderr"
        other.snapshot.return_value = dict(state.snapshot.return_value, name="stderr", eof=True)
        receipt = {}
        proof.final_output_receipt(receipt, proof.OutputBudget(100), [state, other])
        self.assertFalse(receipt["output_complete"])
        self.assertEqual("output-pump-incomplete", receipt["failure"])

    def test_capture_requires_two_streams_and_matching_budget(self):
        import io
        for missing_stream in (False, True):
            with self.subTest(missing_stream=missing_stream), tempfile.TemporaryDirectory(prefix="pump-control-") as name:
                budget = proof.OutputBudget(100)
                states = [proof.PumpState("stdout"), proof.PumpState("stderr")]
                for state in states:
                    proof.pump(io.BytesIO(b"x"), Path(name) / state.name, budget, state)
                if missing_stream:
                    states = states[:1]
                else:
                    budget.consume(1)
                receipt = {}
                proof.final_output_receipt(receipt, budget, states)
                self.assertFalse(receipt["output_complete"])
                self.assertEqual("output-pump-incomplete", receipt["failure"])

    def test_quota_failure_stays_latched_after_actual_pumps_finish(self):
        import io
        budget = proof.OutputBudget(3)
        states = [proof.PumpState("stdout"), proof.PumpState("stderr")]
        with tempfile.TemporaryDirectory(prefix="pump-control-") as name:
            proof.pump(io.BytesIO(b"four"), Path(name) / "stdout", budget, states[0])
            proof.pump(io.BytesIO(b""), Path(name) / "stderr", budget, states[1])
        receipt = {}
        proof.final_output_receipt(receipt, budget, states)
        self.assertTrue(all(state.snapshot()["finished"] for state in states))
        self.assertEqual(4, receipt["output_bytes"])
        self.assertTrue(receipt["output_quota_exceeded"])
        self.assertEqual("output-quota-exceeded", receipt["failure"])


if __name__ == "__main__":
    unittest.main()
