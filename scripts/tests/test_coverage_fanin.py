"""Regression tests for the conservative coverage fan-in functions."""

from __future__ import annotations

import copy
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest import mock


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "scripts"))
import coverage_fanin as fanin  # noqa: E402


CONFIGURATION = "Release"
PROJECTS = (
    "src/AppSurface.Tests/AppSurface.Tests.csproj",
    "src/AppSurface.IntegrationTests/AppSurface.IntegrationTests.csproj",
)
PLATFORMS = ("Linux", "Windows", "Darwin")
RUN = {"GITHUB_REPOSITORY": "example/coverage", "GITHUB_RUN_ID": "42",
       "GITHUB_RUN_ATTEMPT": "1"}


def line(number: int, hits: int, branch: tuple[int, int] | None = None) -> dict:
    return {"number": number, "hits": hits, "branch": branch}


def document(rows: list[dict], source_root: str = "/checkout", *,
             method_lines: list[tuple[str, int, int]] | None = None) -> ET.Element:
    """Build a small Cobertura document; rows contain direct class line records."""
    lines = [item for row in rows for item in row["lines"]]
    branches = [item["branch"] for item in lines if item["branch"] is not None]
    root = ET.Element("coverage", {
        "lines-covered": str(sum(item["hits"] > 0 for item in lines)),
        "lines-valid": str(len(lines)),
        "branches-covered": str(sum(item[0] for item in branches)),
        "branches-valid": str(sum(item[1] for item in branches)),
    })
    packages = ET.SubElement(root, "packages")
    for row in rows:
        package = ET.SubElement(packages, "package", name=row.get("package", "Example"))
        cls = ET.SubElement(ET.SubElement(package, "classes"), "class", {
            "name": row["name"], "filename": row["filename"],
        })
        class_lines = ET.SubElement(cls, "lines")
        for item in row["lines"]:
            attrs = {"number": str(item["number"]), "hits": str(item["hits"])}
            if item["branch"] is not None:
                covered, valid = item["branch"]
                attrs["branch"] = "true"
                attrs["condition-coverage"] = f"{covered * 100 / valid:g}% ({covered}/{valid})"
            ET.SubElement(class_lines, "line", attrs)
        for class_name, number, hits in method_lines or ():
            if class_name == row["name"]:
                method = ET.SubElement(ET.SubElement(cls, "methods"), "method", name="M")
                ET.SubElement(ET.SubElement(method, "lines"), "line",
                              number=str(number), hits=str(hits))
    return root


def class_row(name: str, filename: str, *lines: dict, package: str = "Example") -> dict:
    return {"name": name, "filename": filename, "package": package, "lines": list(lines)}


class CoverageFaninTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = pathlib.Path(self.temporary.name)
        self.root = self.base / "repo"
        self.root.mkdir()
        self.evidence = self.base / "evidence"
        self.evidence.mkdir()
        slnx = self.root / "ForgeTrust.AppSurface.slnx"
        slnx.write_text("<Solution>" + "".join(
            f'<Project Path="{project}" />' for project in PROJECTS
        ) + "</Solution>\n", encoding="utf-8")
        (self.root / "tracked.txt").write_text("baseline\n", encoding="utf-8")
        subprocess.run(["git", "init", "-q"], cwd=self.root, check=True)
        subprocess.run(["git", "config", "user.name", "Coverage Tests"], cwd=self.root, check=True)
        subprocess.run(["git", "config", "user.email", "coverage-tests@example.invalid"],
                       cwd=self.root, check=True)
        subprocess.run(["git", "add", "ForgeTrust.AppSurface.slnx", "tracked.txt"],
                       cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-qm", "test fixture"], cwd=self.root, check=True)
        self.commands = self.base / "commands"
        self.commands.mkdir()
        dotnet = self.commands / "dotnet"
        dotnet.write_text("#!/bin/sh\nprintf '10.0.100\\n'\n", encoding="utf-8")
        dotnet.chmod(0o755)
        self.path_patch = mock.patch.dict(
            os.environ, {"PATH": str(self.commands) + os.pathsep + os.environ.get("PATH", "")}
        )
        self.path_patch.start()
        self.addCleanup(self.path_patch.stop)
        self.identity = fanin.source_identity(self.root)

    def write_report(self, path: pathlib.Path, rows: list[dict], *, empty: bool = False,
                     method_lines: list[tuple[str, int, int]] | None = None) -> None:
        value = document([] if empty else rows, str(self.root.resolve()), method_lines=method_lines)
        path.parent.mkdir(parents=True, exist_ok=True)
        ET.ElementTree(value).write(path, encoding="utf-8", xml_declaration=True)

    @staticmethod
    def timings(configuration: str = CONFIGURATION) -> dict:
        return {
            "configuration": configuration,
            "coverageDriver": "collector",
            "merge": {"exitCode": 0},
            "artifacts": {"coverageFiles": len(PROJECTS)},
            "projects": [{"project": project, "exitCode": 0,
                          "executionStatus": "completed",
                          "coverageArtifactStatus": "produced"} for project in PROJECTS],
        }

    def make_evidence(self, name: str, scope: str, platform: str,
                      rows: list[dict] | None = None, *,
                      timings: dict | None = None) -> pathlib.Path:
        folder = self.evidence / name
        folder.mkdir(parents=True)
        report = folder / "report.xml"
        self.write_report(report, rows or [class_row("Baseline", "src/Baseline.cs", line(1, 1))])
        shutil_timings = None
        if scope == "solution":
            timing_path = folder / "timings.json"
            timing_path.write_text(json.dumps(timings or self.timings()), encoding="utf-8")
            shutil_timings = timing_path
        with mock.patch.object(fanin.platform, "system", return_value=platform), \
                mock.patch.dict(os.environ, RUN):
            fanin.capture(self.root, report, folder / "captured", CONFIGURATION, scope,
                          shutil_timings, report if scope == "native" else None)
        return folder / "captured"

    def make_bundle(self, *, platforms: tuple[str, ...] = PLATFORMS,
                    prefix: str = "") -> tuple[pathlib.Path, pathlib.Path]:
        primary = self.make_evidence(f"{prefix}primary", "solution", "Linux")
        native = self.evidence / f"{prefix}native"
        native.mkdir()
        for platform in platforms:
            self.make_evidence(f"{prefix}native-{platform}", "native", platform).rename(
                native / platform.lower()
            )
        return primary, native

    def test_capture_accepts_clean_temporary_repo_without_global_json(self) -> None:
        self.assertFalse((self.root / "global.json").exists())
        evidence = self.make_evidence("no-global-json", "native", "Linux")
        manifest = json.loads((evidence / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["sdk"], "10.0.100")
        self.assertEqual(manifest["tree"], self.identity["tree"])

    def test_source_identity_rejects_tracked_edits_and_untracked_files(self) -> None:
        (self.root / "tracked.txt").write_text("edited\n", encoding="utf-8")
        with self.assertRaises(subprocess.CalledProcessError):
            fanin.source_identity(self.root)
        (self.root / "tracked.txt").write_text("baseline\n", encoding="utf-8")
        (self.root / "untracked.txt").write_text("uncommitted\n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Untracked files"):
            fanin.source_identity(self.root)

    def test_solution_inventory_accepts_test_and_integration_test_projects(self) -> None:
        fanin.verify_solution(self.root, self.timings(), CONFIGURATION)

    def test_capture_rejects_incomplete_or_failed_primary_solution_evidence(self) -> None:
        report = self.evidence / "primary-report.xml"
        self.write_report(report, [class_row("Baseline", "src/Baseline.cs", line(1, 1))])
        bad_cases = []
        missing = self.timings()
        missing["projects"] = missing["projects"][:1]
        bad_cases.append(("missing integration project", missing))
        duplicate = self.timings()
        duplicate["projects"] = [duplicate["projects"][0], duplicate["projects"][0]]
        bad_cases.append(("duplicate project", duplicate))
        failed = self.timings()
        failed["projects"][1]["exitCode"] = 1
        bad_cases.append(("failed project", failed))
        incomplete = self.timings()
        incomplete["artifacts"]["coverageFiles"] -= 1
        bad_cases.append(("missing report", incomplete))
        failed_merge = self.timings()
        failed_merge["merge"]["exitCode"] = 1
        bad_cases.append(("failed merge", failed_merge))
        wrong_driver = self.timings()
        wrong_driver["coverageDriver"] = "other"
        bad_cases.append(("wrong driver", wrong_driver))
        wrong_configuration = self.timings("Debug")
        bad_cases.append(("wrong configuration", wrong_configuration))
        unfinished = self.timings()
        unfinished["projects"][0]["executionStatus"] = "failed"
        bad_cases.append(("unfinished project", unfinished))
        for index, (case, value) in enumerate(bad_cases):
            with self.subTest(case=case):
                timing_path = self.evidence / f"timings-{index}.json"
                timing_path.write_text(json.dumps(value), encoding="utf-8")
                destination = self.evidence / f"rejected-{index}"
                with self.assertRaises(ValueError):
                    fanin.capture(self.root, report, destination, CONFIGURATION,
                                  "solution", timing_path)
                self.assertFalse(destination.exists())
        with self.assertRaisesRegex(ValueError, "timing inventory"):
            fanin.capture(self.root, report, self.evidence / "no-timings", CONFIGURATION,
                          "solution")

    def test_merge_accepts_matching_source_sdk_tree_policy_and_all_platforms(self) -> None:
        primary, native = self.make_bundle()
        output = self.evidence / "merged"
        fanin.merge(self.root, primary, native, output, CONFIGURATION)
        receipt = json.loads((output / "verification.json").read_text(encoding="utf-8"))
        self.assertEqual(receipt["commit"], self.identity["commit"])
        self.assertEqual(receipt["tree"], self.identity["tree"])
        self.assertEqual(receipt["sdk"], self.identity["sdk"])
        self.assertEqual(receipt["configuration"], CONFIGURATION)
        self.assertEqual(len(receipt["inputs"]), 4)

    def test_load_evidence_rejects_identity_policy_and_filter_mismatches(self) -> None:
        mutations = {
            "commit": lambda manifest: manifest.update(commit="wrong"),
            "tree": lambda manifest: manifest.update(tree="wrong"),
            "sdk": lambda manifest: manifest.update(sdk="wrong"),
            "configuration": lambda manifest: manifest.update(configuration="Debug"),
            "policy": lambda manifest: manifest.update(policy={"driver": "other"}),
            "filter": lambda manifest: manifest.update(filter="unapproved"),
        }
        for index, (case, mutate) in enumerate(mutations.items()):
            with self.subTest(case=case):
                evidence = self.make_evidence(f"mismatch-{index}", "native", "Linux")
                manifest_path = evidence / "manifest.json"
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                mutate(manifest)
                manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaises(ValueError):
                    fanin.load_evidence(self.root, evidence, self.identity, CONFIGURATION)

    def test_merge_rejects_each_missing_required_platform(self) -> None:
        for missing in PLATFORMS:
            with self.subTest(missing=missing):
                primary, native = self.make_bundle(
                    platforms=tuple(platform for platform in PLATFORMS if platform != missing),
                    prefix=f"missing-{missing.lower()}-",
                )
                with self.assertRaisesRegex(ValueError, "Linux, Windows and macOS"):
                    fanin.merge(self.root, primary, native, self.evidence / f"out-{missing}",
                                CONFIGURATION)

    def test_merge_rejects_mixed_workflow_attempts(self) -> None:
        primary, native = self.make_bundle()
        manifest_path = native / "linux" / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["run"]["GITHUB_RUN_ATTEMPT"] = "2"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "same workflow run"):
            fanin.merge(self.root, primary, native, self.evidence / "mixed-run", CONFIGURATION)

    def test_merge_rejects_changed_report_bytes(self) -> None:
        primary, native = self.make_bundle()
        report = native / "windows" / "coverage.cobertura.xml"
        report.write_bytes(report.read_bytes() + b"\n<!-- changed -->\n")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"):
            fanin.merge(self.root, primary, native, self.evidence / "changed", CONFIGURATION)

    def test_merge_rejects_non_solution_primary_scope(self) -> None:
        primary = self.make_evidence("native-primary", "native", "Linux")
        native = self.evidence / "native-supplements"
        native.mkdir()
        for platform in PLATFORMS:
            self.make_evidence(f"supplement-{platform}", "native", platform).rename(
                native / platform.lower()
            )
        with self.assertRaises(ValueError):
            fanin.merge(self.root, primary, native, self.evidence / "wrong-scope", CONFIGURATION)

    def test_merge_rejects_incomplete_or_failed_primary_timings_after_checksum_refresh(self) -> None:
        for case in ("missing-project", "failed-project"):
            with self.subTest(case=case):
                primary, native = self.make_bundle(prefix=f"{case}-")
                timings_path = primary / "timings.json"
                timings = json.loads(timings_path.read_text(encoding="utf-8"))
                if case == "missing-project":
                    timings["projects"].pop()
                else:
                    timings["projects"][0]["exitCode"] = 1
                timings_path.write_text(json.dumps(timings), encoding="utf-8")
                manifest_path = primary / "manifest.json"
                manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
                manifest["files"]["timings.json"] = hashlib.sha256(
                    timings_path.read_bytes()
                ).hexdigest()
                manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
                with self.assertRaises(ValueError):
                    fanin.merge(self.root, primary, native, self.evidence / f"bad-{case}",
                                CONFIGURATION)

    def test_source_paths_normalize_windows_and_unix_and_reject_escapes(self) -> None:
        self.assertEqual(fanin.relative_filename(
            r"C:\agent\work\repo\src\Widget.cs", r"C:\agent\work\repo"),
            "src/Widget.cs")
        self.assertEqual(fanin.relative_filename("/agent/work/repo/src/Widget.cs",
                                                  "/agent/work/repo"), "src/Widget.cs")
        for value, source_root in (("../outside.cs", "/repo"),
                                   ("src/../../outside.cs", "/repo"),
                                   ("/other/repo/File.cs", "/repo"),
                                   (r"D:\other\File.cs", r"C:\repo")):
            with self.subTest(value=value), self.assertRaises(ValueError):
                fanin.relative_filename(value, source_root)

    def test_compose_retains_baseline_points_distinct_classes_and_method_lines(self) -> None:
        first = document([
            class_row("Alpha", "src/Shared.cs", line(10, 1)),
            class_row("Beta", "src/Shared.cs", line(10, 0), line(11, 1)),
        ], method_lines=[("Alpha", 10, 1), ("Alpha", 999, 1)])
        second = document([
            class_row("Alpha", "src/Shared.cs", line(10, 1)),
            class_row("Gamma", "src/Shared.cs", line(10, 1)),
        ])
        result, inventory = fanin.compose([
            ({"sourceRoot": "/checkout"}, first),
            ({"sourceRoot": "/checkout"}, second),
        ])
        classes = [cls for cls in result.findall("packages/package/classes/class")]
        self.assertEqual({cls.get("name") for cls in classes}, {"Alpha", "Beta", "Gamma"})
        self.assertEqual(inventory["inputClassLines"], [3, 2])
        self.assertEqual(inventory["retainedClassLines"], 4)
        self.assertEqual(inventory["inputMethodLines"], [2, 0])
        self.assertEqual(inventory["retainedMethodLines"], 2)
        alpha_lines = next(cls for cls in classes if cls.get("name") == "Alpha").findall("lines/line")
        self.assertEqual([item.get("number") for item in alpha_lines], ["10"])
        self.assertNotIn("999", [item.get("number") for item in alpha_lines])
        alpha_methods = next(cls for cls in classes if cls.get("name") == "Alpha").findall("methods/method/lines/line")
        self.assertEqual([item.get("number") for item in alpha_methods], ["10", "999"])

    def test_compose_retains_the_gate_method_and_class_branch_denominator(self) -> None:
        first = document([class_row("Branchy", "src/Branchy.cs", line(20, 1, (1, 2)))])
        cls = first.find("packages/package/classes/class")
        method = ET.SubElement(ET.SubElement(cls, "methods"), "method", name="M", signature="()")
        ET.SubElement(method, "lines").append(ET.fromstring(
            '<line number="20" hits="1" branch="true" condition-coverage="50% (1/2)" />'))
        result, inventory = fanin.compose([({"sourceRoot": "/checkout"}, first)])
        # The existing gate reads both line collections, including their branch records.
        self.assertEqual(sum(fanin.branch_counts(item)[1] for item in first.iter("line")), 4)
        self.assertEqual(sum(fanin.branch_counts(item)[1] for item in result.iter("line")), 4)
        self.assertEqual(inventory["retainedMethodLines"], 1)
        self.assertEqual(int(result.get("branches-valid")), 2)

    def test_compose_method_promotions_keep_overloads_and_partial_observations_separate(self) -> None:
        def with_methods(branches: tuple[int, int], signature: str = "()") -> ET.Element:
            value = document([class_row("Branchy", "src/Branchy.cs", line(20, 1, (1, 2)))])
            cls = value.find("packages/package/classes/class")
            method = ET.SubElement(ET.SubElement(cls, "methods"), "method", name="M", signature=signature)
            covered, valid = branches
            ET.SubElement(ET.SubElement(method, "lines"), "line", number="20", hits="1", branch="true",
                          **{"condition-coverage": f"50% ({covered}/{valid})"})
            return value

        for other, expected in (((1, 2), (1, 2)), ((2, 2), (2, 2)), ((4, 4), (1, 2))):
            with self.subTest(other=other):
                result, _ = fanin.compose([
                    ({"sourceRoot": "/checkout"}, with_methods((1, 2))),
                    ({"sourceRoot": "/checkout"}, with_methods(other)),
                ])
                methods = result.findall("packages/package/classes/class/methods/method")
                self.assertEqual(len(methods), 1)
                self.assertEqual(fanin.branch_counts(methods[0].find("lines/line")), expected)
                self.assertEqual(sum(fanin.branch_counts(item)[1] for item in result.iter("line")), 4)
        result, _ = fanin.compose([
            ({"sourceRoot": "/checkout"}, with_methods((1, 2))),
            ({"sourceRoot": "/checkout"}, with_methods((2, 2), "(System.Int32)")),
        ])
        methods = result.findall("packages/package/classes/class/methods/method")
        self.assertEqual({item.get("signature") for item in methods}, {"()", "(System.Int32)"})
        self.assertEqual(sum(fanin.branch_counts(item)[1] for item in result.iter("line")), 6)

    def test_compose_retains_repeated_records_within_a_single_observation(self) -> None:
        first = document([class_row("Branchy", "src/Branchy.cs", line(20, 1, (1, 2)))])
        cls = first.find("packages/package/classes/class")
        methods = ET.SubElement(cls, "methods")
        for _ in range(2):
            method = ET.SubElement(methods, "method", name="M", signature="()")
            lines = ET.SubElement(method, "lines")
            for _ in range(2):
                ET.SubElement(lines, "line", number="20", hits="1", branch="true",
                              **{"condition-coverage": "50% (1/2)"})
        result, inventory = fanin.compose([
            ({"sourceRoot": "/checkout"}, first),
            ({"sourceRoot": "/checkout"}, first),
        ])
        self.assertEqual(inventory["retainedMethodLines"], 4)
        self.assertEqual(len(result.findall("packages/package/classes/class/methods/method")), 2)
        self.assertEqual(sum(fanin.branch_counts(item)[1] for item in result.iter("line")), 10)

    @staticmethod
    def composed_branch(first: tuple[int, int], second: tuple[int, int]) -> tuple[int, int]:
        rows = [class_row("Branchy", "src/Branchy.cs", line(20, 1, first))]
        other = [class_row("Branchy", "src/Branchy.cs", line(20, 1, second))]
        result, _ = fanin.compose([
            ({"sourceRoot": "/checkout"}, document(rows)),
            ({"sourceRoot": "/checkout"}, document(other)),
        ])
        output_line = result.find("packages/package/classes/class/lines/line")
        assert output_line is not None
        return fanin.branch_counts(output_line)

    def test_two_partial_half_observations_never_become_full_coverage(self) -> None:
        self.assertEqual(self.composed_branch((1, 2), (1, 2)), (1, 2))

    def test_only_full_native_coverage_with_matching_denominator_promotes(self) -> None:
        self.assertEqual(self.composed_branch((1, 2), (2, 2)), (2, 2))
        self.assertEqual(self.composed_branch((1, 2), (4, 4)), (1, 2))
        self.assertEqual(self.composed_branch((0, 2), (1, 2)), (0, 2))

    def test_checked_xml_rejects_empty_coverage(self) -> None:
        report = self.evidence / "empty.xml"
        self.write_report(report, [], empty=True)
        with self.assertRaisesRegex(ValueError, "empty or out of range"):
            fanin.checked_xml(report)

    def test_native_capture_requires_raw_collector_before_creating_destination(self) -> None:
        report = self.evidence / "normalized.xml"
        self.write_report(report, [class_row("C", "src/C.cs", line(1, 1))])
        output = self.evidence / "missing-collector"
        with self.assertRaisesRegex(ValueError, "original collector"):
            fanin.capture(self.root, report, output, CONFIGURATION, "native")
        self.assertFalse(output.exists())

    def test_native_capture_retains_collector_bytes_and_normalization_policy(self) -> None:
        captured = self.make_evidence("collector-roundtrip", "native", "Linux")
        manifest = json.loads((captured / "manifest.json").read_text())
        self.assertEqual(set(manifest["files"]), {"coverage.cobertura.xml", "collector.cobertura.xml"})
        self.assertEqual(manifest["policy"]["normalization"], "appsurface-coverage-merge")
        self.assertEqual((captured / "collector.cobertura.xml").read_bytes(),
                         (captured / "coverage.cobertura.xml").read_bytes())
        self.assertEqual(manifest["files"]["collector.cobertura.xml"],
                         fanin.digest(captured / "collector.cobertura.xml"))

    def test_normalization_accepts_class_folding_without_losing_execution(self) -> None:
        raw = document([class_row("C/<M>d__0", "src/C.cs", line(1, 1), line(2, 0))])
        normalized = document([class_row("C", "src/C.cs", line(1, 1), line(2, 0))])
        fanin.verify_normalization(raw, normalized, "/checkout")

    def test_normalization_rejects_lost_points_hits_and_branch_scope(self) -> None:
        raw = document([class_row("C", "src/C.cs", line(1, 1, (1, 2)), line(2, 0))])
        for rows in ([line(1, 1, (1, 2))],
                     [line(1, 0, (1, 2)), line(2, 0)],
                     [line(1, 1), line(2, 0)]):
            with self.subTest(rows=rows), self.assertRaisesRegex(ValueError, "normalization"):
                fanin.verify_normalization(raw, document([class_row("C", "src/C.cs", *rows)]),
                                           "/checkout")

    def test_tampered_collector_is_rejected_by_loading_and_composition(self) -> None:
        primary, native = self.make_bundle(prefix="collector-tamper-")
        shard = native / "linux"
        with (shard / "collector.cobertura.xml").open("a") as stream:
            stream.write("<!-- changed -->")
        with self.assertRaisesRegex(ValueError, "checksum"):
            fanin.load_evidence(self.root, shard, self.identity, CONFIGURATION)
        with self.assertRaisesRegex(ValueError, "checksum"):
            fanin.merge(self.root, primary, native, self.evidence / "tampered-output", CONFIGURATION)

    def test_default_output_can_be_prepared_and_composed_repeatedly(self) -> None:
        primary, native = self.make_bundle(prefix="repeat-")
        output = self.root / "TestResults/coverage-fanin"
        fanin.prepare_default_output(self.root, output)
        for _ in range(2):
            fanin.merge(self.root, primary, native, output, CONFIGURATION)
            self.assertTrue((output / "verification.json").is_file())
            fanin.prepare_default_output(self.root, output)
            self.assertFalse(output.exists())

    def default_primary(self) -> tuple[pathlib.Path, pathlib.Path, pathlib.Path]:
        # Match the real checkout's ignored output without weakening source attestation.
        (self.root / ".gitignore").write_text("/TestResults\n")
        subprocess.run(["git", "add", ".gitignore"], cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-qm", "ignore generated fixture output"],
                       cwd=self.root, check=True)
        report = self.evidence / "current-primary.xml"
        self.write_report(report, [class_row("Baseline", "src/Baseline.cs", line(1, 1))])
        timings = self.evidence / "current-timings.json"
        timings.write_text(json.dumps(self.timings()))
        return report, timings, self.root / "TestResults/coverage-merged/fanin-primary"

    def test_default_primary_capture_can_repeat_after_successful_collection(self) -> None:
        report, timings, output = self.default_primary()
        with mock.patch.dict(os.environ, RUN):
            fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
            original = {item.name: item.read_bytes() for item in output.iterdir()}
            fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        self.assertEqual(original, {item.name: item.read_bytes() for item in output.iterdir()})
        self.assertEqual(set(original), {"manifest.json", "coverage.cobertura.xml", "timings.json"})

    def test_default_primary_recapture_preserves_unknown_modified_or_linked_material(self) -> None:
        report, timings, output = self.default_primary()
        for defect in ("unknown", "report", "timings", "manifest", "link"):
            with self.subTest(defect=defect):
                if output.exists():
                    shutil.rmtree(output)
                fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
                if defect == "unknown":
                    (output / "caller.txt").write_text("preserve")
                elif defect in {"report", "timings"}:
                    name = "coverage.cobertura.xml" if defect == "report" else "timings.json"
                    with (output / name).open("a") as stream:
                        stream.write("changed")
                elif defect == "manifest":
                    manifest = json.loads((output / "manifest.json").read_text())
                    manifest["policy"] = {"driver": "other"}
                    (output / "manifest.json").write_text(json.dumps(manifest))
                else:
                    outside = self.evidence / "outside-report.xml"
                    outside.write_text("preserve external evidence")
                    (output / "coverage.cobertura.xml").unlink()
                    (output / "coverage.cobertura.xml").symlink_to(outside)
                before = {item.name: item.read_bytes() for item in output.iterdir()}
                with self.assertRaises((ValueError, OSError)):
                    fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
                self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})
                if defect == "link":
                    self.assertTrue((output / "coverage.cobertura.xml").is_symlink())
                    self.assertEqual(outside.read_text(), "preserve external evidence")

    def test_invalid_new_primary_preserves_previous_primary_before_recapture(self) -> None:
        report, timings, output = self.default_primary()
        fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        before = {item.name: item.read_bytes() for item in output.iterdir()}
        invalid = self.timings()
        invalid["projects"].pop()
        timings.write_text(json.dumps(invalid))
        with self.assertRaisesRegex(ValueError, "every solution test project"):
            fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})

    def test_default_primary_capture_rejects_linked_ancestors_without_outside_mutation(self) -> None:
        report, timings, output = self.default_primary()
        outside = self.evidence / "outside-results"
        (outside / "coverage-merged").mkdir(parents=True)
        sentinel = outside / "coverage-merged/caller.txt"
        sentinel.write_text("preserve outside evidence")
        (self.root / "TestResults").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "non-linked"):
            fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        self.assertEqual(sentinel.read_text(), "preserve outside evidence")
        self.assertFalse((outside / "coverage-merged/fanin-primary").exists())

    def test_custom_primary_capture_requires_fresh_destination(self) -> None:
        report, timings, _ = self.default_primary()
        output = self.evidence / "custom-primary"
        fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        before = {item.name: item.read_bytes() for item in output.iterdir()}
        with self.assertRaisesRegex(ValueError, "must be fresh"):
            fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})

    def test_primary_recapture_cannot_retire_its_own_producer_inputs(self) -> None:
        report, timings, output = self.default_primary()
        fanin.capture(self.root, report, output, CONFIGURATION, "solution", timings)
        before = {item.name: item.read_bytes() for item in output.iterdir()}
        for primary_report, primary_timings in (
                (output / "coverage.cobertura.xml", timings), (report, output / "timings.json")):
            with self.subTest(report=primary_report, timings=primary_timings):
                with self.assertRaisesRegex(ValueError, "producer inputs"):
                    fanin.capture(self.root, primary_report, output, CONFIGURATION, "solution", primary_timings)
                self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})

    def test_preparation_preserves_unknown_or_modified_composition_contents(self) -> None:
        primary, native = self.make_bundle(prefix="preserve-")
        output = self.root / "TestResults/coverage-fanin"
        fanin.merge(self.root, primary, native, output, CONFIGURATION)
        stranger = output / "caller-notes.txt"
        stranger.write_text("preserve caller evidence")
        before = {item.name: item.read_bytes() for item in output.iterdir()}
        with self.assertRaisesRegex(ValueError, "Unknown"):
            fanin.prepare_default_output(self.root, output)
        self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})
        stranger.unlink()
        with (output / "coverage.cobertura.xml").open("a") as stream:
            stream.write("<!-- preserve edited evidence -->")
        before = {item.name: item.read_bytes() for item in output.iterdir()}
        with self.assertRaisesRegex(ValueError, "Modified"):
            fanin.prepare_default_output(self.root, output)
        self.assertEqual(before, {item.name: item.read_bytes() for item in output.iterdir()})

    def test_preparation_rejects_linked_parent_and_preserves_outside_composition(self) -> None:
        primary, native = self.make_bundle(prefix="parent-link-")
        outside = self.evidence / "outside"
        outside.mkdir()
        composition = outside / "coverage-fanin"
        fanin.merge(self.root, primary, native, composition, CONFIGURATION)
        before = {item.name: item.read_bytes() for item in composition.iterdir()}
        (self.root / "TestResults").symlink_to(outside, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, "non-linked"):
            fanin.prepare_default_output(self.root, self.root / "TestResults/coverage-fanin")
        self.assertEqual(before, {item.name: item.read_bytes() for item in composition.iterdir()})

    def test_preparation_rejects_custom_output_without_mutation(self) -> None:
        custom = self.evidence / "custom"
        custom.mkdir()
        sentinel = custom / "caller.txt"
        sentinel.write_text("keep")
        with self.assertRaisesRegex(ValueError, "default composition"):
            fanin.prepare_default_output(self.root, custom)
        self.assertEqual(sentinel.read_text(), "keep")

    def test_parent_replacement_cannot_redirect_cleanup_unlinks(self) -> None:
        primary, native = self.make_bundle(prefix="parent-replacement-")
        output = self.root / "TestResults/coverage-fanin"
        fanin.merge(self.root, primary, native, output, CONFIGURATION)
        outside = self.evidence / "unrelated"
        redirected = outside / "coverage-fanin"
        redirected.mkdir(parents=True)
        for name in ("coverage.cobertura.xml", "primary-paths.cobertura.xml", "verification.json"):
            (redirected / name).write_text("unrelated caller evidence")
        before = {item.name: item.read_bytes() for item in redirected.iterdir()}
        real_unlink = os.unlink
        changed = False

        def replace_parent_then_unlink(name, *args, **kwargs):
            nonlocal changed
            if not changed:
                changed = True
                (self.root / "TestResults").rename(self.root / "previous-results")
                (self.root / "TestResults").symlink_to(outside, target_is_directory=True)
            return real_unlink(name, *args, **kwargs)

        with mock.patch.object(os, "unlink", side_effect=replace_parent_then_unlink) as intercepted, \
                mock.patch.object(os, "supports_dir_fd", os.supports_dir_fd | {intercepted}):
            fanin.prepare_default_output(self.root, output)

        self.assertTrue(changed)
        self.assertEqual(before, {item.name: item.read_bytes() for item in redirected.iterdir()})
        self.assertFalse((self.root / "previous-results/coverage-fanin").exists())


class CoverageFaninHelperTests(unittest.TestCase):
    """Execute the real shell helper and provenance verifier with a guarded CLI fixture."""

    def setUp(self) -> None:
        self.fixture = CoverageFaninTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        scripts = self.root / "scripts"
        scripts.mkdir()
        for name in ("coverage-gate-fanin.sh", "coverage_fanin.py"):
            shutil.copyfile(REPO_ROOT / "scripts" / name, scripts / name)
        subprocess.run(["git", "add", "scripts"], cwd=self.root, check=True)
        subprocess.run(["git", "commit", "-qm", "helper fixture"], cwd=self.root, check=True)
        self.fixture.identity = fanin.source_identity(self.root)
        primary, self.native = self.fixture.make_bundle()
        self.coverage = self.fixture.base / "coverage-merged"
        self.coverage.mkdir()
        shutil.copytree(primary, self.coverage / "fanin-primary")
        self.composed = self.fixture.base / "coverage-fanin"
        fanin.merge(self.root, self.coverage / "fanin-primary", self.native,
                    self.composed, CONFIGURATION)
        (self.fixture.commands / "dotnet").write_text(GUARDED_DOTNET, encoding="utf-8")

    def run_helper(self, *, status: int = 0, diff_base: str = "origin/main") -> subprocess.CompletedProcess:
        env = os.environ.copy()
        env.update(COVERAGE_DIRECTORY=str(self.coverage), COVERAGE_FANIN_NATIVE=str(self.native),
                   BUILD_CONFIGURATION=CONFIGURATION, BUILD_NO_RESTORE="true",
                   COVERAGE_GATE_DIFF_BASE=diff_base, FAKE_PATCH_STATUS=str(status))
        return subprocess.run(["bash", "scripts/coverage-gate-fanin.sh"], cwd=self.root,
                              env=env, capture_output=True, text=True)

    def test_helper_publishes_canonical_reports_without_input_output_overlap(self) -> None:
        result = self.run_helper()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        for name in ("coverage-gate.json", "coverage-gate.md",
                     "coverage-patch-targets.json", "coverage-patch-targets.md"):
            self.assertTrue((self.coverage / name).is_file(), name)
        self.assertTrue((self.coverage / "aggregate-gate/coverage-gate.json").is_file())
        calls = [json.loads(line) for line in (self.fixture.base / "gate-calls.jsonl").read_text().splitlines()]
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[1]["coverage"], str((self.composed / "coverage.cobertura.xml").resolve()))
        self.assertEqual(calls[1]["output"], str(self.coverage.resolve()))

    def test_failed_patch_gate_returns_failure_and_keeps_failure_reports(self) -> None:
        result = self.run_helper(status=1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        report = json.loads((self.coverage / "coverage-gate.json").read_text())
        self.assertFalse(report["passed"])
        self.assertTrue((self.coverage / "coverage-patch-targets.json").is_file())

    def test_empty_base_runs_only_the_unchanged_aggregate_gate(self) -> None:
        result = self.run_helper(diff_base="")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue((self.coverage / "aggregate-gate/coverage-gate.json").is_file())
        self.assertFalse((self.coverage / "coverage-gate.json").exists())

    def test_changed_composition_is_rejected_before_either_gate(self) -> None:
        with (self.composed / "coverage.cobertura.xml").open("a") as stream:
            stream.write("<!-- stale composition -->")
        result = self.run_helper()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((self.fixture.base / "gate-calls.jsonl").exists())


GUARDED_DOTNET = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
if args == ["--version"]:
    print("10.0.100")
    raise SystemExit(0)
if "gate" not in args:
    raise SystemExit("Unexpected CLI invocation")
def option(name):
    return args[args.index(name) + 1]
coverage = pathlib.Path(option("--coverage")).resolve()
output = pathlib.Path(option("--output")).resolve()
if output == coverage or output in coverage.parents:
    print("ASCOV009 --output must not contain the input coverage file.", file=sys.stderr)
    raise SystemExit(2)
with (output.parent / "gate-calls.jsonl" if output.name == "coverage-merged" else output.parent.parent / "gate-calls.jsonl").open("a") as stream:
    stream.write(json.dumps({"coverage": str(coverage), "output": str(output)}) + "\n")
patch = "--min-patch-line" in args
status = int(os.environ["FAKE_PATCH_STATUS"]) if patch else 0
report = {"passed": status == 0, "tolerancePercent": 0.5, "patchLineMode": "codecov",
          "patchDiffSource": {"diffBase": "origin/main"},
          "thresholds": {"line": 95, "branch": 85, "patchLine": 95, "patchBranch": 85},
          "patchLine": {"measurable": 10}, "patchBranch": {"measurable": 5}}
output.mkdir(parents=True, exist_ok=True)
for name in ("coverage-gate.json", "coverage-gate.md", "coverage-patch-targets.json", "coverage-patch-targets.md"):
    (output / name).write_text(json.dumps(report))
raise SystemExit(status)
'''


if __name__ == "__main__":
    unittest.main()
