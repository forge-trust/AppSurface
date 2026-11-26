#!/usr/bin/env python3
"""Prepare a portable reader and complete worker closure from two local candidate packages.

Restore has cleared feeds and fresh private package, HTTP and scratch directories. The generated
project lives outside every checkout and has one assembly reference to the extracted pure DLL.
This prepares a private fixture; it neither publishes packages nor starts a runtime container.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tempfile
import time
import zipfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--adapter-package", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    parser.add_argument("--sdk", type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    args.evidence.mkdir(parents=True, exist_ok=False)
    root = Path(tempfile.mkdtemp(prefix="i888-memory-worker-", dir="/private/tmp")).resolve()
    for name in ("source", "extracted", "code", "nuget", "http-cache", "scratch", "fixture-publish"):
        (root / name).mkdir(mode=0o700)
    source = Path(__file__).with_name("memory-library-probe") / "Program.cs"
    shutil.copyfile(source, root / "source/Program.cs")
    worker_prefix = "tools/net10.0/any/realm-ready/"
    with zipfile.ZipFile(args.package) as package:
        pure = package.read("lib/net10.0/ForgeTrust.AppSurface.Auth.Keycloak.dll")
    (root / "extracted/ForgeTrust.AppSurface.Auth.Keycloak.dll").write_bytes(pure)
    closure = {}
    with zipfile.ZipFile(args.adapter_package) as package:
        for item in package.infolist():
            if not item.filename.startswith(worker_prefix) or item.is_dir():
                continue
            relative = PurePosixPath(item.filename.removeprefix(worker_prefix))
            if relative.is_absolute() or ".." in relative.parts or str(relative) in closure:
                raise ValueError("invalid or duplicate worker closure entry")
            closure[str(relative)] = package.read(item)
    if not closure or closure.get("ForgeTrust.AppSurface.Auth.Keycloak.dll") != pure:
        raise ValueError("candidate pure and recursive worker closure differ")
    for name, data in closure.items():
        path = root / "code" / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    project = root / "source/MemoryLibraryProbe.csproj"
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <AssemblyName>ForgeTrust.AppSurface.Auth.Keycloak.Tests</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
    <IsPackable>false</IsPackable><UseAppHost>false</UseAppHost>
  </PropertyGroup>
  <ItemGroup><Reference Include="ForgeTrust.AppSurface.Auth.Keycloak">
    <HintPath>../extracted/ForgeTrust.AppSurface.Auth.Keycloak.dll</HintPath>
  </Reference></ItemGroup>
</Project>
''')
    config = root / "NuGet.Config"
    config.write_text('<configuration><packageSources><clear /></packageSources></configuration>\n')
    environment = dict(os.environ, DOTNET_ROOT=str(args.sdk.resolve()),
                       NUGET_PACKAGES=str(root / "nuget"), NUGET_HTTP_CACHE_PATH=str(root / "http-cache"),
                       NUGET_SCRATCH=str(root / "scratch"), DOTNET_CLI_TELEMETRY_OPTOUT="1",
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", MSBUILDDISABLENODEREUSE="1")
    dotnet = str(args.sdk.resolve() / "dotnet")
    commands = [
        [dotnet, "restore", str(project), "--configfile", str(config), "--packages", str(root / "nuget")],
        [dotnet, "publish", str(project), "--configuration", "Release", "--no-restore",
         "--output", str(root / "fixture-publish"), "-m:1", "-nr:false"],
    ]
    outcomes = []
    for index, command in enumerate(commands):
        started = time.monotonic()
        with (args.evidence / (str(index) + ".log")).open("x") as log:
            result = subprocess.run(command, cwd=project.parent, env=environment, stdout=log,
                                    stderr=subprocess.STDOUT, timeout=120)
        outcomes.append({"command": command, "exitCode": result.returncode, "seconds": time.monotonic() - started})
        if result.returncode:
            break
    success = len(outcomes) == 2 and all(item["exitCode"] == 0 for item in outcomes)
    if success:
        for path in (root / "fixture-publish").rglob("*"):
            if not path.is_file():
                continue
            destination = root / "code" / path.relative_to(root / "fixture-publish")
            if destination.exists() and destination.read_bytes() != path.read_bytes():
                raise ValueError("fixture output would replace candidate bytes")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, destination)
        assets = json.loads((root / "source/obj/project.assets.json").read_text())
        success = (not assets["libraries"] and set(assets["packageFolders"]) == {str(root / "nuget")}
                   and not any((root / "nuget").iterdir())
                   and all((root / "code" / name).read_bytes() == data for name, data in closure.items()))
    receipt = {"schema": 1, "kind": "extracted-worker-anchor-preparation", "passed": success,
               "preparedRoot": str(root), "sdk": str(args.sdk.resolve()), "commands": outcomes,
               "emptyPackageCache": not any((root / "nuget").iterdir()),
               "clearedPackageFeeds": config.read_text(), "probeSourceSha256": digest(source),
               "packageSha256": digest(args.package), "adapterPackageSha256": digest(args.adapter_package),
               "workerClosure": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(closure.items())},
               "runtimePayload": {str(path.relative_to(root / "code")): digest(path)
                                  for path in sorted((root / "code").rglob("*")) if path.is_file()}}
    (args.evidence / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"passed": success, "preparedRoot": str(root), "workerFiles": len(closure)}))
    return 0 if success else 1


if __name__ == "__main__":
    raise SystemExit(main())
