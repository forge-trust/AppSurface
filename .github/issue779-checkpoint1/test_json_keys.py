"""Case-sensitive Linux map controls; detached data only, with no native launch.

SOURCE44_IMAGE is a small projection of actual rows in the verified None build
receipt SHA256 edf7491b5060f0d314b6bdbd7ee4dd4948989fa3142a0dc67d155c818bdcfc2f.
It is not a full image or a source/build receipt and grants no acceptance credit.
"""

import copy
import importlib.util
import json
from pathlib import Path
import unittest

import binding


ROOT = Path(__file__).resolve().parent


def load_bridge(name, relative):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


BRIDGES = (
    load_bridge("slot_json_keys", "procedures/slot/bridge.py"),
    load_bridge("stall_json_keys", "procedures/n11/bridge.py"),
)

SOURCE44_IMAGE = b'''{
  "root":"/home/runner/work/_temp/checkpoint1-None-inputs/tool",
  "entry":"ForgeTrust.AppSurface.Cli.dll",
  "directories":{"Assets":"0755","Assets/Pwa":"0755","assets":"0755"},
  "files":{
    "Assets/Pwa/pwa-path-vectors.json":{
      "bytes":1295,"mode":"0644",
      "sha256":"d12dd92dcbc548f2ade576b95cf015e65d67b7d6b04f21fb4fbe3c701efd616e"},
    "ForgeTrust.AppSurface.Cli.dll":{
      "bytes":974848,"mode":"0644",
      "sha256":"08666a5202e9d95e7e8d88a87d2b301dd342824fbc8feb110bbf8b4c0a8ded00"},
    "assets/package.json":{
      "bytes":409,"mode":"0644",
      "sha256":"d2508d1bc2a6724cbffdf80dce15682d7e052b34cc293f9530f309ea79867fc0"}
  }
}'''


class LinuxJsonKeyControls(unittest.TestCase):
    def test_actual_source44_map_projection_keeps_both_directory_names(self):
        expected = json.loads(SOURCE44_IMAGE)
        self.assertEqual({"Assets", "Assets/Pwa", "assets"}, set(expected["directories"]))
        for bridge in BRIDGES:
            with self.subTest(bridge=bridge.__name__):
                actual = bridge.decode(SOURCE44_IMAGE)
                self.assertEqual(expected, actual)
                self.assertIs(actual, binding.validate_image(actual, expected["entry"]))
                self.assertEqual(actual, bridge.decode(bridge.canonical(actual)))

    def test_case_distinct_file_paths_preserve_independent_data(self):
        image = json.loads(SOURCE44_IMAGE)
        # Extend only detached data: the actual projection above is unmodified.
        image["files"]["Assets/package.json"] = {
            "bytes": 7, "mode": "0644", "sha256": "a" * 64}
        raw = binding.canonical(image)
        for bridge in BRIDGES:
            with self.subTest(bridge=bridge.__name__):
                actual = bridge.decode(raw)
                binding.validate_image(actual, image["entry"])
                self.assertEqual(7, actual["files"]["Assets/package.json"]["bytes"])
                self.assertEqual(409, actual["files"]["assets/package.json"]["bytes"])
                self.assertNotEqual(actual["files"]["Assets/package.json"],
                                    actual["files"]["assets/package.json"])

    def test_exact_directory_duplicates_reject_even_beside_distinct_case(self):
        good = b'{"directories":{"Assets":"0755","assets":"0555"}}'
        bad = b'{"directories":{"Assets":"0755","assets":"0555","Assets":"0755"}}'
        for bridge in BRIDGES:
            with self.subTest(bridge=bridge.__name__):
                self.assertEqual(2, len(bridge.decode(good)["directories"]))
                with self.assertRaisesRegex(ValueError, "json-member$"):
                    bridge.decode(bad)

    def test_exact_file_duplicates_reject_in_nested_objects(self):
        good = b'{"files":{"Assets/x":{"bytes":1},"assets/x":{"bytes":2}}}'
        bad = b'{"files":{"Assets/x":{"bytes":1},"Assets/x":{"bytes":2}}}'
        nested = b'{"files":{"Assets/x":{"bytes":1,"bytes":1}}}'
        for bridge in BRIDGES:
            with self.subTest(bridge=bridge.__name__):
                self.assertEqual(2, len(bridge.decode(good)["files"]))
                for raw in (bad, nested):
                    with self.assertRaisesRegex(ValueError, "json-member$"):
                        bridge.decode(raw)

    def test_exact_and_escape_equivalent_duplicate_root_names_reject(self):
        for bridge in BRIDGES:
            for raw in (b'{"root":"/one","root":"/two"}',
                        b'{"root":"/one","r\\u006fot":"/two"}',
                        b'[{"root":"/one","root":"/one"}]'):
                with self.subTest(bridge=bridge.__name__, raw=raw):
                    with self.assertRaisesRegex(ValueError, "json-member$"):
                        bridge.decode(raw)

    def test_closed_image_fields_still_reject_case_aliases(self):
        expected = json.loads(SOURCE44_IMAGE)
        for bridge in BRIDGES:
            binding.validate_image(bridge.decode(SOURCE44_IMAGE), expected["entry"])
            for field in ("root", "entry", "files", "directories"):
                for retain_original in (False, True):
                    image = copy.deepcopy(expected)
                    image[field.upper()] = image[field]
                    if not retain_original:
                        del image[field]
                    with self.subTest(bridge=bridge.__name__, field=field, extra=retain_original):
                        decoded = bridge.decode(binding.canonical(image))
                        with self.assertRaisesRegex(binding.Rejected, "image-fields$"):
                            binding.validate_image(decoded, expected["entry"])

    def test_file_row_aliases_and_typed_hash_byte_mode_bounds_remain_closed(self):
        expected = json.loads(SOURCE44_IMAGE)
        entry = expected["entry"]
        changes = (("bytes", True), ("bytes", -1), ("bytes", binding.MAX_FILE + 1),
                   ("sha256", "F" * 64), ("sha256", "f" * 63), ("mode", "0744"))
        for bridge in BRIDGES:
            binding.validate_image(bridge.decode(SOURCE44_IMAGE), entry)
            for field, bad in changes:
                image = copy.deepcopy(expected)
                image["files"][entry][field] = bad
                with self.subTest(bridge=bridge.__name__, field=field, bad=bad):
                    with self.assertRaises(binding.Rejected):
                        binding.validate_image(bridge.decode(binding.canonical(image)), entry)
            image = copy.deepcopy(expected)
            image["files"][entry]["Bytes"] = image["files"][entry]["bytes"]
            with self.assertRaisesRegex(binding.Rejected, "image-file-fields$"):
                binding.validate_image(bridge.decode(binding.canonical(image)), entry)
            image = copy.deepcopy(expected)
            image["directories"]["assets"] = "0777"
            with self.assertRaisesRegex(binding.Rejected, "image-directory-mode$"):
                binding.validate_image(bridge.decode(binding.canonical(image)), entry)

    def test_nonfinite_json_numbers_remain_rejected(self):
        for bridge in BRIDGES:
            for number in (b"NaN", b"Infinity", b"-Infinity"):
                with self.subTest(bridge=bridge.__name__, number=number):
                    with self.assertRaisesRegex(ValueError, "json-number$"):
                        bridge.decode(b'{"bytes":' + number + b'}')


if __name__ == "__main__":
    unittest.main()
