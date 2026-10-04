"""Portable SDK filesystem/data procedures; no native execution or admission.

The ELF64 fixture is a header marker, never a usable or executed SDK host. All
paths and FDs belong to temporary directories owned by this process. Root/NSS
bootstrap and command paths are forbidden. Sealing records every requested
root ownership change without performing it; only the matching observed inode's
UID/GID are simulated for postcondition checks. Real reads and fchmod exercise
the procedure. This does not establish root origin or a trusted installation.
"""
from contextlib import contextmanager, nullcontext, redirect_stderr, redirect_stdout
import errno
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import sys
import tempfile
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "private_trusted_sdk_data", Path(__file__).with_name("trusted_sdk.py"))
sdk = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(sdk)


class TrustedSdkProcedureControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="trusted-sdk-data-")
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name).resolve()
        self.guards = []
        for owner, name in ((sdk, "seal_trusted_sdk"), (sdk.pwd, "getpwnam"),
                            (sdk.shutil, "which"), (os, "chown"), (os, "fchown"),
                            (subprocess, "Popen"), (subprocess, "check_output")):
            guard = patch.object(owner, name, side_effect=AssertionError("root-or-command-path-forbidden"))
            self.guards.append(guard.start())
            self.addCleanup(guard.stop)

    def tearDown(self):
        for guard in self.guards:
            guard.assert_not_called()

    @staticmethod
    def header():
        # ELF64 little-endian, ET_DYN, EM_X86_64. Remaining zero fields make this
        # only a metadata fixture, not a runnable program.
        result = bytearray(64)
        result[:7] = b"\x7fELF\x02\x01\x01"
        struct.pack_into("<HHI", result, 16, 3, 62, 1)
        return bytes(result)

    def tree(self, label, writable=False):
        root = self.base / label
        root.mkdir()
        contents = {"dotnet": self.header(), "host/fxr/version/hostfxr.bin": b"loader-data\x00\xff",
                    "shared/framework/version/runtime.bin": b"runtime-data",
                    "sdk/version/sdk.bin": b"sdk-data", "sdk/version/empty.bin": b""}
        for relative, data in contents.items():
            path = root / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            path.chmod((0o775 if writable else 0o755) if relative == "dotnet"
                       else (0o664 if writable else 0o644))
        for path in [root, *[p for p in root.rglob("*") if p.is_dir()]]:
            path.chmod(0o775 if writable else 0o755)
        return root, contents

    @contextmanager
    def directory(self, path):
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
        try:
            yield fd
        finally:
            os.close(fd)

    def inventory(self, fd, deadline=None):
        return sdk.inventory(fd, os.geteuid(), os.getegid(),
                             time.monotonic() + 30 if deadline is None else deadline)

    @staticmethod
    def changed_stat(info, **changes):
        fields = {name: getattr(info, name) for name in dir(info) if name.startswith("st_")}
        fields.update(changes)
        return SimpleNamespace(**fields)

    def reject(self, action):
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console), self.assertRaises(sdk.TrustedSdkFailure) as caught:
            action()
        self.assertEqual("trusted-sdk-bootstrap-rejected", str(caught.exception))
        self.assertEqual("", console.getvalue())

    @contextmanager
    def recorded_seal(self, before):
        """Simulate ownership only on audited real inodes; never call real chown."""
        real_stat, real_fstat, real_fchmod = os.stat, os.fstat, os.fchmod
        approved = {(r["metadata"]["device"], r["metadata"]["inode"]) for r in before.values()}
        adopted = set()

        def observed(info):
            key = (info.st_dev, info.st_ino)
            return self.changed_stat(info, st_uid=0, st_gid=0) if key in adopted else info

        def record_chown(fd, uid, gid):
            info = real_fstat(fd)
            key = (info.st_dev, info.st_ino)
            self.assertIn(key, approved)
            self.assertEqual((0, 0), (uid, gid))
            adopted.add(key)

        def actual_chmod(fd, mode):
            info = real_fstat(fd)
            self.assertIn((info.st_dev, info.st_ino), approved)
            real_fchmod(fd, mode)

        with patch.object(os, "fchown", side_effect=record_chown) as chown, \
                patch.object(os, "fchmod", side_effect=actual_chmod) as chmod, \
                patch.object(os, "fstat", side_effect=lambda fd: observed(real_fstat(fd))), \
                patch.object(os, "stat", side_effect=lambda *a, **kw: observed(real_stat(*a, **kw))):
            yield chown, chmod

    def test_inventory_records_actual_header_bytes_empty_file_and_directory_metadata_without_mutation(self):
        root, contents = self.tree("inventory")
        initial = {"" if p == root else p.relative_to(root).as_posix(): p.stat()
                   for p in [root, *root.rglob("*")]}
        with self.directory(root) as fd:
            rows, total = self.inventory(fd)
            self.assertEqual(total, sum(map(len, contents.values())))
            self.assertTrue(all(rows[n]["directory"] for n in ("", "host", "host/fxr", "shared", "sdk")))
            for name, data in contents.items():
                self.assertFalse(rows[name]["directory"])
                self.assertEqual(hashlib.sha256(data).hexdigest(), rows[name]["sha256"])
                self.assertEqual(sdk.metadata(initial[name]), rows[name]["metadata"])
                self.assertEqual(1, rows[name]["metadata"]["links"])
            self.assertEqual(sdk.metadata(initial[""]), rows[""]["metadata"])
        self.assertEqual(contents, {n: (root / n).read_bytes() for n in contents})
        for name, info in initial.items():
            self.assertEqual(sdk.identity(info), sdk.identity((root / name).stat()))

    def test_seal_records_root_calls_once_per_actual_inode_preserves_bytes_and_only_removes_write_bits(self):
        for writable in (False, True):
            with self.subTest(writable=writable):
                root, contents = self.tree("seal-" + str(writable), writable=writable)
                with self.directory(root) as fd:
                    before, total = self.inventory(fd)
                    with self.recorded_seal(before) as (chown, chmod):
                        sdk.seal_inventory(fd, before, time.monotonic() + 30)
                        after, final_total = sdk.inventory(fd, 0, 0, time.monotonic() + 30)
                        self.assertEqual(len(before), chown.call_count)
                        self.assertEqual(len(before), chmod.call_count)
                        self.assertEqual(set(before), set(after))
                        self.assertEqual(total, final_total)
                        for name, row in before.items():
                            expected = dict(row["metadata"], uid=0, gid=0,
                                            mode=format(int(row["metadata"]["mode"], 8) & ~0o022, "04o"))
                            self.assertEqual(expected, after[name]["metadata"])
                            self.assertEqual(row.get("sha256"), after[name].get("sha256"))
                for name, data in contents.items():
                    self.assertEqual(data, (root / name).read_bytes())
                    self.assertEqual(os.geteuid(), (root / name).stat().st_uid)
                    self.assertEqual(os.getegid(), (root / name).stat().st_gid)

    def test_inventory_rejects_real_symlink_hardlink_and_fifo_before_any_privileged_call(self):
        for kind in ("symlink", "hardlink", "fifo"):
            with self.subTest(kind=kind):
                root, _ = self.tree(kind)
                extra = root / "sdk/version/unsafe"
                if kind == "symlink":
                    extra.symlink_to(root / "dotnet")
                elif kind == "hardlink":
                    os.link(root / "dotnet", extra)
                    self.assertEqual(2, (root / "dotnet").stat().st_nlink)
                else:
                    os.mkfifo(extra, 0o644)
                with self.directory(root) as fd:
                    self.reject(lambda: self.inventory(fd))

    def test_host_nonexecutable_or_invalid_elf_metadata_rejects_without_command_execution(self):
        controls = [("nonexec", self.header(), 0o644), ("invalid-magic", b"private-canary" * 8, 0o755)]
        for label, offset, value in (("elf32", 4, 1), ("big-endian", 5, 2),
                                     ("relocatable", 16, 1), ("wrong-machine", 18, 183)):
            data = bytearray(self.header())
            data[offset] = value
            controls.append((label, bytes(data), 0o755))
        for label, data, mode in controls:
            with self.subTest(label=label):
                root, _ = self.tree(label)
                host = root / "dotnet"
                host.write_bytes(data)
                host.chmod(mode)
                with self.directory(root) as fd:
                    self.reject(lambda: self.inventory(fd))

    def test_unapproved_observed_uid_or_gid_rejects_before_file_open_or_mutation(self):
        for field, value in (("st_uid", os.geteuid() + 100_000), ("st_gid", os.getegid() + 100_000)):
            with self.subTest(field=field):
                root, _ = self.tree(field)
                real_stat, real_open = os.stat, os.open
                def observed(path, *a, **kw):
                    info = real_stat(path, *a, **kw)
                    return self.changed_stat(info, **{field: value}) if path == "dotnet" else info
                with self.directory(root) as fd, patch.object(os, "stat", side_effect=observed), \
                        patch.object(os, "open", wraps=real_open) as opened:
                    self.reject(lambda: self.inventory(fd))
                    self.assertFalse(any(call.args[0] == "dotnet" for call in opened.call_args_list))

    def test_numeric_tree_bounds_and_actual_read_error_do_not_mutate_or_echo(self):
        for name, value in (("MAX_FILE_BYTES", 4), ("MAX_TOTAL_BYTES", 4),
                            ("MAX_NODES", 2), ("MAX_DEPTH", 0)):
            with self.subTest(bound=name):
                root, _ = self.tree(name)
                with self.directory(root) as fd, patch.object(sdk, name, value):
                    self.reject(lambda: self.inventory(fd))
        root, _ = self.tree("read-error")
        fault = OSError(errno.EIO, "private-read-canary")
        console = io.StringIO()
        with self.directory(root) as fd, patch.object(os, "read", side_effect=fault), \
                redirect_stdout(console), redirect_stderr(console), self.assertRaises(OSError) as caught:
            self.inventory(fd)
        self.assertIs(fault, caught.exception)
        self.assertEqual("", console.getvalue())

    def test_postaudit_changes_reject_without_adopting_changed_or_unapproved_inodes(self):
        # A post-audit race may leave an already verified prefix sealed. It must
        # fail preparation, preserve current bytes and never adopt a changed inode.
        for kind in ("size", "bytes", "extra-node", "replacement"):
            with self.subTest(kind=kind):
                root, _ = self.tree("post-audit-" + kind)
                with self.directory(root) as fd:
                    before, _ = self.inventory(fd)
                    target = root / "sdk/version/sdk.bin"
                    if kind == "size":
                        target.write_bytes(target.read_bytes() + b"growth")
                    elif kind == "bytes":
                        target.write_bytes(b"X" * target.stat().st_size)
                    elif kind == "extra-node":
                        extra = root / "host/fxr/extra.bin"
                        extra.write_bytes(b"unexpected")
                        extra.chmod(0o644)
                    else:
                        old_inode = target.stat().st_ino
                        replacement = root / "sdk/version/replacement.bin"
                        replacement.write_bytes(target.read_bytes())
                        replacement.chmod(0o644)
                        os.replace(replacement, target)
                        self.assertNotEqual(old_inode, target.stat().st_ino)
                    current_bytes = {p.relative_to(root).as_posix(): p.read_bytes()
                                     for p in root.rglob("*") if p.is_file()}
                    blocked = target.stat() if kind != "extra-node" else extra.stat()
                    with self.recorded_seal(before) as (chown, chmod):
                        real_fstat = os.fstat
                        changed_inode = (blocked.st_dev, blocked.st_ino)
                        adopted_inodes = []
                        record_chown = chown.side_effect
                        def observe_adoption(selected, uid, gid):
                            info = real_fstat(selected)
                            adopted_inodes.append((info.st_dev, info.st_ino))
                            record_chown(selected, uid, gid)
                        chown.side_effect = observe_adoption
                        self.reject(lambda: sdk.seal_inventory(fd, before, time.monotonic() + 30))
                        self.assertNotIn(changed_inode, adopted_inodes)
                    self.assertEqual(current_bytes, {p.relative_to(root).as_posix(): p.read_bytes()
                                                    for p in root.rglob("*") if p.is_file()})

    def test_expired_deadline_rejects_inventory_and_seal_before_first_privileged_call(self):
        root, _ = self.tree("deadline")
        with self.directory(root) as fd:
            before, _ = self.inventory(fd)
            expired = time.monotonic() - 1
            self.reject(lambda: self.inventory(fd, expired))
            with self.recorded_seal(before) as (chown, chmod):
                self.reject(lambda: sdk.seal_inventory(fd, before, expired))
                chown.assert_not_called()
                chmod.assert_not_called()

    def test_digest_rejects_short_read_and_actual_growth_through_retained_file_descriptor(self):
        for kind in ("short-read", "growth"):
            with self.subTest(kind=kind):
                root, _ = self.tree("digest-" + kind)
                path = root / "sdk/version/sdk.bin"
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
                self.addCleanup(os.close, fd)
                real_read = os.read
                changed = False
                def read(selected, count):
                    nonlocal changed
                    self.assertEqual(fd, selected)
                    if kind == "short-read":
                        return b""
                    block = real_read(selected, count)
                    if not changed:
                        with path.open("ab") as stream:
                            stream.write(b"growth")
                        changed = True
                    return block
                with patch.object(os, "read", side_effect=read):
                    self.reject(lambda: sdk.digest_fd(fd, time.monotonic() + 30))

    def test_fanout_enumeration_stops_at_first_excess_and_sealing_extra_entry_precedes_file_chowns(self):
        class CountedEntries:
            def __init__(self, entries):
                self.entries = entries
                self.yielded = 0
                self.closed = False

            def __enter__(self):
                return self

            def __exit__(self, *exc):
                self.entries.close()
                self.closed = True

            def __iter__(self):
                return self

            def __next__(self):
                entry = next(self.entries)
                self.yielded += 1
                return entry

        real_scandir = os.scandir
        with self.subTest(control="inventory-fanout"):
            root, _ = self.tree("bounded-fanout")
            for index in range(16):
                extra = root / ("extra-" + str(index))
                extra.write_bytes(b"small-owned-data")
                extra.chmod(0o644)
            scans = []
            with self.directory(root) as fd:
                def counted(directory):
                    self.assertEqual(fd, directory)
                    entries = CountedEntries(real_scandir(directory))
                    scans.append(entries)
                    return entries
                with patch.object(sdk, "MAX_NODES", 4), \
                        patch.object(os, "scandir", side_effect=counted), \
                        patch.object(os, "fchown", side_effect=AssertionError("must-not-adopt-fanout")) as chown:
                    self.reject(lambda: self.inventory(fd))
                    chown.assert_not_called()
                self.assertEqual(1, len(scans))
                # The root row consumes one node: allowance 3, stop at entry 4.
                self.assertEqual(4, scans[0].yielded)
                self.assertTrue(scans[0].closed)

        with self.subTest(control="sealing-added-entry"):
            root, _ = self.tree("bounded-seal")
            with self.directory(root) as fd:
                before, _ = self.inventory(fd)
                expected_count = sum(bool(name) and "/" not in name for name in before)
                scans = []
                def added_entry(directory):
                    self.assertEqual(fd, directory)
                    # Add a real file after the initial root metadata check so
                    # enumeration, rather than an incidental size check, rejects.
                    extra = root / "late-entry.bin"
                    extra.write_bytes(b"late-owned-data")
                    extra.chmod(0o644)
                    entries = CountedEntries(real_scandir(directory))
                    scans.append(entries)
                    return entries
                with self.recorded_seal(before) as (chown, chmod), \
                        patch.object(os, "scandir", side_effect=added_entry):
                    self.reject(lambda: sdk.seal_inventory(fd, before, time.monotonic() + 30))
                    # No file or descendant-directory ownership operation was
                    # reached. A fully read-only preflight may also leave the
                    # retained root untouched.
                    self.assertLessEqual(chown.call_count, 1)
                    self.assertLessEqual(chmod.call_count, 1)
                    for call in [*chown.call_args_list, *chmod.call_args_list]:
                        self.assertEqual(fd, call.args[0])
                self.assertEqual(1, len(scans))
                self.assertEqual(expected_count + 1, scans[0].yielded)
                self.assertTrue(scans[0].closed)


    def test_sdk_diagnostic_actual_nonexec_host_records_failed_phase_and_numeric_stat_before_adoption(self):
        root, _ = self.tree("diagnostic-host")
        host = root / "dotnet"
        host.chmod(0o644)
        info = host.lstat()
        diagnostic = sdk.SdkDiagnostic()
        with self.directory(root) as fd:
            self.reject(lambda: sdk.inventory(fd, os.geteuid(), os.getegid(), time.monotonic() + 30,
                                              diagnostic=diagnostic))
        frame = diagnostic.snapshot()
        self.assertEqual(("inventory-type", "host", "sdk"),
                         (frame["phase"], frame["role"], frame["error_class"]))
        self.assertEqual({"uid": info.st_uid, "gid": info.st_gid, "mode": 0o644,
                          "links": info.st_nlink, "device": info.st_dev, "inode": info.st_ino,
                          "length": info.st_size, "kind": "regular"}, frame["node"])
        self.assertIsNone(frame["runner_uid"])
        self.assertIsNone(frame["fixed_path_match"])

    def test_sdk_diagnostic_actual_symlink_projects_numeric_type_failure_without_name_or_canary(self):
        root, _ = self.tree("diagnostic-link")
        link = root / "sdk/version/private-node-canary"
        link.symlink_to(root / "dotnet")
        observed = link.lstat()
        diagnostic = sdk.SdkDiagnostic()
        with self.directory(root) as fd:
            self.reject(lambda: sdk.inventory(fd, os.geteuid(), os.getegid(), time.monotonic() + 30,
                                              diagnostic=diagnostic))
        frame = diagnostic.snapshot()
        self.assertEqual(("inventory-type", "file", "sdk"),
                         (frame["phase"], frame["role"], frame["error_class"]))
        self.assertEqual(stat.S_IMODE(observed.st_mode), frame["node"]["mode"])
        self.assertEqual("symlink", frame["node"]["kind"])
        self.assertIn(frame["node"]["kind"], ("regular", "directory", "symlink", "other"))
        encoded = json.dumps(frame, sort_keys=True).encode()
        self.assertLessEqual(len(encoded), 4096)
        for forbidden in (b"private-node-canary", str(root).encode(), b"dotnet"):
            self.assertNotIn(forbidden, encoded)

    def test_sdk_diagnostic_first_failure_bounds_identity_parsing_and_snapshot_copy(self):
        root, _ = self.tree("diagnostic-data")
        diagnostic = sdk.SdkDiagnostic()
        runner = SimpleNamespace(pw_uid=os.geteuid(), pw_gid=os.getegid())
        diagnostic.identities(runner, str(os.geteuid()), "private-identity-canary")
        diagnostic.path_match("ambient_path_match", False)
        diagnostic.note("sudo-identity", "host", (root / "dotnet").lstat())
        original = sdk.TrustedSdkFailure("private-exception-canary")
        diagnostic.capture(original)
        first = diagnostic.snapshot()
        diagnostic.note("final-deadline", "file")
        diagnostic.capture(OSError(errno.EIO, "late-private-canary"))
        diagnostic.identities(runner, "4294967296", "-1")
        self.assertEqual(first, diagnostic.snapshot())
        self.assertEqual(os.geteuid(), first["sudo_uid"])
        self.assertIsNone(first["sudo_gid"])
        self.assertFalse(first["ambient_path_match"])
        self.assertIsNone(first["fixed_path_match"])
        first["node"]["uid"] = -1
        self.assertEqual(os.geteuid(), diagnostic.snapshot()["node"]["uid"])
        encoded = json.dumps(diagnostic.snapshot()).encode()
        self.assertNotIn(b"canary", encoded)
        self.assertLessEqual(len(encoded), 4096)
        invalid = sdk.SdkDiagnostic()
        invalid.identities(runner, "4294967296", "9" * 100)
        invalid.note([], [])
        self.assertIsNone(invalid.snapshot()["sudo_uid"])
        self.assertIsNone(invalid.snapshot()["sudo_gid"])
        self.assertEqual("unknown", invalid.snapshot()["phase"])

    def test_sdk_diagnostic_observer_failure_preserves_actual_guard_failure_and_no_privileged_call(self):
        root, _ = self.tree("diagnostic-observer-fault")
        (root / "dotnet").chmod(0o644)
        diagnostic = sdk.SdkDiagnostic()
        with self.directory(root) as fd, \
                patch.object(diagnostic, "note", side_effect=MemoryError("private-sink-canary")), \
                patch.object(diagnostic, "capture", side_effect=MemoryError("private-capture-canary")):
            self.reject(lambda: sdk.inventory(fd, os.geteuid(), os.getegid(), time.monotonic() + 30,
                                              diagnostic=diagnostic))
        self.assertIsNone(diagnostic.failure)


    def test_unexpired_hash_read_eio_retains_hash_phase_and_original_exception(self):
        for phase in ("inventory-hash", "sealing-hash"):
            with self.subTest(phase=phase):
                root, _ = self.tree("hash-attribution-" + phase)
                path = root / "sdk/version/sdk.bin"
                fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
                self.addCleanup(os.close, fd)
                diagnostic = sdk.SdkDiagnostic()
                diagnostic.note(phase, "file", os.fstat(fd))
                fault = OSError(errno.EIO, "private-read-attribution-canary")
                with patch.object(os, "read", side_effect=fault), self.assertRaises(OSError) as caught:
                    sdk.digest_fd(fd, time.monotonic() + 30, diagnostic=diagnostic)
                self.assertIs(fault, caught.exception)
                frame = diagnostic.snapshot()
                self.assertEqual((phase, "os"), (frame["phase"], frame["error_class"]))
                self.assertEqual(path.stat().st_ino, frame["node"]["inode"])
                self.assertNotIn("canary", json.dumps(frame))

    def test_sealing_enumeration_overflow_retains_sealing_count_phase(self):
        root, _ = self.tree("sealing-count-attribution")
        diagnostic = sdk.SdkDiagnostic()
        with self.directory(root) as fd:
            diagnostic.note("sealing-count", "sdk-root", os.fstat(fd))
            self.reject(lambda: sdk.bounded_names(fd, 1, time.monotonic() + 30,
                                                  diagnostic=diagnostic))
        frame = diagnostic.snapshot()
        self.assertEqual(("sealing-count", "sdk-root", "sdk"),
                         (frame["phase"], frame["role"], frame["error_class"]))

    def test_failure_binding_retention_owned_fd_seam_preserves_original_and_closes_all_handles(self):
        # Load only the preparation module to call its data-retention helper.
        # This seam changes observed UID/GID for owned test inodes; it cannot
        # invoke actual root bootstrap, NSS, commands or SDK system paths.
        spec = importlib.util.spec_from_file_location(
            "private_sdk_retention_data", Path(__file__).with_name("prepare.py"))
        preparation = importlib.util.module_from_spec(spec)
        with patch.object(sys, "path", [str(Path(__file__).parent), *sys.path]):
            spec.loader.exec_module(preparation)
        for kind in ("success", "oversized", "unsafe-mode", "replace-failure"):
            with self.subTest(kind=kind):
                workspace = self.base / ("retention-" + kind)
                workspace.mkdir(mode=0o700)
                workspace.chmod(0o700)
                source = workspace / "build-binding.json"
                original = {"source_commit": "b" * 40, "preparation_complete": False,
                            "sdk_bootstrap": {"host_before": {"uid": os.geteuid(), "mode": "0777"},
                                              "host_metadata_state": "observed"}}
                raw = json.dumps(original, sort_keys=True).encode() + b"\n"
                source.write_bytes(b"X" * 4097 if kind == "oversized" else raw)
                source.chmod(0o644 if kind == "unsafe-mode" else 0o600)
                before_bytes = source.read_bytes()
                before_inode = source.stat().st_ino
                diagnostic = preparation.SdkDiagnostic()
                diagnostic.note("inventory-mode", "host", source.lstat())
                original_failure = OSError(errno.EIO, "private-original-failure-canary")
                diagnostic.capture(original_failure)
                real_open, real_stat, real_fstat = os.open, os.stat, os.fstat
                allowed = {(workspace.stat().st_dev, workspace.stat().st_ino),
                           (source.stat().st_dev, before_inode)}
                opened = []

                def owned_info(info):
                    if (info.st_dev, info.st_ino) in allowed:
                        self.assertEqual(os.geteuid(), info.st_uid)
                        return self.changed_stat(info, st_uid=0, st_gid=0)
                    return info

                def owned_open(path, flags, *args, **kwargs):
                    self.assertTrue(path == workspace or path in
                                    ("build-binding.json", ".build-binding-sdk-diagnostic.tmp"))
                    selected = real_open(path, flags, *args, **kwargs)
                    opened.append(selected)
                    info = real_fstat(selected)
                    self.assertEqual(os.geteuid(), info.st_uid)
                    if path == ".build-binding-sdk-diagnostic.tmp":
                        allowed.add((info.st_dev, info.st_ino))
                    return selected

                replace_failure = patch.object(os, "replace", side_effect=OSError(errno.EIO, "private-replace-canary"))
                with patch.object(os, "open", side_effect=owned_open), \
                        patch.object(os, "fstat", side_effect=lambda fd: owned_info(real_fstat(fd))), \
                        patch.object(os, "stat", side_effect=lambda *a, **kw: owned_info(real_stat(*a, **kw))):
                    with replace_failure if kind == "replace-failure" else nullcontext():
                        with self.assertRaises(OSError) as caught:
                            try:
                                raise original_failure
                            except OSError:
                                retained = preparation.retain_sdk_failure_diagnostic(workspace, diagnostic)
                                self.assertEqual(kind == "success", retained)
                                raise
                self.assertIs(original_failure, caught.exception)
                for selected in opened:
                    with self.assertRaises(OSError) as closed:
                        real_fstat(selected)
                    self.assertEqual(errno.EBADF, closed.exception.errno)
                self.assertFalse((workspace / ".build-binding-sdk-diagnostic.tmp").exists())
                if kind == "success":
                    after = json.loads(source.read_bytes())
                    self.assertFalse(after["preparation_complete"])
                    self.assertEqual(original["sdk_bootstrap"]["host_before"], after["sdk_bootstrap"]["host_before"])
                    self.assertEqual(diagnostic.snapshot(), after["sdk_bootstrap"]["diagnostic"])
                    self.assertEqual(0o600, stat.S_IMODE(source.stat().st_mode))
                    self.assertLessEqual(source.stat().st_size, 4096)
                    self.assertNotIn(b"canary", source.read_bytes())
                else:
                    self.assertEqual(before_bytes, source.read_bytes())
                    self.assertEqual(before_inode, source.stat().st_ino)


    @contextmanager
    def root_share_observations(self, share_fd, transform=None):
        """Data-only root-stat projection on one current-owned temporary inode."""
        real_stat, real_fstat = os.stat, os.fstat
        key = (real_fstat(share_fd).st_dev, real_fstat(share_fd).st_ino)

        def project(info):
            if (info.st_dev, info.st_ino) == key:
                info = self.changed_stat(info, st_uid=0, st_gid=0)
                if transform is not None:
                    info = transform(info)
            return info

        with patch.object(os, "fstat", side_effect=lambda fd: project(real_fstat(fd))), \
                patch.object(os, "stat", side_effect=lambda *a, **kw: project(real_stat(*a, **kw))):
            yield

    def test_share_ancestor_retained_fd_only_clears_write_bits_and_preserves_actual_tree(self):
        for index, mode in enumerate((0o777, 0o755, 0o577)):
            with self.subTest(mode=oct(mode)):
                parent = self.base / ("ancestor-positive-" + str(index))
                parent.mkdir()
                share = parent / "share"
                share.mkdir()
                payload = share / "unchanged"
                payload.write_bytes(b"ancestor-private-bytes\x00\xff")
                share.chmod(mode)
                initial = share.stat()
                real_fchmod = os.fchmod
                with self.directory(parent) as parent_fd, self.directory(share) as share_fd, \
                        self.root_share_observations(share_fd), \
                        patch.object(os, "fchmod", wraps=real_fchmod) as chmod, \
                        patch.object(sdk, "inventory", side_effect=AssertionError("SDK-dispatch-forbidden")) as dispatch:
                    receipt = sdk.seal_share_ancestor(parent_fd, share_fd, time.monotonic() + 30)
                    self.assertEqual(int(bool(mode & 0o022)), chmod.call_count)
                    self.assertEqual(format(mode, "04o"), receipt["before"]["mode"])
                    self.assertEqual(format(mode & ~0o022, "04o"), receipt["after"]["mode"])
                    self.assertEqual(bool(mode & 0o022), receipt["write_bits_cleared"])
                    self.assertEqual((0, 0), (receipt["after"]["uid"], receipt["after"]["gid"]))
                    dispatch.assert_not_called()
                final = share.stat()
                self.assertEqual(mode & ~0o022, stat.S_IMODE(final.st_mode))
                self.assertEqual((initial.st_dev, initial.st_ino, initial.st_uid, initial.st_gid),
                                 (final.st_dev, final.st_ino, final.st_uid, final.st_gid))
                self.assertEqual(b"ancestor-private-bytes\x00\xff", payload.read_bytes())
                self.assertEqual(["unchanged"], sorted(p.name for p in share.iterdir()))

    def test_share_ancestor_unsafe_metadata_or_named_link_rejects_before_chmod(self):
        cases = ("uid", "gid", "regular", "symlink", "substitution", "link-count", "special", "missing-search")
        for kind in cases:
            with self.subTest(kind=kind):
                parent = self.base / ("ancestor-negative-" + kind)
                parent.mkdir()
                share = parent / "share"
                if kind == "regular":
                    share.write_bytes(b"not-a-directory")
                    fd = os.open(share, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
                else:
                    share.mkdir()
                    share.chmod(0o1777 if kind == "special" else 0o750 if kind == "missing-search" else 0o777)
                    fd = os.open(share, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
                self.addCleanup(os.close, fd)
                if kind == "symlink":
                    share.rename(parent / "retained")
                    share.symlink_to(parent / "retained", target_is_directory=True)
                transform = (lambda info: self.changed_stat(info, **{("st_uid" if kind == "uid" else "st_gid"): 100_000})) \
                    if kind in ("uid", "gid") else None
                with self.directory(parent) as parent_fd, self.root_share_observations(fd, transform), \
                        patch.object(os, "fchmod", side_effect=AssertionError("mutation-forbidden")) as chmod, \
                        patch.object(sdk, "inventory", side_effect=AssertionError("SDK-dispatch-forbidden")) as dispatch:
                    if kind in ("substitution", "link-count"):
                        projected_stat = os.stat
                        def changed_name(*args, **kwargs):
                            info = projected_stat(*args, **kwargs)
                            return self.changed_stat(info, **{"st_ino" if kind == "substitution" else "st_nlink":
                                info.st_ino + 1 if kind == "substitution" else info.st_nlink + 1})
                        seam = patch.object(os, "stat", side_effect=changed_name)
                    else:
                        seam = nullcontext()
                    with seam:
                        self.reject(lambda: sdk.seal_share_ancestor(parent_fd, fd, time.monotonic() + 30))
                    chmod.assert_not_called()
                    dispatch.assert_not_called()

    def test_share_ancestor_mutation_recheck_or_deadline_failure_aborts_without_sdk_dispatch(self):
        for kind in ("chmod-failure", "recheck", "expired", "late-deadline"):
            with self.subTest(kind=kind):
                parent = self.base / ("ancestor-failure-" + kind)
                parent.mkdir()
                share = parent / "share"
                share.mkdir()
                share.chmod(0o777)
                calls = []
                real_fchmod = os.fchmod
                original = OSError(errno.EIO, "ancestor-private-canary")
                diagnostic = sdk.SdkDiagnostic()
                def mutate(fd, mode):
                    calls.append((fd, mode))
                    if kind == "chmod-failure":
                        raise original
                    real_fchmod(fd, mode)
                def observed(info):
                    return self.changed_stat(info, st_mode=stat.S_IFDIR | 0o757) \
                        if kind == "recheck" and calls else info
                with self.directory(parent) as parent_fd, self.directory(share) as share_fd, \
                        self.root_share_observations(share_fd, observed), \
                        patch.object(os, "fchmod", side_effect=mutate), \
                        patch.object(sdk, "inventory", side_effect=AssertionError("SDK-dispatch-forbidden")) as dispatch:
                    deadline = time.monotonic() + 30
                    clock = patch.object(sdk.time, "monotonic", side_effect=[0.0, 0.0, 2.0]) \
                        if kind == "late-deadline" else nullcontext()
                    with clock:
                        if kind == "chmod-failure":
                            with self.assertRaises(OSError) as caught:
                                sdk.seal_share_ancestor(parent_fd, share_fd, deadline, diagnostic=diagnostic)
                            self.assertIs(original, caught.exception)
                            self.assertEqual("share-sealing", diagnostic.snapshot()["phase"])
                        else:
                            self.reject(lambda: sdk.seal_share_ancestor(parent_fd, share_fd,
                                1.0 if kind == "late-deadline" else time.monotonic() - 1 if kind == "expired" else deadline,
                                diagnostic=diagnostic))
                    dispatch.assert_not_called()
                    self.assertEqual(0 if kind == "expired" else 1, len(calls))
                    self.assertNotIn("ancestor-private-canary", json.dumps(diagnostic.snapshot()))


if __name__ == "__main__":
    unittest.main()
