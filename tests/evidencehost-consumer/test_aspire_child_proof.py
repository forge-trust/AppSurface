"""Portable controls and resource HTTP probes, never native acceptance."""
import http.client
import importlib.util
import json
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

SELECTED_UNIT = "issue779-child-" + "a" * 32 + ".service"
SELECTED_GROUP = "/system.slice/" + SELECTED_UNIT


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
            self.assertEqual((True, False), proof.stop_and_join(SELECTED_UNIT, process, SELECTED_GROUP, [pump]))
            stop.assert_called_once_with(SELECTED_UNIT)
            process.wait.assert_called_once()
            pump.join.assert_called_once()

    def test_stalled_stop_escalates_only_after_grace(self):
        process = mock.Mock()
        process.poll.return_value = None
        ticks = iter([0, 0.1, 6, 6, 6, 6])
        with mock.patch.object(proof, "stop_unit") as stop, mock.patch.object(proof.time, "monotonic", side_effect=lambda: next(ticks)), \
                mock.patch.object(proof.time, "sleep") as sleep, mock.patch.object(proof, "cgroup_pids", return_value=set()):
            self.assertEqual((True, True), proof.stop_and_join(SELECTED_UNIT, process, SELECTED_GROUP, []))
            self.assertEqual([mock.call(SELECTED_UNIT), mock.call(SELECTED_UNIT, force=True)], stop.call_args_list)
            sleep.assert_called_once()

    def test_unjoined_pump_never_confirms_owned_exit(self):
        process = mock.Mock()
        process.poll.return_value = 0
        pump = mock.Mock()
        pump.is_alive.return_value = True
        with mock.patch.object(proof, "stop_unit"), mock.patch.object(proof, "cgroup_pids", return_value=set()):
            self.assertEqual((False, False), proof.stop_and_join(SELECTED_UNIT, process, SELECTED_GROUP, [pump]))

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
            proof.stop_and_join(SELECTED_UNIT, process, SELECTED_GROUP, [pump])
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


class StartupQueryAndStopDiscoveryControls(unittest.TestCase):
    """Closed query evidence and physical lifetime controls, no native service execution."""

    @staticmethod
    def properties(**extra):
        return {"_query_exit_status": 0, "LoadState": "loaded", "Type": "exec", "ActiveState": "active",
                "SubState": "running", "MainPID": "123", "ControlGroup": SELECTED_GROUP, **extra}

    @classmethod
    def missing(cls):
        return cls.properties(LoadState="not-found", Type="", ActiveState="inactive", SubState="dead",
                              MainPID="0", ControlGroup="")

    @staticmethod
    def query_result(properties, status=0, stderr=b""):
        raw = "".join(f"{key}={value}\n" for key, value in properties.items() if not key.startswith("_"))
        return subprocess.CompletedProcess([], status, raw.encode("ascii"), stderr)

    def test_successful_not_found_then_loaded_start_then_running_keeps_identity_guard_closed(self):
        samples = [self.missing(), self.properties(ActiveState="activating", SubState="start", MainPID="0"),
                   self.properties()]
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", side_effect=[
                self.query_result(value) for value in samples]) as query, mock.patch.object(proof, "identity_matches") as identity:
            receipt = {}
            seen = False
            for expected, sample in zip((False, False, True), samples):
                properties, complete = proof.query_exec_startup(SELECTED_UNIT, time.monotonic() + 1, seen,
                                                               receipt, Path(name), time.monotonic())
                self.assertEqual(expected, complete)
                seen = seen or properties["LoadState"] == "loaded"
            self.assertEqual(3, query.call_count)
            self.assertTrue(all(SELECTED_UNIT in call.args[0] for call in query.call_args_list))
            self.assertNotIn("failure", receipt)
            identity.assert_not_called()

    def test_not_found_after_loaded_exec_is_terminal_with_private_closed_facts(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", return_value=self.query_result(self.missing())):
            receipt = {}
            with self.assertRaises(proof.StartupFailure):
                proof.query_exec_startup(SELECTED_UNIT, time.monotonic() + 1, True, receipt, Path(name), time.monotonic())
            self.assertTrue(receipt["startup_diagnostic_written"])
            self.assertEqual("not-found", json.loads((Path(name) / "startup-diagnostic.json").read_text())["load_state"])

    def test_nonzero_query_captures_status_and_original_cause_survives_watchdog_and_output_failure(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", return_value=self.query_result(
                self.properties(), 5, b"private-query-canary")):
            receipt = {}
            with self.assertRaises(proof.StartupFailure):
                proof.query_exec_startup(SELECTED_UNIT, time.monotonic() + 1, False, receipt, Path(name), time.monotonic())
            path = Path(name) / "startup-diagnostic.json"
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertLessEqual(path.stat().st_size, 4096)
            self.assertEqual(5, json.loads(path.read_text())["query_exit_status"])
            self.assertNotIn("private-query-canary", path.read_text() + json.dumps(receipt))
            receipt.update(failure="watchdog-unavailable", watchdog_clean_exit=False, owned_exit=False)
            proof.final_output_receipt(receipt, proof.OutputBudget(), [])
            proof.preserve_identity_rejection(receipt)
            self.assertEqual(proof.STARTUP_FAILURE, receipt["failure"])
            self.assertEqual(proof.STARTUP_STAGE, receipt["failure_stage"])
            self.assertFalse(receipt["owned_exit"])
            self.assertFalse(receipt["output_complete"])
            self.assertFalse(receipt["watchdog_clean_exit"])

    def test_timeout_query_records_unknown_status_without_raw_exception(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", side_effect=
                subprocess.TimeoutExpired("private-query-canary", 1, stderr=b"private-query-canary")):
            receipt = {}
            with self.assertRaises(proof.StartupFailure):
                proof.query_exec_startup(SELECTED_UNIT, time.monotonic() + 1, False, receipt, Path(name), time.monotonic())
            data = json.loads((Path(name) / "startup-diagnostic.json").read_text())
            self.assertEqual({"load_state", "type", "active_state", "sub_state", "main_pid", "query_exit_status", "elapsed_seconds"}, set(data))
            self.assertIsNone(data["query_exit_status"])
            self.assertEqual(0, data["main_pid"])
            self.assertNotIn("private-query-canary", json.dumps(data) + json.dumps(receipt))

    def test_terminal_malformed_and_foreign_properties_are_not_pending(self):
        cases = [self.properties(LoadState="masked"), self.properties(ActiveState="inactive", SubState="dead"),
                 self.properties(Type="private-query-canary"), self.properties(SubState="private-query-canary"),
                 self.properties(MainPID="4294967296"), self.properties(ControlGroup="/system.slice/other.service"),
                 self.missing() | {"_query_exit_status": 4}, self.missing() | {"MainPID": "123"}]
        for sample in cases:
            with self.subTest(sample=sample), tempfile.TemporaryDirectory() as name, mock.patch.object(
                    proof, "command", return_value=self.query_result(sample, sample["_query_exit_status"])):
                receipt = {}
                with self.assertRaises(proof.StartupFailure):
                    proof.query_exec_startup(SELECTED_UNIT, time.monotonic() + 1, False, receipt, Path(name), time.monotonic())
                self.assertTrue(receipt["startup_diagnostic_written"])
                self.assertNotIn("private-query-canary", (Path(name) / "startup-diagnostic.json").read_text())

    def test_expired_missing_query_is_failure_with_last_observation(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", return_value=self.query_result(self.missing())):
            receipt = {}
            with self.assertRaises(proof.StartupFailure):
                proof.query_exec_startup(SELECTED_UNIT, time.monotonic() - 1, False, receipt, Path(name), time.monotonic())
            self.assertEqual("not-found", json.loads((Path(name) / "startup-diagnostic.json").read_text())["load_state"])

    def test_deadline_between_query_and_observation_captures_first_cause_before_teardown(self):
        with tempfile.TemporaryDirectory() as name, mock.patch.object(proof, "command", return_value=self.query_result(self.properties())):
            receipt = {}
            with mock.patch.object(proof.time, "monotonic", return_value=11):
                properties, complete = proof.query_exec_startup(SELECTED_UNIT, 12, False, receipt, Path(name), 0)
            self.assertTrue(complete)
            with mock.patch.object(proof.time, "monotonic", return_value=12), mock.patch.object(proof, "identity_matches") as identity:
                try:
                    proof.observe_unit_processes(properties, 999, SELECTED_GROUP, receipt, Path(name), 0, 12)
                except proof.StartupFailure as error:
                    proof.record_control_failure(receipt, error, Path(name), properties, 0)
                else:
                    self.fail("Expired observation cannot accept a running unit")
                identity.assert_not_called()
            self.assertTrue(receipt["startup_diagnostic_written"])
            self.assertEqual("StartupFailure", receipt["error_class"])
            self.assertEqual(12, json.loads((Path(name) / "startup-diagnostic.json").read_text())["elapsed_seconds"])
            receipt.update(failure="watchdog-unavailable", owned_exit=False, watchdog_clean_exit=False)
            proof.preserve_identity_rejection(receipt)
            self.assertEqual(proof.STARTUP_FAILURE, receipt["failure"])
            self.assertFalse(receipt["owned_exit"])

    def test_duplicate_and_oversized_query_cannot_prove_startup(self):
        for raw in (b"Type=exec\nType=exec\n", b"x" * 4097):
            with self.subTest(size=len(raw)), mock.patch.object(proof, "command", return_value=subprocess.CompletedProcess([], 0, raw, b"")):
                properties = proof.unit_properties(SELECTED_UNIT)
                with self.assertRaises(proof.StartupFailure):
                    proof.exec_startup_complete(properties, time.monotonic() + 1)

    def test_query_never_selects_foreign_or_malformed_unit(self):
        for unit in ("other.service", "issue779-child-../foreign.service", "issue779-child-" + "g" * 32 + ".service"):
            with self.subTest(unit=unit), mock.patch.object(proof, "command") as query:
                with self.assertRaises(ValueError):
                    proof.unit_properties(unit)
                query.assert_not_called()

    def test_failed_existing_or_oversized_capture_preserves_original_failure(self):
        for failure in ("existing", "write", "size"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as name:
                path = Path(name) / "startup-diagnostic.json"
                if failure == "existing":
                    path.write_bytes(b"existing-private-data")
                receipt = {}
                original_open = Path.open
                def opened(candidate, *args, **kwargs):
                    if failure == "write" and candidate == path:
                        raise OSError("private-query-canary")
                    return original_open(candidate, *args, **kwargs)
                with mock.patch.object(Path, "open", opened), mock.patch.object(proof, "STARTUP_DIAGNOSTIC_LIMIT", 1 if failure == "size" else 4096):
                    proof.record_startup_failure(receipt, Path(name), self.properties(), time.monotonic())
                self.assertFalse(receipt["startup_diagnostic_written"])
                receipt["failure"] = "watchdog-unavailable"
                proof.preserve_identity_rejection(receipt)
                self.assertEqual(proof.STARTUP_FAILURE, receipt["failure"])
                if failure == "existing":
                    self.assertEqual(b"existing-private-data", path.read_bytes())
                else:
                    self.assertFalse(path.exists())
                self.assertNotIn("private-query-canary", json.dumps(receipt))

    def test_invalid_query_status_and_pid_have_closed_private_defaults(self):
        with tempfile.TemporaryDirectory() as name:
            receipt = {}
            proof.record_startup_failure(receipt, Path(name), self.properties(
                _query_exit_status=999999, MainPID="private-query-canary", LoadState="private-query-canary",
                Type="private-query-canary", ActiveState="private-query-canary", SubState="private-query-canary"), time.monotonic())
            data = json.loads((Path(name) / "startup-diagnostic.json").read_text())
            self.assertEqual(0, data["main_pid"])
            self.assertIsNone(data["query_exit_status"])
            self.assertEqual("unknown", data["load_state"])
            self.assertNotIn("private-query-canary", json.dumps(data))

    def test_real_fork_stop_discovers_late_exact_group_and_watchdog_disarms_cleanly(self):
        import multiprocessing
        context = multiprocessing.get_context("fork")
        child_done = context.Event()
        child = context.Process(target=child_done.wait, args=(3,))
        class JoinedChild:
            def poll(self):
                return child.exitcode
            def wait(self, timeout):
                child.join(timeout)
                if child.is_alive():
                    raise subprocess.TimeoutExpired("portable-child", timeout)
                return child.exitcode
        with tempfile.TemporaryDirectory() as name:
            reader, writer = context.Pipe(duplex=False)
            watchdog_done = context.Event()
            monitor = context.Process(target=proof.watchdog, args=(SELECTED_UNIT, time.monotonic() + 10, watchdog_done, Path(name), writer))
            queries = []
            def command(argv, **kwargs):
                if argv[1] == "show":
                    queries.append(argv)
                    return self.query_result(self.missing() if len(queries) == 1 else self.properties(
                        ActiveState="inactive", SubState="dead", MainPID="0"))
                self.assertEqual(["systemctl", "kill", "--kill-whom=main", "--signal=TERM", SELECTED_UNIT], argv)
                child_done.set()
                return subprocess.CompletedProcess(argv, 0, b"", b"")
            with mock.patch.object(proof, "command", side_effect=command), mock.patch.object(proof, "cgroup_pids", return_value=set()) as pids:
                child.start()
                monitor.start()
                writer.close()
                try:
                    proof.await_watchdog_ack(monitor, reader, timeout=1)
                    self.assertEqual((True, False), proof.stop_and_join(SELECTED_UNIT, JoinedChild(), "", [], guard=lambda: proof.require_watchdog_alive(monitor)))
                    self.assertGreaterEqual(len(queries), 2)
                    self.assertTrue(all(call.args[0] == SELECTED_GROUP for call in pids.call_args_list))
                    self.assertEqual(0, child.exitcode)
                    proof.disarm_watchdog(monitor, watchdog_done, reader)
                    self.assertEqual(0, monitor.exitcode)
                finally:
                    child_done.set()
                    watchdog_done.set()
                    WatchdogAndPumpControls._finish_process(child)
                    WatchdogAndPumpControls._finish_process(monitor)
                    reader.close()

    def test_unknown_or_foreign_group_never_confirms_exit_and_force_targets_selected_unit(self):
        foreign = "/system.slice/issue779-child-" + "b" * 32 + ".service"
        for initial, observed in (("", ""), (foreign, SELECTED_GROUP), ("", foreign)):
            with self.subTest(initial=initial, observed=observed):
                process = mock.Mock()
                process.poll.return_value = 0
                with mock.patch.object(proof, "COOPERATIVE_SECONDS", 0.001), mock.patch.object(proof, "CLEANUP_SECONDS", 0.002), \
                        mock.patch.object(proof, "stop_unit") as stop, mock.patch.object(proof, "unit_properties", return_value=self.properties(ControlGroup=observed)), \
                        mock.patch.object(proof, "cgroup_pids", return_value=set()) as pids:
                    owned, escalated = proof.stop_and_join(SELECTED_UNIT, process, initial, [])
                self.assertFalse(owned)
                self.assertTrue(escalated)
                self.assertEqual([mock.call(SELECTED_UNIT), mock.call(SELECTED_UNIT, force=True)], stop.call_args_list)
                self.assertTrue(all(call.args[0] == SELECTED_GROUP for call in pids.call_args_list))

    def test_discovery_cannot_clear_root_uid_rejection_or_prior_watchdog_loss(self):
        with tempfile.TemporaryDirectory() as name:
            receipt = {}
            with mock.patch.object(proof, "cgroup_pids", return_value={123}), mock.patch.object(
                    Path, "read_text", return_value="Uid:\t0\t0\t0\t0\n"), mock.patch.object(proof, "belongs_to_group", return_value=True):
                with self.assertRaises(RuntimeError):
                    proof.observe_unit_processes(self.properties(), 999, SELECTED_GROUP, receipt, Path(name), 0, time.monotonic() + 1)
            process = mock.Mock()
            process.poll.return_value = 0
            with mock.patch.object(proof, "stop_unit"), mock.patch.object(proof, "unit_properties", return_value=self.properties()), \
                    mock.patch.object(proof, "cgroup_pids", return_value=set()):
                self.assertEqual((True, False), proof.stop_and_join(SELECTED_UNIT, process, "", []))
            proof.preserve_identity_rejection(receipt)
            self.assertEqual(proof.IDENTITY_FAILURE, receipt["failure"])
            monitor = WatchdogAndPumpControls._monitor()
            done = mock.Mock()
            with self.assertRaises(proof.WatchdogFailure):
                proof.disarm_watchdog(monitor, done, mock.Mock(), physical_exit=True, ownership_lost=True)
            done.set.assert_not_called()
            with self.assertRaises(proof.WatchdogFailure):
                proof.disarm_watchdog(monitor, done, mock.Mock(), physical_exit=False)
            done.set.assert_not_called()


class ExitedLauncherOrchestrationControls(unittest.TestCase):
    """Execute controller orchestration with fake launcher/query outcomes, no native service."""

    def _run_exited_launcher(self, *, activating_first=False, read_failure=None):
        import io
        from types import SimpleNamespace
        original_path = Path
        with tempfile.TemporaryDirectory(prefix="exited-launcher-") as name:
            root = original_path(name)
            run_root = root / "run"
            run_root.mkdir()
            controllers = root / "cgroup.controllers"
            controllers.touch()
            bundle = root / "bundle"
            bundle.mkdir()
            dotnet = root / "dotnet"
            dotnet.touch()
            tools, output = root / "tools", root / "output"
            tools.mkdir()
            output.mkdir()
            resource = original_path(__file__).parent / "NativeHttpResource/bin/Debug/net10.0/NativeHttpResource.dll"
            shutil.copyfile(resource, tools / "protected-tool.dll")
            args = SimpleNamespace(bundle=bundle, dotnet=dotnet, protected_tools=tools,
                                   protected_output=output, subject_uid=999, subject_gid=999, case="normal")
            class ExitedLauncher:
                def __init__(self):
                    self.stdout, self.stderr = io.BytesIO(), io.BytesIO()
                    self.returncode = 1
                    self.polls = [None, 1] if activating_first else [1]
                def poll(self):
                    return self.polls.pop(0) if self.polls else self.returncode
            process = ExitedLauncher()
            monitor = WatchdogAndPumpControls._monitor()
            events, query_timeouts = [], []
            pending = StartupQueryAndStopDiscoveryControls.properties(ActiveState="activating", SubState="start", MainPID="0")
            terminal = StartupQueryAndStopDiscoveryControls.properties(ActiveState="failed", SubState="failed", MainPID="0")
            samples = iter(([pending] if activating_first else []) + [read_failure if read_failure is not None else terminal])
            def query(unit, timeout=5):
                self.assertEqual(SELECTED_UNIT, unit)
                self.assertGreater(timeout, 0)
                self.assertLessEqual(timeout, 5)
                events.append("query")
                query_timeouts.append(timeout)
                result = next(samples)
                if isinstance(result, Exception):
                    raise result
                return result
            control = run_root / SELECTED_UNIT.removesuffix(".service") / "control"
            original_capture = proof.record_startup_failure
            def capture(receipt, destination, properties, started):
                if receipt.get("failure_stage") != proof.STARTUP_STAGE:
                    events.append("capture")
                original_capture(receipt, destination, properties, started)
            def stop(unit, launcher, group, pumps, guard):
                self.assertEqual(SELECTED_UNIT, unit)
                self.assertIs(process, launcher)
                self.assertTrue((control / "startup-diagnostic.json").is_file())
                events.append("TERM")
                proof.stop_unit(unit)
                for thread in pumps:
                    thread.join(timeout=1)
                    self.assertFalse(thread.is_alive())
                guard()
                return True, False
            def stage(source, destination):
                destination.mkdir()
                return {}
            def selected_path(value, *parts):
                if str(value) == "/run":
                    return run_root
                if str(value) == "/sys/fs/cgroup/cgroup.controllers":
                    return controllers
                return original_path(value, *parts)
            def disarm(*args, **kwargs):
                monitor.exitcode = 0
            original_chmod = original_path.chmod
            def root_fixture_chmod(candidate, mode, *args, **kwargs):
                # The controller is root and can create declared.txt in a 0555 directory.
                # This fake-root portable fixture grants its actual owner that setup write.
                if candidate == control.parent / "payload/proof-input":
                    mode |= 0o200
                return original_chmod(candidate, mode, *args, **kwargs)
            with mock.patch.object(proof.sys, "platform", "linux"), \
                    mock.patch.multiple(proof.os, geteuid=mock.Mock(return_value=0), chown=mock.Mock()), \
                    mock.patch.object(proof.pwd, "getpwuid"), \
                    mock.patch.object(proof, "Path", side_effect=selected_path), \
                    mock.patch.object(original_path, "chmod", root_fixture_chmod), \
                    mock.patch.object(proof.uuid, "uuid4", return_value=SimpleNamespace(hex="a" * 32)), \
                    mock.patch.object(proof, "prepare_payload", side_effect=stage), \
                    mock.patch.object(proof.multiprocessing, "Event"), mock.patch.object(proof.multiprocessing, "Pipe", return_value=(mock.Mock(), mock.Mock())), \
                    mock.patch.object(proof.multiprocessing, "Process", return_value=monitor), \
                    mock.patch.object(proof, "await_watchdog_ack"), mock.patch.object(proof, "disarm_watchdog", side_effect=disarm), \
                    mock.patch.object(proof.subprocess, "Popen", return_value=process), \
                    mock.patch.object(proof, "unit_properties", side_effect=query), \
                    mock.patch.object(proof, "record_startup_failure", side_effect=capture), \
                    mock.patch.object(proof, "record_budget_diagnostic"), mock.patch.object(proof, "stop_and_join", side_effect=stop), \
                    mock.patch.object(proof, "command") as command, mock.patch.object(proof, "identity_matches") as identity, \
                    mock.patch("builtins.print") as printed:
                code = proof.prove(args)
            self.assertEqual(1, code)
            self.assertEqual(["query", "query", "capture", "TERM"] if activating_first else ["query", "capture", "TERM"], events)
            command.assert_called_once_with(["systemctl", "kill", "--kill-whom=main", "--signal=TERM", SELECTED_UNIT], check=False)
            identity.assert_not_called()
            receipt = json.loads(printed.call_args.args[0])
            self.assertEqual(proof.STARTUP_FAILURE, receipt["failure"])
            self.assertEqual("StartupFailure", receipt["error_class"])
            self.assertTrue(receipt["startup_diagnostic_written"])
            self.assertTrue(receipt["output_complete"])
            path = control / "startup-diagnostic.json"
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertLessEqual(path.stat().st_size, 4096)
            facts = json.loads(path.read_text())
            expected = pending if read_failure is not None else terminal
            self.assertEqual(expected["ActiveState"], facts["active_state"])
            self.assertEqual(expected["SubState"], facts["sub_state"])
            self.assertEqual("loaded", facts["load_state"])
            self.assertEqual("exec", facts["type"])
            self.assertEqual(0, facts["main_pid"])
            self.assertEqual(0, facts["query_exit_status"])
            self.assertNotIn("private-query-canary", json.dumps(receipt) + json.dumps(facts))

    def test_launcher_already_exited_before_first_poll_captures_terminal_facts_before_term_and_fails(self):
        self._run_exited_launcher()

    def test_activating_launcher_exits_between_queries_refreshes_terminal_facts_before_term_and_fails(self):
        self._run_exited_launcher(activating_first=True)

    def test_final_read_failure_keeps_old_activating_facts_and_original_startup_failure(self):
        for error in (OSError("private-query-canary"), subprocess.TimeoutExpired("private-query-canary", 1)):
            with self.subTest(error_class=type(error).__name__):
                self._run_exited_launcher(activating_first=True, read_failure=error)

    def test_expired_startup_bound_cannot_start_refresh_or_replace_previous_facts(self):
        previous = StartupQueryAndStopDiscoveryControls.properties(ActiveState="activating", SubState="start")
        with mock.patch.object(proof.time, "monotonic", return_value=12), mock.patch.object(proof, "unit_properties") as query:
            self.assertIs(previous, proof.refresh_unconfirmed_startup(SELECTED_UNIT, 12, previous))
        query.assert_not_called()


class IdentityRejectionDiagnostics(unittest.TestCase):
    """Private failure capture never changes the kernel identity acceptance rule."""

    @staticmethod
    def _record(receipt, control, facts, properties=None):
        with mock.patch.object(proof, "belongs_to_group", return_value=True), \
                mock.patch.object(proof.time, "monotonic", return_value=12.5):
            proof.record_identity_rejection(receipt, control, 123, 999, facts, "/selected",
                                            properties or {"MainPID": "123", "ActiveState": "activating"}, 10)

    def test_uid_rejection_retains_guard_tuple_in_bounded_private_file(self):
        import json
        facts = {}
        with mock.patch.object(Path, "read_text", return_value="Uid:\t0\t0\t0\t0\n"), \
                mock.patch.object(proof, "belongs_to_group") as membership:
            self.assertFalse(proof.identity_matches(123, 999, "/selected", facts))
            membership.assert_not_called()
        self.assertEqual({"uid": [0, 0, 0, 0]}, facts)
        with tempfile.TemporaryDirectory(prefix="identity-control-") as name:
            control = Path(name)
            control.chmod(0o700)
            receipt = {}
            self._record(receipt, control, facts)
            path = control / "identity-rejection.json"
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertEqual(os.geteuid(), path.stat().st_uid)
            self.assertLessEqual(path.stat().st_size, 4096)
            self.assertEqual({"candidate_pid": 123, "target_uid": 999, "uid": [0, 0, 0, 0],
                              "cgroup_matches": True, "main_pid": 123, "active_state": "activating",
                              "elapsed_seconds": 2.5}, json.loads(path.read_text()))
        self.assertEqual("application-identity-rejected", receipt["failure"])
        self.assertEqual("startup-identity-validation", receipt["failure_stage"])
        self.assertTrue(receipt["identity_diagnostic_written"])

    def test_cgroup_rejection_retains_guard_result_without_rereading_membership(self):
        import json
        facts = {}
        with mock.patch.object(Path, "read_text", return_value="Uid:\t999\t999\t999\t999\n"), \
                mock.patch.object(proof, "belongs_to_group", return_value=False) as membership:
            self.assertFalse(proof.identity_matches(123, 999, "/selected", facts))
            with tempfile.TemporaryDirectory(prefix="identity-control-") as name:
                with mock.patch.object(proof.time, "monotonic", return_value=12.5):
                    proof.record_identity_rejection({}, Path(name), 123, 999, facts, "/selected",
                                                    {"MainPID": "123", "ActiveState": "active"}, 10)
                data = json.loads((Path(name) / "identity-rejection.json").read_bytes())
                self.assertFalse(data["cgroup_matches"])
                self.assertEqual([999, 999, 999, 999], data["uid"])
            membership.assert_called_once_with(123, "/selected")

    def test_active_state_is_closed_and_canary_cannot_enter_safe_or_private_json(self):
        import json
        canary = "identity-canary-private-command-path-error"
        with tempfile.TemporaryDirectory(prefix="identity-control-") as name:
            receipt = {}
            self._record(receipt, Path(name), {"uid": [0, 0, 0, 0]},
                         {"MainPID": "123", "ActiveState": canary, "Ignored": canary})
            private = (Path(name) / "identity-rejection.json").read_text()
            self.assertEqual("unknown", json.loads(private)["active_state"])
            self.assertNotIn(canary, private)
            self.assertNotIn(canary, json.dumps(receipt))

    def test_byte_bound_rejects_capture_before_creating_file(self):
        with tempfile.TemporaryDirectory(prefix="identity-control-") as name:
            receipt = {}
            with mock.patch.object(proof, "IDENTITY_DIAGNOSTIC_LIMIT", 1):
                self._record(receipt, Path(name), {"uid": [0, 0, 0, 0]})
            self.assertFalse((Path(name) / "identity-rejection.json").exists())
        self.assertFalse(receipt["identity_diagnostic_written"])
        self.assertEqual("application-identity-rejected", receipt["failure"])

    def test_capture_failure_and_existing_file_preserve_original_rejection(self):
        import json
        for existing in (False, True):
            with self.subTest(existing=existing), tempfile.TemporaryDirectory(prefix="identity-control-") as name:
                control = Path(name)
                path = control / "identity-rejection.json"
                receipt = {"owned_exit": False, "cleanup": False}
                if existing:
                    path.write_text("existing-private-control\n")
                    self._record(receipt, control, {"uid": [0, 0, 0, 0]})
                    self.assertEqual("existing-private-control\n", path.read_text())
                else:
                    with mock.patch.object(Path, "open", side_effect=OSError("identity-canary-write-error")):
                        self._record(receipt, control, {"uid": [0, 0, 0, 0]})
                    self.assertFalse(path.exists())
                self.assertFalse(receipt["identity_diagnostic_written"])
                proof.record_stop_result(receipt, "factory-stall", 0)
                proof.preserve_identity_rejection(receipt)
                self.assertEqual("application-identity-rejected", receipt["failure"])
                self.assertEqual("startup-identity-validation", receipt["failure_stage"])
                self.assertFalse(receipt["owned_exit"])
                self.assertFalse(receipt["cleanup"])
                self.assertNotIn("identity-canary", json.dumps(receipt))

    def test_invalid_numeric_facts_cannot_publish_untrusted_data(self):
        import json
        for uid in ([0, 0, 0], [0, 0, 0, "identity-canary-uid"], [0, 0, 0, True], [0, 0, 0, -1]):
            with self.subTest(uid=uid), tempfile.TemporaryDirectory(prefix="identity-control-") as name:
                receipt = {}
                self._record(receipt, Path(name), {"uid": uid})
                self.assertFalse((Path(name) / "identity-rejection.json").exists())
                self.assertFalse(receipt["identity_diagnostic_written"])
                self.assertEqual("application-identity-rejected", receipt["failure"])
                self.assertNotIn("identity-canary", json.dumps(receipt))


class ExecStartupControls(unittest.TestCase):
    @staticmethod
    def properties(active="active", sub="running", **extra):
        return {"LoadState": "loaded", "_query_exit_status": 0, "Type": "exec", "ActiveState": active, "SubState": sub,
                "MainPID": "123", "ControlGroup": "/selected", **extra}

    def test_command_selects_exec_and_queries_completed_startup_states_without_changing_caps(self):
        argv = proof.service_command("selected", Path("/payload"), Path("/scratch"), Path("/dotnet/dotnet"),
                                     999, 999, Path("/tools"), Path("/output"), Path("/control"), "normal")
        self.assertIn("--property=Type=exec", argv)
        self.assertIn("--property=TasksMax=64", argv)
        self.assertIn("--property=MemoryMax=1G", argv)
        self.assertFalse(any("PROCESSOR_COUNT" in value for value in argv))
        with mock.patch.object(proof, "command", return_value=mock.Mock(returncode=0, stdout=b"Type=exec\nSubState=running\n")) as run:
            self.assertEqual({"_query_exit_status": 0, "Type": "exec", "SubState": "running"}, proof.unit_properties(SELECTED_UNIT))
        self.assertIn("--property=LoadState,ControlGroup,MainPID,Type,ActiveState,SubState", run.call_args.args[0])

    def test_activating_root_candidate_is_not_observed_or_accepted(self):
        with mock.patch.object(proof, "cgroup_pids") as pids, mock.patch.object(proof, "identity_matches") as identity:
            result = proof.observe_unit_processes(self.properties("activating", "start"), 999, "/selected", {}, Path("/control"), 0, time.monotonic() + 1)
        self.assertIsNone(result)
        pids.assert_not_called()
        identity.assert_not_called()

    def test_completed_exec_applies_exact_uid_and_cgroup_guard_before_role_observation(self):
        with mock.patch.object(proof, "cgroup_pids", return_value={123}), \
                mock.patch.object(Path, "read_text", return_value="Uid:\t999\t999\t999\t999\n"), \
                mock.patch.object(proof, "belongs_to_group", return_value=True) as membership, \
                mock.patch.object(Path, "read_bytes", return_value=b"dotnet\0AspireChild.dll\0"):
            result = proof.observe_unit_processes(self.properties(), 999, "/selected", {}, Path("/control"), 0, time.monotonic() + 1)
        self.assertEqual({"123": "apphost"}, result)
        membership.assert_called_once_with(123, "/selected")

    def test_completed_exec_still_rejects_root_uid_and_wrong_cgroup(self):
        for uid, group_matches in ((0, True), (999, False)):
            with self.subTest(uid=uid), tempfile.TemporaryDirectory(prefix="exec-control-") as name:
                receipt = {}
                with mock.patch.object(proof, "cgroup_pids", return_value={123}), \
                        mock.patch.object(Path, "read_text", return_value=f"Uid:\t{uid}\t{uid}\t{uid}\t{uid}\n"), \
                        mock.patch.object(proof, "belongs_to_group", return_value=group_matches), \
                        mock.patch.object(Path, "read_bytes") as cmdline:
                    with self.assertRaises(RuntimeError):
                        proof.observe_unit_processes(self.properties(), 999, "/selected", receipt, Path(name), 0, time.monotonic() + 1)
                    cmdline.assert_not_called()
                self.assertEqual("application-identity-rejected", receipt["failure"])
                self.assertTrue(receipt["identity_diagnostic_written"])

    def test_terminal_or_malformed_startup_never_runs_identity_guard(self):
        cases = [self.properties("failed", "failed"), self.properties("inactive", "dead"),
                 self.properties("deactivating", "stop"), self.properties("active", "exited"),
                 self.properties(Type="simple"), self.properties(MainPID="0"),
                 self.properties(MainPID="private-canary"), self.properties(ControlGroup=""), {}]
        for properties in cases:
            with self.subTest(properties=properties), mock.patch.object(proof, "identity_matches") as identity:
                with self.assertRaisesRegex(proof.StartupFailure, "^application-exec-startup-failed$"):
                    proof.observe_unit_processes(properties, 999, "/selected", {}, Path("/control"), 0, time.monotonic() + 1)
                identity.assert_not_called()

    def test_expired_startup_cannot_accept_even_a_running_exec_unit(self):
        with mock.patch.object(proof.time, "monotonic", return_value=12), \
                mock.patch.object(proof, "identity_matches") as identity:
            with self.assertRaises(proof.StartupFailure):
                proof.observe_unit_processes(self.properties(), 999, "/selected", {}, Path("/control"), 10, 12)
        identity.assert_not_called()

    def test_http_readiness_completed_after_deadline_is_not_accepted(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.getsockopt.return_value = proof.struct.pack("3i", 123, 999, 999)
        connection.recv.side_effect = [b"HTTP/1.1 200 OK\r\n\r\nnative-http-ready", b""]
        with mock.patch.object(proof.socket, "socket", return_value=connection), \
                mock.patch.object(proof.socket, "SO_PEERCRED", 17, create=True), \
                mock.patch.object(proof, "belongs_to_group", return_value=True), \
                mock.patch.object(proof.time, "monotonic", side_effect=[10, 10, 10, 10, 12]):
            with self.assertRaises(TimeoutError):
                proof.ready_request(Path("/scratch/http.sock"), 999, "/selected", 11)

    def test_http_operations_use_remaining_deadline_and_exact_peer_body(self):
        connection = mock.MagicMock()
        connection.__enter__.return_value = connection
        connection.getsockopt.return_value = proof.struct.pack("3i", 123, 999, 999)
        connection.recv.side_effect = [b"HTTP/1.1 200 OK\r\n\r\nnative-http-ready", b""]
        with mock.patch.object(proof.socket, "socket", return_value=connection), \
                mock.patch.object(proof.socket, "SO_PEERCRED", 17, create=True), \
                mock.patch.object(proof, "belongs_to_group", return_value=True), \
                mock.patch.object(proof.time, "monotonic", return_value=10.75):
            self.assertEqual((True, 123, False), proof.ready_request(Path("/scratch/http.sock"), 999, "/selected", 11))
        self.assertTrue(connection.settimeout.call_args_list)
        self.assertTrue(all(call == mock.call(0.25) for call in connection.settimeout.call_args_list))


class BudgetDiagnosticControls(unittest.TestCase):
    GROUP = "/system.slice/issue779-child-" + "a" * 32 + ".service"
    COUNTERS = {"pids.current": "64\n", "pids.max": "64\n", "pids.events": "max 7\n",
                "memory.current": "123456\n", "memory.max": "1073741824\n",
                "memory.events": "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\noom_group_kill 0\nsock_throttled 0\n"}

    def capture(self, directory, receipt, counters=None):
        cgroup = directory / "cgroup"
        cgroup.mkdir()
        for filename, value in (counters or self.COUNTERS).items():
            (cgroup / filename).write_text(value)
        original_open = os.open
        opened = []

        def selected_open(path, flags, **kwargs):
            if flags & os.O_DIRECTORY:
                self.assertEqual(Path("/sys/fs/cgroup") / self.GROUP.lstrip("/"), path)
                fd = original_open(cgroup, flags)
                opened.append(fd)
                return fd
            self.assertIn(path, self.COUNTERS)
            self.assertTrue(flags & os.O_NOFOLLOW)
            return original_open(path, flags, **kwargs)

        with mock.patch.object(proof.os, "open", side_effect=selected_open), \
                mock.patch.object(proof.time, "monotonic", return_value=12.5):
            proof.record_budget_diagnostic(receipt, directory, self.GROUP, 10)
        for fd in opened:
            with self.assertRaises(OSError):
                os.fstat(fd)

    def test_real_numeric_capture_is_private_bounded_and_preserves_original_failure(self):
        receipt = {"failure": "control-or-readiness-failure", "owned_exit": False, "cleanup": False}
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            directory = Path(name)
            self.capture(directory, receipt)
            path = directory / "budget-diagnostic.json"
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertEqual(os.geteuid(), path.stat().st_uid)
            self.assertLessEqual(path.stat().st_size, 4096)
            data = json.loads(path.read_bytes())
            self.assertEqual(64, data["pids_current"])
            self.assertEqual(64, data["pids_max"])
            self.assertEqual(7, data["pids_events_max"])
            self.assertEqual(0, data["memory_events"]["oom_kill"])
            self.assertEqual(2.5, data["elapsed_seconds"])
            self.assertEqual({"pids_current", "pids_max", "pids_events_max", "memory_current", "memory_max",
                              "memory_events", "elapsed_seconds"}, set(data))
        self.assertEqual({"failure": "control-or-readiness-failure", "owned_exit": False, "cleanup": False,
                          "budget_diagnostic_category": "cgroup-task-memory-counters", "budget_diagnostic_written": True}, receipt)

    def test_unlimited_maxima_and_optional_kernel_events_have_closed_null_values(self):
        counters = {**self.COUNTERS, "pids.max": "max\n", "memory.max": "max\n",
                    "memory.events": "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\n"}
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            self.capture(Path(name), {}, counters)
            data = json.loads((Path(name) / "budget-diagnostic.json").read_bytes())
            self.assertIsNone(data["pids_max"])
            self.assertIsNone(data["memory_max"])
            self.assertIsNone(data["memory_events"]["oom_group_kill"])
            self.assertIsNone(data["memory_events"]["sock_throttled"])

    def test_malformed_overflow_duplicate_missing_and_unknown_counters_fail_capture_only(self):
        cases = [("pids.current", "-1"), ("pids.current", str(2**64)),
                 ("pids.current", "private-counter-canary"), ("pids.events", "max 1\nmax 2\n"),
                 ("memory.events", "oom 0\n"), ("memory.events", self.COUNTERS["memory.events"] + "private-counter-canary 1\n"),
                 ("pids.current", "1" * 4097)]
        for filename, value in cases:
            with self.subTest(filename=filename, value_length=len(value)), tempfile.TemporaryDirectory(prefix="budget-control-") as name:
                receipt = {"failure": "application-identity-rejected", "cleanup": False}
                self.capture(Path(name), receipt, {**self.COUNTERS, filename: value})
                self.assertFalse((Path(name) / "budget-diagnostic.json").exists())
                self.assertFalse(receipt["budget_diagnostic_written"])
                self.assertEqual("application-identity-rejected", receipt["failure"])
                self.assertFalse(receipt["cleanup"])
                self.assertNotIn("private-counter-canary", json.dumps(receipt))

    def test_unselected_or_traversing_cgroup_cannot_open_any_counter(self):
        for group in ("", "/system.slice/other.service", self.GROUP + "/child", self.GROUP + "/../other"):
            with self.subTest(group=group), mock.patch.object(proof.os, "open") as opened:
                receipt = {"failure": "original"}
                proof.record_budget_diagnostic(receipt, Path("/control"), group, 0)
                opened.assert_not_called()
                self.assertFalse(receipt["budget_diagnostic_written"])
                self.assertEqual("original", receipt["failure"])

    def test_unreadable_group_and_write_failure_never_publish_exception_text(self):
        with mock.patch.object(proof.os, "open", side_effect=OSError("private-counter-canary")):
            receipt = {"failure": "original"}
            proof.record_budget_diagnostic(receipt, Path("/control"), self.GROUP, 0)
        self.assertFalse(receipt["budget_diagnostic_written"])
        self.assertEqual("original", receipt["failure"])
        self.assertNotIn("private-counter-canary", json.dumps(receipt))
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            receipt = {"failure": "original"}
            original_open = Path.open

            def write_failure(path, *args, **kwargs):
                if path.name == "budget-diagnostic.json":
                    raise OSError("private-counter-canary")
                return original_open(path, *args, **kwargs)

            with mock.patch.object(Path, "open", autospec=True, side_effect=write_failure):
                self.capture(Path(name), receipt)
            self.assertFalse(receipt["budget_diagnostic_written"])
            self.assertEqual("original", receipt["failure"])

    def test_exclusive_destination_is_not_overwritten(self):
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            path = Path(name) / "budget-diagnostic.json"
            path.write_text("existing-private-control")
            receipt = {"failure": "original"}
            self.capture(Path(name), receipt)
            self.assertEqual("existing-private-control", path.read_text())
            self.assertFalse(receipt["budget_diagnostic_written"])
            self.assertEqual("original", receipt["failure"])

    def test_counter_symlink_is_not_followed(self):
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            directory = Path(name)
            (directory / "target").write_text("64\n")
            (directory / "pids.current").symlink_to(directory / "target")
            fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                with self.assertRaises(OSError):
                    proof.read_cgroup_counter(fd, "pids.current")
            finally:
                os.close(fd)

    def test_counter_output_bound_leaves_failure_and_no_written_file(self):
        with tempfile.TemporaryDirectory(prefix="budget-control-") as name:
            receipt = {"failure": "original"}
            with mock.patch.object(proof, "BUDGET_DIAGNOSTIC_LIMIT", 200):
                self.capture(Path(name), receipt)
            self.assertFalse((Path(name) / "budget-diagnostic.json").exists())
            self.assertFalse(receipt["budget_diagnostic_written"])
            self.assertEqual("original", receipt["failure"])


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
