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
from pathlib import Path
import shutil
import tempfile
import zipfile

from replay_environment import run_offline_build, verify_offline_restore
from worker_payload import PURE_LIBRARY, read_worker_payload, worker_payload_target


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
    for name in ("source", "extracted", "code", "fixture-publish"):
        (root / name).mkdir(mode=0o700)
    source = Path(__file__).with_name("memory-library-probe") / "Program.cs"
    shutil.copyfile(source, root / "source/Program.cs")
    with zipfile.ZipFile(args.package) as package:
        pure = package.read("lib/net10.0/" + PURE_LIBRARY)
    (root / "extracted" / PURE_LIBRARY).write_bytes(pure)
    closure = read_worker_payload(args.adapter_package)
    if closure.get(PURE_LIBRARY) != pure:
        raise ValueError("candidate pure and recursive worker closure differ")
    for name, data in closure.items():
        path = worker_payload_target(root / "code", name)
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
    dotnet = args.sdk.resolve() / "dotnet"
    steps = run_offline_build(dotnet, project, cache_root=root, publish_to=root / "fixture-publish",
                              logs=(args.evidence / "0.log", args.evidence / "1.log"))
    outcomes = [{"command": step.command, "exitCode": step.exit_code, "seconds": step.seconds} for step in steps]
    success = steps[-1].exit_code == 0
    if success:
        for path in (root / "fixture-publish").rglob("*"):
            if not path.is_file():
                continue
            destination = root / "code" / path.relative_to(root / "fixture-publish")
            if destination.exists() and destination.read_bytes() != path.read_bytes():
                raise ValueError("fixture output would replace candidate bytes")
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(path, destination)
        try:
            verify_offline_restore(root / "source/obj/project.assets.json", root / "nuget", exact_package_folder=True)
        except ValueError:
            success = False
        else:
            success = all((root / "code" / name).read_bytes() == data for name, data in closure.items())
    receipt = {"schema": 1, "kind": "extracted-worker-anchor-preparation", "passed": success,
               "preparedRoot": str(root), "sdk": str(args.sdk.resolve()), "commands": outcomes,
               "emptyPackageCache": not any((root / "nuget").iterdir()),
               "clearedPackageFeeds": project.with_name("NuGet.Config").read_text(), "probeSourceSha256": digest(source),
               "packageSha256": digest(args.package), "adapterPackageSha256": digest(args.adapter_package),
               "workerClosure": {name: hashlib.sha256(data).hexdigest() for name, data in sorted(closure.items())},
               "runtimePayload": {str(path.relative_to(root / "code")): digest(path)
                                  for path in sorted((root / "code").rglob("*")) if path.is_file()}}
    (args.evidence / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(json.dumps({"passed": success, "preparedRoot": str(root), "workerFiles": len(closure)}))
    return 0 if success else 1


if __name__ == "__main__":
    raise SystemExit(main())
