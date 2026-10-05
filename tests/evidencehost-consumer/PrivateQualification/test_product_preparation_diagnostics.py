"""Real tiny-file diagnostics only; no root, build, worker or ABI authority."""
import ast
import importlib.util
import json
import os
from pathlib import Path
import tarfile
import tempfile
import time
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("product_preparation_diagnostics", Path(__file__).with_name("prepare-product.py"))
P = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(P)


class ProductPreparationDiagnosticControls(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="issue779-product-diagnostic-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        self.workspace = self.root / "workspace"
        self.workspace.mkdir(mode=0o700)
        self.tool = self.workspace / "tool-cli"
        self.tool.mkdir(mode=0o700)
        self.receipt = self.workspace / "product-binary-cli.json"
        self.diagnostic = self.workspace / "product-preparation-failure-cli.json"
        for library in P.LIBRARIES:
            for extension in ("dll", "pdb"):
                selected = self.tool / ("ForgeTrust.AppSurface.Evidence."+library+"."+extension)
                selected.write_bytes(b"private-image-content-canary")
                selected.chmod(0o644)

    def capture(self, phase="image-replacement", error=None, **kwargs):
        return P.capture_product_preparation_failure(self.tool, self.receipt, phase,
            error if error is not None else ValueError("message-path-canary-"+str(self.root)),
            expected_owner_uid=os.geteuid(), deadline=kwargs.pop("deadline", time.monotonic()+5), **kwargs)

    def test_real_closed_facts_private_mode_and_canary_absence(self):
        parent_before = self.tool.lstat()
        workspace_before = self.workspace.lstat()
        self.assertTrue(self.capture(error=PermissionError(13, "secret-message-canary")))
        raw = self.diagnostic.read_bytes()
        record = json.loads(raw)
        self.assertLessEqual(len(raw), 4096)
        self.assertEqual("issue779-private-product-preparation-failure-v1", record["schema"])
        self.assertEqual("image-replacement", record["phase"])
        self.assertEqual("PermissionError", record["error_class"])
        self.assertEqual(13, record["errno"])
        self.assertIsNone(record["completed_replacement_count"])
        self.assertGreater(record["remaining_seconds"], 0)
        self.assertEqual({"kind": "directory", "uid": os.geteuid(), "gid": parent_before.st_gid,
                          "mode": "0700", "nlink": parent_before.st_nlink, "errno": None}, record["parent"])
        self.assertEqual({"kind": "directory", "uid": os.geteuid(), "gid": workspace_before.st_gid,
                          "mode": "0700", "nlink": workspace_before.st_nlink, "errno": None}, record["workspace"])
        self.assertEqual([name+"."+ext for name in P.LIBRARIES for ext in ("dll", "pdb")],
                         [row["target"] for row in record["targets"]])
        self.assertTrue(all(row["kind"] == "regular-file" and row["mode"] == "0644"
                            and row["uid"] == os.geteuid() and row["nlink"] == 1 for row in record["targets"]))
        self.assertEqual(0o600, self.diagnostic.stat().st_mode & 0o777)
        self.assertEqual(1, self.diagnostic.stat().st_nlink)
        for canary in (str(self.root), "secret-message-canary", "private-image-content-canary"):
            self.assertNotIn(canary.encode(), raw)
        self.assertFalse(record["runtime_compatibility_claim"])
        self.assertFalse(record["native_execution"])

    def test_unsafe_tool_mode_is_observed_without_open_or_child_stat_and_original_error_survives(self):
        self.tool.chmod(0o775)
        parent_before = self.tool.lstat()
        original = ValueError("original-failure-path-canary-"+str(self.root))
        real_open, real_lstat = os.open, os.lstat
        tool_opens, child_stats = [], []
        def opened(path, *args, **kwargs):
            if str(path) == "tool-cli":
                tool_opens.append(path)
                raise AssertionError("unsafe tool must not be opened")
            return real_open(path, *args, **kwargs)
        def observed(path, *args, **kwargs):
            if str(path).startswith("ForgeTrust.AppSurface.Evidence."):
                child_stats.append(path)
                raise AssertionError("unsafe tool children must not be observed")
            return real_lstat(path, *args, **kwargs)
        with self.assertRaises(ValueError) as caught:
            try:
                raise original
            except ValueError:
                with patch.object(P.os, "open", side_effect=opened), patch.object(P.os, "lstat", side_effect=observed):
                    self.assertTrue(self.capture(error=original))
                raise
        self.assertIs(original, caught.exception)
        self.assertEqual([], tool_opens)
        self.assertEqual([], child_stats)
        raw = self.diagnostic.read_bytes()
        record = json.loads(raw)
        self.assertEqual({"kind": "directory", "uid": parent_before.st_uid, "gid": parent_before.st_gid,
                          "mode": "0775", "nlink": parent_before.st_nlink, "errno": None}, record["parent"])
        self.assertEqual("0700", record["workspace"]["mode"])
        self.assertEqual(6, len(record["targets"]))
        for row in record["targets"]:
            self.assertEqual("unavailable", row["kind"])
            self.assertTrue(all(row[field] is None for field in ("uid", "gid", "mode", "nlink", "errno")))
        for canary in (str(self.root), "original-failure-path-canary", "private-image-content-canary"):
            self.assertNotIn(canary.encode(), raw)
        self.assertEqual(0o600, self.diagnostic.stat().st_mode & 0o777)
        self.assertEqual(1, self.diagnostic.stat().st_nlink)

    def test_target_symlink_hardlink_fifo_and_missing_are_stat_only(self):
        selected = [self.tool / ("ForgeTrust.AppSurface.Evidence."+name+"."+ext)
                    for name in P.LIBRARIES for ext in ("dll", "pdb")]
        outside = self.root / "outside"
        outside.write_bytes(b"outside-sentinel")
        for path in selected[:4]:
            path.unlink()
        selected[0].symlink_to(outside)
        os.link(outside, selected[1])
        os.mkfifo(selected[2])
        with patch.object(P.os, "read", side_effect=AssertionError("target content must not be read")):
            self.assertTrue(self.capture())
        rows = json.loads(self.diagnostic.read_bytes())["targets"]
        self.assertEqual("symlink", rows[0]["kind"])
        self.assertEqual("regular-file", rows[1]["kind"])
        self.assertEqual(2, rows[1]["nlink"])
        self.assertEqual("fifo", rows[2]["kind"])
        self.assertEqual("missing", rows[3]["kind"])
        self.assertEqual(2, rows[3]["errno"])
        self.assertIsNone(rows[3]["uid"])
        self.assertEqual(b"outside-sentinel", outside.read_bytes())

    def test_exclusive_existing_file_and_symlink_preserve_first_bytes(self):
        self.assertTrue(self.capture())
        before = self.diagnostic.read_bytes()
        self.assertFalse(self.capture(phase="tool-sealing"))
        self.assertEqual(before, self.diagnostic.read_bytes())
        self.diagnostic.unlink()
        outside = self.root / "outside"
        outside.write_bytes(b"outside-sentinel")
        self.diagnostic.symlink_to(outside)
        self.assertFalse(self.capture())
        self.assertEqual(b"outside-sentinel", outside.read_bytes())

    def test_unsafe_parent_wrong_role_and_deadline_capture_nothing(self):
        self.workspace.chmod(0o720)
        self.assertFalse(self.capture())
        self.assertFalse(self.diagnostic.exists())
        self.workspace.chmod(0o700)
        self.assertFalse(P.capture_product_preparation_failure(self.tool,
            self.workspace / "product-binary-host.json", "image-replacement", ValueError(),
            deadline=time.monotonic()+5, expected_owner_uid=os.geteuid()))
        self.assertFalse(self.capture(phase="unknown-canary"))
        self.assertFalse(self.capture(deadline=time.monotonic()-1))
        self.assertFalse(self.diagnostic.exists())

    def test_capture_io_failure_closes_all_actual_fds_and_preserves_original_error(self):
        original = ValueError("original-error-canary")
        fds = []
        real_open, real_close = os.open, os.close
        def opened(*args, **kwargs):
            fd = real_open(*args, **kwargs)
            fds.append(fd)
            return fd
        def closed(fd):
            real_close(fd)
            if fd == fds[-1]:
                raise OSError(5, "close-error-canary")
        with self.assertRaises(ValueError) as caught:
            try:
                raise original
            except ValueError:
                with patch.object(P.os, "open", side_effect=opened), patch.object(P.os, "close", side_effect=closed), \
                     patch.object(P.os, "write", side_effect=OSError(5, "write-error-canary")):
                    self.assertFalse(self.capture(error=original))
                raise
        self.assertIs(original, caught.exception)
        self.assertEqual(3, len(fds))
        for fd in fds:
            with self.assertRaises(OSError):
                os.fstat(fd)
        tree = ast.parse(Path(P.__file__).read_text())
        replacement = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                           and node.name == "replace_and_inspect")
        self.assertTrue(any(isinstance(node, ast.Raise) and node.exc is None for node in ast.walk(replacement)))

    def test_all_closed_phases_and_unknown_error_class(self):
        class MessageCanaryException(Exception):
            pass
        for phase in P.PRODUCT_PREPARATION_FAILURE_PHASES:
            with self.subTest(phase=phase):
                self.assertTrue(self.capture(phase=phase, error=MessageCanaryException("raw-canary")))
                record = json.loads(self.diagnostic.read_bytes())
                self.assertEqual(phase, record["phase"])
                self.assertEqual("OtherException", record["error_class"])
                self.assertIsNone(record["errno"])
                self.diagnostic.unlink()

    def test_both_fixed_records_are_retained_as_complete_private_archive_bytes(self):
        self.assertTrue(self.capture())
        cli_bytes = self.diagnostic.read_bytes()
        host_tool = self.workspace / "tool-host"
        host_tool.mkdir(mode=0o700)
        self.assertTrue(P.capture_product_preparation_failure(host_tool,
            self.workspace / "product-binary-host.json", "tool-sealing", ValueError("secret-canary"),
            deadline=time.monotonic()+5, expected_owner_uid=os.geteuid()))
        host_path = self.workspace / "product-preparation-failure-host.json"
        host_bytes = host_path.read_bytes()
        spec = importlib.util.spec_from_file_location("private_diagnostic_retention", Path(__file__).with_name("run-qualification.py"))
        retention = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(retention)
        output = self.root / "output"
        output.mkdir(mode=0o700)
        retention.retain_diagnostics(self.workspace, output, expected_owner_uid=os.geteuid())
        archive = output / "private-diagnostics.tar"
        with tarfile.open(archive) as retained:
            for name, expected in ((self.diagnostic.name, cli_bytes), (host_path.name, host_bytes)):
                member = retained.getmember(name)
                self.assertEqual(0o600, member.mode)
                self.assertEqual(expected, retained.extractfile(member).read())
                self.assertLessEqual(member.size, 4096)


if __name__ == "__main__":
    unittest.main()
