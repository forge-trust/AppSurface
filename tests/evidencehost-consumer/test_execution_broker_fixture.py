#!/usr/bin/env python3
"""Portable descriptor controls for the root execution-broker fixture."""
import base64
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import threading
import struct
import tempfile
import unittest
from unittest.mock import Mock, call, patch
import xml.etree.ElementTree as ET

source = Path(__file__).with_name("test_execution_broker.py")
spec = importlib.util.spec_from_file_location("execution_broker_fixture", source)
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class DescriptorTests(unittest.TestCase):
    def test_peer_pin_creates_one_immutable_root_selected_control_snapshot(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-control-") as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            control = root / "control"
            (control / "broker").mkdir(parents=True)
            scenario = broker.Scenario("cli-coverage", root, root / "tool", root / "subject",
                                       output, 1001, 1002, 1003, 1004, "/usr/bin/dotnet",
                                       root / "tool" / "policy.json", "a" * 64,
                                       root / "operations.jsonl", control / "broker" / "control.sock")
            peer = Mock()
            peer.getsockopt.return_value = struct.pack("3i", 12345, 1001, 1002)
            snapshot = control / "worker-control.json"
            self.assertFalse(snapshot.exists())
            # The credential bytes are a portable fixture control. macOS does not
            # expose the Linux SO_PEERCRED constant; actual kernel peers remain native-only.
            with patch.object(broker.socket, "SO_PEERCRED", 17, create=True), patch.object(broker.os, "chown") as chown:
                self.assertEqual((12345, 1001, 1002), scenario.pin_peer(peer))
                self.assertEqual((12345, 1001, 1002), scenario.pin_peer(peer))
            captured = json.loads(snapshot.read_bytes())
            self.assertEqual(scenario.descriptor, captured)
            self.assertEqual(os.getpid(), captured["broker_pid"])
            self.assertEqual(str(snapshot), captured["descriptor_path"])
            self.assertEqual(str(control / "broker" / "control.sock"), captured["socket_path"])
            self.assertEqual(0o440, stat.S_IMODE(snapshot.stat().st_mode))
            self.assertEqual(2, chown.call_count, "A repeated identical peer must reuse the retained snapshot.")
            self.assertIn(call(snapshot, 0, 1002, follow_symlinks=False), chown.call_args_list)

    def test_every_scenario_creates_a_run_bound_to_its_root_and_authenticated_peer(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-") as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            run_ids = set()
            for name in broker.SCENARIOS:
                with self.subTest(scenario=name):
                    scenario = broker.Scenario(name, root, root / "tool", root / "subject",
                                               output, 1001, 1002, 1003, 1004, "/usr/bin/dotnet",
                                               root / "policy.json", "a" * 64,
                                               root / f"{name}.jsonl", root / name / "broker" / "control.sock")
                    descriptor = scenario.make_descriptor((12345, 1001, 1002))
                    self.assertEqual(f"fixture-{root.name}-{name}/1", descriptor["run_id"])
                    self.assertNotIn(descriptor["run_id"], run_ids)
                    run_ids.add(descriptor["run_id"])
                    self.assertEqual((12345, 1001, 1002),
                                     (descriptor["worker_pid"], descriptor["worker_uid"], descriptor["worker_gid"]))
                    self.assertEqual(scenario.parent_identity, descriptor["output_parent_identity"])
                    self.assertEqual(str(root / name / "broker" / "control.sock"), descriptor["socket_path"])
                    self.assertEqual(os.getpid(), descriptor["broker_pid"])
                    self.assertEqual(str(root / name / "worker-control.json"), descriptor["descriptor_path"])
                    self.assertEqual("b" * 40, descriptor["base_revision"])
                    self.assertEqual("c" * 40, descriptor["subject_revision"])
                    self.assertIsNone(descriptor["diff_file"])
                    self.assertIsNone(descriptor["diff_sha256"])
            self.assertEqual(len(broker.SCENARIOS), len(run_ids))


class CoverageReportTests(unittest.TestCase):
    def test_positive_report_has_covered_line_and_branch_items_matching_declared_totals(self):
        report = ET.fromstring(broker.REPORT)
        lines = report.findall("./packages/package/classes/class/lines/line")
        self.assertGreater(len(lines), 0, "ReportGenerator requires actual line items to retain valid coverage.")
        covered_lines = sum(int(line.attrib["hits"]) > 0 for line in lines)
        self.assertEqual(len(lines), covered_lines)
        covered_branches = valid_branches = 0
        for line in lines:
            if line.get("branch") == "true":
                condition = re.fullmatch(r"100% \((\d+)/(\d+)\)", line.attrib["condition-coverage"])
                self.assertIsNotNone(condition)
                covered, valid = map(int, condition.groups())
                self.assertGreater(valid, 0)
                self.assertEqual(valid, covered)
                covered_branches += covered
                valid_branches += valid
        self.assertGreater(valid_branches, 0)
        self.assertEqual(len(lines), int(report.attrib["lines-valid"]))
        self.assertEqual(covered_lines, int(report.attrib["lines-covered"]))
        self.assertEqual(valid_branches, int(report.attrib["branches-valid"]))
        self.assertEqual(covered_branches, int(report.attrib["branches-covered"]))


class CoverageAncestorPermissionsTests(unittest.TestCase):
    def test_worker_can_read_and_search_ancestors_while_distinct_subject_has_no_access(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-permissions-") as temporary:
            root = Path(temporary)
            worker_root = root / "worker"
            worker_root.mkdir()
            worker_gid = 1002
            # Exercise real chmod/stat; only privileged ownership assignment is recorded.
            with patch.object(broker.os, "chown") as chown:
                broker.configure_coverage_ancestors(root, worker_root, worker_gid)
            self.assertEqual([
                call(root, 0, worker_gid, follow_symlinks=False),
                call(worker_root, 0, worker_gid, follow_symlinks=False),
            ], chown.call_args_list)
            for ancestor in (root, worker_root):
                with self.subTest(ancestor=ancestor.name):
                    mode = stat.S_IMODE(ancestor.stat().st_mode)
                    self.assertEqual(stat.S_IRGRP | stat.S_IXGRP, mode & stat.S_IRWXG)
                    self.assertEqual(0, mode & stat.S_IRWXO,
                                     "The distinct non-root subject must have no ancestor access.")


class ProtocolRejectionTests(unittest.TestCase):
    def scenario(self, root, name):
        output = root / "output"
        output.mkdir(exist_ok=True)
        log = root / f"{name}.jsonl"
        log.touch()
        return broker.Scenario(name, root, root / "tool", root / "subject", output,
                               65534, 65532, 65533, 65531, "/usr/bin/dotnet", root / "policy.json",
                               "a" * 64, log, root / name / "broker" / "control.sock")

    def test_negative_length_declaration_is_fixed_and_has_no_implicit_artifact_request(self):
        with tempfile.TemporaryDirectory() as temporary:
            scenario = self.scenario(Path(temporary), "protocol-negative-length")
            response = scenario.response({"op": "artifacts", "relative_root": "coverage-protocol"},
                                         (12345, 65534, 65532))
            self.assertEqual([{"path": "reports/result.bin", "length_bytes": -1}], response["artifacts"])
            self.assertEqual(["artifacts"], [json.loads(line)["op"] for line in scenario.log_file.read_text().splitlines()])

    def test_declared_length_and_encoded_limit_responses_are_valid_base64_within_wire_bound(self):
        with tempfile.TemporaryDirectory() as temporary:
            for name, declared, actual in (("protocol-declared-length", 1, 2),
                                           ("protocol-encoded-limit", 131076, 131076)):
                with self.subTest(scenario=name):
                    scenario = self.scenario(Path(temporary), name)
                    peer = (12345, 65534, 65532)
                    declaration = scenario.response({"op": "artifacts", "relative_root": "coverage-protocol"}, peer)
                    self.assertEqual(declared, declaration["artifacts"][0]["length_bytes"])
                    chunk = scenario.response({"op": "artifact", "relative_root": "coverage-protocol",
                                               "relative_path": "reports/result.bin", "offset": 0}, peer)
                    self.assertEqual(actual, len(base64.b64decode(chunk["bytes_base64"], validate=True)))
                    self.assertTrue(chunk["end"])
                    wire = json.dumps(chunk, separators=(",", ":")).encode() + b"\n"
                    self.assertLess(len(wire), 1024 * 1024)
                    if name == "protocol-encoded-limit":
                        self.assertEqual(174768, len(chunk["bytes_base64"]))
                        self.assertGreater(len(chunk["bytes_base64"]), 174764)
                    self.assertEqual(["artifacts", "artifact"],
                                     [json.loads(line)["op"] for line in scenario.log_file.read_text().splitlines()])

    def test_duplicate_declarations_allow_one_valid_empty_terminal_chunk_before_duplicate_guard(self):
        with tempfile.TemporaryDirectory() as temporary:
            scenario = self.scenario(Path(temporary), "protocol-duplicate-declaration")
            peer = (12345, 65534, 65532)
            declarations = scenario.response({"op": "artifacts", "relative_root": "coverage-protocol"}, peer)
            self.assertEqual(2, len(declarations["artifacts"]))
            first, duplicate = declarations["artifacts"]
            self.assertEqual(first, duplicate)
            self.assertEqual("reports/protocol-private-canary.bin", first["path"])
            self.assertEqual(0, first["length_bytes"])
            chunk = scenario.response({"op": "artifact", "relative_root": "coverage-protocol",
                                       "relative_path": first["path"], "offset": 0}, peer)
            decoded = base64.b64decode(chunk["bytes_base64"], validate=True)
            self.assertEqual("", chunk["bytes_base64"])
            self.assertEqual(b"", decoded)
            self.assertEqual(first["length_bytes"], len(decoded))
            self.assertTrue(chunk["end"])
            self.assertEqual(["artifacts", "artifact"],
                             [json.loads(line)["op"] for line in scenario.log_file.read_text().splitlines()])

    def test_decoded_limit_response_is_one_byte_over_at_exact_legal_base64_length(self):
        with tempfile.TemporaryDirectory() as temporary:
            scenario = self.scenario(Path(temporary), "protocol-decoded-limit")
            peer = (12345, 65534, 65532)
            declarations = scenario.response({"op": "artifacts", "relative_root": "coverage-protocol"}, peer)
            row, = declarations["artifacts"]
            self.assertEqual(128 * 1024 + 1, row["length_bytes"])
            chunk = scenario.response({"op": "artifact", "relative_root": "coverage-protocol",
                                       "relative_path": row["path"], "offset": 0}, peer)
            decoded = base64.b64decode(chunk["bytes_base64"], validate=True)
            self.assertEqual(row["length_bytes"], len(decoded))
            self.assertEqual(174764, len(chunk["bytes_base64"]))
            self.assertEqual(4 * ((128 * 1024 + 2) // 3), len(chunk["bytes_base64"]))
            self.assertEqual(128 * 1024, len(decoded[:-1]))
            self.assertEqual(174764, len(base64.b64encode(decoded[:-1])))
            self.assertTrue(decoded.startswith(broker.PROTOCOL_PRIVATE_CANARY))
            self.assertTrue(chunk["end"])
            self.assertLess(len(json.dumps(chunk, separators=(",", ":")).encode()) + 1, 1024 * 1024)
            self.assertEqual(["artifacts", "artifact"],
                             [json.loads(line)["op"] for line in scenario.log_file.read_text().splitlines()])

    def test_empty_chunk_response_is_nonterminal_against_a_positive_declaration(self):
        with tempfile.TemporaryDirectory() as temporary:
            scenario = self.scenario(Path(temporary), "protocol-empty-chunk")
            peer = (12345, 65534, 65532)
            declarations = scenario.response({"op": "artifacts", "relative_root": "coverage-protocol"}, peer)
            row, = declarations["artifacts"]
            self.assertEqual(1, row["length_bytes"])
            chunk = scenario.response({"op": "artifact", "relative_root": "coverage-protocol",
                                       "relative_path": row["path"], "offset": 0}, peer)
            self.assertEqual("", chunk["bytes_base64"])
            self.assertEqual(b"", base64.b64decode(chunk["bytes_base64"], validate=True))
            self.assertFalse(chunk["end"])
            self.assertEqual(["artifacts", "artifact"],
                             [json.loads(line)["op"] for line in scenario.log_file.read_text().splitlines()])

    def test_false_owned_exit_cannot_acknowledge_terminal_exit(self):
        with tempfile.TemporaryDirectory() as temporary:
            scenario = self.scenario(Path(temporary), "protocol-owned-exit-false")
            peer = (12345, 65534, 65532)
            self.assertTrue(scenario.response({"op": "stop"}, peer)["ok"])
            self.assertFalse(scenario.response({"op": "wait"}, peer)["owned_exit"])
            self.assertFalse(scenario.response({"op": "exit"}, peer)["ok"])
            self.assertFalse(scenario.exit_seen)

    def test_exited_leader_still_requires_descendant_group_kill_and_absence(self):
        process = Mock(pid=12345, returncode=0)
        process.wait.return_value = 0
        # Portable procedure double: a group survives TERM despite an exited leader.
        with patch.object(broker.os, "killpg", side_effect=[None, None, None, None, ProcessLookupError()]) as kill:
            self.assertTrue(broker.join_test_process(process))
        self.assertEqual([call(12345, 0), call(12345, broker.signal.SIGTERM), call(12345, 0),
                          call(12345, broker.signal.SIGKILL), call(12345, 0)], kill.call_args_list)
        self.assertEqual(2, process.wait.call_count)
        self.assertTrue(all(0 <= item.kwargs["timeout"] <= 3 for item in process.wait.call_args_list))
        with patch.object(broker.os, "killpg", return_value=None):
            self.assertFalse(broker.join_test_process(process, cleanup_seconds=0.05))

    def test_server_and_handler_threads_are_joined_and_survivors_reject_cleanup(self):
        server = Mock()
        server.handler_lock = broker.threading.Lock()
        handler = Mock()
        handler.is_alive.return_value = False
        server.handler_threads = [handler]
        accept = Mock()
        accept.is_alive.side_effect = [True, False, True, False]
        self.assertTrue(broker.join_broker_servers([server], [accept]))
        server.shutdown.assert_called_once_with()
        server.server_close.assert_called_once_with()
        accept.join.assert_called_once()
        handler.join.assert_called_once()
        handler.is_alive.return_value = True
        self.assertFalse(broker.join_broker_servers([server], [accept]))


class BoundedOwnershipTests(unittest.TestCase):
    def test_owner_selected_budgets_preserve_full_lane_and_reject_unbounded_values(self):
        argv = ["--worker-uid", "65534", "--worker-gid", "65532", "--subject-uid", "65533",
                "--subject-gid", "65531", "--reportgenerator-package", "/fixture/reportgenerator/5.5.10"]
        self.assertEqual(4200, broker.parser().parse_args(argv).command_timeout_seconds)
        self.assertEqual(600, broker.parser().parse_args(argv + ["--command-timeout-seconds", "600"]).command_timeout_seconds)
        for value in ("0", "4201", "-1", "nan", "inf", "1.5"):
            with self.subTest(value=value), self.assertRaises(broker.argparse.ArgumentTypeError):
                broker.parse_command_timeout(value)

    def test_real_owned_command_timeout_reaps_and_joins_before_returning_original_timeout(self):
        actual_popen = subprocess.Popen
        captured = []

        def start(*args, **kwargs):
            process = actual_popen(*args, **kwargs)
            captured.append(process)
            return process

        with patch.object(broker.subprocess, "Popen", side_effect=start):
            with self.assertRaises(subprocess.TimeoutExpired):
                broker.run_test_command([sys.executable, "-c", "import threading; threading.Event().wait()"],
                                        dict(os.environ), 0.1)
        self.assertEqual(1, len(captured))
        self.assertIsNotNone(captured[0].returncode)
        with self.assertRaises(ProcessLookupError):
            os.killpg(captured[0].pid, 0)
        self.assertEqual(0, broker.run_test_command([sys.executable, "-c", "pass"], dict(os.environ), 5))

    def test_surviving_real_daemon_handler_returns_failure_and_does_not_trap_interpreter_exit(self):
        # Actual child interpreter and thread; this deliberately unjoined handler must never report cleanup.
        script = """
import importlib.util, os, pathlib, socket, sys, tempfile, threading, types
from unittest.mock import patch
spec = importlib.util.spec_from_file_location('fixture', sys.argv[1])
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
entered = threading.Event()
blocked = threading.Event()
def pin(connection):
    entered.set()
    blocked.wait()
with tempfile.TemporaryDirectory() as temporary:
    scenario = types.SimpleNamespace(name='blocked-handler', socket_path=pathlib.Path(temporary) / 'control.sock',
                                     pin_peer=pin)
    # Only privileged metadata is mocked. Actual server/thread/socket lifetime runs in this child.
    with patch.object(fixture.os, 'chown'):
        server = fixture.scenario_server(scenario, os.getgid())
    accept = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': 0.01}, daemon=True)
    accept.start()
    client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    client.connect(str(scenario.socket_path))
    client.sendall(b'{"op":"ready"}\\n')
    if not entered.wait(timeout=2):
        raise SystemExit(3)
    joined = fixture.join_broker_servers([server], [accept], cleanup_seconds=0.05)
    client.close()
    print('cleanup-unconfirmed' if not joined else 'unexpected-cleanup', flush=True)
    raise SystemExit(2 if not joined else 0)
"""
        process = subprocess.Popen([sys.executable, "-c", script, str(source)], stdout=subprocess.PIPE,
                                   stderr=subprocess.PIPE, start_new_session=True)
        try:
            stdout, _ = process.communicate(timeout=3)
            self.assertEqual(2, process.returncode)
            self.assertEqual(b"cleanup-unconfirmed\n", stdout)
        finally:
            self.assertTrue(broker.join_test_process(process))

    def test_real_group_absence_is_polled_after_leader_reap_until_sibling_is_reaped(self):
        import select

        # Both actual children belong to this test and share one group in the parent's session.
        leader = subprocess.Popen([sys.executable, "-c", "import threading; threading.Event().wait()"],
                                  preexec_fn=lambda: os.setpgid(0, 0))
        sibling = None
        reaper = None
        after_leader_reap = threading.Event()
        sibling_reaped = threading.Event()
        kill_attempted = threading.Event()
        real_killpg = os.killpg
        observations = []
        reaper_errors = []
        try:
            # The ACK follows handler installation: TERM must leave a live group member until real KILL.
            sibling = subprocess.Popen([sys.executable, "-c",
                                        "import signal,sys,threading; "
                                        "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                                        "sys.stdout.buffer.write(b'A'); sys.stdout.buffer.flush(); "
                                        "threading.Event().wait()"], stdout=subprocess.PIPE,
                                       preexec_fn=lambda: os.setpgid(0, leader.pid))
            self.assertEqual([sibling.stdout], select.select([sibling.stdout], [], [], 2)[0])
            self.assertEqual(b"A", sibling.stdout.read(1))
            self.assertIsNone(sibling.poll())

            def reap_sibling():
                try:
                    if not after_leader_reap.wait(timeout=3):
                        raise TimeoutError("post-leader-probe-missing")
                    sibling.wait(timeout=2)
                except Exception as error:
                    reaper_errors.append(type(error).__name__)
                finally:
                    sibling_reaped.set()

            reaper = threading.Thread(target=reap_sibling, daemon=True)
            reaper.start()

            def observe(pgid, signal):
                try:
                    try:
                        result = real_killpg(pgid, signal)
                    except PermissionError:
                        # Darwin can return EPERM for a group containing only this unreaped zombie.
                        # This test-only observation remains PRESENT. Confirm this owned child's
                        # actual SIGKILL wait status before returning; never map EPERM to absence.
                        if (signal != 0 or not kill_attempted.is_set() or leader.returncode is None
                                or sibling.returncode is not None):
                            raise
                        observations.append('group-still-present-after-leader-reap')
                        after_leader_reap.set()
                        self.assertTrue(sibling_reaped.wait(timeout=2))
                        self.assertEqual([], reaper_errors)
                        self.assertEqual(-broker.signal.SIGKILL, sibling.returncode)
                        return None
                    if signal == 0 and leader.returncode is not None and kill_attempted.is_set():
                        observations.append('group-still-present-after-leader-reap')
                        after_leader_reap.set()
                    return result
                finally:
                    if signal == broker.signal.SIGKILL:
                        kill_attempted.set()

            with patch.object(broker.os, "killpg", side_effect=observe):
                self.assertTrue(broker.join_test_process(leader, cleanup_seconds=5))
            self.assertIn('group-still-present-after-leader-reap', observations)
            self.assertTrue(sibling_reaped.wait(timeout=2))
            reaper.join(timeout=2)
            self.assertFalse(reaper.is_alive())
            self.assertEqual([], reaper_errors)
            self.assertEqual(-broker.signal.SIGKILL, sibling.returncode)
            with self.assertRaises(ProcessLookupError):
                real_killpg(leader.pid, 0)
        finally:
            # A failed probe must not skip either owned child's reap or the retained thread join.
            after_leader_reap.set()
            cleanup_errors = []
            try:
                real_killpg(leader.pid, broker.signal.SIGKILL)
            except OSError:
                pass  # Individual waits and final physical absence are still mandatory below.
            for process in (leader, sibling):
                if process is not None:
                    try:
                        process.kill()
                    except OSError:
                        pass
                    try:
                        process.wait(timeout=2)
                    except Exception as error:
                        cleanup_errors.append(type(error).__name__)
            if reaper is not None:
                reaper.join(timeout=2)
                if reaper.is_alive():
                    cleanup_errors.append("reaper-not-joined")
            if sibling is not None and sibling.stdout is not None:
                sibling.stdout.close()
            try:
                real_killpg(leader.pid, 0)
            except ProcessLookupError:
                pass
            except OSError as error:
                cleanup_errors.append(type(error).__name__)
            else:
                cleanup_errors.append("owned-group-present")
            self.assertEqual([], cleanup_errors)


if __name__ == "__main__":
    unittest.main()
