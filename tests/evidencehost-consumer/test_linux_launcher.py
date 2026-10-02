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
from pathlib import Path
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


class LauncherValidationTests(unittest.TestCase):
    def test_trusted_allowlist_rejection_has_its_exact_safe_diagnostic(self):
        with patch.object(launcher, "parser") as parser, \
                patch.object(launcher, "launch", side_effect=launcher.LauncherError("trusted-proof-not-allowlisted")), \
                patch.object(launcher.sys, "stderr", io.StringIO()) as stderr:
            self.assertEqual(launcher.main([]), 1)
            self.assertEqual(json.loads(stderr.getvalue()), {"status": "failed", "diagnostic": "ASEVD407"})

    def test_other_launcher_failures_never_echo_exception_canaries(self):
        for error in (launcher.LauncherError("secret-779"), OSError("secret-779"), ValueError("secret-779")):
            with patch.object(launcher, "parser"), patch.object(launcher, "launch", side_effect=error), \
                    patch.object(launcher.sys, "stderr", io.StringIO()) as stderr:
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

                def wait(self, timeout=None):
                    del timeout

            process = FakeProcess()
            try:
                with patch.object(launcher, "openat2", side_effect=portable_openat2), \
                     patch.object(launcher.subprocess, "Popen", return_value=process), \
                     patch.object(broker, "_unit_properties", return_value={
                         "User": str(os.getuid()), "KillMode": "control-group", "ControlGroup": "/system.slice/test.service",
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
                self.assertTrue(result["output_truncated"])
                self.assertEqual(len(result["stdout"].encode()), launcher.MAX_PREFIX)
                self.assertEqual(len(result["stderr"].encode()), launcher.MAX_PREFIX)
            finally:
                broker.close_artifact_handles()


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


if __name__ == "__main__": unittest.main()
