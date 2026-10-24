#!/usr/bin/env python3
"""Repository-only, conservative coverage fan-in. See guides/coverage-fan-in.md."""

from __future__ import annotations

import argparse
from contextlib import ExitStack
import hashlib
import json
import os
import platform
import re
import shutil
import stat
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
    "normalization": "appsurface-coverage-merge",
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


def verify_normalization(raw: ET.Element, normalized: ET.Element, source_root: str) -> None:
    """Require the existing merger to retain every collector source point and hit.

    ReportGenerator folds compiler-generated class names. Compare file/line
    execution across that transformation, then retain normalized class records
    unchanged for conservative composition. Never discard a native-only point.
    """
    def observations(document: ET.Element) -> dict[tuple[str, int], bool]:
        points: dict[tuple[str, int], bool] = {}
        for cls in document.findall("packages/package/classes/class"):
            filename = relative_filename(cls.attrib["filename"], source_root)
            for line in cls.findall("lines/line"):
                number, hits = int(line.attrib["number"]), int(line.attrib["hits"])
                if number <= 0 or hits < 0:
                    raise ValueError("Invalid source line or hit count")
                key = filename, number
                points[key] = points.get(key, False) or hits > 0
        return points

    before, after = observations(raw), observations(normalized)
    if not before or any(point not in after or hit and not after[point]
                         for point, hit in before.items()):
        raise ValueError("Native normalization removed source points or observed execution")
    if int(normalized.attrib["branches-valid"]) < int(raw.attrib["branches-valid"]):
        raise ValueError("Native normalization reduced measurable branches")


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
            timings: Path | None = None, raw_report: Path | None = None) -> None:
    """Snapshot successful producer bytes; replace only a verified default solution capture.

    Custom/native destinations stay fresh-only. Manifests are local CI evidence,
    not signatures; replacement never makes previous source evidence current.
    The destination cannot contain any producer input that replacement would retire.
    """
    identity = source_identity(root)
    destination = output.resolve()
    if any(source is not None and source.resolve().is_relative_to(destination)
           for source in (report, timings, raw_report)):
        raise ValueError("Capture destination must not contain producer inputs")
    document = checked_xml(report)
    if scope == "solution":
        if timings is None:
            raise ValueError("Full solution timing inventory is required")
        verify_solution(root, read_json(timings), configuration)
    elif scope == "native":
        if raw_report is None:
            raise ValueError("Native evidence requires the original collector report")
        verify_normalization(checked_xml(raw_report), document, str(root.resolve()))
    if scope == "solution" and output.absolute() == (root / "TestResults/coverage-merged/fanin-primary").absolute():
        # Validate the new producer before retiring any previous default capture.
        _prepare_default_output(root, output, ("TestResults", "coverage-merged", "fanin-primary"), primary=True)
    elif output.exists():
        raise ValueError("Evidence destination must be fresh")
    output.mkdir(parents=True)
    shutil.copyfile(report, output / "coverage.cobertura.xml")
    files = {"coverage.cobertura.xml": digest(output / "coverage.cobertura.xml")}
    if scope == "native":
        shutil.copyfile(raw_report, output / "collector.cobertura.xml")
        files["collector.cobertura.xml"] = digest(output / "collector.cobertura.xml")
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
    expected_files = ({"coverage.cobertura.xml", "timings.json"} if scope == "solution"
                      else {"coverage.cobertura.xml", "collector.cobertura.xml"})
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
    document = checked_xml(path / "coverage.cobertura.xml")
    if scope == "native":
        verify_normalization(checked_xml(path / "collector.cobertura.xml"), document,
                             manifest["sourceRoot"])
    return manifest, document


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
    """Preserve the class and method records consumed by the existing gate.

    The patch reader visits both line collections and sums their branch counts.
    Keep method overloads and repeated records distinct; dropping method lines
    would shrink its denominator even when the root aggregate stayed intact.
    Root counters continue to describe class lines, as in normalized Cobertura.
    Only a fully covered observation with the same denominator promotes a point;
    partial observations are never added together to infer full coverage.
    """
    points: dict[tuple[str, str, str, int], dict] = {}
    original_counts, original_method_counts = [], []

    def observe(target: dict, source: ET.Element | None) -> int:
        occurrences: dict[int, int] = {}
        if source is None:
            return 0
        count = 0
        for line in source.findall("line"):
            number, hits = int(line.attrib["number"]), int(line.attrib["hits"])
            if number <= 0 or hits < 0:
                raise ValueError("Invalid source line or hit count")
            occurrence = occurrences.get(number, 0)
            occurrences[number] = occurrence + 1
            key = number, occurrence
            covered, valid = branch_counts(line)
            previous = target.get(key)
            if previous is None:
                target[key] = hits, covered, valid
            else:
                promote = valid == previous[2] and covered == valid
                target[key] = (max(previous[0], hits),
                               covered if promote else previous[1], previous[2])
            count += 1
        return count

    for manifest, document in documents:
        count = method_count = 0
        class_occurrences: dict[tuple[str, str, str], int] = {}
        for package in document.findall("packages/package"):
            for cls in package.findall("classes/class"):
                filename = relative_filename(cls.attrib["filename"], manifest["sourceRoot"])
                identity = package.attrib["name"], cls.attrib["name"], filename
                occurrence = class_occurrences.get(identity, 0)
                class_occurrences[identity] = occurrence + 1
                target = points.setdefault((*identity, occurrence), {"lines": {}, "methods": {}})
                count += observe(target["lines"], cls.find("lines"))
                method_occurrences: dict[tuple[str, str], int] = {}
                for method in cls.findall("methods/method"):
                    method_identity = method.get("name", ""), method.get("signature", "")
                    occurrence = method_occurrences.get(method_identity, 0)
                    method_occurrences[method_identity] = occurrence + 1
                    lines = target["methods"].setdefault((*method_identity, occurrence), {})
                    method_count += observe(lines, method.find("lines"))
        if count == 0:
            raise ValueError("Evidence contains no measurable class lines")
        original_counts.append(count)
        original_method_counts.append(method_count)

    def write_lines(parent: ET.Element, lines: dict) -> None:
        target = ET.SubElement(parent, "lines")
        for (number, _), (hits, covered, valid) in sorted(lines.items()):
            line = ET.SubElement(target, "line", number=str(number), hits=str(hits), branch=str(valid > 0).lower())
            if valid:
                line.set("condition-coverage", f"{covered * 100 / valid:g}% ({covered}/{valid})")

    output = ET.Element("coverage", version="appsurface-verified-fanin-v1")
    packages = ET.SubElement(output, "packages")
    lc = lv = bc = bv = retained_method_lines = 0
    for (package_name, class_name, filename, _), records in sorted(points.items()):
        package = ET.SubElement(packages, "package", name=package_name)
        cls = ET.SubElement(ET.SubElement(package, "classes"), "class", name=class_name, filename=filename)
        if records["methods"]:
            methods = ET.SubElement(cls, "methods")
            for (name, signature, _), lines in sorted(records["methods"].items()):
                method = ET.SubElement(methods, "method", name=name, signature=signature)
                write_lines(method, lines)
                retained_method_lines += len(lines)
        write_lines(cls, records["lines"])
        for hits, covered, valid in records["lines"].values():
            lv += 1
            lc += int(hits > 0)
            bc += covered
            bv += valid
    output.attrib.update({"lines-covered": str(lc), "lines-valid": str(lv),
                          "branches-covered": str(bc), "branches-valid": str(bv),
                          "line-rate": str(lc / lv), "branch-rate": str(bc / bv if bv else 1)})
    return output, {"inputClassLines": original_counts, "retainedClassLines": lv,
                    "inputMethodLines": original_method_counts, "retainedMethodLines": retained_method_lines,
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


def prepare_default_output(root: Path, output: Path) -> None:
    """Retire only a byte-verified previous composition at the wrapper's default path.

    Custom output paths remain caller-managed and fresh. Unknown files, modified
    reports and links prevent cleanup; validate the entire inventory before any
    unlink so a rejected preparation preserves all previous material. Cleanup
    uses retained no-follow directory handles; a parent rename cannot redirect
    its unlinks. Hosts without those capabilities must preserve existing output.
    """
    _prepare_default_output(root, output, ("TestResults", "coverage-fanin"), primary=False)


def _prepare_default_output(root: Path, output: Path, parts: tuple[str, ...], *, primary: bool) -> None:
    """Retire a complete hash-verified default capture/composition, never arbitrary files.

    Previous capture identity may be historical; new capture inputs have already
    passed current-source validation. These saved bytes grant cleanup authority
    only, and are never accepted as evidence for the next producer.
    """
    label = "primary evidence" if primary else "composition"
    expected = root.joinpath(*parts)
    if (output.absolute() != expected.absolute()
            or any(root.joinpath(*parts[:index]).is_symlink() for index in range(1, len(parts) + 1))):
        raise ValueError(f"Only the wrapper's non-linked default {label} can be prepared")
    if not output.exists():
        return
    required = {os.open, os.stat, os.unlink, os.rmdir}
    if not required <= os.supports_dir_fd or os.listdir not in os.supports_fd or not hasattr(os, "O_NOFOLLOW"):
        raise ValueError(f"Existing default {label} requires no-follow directory handles")
    flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
    with ExitStack() as handles:
        def directory(name: str | Path, parent: int | None = None) -> int:
            descriptor = os.open(name, flags, dir_fd=parent)
            handles.callback(os.close, descriptor)
            return descriptor

        root_fd = directory(root)
        bindings = []
        try:
            parent_fd = root_fd
            for name in parts[:-1]:
                descriptor = directory(name, parent_fd)
                bindings.append((name, parent_fd, descriptor))
                parent_fd = descriptor
            output_fd = directory(parts[-1], parent_fd)
        except OSError as error:
            raise ValueError(f"Only a non-linked default {label} can be prepared") from error
        receipt_name = "manifest.json" if primary else "verification.json"
        hashes = ({"coverage.cobertura.xml": None, "timings.json": None} if primary
                  else {"coverage.cobertura.xml": "outputSha256", "primary-paths.cobertura.xml": "primaryPathsSha256"})
        names = set(hashes) | {receipt_name}
        if set(os.listdir(output_fd)) != names:
            raise ValueError(f"Unknown {label} contents must be preserved")

        def regular_file(name: str):
            stream = handles.enter_context(os.fdopen(os.open(name, os.O_RDONLY | os.O_NOFOLLOW,
                                                             dir_fd=output_fd), "rb"))
            info = os.fstat(stream.fileno())
            limit = 1024 * 1024 if name.endswith(".json") else MAX_REPORT_BYTES
            if not stat.S_ISREG(info.st_mode) or not 0 < info.st_size <= limit:
                raise ValueError(f"Unknown {label} contents must be preserved")
            return stream

        receipt = json.loads(regular_file(receipt_name).read(1024 * 1024 + 1))
        if not isinstance(receipt, dict) or receipt.get("policy") != POLICY:
            raise ValueError(f"Modified {label} contents must be preserved")
        if primary and (receipt.get("schemaVersion") != SCHEMA or receipt.get("scope") != "solution"
                        or receipt.get("filter") is not None or not isinstance(receipt.get("files"), dict)
                        or set(receipt["files"]) != set(hashes)):
            raise ValueError("Modified primary evidence inventory must be preserved")
        for name, key in hashes.items():
            expected_hash = receipt["files"][name] if primary else receipt.get(key)
            if hashlib.file_digest(regular_file(name), "sha256").hexdigest() != expected_hash:
                raise ValueError(f"Modified {label} contents must be preserved")

        def bound(name: str, parent: int, descriptor: int) -> bool:
            current = os.stat(name, dir_fd=parent, follow_symlinks=False)
            retained = os.fstat(descriptor)
            return (current.st_dev, current.st_ino) == (retained.st_dev, retained.st_ino)

        if (any(not bound(name, parent, descriptor) for name, parent, descriptor in bindings)
                or not bound(parts[-1], parent_fd, output_fd)):
            raise ValueError(f"Default {label} directory changed; preserve its contents")
        for name in names:
            os.unlink(name, dir_fd=output_fd)
        if not bound(parts[-1], parent_fd, output_fd):
            raise ValueError(f"Default {label} directory changed before removal")
        os.rmdir(parts[-1], dir_fd=parent_fd)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("capture", "merge", "prepare-default"))
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--configuration", choices=("Debug", "Release"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--raw-report", type=Path)
    parser.add_argument("--scope", choices=("solution", "native"))
    parser.add_argument("--timings", type=Path)
    parser.add_argument("--primary", type=Path)
    parser.add_argument("--native", type=Path)
    args = parser.parse_args()
    if args.mode == "prepare-default":
        prepare_default_output(args.root, args.output)
    elif args.mode == "capture":
        if args.report is None or args.scope is None:
            parser.error("capture requires --report and --scope")
        capture(args.root, args.report, args.output, args.configuration, args.scope,
                args.timings, args.raw_report)
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
