#!/usr/bin/env python3
"""Portable fixture controls; privileged ownership and Linux peer credentials are mocked."""
import importlib.util
import json
import os
from pathlib import Path
import stat
import struct
import tempfile
import unittest
from unittest.mock import Mock, call, patch

source = Path(__file__).with_name("test_control_protocol.py")
spec = importlib.util.spec_from_file_location("control_protocol_fixture", source)
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class DescriptorControls(unittest.TestCase):
    def test_valid_descriptor_uses_closed_outer_grammar_and_observed_peer(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            control = Path(temporary) / "valid-handshake"
            socket_path = control / "broker" / "control.sock"
            peer = (12345, 1001, 1002)
            descriptor = fixture.make_descriptor(peer, socket_path, "/usr/share/dotnet/dotnet",
                                                 "valid", os.getpid())
            required = {
                "schema", "run_id", "worker_pid", "broker_pid", "worker_uid", "worker_gid", "subject_uid", "subject_gid",
                "unit", "cgroup", "job_deadline_utc", "tool_root", "subject_root", "output_parent", "output_slot", "dotnet_path",
                "test_output_root", "policy_file", "mode", "socket_path", "descriptor_path", "entry_sha256", "base_revision",
                "subject_revision", "workflow_identity", "provider", "platform", "proof_digest", "policy_sha256",
                "output_parent_identity", "observation_profile_ids", "observation_producer_ids", "paths", "admission_seconds",
                "start_seconds", "collection_seconds", "cleanup_seconds", "stopping_seconds",
            }
            self.assertEqual(required | {"diff_file", "diff_sha256", "solution"}, set(descriptor))
            self.assertEqual(38, len(required))
            self.assertRegex(descriptor["run_id"], r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
            self.assertEqual(f"control-{control.parent.name}-valid-handshake/1", descriptor["run_id"])
            self.assertEqual(str(socket_path), descriptor["socket_path"])
            self.assertEqual(str(control / "worker-control.json"), descriptor["descriptor_path"])
            self.assertEqual(peer, (descriptor["worker_pid"], descriptor["worker_uid"], descriptor["worker_gid"]))
            self.assertEqual(os.getpid(), descriptor["broker_pid"])
            self.assertEqual(peer[1:], (descriptor["output_parent_identity"]["uid"], descriptor["output_parent_identity"]["gid"]))
            self.assertEqual("/system.slice/" + descriptor["unit"], descriptor["cgroup"])
            self.assertEqual("b" * 40, descriptor["base_revision"])
            self.assertEqual("c" * 40, descriptor["subject_revision"])
            self.assertIsNone(descriptor["diff_file"])
            self.assertIsNone(descriptor["diff_sha256"])
            self.assertIsNone(descriptor["solution"])
            self.assertLessEqual(len(str(socket_path)), 100)

    def test_identity_negatives_change_the_intended_runtime_binding_only(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            socket_path = Path(temporary) / "identity" / "broker" / "control.sock"
            peer = (12345, 1001, 1002)
            for scenario, field, value in (
                ("worker-pid-mismatch", "worker_pid", peer[0] + 1),
                ("worker-uid-mismatch", "worker_uid", peer[1] + 1),
                ("worker-gid-mismatch", "worker_gid", peer[2] + 1),
                ("expired-wall-deadline", "job_deadline_utc", "2000-01-01T00:00:00Z"),
            ):
                with self.subTest(scenario=scenario):
                    descriptor = fixture.make_descriptor(peer, socket_path, "/usr/bin/dotnet", scenario, os.getpid())
                    self.assertEqual(value, descriptor[field])
                    self.assertEqual(descriptor["worker_uid"], descriptor["output_parent_identity"]["uid"])
                    self.assertEqual(descriptor["worker_gid"], descriptor["output_parent_identity"]["gid"])
                    self.assertEqual(os.getpid(), descriptor["broker_pid"])
                    self.assertEqual(str(socket_path), descriptor["socket_path"])
                    self.assertEqual(str(socket_path.parent.parent / "worker-control.json"), descriptor["descriptor_path"])


class LayoutControls(unittest.TestCase):
    def test_normal_and_nonroot_negative_brokers_keep_control_root_ownership(self):
        for broker_uid, broker_gid, outer_mode in ((0, 0, 0o710), (fixture.SUBJECT_UID, fixture.SUBJECT_GID, 0o711)):
            with self.subTest(broker_uid=broker_uid), tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
                root = Path(temporary)
                control = root / "case"
                process = Mock()
                process.poll.return_value = None

                def started(*args, **kwargs):
                    Path(str(socket_path) + ".ready").touch()
                    return process

                with patch.object(fixture.os, "chown") as chown:
                    socket_path = fixture.prepare_control_root(control, broker_uid)
                    with patch.object(fixture.subprocess, "Popen", side_effect=started) as popen:
                        self.assertIs(process, fixture.start_broker(root / "driver.py", socket_path, "valid", "/usr/bin/dotnet",
                                                                   "/usr/bin/setpriv", broker_uid, broker_gid, control, root))
                self.assertEqual(control / "broker" / "control.sock", socket_path)
                self.assertEqual(outer_mode, stat.S_IMODE(control.stat().st_mode))
                self.assertEqual(0o710, stat.S_IMODE(socket_path.parent.stat().st_mode))
                self.assertEqual([
                    call(control, 0, fixture.WORKER_GID, follow_symlinks=False),
                    call(control / "broker", broker_uid, fixture.WORKER_GID, follow_symlinks=False),
                ], chown.call_args_list)
                command = popen.call_args.args[0]
                if broker_uid:
                    self.assertEqual(["/usr/bin/setpriv", "--no-new-privs", f"--reuid={broker_uid}", f"--regid={broker_gid}",
                                      "--clear-groups", "--"], command[:6])
                else:
                    self.assertEqual(fixture.sys.executable, command[0])

    def test_snapshot_is_fresh_read_only_and_cannot_replace_existing_file_or_link(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            control = Path(temporary) / "case"
            (control / "broker").mkdir(parents=True)
            socket_path = control / "broker" / "control.sock"
            descriptor = fixture.make_descriptor((12345, 1001, 1002), socket_path, "/usr/bin/dotnet", "valid", os.getpid())
            snapshot = control / "worker-control.json"
            with patch.object(fixture.os, "fchown") as chown:
                fixture.write_descriptor_snapshot(socket_path, descriptor, 1002)
            self.assertEqual(descriptor, json.loads(snapshot.read_bytes()))
            self.assertEqual(0o440, stat.S_IMODE(snapshot.stat().st_mode))
            self.assertEqual(1, snapshot.stat().st_nlink)
            self.assertEqual((0, 1002), chown.call_args.args[1:])
            original = snapshot.read_bytes()
            with self.assertRaises(FileExistsError):
                fixture.write_descriptor_snapshot(socket_path, {}, 1002)
            self.assertEqual(original, snapshot.read_bytes())
            snapshot.unlink()
            target = control / "canary"
            target.write_bytes(b"unchanged-canary")
            snapshot.symlink_to(target)
            with self.assertRaises(OSError):
                fixture.write_descriptor_snapshot(socket_path, {}, 1002)
            self.assertEqual(b"unchanged-canary", target.read_bytes())

    def test_broker_rejects_the_old_flat_socket_layout_before_starting_any_process(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            root = Path(temporary)
            with patch.object(fixture.subprocess, "Popen") as popen, patch.object(fixture.os, "chown") as chown:
                with self.assertRaisesRegex(fixture.HarnessFailure, "^broker-control-layout-mismatch$"):
                    fixture.start_broker(root / "driver.py", root / "c.sock", "valid", "/usr/bin/dotnet",
                                         "/usr/bin/setpriv", 0, 0, root, root)
            popen.assert_not_called()
            chown.assert_not_called()


class BrokerResponseControls(unittest.TestCase):
    def test_ready_response_and_retained_snapshot_are_identical_after_observed_peer(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            control = Path(temporary) / "case"
            (control / "broker").mkdir(parents=True)
            socket_path = control / "broker" / "control.sock"
            listener = Mock()
            connection = Mock()
            connection.__enter__ = Mock(return_value=connection)
            connection.__exit__ = Mock(return_value=False)
            connection.recv.return_value = b'{"op":"ready"}\n'
            connection.getsockopt.return_value = struct.pack("3i", 12345, 1001, 1002)
            listener.accept.return_value = (connection, None)
            listener.bind.side_effect = lambda address: Path(address).touch()
            modes = []
            listener.listen.side_effect = lambda backlog: modes.append(stat.S_IMODE(socket_path.stat().st_mode))
            with patch.object(fixture.socket, "socket", return_value=listener), patch.object(fixture.os, "geteuid", return_value=0), \
                    patch.object(fixture.os, "chown"), patch.object(fixture.os, "fchown"):
                self.assertEqual(0, fixture.serve(socket_path, "valid", "/usr/bin/dotnet"))
            response = json.loads(connection.sendall.call_args.args[0])
            retained = json.loads((control / "worker-control.json").read_bytes())
            self.assertEqual(retained, response["descriptor"])
            self.assertEqual(600.0, response["job_remaining_seconds"])
            self.assertEqual([0o660], modes)
            self.assertFalse(socket_path.exists())

    def test_nonroot_and_replacement_brokers_receive_no_request_and_write_no_snapshot(self):
        for scenario, uid, socket_mode in (("wrong-root-peer", fixture.SUBJECT_UID, 0o666), ("broker-second", 0, 0o660)):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
                control = Path(temporary) / "case"
                (control / "broker").mkdir(parents=True)
                socket_path = control / "broker" / "control.sock"
                listener = Mock()
                connection = Mock()
                connection.__enter__ = Mock(return_value=connection)
                connection.__exit__ = Mock(return_value=False)
                connection.recv.return_value = b""
                connection.getsockopt.return_value = struct.pack("3i", 12345, 1001, 1002)
                listener.accept.return_value = (connection, None)
                listener.bind.side_effect = lambda address: Path(address).touch()
                modes = []
                listener.listen.side_effect = lambda backlog: modes.append(stat.S_IMODE(socket_path.stat().st_mode))
                with patch.object(fixture.socket, "socket", return_value=listener), patch.object(fixture.os, "geteuid", return_value=uid), \
                        patch.object(fixture.os, "chown"), patch.object(fixture.os, "fchown"):
                    self.assertEqual(0, fixture.serve(socket_path, scenario, "/usr/bin/dotnet"))
                self.assertEqual([socket_mode], modes)
                self.assertFalse((control / "worker-control.json").exists())
                connection.sendall.assert_not_called()


class ReplacementOrderingControls(unittest.TestCase):
    def test_replacement_waits_for_socket_and_ready_removal_while_first_pid_lives(self):
        for operation in (None, "start", "wait"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory(prefix="evctl-", dir="/tmp") as temporary:
                root = Path(temporary)
                name = "broker-replacement" if operation is None else "application-replace-" + operation
                socket_path = root / name / "broker" / "control.sock"
                ready_path = Path(str(socket_path) + ".ready")
                events = []
                first = Mock(pid=1001)
                first.poll.return_value = None
                second = Mock(pid=1002, returncode=0)
                second.poll.return_value = 0
                worker = Mock(returncode=20)
                worker.poll.return_value = 20
                worker.communicate.return_value = ('{"status":"rejected","code":"ASEVD402"}\n', "")

                def started(*args):
                    if args[2] != "broker-second":
                        socket_path.touch()
                        ready_path.touch()
                        return first
                    self.assertFalse(socket_path.exists())
                    self.assertFalse(ready_path.exists())
                    self.assertIsNone(first.poll())
                    events.append("second-started")
                    return second

                def connected(*args):
                    socket_path.unlink()
                    self.assertTrue(ready_path.is_file())
                    events.append("old-socket-gone")
                    return json.dumps({"status": "connected", "armed": True} if operation is None else
                                      {"status": "application-held", "start_ack": operation == "wait"}) + "\n"

                def wait_for_ready_removal(_seconds):
                    self.assertFalse(socket_path.exists())
                    self.assertTrue(ready_path.is_file())
                    self.assertIsNone(first.poll())
                    self.assertNotIn("second-started", events)
                    events.append("wait-on-stale-ready")
                    ready_path.unlink()

                def first_join(*args, **kwargs):
                    first.poll.return_value = 0

                first.wait.side_effect = first_join
                with patch.object(fixture.os, "chown"), patch.object(fixture, "start_broker", side_effect=started), \
                        patch.object(fixture, "start_worker", return_value=worker), \
                        patch.object(fixture, "read_worker_line", side_effect=connected), \
                        patch.object(fixture.time, "sleep", side_effect=wait_for_ready_removal):
                    fixture.run_broker_replacement(root / "driver.py", root, root, root / "worker.dll",
                                                   "/usr/bin/dotnet", "/usr/bin/setpriv", operation)
                self.assertEqual(["old-socket-gone", "wait-on-stale-ready", "second-started"], events)
                first.stdin.write.assert_called_once_with("release\n")
                worker.stdin.write.assert_called_once_with("stop\n" if operation is None else "operate\n")


class ReplacementDiagnosticControls(unittest.TestCase):
    @staticmethod
    def details(failure):
        return json.loads(str(failure).split("safeHarnessFailure=", 1)[1])

    def run_result(self, operation, stdout, stderr="", exit_code=20, *, timeout=False):
        """Drive the real coordinator with process doubles; no native credential claim."""
        with tempfile.TemporaryDirectory(prefix="evctl-", dir="/tmp") as temporary:
            root = Path(temporary)
            first = Mock(pid=1001)
            first.poll.return_value = None
            first.wait.side_effect = lambda *args, **kwargs: setattr(first.poll, "return_value", 0)
            second = Mock(pid=1002, returncode=0)
            second.poll.return_value = 0
            worker = Mock(returncode=exit_code)
            worker.poll.return_value = exit_code
            worker.communicate.return_value = (stdout, stderr)
            if timeout:
                worker.poll.return_value = None
                worker.communicate.side_effect = fixture.subprocess.TimeoutExpired("private-secret-path", 15,
                                                                                  output=stdout, stderr=stderr)

                def joined_worker():
                    worker.returncode = -9
                    worker.poll.return_value = -9

                worker.wait.side_effect = joined_worker
            connected = {"status": "connected", "armed": True} if operation is None else {
                "status": "application-held", "start_ack": operation == "wait"}
            failure = None
            with patch.object(fixture.os, "chown"), \
                    patch.object(fixture, "start_broker", side_effect=[first, second]), \
                    patch.object(fixture, "start_worker", return_value=worker), \
                    patch.object(fixture, "read_worker_line", return_value=json.dumps(connected)):
                try:
                    fixture.run_broker_replacement(root / "driver.py", root, root, root / "worker.dll",
                                                   "/usr/bin/dotnet", "/usr/bin/setpriv", operation)
                except fixture.HarnessFailure as error:
                    failure = error
            first.stdin.write.assert_called_once_with("release\n")
            first.wait.assert_called_once()
            if timeout:
                worker.kill.assert_called_once()
                worker.wait.assert_called_once()
            return failure, second

    def test_unknown_result_fields_and_values_never_enter_closed_diagnostic(self):
        canary = "private-secret-path-protocol-canary"
        for value in ({"status": canary, "code": canary, "exception": canary * 10000},
                      {"status": [canary], "code": {"secret": canary}}, [canary], None):
            with self.subTest(value_type=type(value).__name__):
                failure = fixture.replacement_failure(canary, canary, True, value)
                details = self.details(failure)
                self.assertEqual({"category", "mode", "worker_exit", "worker_status", "worker_code"}, set(details))
                self.assertEqual("replacement-control-failed", details["category"])
                self.assertEqual("unrecognized", details["mode"])
                self.assertIsNone(details["worker_exit"])
                self.assertNotIn(canary, str(failure))
                self.assertLessEqual(len(str(failure).encode("utf-8")), 512)

    def test_wrong_code_or_exit_stays_failed_and_records_only_observed_closed_facts(self):
        for operation in (None, "start", "wait"):
            for code, exit_code in (("ASEVD410", 20), ("ASEVD402", 21)):
                with self.subTest(operation=operation, code=code, exit=exit_code):
                    failure, second = self.run_result(operation, json.dumps({"status": "rejected", "code": code}),
                                                      exit_code=exit_code)
                    self.assertIsNotNone(failure)
                    self.assertEqual({"category": "worker-result-mismatch", "mode": operation or "stop",
                                      "worker_exit": exit_code, "worker_status": "rejected", "worker_code": code},
                                     self.details(failure))
                    second.wait.assert_not_called()
            with self.subTest(operation=operation, exact_rejection=True):
                failure, second = self.run_result(operation, '{"status":"rejected","code":"ASEVD402"}\n')
                self.assertIsNone(failure)
                second.wait.assert_called_once_with(timeout=10)

    def test_missing_malformed_or_canary_output_fails_without_raw_echo(self):
        cases = (("", "", "worker-result-missing"),
                 ("private-secret-path{", "", "worker-result-invalid"),
                 ('["private-secret-path"]', "", "worker-result-shape"),
                 ('{"status":"rejected","code":"ASEVD402"}', "protocol-canary-private-secret-path",
                  "worker-echoed-protocol-canary"))
        for stdout, stderr, category in cases:
            with self.subTest(category=category):
                failure, second = self.run_result("start", stdout, stderr, exit_code=21)
                self.assertIsNotNone(failure)
                details = self.details(failure)
                self.assertEqual(category, details["category"])
                self.assertEqual(21, details["worker_exit"])
                self.assertEqual("start", details["mode"])
                self.assertIsNone(details["worker_status"])
                self.assertIsNone(details["worker_code"])
                self.assertNotIn("private-secret-path", str(failure))
                self.assertNotIn("protocol-canary", str(failure).replace("worker-echoed-protocol-canary", ""))
                second.wait.assert_not_called()


    def test_timeout_reports_joined_exit_without_timeout_output_or_exception(self):
        failure, second = self.run_result("wait", "protocol-canary-private-secret-path", "private-secret-path",
                                          exit_code=None, timeout=True)
        self.assertEqual({"category": "worker-timeout", "mode": "wait", "worker_exit": -9,
                          "worker_status": None, "worker_code": None}, self.details(failure))
        self.assertNotIn("private-secret-path", str(failure))
        self.assertNotIn("protocol-canary", str(failure))
        second.wait.assert_not_called()


class ApplicationDescriptorControls(unittest.TestCase):
    def test_v2_complete_declarations_nine_roles_and_eight_distinct_identities(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            socket_path = Path(temporary) / "application-valid" / "broker" / "control.sock"
            peer = (12345, fixture.WORKER_UID, fixture.WORKER_GID)
            descriptor = fixture.make_descriptor(peer, socket_path, "/usr/bin/dotnet", "application-valid", os.getpid())
            self.assertEqual("evidence-worker-linux-v2", descriptor["schema"])
            application = descriptor["application"]
            self.assertEqual({"application_id", "application_version", "build_id", "catalogue_digest", "entry_digest",
                              "aspire_sdk_version", "resources", "producers", "bundle_files", "capabilities",
                              "application_uid", "application_gid", "results_gid", "resource_access_gid"}, set(application))
            identities = [descriptor[key] for key in ("worker_uid", "worker_gid", "subject_uid", "subject_gid")]
            identities += [application[key] for key in ("application_uid", "application_gid", "results_gid", "resource_access_gid")]
            self.assertEqual(8, len(set(identities)))
            self.assertTrue(all(value > 0 for value in identities))
            self.assertEqual(peer, (descriptor["worker_pid"], descriptor["worker_uid"], descriptor["worker_gid"]))
            self.assertEqual("13.4.4", application["aspire_sdk_version"])
            self.assertEqual({"apphost", "apphost_runtime_configuration", "resource", "resource_runtime_configuration",
                              "dcp", "dcp_extension", "dependency", "declared_input", "dependency_manifest"},
                             {item["role"] for item in application["bundle_files"]})
            self.assertEqual(9, len(application["bundle_files"]))
            for item in application["bundle_files"]:
                self.assertEqual(0o555 if item["role"] in ("dcp", "dcp_extension") else 0o444, item["mode"])
            self.assertEqual(["input/request.json"], application["capabilities"]["read_only_inputs"])
            self.assertEqual(fixture.RESOURCE_ID, application["producers"][0]["required_resources"][0])
            self.assertEqual({"logical_name", "relative_root", "media_type", "required", "maximum_bytes"},
                             set(application["producers"][0]["artifact_slots"][0]))
            self.assertEqual({"min_line_percent", "min_branch_percent", "min_patch_line_percent", "min_patch_branch_percent",
                              "patch_line_mode", "tolerance_percent"}, set(application["producers"][0]["coverage_gate"]))
            self.assertEqual(fixture.ENTRY_DIGEST, application["entry_digest"])

    def test_each_synthetic_descriptor_has_fresh_nested_data(self):
        first = fixture.make_application_descriptor()
        first["resources"][0]["requires"].append("changed")
        first["producers"][0]["artifact_slots"][0]["maximum_bytes"] = 0
        first["bundle_files"][0]["relative_path"] = "changed"
        first["capabilities"]["read_only_inputs"].clear()
        second = fixture.make_application_descriptor()
        self.assertEqual([], second["resources"][0]["requires"])
        self.assertEqual(1024, second["producers"][0]["artifact_slots"][0]["maximum_bytes"])
        self.assertEqual("app.dll", second["bundle_files"][0]["relative_path"])
        self.assertEqual(["input/request.json"], second["capabilities"]["read_only_inputs"])

    def test_long_socket_path_fails_before_mkdir_or_privileged_changes(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            control = Path(temporary) / ("a" * 101)
            with patch.object(fixture.os, "chown") as chown:
                with self.assertRaisesRegex(fixture.HarnessFailure, "^broker-control-path-limit$"):
                    fixture.prepare_control_root(control, 0)
            self.assertFalse(control.exists())
            chown.assert_not_called()


class ApplicationWireControls(unittest.TestCase):
    peer = (12345, fixture.WORKER_UID, fixture.WORKER_GID)

    def connection(self, request, *, eof=b"", peer=None):
        connection = Mock()
        connection.__enter__ = Mock(return_value=connection)
        connection.__exit__ = Mock(return_value=False)
        connection.getsockopt.return_value = struct.pack("3i", *(peer or self.peer))
        connection.recv.side_effect = [json.dumps(request).encode() + b"\n", eof]
        return connection

    def run_operations(self, directory, scenario, connections):
        listener = Mock()
        listener.accept.side_effect = [(connection, None) for connection in connections]
        socket_path = directory / "broker" / "control.sock"
        socket_path.parent.mkdir()
        fixture.serve_application_operations(listener, socket_path, scenario, self.peer)
        receipt_path = directory / "protocol-operations.json"
        self.assertEqual(0o600, stat.S_IMODE(receipt_path.stat().st_mode))
        return json.loads(receipt_path.read_bytes())

    def start_request(self):
        return {"op": "application-start", "application_id": fixture.APPLICATION_ID, "entry_digest": fixture.ENTRY_DIGEST}

    def wait_request(self):
        return {"op": "resource-wait", "lease_id": fixture.LEASE_ID, "resource_id": fixture.RESOURCE_ID}

    def test_exact_start_then_readiness_then_fresh_stop_wait_frames(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            start = self.connection(self.start_request()); wait = self.connection(self.wait_request())
            stop = self.connection({"op": "stop"}); join = self.connection({"op": "wait"})
            receipt = self.run_operations(Path(temporary), "application-valid", [start, wait, stop, join])
            self.assertEqual(["ready", "application-start", "resource-wait", "stop", "wait"], receipt["operations"])
            self.assertFalse(receipt["blocked_operation_eof"])
            self.assertEqual(fixture.application_start_response("application-valid"), json.loads(start.sendall.call_args.args[0]))
            self.assertEqual(fixture.application_wait_response("application-valid"), json.loads(wait.sendall.call_args.args[0]))
            self.assertEqual({"ok": True, "owned_exit": True}, json.loads(join.sendall.call_args.args[0]))

    def test_closed_start_wire_has_only_cleanup_and_no_application_request(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            receipt = self.run_operations(Path(temporary), "application-closed-start",
                                          [self.connection({"op": "stop"}), self.connection({"op": "wait"})])
            self.assertEqual(["ready", "stop", "wait"], receipt["operations"])

    def test_both_blocked_ack_controls_require_eof_then_separate_stop_wait(self):
        for operation in ("start", "wait"):
            with self.subTest(operation=operation), tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
                start = self.connection(self.start_request()); wait = self.connection(self.wait_request())
                blocked = start if operation == "start" else wait
                connections = [start] if operation == "start" else [start, wait]
                connections += [self.connection({"op": "stop"}), self.connection({"op": "wait"})]
                receipt = self.run_operations(Path(temporary), "application-cancel-" + operation, connections)
                blocked.sendall.assert_not_called()
                self.assertTrue(receipt["blocked_operation_eof"])
                self.assertEqual(["stop", "wait"], receipt["operations"][-2:])
                self.assertTrue((Path(temporary) / "broker" / "control.sock.operation-seen").is_file())

    def test_changed_peer_and_invalid_request_deny_before_response_or_receipt(self):
        cases = [(self.start_request(), (12346, fixture.WORKER_UID, fixture.WORKER_GID), "application-operation-peer-changed"),
                 ({**self.start_request(), "entry_digest": "protocol-canary"}, self.peer, "application-start-request-mismatch"),
                 ({**self.wait_request(), "lease_id": "protocol-canary"}, self.peer, "application-wait-request-mismatch"),
                 ({"op": "stop", "argv": ["protocol-canary"]}, self.peer, "application-unknown-operation")]
        for request, peer, diagnostic in cases:
            with self.subTest(diagnostic=diagnostic), tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
                connection = self.connection(request, peer=peer)
                with self.assertRaisesRegex(fixture.HarnessFailure, "^" + diagnostic + "$"):
                    self.run_operations(Path(temporary), "application-valid", [connection])
                connection.sendall.assert_not_called()
                self.assertFalse((Path(temporary) / "protocol-operations.json").exists())

    def test_blocked_ack_with_extra_request_bytes_is_not_cancellation_eof(self):
        with tempfile.TemporaryDirectory(prefix="evctl-") as temporary:
            blocked = self.connection(self.start_request(), eof=b"x")
            with self.assertRaisesRegex(fixture.HarnessFailure, "^blocked-operation-received-extra-bytes$"):
                self.run_operations(Path(temporary), "application-cancel-start", [blocked])
            blocked.sendall.assert_not_called()
            self.assertFalse((Path(temporary) / "protocol-operations.json").exists())


if __name__ == "__main__":
    unittest.main()
