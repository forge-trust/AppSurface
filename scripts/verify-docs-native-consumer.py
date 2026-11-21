#!/usr/bin/env python3
"""Build the packed Docs consumer and verify portable Tree-sitter assets."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


DOCS = "ForgeTrust.AppSurface.Docs"
ASSET_PACKAGE = "treesitter.dotnet"
TREE_SITTER = "TreeSitter.DotNet"
SDK_VERSION = "10.0.402"
HOST_ARCHITECTURES = {
    "linux-arm": "Arm",
    "linux-arm64": "Arm64",
    "linux-x64": "X64",
    "osx-arm64": "Arm64",
    "osx-x64": "X64",
    "win-arm64": "Arm64",
    "win-x64": "X64",
    "win-x86": "X86",
}
GRAPHS = ("Direct", "Transitive")
MODE_PROPERTY = "AppSurfaceDocsKeepAllTreeSitterRuntimes"
STARTUP = "APPSURFACE_916_STARTUP"
RESULT = "APPSURFACE_916_RESULT "
PARSER_UNAVAILABLE = "appsurfacedocs.python.parser_unavailable"


class VerificationError(RuntimeError):
    """An input or observable proof differed from the consumer contract."""


def require(condition, message):
    if not condition:
        raise VerificationError(message)


def read_json(path):
    return json.loads(path.read_bytes())


def write_json(path, value):
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(value, stream, indent=2, ensure_ascii=True)
        stream.write("\n")


def digest(path, algorithm="sha256"):
    checksum = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            checksum.update(chunk)
    return checksum.hexdigest()


def run_dotnet(dotnet, arguments, cwd):
    environment = os.environ.copy()
    environment["DOTNET_CLI_UI_LANGUAGE"] = "en"
    for name in ("DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_STARTUP_HOOKS"):
        environment.pop(name, None)
    result = subprocess.run(
        [dotnet, *arguments], cwd=cwd, env=environment, capture_output=True, encoding="utf-8",
        errors="replace", timeout=300, check=False,
    )
    if result.returncode:
        raise VerificationError(
            f"dotnet {arguments[0]} failed ({result.returncode}):\n{result.stdout}\n{result.stderr}"
        )
    return result.stdout


def require_sdk(dotnet, root, rid):
    version = run_dotnet(dotnet, ["--version"], root).strip()
    require(version == SDK_VERSION, f"Expected SDK {SDK_VERSION}, observed {version}")
    info = run_dotnet(dotnet, ["--info"], root)
    require(re.search(rf"^\s*RID:\s+{re.escape(rid)}\s*$", info, re.MULTILINE), f"SDK RID differs from {rid}")


def run_fixture(dotnet, output, graph, mode, rid):
    assembly = output / f"DocsNative{graph}.dll"
    require(not list(output.glob("*.runtimeconfig.dev.json")), f"Ambient probing config in {output}")
    stdout = run_dotnet(dotnet, [str(assembly), mode], output).splitlines()
    require(STARTUP in stdout, f"{graph} {mode} did not reach application startup")
    results = [json.loads(line[len(RESULT):]) for line in stdout if line.startswith(RESULT)]
    require(len(results) == 1, f"{graph} {mode} produced {len(results)} result records")
    result = results[0]
    require(result["mode"] == mode and result["rid"] == rid
            and result["architecture"] == HOST_ARCHITECTURES[rid],
            f"{graph} {mode} did not execute on {rid}")
    expected_health = {"inactive": "Empty", "harvest": "Healthy", "unavailable": "Failed"}[mode]
    require(result["health"] == expected_health, f"{graph} {mode}: unexpected health {result['health']}")
    require(result["hasFunctionDocstring"] is (mode == "harvest")
            and result["hasClassDocstring"] is (mode == "harvest"),
            f"{graph} {mode}: unexpected Python docstrings")
    if mode == "unavailable":
        require(result["parserDiagnostic"] == PARSER_UNAVAILABLE and result["parserSeverity"] == "Error",
                f"{graph}: missing strict parser-unavailable diagnostic")
    else:
        require(result["parserDiagnostic"] is None and result["parserSeverity"] is None,
                f"{graph} {mode}: unexpected parser diagnostic")
    return result


def configure_feed(path, artifacts):
    configuration = ET.Element("configuration")
    sources = ET.SubElement(configuration, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(artifacts))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")

    mappings = ET.SubElement(configuration, "packageSourceMapping")
    first_party = ET.SubElement(mappings, "packageSource", key="candidate")
    ET.SubElement(first_party, "package", pattern="ForgeTrust.*")
    public = ET.SubElement(mappings, "packageSource", key="nuget.org")
    ET.SubElement(public, "package", pattern="*")
    ET.ElementTree(configuration).write(path, encoding="utf-8", xml_declaration=True)


def candidate_package(artifacts, manifest_path):
    manifest = read_json(manifest_path)
    entries = [entry for entry in manifest["entries"] if entry["package_id"] == DOCS]
    require(len(entries) == 1, "Candidate manifest must contain exactly one Docs package")
    entry = entries[0]
    name = entry["artifact_file_name"]
    require(Path(name).name == name and "/" not in name and "\\" not in name, "Invalid Docs archive basename")
    version = manifest["package_version"]
    package = artifacts / name
    require(digest(package, "sha512") == entry["sha512"], "Docs package bytes differ from candidate manifest")
    with zipfile.ZipFile(package) as archive:
        archive.getinfo("buildTransitive/ForgeTrust.AppSurface.Docs.targets")
    return version, entry["sha512"]


def package_native_files(cache):
    package_versions = list((cache / ASSET_PACKAGE).iterdir())
    require(len(package_versions) == 1, "Expected a single restored Tree-sitter package version")
    root = package_versions[0]
    files = {path.relative_to(root).as_posix(): path for path in root.glob("runtimes/*/native/*") if path.is_file()}
    require(bool(files), "Tree-sitter package has no native files")
    return files


def assert_projection(output, graph, package_files, native_hashes, rid=None):
    manifest = read_json(output / f"DocsNative{graph}.deps.json")
    expected = {
        name for name in package_files
        if rid is None or name.split("/")[1] == rid
    }
    require(bool(expected), f"Native projection for {rid} is empty")
    copied = {name for name in package_files if (output / name).is_file()}
    require(copied == expected, f"Unexpected Tree-sitter files in {graph} output for {rid or 'keep-all'}")

    actual = set()
    for target in manifest["targets"].values():
        for name, library in target.items():
            if name.split("/")[0].casefold() == TREE_SITTER.casefold():
                runtime_targets = library.get("runtimeTargets", {})
                for path, asset in list(runtime_targets.items()):
                    if asset.get("assetType") == "native":
                        require(path.startswith(f"runtimes/{asset['rid']}/native/"), f"Unexpected native RID in {path}")
                        actual.add(path)
                        del runtime_targets[path]

    require(actual == expected, f"Tree-sitter dependency manifest differs from output in {graph}")
    require(all(digest(output / name) == native_hashes[name] for name in expected),
            f"Tree-sitter output bytes differ from package in {graph}")
    return manifest, sorted(expected), sum((output / name).stat().st_size for name in expected)


def exercise_graph(args, root, cache, config, version, graph):
    assembly = f"DocsNative{graph}"
    project = root / graph / f"{assembly}.csproj"
    identity = f"-p:AppSurfacePackageVersion={version}"
    run_dotnet(args.dotnet, ["restore", str(project), "--configfile", str(config), "--packages", str(cache),
                           identity, "--verbosity", "quiet"], root)
    manifest_for_restore = read_json(project.parent / "obj" / "project.assets.json")
    dependencies = manifest_for_restore["project"]["frameworks"]["net10.0"].get("dependencies", {})
    require((DOCS in dependencies) is (graph == "Direct"), f"Unexpected direct Docs reference in {graph}")
    files = package_native_files(cache)
    native_hashes = {name: digest(path) for name, path in files.items()}
    build = project.parent / "bin" / "Release" / "net10.0"
    publish = root / f"published-{graph}"
    publish_no_build = root / f"published-no-build-{graph}"
    snapshots = {}
    history = []

    # Reuse both obj/bin and publish directory across both policy transitions.
    for keep_all in (False, True, False):
        policy = [f"-p:{MODE_PROPERTY}=true"] if keep_all else []
        run_dotnet(args.dotnet, ["build", str(project), "--configuration", "Release", "--no-restore",
                                identity, *policy, "--verbosity", "quiet"], root)
        publish_arguments = ["publish", str(project), "--configuration", "Release",
                             identity, *policy, "--verbosity", "quiet"]
        run_dotnet(args.dotnet, [*publish_arguments, "--no-build", "--output", str(publish_no_build)], root)
        run_dotnet(args.dotnet, [*publish_arguments, "--no-restore", "--output", str(publish)], root)
        snapshot = {}
        for stage, output in (("build", build), ("publish", publish), ("publishNoBuild", publish_no_build)):
            dependency_graph, paths, output_bytes = assert_projection(
                output, graph, files, native_hashes, None if keep_all else args.rid,
            )
            if stage in snapshots:
                require(dependency_graph == snapshots[stage], f"Unrelated {graph} {stage} dependency entries changed")
            else:
                snapshots[stage] = dependency_graph
            observation = run_fixture(args.dotnet, output, graph, "harvest", args.rid)
            snapshot[stage] = {"nativePaths": paths, "nativeBytes": output_bytes, "process": observation}
        history.append({"keepAll": keep_all, **snapshot})
        if args.transfer_dir and len(history) in (2, 3):
            # Save the keep-all output and final host-only output from the same candidate.
            name = "keep-all" if keep_all else "default"
            shutil.copytree(publish, args.work / name / graph)

    return history


def archive_output(files, destination):
    with zipfile.ZipFile(destination, "x", zipfile.ZIP_DEFLATED, compresslevel=3) as archive:
        for path in sorted(files.rglob("*")):
            if path.is_file():
                archive.write(path, path.relative_to(files).as_posix())
    return digest(destination)


def host_proof(args):
    require(args.rid in HOST_ARCHITECTURES, f"Unsupported verification RID {args.rid}")
    require(args.transfer_dir is None or args.rid == "linux-x64", "Transfer producer must run on linux-x64")
    version, package_hash = candidate_package(args.artifacts, args.manifest)
    require_sdk(args.dotnet, args.fixture, args.rid)
    args.work.mkdir(parents=True)
    fixture = args.work / "consumer"
    shutil.copytree(args.fixture, fixture, ignore=shutil.ignore_patterns("bin", "obj"))
    config = args.work / "NuGet.config"
    configure_feed(config, args.artifacts)
    cache = args.work / "packages"
    histories = {graph: exercise_graph(args, fixture, cache, config, version, graph) for graph in GRAPHS}
    restored = cache / DOCS.casefold() / version.lower() / f"{DOCS.casefold()}.{version.lower()}.nupkg"
    require(digest(restored, "sha512") == package_hash, "Restore did not consume the frozen Docs artifact")

    record = {
        "producerArtifactId": args.producer_id,
        "sourceCommit": args.source_commit,
        "packageVersion": version,
        "docsPackageSha512": package_hash,
        "sdkVersion": SDK_VERSION,
        "rid": args.rid,
        "architecture": HOST_ARCHITECTURES[args.rid],
        "directBuild": True,
        "directPublish": True,
        "transitiveBuild": True,
        "transitivePublish": True,
        "histories": histories,
    }
    if args.transfer_dir:
        args.transfer_dir.mkdir(parents=True)
        record["defaultSha256"] = archive_output(args.work / "default", args.transfer_dir / "default.zip")
        record["keepAllSha256"] = archive_output(args.work / "keep-all", args.transfer_dir / "keep-all.zip")
        write_json(args.transfer_dir / "source.json", record)
    write_json(args.output, record)


def verified_extract(path, destination, expected_digest):
    require(digest(path) == expected_digest, f"Transferred archive differs: {path.name}")
    with zipfile.ZipFile(path) as archive:
        members = archive.infolist()
        for member in members:
            names = member.filename.split("/")
            require(len(names) > 1 and names[0] in GRAPHS
                    and all(name not in ("", ".", "..") and "\\" not in name and ":" not in name for name in names),
                    f"Unexpected transfer path {member.filename}")
        require(len({member.filename.casefold() for member in members}) == len(members),
                "Transfer archive contains duplicate paths")
        archive.extractall(destination)
    return expected_digest


def destination_proof(args):
    source = read_json(args.transfer_dir / "source.json")
    require(source["rid"] == "linux-x64"
            and source["producerArtifactId"] == args.producer_id
            and source["sourceCommit"] == args.source_commit
            and source["packageVersion"] == args.package_version, "Unexpected transfer producer identity")
    require_sdk(args.dotnet, args.transfer_dir, "win-x64")
    args.work.mkdir(parents=True)
    paths = {name: args.work / name for name in ("default", "keep-all")}
    default_sha = verified_extract(args.transfer_dir / "default.zip", paths["default"], source["defaultSha256"])
    keep_all_sha = verified_extract(args.transfer_dir / "keep-all.zip", paths["keep-all"], source["keepAllSha256"])
    require(default_sha != keep_all_sha, "Default and keep-all transfer archives must differ")
    traces = {}
    for graph in GRAPHS:
        traces[graph] = [
            run_fixture(args.dotnet, paths["default"] / graph, graph, "inactive", "win-x64"),
            run_fixture(args.dotnet, paths["default"] / graph, graph, "unavailable", "win-x64"),
            run_fixture(args.dotnet, paths["keep-all"] / graph, graph, "harvest", "win-x64"),
        ]
    write_json(args.output, {
        **{key: source[key] for key in ("producerArtifactId", "sourceCommit", "packageVersion",
                                        "docsPackageSha512", "sdkVersion", "defaultSha256", "keepAllSha256")},
        "sourceRid": "linux-x64",
        "destinationRid": "win-x64",
        "sourceArtifactId": args.transfer_artifact_id,
        "observedDefaultSha256": default_sha,
        "observedKeepAllSha256": keep_all_sha,
        "defaultStartup": True,
        "defaultParserUnavailable": True,
        "keepAllHarvest": True,
        "traces": traces,
    })


def resolve_artifacts(args):
    pages = read_json(args.listing)
    prefix = f"docs-native-{args.run_id}-{args.attempt}-"
    required = {"transfer": prefix + "transfer"}
    if args.kind == "receipts":
        required.update({rid: prefix + rid for rid in HOST_ARCHITECTURES})
        required["destination"] = prefix + "destination"

    selected = {}
    for key, name in required.items():
        matches = [
            artifact for page in pages for artifact in page["artifacts"]
            if artifact["name"] == name
        ]
        require(len(matches) == 1 and not matches[0]["expired"], f"Missing or ambiguous artifact {name}")
        identifier = matches[0]["id"]
        require(isinstance(identifier, int) and identifier > 0, f"Invalid artifact ID for {name}")
        selected[key] = {"name": name, "id": str(identifier)}

    write_json(args.output, selected)


def aggregate_proof(args):
    references = read_json(args.references)
    required = {*HOST_ARCHITECTURES, "transfer", "destination"}
    require(set(references) == required, "Incomplete Docs proof artifact references")
    require(len({reference["id"] for reference in references.values()}) == len(required),
            "Docs proof contains duplicate artifact IDs")
    common = {
        "producerArtifactId": args.producer_id,
        "sourceCommit": args.source_commit,
        "packageVersion": args.package_version,
        "sdkVersion": SDK_VERSION,
    }

    def receipt(key, name):
        path = args.receipts / references[key]["name"] / name
        record = read_json(path)
        for field, expected in common.items():
            require(record[field] == expected, f"{key} proof differs from candidate {field}")
        return record, digest(path)

    hosts = []
    flags = ("directBuild", "directPublish", "transitiveBuild", "transitivePublish")
    linux_source = None
    for rid, architecture in HOST_ARCHITECTURES.items():
        record, receipt_hash = receipt(rid, "host.json")
        require(record["rid"] == rid and record["architecture"] == architecture,
                f"Receipt identity differs from {rid}/{architecture}")
        require(all(record[flag] is True for flag in flags), f"Incomplete host proof for {rid}")
        package_hash = common.setdefault("docsPackageSha512", record["docsPackageSha512"])
        require(record["docsPackageSha512"] == package_hash, f"Different Docs package on {rid}")
        hosts.append({
            "rid": rid,
            "architecture": architecture,
            "artifactId": references[rid]["id"],
            "receiptSha256": receipt_hash,
            **{flag: True for flag in flags},
        })
        if rid == "linux-x64":
            linux_source = record

    destination, _ = receipt("destination", "destination.json")
    require(destination["sourceRid"] == "linux-x64" and destination["destinationRid"] == "win-x64",
            "Unexpected transfer platform pair")
    require(destination["sourceArtifactId"] == references["transfer"]["id"],
            "Windows proof refers to a different transfer artifact")
    for declared, observed in (
        ("defaultSha256", "observedDefaultSha256"),
        ("keepAllSha256", "observedKeepAllSha256"),
    ):
        require(destination[declared] == linux_source[declared] == destination[observed],
                f"Transferred {declared} differs from the Linux publisher")
    transfer_fields = (
        "sourceRid", "destinationRid", "sourceArtifactId", "defaultSha256", "keepAllSha256",
        "observedDefaultSha256", "observedKeepAllSha256", "defaultStartup",
        "defaultParserUnavailable", "keepAllHarvest",
    )
    require(all(destination[name] is True for name in transfer_fields[-3:]),
            "Windows receipt has a failed startup or parser case")
    write_json(args.output, {
        "schema": "appsurface-docs-native-proof-v1",
        **common,
        "hosts": hosts,
        "crossPlatform": {
            **{field: destination[field] for field in transfer_fields},
            "destinationArtifactId": references["destination"]["id"],
        },
    })


def parse_arguments(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    host = commands.add_parser("host", help="Build and run both candidate consumer graphs")
    host.add_argument("--artifacts", type=Path, required=True)
    host.add_argument("--manifest", type=Path, required=True)
    host.add_argument("--fixture", type=Path, required=True)
    host.add_argument("--rid", choices=HOST_ARCHITECTURES, required=True)
    host.add_argument("--producer-id", required=True)
    host.add_argument("--source-commit", required=True)
    host.add_argument("--transfer-dir", type=Path)
    destination = commands.add_parser("destination", help="Run exact Linux publish archives on Windows")
    destination.add_argument("--transfer-dir", type=Path, required=True)
    destination.add_argument("--transfer-artifact-id", required=True)
    destination.add_argument("--producer-id", required=True)
    destination.add_argument("--source-commit", required=True)
    destination.add_argument("--package-version", required=True)
    for command in (host, destination):
        command.add_argument("--dotnet", default="dotnet")
        command.add_argument("--work", type=Path, required=True)
        command.add_argument("--output", type=Path, required=True)
    resolve = commands.add_parser("resolve-artifacts", help="Select exact current-attempt artifact IDs")
    resolve.add_argument("--listing", type=Path, required=True)
    resolve.add_argument("--run-id", required=True)
    resolve.add_argument("--attempt", required=True)
    resolve.add_argument("--kind", choices=("transfer", "receipts"), required=True)
    resolve.add_argument("--output", type=Path, required=True)
    aggregate = commands.add_parser("aggregate", help="Bind all host receipts and the Windows execution")
    aggregate.add_argument("--receipts", type=Path, required=True)
    aggregate.add_argument("--references", type=Path, required=True)
    aggregate.add_argument("--producer-id", required=True)
    aggregate.add_argument("--source-commit", required=True)
    aggregate.add_argument("--package-version", required=True)
    aggregate.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    for key, value in vars(args).items():
        if isinstance(value, Path):
            setattr(args, key, value.resolve())
    return args


def main(argv=None):
    args = parse_arguments(argv)
    try:
        commands = {
            "host": host_proof,
            "destination": destination_proof,
            "resolve-artifacts": resolve_artifacts,
            "aggregate": aggregate_proof,
        }
        commands[args.command](args)
        return 0
    except (VerificationError, OSError, KeyError, TypeError, ValueError, subprocess.SubprocessError) as error:
        print(f"Docs native consumer proof failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
