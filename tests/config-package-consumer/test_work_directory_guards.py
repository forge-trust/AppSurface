"""Public CLI regressions for packed consumer input and output path guards.

Run: python3 -m unittest discover -s tests/config-package-consumer -p 'test_*.py'
"""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


VERIFIER = Path(__file__).resolve().parents[2] / "scripts/verify_config_package_consumer.py"


class ConsumerOutputGuardTests(unittest.TestCase):
    def assert_rejected_without_mutation(self, kind, destination="pinned-inventory.md"):
        with tempfile.TemporaryDirectory(prefix="config-consumer-guard-") as directory:
            root = Path(directory)
            work = root / "work"
            work.mkdir()
            artifacts = root / "artifacts"
            artifacts.mkdir()
            blocked_commands = root / "empty-path"
            blocked_commands.mkdir()
            inventory = work / destination
            outside = root / "outside-sentinel.md"
            sentinel = b"caller-owned sentinel\n"
            if kind == "file":
                inventory.write_bytes(sentinel)
            elif kind == "directory":
                inventory.mkdir()
            elif kind == "symlink":
                outside.write_bytes(sentinel)
                inventory.symlink_to(outside)
            else:
                inventory.symlink_to(outside)
            before = sorted(path.name for path in work.iterdir())
            result = subprocess.run(
                [sys.executable, str(VERIFIER), "--artifacts", str(artifacts),
                 "--work-directory", str(work)],
                env={**os.environ, "PATH": str(blocked_commands)},
                capture_output=True,
                text=True,
                timeout=15,
                check=False,
            )

            if kind == "symlink":
                self.assertTrue(outside.read_bytes() == sentinel, "Outside sentinel was overwritten.")
            elif kind == "dangling-symlink":
                self.assertFalse(outside.exists(), "A dangling symlink created an outside file.")
            elif kind == "file":
                self.assertTrue(inventory.read_bytes() == sentinel, "Existing output was overwritten.")
            else:
                self.assertTrue(inventory.is_dir())
            self.assertEqual(before, sorted(path.name for path in work.iterdir()),
                             "Guard rejection created verifier outputs.")
            self.assertEqual(1, result.returncode)
            self.assertEqual("", result.stdout)
            self.assertIn("Verifier-owned fixture or cache path already exists", result.stderr)

    def test_existing_inventory_file_is_preserved(self):
        self.assert_rejected_without_mutation("file")

    def test_existing_inventory_directory_is_rejected_before_scaffolding(self):
        self.assert_rejected_without_mutation("directory")

    def test_inventory_symlink_cannot_overwrite_an_outside_file(self):
        self.assert_rejected_without_mutation("symlink")

    def test_dangling_inventory_symlink_cannot_create_an_outside_file(self):
        self.assert_rejected_without_mutation("dangling-symlink")


    def test_evidence_and_stage_log_destinations_are_preserved_before_scaffolding(self):
        destinations = (
            "evidence.json", "checkout-head.log", "sdk.log",
            "pack-ForgeTrust.AppSurface.Core.log", "pack-ForgeTrust.AppSurface.Config.log",
            "pack-ForgeTrust.AppSurface.Config.Testing.log", "domain-restore.log",
            "pack-domain-fixture.log", "restore.log", "build.log", "baseline-run.log",
            "file-run.log", "override-run.log",
        )
        for destination in destinations:
            for kind in ("file", "directory", "symlink", "dangling-symlink"):
                with self.subTest(destination=destination, kind=kind):
                    self.assert_rejected_without_mutation(kind, destination)


class ConsumerArtifactInputGuardTests(unittest.TestCase):
    def test_invalid_artifact_inputs_are_rejected_before_creating_any_output(self):
        for kind in ("missing", "file", "symlink-to-file", "dangling-symlink"):
            for explicit_work in (False, True):
                with self.subTest(kind=kind, explicit_work=explicit_work):
                    with tempfile.TemporaryDirectory(prefix="config-consumer-input-") as directory:
                        root = Path(directory)
                        artifacts = root / "artifacts"
                        outside = root / "outside-sentinel"
                        sentinel = b"caller-owned package input\n"
                        if kind == "file":
                            artifacts.write_bytes(sentinel)
                        elif kind == "symlink-to-file":
                            outside.write_bytes(sentinel)
                            artifacts.symlink_to(outside)
                        elif kind == "dangling-symlink":
                            artifacts.symlink_to(outside, target_is_directory=True)
                        temporary_outputs = root / "temporary-outputs"
                        temporary_outputs.mkdir()
                        blocked_commands = root / "empty-path"
                        blocked_commands.mkdir()
                        work = root / "work"
                        command = [sys.executable, str(VERIFIER), "--artifacts", str(artifacts)]
                        if explicit_work:
                            command.extend(["--work-directory", str(work)])
                        before = sorted(path.name for path in root.iterdir())
                        result = subprocess.run(
                            command,
                            env={**os.environ, "PATH": str(blocked_commands),
                                 "TMPDIR": str(temporary_outputs), "TEMP": str(temporary_outputs),
                                 "TMP": str(temporary_outputs)},
                            capture_output=True,
                            text=True,
                            timeout=15,
                            check=False,
                        )

                        self.assertEqual(1, result.returncode)
                        self.assertEqual("", result.stdout)
                        self.assertIn("--artifacts must name an existing package directory", result.stderr)
                        self.assertEqual(before, sorted(path.name for path in root.iterdir()),
                                         "Invalid input created caller or verifier paths.")
                        self.assertEqual([], list(temporary_outputs.iterdir()),
                                         "Invalid input created a default work directory.")
                        self.assertFalse(work.exists(), "Invalid input created an explicit work directory.")
                        if kind == "file":
                            self.assertEqual(sentinel, artifacts.read_bytes())
                        elif kind == "symlink-to-file":
                            self.assertEqual(sentinel, outside.read_bytes())
                            self.assertTrue(artifacts.is_symlink())
                        else:
                            self.assertFalse(artifacts.exists())
                            self.assertFalse(outside.exists())
                            if kind == "dangling-symlink":
                                self.assertTrue(artifacts.is_symlink())


if __name__ == "__main__":
    unittest.main()
