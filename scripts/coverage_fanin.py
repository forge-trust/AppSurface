#!/usr/bin/env python3
"""Repository-only, conservative coverage fan-in. See guides/coverage-fan-in.md."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath

SCHEMA = 1
POLICY = {
    "driver": "collector",
    "format": "cobertura",
    "exclude": "[*.Tests]*,[*.IntegrationTests]*",
    "include": None,
}
NATIVE_FILTER = (
    "FullyQualifiedName~CoverageRunArtifactReaderTests|"
    "FullyQualifiedName~CoverageRunOutputGuardSecurityTests|"
    "FullyQualifiedName~CoverageGateTests|FullyQualifiedName~CoverageArtifact|"
    "FullyQualifiedName~CoverageStorageCommand"
)
PLATFORMS = {"Linux", "Windows", "Darwin"}
MAX_REPORT_BYTES = 512 * 1024 * 1024


def command(root: Path, *args: str) -> str:
    return subprocess.check_output(args, cwd=root, text=True).strip()


def source_identity(root: Path) -> dict:
    """Bind a clean tracked tree to its exact commit and SDK, including test sources."""
    subprocess.run(["git", "diff", "--exit-code", "--quiet", "HEAD", "--"], cwd=root, check=True)
    if command(root, "git", "ls-files", "--others", "--exclude-standard"):
        raise ValueError("Untracked files prevent source attestation; commit source changes first")
    return {
        "commit": command(root, "git", "rev-parse", "HEAD"),
        "tree": command(root, "git", "rev-parse", "HEAD^{tree}"),
        "sdk": command(root, "dotnet", "--version"),
    }


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path: Path, limit: int = 1024 * 1024) -> dict:
    if path.is_symlink() or path.stat().st_size > limit:
        raise ValueError(f"Invalid or oversized evidence: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def relative_filename(value: str, source_root: str) -> str:
    """Remove only the attested checkout prefix; never guess foreign absolute roots."""
    value = value.replace("\\", "/")
    prefix = source_root.replace("\\", "/").rstrip("/") + "/"
    if value.startswith(prefix):
        value = value[len(prefix):]
    if "\x00" in value:
        raise ValueError("Coverage path contains NUL")
    path = PurePosixPath(value)
    if not value or path.is_absolute() or ":" in value or ".." in path.parts:
        raise ValueError(f"Coverage path is outside its attested source root: {value}")
    return str(path)


def checked_xml(path: Path) -> ET.Element:
    if path.is_symlink() or not 0 < path.stat().st_size <= MAX_REPORT_BYTES:
        raise ValueError(f"Invalid or oversized coverage report: {path}")
    data = path.read_bytes()
    # ReportGenerator emits an external Cobertura DTD. ElementTree never resolves it.
    # Entity declarations, including internal subsets, are not accepted.
    if b"<!ENTITY" in data.upper() or re.search(br"<!DOCTYPE[^>]*\[", data, re.IGNORECASE):
        raise ValueError("Coverage entity declarations are forbidden")
    root = ET.fromstring(data)
    if root.tag != "coverage":
        raise ValueError("Expected Cobertura coverage root")
    for dimension in ("lines", "branches"):
        valid = int(root.attrib[f"{dimension}-valid"])
        covered = int(root.attrib[f"{dimension}-covered"])
        if not 0 <= covered <= valid or dimension == "lines" and valid == 0:
            raise ValueError("Coverage counters are empty or out of range")
    return root


def verify_solution(root: Path, timings: dict, configuration: str) -> None:
    """Reject filtered, incomplete or failed primary solution collection."""
    solution = ET.parse(root / "ForgeTrust.AppSurface.slnx")
    expected = {
        item.attrib["Path"] for item in solution.iter("Project")
        if Path(item.attrib["Path"]).stem.endswith((".Tests", ".IntegrationTests"))
    }
    projects = timings.get("projects", [])
    actual = [item["project"] for item in projects]
    if not expected or len(actual) != len(expected) or set(actual) != expected:
        raise ValueError("Primary evidence must contain every solution test project exactly once")
    if timings.get("configuration") != configuration or timings.get("coverageDriver") != "collector":
        raise ValueError("Primary configuration or collector differs")
    if timings.get("merge", {}).get("exitCode") != 0:
        raise ValueError("Primary coverage merge did not succeed")
    if timings.get("artifacts", {}).get("coverageFiles") != len(expected):
        raise ValueError("Primary coverage report inventory is incomplete")
    for item in projects:
        if (item.get("exitCode") != 0 or item.get("executionStatus") != "completed"
                or item.get("coverageArtifactStatus") != "produced"):
            raise ValueError("Primary test project did not complete successfully with coverage")


def capture(root: Path, report: Path, output: Path, configuration: str, scope: str,
            timings: Path | None = None) -> None:
    """Snapshot bytes after a successful producer step; manifests are CI evidence, not signatures."""
    identity = source_identity(root)
    checked_xml(report)
    if output.exists():
        raise ValueError("Evidence destination must be fresh")
    if scope == "solution":
        if timings is None:
            raise ValueError("Full solution timing inventory is required")
        verify_solution(root, read_json(timings), configuration)
    output.mkdir(parents=True)
    shutil.copyfile(report, output / "coverage.cobertura.xml")
    files = {"coverage.cobertura.xml": digest(output / "coverage.cobertura.xml")}
    if scope == "solution":
        shutil.copyfile(timings, output / "timings.json")
        files["timings.json"] = digest(output / "timings.json")
    manifest = {
        "schemaVersion": SCHEMA, **identity, "configuration": configuration,
        "sourceRoot": str(root.resolve()), "platform": platform.system(),
        "policy": POLICY, "scope": scope,
        "filter": NATIVE_FILTER if scope == "native" else None,
        "run": {key: os.environ.get(key) for key in (
            "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
        "files": files,
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def load_evidence(root: Path, path: Path, identity: dict, configuration: str) -> tuple[dict, ET.Element]:
    manifest = read_json(path / "manifest.json")
    expected = {"schemaVersion": SCHEMA, **identity, "configuration": configuration, "policy": POLICY}
    if any(manifest.get(key) != value for key, value in expected.items()):
        raise ValueError(f"Source, SDK, configuration or instrumentation mismatch: {path}")
    scope = manifest.get("scope")
    expected_files = {"coverage.cobertura.xml", "timings.json"} if scope == "solution" else {"coverage.cobertura.xml"}
    if scope not in {"solution", "native"} or set(manifest.get("files", {})) != expected_files:
        raise ValueError("Unexpected evidence scope or inventory")
    if manifest.get("filter") != (NATIVE_FILTER if scope == "native" else None):
        raise ValueError("Native test selection differs")
    for name, expected_hash in manifest["files"].items():
        item = path / name
        limit = MAX_REPORT_BYTES if name.endswith(".xml") else 1024 * 1024
        if item.is_symlink() or item.stat().st_size > limit or digest(item) != expected_hash:
            raise ValueError(f"Evidence checksum mismatch: {item}")
    if scope == "solution":
        verify_solution(root, read_json(path / "timings.json"), configuration)
    return manifest, checked_xml(path / "coverage.cobertura.xml")


def branch_counts(line: ET.Element) -> tuple[int, int]:
    value = line.get("condition-coverage")
    if value is None:
        if line.get("branch", "false").lower() == "true":
            raise ValueError("Branch counters are missing")
        return 0, 0
    match = re.fullmatch(r"[0-9.]+% \((\d+)/(\d+)\)", value)
    if match is None:
        raise ValueError("Invalid branch counters")
    covered, valid = map(int, match.groups())
    if not 0 <= covered <= valid or valid == 0:
        raise ValueError("Branch counters are out of range")
    return covered, valid


def compose(documents: list[tuple[dict, ET.Element]]) -> tuple[ET.Element, dict]:
    """Keep every class/line point. Only merge repeated observations of that same point.

    Cobertura cannot identify which outcome a partial observation hit. Preserve
    the first observation's branch counts unless another observation of the same
    class/line proves every branch covered with the identical denominator.
    Different classes on a source line remain separate, as in the existing gate.
    """
    points: dict[tuple[str, str, str], dict[int, tuple[int, int, int]]] = {}
    original_counts = []
    for manifest, document in documents:
        count = 0
        for package in document.findall("packages/package"):
            for cls in package.findall("classes/class"):
                filename = relative_filename(cls.attrib["filename"], manifest["sourceRoot"])
                key = package.attrib["name"], cls.attrib["name"], filename
                lines = points.setdefault(key, {})
                for line in cls.findall("lines/line"):
                    number, hits = int(line.attrib["number"]), int(line.attrib["hits"])
                    if number <= 0 or hits < 0:
                        raise ValueError("Invalid source line or hit count")
                    covered, valid = branch_counts(line)
                    previous = lines.get(number)
                    if previous is None:
                        lines[number] = hits, covered, valid
                    else:
                        promote = valid == previous[2] and covered == valid
                        lines[number] = (max(previous[0], hits),
                                         covered if promote else previous[1], previous[2])
                    count += 1
        if count == 0:
            raise ValueError("Evidence contains no measurable class lines")
        original_counts.append(count)
    output = ET.Element("coverage", version="appsurface-verified-fanin-v1")
    packages = ET.SubElement(output, "packages")
    lc = lv = bc = bv = 0
    for (package_name, class_name, filename), lines in sorted(points.items()):
        package = ET.SubElement(packages, "package", name=package_name)
        cls = ET.SubElement(ET.SubElement(package, "classes"), "class", name=class_name, filename=filename)
        target = ET.SubElement(cls, "lines")
        for number, (hits, covered, valid) in sorted(lines.items()):
            line = ET.SubElement(target, "line", number=str(number), hits=str(hits), branch=str(valid > 0).lower())
            if valid:
                line.set("condition-coverage", f"{covered * 100 / valid:g}% ({covered}/{valid})")
            lv += 1
            lc += int(hits > 0)
            bc += covered
            bv += valid
    output.attrib.update({"lines-covered": str(lc), "lines-valid": str(lv),
                          "branches-covered": str(bc), "branches-valid": str(bv),
                          "line-rate": str(lc / lv), "branch-rate": str(bc / bv if bv else 1)})
    return output, {"inputClassLines": original_counts, "retainedClassLines": lv,
                    "linesCovered": lc, "branchesValid": bv, "branchesCovered": bc}


def merge(root: Path, primary: Path, native: Path, output: Path, configuration: str) -> None:
    identity = source_identity(root)
    first = load_evidence(root, primary, identity, configuration)
    if first[0]["scope"] != "solution":
        raise ValueError("A complete solution primary is mandatory")
    paths = sorted(native.glob("*/manifest.json"))
    supplements = [load_evidence(root, item.parent, identity, configuration) for item in paths]
    if len(supplements) != 3 or {m["platform"] for m, _ in supplements} != PLATFORMS:
        raise ValueError("Exactly one successful native shard per Linux, Windows and macOS is required")
    if any(m["scope"] != "native" or m["run"] != first[0]["run"] for m, _ in supplements):
        raise ValueError("Evidence must come from the same workflow run and attempt")
    if not all(first[0]["run"].values()):
        raise ValueError("Cross-platform acceptance requires workflow provenance")
    if output.exists():
        raise ValueError("Fan-in destination must be fresh")
    result, inventory = compose([first, *supplements])
    output.mkdir(parents=True)
    ET.ElementTree(result).write(output / "coverage.cobertura.xml", encoding="utf-8", xml_declaration=True)
    # Preserve the primary XML and its aggregate counts; only filenames change so
    # its patch denominator can be evaluated on a different checkout host.
    for cls in first[1].findall("packages/package/classes/class"):
        cls.set("filename", relative_filename(cls.attrib["filename"], first[0]["sourceRoot"]))
    ET.ElementTree(first[1]).write(output / "primary-paths.cobertura.xml", encoding="utf-8", xml_declaration=True)
    receipt = {**identity, "configuration": configuration, "policy": POLICY,
               "run": first[0]["run"], "inventory": inventory,
               "inputs": [{"manifestSha256": digest(item / "manifest.json"),
                            "manifest": m} for item, (m, _) in zip([primary, *[p.parent for p in paths]], [first, *supplements])],
               "outputSha256": digest(output / "coverage.cobertura.xml"),
               "primaryPathsSha256": digest(output / "primary-paths.cobertura.xml")}
    (output / "verification.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("capture", "merge"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--configuration", choices=("Debug", "Release"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--scope", choices=("solution", "native"))
    parser.add_argument("--timings", type=Path)
    parser.add_argument("--primary", type=Path)
    parser.add_argument("--native", type=Path)
    args = parser.parse_args()
    if args.mode == "capture":
        if args.report is None or args.scope is None:
            parser.error("capture requires --report and --scope")
        capture(args.root, args.report, args.output, args.configuration, args.scope, args.timings)
    else:
        if args.primary is None or args.native is None:
            parser.error("merge requires --primary and --native")
        merge(args.root, args.primary, args.native, args.output, args.configuration)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, ET.ParseError, subprocess.CalledProcessError) as error:
        print(f"Coverage fan-in rejected: {error}", file=sys.stderr)
        sys.exit(1)
