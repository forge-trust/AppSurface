"""Private diagnostic-data/ordering controls; no root, systemd or admission proof.

Privileged operations in the one startup procedure control are mocked and
recorded. Files and owned descriptors are real temporary data. Source prep does
not execute this suite; the parent authorizes finite validation separately.
"""
from contextlib import ExitStack, redirect_stderr, redirect_stdout
import argparse
import errno
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "private_launcher_startup_data", Path(__file__).parents[3] / "scripts" / "evidencehost-linux-launcher.py")
module = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = module
SPEC.loader.exec_module(module)


class LauncherStartupDiagnosticControls(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.root.chmod(0o700)
        self.number = 0

    def directory(self):
        self.number += 1
        path = self.root / str(self.number)
        path.mkdir(mode=0o700)
        fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        self.addCleanup(os.close, fd)
        return path, fd

    def progress(self, *, deadline=None):
        progress = module._LauncherStartupProgress()
        progress.enter("product-prepare")
        if deadline is not None:
            progress.bind_deadline(deadline)
        return progress

    def capture(self, fd, error, progress=None):
        return module._capture_launcher_startup_failure(
            fd, error, self.progress() if progress is None else progress, expected_owner_uid=os.getuid())

    def record(self, path):
        target = path / module.LAUNCHER_STARTUP_FILE
        data = target.read_bytes()
        self.assertLessEqual(len(data), 4096)
        self.assertEqual(0o600, stat.S_IMODE(target.stat().st_mode))
        self.assertEqual(1, target.stat().st_nlink)
        self.assertNotIn(b"private-canary", data)
        result = json.loads(data)
        self.assertEqual({"schema", "stage", "exception_family", "product_category", "errno",
                          "job_remaining_ms", "qualification_claim", "coverage_credit"}, set(result))
        self.assertIs(result["qualification_claim"], False)
        self.assertIs(result["coverage_credit"], False)
        return result

    def test_actual_families_known_categories_and_predeadline_null_are_private(self):
        errors = ((module._product.ProductCoverageError("tree-bound"), "ProductCoverageError", "tree-bound", None),
                  (module._product.ProductCoverageError("tree-owner"), "ProductCoverageError", "tree-owner", None),
                  (module._product.ProductCoverageError("file-count"), "ProductCoverageError", "file-count", None),
                  (TypeError("private-canary"), "TypeError", None, None),
                  (OSError(errno.EIO, "private-canary"), "OSError", None, errno.EIO),
                  (Exception("private-canary"), "Other", None, None))
        for error, family, category, code in errors:
            with self.subTest(family=family, category=category):
                path, fd = self.directory()
                console = io.StringIO()
                with redirect_stdout(console), redirect_stderr(console):
                    self.assertTrue(self.capture(fd, error))
                self.assertEqual("", console.getvalue())
                row = self.record(path)
                self.assertEqual("product-prepare", row["stage"])
                self.assertEqual(family, row["exception_family"])
                self.assertEqual(category, row["product_category"])
                self.assertEqual(code, row["errno"])
                self.assertIsNone(row["job_remaining_ms"])
                self.assertTrue(stat.S_ISDIR(os.fstat(fd).st_mode))

    def test_file_shape_category_is_closed_private_data(self):
        for category in ('file-shape', 'published-binding', 'published-files-missing'):
            with self.subTest(category=category):
                path, fd = self.directory()
                self.assertTrue(self.capture(fd, module._product.ProductCoverageError(category)))
                row = self.record(path)
                self.assertEqual('ProductCoverageError', row['exception_family'])
                self.assertEqual(category, row['product_category'])
                self.assertIsNone(row['errno'])
                self.assertIsNone(row['job_remaining_ms'])

    def test_unknown_category_and_numeric_errno_never_echo_or_gain_a_category(self):
        for error in (module._product.ProductCoverageError("private-canary"),
                      module._product.ProductCoverageError("tree-bound", "private-canary"),
                      OSError(999999, "private-canary"), module.LauncherError("private-canary", errno=True)):
            with self.subTest(error_type=type(error).__name__):
                path, fd = self.directory()
                self.assertTrue(self.capture(fd, error))
                row = self.record(path)
                self.assertIsNone(row["product_category"])
                self.assertIsNone(row["errno"])

    def test_unsafe_directory_owner_mode_and_non_directory_reject_with_valid_neighbor(self):
        path, fd = self.directory()
        error = ValueError("private-canary")
        self.assertFalse(module._capture_launcher_startup_failure(
            fd, error, self.progress(), expected_owner_uid=os.getuid()+1))
        path.chmod(0o755)
        self.assertFalse(self.capture(fd, error))
        self.assertEqual([], list(path.iterdir()))
        path.chmod(0o700)
        self.assertTrue(self.capture(fd, error))
        _, other_fd = self.directory()
        target = self.root / "ordinary"
        target.write_bytes(b"private-canary")
        target.chmod(0o600)
        with target.open("rb") as stream:
            self.assertFalse(self.capture(stream.fileno(), error))
        self.assertTrue(stat.S_ISDIR(os.fstat(other_fd).st_mode))

    def test_existing_regular_symlink_and_hardlink_are_not_replaced(self):
        for kind in ("regular", "symlink", "hardlink"):
            with self.subTest(kind=kind):
                path, fd = self.directory()
                sentinel = path / "sentinel"
                sentinel.write_bytes(b"private-canary")
                sentinel.chmod(0o600)
                target = path / module.LAUNCHER_STARTUP_FILE
                if kind == "symlink": target.symlink_to(sentinel)
                elif kind == "hardlink": os.link(sentinel, target)
                else:
                    target.write_bytes(b"private-canary")
                    target.chmod(0o600)
                before = target.lstat()
                self.assertFalse(self.capture(fd, ValueError("private-canary")))
                self.assertEqual(b"private-canary", sentinel.read_bytes())
                self.assertEqual((before.st_ino, before.st_mode), (target.lstat().st_ino, target.lstat().st_mode))
                neighbor, neighbor_fd = self.directory()
                self.assertTrue(self.capture(neighbor_fd, ValueError("private-canary")))
                self.record(neighbor)

    def test_original_deadline_expiry_inactive_and_invalid_phase_skip_before_open(self):
        path, fd = self.directory()
        error = ValueError("private-canary")
        with patch.object(module.time, "monotonic", return_value=10.0):
            for progress in (self.progress(deadline=10.0), self.progress(deadline=float("nan"))):
                with patch.object(module.os, "open", wraps=os.open) as opened:
                    self.assertFalse(self.capture(fd, error, progress))
                    opened.assert_not_called()
            progress = self.progress(deadline=20.0)
            progress.finish()
            self.assertFalse(self.capture(fd, error, progress))
            progress = self.progress()
            progress.stage = "private-canary"
            self.assertFalse(self.capture(fd, error, progress))
            self.assertEqual([], list(path.iterdir()))
            self.assertTrue(self.capture(fd, error, self.progress(deadline=20.0)))
        self.assertEqual(10000, self.record(path)["job_remaining_ms"])

    def test_real_close_error_or_clock_crossing_never_publishes_capture_success(self):
        original_close = os.close
        for shape in ("close-error", "deadline"):
            with self.subTest(shape=shape):
                path, fd = self.directory()
                clock = [10.0]
                closed = []
                def close_owned(number):
                    self.assertNotEqual(fd, number)
                    original_close(number)
                    closed.append(number)
                    if shape == "close-error": raise OSError(errno.EIO, "private-canary")
                    clock[0] = 21.0
                with patch.object(module.time, "monotonic", side_effect=lambda: clock[0]), \
                        patch.object(module.os, "close", side_effect=close_owned):
                    self.assertFalse(self.capture(fd, ValueError("private-canary"), self.progress(deadline=20.0)))
                self.assertEqual(1, len(closed))
                with self.assertRaises(OSError): os.fstat(closed[0])
                self.assertTrue(stat.S_ISDIR(os.fstat(fd).st_mode))
                self.record(path)  # Complete private bytes are not a successful close/deadline result.

    def test_write_failure_and_named_substitution_preserve_borrowed_fd_and_canary(self):
        for shape in ("write", "named-substitution"):
            with self.subTest(shape=shape):
                path, fd = self.directory()
                sentinel = path / "sentinel"
                sentinel.write_bytes(b"private-canary")
                sentinel.chmod(0o600)
                original_write = os.write
                def write_owned(number, data):
                    if shape == "write": raise OSError(errno.EIO, "private-canary")
                    count = original_write(number, data)
                    target = path / module.LAUNCHER_STARTUP_FILE
                    target.unlink()
                    target.symlink_to(sentinel)
                    return count
                with patch.object(module.os, "write", side_effect=write_owned):
                    self.assertFalse(self.capture(fd, ValueError("private-canary")))
                self.assertEqual(b"private-canary", sentinel.read_bytes())
                self.assertTrue(stat.S_ISDIR(os.fstat(fd).st_mode))

    def test_wrapper_rethrows_same_exception_even_when_capture_itself_raises(self):
        original = TypeError("private-canary")
        with patch.object(module, "_launch_with_completion_impl", side_effect=original), \
                patch.object(module, "_capture_launcher_startup_failure", side_effect=OSError("private-canary")):
            with self.assertRaises(TypeError) as caught:
                module.launch_with_completion(argparse.Namespace(), diagnostic_directory_fd=123)
        self.assertIs(original, caught.exception)

    def test_poststartup_failure_does_not_create_startup_record(self):
        path, fd = self.directory()
        original = module.LauncherError("worker-timeout")
        def after_startup(args, *, diagnostic_directory_fd, _startup):
            _startup.finish()
            raise original
        with patch.object(module, "_launch_with_completion_impl", side_effect=after_startup):
            with self.assertRaises(module.LauncherError) as caught:
                module.launch_with_completion(argparse.Namespace(), diagnostic_directory_fd=fd)
        self.assertIs(original, caught.exception)
        self.assertEqual([], list(path.iterdir()))

    def test_actual_startup_procedure_marks_owner_prepare_before_error_and_cleanup(self):
        # Actual launch procedure and real temp FDs, with privileged account,
        # systemd, root metadata and product APIs mocked: no root authority claim.
        tool, subject, parent = (self.root / name for name in ("tool", "subject", "parent"))
        for path in (tool, subject, parent): path.mkdir(mode=0o700)
        solution = subject / "subject.sln"
        solution.write_bytes(b"temporary-subject")
        policy = self.root / "policy.json"
        policy.write_bytes(b"{}")
        original = module._product.ProductCoverageError("tree-bound")
        events = []
        class Owner:
            def prepare(self):
                events.append("prepare")
                raise original
            def abort_after_owned_exit(self): events.append("abort")
        def create_temp(**kwargs):
            path, fd = self.directory()
            return str(path)
        def scratch_layout(path, *identities):
            (path / "subject").mkdir()
            (path / "test-output").mkdir()
        original_fstat, original_read_text, original_is_file = os.fstat, Path.read_text, Path.is_file
        original_lstat = Path.lstat
        def root_stat(fd):
            value = original_fstat(fd)
            values = list(value)
            values[4] = 0  # Narrow stat-owner metadata mock; actual temp directory FD.
            return os.stat_result(values)
        def named_stat(path, *args, **kwargs):
            value = original_lstat(path, *args, **kwargs)
            if path.name.startswith("product-coverage-"):
                values = list(value)
                values[4] = 0
                return os.stat_result(values)
            return value
        def read_text(path, *args, **kwargs):
            return "systemd" if str(path) == "/proc/1/comm" else original_read_text(path, *args, **kwargs)
        def is_file(path):
            return True if str(path) == "/sys/fs/cgroup/cgroup.controllers" else original_is_file(path)
        def captured(directory_fd, error, progress):
            events.append(("capture", progress.stage, progress.deadline, error))
            return False
        args = argparse.Namespace(run_id="1/1", job_seconds=60, base_revision="base", subject_revision="subject",
            workflow_identity="workflow", observation_profile=[], observation_producer=[], solution=str(solution),
            path=["src/Orders.cs"], mode="observation", output_slot="qualification")
        with ExitStack() as stack:
            replacements = ((module.sys, "platform", "linux"), (module.os, "geteuid", lambda: 0),
                (Path, "read_text", read_text), (Path, "is_file", is_file), (Path, "lstat", named_stat),
                (module, "ensure_openat2_supported", lambda: None),
                (module, "_systemd", lambda *a, **k: SimpleNamespace(stdout=b"systemd 255\n")),
                (module, "validate_args", lambda a: (tool, subject, policy, parent)),
                (module, "protected_diff_snapshot", lambda *a: (None, None)),
                (module, "validate_budgets", lambda a: {}), (module, "select_root_application", lambda *a: None),
                (module, "declared_subject_inputs", lambda *a: (str(solution), ["src/Orders.cs"])),
                (module, "_create_run_accounts", lambda *a: events.append("accounts")),
                (module.pwd, "getpwnam", lambda name: SimpleNamespace(pw_uid=1001 if name.startswith("evw") else 1002, pw_gid=1101)),
                (module.grp, "getgrnam", lambda name: SimpleNamespace(gr_gid=1201)),
                (module.os, "chown", lambda *a: events.append("chown")),
                (module.tempfile, "mkdtemp", create_temp), (module, "prepare_scratch_layout", scratch_layout),
                (module, "_copy_subject_tree", lambda source, destination, *a: destination),
                (module, "open_test_output_root", lambda path, *a: os.open(path / "test-output", os.O_RDONLY | os.O_DIRECTORY)),
                (module, "capture_job_deadline", lambda seconds: ("frozen-wall", 12345.0)),
                (module.shutil, "which", lambda *a, **k: sys.executable), (module.os, "fstat", root_stat),
                (module._product, "ProductCoverageOwner", lambda *a, **k: Owner()),
                (module, "_capture_launcher_startup_failure", captured))
            for target, name, value in replacements: stack.enter_context(patch.object(target, name, value))
            with self.assertRaises(module._product.ProductCoverageError) as caught:
                module.launch_with_completion(args, diagnostic_directory_fd=None)
        self.assertIs(original, caught.exception)
        self.assertIn("prepare", events)
        self.assertIn("abort", events)
        capture = next(value for value in events if isinstance(value, tuple))
        self.assertEqual(("capture", "product-prepare", 12345.0, original), capture)
        self.assertLess(events.index("abort"), events.index(capture))


class PrivateBundleSourceControls(unittest.TestCase):
    """Real portable FD/audit data; never a registration, root lease or launch."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="private-bundle-source-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.root.chmod(0o700)
        self.uid, self.gid, self.number = os.getuid(), os.getgid(), 0
        self.guards = []
        for owner, name in ((module._application, "RootApplicationLease"),
                            (module._application, "select_registration"),
                            (module, "_systemd"), (module.subprocess, "Popen")):
            guard = patch.object(owner, name, side_effect=AssertionError("authority-path-not-allowed"))
            self.guards.append(guard.start()); self.addCleanup(guard.stop)

    def tearDown(self):
        for guard in self.guards: guard.assert_not_called()

    def fixture(self):
        import hashlib
        self.number += 1
        workspace = self.root / str(self.number)
        workspace.mkdir(mode=0o700)
        tool = workspace / "tool-cli"
        tool.mkdir(mode=0o700)
        canary = tool / "untouched.dll"
        canary.write_bytes(b"outside-published-canary"); canary.chmod(0o444)
        tool.chmod(0o555)
        outer = workspace / "application-bundle-input"
        outer.mkdir(mode=0o700)
        application = outer / "app"
        application.mkdir(mode=0o700)
        bundle = application / "build"
        bundle.mkdir(mode=0o700)
        payloads = {"data.dll": (b"dll-data", 0o444),
                    "native": (b"\x7fELF\x02data-only", 0o555),
                    "declared.txt": (b"declared-input", 0o444)}
        rows = []
        for name, (data, mode) in payloads.items():
            (bundle / name).write_bytes(data); (bundle / name).chmod(mode)
            rows.append(module._application.BundleFile(name, "Dependency", len(data),
                                                       hashlib.sha256(data).hexdigest(), mode))
        bundle.chmod(0o555); application.chmod(0o555)
        # Direct CandidateAudit file data has no selected-registration identity.
        # Only audit_bundle consumes it; every authority constructor is guarded.
        canonical = b'{"file_audit_data_only":true}'
        candidate = module._application.CandidateAudit(canonical, b'{}', hashlib.sha256(canonical).hexdigest(),
            "0"*64, "0"*64, "profile", "app", tuple(rows),
            module._application.Capabilities(("declared.txt",), 1, 1, 1, 1, 1, 1), "resource", 1)
        return workspace, tool, outer, application, bundle, candidate, payloads, canary

    def source(self, tool, *, uid=None, gid=None, deadline=None):
        return module._application_bundle_source(tool, "app", "build",
            module.time.monotonic()+10 if deadline is None else deadline,
            expected_owner_uid=self.uid if uid is None else uid,
            expected_owner_gid=self.gid if gid is None else gid)

    def test_default_false_preserves_tool_lookup_and_private_missing_sibling_has_no_fallback(self):
        workspace, tool, outer, application, bundle, candidate, payloads, canary = self.fixture()
        with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", False), patch.object(
                module._product, "open_directory", side_effect=AssertionError("ordinary-lookup-must-not-open")) as opened:
            with self.source(tool) as source:
                self.assertEqual(tool / "application-bundles/app/build", source)
        opened.assert_not_called()
        tool.chmod(0o700)
        ordinary = tool / "application-bundles/app/build"
        ordinary.mkdir(parents=True)
        (ordinary / "ordinary-canary").write_bytes(b"must-not-be-fallback")
        outer.rename(workspace / "retained-sibling")
        with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True):
            with self.assertRaises((module._application.ApplicationError, module._product.ProductCoverageError, OSError)):
                with self.source(tool): self.fail("missing-sibling-must-reject")
        self.assertEqual(b"must-not-be-fallback", (ordinary / "ordinary-canary").read_bytes())
        self.assertEqual(b"outside-published-canary", canary.read_bytes())

    def test_private_fixed_sibling_audit_pins_exact_bytes_and_closes_actual_descriptors(self):
        _, tool, outer, _, bundle, candidate, payloads, canary = self.fixture()
        held = []
        with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True):
            with self.source(tool) as source:
                self.assertEqual(bundle, source)
                with module._application.audit_bundle(source, candidate,
                        expected_owner_uid=self.uid, deadline=module.time.monotonic()+10) as pinned:
                    held = [pinned.root_fd, *(fd for _, fd, _ in pinned.files)]
                    pinned.require_binding(candidate)
                    for name, fd, identity in pinned.files:
                        self.assertEqual(payloads[name][0], os.pread(fd, len(payloads[name][0])+1, 0))
                        self.assertEqual(payloads[name][1], stat.S_IMODE(os.fstat(fd).st_mode))
                        self.assertEqual(1, os.fstat(fd).st_nlink)
                    pinned.verify_candidate(candidate, expected_owner_uid=self.uid,
                                            deadline=module.time.monotonic()+10)
                    # Copy only actual retained small FD data, then exercise the
                    # same independent audit on the copy; no workspace/lease runs.
                    copied = self.root / "audited-copy"
                    copied.mkdir(mode=0o700)
                    for name, fd, _ in pinned.files:
                        item = next(row for row in candidate.files if row.relative_path == name)
                        destination = os.open(copied / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                        try:
                            data = os.pread(fd, item.length_bytes+1, 0)
                            self.assertEqual(item.length_bytes, len(data))
                            position = 0
                            while position < len(data):
                                written = os.write(destination, data[position:])
                                self.assertGreater(written, 0); position += written
                            os.fchmod(destination, item.mode)
                        finally:
                            os.close(destination)
                    copied.chmod(0o555)
                    with module._application.audit_bundle(copied, candidate,
                            expected_owner_uid=self.uid, deadline=module.time.monotonic()+10) as audited_copy:
                        audited_copy.require_binding(candidate)
                        self.assertNotEqual(pinned.root_identity[:2], audited_copy.root_identity[:2])
                        for name, fd, _ in audited_copy.files:
                            self.assertEqual(payloads[name][0], os.pread(fd, len(payloads[name][0])+1, 0))
                self.assertEqual(0o700, stat.S_IMODE(outer.lstat().st_mode))
        for fd in held:
            with self.assertRaises(OSError) as failure: os.fstat(fd)
            self.assertEqual(errno.EBADF, failure.exception.errno)
        self.assertEqual(b"outside-published-canary", canary.read_bytes())

    def test_private_unsafe_parent_modes_owner_group_or_link_reject_before_bundle_audit(self):
        for variant in ("outer-mode", "app-mode", "build-mode", "owner", "group", "link"):
            with self.subTest(variant=variant):
                _, tool, outer, application, bundle, _, _, canary = self.fixture()
                kwargs = {}
                if variant == "outer-mode": outer.chmod(0o755)
                if variant == "app-mode": application.chmod(0o755)
                if variant == "build-mode": bundle.chmod(0o755)
                if variant == "owner": kwargs["uid"] = self.uid+1
                if variant == "group": kwargs["gid"] = self.gid+1
                if variant == "link":
                    application.chmod(0o700)
                    bundle.rename(application / "original")
                    bundle.symlink_to(application / "original", target_is_directory=True)
                    application.chmod(0o555)
                with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True), patch.object(
                        module._application, "audit_bundle", side_effect=AssertionError("must-not-audit")) as audited:
                    with self.assertRaises((module._application.ApplicationError, module._product.ProductCoverageError, OSError)):
                        with self.source(tool, **kwargs): self.fail("unsafe-parent-must-reject")
                audited.assert_not_called()
                self.assertEqual(b"outside-published-canary", canary.read_bytes())

    def test_bundle_actual_hash_length_mode_link_and_fifo_fail_without_outside_mutation(self):
        for variant in ("hash", "length", "mode", "hardlink", "symlink", "fifo"):
            with self.subTest(variant=variant):
                _, tool, _, _, bundle, candidate, _, canary = self.fixture()
                target = bundle / "data.dll"
                bundle.chmod(0o700)
                if variant == "hash":
                    target.chmod(0o600); target.write_bytes(b"bad-data"); target.chmod(0o444)
                if variant == "length":
                    target.chmod(0o600); target.write_bytes(b"dll-data-extra"); target.chmod(0o444)
                if variant == "mode": target.chmod(0o644)
                if variant == "hardlink": os.link(target, self.root / f"hardlink-{self.number}")
                if variant == "symlink": target.unlink(); target.symlink_to(canary)
                if variant == "fifo": target.unlink(); os.mkfifo(target, 0o444)
                bundle.chmod(0o555)
                with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True):
                    with self.source(tool) as source:
                        with self.assertRaises(module._application.ApplicationError):
                            module._application.audit_bundle(source, candidate,
                                expected_owner_uid=self.uid, deadline=module.time.monotonic()+10)
                self.assertEqual(b"outside-published-canary", canary.read_bytes())
                self.assertEqual(0o444, stat.S_IMODE(canary.lstat().st_mode))

    def test_named_build_substitution_rejects_on_context_exit_and_closes_all_retained_fds(self):
        _, tool, _, application, bundle, _, payloads, canary = self.fixture()
        closed = []
        original_close = os.close
        def close(fd):
            closed.append(fd); return original_close(fd)
        with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True), patch.object(module.os, "close", close):
            with self.assertRaises(module._application.ApplicationError):
                with self.source(tool) as source:
                    application.chmod(0o700)
                    source.rename(application / "retained-original")
                    source.mkdir(mode=0o555)
                    application.chmod(0o555)
        self.assertEqual(4, len(set(closed)))
        for fd in set(closed):
            with self.assertRaises(OSError) as failure: os.fstat(fd)
            self.assertEqual(errno.EBADF, failure.exception.errno)
        self.assertEqual(payloads["data.dll"][0], (application / "retained-original/data.dll").read_bytes())
        self.assertEqual(b"outside-published-canary", canary.read_bytes())

    def test_original_deadline_rejects_before_any_directory_open(self):
        _, tool, _, _, _, _, _, canary = self.fixture()
        with patch.object(module, "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE", True), patch.object(
                module.os, "open", side_effect=AssertionError("expired-must-not-open")) as opened:
            with self.assertRaises(module.LauncherError):
                with self.source(tool, deadline=module.time.monotonic()-1): self.fail("expired-must-reject")
        opened.assert_not_called()
        self.assertEqual(b"outside-published-canary", canary.read_bytes())

    def test_compiled_marker_selects_empty_published_rows_and_source_audits_enclose_copy(self):
        import ast
        tree = ast.parse(Path(module.__file__).read_text())
        constants = [node for node in tree.body if isinstance(node, ast.Assign)
                     and any(isinstance(target, ast.Name) and target.id == "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE"
                             for target in node.targets)]
        self.assertEqual(1, len(constants)); self.assertIs(constants[0].value.value, False)
        launch = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                      and node.name == "_launch_with_completion_impl")
        owner = next(node for node in ast.walk(launch) if isinstance(node, ast.Call)
                     and isinstance(node.func, ast.Attribute) and node.func.attr == "ProductCoverageOwner")
        expression = next(row.value for row in owner.keywords if row.arg == "published_files")
        self.assertIsInstance(expression, ast.IfExp)
        self.assertIsInstance(expression.orelse, ast.Tuple); self.assertEqual([], expression.orelse.elts)
        markers = [node for node in ast.walk(expression.test)
                   if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not)
                   and isinstance(node.operand, ast.Name)
                   and node.operand.id == "_PRIVATE_QUALIFICATION_BUNDLE_SOURCE"]
        self.assertEqual(1, len(markers))
        self.assertIsInstance(expression.body, ast.Call)
        self.assertIsInstance(expression.body.func, ast.Name)
        self.assertEqual("tuple", expression.body.func.id)
        # Evaluate only the actual closed selector, never ProductCoverageOwner.
        result = eval(compile(ast.Expression(expression), "published-selector-data", "eval"),
                      {"_PRIVATE_QUALIFICATION_BUNDLE_SOURCE": True,
                       "selected_application": object(), "Path": Path})
        self.assertEqual((), result)
        workspace = next(node for node in tree.body if isinstance(node, ast.ClassDef)
                         and node.name == "_ApplicationWorkspaceOwner")
        initializer = next(node for node in workspace.body if isinstance(node, ast.FunctionDef)
                           and node.name == "__init__")
        source_context = next(node for node in ast.walk(initializer) if isinstance(node, ast.With)
                              and any(isinstance(item.context_expr, ast.Call)
                                      and isinstance(item.context_expr.func, ast.Name)
                                      and item.context_expr.func.id == "_application_bundle_source"
                                      for item in node.items))
        audited = [node for node in ast.walk(source_context) if isinstance(node, ast.Call)
                   and isinstance(node.func, ast.Attribute) and node.func.attr == "audit_bundle"]
        self.assertEqual(2, len(audited))
        audited.sort(key=lambda node: node.lineno)
        copy = next(node for node in ast.walk(source_context) if isinstance(node, ast.Call)
                    and isinstance(node.func, ast.Attribute) and node.func.attr == "pread")
        self.assertLess(audited[0].lineno, copy.lineno)
        self.assertGreater(audited[1].lineno, copy.lineno)


if __name__ == "__main__":
    unittest.main()
