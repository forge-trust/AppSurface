"""Helper build selection and custody fixtures; commands are mocked, never .NET."""

import hashlib
import importlib.util
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

import binding


spec = importlib.util.spec_from_file_location("c1_publish_helpers", Path(__file__).with_name("publish-helpers.py"))
helpers = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helpers)


class HelperPublicationControls(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="c1-helper-publish-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.source = self.root / "source"
        self.source.mkdir(mode=0o700)
        self.sdk = self.root / "sdk"
        self.sdk.mkdir(mode=0o700)
        self.dotnet = self.sdk / "dotnet"
        self.dotnet.write_bytes(b"not executed")
        self.dotnet.chmod(0o755)
        self._projects = {}
        for helper_id, (relative, _) in helpers.HELPERS.items():
            project = self.source / relative
            project.parent.mkdir(mode=0o755, parents=True, exist_ok=True)
            project.write_text("fixture project")
            project.chmod(0o644)
            self._projects[helper_id] = project
        self.capture_raw = self._capture()
        self.output = self.root / "helper-output"
        self.calls = []
        self.publisher_module = __import__("publish")

    def _capture(self):
        files = {}
        for path in sorted(self.source.rglob("*.csproj")):
            relative = path.relative_to(self.source).as_posix()
            raw = path.read_bytes()
            files[relative] = {
                "sha256": hashlib.sha256(raw).hexdigest(),
                "mode": "0644",
                "git_blob": "a" * 40,
            }
        value = {"schema": "issue779-checkpoint2-source-v1", "source": "b" * 40,
                 "tree": "c" * 40, "count": len(files), "files": files}
        return binding.canonical(value)

    def fake_command(self, argv, phase, log, source_root, environment, deadline):
        self.calls.append((argv, phase, Path(log), source_root, environment, deadline))
        log = Path(log)
        log.write_bytes((phase + "\n").encode())
        log.chmod(0o600)
        if argv[1] == "publish":
            target = Path(argv[argv.index("-o") + 1])
            target.mkdir(mode=0o755)
            entry_name = Path(argv[2]).stem + ".dll"
            if Path(argv[2]).parts[-2] == "peer":
                entry_name = "NativePeerBroker.dll"
            else:
                entry_name = "NativeRootCoordinator.dll"
            (target / entry_name).write_bytes(b"managed image")
            (target / entry_name.replace(".dll", ".deps.json")).write_text("{}")
            (target / entry_name.replace(".dll", ".runtimeconfig.json")).write_text("{}")
            for path in target.iterdir():
                path.chmod(0o644)
        return {"phase": phase, "argv": argv, "exit": 0, "timed_out": False,
                "joined": True, "log_path": str(log),
                "log_sha256": hashlib.sha256(log.read_bytes()).hexdigest(),
                "log_bytes": log.stat().st_size}

    def invoke(self, selector):
        with patch.object(helpers.binding, "verify_source") as verify, \
             patch.object(self.publisher_module, "command", side_effect=self.fake_command):
            value = helpers.publish_helpers(source_root=self.source,
                capture_raw=self.capture_raw, selector=selector, sdk_root=self.sdk,
                output=self.output, deadline=time.monotonic() + 30)
        return value, verify

    def test_fixed_helper_map_and_actual_command_rows_are_returned(self):
        value, verify = self.invoke("N04")
        self.assertEqual(set(value), {"peer", "n04"})
        self.assertEqual(len(self.calls), 4)
        self.assertEqual([call[1] for call in self.calls], [
            "helper-peer-locked-restore", "helper-peer-publish",
            "helper-n04-locked-restore", "helper-n04-publish"])
        self.assertEqual(verify.call_count, 4)
        for helper_id, row in value.items():
            self.assertEqual(len(row["commands"]), 2)
            self.assertIn("-p:Configuration=Release", row["commands"][0]["argv"])
            self.assertNotIn("-c", row["commands"][0]["argv"])
            self.assertEqual(row["image"]["entry"], helpers.HELPERS[helper_id][1])
            self.assertTrue(Path(row["image"]["root"]).is_dir())
            binding.validate_helper_commands(
                helper_id, row["commands"], self.source, Path(row["image"]["root"]), set())

    def test_restore_rejects_unsupported_configuration_switch_and_missing_property(self):
        value, _ = self.invoke("None")
        row = value["peer"]
        original = row["commands"][0]["argv"][:]
        for replacement in ([], ["-c", "Release"]):
            with self.subTest(replacement=replacement):
                argv = original[:]
                index = argv.index("-p:Configuration=Release")
                argv[index:index + 1] = replacement
                row["commands"][0]["argv"] = argv
                with self.assertRaises(binding.Rejected):
                    binding.validate_helper_commands("peer", row["commands"], self.source,
                                                     Path(row["image"]["root"]), set())

    def test_fixed_helper_selection_matches_current_binding_for_every_selector(self):
        for selector in binding.SELECTORS:
            with self.subTest(selector=selector):
                self.assertEqual(set(helpers._helper_ids(selector)),
                                 binding.required_helpers(selector))

    def test_selectors_without_helpers_return_empty_without_creating_outputs(self):
        with patch.object(helpers.binding, "verify_source") as verify:
            value = helpers.publish_helpers(source_root=self.source,
                capture_raw=self.capture_raw, selector="N09", sdk_root=self.sdk,
                output=self.output, deadline=time.monotonic() + 30)
        self.assertEqual(value, {})
        verify.assert_called_once()
        self.assertEqual(self.calls, [])
        self.assertFalse(self.output.exists())

    def test_failed_restore_is_terminal_and_no_helper_image_is_invented(self):
        def failed(argv, phase, log, source_root, environment, deadline):
            log = Path(log)
            log.write_bytes(b"restore failed\n")
            log.chmod(0o600)
            return {"phase": phase, "argv": argv, "exit": 9, "timed_out": False,
                    "joined": True, "log_path": str(log),
                    "log_sha256": hashlib.sha256(log.read_bytes()).hexdigest(),
                    "log_bytes": log.stat().st_size}

        with patch.object(helpers.binding, "verify_source"), \
             patch.object(self.publisher_module, "command", side_effect=failed):
            with self.assertRaisesRegex(binding.Rejected, "^helper-command-terminal$"):
                helpers.publish_helpers(source_root=self.source,
                    capture_raw=self.capture_raw, selector="N05", sdk_root=self.sdk,
                    output=self.output, deadline=time.monotonic() + 30)
        self.assertFalse((self.output / "peer").exists())

    def test_existing_output_is_preserved_and_never_dispatched(self):
        self.output.mkdir(mode=0o700)
        sentinel = self.output / "sentinel"
        sentinel.write_bytes(b"owned")
        with patch.object(helpers.binding, "verify_source"):
            with self.assertRaisesRegex(binding.Rejected, "^helper-output-fresh$"):
                helpers.publish_helpers(source_root=self.source,
                    capture_raw=self.capture_raw, selector="N05", sdk_root=self.sdk,
                    output=self.output, deadline=time.monotonic() + 30)
        self.assertEqual(sentinel.read_bytes(), b"owned")
        self.assertEqual(self.calls, [])


if __name__ == "__main__":
    unittest.main()
