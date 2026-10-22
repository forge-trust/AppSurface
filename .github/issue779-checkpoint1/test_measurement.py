"""Detached measurement grammar and real ordinary-file controls, never native proof."""

import argparse
import copy
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

import binding
import measurement as subject
from test_binding import build_model, HEAD, SHA, OTHER


def modules():
    value = {name + ".dll": {} for name in binding.MEASUREMENT_MODULES}
    value[binding.MEASUREMENT_MODULES[0] + ".dll"] = {
        "/src/Example.cs": {"Example": {"System.Void Example::Run()": {
            "Lines": {"12": 1, "13": 0}, "Branches": [
                {"Line": 13, "Offset": 0, "EndOffset": 3, "Path": 0, "Ordinal": 0, "Hits": 0}]}}}}
    return value


def xml():
    return (b'<coverage lines-covered="1" lines-valid="2" branches-covered="0" branches-valid="1">'
            b'<packages><package><classes><class><lines><line number="12" hits="1"/>'
            b'</lines></class></classes></package></packages></coverage>')


def measured_model():
    original, capture = build_model("N10")
    for name in binding.MEASUREMENT_NAMES:
        original["tool"]["files"][name] = {"sha256": SHA, "bytes": 19, "mode": "0644"}
    original["tool"]["files"]["unchanged.json"] = {"sha256": SHA, "bytes": 2, "mode": "0644"}
    actual = copy.deepcopy(original["tool"])
    for name in binding.MEASUREMENT_NAMES:
        if name.endswith(".dll"):
            actual["files"][name]["sha256"] = OTHER
    value = subject.measured_build(original, binding.digest(binding.canonical(original)), actual,
        Path("/collector" + binding.MEASUREMENT_COLLECTOR_SUFFIX), OTHER)
    return original, value, capture


class MeasurementDataTests(unittest.TestCase):
    def test_command_clocks_preserve_both_native_limits_and_post_run_verification_reserve(self):
        for phase, allowance, reserve in (("prepare", 150, 960), ("run", 900, 60)):
            with self.subTest(phase=phase):
                end = 5000.0
                now = end - allowance - reserve
                self.assertEqual(now + allowance, subject.command_deadline(end, phase, now))
                self.assertEqual(reserve, end - subject.command_deadline(end, phase, now))
                for late in (0.001, 1.0, float(allowance)):
                    with self.assertRaises(binding.Rejected):
                        subject.command_deadline(end, phase, now + late)
        with self.assertRaises(binding.Rejected):
            subject.command_deadline(5000.0, "unknown", 0.0)

    def test_standard_json_keeps_zero_and_hit_partial_branches_unchanged(self):
        value = modules()
        raw = binding.canonical(value)
        self.assertEqual(value, subject.report_json(raw))
        self.assertEqual(raw, binding.canonical(value))

    def test_missing_or_duplicate_basename_module_rejects(self):
        good = modules()
        for mutation in ("missing", "alias", "unknown"):
            value = copy.deepcopy(good)
            first = next(iter(value))
            if mutation == "missing":
                del value[first]
            elif mutation == "alias":
                value["/elsewhere/" + first] = value.pop(list(value)[-1])
            else:
                value["other.dll"] = value.pop(first)
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                subject.report_json(binding.canonical(value))

    def test_numeric_boolean_negative_and_float_counters_reject(self):
        for bad in (True, False, -1, 1.5, "1", 2**63):
            value = modules()
            data = next(iter(next(iter(next(iter(value.values())).values())).values()))
            method = next(iter(data.values()))
            for target in ("line", "branch"):
                changed = copy.deepcopy(value)
                item = next(iter(next(iter(next(iter(next(iter(changed.values())).values())).values())).values()))
                if target == "line":
                    item["Lines"]["12"] = bad
                else:
                    item["Branches"][0]["Hits"] = bad
                with self.subTest(bad=bad, target=target), self.assertRaises(binding.Rejected):
                    subject.report_json(binding.canonical(changed))

    def test_missing_extra_branch_and_wrong_method_shapes_reject(self):
        for mutation in ("branch-field", "branch-extra", "method-extra", "method-list", "documents"):
            value = modules()
            item = next(iter(next(iter(next(iter(next(iter(value.values())).values())).values())).values()))
            if mutation == "branch-field":
                del item["Branches"][0]["Hits"]
            elif mutation == "branch-extra":
                item["Branches"][0]["canary"] = 1
            elif mutation == "method-extra":
                item["canary"] = "not-public"
            elif mutation == "method-list":
                item["Lines"] = []
            else:
                value[next(iter(value))] = []
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                subject.report_json(binding.canonical(value))

    def test_invalid_line_labels_and_duplicate_json_members_reject(self):
        for label in ("0", "-1", "1.5", "١", str(2**31)):
            value = modules()
            item = next(iter(next(iter(next(iter(next(iter(value.values())).values())).values())).values()))
            item["Lines"] = {label: 0}
            with self.subTest(label=label), self.assertRaises(binding.Rejected):
                subject.report_json(binding.canonical(value))
        for raw in (b'{"x":1,"x":2}', b'{"x":NaN}', b'\xff', b'[]'):
            with self.subTest(raw_type=len(raw)), self.assertRaises(binding.Rejected):
                subject.report_json(raw)

    def test_report_bound_applies_before_decode(self):
        with patch.object(subject, "REPORT_LIMIT", 3), self.assertRaises(binding.Rejected):
            subject.report_json(b"{}  ")

    def test_standard_cobertura_is_returned_without_mutating_bytes(self):
        raw = xml()
        self.assertEqual("coverage", subject.report_xml(raw).tag)
        self.assertEqual(xml(), raw)

    def test_xml_entities_doctype_wrong_root_and_missing_numeric_fields_reject(self):
        for raw in (b'<!DOCTYPE coverage SYSTEM "file:///canary">' + xml(),
                    b'<!DOCTYPE coverage [<!ENTITY x "canary">]>' + xml(), b'<other/>',
                    xml().replace(b'lines-valid="2"', b'lines-valid="false"'),
                    xml().replace(b'hits="1"', b'hits="-1"'), b'\xff', b'\x00'):
            with self.subTest(bytes_count=len(raw)), self.assertRaises(binding.Rejected):
                subject.report_xml(raw)

    def test_v2_projection_preserves_original_and_all_twelve_actual_hashes(self):
        original, measured, capture = measured_model()
        binding.validate_build(measured, capture, HEAD, "N10")
        self.assertEqual(original["commands"], measured["commands"])
        self.assertEqual(original["tool"], measured["measurement"]["original_tool"])
        self.assertEqual(12, len(measured["measurement"]["instrumented_modules"]))
        self.assertFalse(measured["native_execution"])
        self.assertEqual(SHA, original["tool"]["files"][binding.MEASUREMENT_MODULES[0] + ".dll"]["sha256"])

    def test_v2_unselected_mutation_mode_change_and_uninstrumented_dll_reject(self):
        for mutation in ("unselected", "mode", "unchanged-dll"):
            original, measured, capture = measured_model()
            name = binding.MEASUREMENT_MODULES[0] + ".dll"
            if mutation == "unselected":
                measured["tool"]["files"]["unchanged.json"]["sha256"] = OTHER
            elif mutation == "mode":
                measured["tool"]["files"][name]["mode"] = "0755"
            else:
                measured["tool"]["files"][name]["sha256"] = SHA
                measured["measurement"]["instrumented_modules"][name] = SHA
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                binding.validate_build(measured, capture, HEAD, "N10")

    def test_cli_requires_all_common_inputs_and_explicit_final_collector_exit(self):
        common = ["--" + key.replace("_", "-") for key in subject.COMMON_PATHS]
        values = sum(([key, "/data/" + key[2:]] for key in common), [])
        values += ["--source", HEAD, "--selector", "N10", "--workflow-identity", "github-actions-1-2"]
        args = subject.parser().parse_args(["bind", *values])
        self.assertIs(subject.bind, args.action)
        args = subject.parser().parse_args(["finalize", *values, "--collector-exit", "1"])
        self.assertEqual(1, args.collector_exit)

    def test_fixed_commands_keep_native_workflow_identity_and_use_measured_receipt(self):
        original, _, _ = measured_model()
        args = argparse.Namespace(source_capture=Path("/e/source.json"), evidence=Path("/e"),
                                  source=HEAD, selector="N10", workflow_identity="github-actions-2-3")
        prepare, run = subject.command_argv(args, original)
        self.assertEqual(["/usr/bin/python3", "-B", str(subject.HERE / "run-native.py")], run[:3])
        self.assertIn("/e/build-measured.json", prepare)
        self.assertEqual(["--workflow-identity", "github-actions-2-3"], run[-2:])
        self.assertEqual(dict(x.split("=", 1) for x in binding.FIXED_ENVIRONMENT), subject.environment())


class MeasurementOrdinaryFileTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.end = time.monotonic() + 10

    def leaf(self, name="sample", raw=b"ordinary bytes"):
        path = self.root / name
        path.write_bytes(raw)
        return path

    def test_stable_real_single_link_file_and_exact_bound_succeed(self):
        raw = b"ordinary real retained file"
        path = self.leaf(raw=raw)
        self.assertEqual(raw, subject.read(path, len(raw), self.end))

    def test_oversize_file_and_expired_deadline_reject(self):
        path = self.leaf(raw=b"1234")
        for maximum, end in ((3, self.end), (4, time.monotonic() - 1)):
            with self.subTest(maximum=maximum), self.assertRaises(binding.Rejected):
                subject.read(path, maximum, end)
        self.assertEqual(b"1234", path.read_bytes())

    def test_leaf_symlink_and_real_hardlink_reject(self):
        path = self.leaf()
        link = self.root / "link"
        link.symlink_to(path)
        with self.assertRaises(binding.Rejected):
            subject.read(link, 100, self.end)
        link.unlink()
        os.link(path, link)
        with self.assertRaises(binding.Rejected):
            subject.read(path, 100, self.end)

    def test_parent_symlink_rejects_without_reading_target(self):
        directory = self.root / "real"
        directory.mkdir()
        (directory / "data").write_bytes(b"private-canary")
        (self.root / "alias").symlink_to(directory, target_is_directory=True)
        with self.assertRaises(binding.Rejected) as failure:
            subject.read(self.root / "alias/data", 100, self.end)
        self.assertNotIn("private-canary", str(failure.exception))

    @unittest.skipUnless(hasattr(os, "mkfifo"), "ordinary FIFO requires POSIX")
    def test_fifo_rejects_before_blocking_open(self):
        path = self.root / "fifo"
        os.mkfifo(path)
        with self.assertRaises(binding.Rejected):
            subject.read(path, 100, self.end)

    def test_mid_read_growth_is_rejected_and_no_bytes_are_returned(self):
        path = self.leaf(raw=b"stable")
        actual_read = os.read
        changed = False
        def changing_read(fd, count):
            nonlocal changed
            data = actual_read(fd, count)
            if not changed:
                with path.open("ab") as stream:
                    stream.write(b"changed")
                changed = True
            return data
        with patch.object(subject.os, "read", side_effect=changing_read), self.assertRaises(binding.Rejected):
            subject.read(path, 100, self.end)

    def test_exclusive_record_is_private_and_second_write_preserves_first(self):
        path = self.root / "record.json"
        subject.save(path, {"native_acceptance": False}, self.end)
        self.assertEqual(0o600, path.stat().st_mode & 0o777)
        before = path.read_bytes()
        with self.assertRaises(FileExistsError):
            subject.save(path, {"native_acceptance": True}, self.end)
        self.assertEqual(before, path.read_bytes())

    def test_record_symlink_cannot_replace_existing_target(self):
        target = self.leaf(raw=b"original-canary")
        (self.root / "record.json").symlink_to(target)
        with self.assertRaises(FileExistsError):
            subject.save(self.root / "record.json", {"x": 1}, self.end)
        self.assertEqual(b"original-canary", target.read_bytes())

    def test_actual_command_logs_are_rehashed_and_later_mutation_rejects(self):
        original, _, _ = measured_model()
        args = argparse.Namespace(source_capture=self.root / "source.json", evidence=self.root,
                                  source=HEAD, selector="N10", workflow_identity="github-actions-2-3")
        rows = []
        for phase, argv, name in zip(("prepare", "run"), subject.command_argv(args, original),
                                    ("measurement-prepare.log", "measurement-native.log")):
            path = self.leaf(name, (phase + " ordinary data log").encode())
            raw = path.read_bytes()
            rows.append({"phase": phase, "argv": argv, "exit": 0, "timed_out": False, "joined": True,
                         "log_path": str(path), "log_sha256": binding.digest(raw), "log_bytes": len(raw)})
        subject.validate_commands(rows, args, original, self.end)
        for mutation in ("exit-bool", "unjoined", "timeout", "argv", "log"):
            changed = copy.deepcopy(rows)
            if mutation == "exit-bool": changed[0]["exit"] = False
            elif mutation == "unjoined": changed[0]["joined"] = False
            elif mutation == "timeout": changed[0]["timed_out"] = True
            elif mutation == "argv": changed[0]["argv"].append("canary")
            else: changed[0]["log_sha256"] = "0" * 64
            with self.subTest(mutation=mutation), self.assertRaises(binding.Rejected):
                subject.validate_commands(changed, args, original, self.end)
        (self.root / "measurement-native.log").write_bytes(b"changed")
        with self.assertRaises(binding.Rejected):
            subject.validate_commands(rows, args, original, self.end)


if __name__ == "__main__":
    unittest.main()
