#!/usr/bin/env python3
"""Portable descriptor controls for the root execution-broker fixture."""
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import struct
import tempfile
import unittest
from unittest.mock import Mock, call, patch
import xml.etree.ElementTree as ET

source = Path(__file__).with_name("test_execution_broker.py")
spec = importlib.util.spec_from_file_location("execution_broker_fixture", source)
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class DescriptorTests(unittest.TestCase):
    def test_peer_pin_creates_one_immutable_root_selected_control_snapshot(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-control-") as temporary:
            root = Path(temporary)
            output = root / "output"
            output.mkdir()
            control = root / "control"
            (control / "broker").mkdir(parents=True)
            scenario = broker.Scenario("cli-coverage", root, root / "tool", root / "subject",
                                       output, 1001, 1002, 1003, 1004, "/usr/bin/dotnet",
                                       root / "tool" / "policy.json", "a" * 64,
                                       root / "operations.jsonl", control / "broker" / "control.sock")
            peer = Mock()
            peer.getsockopt.return_value = struct.pack("3i", 12345, 1001, 1002)
            snapshot = control / "worker-control.json"
            self.assertFalse(snapshot.exists())
            # The credential bytes are a portable fixture control. macOS does not
            # expose the Linux SO_PEERCRED constant; actual kernel peers remain native-only.
            with patch.object(broker.socket, "SO_PEERCRED", 17, create=True), patch.object(broker.os, "chown") as chown:
                self.assertEqual((12345, 1001, 1002), scenario.pin_peer(peer))
                self.assertEqual((12345, 1001, 1002), scenario.pin_peer(peer))
            captured = json.loads(snapshot.read_bytes())
            self.assertEqual(scenario.descriptor, captured)
            self.assertEqual(os.getpid(), captured["broker_pid"])
            self.assertEqual(str(snapshot), captured["descriptor_path"])
            self.assertEqual(str(control / "broker" / "control.sock"), captured["socket_path"])
            self.assertEqual(0o440, stat.S_IMODE(snapshot.stat().st_mode))
            self.assertEqual(2, chown.call_count, "A repeated identical peer must reuse the retained snapshot.")
            self.assertIn(call(snapshot, 0, 1002, follow_symlinks=False), chown.call_args_list)

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
                                               root / f"{name}.jsonl", root / name / "broker" / "control.sock")
                    descriptor = scenario.make_descriptor((12345, 1001, 1002))
                    self.assertEqual(f"fixture-{root.name}-{name}/1", descriptor["run_id"])
                    self.assertNotIn(descriptor["run_id"], run_ids)
                    run_ids.add(descriptor["run_id"])
                    self.assertEqual((12345, 1001, 1002),
                                     (descriptor["worker_pid"], descriptor["worker_uid"], descriptor["worker_gid"]))
                    self.assertEqual(scenario.parent_identity, descriptor["output_parent_identity"])
                    self.assertEqual(str(root / name / "broker" / "control.sock"), descriptor["socket_path"])
                    self.assertEqual(os.getpid(), descriptor["broker_pid"])
                    self.assertEqual(str(root / name / "worker-control.json"), descriptor["descriptor_path"])
                    self.assertEqual("b" * 40, descriptor["base_revision"])
                    self.assertEqual("c" * 40, descriptor["subject_revision"])
                    self.assertIsNone(descriptor["diff_file"])
                    self.assertIsNone(descriptor["diff_sha256"])
            self.assertEqual(len(broker.SCENARIOS), len(run_ids))


class CoverageReportTests(unittest.TestCase):
    def test_positive_report_has_covered_line_and_branch_items_matching_declared_totals(self):
        report = ET.fromstring(broker.REPORT)
        lines = report.findall("./packages/package/classes/class/lines/line")
        self.assertGreater(len(lines), 0, "ReportGenerator requires actual line items to retain valid coverage.")
        covered_lines = sum(int(line.attrib["hits"]) > 0 for line in lines)
        self.assertEqual(len(lines), covered_lines)
        covered_branches = valid_branches = 0
        for line in lines:
            if line.get("branch") == "true":
                condition = re.fullmatch(r"100% \((\d+)/(\d+)\)", line.attrib["condition-coverage"])
                self.assertIsNotNone(condition)
                covered, valid = map(int, condition.groups())
                self.assertGreater(valid, 0)
                self.assertEqual(valid, covered)
                covered_branches += covered
                valid_branches += valid
        self.assertGreater(valid_branches, 0)
        self.assertEqual(len(lines), int(report.attrib["lines-valid"]))
        self.assertEqual(covered_lines, int(report.attrib["lines-covered"]))
        self.assertEqual(valid_branches, int(report.attrib["branches-valid"]))
        self.assertEqual(covered_branches, int(report.attrib["branches-covered"]))


class CoverageAncestorPermissionsTests(unittest.TestCase):
    def test_worker_can_read_and_search_ancestors_while_distinct_subject_has_no_access(self):
        with tempfile.TemporaryDirectory(prefix="execution-broker-permissions-") as temporary:
            root = Path(temporary)
            worker_root = root / "worker"
            worker_root.mkdir()
            worker_gid = 1002
            # Exercise real chmod/stat; only privileged ownership assignment is recorded.
            with patch.object(broker.os, "chown") as chown:
                broker.configure_coverage_ancestors(root, worker_root, worker_gid)
            self.assertEqual([
                call(root, 0, worker_gid, follow_symlinks=False),
                call(worker_root, 0, worker_gid, follow_symlinks=False),
            ], chown.call_args_list)
            for ancestor in (root, worker_root):
                with self.subTest(ancestor=ancestor.name):
                    mode = stat.S_IMODE(ancestor.stat().st_mode)
                    self.assertEqual(stat.S_IRGRP | stat.S_IXGRP, mode & stat.S_IRWXG)
                    self.assertEqual(0, mode & stat.S_IRWXO,
                                     "The distinct non-root subject must have no ancestor access.")


if __name__ == "__main__":
    unittest.main()
