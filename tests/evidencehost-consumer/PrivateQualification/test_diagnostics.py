"""Real-FD diagnostic-data controls only; no root origin, admission or qualified run."""
from contextlib import redirect_stderr, redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tarfile
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location("qualification_diagnostic_data", Path(__file__).with_name("run-qualification.py"))
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class DiagnosticDataControls(unittest.TestCase):
    def test_failed_cli_completion_fixed_two_limits_are_opaque_private_canonical_members(self):
        workspace, output = self.roots()
        expected = {}
        for name, maximum in (("evidence-manifest.json", 256*1024), ("evidence-summary.json", 64*1024)):
            data = b"private-canary\x00\xff" + b"x"*(maximum-len(b"private-canary\x00\xff"))
            self.assertEqual(maximum, len(data))
            self.write(workspace, "failure-cli/"+name, data)
            expected["failure-cli/"+name] = data
            self.write(workspace, "failure-host/"+name, b"not-selected")
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console): self.retain(workspace, output)
        _, contents, index = self.archive(output)
        self.assertEqual("", console.getvalue())
        self.assertEqual(set(expected) | {"index.json"}, set(contents))
        for name, data in expected.items(): self.assertEqual(data, contents[name])
        self.assertEqual(set(expected), {item["name"] for item in index})
        self.assertTrue(all(item["state"] == "complete" for item in index))

    def test_failed_cli_completion_oversize_links_and_public_modes_do_not_upgrade_retention(self):
        for name, maximum in (("evidence-manifest.json", 256*1024), ("evidence-summary.json", 64*1024)):
            for shape in ("oversize", "symlink", "hardlink", "public-mode"):
                with self.subTest(name=name, shape=shape):
                    workspace, output = self.roots()
                    path = self.write(workspace, "failure-cli/"+name, b"private-canary")
                    outside = workspace / "outside"
                    outside.write_bytes(b"outside-canary"); outside.chmod(0o600)
                    if shape == "oversize": path.write_bytes(b"x"*(maximum+1))
                    elif shape == "public-mode": path.chmod(0o644)
                    else:
                        path.unlink()
                        if shape == "symlink": path.symlink_to(outside)
                        else: os.link(outside, path)
                    if shape == "oversize":
                        self.retain(workspace, output)
                        _, contents, index = self.archive(output)
                        self.assertEqual({"index.json"}, set(contents))
                        self.assertEqual([{ "name": "failure-cli/"+name, "state": "oversize", "length": maximum+1}], index)
                    else: self.reject_without_archive(workspace, output, exception=(OSError, module.PreparationFailure))
                    self.assertEqual(b"outside-canary", outside.read_bytes())

    def roots(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name).resolve()
        workspace, output = root / "workspace", root / "output"
        workspace.mkdir(mode=0o700)
        output.mkdir(mode=0o700)
        workspace.chmod(0o700)
        output.chmod(0o700)
        return workspace, output

    def write(self, workspace, name, data):
        path = workspace / name
        parent = workspace
        for component in Path(name).parts[:-1]:
            parent = parent / component
            parent.mkdir(exist_ok=True, mode=0o700)
            parent.chmod(0o700)
        path.write_bytes(data)
        path.chmod(0o600)
        return path

    def retain(self, workspace, output, **kwargs):
        # The production controller keeps the default UID 0. This override tests
        # physical file data owned by the portable process and grants no origin.
        return module.retain_diagnostics(workspace, output, expected_owner_uid=os.geteuid(), **kwargs)

    def archive(self, output):
        path = output / "private-diagnostics.tar"
        raw = path.read_bytes()
        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
        self.assertLessEqual(len(raw), 4*1024*1024)
        with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as archive:
            members = archive.getmembers()
            self.assertEqual(sorted(member.name for member in members), [member.name for member in members])
            contents = {}
            for member in members:
                self.assertTrue(member.isfile())
                self.assertEqual((0o600, 0, 0, 0, "", "", ""),
                                 (member.mode, member.uid, member.gid, member.mtime, member.uname, member.gname, member.linkname))
                self.assertEqual({}, member.pax_headers)
                contents[member.name] = archive.extractfile(member).read()
        # Round-trip headers and padding verify canonical USTAR, with no PAX or
        # hidden additional member bytes, independently of the returned digest.
        canonical = io.BytesIO()
        with tarfile.open(fileobj=canonical, mode="w", format=tarfile.USTAR_FORMAT) as archive:
            for name, data in sorted(contents.items()):
                info = tarfile.TarInfo(name)
                info.size, info.mode = len(data), 0o600
                info.uid = info.gid = info.mtime = 0
                info.uname = info.gname = ""
                archive.addfile(info, io.BytesIO(data))
        self.assertEqual(canonical.getvalue(), raw)
        return raw, contents, json.loads(contents["index.json"])

    def reject_without_archive(self, workspace, output, exception=module.PreparationFailure, **kwargs):
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console), self.assertRaises(exception):
            self.retain(workspace, output, **kwargs)
        self.assertEqual("", console.getvalue())
        self.assertFalse((output / "private-diagnostics.tar").exists())

    def test_binary_empty_tail_and_all_closed_entry_names_have_canonical_private_archive(self):
        workspace, output = self.roots()
        expected = {"build-binding.json": b"binary\x00\xff",
                    "build-logs/build-01.log": b"old-prefix" + bytes(range(256))*80,
                    "build-logs/build-32.log": b"", "build-logs/command-01.json": b"\x00\xfe",
                    "build-logs/command-32.json": b""}
        for entry in ("cli", "host"):
            for name in ("launcher-failure.json", "launcher-worker-journal.log",
                         "subject-failure-output/stdout.prefix", "subject-failure-output/stderr.prefix"):
                expected[f"failure-{entry}/{name}"] = b"\x00\xff" if "stdout" in name else b""
            names = ("evidence-plan.json", "evidence-manifest.json", "evidence-summary.json") if entry == "cli" else ("manifest.json",)
            for name in (*names, module.PRODUCER+"/merged/coverage.cobertura.xml"):
                expected[f"collected-{entry}/{name}"] = b"untrusted-data\x00\x80"
        for name, data in expected.items():
            self.write(workspace, name, data)
        digest = self.retain(workspace, output)
        raw, contents, index = self.archive(output)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)
        self.assertEqual(set(expected) | {"index.json"}, set(contents))
        by_name = {item["name"]: item for item in index}
        self.assertEqual(set(expected), set(by_name))
        for name, original in expected.items():
            retained = original[-16*1024:] if name.endswith("build-01.log") else original
            self.assertEqual(retained, contents[name])
            self.assertEqual({"name": name, "state": "tail" if len(original) > len(retained) else "complete",
                              "length": len(original), "retained_length": len(retained),
                              "retained_sha256": hashlib.sha256(retained).hexdigest()}, by_name[name])

    def test_missing_and_unknown_files_are_ignored_without_console_echo(self):
        workspace, output = self.roots()
        canary = b"unknown-private-data\x00\xff"
        self.write(workspace, "build-logs/build-00.log", canary)
        self.write(workspace, "build-logs/build-33.log", canary)
        self.write(workspace, "collected-host/evidence-plan.json", canary)
        (workspace / "unknown-link").symlink_to("missing-target")
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console):
            digest = self.retain(workspace, output)
        raw, contents, index = self.archive(output)
        self.assertEqual("", console.getvalue())
        self.assertEqual({"index.json": b"[]"}, contents)
        self.assertEqual([], index)
        self.assertNotIn(canary, raw)
        self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)

    def test_raw_worker_journal_16k_and_closed_json_4k_are_independent_private_limits(self):
        workspace, output = self.roots()
        prefix = b"fatal-header-test-canary\n" + b"managed-stack-frame\n" * 64
        self.assertNotIn(b"fatal-header-test-canary", b"".join(prefix.splitlines(keepends=True)[-32:]))
        raw = prefix + b"x" * (16*1024 - len(prefix))
        journal = self.write(workspace, "failure-cli/launcher-worker-journal.log", raw)
        safe_json = b"{}" + b" " * (4096 - 2)
        self.write(workspace, "failure-cli/launcher-failure.json", safe_json)
        self.write(workspace, "failure-cli/unknown-worker-journal.log", b"unknown-canary")
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console):
            self.retain(workspace, output)
        self.assertEqual("", console.getvalue())
        self.assertEqual(0o600, stat.S_IMODE(journal.stat().st_mode))
        self.assertEqual(0o700, stat.S_IMODE(journal.parent.stat().st_mode))
        _, contents, index = self.archive(output)
        self.assertEqual({"index.json", "failure-cli/launcher-worker-journal.log", "failure-cli/launcher-failure.json"}, set(contents))
        self.assertEqual(raw, contents["failure-cli/launcher-worker-journal.log"])
        self.assertEqual(safe_json, contents["failure-cli/launcher-failure.json"])
        self.assertEqual({4096, 16384}, {item["retained_length"] for item in index})

    def test_worker_journal_selected_links_and_public_mode_cannot_be_retained(self):
        for shape in ("symlink", "hardlink", "public-mode"):
            with self.subTest(shape=shape):
                workspace, output = self.roots()
                outside = self.write(workspace, "outside-journal", b"outside-journal-canary")
                journal = self.write(workspace, "failure-host/launcher-worker-journal.log", b"private-journal")
                if shape == "public-mode":
                    journal.chmod(0o644)
                else:
                    journal.unlink()
                    if shape == "symlink": journal.symlink_to(outside)
                    else: os.link(outside, journal)
                self.reject_without_archive(workspace, output, OSError if shape == "symlink" else module.PreparationFailure)
                self.assertEqual(b"outside-journal-canary", outside.read_bytes())

    def test_subject_collector_startup_exact_64k_is_private_complete_and_indexed(self):
        for entry in ("cli", "host"):
            with self.subTest(entry=entry):
                workspace, output = self.roots()
                name = f"failure-{entry}/subject-collector-startup.json"
                data = b'{"numeric":null}' + b" " * (64*1024 - len(b'{"numeric":null}'))
                path = self.write(workspace, name, data)
                info = path.stat()
                self.assertTrue(stat.S_ISREG(info.st_mode))
                self.assertEqual((os.geteuid(), 0o600, 1),
                                 (info.st_uid, stat.S_IMODE(info.st_mode), info.st_nlink))
                self.assertEqual(0o700, stat.S_IMODE(path.parent.stat().st_mode))
                console = io.StringIO()
                with redirect_stdout(console), redirect_stderr(console):
                    digest = self.retain(workspace, output)
                raw, contents, index = self.archive(output)
                self.assertEqual("", console.getvalue())
                self.assertEqual({"index.json", name}, set(contents))
                self.assertEqual(data, contents[name])
                self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)
                self.assertEqual([{"name": name, "state": "complete", "length": 64*1024,
                                   "retained_length": 64*1024,
                                   "retained_sha256": hashlib.sha256(data).hexdigest()}], index)
                self.assertEqual(data, path.read_bytes())

    def test_subject_collector_startup_oversize_links_and_public_mode_cannot_upgrade_archive(self):
        for entry in ("cli", "host"):
            for shape in ("oversize", "symlink", "hardlink", "public-mode"):
                with self.subTest(entry=entry, shape=shape):
                    workspace, output = self.roots()
                    name = f"failure-{entry}/subject-collector-startup.json"
                    canary = b"startup-private-canary"
                    outside = self.write(workspace, "outside-startup", canary)
                    path = self.write(workspace, name, canary)
                    if shape == "oversize":
                        path.write_bytes(canary + b"x" * (65537 - len(canary)))
                        console = io.StringIO()
                        with redirect_stdout(console), redirect_stderr(console):
                            self.retain(workspace, output)
                        raw, contents, index = self.archive(output)
                        self.assertEqual("", console.getvalue())
                        self.assertEqual({"index.json"}, set(contents))
                        self.assertEqual([{"name": name, "state": "oversize", "length": 65537}], index)
                        self.assertNotIn(canary, raw)
                        self.assertEqual(65537, path.stat().st_size)
                    else:
                        if shape == "public-mode":
                            path.chmod(0o644)
                        else:
                            path.unlink()
                            if shape == "symlink": path.symlink_to(outside)
                            else: os.link(outside, path)
                        self.reject_without_archive(workspace, output,
                            OSError if shape == "symlink" else module.PreparationFailure)
                    self.assertEqual(canary, outside.read_bytes())

    def test_selected_links_modes_and_wrong_owner_reject_before_archive(self):
        for shape in ("symlink", "hardlink", "file-mode", "directory-mode", "directory-link", "wrong-owner"):
            with self.subTest(shape=shape):
                workspace, output = self.roots()
                path = self.write(workspace, "build-logs/build-01.log", b"private-data")
                self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                if shape in ("symlink", "hardlink"):
                    outside = self.write(workspace, "outside.log", b"private-data")
                    path.unlink()
                    if shape == "symlink": path.symlink_to(outside)
                    else: os.link(outside, path)
                elif shape == "file-mode":
                    path.chmod(0o644)
                    self.assertNotEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                elif shape == "directory-mode":
                    path.parent.chmod(0o750)
                    self.assertNotEqual(0o700, stat.S_IMODE(path.parent.stat().st_mode))
                elif shape == "directory-link":
                    moved = workspace / "saved-build-logs"
                    path.parent.rename(moved)
                    (workspace / "build-logs").symlink_to(moved, target_is_directory=True)
                if shape == "wrong-owner":
                    with self.assertRaises(module.PreparationFailure):
                        module.retain_diagnostics(workspace, output, expected_owner_uid=os.geteuid()+1)
                    self.assertFalse((output / "private-diagnostics.tar").exists())
                else:
                    self.reject_without_archive(workspace, output, OSError if "link" in shape and shape != "hardlink" else module.PreparationFailure)

    def test_read_error_short_read_growth_and_named_replacement_cannot_publish(self):
        actual_pread = os.pread
        for mutation in ("read-error", "short-read", "growth", "replacement"):
            with self.subTest(mutation=mutation):
                workspace, output = self.roots()
                path = self.write(workspace, "build-binding.json", b"private-binary\x00\xff")
                inode = path.stat().st_ino
                calls = []
                def read(fd, count, offset):
                    self.assertEqual(inode, os.fstat(fd).st_ino)
                    calls.append((count, offset))
                    if mutation == "read-error": raise OSError(5, "fixed-test-canary")
                    data = actual_pread(fd, count, offset)
                    if mutation == "short-read": return data[:-1]
                    if mutation == "growth":
                        with path.open("ab") as stream: stream.write(b"growth")
                    else:
                        replacement = self.write(workspace, "replacement", data)
                        replacement.replace(path)
                    return data
                with patch.object(module.os, "pread", side_effect=read):
                    self.reject_without_archive(workspace, output, OSError if mutation == "read-error" else module.PreparationFailure)
                self.assertEqual(1, len(calls))

    def test_complete_file_bounds_index_oversize_and_aggregate_bound_rejects(self):
        workspace, output = self.roots()
        limits = {"build-binding.json": 1024*1024, "build-logs/command-01.json": 16*1024,
                  "failure-cli/launcher-failure.json": 4096, "failure-host/launcher-worker-journal.log": 16*1024,
                  "failure-cli/subject-failure-output/stdout.prefix": 519168,
                  "failure-host/subject-failure-output/stderr.prefix": 519168,
                  "collected-cli/evidence-plan.json": 128*1024, "collected-host/manifest.json": 128*1024}
        for name, maximum in limits.items(): self.write(workspace, name, b"x"*(maximum+1))
        self.retain(workspace, output)
        _, contents, index = self.archive(output)
        self.assertEqual({"index.json"}, set(contents))
        self.assertEqual({name: {"name": name, "state": "oversize", "length": maximum+1} for name, maximum in limits.items()},
                         {item["name"]: item for item in index})
        workspace, output = self.roots()
        self.write(workspace, "build-binding.json", b"x"*(1024*1024))
        for entry in ("cli", "host"):
            for name in ("stdout.prefix", "stderr.prefix"):
                self.write(workspace, f"failure-{entry}/subject-failure-output/{name}", b"x"*519168)
        self.write(workspace, "collected-host/manifest.json", b"x"*(128*1024))
        self.reject_without_archive(workspace, output)

    def test_deadline_and_existing_or_symlinked_destination_cannot_upgrade_retention(self):
        workspace, output = self.roots()
        self.write(workspace, "build-binding.json", b"private-data")
        with patch.object(module.time, "monotonic", side_effect=(0, 6)):
            self.reject_without_archive(workspace, output)
        for shape in ("existing", "symlink"):
            with self.subTest(shape=shape):
                workspace, output = self.roots()
                self.write(workspace, "build-binding.json", b"private-data")
                outside = output / "unchanged"
                outside.write_bytes(b"destination-canary")
                outside.chmod(0o600)
                destination = output / "private-diagnostics.tar"
                if shape == "existing":
                    destination.write_bytes(b"existing-canary")
                    destination.chmod(0o600)
                else: destination.symlink_to(outside)
                console = io.StringIO()
                with redirect_stdout(console), redirect_stderr(console), self.assertRaises(FileExistsError):
                    self.retain(workspace, output)
                self.assertEqual("", console.getvalue())
                self.assertEqual(b"destination-canary", outside.read_bytes())
                if shape == "existing": self.assertEqual(b"existing-canary", destination.read_bytes())
                else: self.assertTrue(destination.is_symlink())

    def test_actual_verifier_failure_retains_private_command_and_log_for_both_entries(self):
        import sys
        import time

        for entry in ("cli", "host"):
            with self.subTest(entry=entry):
                workspace, output = self.roots()
                collected = workspace / ("collected-" + entry)
                collected.mkdir(mode=0o700)
                collected.chmod(0o700)
                canary = b"verifier-private-canary\x00\xff\n"
                argv = [sys.executable, "-c", "import os,sys; os.write(1," + repr(canary) + "); sys.exit(17)"]
                runner = module.Runner(collected, time.monotonic()+10)
                console = io.StringIO()
                with redirect_stdout(console), redirect_stderr(console):
                    with self.assertRaises(module.PreparationFailure) as failed:
                        runner.run(argv, cwd=workspace)
                    original_failure = failed.exception
                    original_args = original_failure.args
                    command_path = collected / "command-01.json"
                    log_path = collected / "build-01.log"
                    command_bytes, log_bytes = command_path.read_bytes(), log_path.read_bytes()
                    command = json.loads(command_bytes)
                    self.assertEqual(17, command["exit_code"])
                    self.assertEqual("nonzero-exit", command["failure_category"])
                    self.assertIs(True, command["owned_group_empty"])
                    self.assertFalse(command["timed_out"])
                    self.assertEqual(canary, log_bytes)
                    for path in (command_path, log_path):
                        self.assertEqual(0o600, stat.S_IMODE(path.stat().st_mode))
                    digest = self.retain(workspace, output)
                    raw, contents, index = self.archive(output)
                command_name = f"collected-{entry}/command-01.json"
                log_name = f"collected-{entry}/build-01.log"
                self.assertEqual({command_name, log_name, "index.json"}, set(contents))
                self.assertEqual(command_bytes, contents[command_name])
                self.assertEqual(17, json.loads(contents[command_name])["exit_code"])
                self.assertEqual(canary, contents[log_name])
                self.assertEqual({command_name, log_name}, {item["name"] for item in index})
                self.assertTrue(all(item["state"] == "complete" for item in index))
                self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)
                self.assertEqual(original_args, original_failure.args)
                self.assertEqual(("qualification-preparation-rejected",), original_failure.args)
                self.assertEqual("", console.getvalue())


class OwnedExitArchiveControls(unittest.TestCase):
    """Only fixed private file data and archive metadata, never a qualified run."""
    roots = DiagnosticDataControls.roots
    write = DiagnosticDataControls.write
    retain = DiagnosticDataControls.retain
    archive = DiagnosticDataControls.archive
    reject_without_archive = DiagnosticDataControls.reject_without_archive
    def test_owned_exit_fixed_private_record_retains_exact_bytes_and_oversize_is_unavailable(self):
        for entry in ("cli", "host"):
            with self.subTest(entry=entry):
                workspace, output = self.roots()
                name = f"failure-{entry}/launcher-owned-exit.json"
                data = b'{"schema":"issue779-owned-exit-diagnostic-v1","category":"app-join-fault"}'
                self.write(workspace, name, data)
                self.retain(workspace, output)
                _, contents, index = self.archive(output)
                self.assertEqual(data, contents[name])
                self.assertEqual(hashlib.sha256(data).hexdigest(), next(x for x in index if x["name"] == name)["retained_sha256"])
                workspace, output = self.roots()
                self.write(workspace, name, b"x"*4097)
                self.retain(workspace, output)
                _, contents, index = self.archive(output)
                self.assertEqual({"index.json"}, set(contents))
                self.assertEqual("oversize", index[0]["state"])
                workspace, output = self.roots()
                self.write(workspace, name, data).chmod(0o644)
                self.reject_without_archive(workspace, output)


class StartupArchiveControls(unittest.TestCase):
    """Only opaque private schema bytes; the archive cannot establish startup or qualification."""
    roots = DiagnosticDataControls.roots
    write = DiagnosticDataControls.write
    retain = DiagnosticDataControls.retain
    archive = DiagnosticDataControls.archive
    reject_without_archive = DiagnosticDataControls.reject_without_archive
    def test_startup_v2_private_bytes_preserved_for_both_entries_with_fixed_file_bound(self):
        for entry in ("cli", "host"):
            with self.subTest(entry=entry):
                workspace, output = self.roots()
                name = f"failure-{entry}/launcher-owned-exit.json"
                data = json.dumps({"schema": "issue779-owned-exit-diagnostic-v2", "category": "app-join-fault",
                    "startup_phase": "dotnet-validation", "startup_error_class": "os", "startup_errno": 13}).encode()
                self.write(workspace, name, data)
                console = io.StringIO()
                with redirect_stdout(console), redirect_stderr(console): self.retain(workspace, output)
                _, contents, index = self.archive(output)
                self.assertEqual("", console.getvalue()); self.assertEqual(data, contents[name])
                self.assertEqual(hashlib.sha256(data).hexdigest(), index[0]["retained_sha256"])
                workspace, output = self.roots(); self.write(workspace, name, b"x"*4097)
                self.retain(workspace, output); _, contents, index = self.archive(output)
                self.assertEqual({"index.json"}, set(contents)); self.assertEqual("oversize", index[0]["state"])


    def test_vstest_fixed_four_binary_private_files_for_each_entry_are_retained_exactly(self):
        workspace, output = self.roots()
        expected = {}
        for entry in ("cli", "host"):
            for name in ("runner.log", "collector.log", "host.log", "index.json"):
                relative = f"failure-{entry}/vstest-diagnostics/{name}"
                expected[relative] = b"\x00\xffsynthetic-private-trace" if name != "index.json" else b"{}"
                self.write(workspace, relative, expected[relative])
            self.write(workspace, f"failure-{entry}/vstest-diagnostics/unknown.log", b"unknown-canary")
        console = io.StringIO()
        with redirect_stdout(console), redirect_stderr(console): digest = self.retain(workspace, output)
        raw, contents, index = self.archive(output)
        self.assertEqual("", console.getvalue())
        self.assertEqual(hashlib.sha256(raw).hexdigest(), digest)
        self.assertEqual(set(expected) | {"index.json"}, set(contents))
        self.assertNotIn(b"unknown-canary", raw)
        self.assertEqual(set(expected), {item["name"] for item in index})
        for name, data in expected.items(): self.assertEqual(data, contents[name])

    def test_vstest_log_and_index_bounds_preserve_valid_neighbors_without_tail(self):
        workspace, output = self.roots()
        expected = {}
        oversize = {}
        for name, maximum in (("runner.log", 128*1024), ("collector.log", 128*1024),
                              ("host.log", 128*1024), ("index.json", 4096)):
            accepted = f"failure-cli/vstest-diagnostics/{name}"
            rejected = f"failure-host/vstest-diagnostics/{name}"
            expected[accepted] = b"x" * maximum
            oversize[rejected] = maximum + 1
            self.write(workspace, accepted, expected[accepted])
            self.write(workspace, rejected, b"x" * (maximum + 1))
        self.retain(workspace, output)
        _, contents, index = self.archive(output)
        self.assertEqual(set(expected) | {"index.json"}, set(contents))
        for name, data in expected.items(): self.assertEqual(data, contents[name])
        rows = {item["name"]: item for item in index}
        for name, length in oversize.items():
            self.assertEqual({"name": name, "state": "oversize", "length": length}, rows[name])

    def test_vstest_selected_file_links_public_modes_and_directory_links_reject(self):
        for shape in ("symlink", "hardlink", "public-mode", "directory-link"):
            with self.subTest(shape=shape):
                workspace, output = self.roots()
                selected = self.write(workspace, "failure-cli/vstest-diagnostics/runner.log", b"private-canary")
                outside = workspace / "outside"
                outside.write_bytes(b"outside-canary")
                outside.chmod(0o600)
                if shape == "symlink":
                    selected.unlink(); selected.symlink_to(outside)
                elif shape == "hardlink":
                    selected.unlink(); os.link(outside, selected)
                elif shape == "public-mode": selected.chmod(0o644)
                else:
                    selected.unlink(); selected.parent.rmdir()
                    selected.parent.symlink_to(workspace)
                self.reject_without_archive(workspace, output, OSError if shape in ("symlink", "directory-link") else module.PreparationFailure)
                self.assertEqual(b"outside-canary", outside.read_bytes())


if __name__ == "__main__":
    unittest.main()
