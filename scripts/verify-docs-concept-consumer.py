#!/usr/bin/env python3
"""Pack and consume AppSurface Docs while checking the #183 core boundary.

The default path uses PackageIndex's verified package-artifact workflow, then
restores a separate consumer from those exact local artifacts. It also compiles
the concept source files without package references and checks the evaluated
Release project-reference graph. No output is written into the consumer fixture.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parents[1]
PACKAGE_INDEX_PROJECT = ROOT / "tools/ForgeTrust.AppSurface.PackageIndex/ForgeTrust.AppSurface.PackageIndex.csproj"
RELEASE_PROJECT = ROOT / "tools/ForgeTrust.AppSurface.Release/ForgeTrust.AppSurface.Release.csproj"
RELEASE_CONTRACTS_DIR = ROOT / "tools/ForgeTrust.AppSurface.ReleaseContracts"
CONSUMER_FIXTURE = ROOT / "tests/fixtures/docs-concept-consumer"
DOCS_PACKAGE = "ForgeTrust.AppSurface.Docs"
CONTRACTS_PACKAGE = "ForgeTrust.AppSurface.ReleaseContracts"
CONCEPT_TYPE_PREFIX = "DocConcept"
VERSION_PATTERN = re.compile(
    r"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
    r"(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)"
    r"(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?\Z"
)


class VerificationError(RuntimeError):
    """Raised when package or dependency-boundary evidence is incomplete."""


def validate_package_version(version: str) -> None:
    if not VERSION_PATTERN.fullmatch(version):
        raise VerificationError(
            "--package-version must be a stable or prerelease SemVer value without build metadata."
        )


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def declared_dependency_versions(package_path: Path, dependency_id: str) -> list[str]:
    """Read every nuspec dependency declaration for one package id."""
    try:
        with zipfile.ZipFile(package_path) as archive:
            nuspec_names = [name for name in archive.namelist() if name.lower().endswith(".nuspec")]
            if len(nuspec_names) != 1:
                raise VerificationError(
                    f"{package_path.name} must contain exactly one nuspec; found {len(nuspec_names)}."
                )
            root = ET.fromstring(archive.read(nuspec_names[0]))
    except (OSError, zipfile.BadZipFile, ET.ParseError) as exc:
        raise VerificationError(f"Could not inspect {package_path}: {exc}") from exc

    wanted = dependency_id.casefold()
    return [
        dependency.attrib.get("version", "")
        for dependency in root.iter()
        if local_name(dependency.tag) == "dependency"
        and dependency.attrib.get("id", "").casefold() == wanted
    ]


def dependency_minimum_version(version_spec: str) -> str:
    """Return the declared minimum/exact version from a nuspec version spec."""
    value = version_spec.strip()
    if not value:
        return ""
    if value[0] in "[(":
        if len(value) < 3 or value[-1] not in ")]":
            return ""
        if value[0] == "(":
            return ""
        body = value[1:-1]
        if "," not in body:
            return body.strip() if value[0] == "[" and value[-1] == "]" else ""
        return body.split(",", 1)[0].strip()
    if any(character in value for character in "*[,()]"):
        return ""
    return value


def require_dependency_pin(package_path: Path, dependency_id: str, expected_version: str) -> None:
    versions = declared_dependency_versions(package_path, dependency_id)
    if not versions:
        raise VerificationError(
            f"{package_path.name} does not declare its {dependency_id} dependency."
        )
    for declared in versions:
        if dependency_minimum_version(declared) != expected_version:
            raise VerificationError(
                f"{package_path.name} declares {dependency_id} version '{declared}'; "
                f"the packed graph must start at {expected_version}."
            )


def read_assets_resolution(assets_path: Path, package_id: str) -> list[tuple[str, str]]:
    """Return the resolved version and library kind for a NuGet package id."""
    try:
        assets = json.loads(assets_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise VerificationError(f"Could not read consumer restore graph {assets_path}: {exc}") from exc

    wanted = package_id.casefold()
    matches: list[tuple[str, str]] = []
    for library_key, library in assets.get("libraries", {}).items():
        resolved_id, separator, version = library_key.rpartition("/")
        if separator and resolved_id.casefold() == wanted:
            matches.append((version, library.get("type", "")))
    return matches


def require_assets_package(assets_path: Path, package_id: str, expected_version: str) -> None:
    matches = read_assets_resolution(assets_path, package_id)
    if matches != [(expected_version, "package")]:
        raise VerificationError(
            f"{assets_path} resolved {package_id} as {matches}; expected exactly "
            f"{expected_version} from a NuGet package."
        )


def require_locked_package(lock_path: Path, package_id: str, expected_version: str) -> None:
    try:
        lock = json.loads(lock_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise VerificationError(f"Could not read consumer lock file {lock_path}: {exc}") from exc

    matches = [
        dependency
        for framework in lock.get("dependencies", {}).values()
        for resolved_id, dependency in framework.items()
        if resolved_id.casefold() == package_id.casefold()
    ]
    if len(matches) != 1 or matches[0].get("resolved") != expected_version:
        actual = [dependency.get("resolved") for dependency in matches]
        raise VerificationError(
            f"{lock_path} locks {package_id} as {actual}; expected one exact {expected_version} entry."
        )


def require_restored_local_artifact(
    artifacts_dir: Path,
    package_cache: Path,
    package_id: str,
    package_version: str,
) -> None:
    packed_path = artifacts_dir / f"{package_id}.{package_version}.nupkg"
    cache_dir = package_cache / package_id.casefold() / package_version.casefold()
    restored_path = cache_dir / f"{package_id.casefold()}.{package_version.casefold()}.nupkg"
    metadata_path = cache_dir / ".nupkg.metadata"
    if not packed_path.is_file() or not restored_path.is_file() or not metadata_path.is_file():
        raise VerificationError(
            f"The exact {package_id}/{package_version} packed and restored artifacts are incomplete."
        )
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise VerificationError(f"Could not read NuGet restore metadata for {package_id}: {exc}") from exc
    if Path(metadata.get("source", "")).resolve() != artifacts_dir.resolve():
        raise VerificationError(f"{package_id}/{package_version} was not restored from the fresh local feed.")
    if packed_path.read_bytes() != restored_path.read_bytes():
        raise VerificationError(
            f"The restored {package_id}/{package_version} bytes differ from the freshly packed artifact."
        )


def msbuild_project_references(project_file: Path, dotnet: str = "dotnet") -> list[Path]:
    """Ask MSBuild for evaluated ProjectReference items, including imported props/targets."""
    command = [
        dotnet, "msbuild", str(project_file), "-nologo", "-property:Configuration=Release",
        "-getItem:ProjectReference",
    ]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise VerificationError(
            f"MSBuild could not evaluate {project_file}: {result.stderr.strip() or result.stdout.strip()}"
        )
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise VerificationError(f"MSBuild returned no ProjectReference JSON for {project_file}.") from exc

    items = data.get("Items", {}).get("ProjectReference")
    if items is None:
        raise VerificationError(f"MSBuild omitted evaluated ProjectReference items for {project_file}.")
    references: list[Path] = []
    for item in items:
        identity = item.get("FullPath") or item.get("Identity")
        if not identity:
            raise VerificationError(f"MSBuild returned a ProjectReference without a path for {project_file}.")
        reference = Path(identity)
        if not reference.is_absolute():
            reference = project_file.parent / reference
        references.append(reference.resolve())
    return references


def inspect_release_project_graph(
    root_project: Path = RELEASE_PROJECT,
    reference_reader=msbuild_project_references,
    repository_root: Path = ROOT,
) -> list[Path]:
    """Traverse the evaluated Release reference graph and reject Docs/Web projects."""
    pending = [root_project.resolve()]
    visited: set[Path] = set()
    graph: list[Path] = []
    while pending:
        project = pending.pop()
        if project in visited:
            continue
        visited.add(project)
        graph.append(project)
        try:
            relative = project.relative_to(repository_root.resolve())
        except ValueError:
            raise VerificationError(f"Release references a project outside the repository: {project}")
        if relative.parts and relative.parts[0].casefold() == "web":
            raise VerificationError(f"Release project graph includes a Web project: {relative.as_posix()}")
        lowered_name = project.stem.casefold()
        if lowered_name.startswith("forgetrust.appsurface.docs") or lowered_name.startswith("forgetrust.appsurface.web"):
            raise VerificationError(f"Release project graph includes a Docs/Web project: {project.name}")
        for reference in reference_reader(project):
            if not reference.is_file():
                raise VerificationError(f"Release project graph contains a missing project: {reference}")
            pending.append(reference)
    return graph


def create_nuget_config(path: Path, local_feed: Path) -> None:
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", {"key": "appsurface-local", "value": str(local_feed)})
    ET.SubElement(sources, "add", {"key": "nuget.org", "value": "https://api.nuget.org/v3/index.json"})
    mapping = ET.SubElement(configuration, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", {"key": "appsurface-local"})
    ET.SubElement(local, "package", {"pattern": "ForgeTrust.*"})
    public = ET.SubElement(mapping, "packageSource", {"key": "nuget.org"})
    ET.SubElement(public, "package", {"pattern": "*"})
    path.write_bytes(ET.tostring(configuration, encoding="utf-8", xml_declaration=True))


def run_checked(command: list[str], cwd: Path, environment: dict[str, str], label: str) -> None:
    print(f"[{label}] {' '.join(command)}", flush=True)
    result = subprocess.run(command, cwd=cwd, env=environment, check=False)
    if result.returncode != 0:
        raise VerificationError(f"{label} failed with exit code {result.returncode}.")


def build_package_artifacts(work_dir: Path, package_version: str, environment: dict[str, str]) -> Path:
    artifacts = work_dir / "package-artifacts"
    artifacts.mkdir()
    command = [
        "dotnet", "run", "--project", str(PACKAGE_INDEX_PROJECT), "--configuration", "Release", "--",
        "verify-packages", "--repo-root", str(ROOT), "--package-version", package_version,
        "--artifacts-output", str(artifacts),
        "--artifact-manifest", str(work_dir / "package-artifact-manifest.json"),
        "--report", str(work_dir / "package-artifact-report.md"),
        "--coverage-proof-work-dir", str(artifacts / "coverage-cli-consumer-proof"),
        "--coverage-proof-report", str(artifacts / "coverage-cli-consumer-proof.md"),
        "--docs-proof-work-dir", str(artifacts / "docs-package-consumer-proof"),
        "--docs-proof-report", str(artifacts / "docs-package-consumer-proof.md"),
    ]
    run_checked(command, ROOT, environment, "PackageIndex pack and artifact verification")
    return artifacts


def build_bcl_only_concept_core(work_dir: Path, environment: dict[str, str]) -> None:
    concept_sources = sorted(RELEASE_CONTRACTS_DIR.glob(f"{CONCEPT_TYPE_PREFIX}*.cs"))
    if not concept_sources:
        raise VerificationError(
            f"No {CONCEPT_TYPE_PREFIX}*.cs sources were found in ReleaseContracts; BCL-only proof would be empty."
        )
    project_dir = work_dir / "bcl-only-core"
    project_dir.mkdir()
    project_file = project_dir / "ConceptCore.BclOnly.csproj"
    compile_items = [
        f'    <Compile Include="{source}" Link="{source.name}" />'
        for source in [RELEASE_CONTRACTS_DIR / "PackageReleaseLink.cs", *concept_sources]
    ]
    if not (RELEASE_CONTRACTS_DIR / "PackageReleaseLink.cs").is_file():
        raise VerificationError("The existing BCL release-link contract source is missing.")
    project_file.write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        "  <PropertyGroup>\n"
        "    <TargetFramework>net10.0</TargetFramework>\n"
        "    <Nullable>enable</Nullable>\n"
        "    <ImplicitUsings>enable</ImplicitUsings>\n"
        "    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>\n"
        "    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n"
        "  </PropertyGroup>\n"
        "  <ItemGroup>\n" + "\n".join(compile_items) + "\n  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    run_checked(["dotnet", "build", str(project_file), "--configuration", "Release"], project_dir, environment,
                "BCL-only concept core compilation")


def restore_build_and_run_consumer(
    work_dir: Path,
    artifacts_dir: Path,
    package_version: str,
    environment: dict[str, str],
) -> None:
    docs_package_path = artifacts_dir / f"{DOCS_PACKAGE}.{package_version}.nupkg"
    contracts_package_path = artifacts_dir / f"{CONTRACTS_PACKAGE}.{package_version}.nupkg"
    if not docs_package_path.is_file() or not contracts_package_path.is_file():
        raise VerificationError("The exact packed Docs and ReleaseContracts files are required in the local feed.")
    require_dependency_pin(docs_package_path, CONTRACTS_PACKAGE, package_version)

    consumer = work_dir / "consumer"
    shutil.copytree(CONSUMER_FIXTURE, consumer)
    config_path = consumer / "NuGet.config"
    create_nuget_config(config_path, artifacts_dir)
    project = consumer / "DocsConceptConsumer.csproj"
    package_cache = work_dir / "consumer-package-cache"
    consumer_environment = {
        **environment,
        "NUGET_PACKAGES": str(package_cache),
        "DOTNET_CLI_HOME": str(work_dir / "consumer-dotnet-home"),
        "NUGET_HTTP_CACHE_PATH": str(work_dir / "consumer-http-cache"),
    }
    package_cache.mkdir()
    command_prefix = ["dotnet", "restore", str(project), "--configfile", str(config_path),
                      "-p:AppSurfacePackageVersion=" + package_version]
    run_checked([*command_prefix, "--force-evaluate"], consumer, consumer_environment,
                "Clean packed Docs consumer lock generation")
    run_checked([*command_prefix, "--locked-mode"], consumer, consumer_environment,
                "Clean packed Docs consumer locked restore")

    assets_path = consumer / "obj/project.assets.json"
    lock_path = consumer / "packages.lock.json"
    for package_id in (DOCS_PACKAGE, CONTRACTS_PACKAGE):
        require_assets_package(assets_path, package_id, package_version)
        require_locked_package(lock_path, package_id, package_version)
        require_restored_local_artifact(artifacts_dir, package_cache, package_id, package_version)

    run_checked(
        ["dotnet", "build", str(project), "--configuration", "Release", "--no-restore",
         "-p:AppSurfacePackageVersion=" + package_version],
        consumer,
        consumer_environment,
        "Packed Docs consumer compilation",
    )
    run_checked(
        ["dotnet", str(consumer / "bin/Release/net10.0/DocsConceptConsumer.dll")],
        consumer,
        consumer_environment,
        "Packed Docs consumer public-surface check",
    )


def parse_arguments(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--package-version", required=True,
        help="Exact coordinated version used by PackageIndex pack and the consumer fixture.",
    )
    parser.add_argument(
        "--artifacts", type=Path,
        help="Reuse a package-artifact directory produced by PackageIndex verify-packages instead of packing it here.",
    )
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    arguments = parse_arguments(sys.argv[1:] if argv is None else argv)
    try:
        validate_package_version(arguments.package_version)
        initial_head = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True
        ).stdout.strip()
        environment = {
            **os.environ,
            "NUGET_PACKAGES": "",
        }
        with tempfile.TemporaryDirectory(prefix="appsurface-docs-concept-consumer-") as temporary:
            work_dir = Path(temporary)
            package_cache = work_dir / "package-index-package-cache"
            package_cache.mkdir()
            environment["NUGET_PACKAGES"] = str(package_cache)
            environment["DOTNET_CLI_HOME"] = str(work_dir / "package-index-dotnet-home")
            environment["NUGET_HTTP_CACHE_PATH"] = str(work_dir / "package-index-http-cache")
            artifacts_dir = arguments.artifacts.resolve() if arguments.artifacts else build_package_artifacts(
                work_dir, arguments.package_version, environment
            )
            docs_package_path = artifacts_dir / f"{DOCS_PACKAGE}.{arguments.package_version}.nupkg"
            contracts_package_path = artifacts_dir / f"{CONTRACTS_PACKAGE}.{arguments.package_version}.nupkg"
            if not docs_package_path.is_file() or not contracts_package_path.is_file():
                raise VerificationError(
                    f"Expected exact artifacts {docs_package_path.name} and {contracts_package_path.name}."
                )
            require_dependency_pin(docs_package_path, CONTRACTS_PACKAGE, arguments.package_version)
            graph = inspect_release_project_graph()
            print(f"Release project-reference graph contains {len(graph)} projects and no Docs/Web project.")
            build_bcl_only_concept_core(work_dir, environment)
            restore_build_and_run_consumer(work_dir, artifacts_dir, arguments.package_version, environment)
        final_head = subprocess.run(
            ["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True
        ).stdout.strip()
        if final_head != initial_head:
            raise VerificationError(f"HEAD changed during verification ({initial_head} -> {final_head}).")
        print("Packed Docs concept consumer and dependency-boundary proof passed.")
        return 0
    except (VerificationError, OSError, subprocess.CalledProcessError) as exc:
        print(f"Docs concept consumer verification failed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
