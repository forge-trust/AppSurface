"""Protect fixture resources when cleanup cannot reserve its evidence destination."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


def load_cleanup_module():
    path = Path(__file__).with_name("cleanup-stopped-normal.py")
    spec = importlib.util.spec_from_file_location("cleanup_stopped_normal", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CleanupEvidenceReservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cleanup = load_cleanup_module()

    def test_occupied_evidence_directory_prevents_docker_and_file_removal(self):
        with tempfile.TemporaryDirectory(prefix="i888-normal-run-", dir="/private/tmp") as scratch:
            root = Path(scratch)
            fixture_file = root / "fixture-input.json"
            fixture_file.write_text("retain fixture input\n")
            receipt = root / "run-receipt.json"
            receipt.write_text(json.dumps({
                "root": str(root),
                "volume": "appsurface-issue888-normal-" + "0" * 32,
            }))
            evidence = root / "occupied-evidence"
            evidence.mkdir()
            sentinel = evidence / "existing-proof.txt"
            sentinel.write_text("retain existing evidence\n")

            argv = ["cleanup-stopped-normal.py", "--receipt", str(receipt), "--evidence", str(evidence)]
            with patch("sys.argv", argv), patch.object(self.cleanup.os, "umask"), \
                    patch.object(self.cleanup.subprocess, "run") as docker, \
                    patch.object(self.cleanup.shutil, "rmtree") as remove_tree, \
                    patch.object(self.cleanup.Path, "unlink", autospec=True) as unlink:
                with self.assertRaises(FileExistsError):
                    self.cleanup.main()

            docker.assert_not_called()
            remove_tree.assert_not_called()
            unlink.assert_not_called()
            self.assertEqual("retain fixture input\n", fixture_file.read_text())
            self.assertEqual("retain existing evidence\n", sentinel.read_text())
            self.assertEqual(str(root), json.loads(receipt.read_text())["root"])


if __name__ == "__main__":
    unittest.main()
