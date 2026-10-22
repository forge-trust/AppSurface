"""Private measurement receipt controls: detached metadata and ordinary files only."""

import copy
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

import binding
from test_binding import build_model, HEAD, SHA, OTHER


def model():
    original, capture = build_model("N10")
    for name in binding.MEASUREMENT_NAMES:
        original["tool"]["files"][name] = {"sha256": SHA, "bytes": 19, "mode": "0644"}
    original["tool"]["files"]["configuration.json"] = {"sha256": SHA, "bytes": 2, "mode": "0644"}
    value = copy.deepcopy(original)
    value["schema"] = "issue779-checkpoint1-build-v2"
    for name in binding.MEASUREMENT_NAMES:
        value["tool"]["files"][name]["sha256"] = OTHER
    value["measurement"] = {
        "schema": "issue779-c1-measurement-build-v1", "original_build_path": "/data/logs/build.json",
        "original_build_sha256": binding.digest(binding.canonical(original)),
        "original_tool": copy.deepcopy(original["tool"]),
        "instrumented_modules": {name: OTHER for name in binding.MEASUREMENT_NAMES},
        "collector_path": "/data/collector" + binding.MEASUREMENT_COLLECTOR_SUFFIX,
        "collector_sha256": SHA,
    }
    return original, value, capture


class MeasuredBuildDataTests(unittest.TestCase):
    def test_exact_six_instrumented_modules_keep_original_commands_and_no_native_authority(self):
        original, value, capture = model()
        actual = binding.validate_build(value, capture, HEAD, "N10")
        self.assertEqual(HEAD, actual["source"])
        self.assertEqual(original["commands"], value["commands"])
        self.assertFalse(value["native_execution"])
        self.assertEqual(12, len(value["measurement"]["instrumented_modules"]))
        # Coverlet may leave portable PDB bytes unchanged; every managed DLL
        # still must differ from its original publication before staging.
        for name in binding.MEASUREMENT_NAMES:
            if name.endswith(".pdb"):
                value["tool"]["files"][name]["sha256"] = SHA
                value["measurement"]["instrumented_modules"][name] = SHA
        binding.validate_build(value, capture, HEAD, "N10")

    def test_unknown_fields_and_nested_measurement_schema_cannot_select_an_alternate_loader(self):
        for where in ("build", "measurement"):
            _, value, capture = model()
            (value if where == "build" else value["measurement"])["untrusted_target"] = "secret-canary"
            with self.subTest(where=where), self.assertRaises(binding.Rejected):
                binding.validate_build(value, capture, HEAD, "N10")
        for field, bad in (("schema", "other"), ("original_build_path", "/data/elsewhere/build.json"),
                           ("collector_path", "/data/collector.dll"), ("collector_sha256", True),
                           ("original_build_sha256", "raw-canary")):
            _, value, capture = model()
            value["measurement"][field] = bad
            with self.subTest(field=field), self.assertRaises(binding.Rejected):
                binding.validate_build(value, capture, HEAD, "N10")

    def test_only_selected_dll_and_pdb_bytes_may_change_and_every_dll_must_be_instrumented(self):
        for mutation in ("unselected", "module-mode", "root", "directory", "missing-module",
                         "extra-map", "missing-map", "wrong-map", "unchanged-dll"):
            _, value, capture = model()
            name = "ForgeTrust.AppSurface.Cli.dll"
            if mutation == "unselected": value["tool"]["files"]["configuration.json"]["sha256"] = OTHER
            elif mutation == "module-mode": value["tool"]["files"][name]["mode"] = "0755"
            elif mutation == "root": value["measurement"]["original_tool"]["root"] = "/data/other-tool"
            elif mutation == "directory": value["measurement"]["original_tool"]["directories"]["nested"] = "0755"
            elif mutation == "missing-module": del value["tool"]["files"]["ForgeTrust.AppSurface.Evidence.Planner.pdb"]
            elif mutation == "extra-map": value["measurement"]["instrumented_modules"]["extra.dll"] = OTHER
            elif mutation == "missing-map": del value["measurement"]["instrumented_modules"][name]
            elif mutation == "wrong-map": value["measurement"]["instrumented_modules"][name] = SHA
            elif mutation == "unchanged-dll":
                value["tool"]["files"][name]["sha256"] = SHA
                value["measurement"]["instrumented_modules"][name] = SHA
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                binding.validate_build(value, capture, HEAD, "N10")

    def test_original_source_selection_commands_and_terminal_guards_still_apply(self):
        for mutation in ("source", "capture", "selector", "command-exit", "command-argv", "native"):
            _, value, capture = model()
            if mutation == "source": value["source"] = "6" * 40
            elif mutation == "capture": value["source_after_capture_sha256"] = OTHER
            elif mutation == "selector": value["selector"] = "None"
            elif mutation == "command-exit": value["commands"][1]["exit"] = 1
            elif mutation == "command-argv": value["commands"][1]["argv"].append("--no-build")
            elif mutation == "native": value["native_execution"] = True
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                binding.validate_build(value, capture, HEAD, "N10")


class MeasuredBuildFileTests(unittest.TestCase):
    def test_actual_original_receipt_and_collector_are_read_before_any_image_is_verified(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original, value, capture = model()
            for row in original["commands"]:
                row["log_path"] = str(root / (row["phase"] + ".log"))
                body = (row["phase"] + " actual ordinary log\n").encode()
                Path(row["log_path"]).write_bytes(body)
                row["log_sha256"] = binding.digest(body)
                row["log_bytes"] = len(body)
            value["commands"] = copy.deepcopy(original["commands"])
            original_path = root / "build.json"
            original_path.write_bytes(binding.canonical(original))
            collector = root / ("collector" + binding.MEASUREMENT_COLLECTOR_SUFFIX)
            collector.parent.mkdir(parents=True)
            collector.write_bytes(b"ordinary pinned collector bytes, not an executable fixture")
            measurement = value["measurement"]
            measurement.update(original_build_path=str(original_path),
                original_build_sha256=binding.digest(original_path.read_bytes()),
                collector_path=str(collector), collector_sha256=binding.digest(collector.read_bytes()))
            end = time.monotonic() + 10
            with patch.object(binding, "verify_source") as verify_source, patch.object(binding, "verify_image") as verify_image:
                binding.verify_build(value, capture, HEAD, "N10", deadline=end)
                verify_source.assert_called_once()
                self.assertEqual(2, verify_image.call_count)
            # Neither a stale receipt hash nor same-shape substituted collector
            # reaches source/image verification. These leaves confer no authority.
            for path in (original_path, collector):
                saved = path.read_bytes()
                path.write_bytes(saved + b"changed")
                with self.subTest(path=path.name), patch.object(binding, "verify_source") as verify_source, \
                        patch.object(binding, "verify_image") as verify_image, self.assertRaises(binding.Rejected):
                    binding.verify_build(value, capture, HEAD, "N10", deadline=end)
                verify_source.assert_not_called()
                verify_image.assert_not_called()
                path.write_bytes(saved)
            # A changed original command cannot be hidden by updating its outer
            # raw hash: it must equal the measured receipt's publication facts.
            altered = copy.deepcopy(original)
            altered["commands"][0]["log_bytes"] += 1
            original_path.write_bytes(binding.canonical(altered))
            measurement["original_build_sha256"] = binding.digest(original_path.read_bytes())
            with patch.object(binding, "verify_source") as verify_source, \
                    patch.object(binding, "verify_image") as verify_image, self.assertRaises(binding.Rejected):
                binding.verify_build(value, capture, HEAD, "N10", deadline=end)
            verify_source.assert_not_called()
            verify_image.assert_not_called()


if __name__ == "__main__":
    unittest.main()
