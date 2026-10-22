"""Regression tests for the conservative coverage fan-in functions."""

from __future__ import annotations

import copy
import hashlib
import json
import os
import pathlib
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
                          shutil_timings)
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

    def test_compose_retains_baseline_points_distinct_classes_and_ignores_method_lines(self) -> None:
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
        alpha_lines = next(cls for cls in classes if cls.get("name") == "Alpha").findall("lines/line")
        self.assertEqual([item.get("number") for item in alpha_lines], ["10"])
        self.assertNotIn("999", [item.get("number") for item in alpha_lines])

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


if __name__ == "__main__":
    unittest.main()
