"""Current inventory pins are checked against every current port without dispatch."""

import importlib.util
import json
from pathlib import Path
import time
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("c1_inventory_hash_driver", ROOT / "run-native.py")
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class CurrentPortedInventoryTests(unittest.TestCase):
    def test_production_inventory_loader_checks_every_ported_current_file(self):
        raw, inventory = driver.inventory_raw(time.monotonic() + 10)

        self.assertEqual(raw, (ROOT / "procedures/inventory.json").read_bytes())
        ported = [row for row in inventory["files"] if row.get("ported_path") is not None]
        self.assertGreater(len(ported), 0)
        # inventory_raw is the same source-owned check used by prepare and run;
        # it rejects stale digests, aliases, and duplicate port paths before
        # any selector can dispatch native work.
        paths = [row["ported_path"] for row in ported]
        self.assertEqual(len(paths), len(set(paths)))

    def test_stale_port_pin_is_rejected_before_any_dispatch(self):
        original_read = driver.binding.read
        inventory_path = ROOT / "procedures/inventory.json"
        inventory = json.loads(inventory_path.read_bytes())
        row = next(item for item in inventory["files"]
                   if item.get("ported_path") == "procedures/slot/checkpoint-n05.sh")
        row["ported_sha256"] = "a8450c625bd8eeeae70b2bc9856227b0d4c2d33f7b854888b7308045c7058e7b"

        def read(path, *args, **kwargs):
            if Path(path) == inventory_path:
                return json.dumps(inventory, separators=(",", ":")).encode()
            return original_read(path, *args, **kwargs)

        with patch.object(driver.binding, "read", side_effect=read):
            with self.assertRaises(driver.binding.Rejected) as rejected:
                driver.inventory_raw(time.monotonic() + 10)
        self.assertEqual(str(rejected.exception), "procedure-file-binding")


if __name__ == "__main__":
    unittest.main()
