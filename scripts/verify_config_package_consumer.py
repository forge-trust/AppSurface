#!/usr/bin/env python3
"""Pack and consume the coordinated Config public API in an isolated, project-reference-free application."""

import argparse
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, help="Existing packed package directory; otherwise pack candidate sources.")
    parser.add_argument("--package-version", default="0.1.0-config-contract.local")
    parser.add_argument("--work-directory", type=Path)
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    work = (args.work_directory or Path(tempfile.mkdtemp(prefix="appsurface-config-consumer-"))).resolve()
    work.mkdir(parents=True, exist_ok=True)
    consumer = work / "consumer"
    if consumer.exists():
        raise SystemExit("Consumer directory already exists; select a fresh work directory.")
    consumer.mkdir()
    artifacts = args.artifacts.resolve() if args.artifacts else work / "packages"
    artifacts.mkdir(parents=True, exist_ok=True)
    timings = {}
    environment = os.environ.copy()
    environment.update({
        "NUGET_PACKAGES": str(work / "nuget-packages"),
        "NUGET_HTTP_CACHE_PATH": str(work / "http-cache"),
        "DOTNET_CLI_HOME": str(work / "dotnet-home"),
        "DOTNET_NOLOGO": "1",
        "DOTNET_SKIP_FIRST_TIME_EXPERIENCE": "1",
    })
    # Sandboxed macOS hosts can stall native filesystem watchers during Generic Host construction.
    # Polling preserves normal configuration reload semantics for this verification subprocess.
    if platform.system() == "Darwin":
        environment["DOTNET_USE_POLLING_FILE_WATCHER"] = "1"
    # Consumer proof owns its environment, not the calling shell's override values.
    for name in list(environment):
        if name.upper().startswith(("PAYMENTS", "PRODUCTION_PAYMENTS", "PRODUCTION__PAYMENTS", "EXTERNAL", "PRODUCTION__EXTERNAL")):
            del environment[name]

    def run(stage, command, cwd=repo, env=None):
        started = time.monotonic()
        try:
            result = subprocess.run(command, cwd=cwd, env=env or environment,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=300)
        except subprocess.TimeoutExpired as failure:
            output = failure.stdout or ""
            if isinstance(output, bytes):
                output = output.decode("utf-8", errors="replace")
            (work / f"{stage}.log").write_text(output)
            timings[stage] = round(time.monotonic() - started, 3)
            raise RuntimeError(f"{stage} timed out; inspect {work / (stage + '.log')}") from failure
        (work / f"{stage}.log").write_text(result.stdout)
        timings[stage] = round(time.monotonic() - started, 3)
        if result.returncode:
            raise RuntimeError(f"{stage} failed with exit {result.returncode}; inspect {work / (stage + '.log')}")
        return result.stdout

    package_ids = ["ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Config", "ForgeTrust.AppSurface.Config.Testing"]
    evidence = {"os": platform.platform(), "packageVersion": args.package_version,
                "cacheState": "isolated-cold", "stagesSeconds": timings, "result": "incomplete"}
    try:
        evidence["sdk"] = run("sdk", ["dotnet", "--version"]).strip()
        if not args.artifacts:
            for package_id in package_ids:
                project = repo / ("" if package_id.endswith("Core") else "Config") / package_id / (package_id + ".csproj")
                run("pack-" + package_id, ["dotnet", "pack", str(project), "--configuration", args.configuration,
                    "--output", str(artifacts), "-p:PackageVersion=" + args.package_version,
                    "-p:Version=" + args.package_version])
        for package_id in package_ids:
            if not (artifacts / f"{package_id}.{args.package_version}.nupkg").is_file():
                raise RuntimeError(f"Missing candidate package {package_id} at the requested version.")
        started = time.monotonic()
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable",
                            "Nullable": "enable", "RestorePackagesWithLockFile": "true"}.items():
            ET.SubElement(properties, name).text = value
        references = ET.SubElement(project, "ItemGroup")
        for package_id in ["ForgeTrust.AppSurface.Config", "ForgeTrust.AppSurface.Config.Testing"]:
            ET.SubElement(references, "PackageReference", Include=package_id, Version=args.package_version)
        ET.indent(project)
        ET.ElementTree(project).write(consumer / "Consumer.csproj", encoding="unicode")
        nuget = ET.Element("configuration")
        sources = ET.SubElement(nuget, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="candidate", value=str(artifacts))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.ElementTree(nuget).write(consumer / "NuGet.Config", encoding="unicode")
        for filename in ["Program.cs", "appsettings.json"]:
            shutil.copyfile(repo / "tests/config-package-consumer" / filename, consumer / filename)
        timings["scaffold"] = round(time.monotonic() - started, 3)
        run("restore", ["dotnet", "restore", "--configfile", "NuGet.Config"], cwd=consumer)
        assets = json.loads((consumer / "obj/project.assets.json").read_text())
        if any(library.get("type") == "project" for library in assets["libraries"].values()):
            raise RuntimeError("Package consumer unexpectedly has project dependencies.")
        run("build", ["dotnet", "build", "--no-restore", "--configuration", args.configuration], cwd=consumer)
        first = run("file-run", ["dotnet", "run", "--no-build", "--configuration", args.configuration], cwd=consumer)
        if "Payments:ApiKey = file demo (source: FileBasedConfigProvider)" not in first:
            raise RuntimeError("File quickstart output did not match its contract.")
        conformance = "Public conformance: provider counterexample and missing-to-lower fallback PASS 2/2"
        if conformance not in first:
            raise RuntimeError("Public conformance cases did not complete in the file run.")
        override_environment = environment.copy()
        override_environment["PAYMENTS__APIKEY"] = "environment override"
        second = run("override-run", ["dotnet", "run", "--no-build", "--configuration", args.configuration],
                     cwd=consumer, env=override_environment)
        expected = "Payments:ApiKey = environment override (source: EnvironmentConfigProvider)"
        if expected not in second or "IConfiguration coexistence: PASS" not in second or conformance not in second:
            raise RuntimeError("Override/public-provider coexistence output did not match its contract.")
        evidence["result"] = "passed"
        evidence["consumerSeconds"] = round(sum(value for name, value in timings.items() if not name.startswith("pack-")), 3)
        print(expected)
        print("Packed public provider and IConfiguration coexistence: PASS")
        print(f"Evidence: {work / 'evidence.json'}")
    finally:
        (work / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    main()
