"""Pack/install the ordinary tool and compile a fresh SDK consumer. No protected execution."""
from pathlib import Path
import hashlib
import json
import os
import signal
import subprocess
import sys
import time
import zipfile

ROOT = Path(__file__).resolve().parents[2]


def main():
    if len(sys.argv) != 2:
        raise ValueError("Use one fresh absolute package smoke directory.")
    work = Path(sys.argv[1])
    if not work.is_absolute() or work.exists() or work.is_symlink():
        raise ValueError("Use one fresh absolute package smoke directory.")
    os.umask(0o077)
    work.mkdir(mode=0o700)
    end = time.monotonic() + 540
    records = []
    receipt = {"schema": "issue779-packaged-consumer-v1", "exit": 1, "commands": records,
               "protected_execution": False, "trusted_acceptance": False}
    version = "0.1.0-issue779-c4." + subprocess.check_output(
        ["git", "rev-parse", "--short=12", "HEAD"], cwd=ROOT, timeout=10, text=True).strip()
    receipt["version"] = version
    receipt["source"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, timeout=10, text=True).strip()
    feed = work / "feed"
    feed.mkdir(mode=0o700)

    def run(name, args, cwd=ROOT, expected=0):
        log = work / (name + ".log")
        with log.open("xb") as output:
            process = subprocess.Popen(args, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            try:
                code = process.wait(timeout=max(.001, end - time.monotonic()))
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait(timeout=10)
        body = log.read_bytes()
        records.append({"name": name, "argv": [str(a) for a in args], "exit": code,
                        "log_sha256": hashlib.sha256(body).hexdigest()})
        if code != expected:
            raise ValueError("Package consumer command failed: " + name)
        return body

    try:
        common = ["-c", "Release", "--no-restore", "-warnaserror", "-p:UseSharedCompilation=false",
                  "-p:EvidencePrivateQualification=None", "-p:Version=" + version, "-o", str(feed)]
        cli = ROOT / "Cli/ForgeTrust.AppSurface.Cli/ForgeTrust.AppSurface.Cli.csproj"
        run("tool-pack", ["dotnet", "pack", str(cli), *common])
        package = feed / ("ForgeTrust.AppSurface.Cli." + version + ".nupkg")
        required = ["ForgeTrust.AppSurface.Evidence.Supervision.dll", "ForgeTrust.AppSurface.Evidence.Contracts.dll",
                    "ForgeTrust.AppSurface.Evidence.Planner.dll", "ForgeTrust.AppSurface.Evidence.Coverage.dll",
                    "Tmds.DBus.Protocol.dll", "reportgenerator/net10.0/ReportGenerator.dll"]
        prefix = "tools/net10.0/any/"
        with zipfile.ZipFile(package) as archive:
            names = archive.namelist()
            if len(names) != len(set(names)) or not all(prefix + n in names for n in required):
                raise ValueError("The tool package is missing runtime dependencies.")
            if any(n.endswith(".py") for n in names):
                raise ValueError("The tool package contains a Python runtime.")
        receipt["tool_package_sha256"] = hashlib.sha256(package.read_bytes()).hexdigest()
        config = work / "tool-nuget.config"
        config.write_text('<configuration><packageSources><clear/><add key="local" value="' +
                          str(feed).replace('&', '&amp;').replace('"', '&quot;') + '"/></packageSources></configuration>')
        tool = work / "tool"
        run("tool-install", ["dotnet", "tool", "install", "ForgeTrust.AppSurface.Cli", "--version", version,
                             "--tool-path", str(tool), "--configfile", str(config), "--no-cache"])
        image = tool / ".store/forgetrust.appsurface.cli" / version / "forgetrust.appsurface.cli" / version / "tools/net10.0/any"
        if not all((image / n).is_file() for n in required):
            raise ValueError("The installed tool is missing runtime dependencies.")
        receipt["tool_image"] = str(image)
        entry = [str(tool / "appsurface")]
        for name, args in (("ordinary-help", ["--help"]), ("worker-help", ["evidence", "worker", "--help"]),
                           ("supervisor-help", ["evidence", "supervise", "--help"])):
            run(name, [*entry, *args], cwd=work)
        error = run("unsupported-owner", [*entry, "evidence", "supervise", "--request", str(work / "missing.json")], cwd=work, expected=1)
        if b"ASEVD402" not in error:
            raise ValueError("Unprivileged or unsupported supervisor did not reject.")
        run("omitted-mode", [*entry, "evidence", "run"], cwd=work, expected=1)
        for name in ("Contracts", "Planner", "Coverage", "Aspire"):
            project = ROOT / ("Evidence/ForgeTrust.AppSurface.Evidence." + name) / ("ForgeTrust.AppSurface.Evidence." + name + ".csproj")
            run("sdk-pack-" + name.lower(), ["dotnet", "pack", str(project), *common])
        consumer = work / "sdk-consumer"
        consumer.mkdir(mode=0o700)
        # The caller may retain evidence under another checkout: never inherit its MSBuild/CPM policy.
        (consumer / "Directory.Build.props").write_text("<Project />")
        (consumer / "Directory.Build.targets").write_text("<Project />")
        (consumer / "Directory.Packages.props").write_text("<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>")
        (consumer / "Consumer.csproj").write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
<ItemGroup><PackageReference Include="ForgeTrust.AppSurface.Evidence.Aspire" Version="''' + version + '''"/>
</ItemGroup></Project>''')
        (consumer / "Program.cs").write_text('''using ForgeTrust.AppSurface.Evidence.Aspire;
using ForgeTrust.AppSurface.Evidence.Contracts;
Console.WriteLine(typeof(EvidenceHostBootstrap).FullName);
Console.WriteLine(typeof(IEvidenceProducer).FullName);
''')
        # A fresh package cache forces actual restore of the local SDK graph and its declared external dependencies.
        run("sdk-restore", ["dotnet", "restore", "Consumer.csproj", "--packages", str(work / "sdk-packages"),
                            "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json", "--use-lock-file", "-warnaserror"], cwd=consumer)
        run("sdk-locked-restore", ["dotnet", "restore", "Consumer.csproj", "--packages", str(work / "sdk-packages"),
                                   "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json", "--locked-mode", "-warnaserror"], cwd=consumer)
        run("sdk-build", ["dotnet", "build", "Consumer.csproj", "--no-restore", "-warnaserror", "-p:UseSharedCompilation=false"], cwd=consumer)
        run("sdk-load", ["dotnet", str(consumer / "bin/Debug/net10.0/Consumer.dll")], cwd=consumer)
        dependencies = json.loads((consumer / "bin/Debug/net10.0/Consumer.deps.json").read_bytes())["libraries"]
        for name in ("Contracts", "Planner", "Coverage", "Aspire"):
            if "ForgeTrust.AppSurface.Evidence." + name + "/" + version not in dependencies:
                raise ValueError("The fresh SDK consumer used a different package graph.")
        receipt["sdk_dependencies"] = sorted(dependencies)
        receipt["exit"] = 0
    finally:
        (work / "receipt.json").write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n")
    print("Ordinary installed CLI and fresh SDK package consumer passed; no protected acceptance claim.")


if __name__ == "__main__":
    main()
