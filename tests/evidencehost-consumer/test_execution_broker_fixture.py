#!/usr/bin/env python3
"""Portable descriptor controls for the root execution-broker fixture."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

source = Path(__file__).with_name("test_execution_broker.py")
spec = importlib.util.spec_from_file_location("execution_broker_fixture", source)
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class DescriptorTests(unittest.TestCase):
    def test_every_scenario_creates_a_run_bound_to_its_root_and_authenticated_peer(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-") as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            run_ids = set()
            for name in broker.SCENARIOS:
                with self.subTest(scenario=name):
                    scenario = broker.Scenario(name, root, root / "tool", root / "subject",
                                               output, 1001, 1002, 1003, 1004, "/usr/bin/dotnet",
                                               root / "policy.json", "a" * 64,
                                               root / f"{name}.jsonl", root / f"{name}.sock")
                    descriptor = scenario.make_descriptor((12345, 1001, 1002))
                    self.assertEqual(f"fixture-{root.name}-{name}/1", descriptor["run_id"])
                    self.assertNotIn(descriptor["run_id"], run_ids)
                    run_ids.add(descriptor["run_id"])
                    self.assertEqual((12345, 1001, 1002),
                                     (descriptor["worker_pid"], descriptor["worker_uid"], descriptor["worker_gid"]))
                    self.assertEqual(scenario.parent_identity, descriptor["output_parent_identity"])
                    self.assertEqual(str(root / f"{name}.sock"), descriptor["socket_path"])
            self.assertEqual(len(broker.SCENARIOS), len(run_ids))


if __name__ == "__main__":
    unittest.main()
