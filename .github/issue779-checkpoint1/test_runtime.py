"""Filesystem fixtures for compact runtime custody; no actual .NET run is claimed."""

import hashlib
import importlib.util
import io
import json
import os
from contextlib import redirect_stdout
from pathlib import Path
import stat
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("c1_prepare_runtime", Path(__file__).with_name("prepare-runtime.py"))
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class RuntimeCopyControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-runtime-copy-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.sdk = self.root / "sdk-root"
        self.sdk.mkdir(mode=0o700)
        (self.sdk / "sdk").mkdir(mode=0o755)
        self._file(self.sdk / "dotnet", b"host", 0o755)
        for version in ("10.0.0", "10.0.1", "9.0.0"):
            self._tree(self.sdk / "host" / "fxr" / version, "hostfxr-" + version)
        for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
            for version in ("10.0.0", "10.0.1", "9.0.0"):
                self._tree(self.sdk / "shared" / framework / version,
                           framework + "-" + version)
        self._file(self.sdk / "sdk" / "do-not-copy", b"sdk", 0o644)
        self._file(self.sdk / "packs" / "do-not-copy", b"packs", 0o644)
        self._file(self.sdk / "templates" / "do-not-copy", b"templates", 0o644)
        # Model the installed SDK's exact public directory modes even when the
        # workflow creates this owned fixture under its private 077 umask.
        for directory in self.sdk.rglob("*"):
            if directory.is_dir():
                directory.chmod(0o755)
        self.output = self.root / "compact-runtime"
        self.receipt = self.root / "runtime-receipt.json"

    @staticmethod
    def _file(path, data, mode):
        path.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
        path.write_bytes(data)
        path.chmod(mode)

    def _tree(self, root, content):
        root.mkdir(mode=0o755, parents=True, exist_ok=True)
        self._file(root / "libfixture.so", content.encode(), 0o755)
        self._file(root / "metadata.json", b"{}", 0o644)

    def test_copies_only_selected_actual_bytes_and_writes_private_receipt(self):
        result = runtime.prepare_runtime(self.sdk, self.output, self.receipt)
        self.assertEqual(result["host_fxr_version"], "10.0.1")
        self.assertEqual(result["shared_framework_version"], "10.0.1")
        self.assertFalse((self.output / "sdk").exists())
        self.assertFalse((self.output / "packs").exists())
        self.assertFalse((self.output / "templates").exists())
        copied = self.output / "shared/Microsoft.NETCore.App/10.0.1/metadata.json"
        self.assertEqual(copied.read_bytes(), b"{}")
        self.assertEqual(copied.stat().st_mode & 0o777, 0o644)
        self.assertEqual((self.output / "dotnet").stat().st_mode & 0o777, 0o755)
        self.assertEqual(self.output.stat().st_mode & 0o777, 0o755)
        self.assertEqual(self.receipt.stat().st_mode & 0o777, 0o600)
        self.assertEqual(result["image"]["files"]["dotnet"]["sha256"],
                         hashlib.sha256(b"host").hexdigest())
        self.assertFalse(result["os_qualification"])

    def test_symlink_and_hardlink_reject_without_changing_outside_mode(self):
        outside = self.root / "outside"
        outside.write_bytes(b"outside")
        outside.chmod(0o644)
        selected = self.sdk / "host/fxr/10.0.1/libfixture.so"
        original = selected.read_bytes()
        selected.unlink()
        selected.symlink_to(outside)
        with self.assertRaisesRegex(runtime.Rejected, "^runtime-source-link-or-special$"):
            runtime.prepare_runtime(self.sdk, self.output)
        self.assertEqual(outside.stat().st_mode & 0o777, 0o644)
        selected.unlink()
        os.link(outside, selected)
        with self.assertRaisesRegex(runtime.Rejected, "^runtime-source-link-or-special$"):
            runtime.prepare_runtime(self.sdk, self.output)
        self.assertEqual(outside.stat().st_mode & 0o777, 0o644)
        self.assertFalse(self.output.exists())
        selected.unlink()
        selected.write_bytes(original)
        selected.chmod(0o755)

    def test_oversized_selected_file_rejects_before_output_creation(self):
        selected = self.sdk / "shared/Microsoft.NETCore.App/10.0.1/metadata.json"
        selected.write_bytes(b"x" * 12)
        selected.chmod(0o644)
        with patch.object(runtime, "FILE_LIMIT", 8):
            with self.assertRaisesRegex(runtime.Rejected, "^runtime-source-file$"):
                runtime.prepare_runtime(self.sdk, self.output)
        self.assertFalse(self.output.exists())

    def test_source_substitution_during_hash_scan_rejects_and_keeps_replacement_mode(self):
        selected = self.sdk / "host/fxr/10.0.1/libfixture.so"
        replacement_mode = 0o644
        original_read = runtime.os.read
        replaced = False

        def substitute(fd, size):
            nonlocal replaced
            opened = os.fstat(fd)
            if (not replaced and stat.S_ISREG(opened.st_mode)
                    and opened.st_size == len(b"hostfxr-10.0.1")):
                replaced = True
                selected.rename(selected.with_name("saved"))
                selected.write_bytes(b"replacement")
                selected.chmod(replacement_mode)
            return original_read(fd, size)

        with patch.object(runtime.os, "read", side_effect=substitute):
            with self.assertRaisesRegex(runtime.Rejected, "^runtime-source-substitution$"):
                runtime.prepare_runtime(self.sdk, self.output)
        self.assertTrue(replaced)
        self.assertEqual(selected.stat().st_mode & 0o777, replacement_mode)
        self.assertFalse(self.output.exists())

    def test_invalid_input_mode_and_mismatched_framework_versions_reject(self):
        selected = self.sdk / "shared/Microsoft.NETCore.App/10.0.1/metadata.json"
        selected.chmod(0o600)
        with self.assertRaisesRegex(runtime.Rejected, "^runtime-source-file$"):
            runtime.prepare_runtime(self.sdk, self.output)
        selected.chmod(0o644)
        aspnet = self.sdk / "shared/Microsoft.AspNetCore.App/10.0.1"
        for node in aspnet.iterdir():
            node.unlink()
        aspnet.rmdir()
        core_old = self.sdk / "shared/Microsoft.NETCore.App/10.0.0"
        for node in core_old.iterdir():
            node.unlink()
        core_old.rmdir()
        with self.assertRaisesRegex(runtime.Rejected, "^runtime-framework-version-mismatch$"):
            runtime.prepare_runtime(self.sdk, self.output)
        self.assertFalse(self.output.exists())

    def test_main_reports_actual_directory_rejection_without_publishing_runtime(self):
        (self.sdk / "host/fxr").chmod(0o700)
        output = io.StringIO()
        with redirect_stdout(output):
            exit_code = runtime.main(["--sdk-root", str(self.sdk), "--output", str(self.output)])
        self.assertEqual(exit_code, 1)
        self.assertEqual(json.loads(output.getvalue()), {
            "schema": "issue779-checkpoint1-runtime-failure-v1",
            "category": "runtime-source-directory", "error_kind": "input", "errno": None,
        })
        self.assertFalse(self.output.exists())

    def test_main_normalizes_unknown_errors_without_echoing_messages_or_paths(self):
        secret = "/private-canary/runtime-input"
        for error, category, kind, number in (
                (runtime.Rejected(secret), "runtime-input-invalid", "input", None),
                (ValueError(secret), "runtime-input-invalid", "input", None),
                (PermissionError(13, secret, secret), "runtime-os-error", "os", 13)):
            with self.subTest(error=type(error).__name__):
                output = io.StringIO()
                with patch.object(runtime, "prepare_runtime", side_effect=error), redirect_stdout(output):
                    exit_code = runtime.main(["--sdk-root", str(self.sdk), "--output", str(self.output)])
                raw = output.getvalue()
                self.assertEqual(exit_code, 1)
                self.assertEqual(json.loads(raw), {
                    "schema": "issue779-checkpoint1-runtime-failure-v1",
                    "category": category, "error_kind": kind, "errno": number,
                })
                self.assertNotIn(secret, raw)
                self.assertLess(len(raw.encode()), 512)
                self.assertFalse(self.output.exists())

    def test_failure_errno_requires_a_positive_bounded_integer(self):
        for number in (None, True, -1, 0, 4096, 2):
            with self.subTest(number=number):
                error = OSError(13, "/private-canary/runtime-input")
                error.errno = number
                self.assertEqual(runtime.failure_diagnostic(error)["errno"],
                                 2 if type(number) is int and number == 2 else None)


if __name__ == "__main__":
    unittest.main()
