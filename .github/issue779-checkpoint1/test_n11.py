"""Detached stream metadata and real unprivileged file custody regressions.

No test invokes a native procedure, constructs a protected owner, or claims a
kernel/process observation. Archive members here are ordinary test data only.
"""

import copy
import importlib.util
import io
import os
import json
import shlex
import subprocess
import sys
from pathlib import Path
import stat
import tarfile
import tempfile
import time
import unittest
from unittest import mock


BRIDGE_PATH = Path(__file__).resolve().parent / "procedures/n11/bridge.py"
SPEC = importlib.util.spec_from_file_location("n11_retention_data", BRIDGE_PATH)
bridge = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bridge)


def command_rows():
    """Detached terminal metadata only, never evidence of an executed command."""
    return [{"ordinal": ordinal, "exit": 0, "waited": True, "group_absent": True,
             "forced_cleanup": False, "timed_out": False, "failure": None,
             "root_utility_cgroup_empty": True,
             "logs": [{"name": f"{ordinal:02d}-{stream}.log", "bytes": 0,
                       "sha256": bridge.sha(b"")} for stream in ("stdout", "stderr")]}
            for ordinal in range(2)]


def retain_commands(output, rows):
    directory = output / "commands"
    directory.mkdir(mode=0o700)
    for row in rows:
        (directory / f"command-{row['ordinal']:02d}.json").write_bytes(bridge.canonical(row))
        for log in row["logs"]:
            (directory / log["name"]).write_bytes(b"")


def archive_data():
    """A genuine USTAR byte stream containing only detached test members."""
    members = {"retention-selection.json": b"{}\n", "sample.log": b"detached data\n"}
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w:", format=tarfile.USTAR_FORMAT) as archive:
        for name, raw in sorted(members.items()):
            node = tarfile.TarInfo(name)
            node.size = len(raw)
            node.mode = 0o600
            node.uid = node.gid = node.mtime = 0
            archive.addfile(node, io.BytesIO(raw))
    return stream.getvalue(), members


class NonRootPrerequisiteEnvironmentTests(unittest.TestCase):
    def test_both_actual_prerequisites_use_the_fixed_runtime_environment(self):
        import binding
        root = Path(__file__).resolve().parent
        scripts = (root / "procedures/n11/checkpoint-n11.sh",
                   root / "procedures/n12/checkpoint-n12-data-integration.sh")
        with tempfile.TemporaryDirectory() as directory:
            probe = Path(directory) / "probe.py"
            probe.write_text("import json,os,sys;print(json.dumps({'environment':dict(os.environ),'arguments':sys.argv[1:]}))")
            for script in scripts:
                with self.subTest(script=script.name):
                    lines = [line for line in script.read_text().splitlines()
                             if "strace -f" in line and '>"$log/n02.stdout"' in line]
                    self.assertEqual(1, len(lines))
                    self.assertIn('setpriv --reuid="$n02_uid" --regid="$n02_gid" --clear-groups', lines[0])
                    invocation = lines[0].split('/usr/bin/env -i ', 1)[1].split(') >"$log/n02.stdout"', 1)[0]
                    tokens = shlex.split(invocation)
                    host_index = tokens.index('$host')
                    self.assertEqual(list(binding.FIXED_ENVIRONMENT), tokens[:host_index])
                    self.assertEqual(['$host', '$managed', 'evidence', 'supervise', '--request', '$request'], tokens[host_index:])
                    command = ['/usr/bin/env', '-i', *tokens[:host_index], sys.executable,
                               str(probe), *tokens[host_index + 2:-1], 'unopened-fixture-request']
                    result = subprocess.run(command, capture_output=True, text=True, timeout=5,
                                            env={**os.environ, 'UNSELECTED_CANARY': 'must-not-survive'})
                    self.assertEqual(0, result.returncode, result.stderr)
                    value = json.loads(result.stdout)
                    expected = dict(item.split('=', 1) for item in binding.FIXED_ENVIRONMENT)
                    self.assertEqual(expected, {key: value['environment'].get(key) for key in expected})
                    self.assertNotIn('UNSELECTED_CANARY', value['environment'])
                    self.assertEqual(['evidence', 'supervise', '--request', 'unopened-fixture-request'], value['arguments'])


class N11RetentionDataTests(unittest.TestCase):
    def test_both_empty_streams_are_valid_data(self):
        rows = command_rows()
        self.assertIsNone(bridge.validate_commands(rows))
        rows[0]["logs"].reverse()
        self.assertIsNone(bridge.validate_commands(rows))

    def test_missing_duplicate_and_foreign_streams_are_rejected(self):
        good = command_rows()
        bridge.validate_commands(good)
        for logs in ([], good[0]["logs"][:1], [good[0]["logs"][0]] * 2,
                     [*good[0]["logs"], good[0]["logs"][0]],
                     [{**good[0]["logs"][0], "name": "01-stdout.log"}, good[0]["logs"][1]]):
            with self.subTest(logs=logs):
                bad = copy.deepcopy(good)
                bad[0]["logs"] = logs
                with self.assertRaises(ValueError):
                    bridge.validate_commands(bad)

    def test_stream_field_types_bounds_and_hash_are_rejected(self):
        for field, value in (("bytes", True), ("bytes", -1), ("bytes", bridge.MAX_JSON + 1),
                             ("sha256", "f" * 63), ("sha256", "F" * 64),
                             ("sha256", None), ("name", "../../stdout.log"), ("name", 0)):
            with self.subTest(field=field, value=value):
                rows = command_rows()
                bridge.validate_commands(rows)
                rows[0]["logs"][0][field] = value
                with self.assertRaises(ValueError):
                    bridge.validate_commands(rows)
        rows = command_rows()
        rows[0]["logs"][0]["unexpected"] = False
        with self.assertRaises(ValueError):
            bridge.validate_commands(rows)

    def test_bad_terminal_and_boolean_numeric_fields_are_rejected(self):
        for field, value in (("ordinal", True), ("ordinal", 1), ("exit", False), ("exit", 1),
                             ("waited", False), ("group_absent", False), ("forced_cleanup", True),
                             ("timed_out", True), ("failure", "Io"), ("root_utility_cgroup_empty", False)):
            with self.subTest(field=field):
                rows = command_rows()
                rows[0][field] = value
                with self.assertRaises(ValueError):
                    bridge.validate_commands(rows)

    def test_actual_empty_logs_must_exist_on_reconciliation(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            rows = command_rows()
            retain_commands(output, rows)
            bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)
            (output / "commands/01-stderr.log").unlink()
            with self.assertRaises(FileNotFoundError):
                bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)

    def test_actual_changed_stream_or_command_row_rejects(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            rows = command_rows()
            retain_commands(output, rows)
            bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)
            path = output / "commands/00-stdout.log"
            path.write_bytes(b"changed")
            with self.assertRaises(ValueError):
                bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)
            path.write_bytes(b"")
            changed = copy.deepcopy(rows[0])
            changed["waited"] = False
            (output / "commands/command-00.json").write_bytes(bridge.canonical(changed))
            with self.assertRaises(ValueError):
                bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)

    def test_actual_symlink_and_hardlink_streams_reject(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            rows = command_rows()
            retain_commands(output, rows)
            path = output / "commands/00-stdout.log"
            path.unlink()
            sentinel = output / "sentinel"
            sentinel.write_bytes(b"")
            path.symlink_to(sentinel)
            with self.assertRaises(ValueError):
                bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)
            path.unlink()
            os.link(sentinel, path)
            with self.assertRaises(ValueError):
                bridge.verify_commands(output, rows, deadline=time.monotonic() + 5)
            self.assertEqual(b"", sentinel.read_bytes())

    def test_reconciliation_does_not_renew_expired_deadline(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            rows = command_rows()
            retain_commands(output, rows)
            with self.assertRaisesRegex(ValueError, "original-deadline"):
                bridge.verify_commands(output, rows, deadline=time.monotonic() - 1)

    def test_archive_above_leaf_cap_is_retained_byte_exact(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            output.chmod(0o700)
            raw = b"original detached archive data\0" * 300000
            self.assertGreater(len(raw), bridge.MAX_JSON)
            path = output / "native-private.tar"
            end = time.monotonic() + 5
            bridge.save_archive(path, raw, deadline=end)
            self.assertEqual(raw, bridge.read(path, bridge.MAX_ARCHIVE, deadline=end))
            facts = path.lstat()
            self.assertTrue(stat.S_ISREG(facts.st_mode))
            self.assertEqual((os.geteuid(), os.getegid(), 0o600, 1),
                             (facts.st_uid, facts.st_gid, stat.S_IMODE(facts.st_mode), facts.st_nlink))

    def test_archive_write_is_exclusive_and_symlink_preserves_sentinel(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            output.chmod(0o700)
            path = output / "native-private.tar"
            path.write_bytes(b"original")
            with self.assertRaises(FileExistsError):
                bridge.save_archive(path, b"replacement", deadline=time.monotonic() + 5)
            self.assertEqual(b"original", path.read_bytes())
            path.unlink()
            sentinel = output / "sentinel"
            sentinel.write_bytes(b"preserved")
            path.symlink_to(sentinel)
            with self.assertRaises(FileExistsError):
                bridge.save_archive(path, b"replacement", deadline=time.monotonic() + 5)
            self.assertEqual(b"preserved", sentinel.read_bytes())

    def test_archive_size_deadline_and_private_parent_guards(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory).resolve()
            output.chmod(0o700)
            path = output / "native-private.tar"
            for raw in (b"", b"x" * (bridge.MAX_ARCHIVE + 1)):
                with self.subTest(length=len(raw)), self.assertRaises(ValueError):
                    bridge.save_archive(path, raw, deadline=time.monotonic() + 5)
                self.assertFalse(path.exists())
            with self.assertRaisesRegex(ValueError, "original-deadline"):
                bridge.save_archive(path, b"x", deadline=time.monotonic() - 1)
            self.assertFalse(path.exists())
            output.chmod(0o755)
            with self.assertRaises(ValueError):
                bridge.save_archive(path, b"x", deadline=time.monotonic() + 5)
            self.assertFalse(path.exists())

    def test_unpacked_members_do_not_reconstruct_original_archive(self):
        raw, members = archive_data()
        self.assertEqual(members, bridge.unpack(raw, set(members)))
        # Extra zero record padding is a valid representation of the same USTAR
        # members. Custody binds the actual bytes, not a Python reserialization.
        different_bytes = raw + b"\0" * 10240
        self.assertEqual(members, bridge.unpack(different_bytes, set(members)))
        self.assertNotEqual(bridge.sha(raw), bridge.sha(different_bytes))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory).resolve() / "native-private.tar"
            bridge.save_archive(path, different_bytes, deadline=time.monotonic() + 5)
            self.assertEqual(different_bytes, path.read_bytes())

    def test_actual_packet_and_aggregate_forward_original_archive_before_sentinel_rejection(self):
        """Exercise real retention wiring; the fixed record sentinel NEVER returns success."""
        canonical_archive, members = archive_data()
        original_archive = canonical_archive + b"\0" * 10240
        self.assertNotEqual(bridge.sha(canonical_archive), bridge.sha(original_archive))
        sentinel_source = b'''ARCHIVE_NAMES = {"retention-selection.json", "sample.log"}
def validate(raw, **expected):
    error = ValueError("record-data-sentinel-no-native-pass")
    error.archive_raw = raw
    error.expected = expected
    raise error
'''
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            source_root, output = root / "source", root / "output"
            current_c1 = source_root / ".github/issue779-checkpoint1"
            records_path = current_c1 / "procedures/n12/records.py"
            records_path.parent.mkdir(parents=True)
            records_path.write_bytes(sentinel_source)
            output.mkdir(mode=0o700)
            capture = {"source": "a" * 40, "files": {
                records_path.relative_to(source_root).as_posix(): {"sha256": bridge.sha(sentinel_source)},
                ".github/issue779-checkpoint1/procedures/n12/checkpoint-n12-data-integration.sh":
                    {"sha256": "b" * 64}}}
            capture_raw = bridge.canonical(capture)
            build_raw = bridge.canonical({"source_root": str(source_root), "tool": {"files": {
                "ForgeTrust.AppSurface.Cli.dll": {"sha256": "c" * 64}}}})
            capture_path, build_path = root / "capture.json", root / "build.json"
            capture_path.write_bytes(capture_raw)
            build_path.write_bytes(build_raw)
            context = {"source": "a" * 40, "selector": "N12", "capture_path": str(capture_path),
                "capture_sha256": bridge.sha(capture_raw), "build_receipt_path": str(build_path),
                "build_receipt_sha256": bridge.sha(build_raw), "tool_root": str(root / "unused-tool"),
                "runtime_root": str(root / "unused-runtime"), "output": str(output), "generation": "d" * 32,
                "binding_validator": str(current_c1 / "binding.py"), "binding_validator_sha256": "e" * 64,
                "workflow_identity": "github-actions-1-1"}
            rows = command_rows()
            retain_commands(output, rows)
            retained = output / "retained"
            retained.mkdir(mode=0o700)
            for name, raw in members.items():
                (retained / name).write_bytes(raw)
            packet = {"schema": "issue779-checkpoint1-native-family-v1", "source": context["source"],
                "selector": "N12", "input_generation": context["generation"],
                "source_capture_sha256": context["capture_sha256"],
                "build_receipt_sha256": context["build_receipt_sha256"], "base_revision": "f" * 40,
                "procedure_sha256": "b" * 64, "archive_sha256": bridge.sha(original_archive),
                "files": {name: {"bytes": len(raw), "sha256": bridge.sha(raw)} for name, raw in members.items()},
                "commands": rows}
            evidence_path = output / "N12-evidence.json"
            evidence_path.write_bytes(bridge.canonical(packet))
            archive_path = output / "native-private.tar"
            bridge.save_archive(archive_path, original_archive, deadline=time.monotonic() + 5)
            actual_archive_reads = []
            original_read = bridge.read

            def read_actual_file(path, cap, *, deadline):
                raw = original_read(path, cap, deadline=deadline)
                if Path(path) == archive_path:
                    actual_archive_reads.append(raw)
                return raw

            # Change only the source location. Real load/hash, archive read,
            # member projection and packet/aggregate validation all execute.
            with mock.patch.object(bridge, "C1", current_c1):
                with self.assertRaisesRegex(ValueError, "record-data-sentinel-no-native-pass") as direct:
                    bridge.validate_packet(context, packet, capture_raw, build_raw, members,
                        archive_raw=original_archive, deadline=time.monotonic() + 5)
                self.assertIs(original_archive, direct.exception.archive_raw)
                with mock.patch.object(bridge, "read", side_effect=read_actual_file):
                    with self.assertRaisesRegex(ValueError, "record-data-sentinel-no-native-pass") as aggregate:
                        bridge.verify_packet(context, deadline=time.monotonic() + 5)
                self.assertEqual(1, len(actual_archive_reads))
                self.assertIs(actual_archive_reads[0], aggregate.exception.archive_raw)
                self.assertEqual(original_archive, aggregate.exception.archive_raw)
                self.assertEqual(packet["archive_sha256"], bridge.sha(aggregate.exception.archive_raw))
                self.assertEqual({"source": context["source"], "base": "f" * 40, "entry_sha": "c" * 64},
                                 aggregate.exception.expected)
                # Same members, different original bytes: digest rejection must
                # happen before the records sentinel can be reached.
                archive_path.write_bytes(canonical_archive)
                with self.assertRaisesRegex(ValueError, "packet-archive$"):
                    bridge.verify_packet(context, deadline=time.monotonic() + 5)
                archive_path.write_bytes(original_archive)
                packet["archive_sha256"] = bridge.sha(canonical_archive)
                evidence_path.write_bytes(bridge.canonical(packet))
                with self.assertRaisesRegex(ValueError, "packet-archive$"):
                    bridge.verify_packet(context, deadline=time.monotonic() + 5)
                packet["archive_sha256"] = bridge.sha(original_archive)
                evidence_path.write_bytes(bridge.canonical(packet))
                archive_path.unlink()
                with self.assertRaises(FileNotFoundError):
                    bridge.verify_packet(context, deadline=time.monotonic() + 5)


if __name__ == "__main__":
    unittest.main()
