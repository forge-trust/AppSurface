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
        omitted_mode = run("omitted-mode", [*entry, "evidence", "run"], cwd=work, expected=1)
        if b"ASEVD401: Select --mode trusted or --mode observation explicitly." not in omitted_mode:
            raise ValueError("The installed tool did not enforce explicit mode selection.")
        # Exercise the published planning journey against the installed image. These commands
        # create plans only; protected execution still belongs to the genuine Linux controls.
        journey = work / "planning-consumer"
        journey.mkdir(mode=0o700)
        run("evidence-starter", [*entry, "evidence", "init", "--sample"], cwd=journey)
        starter = journey / ".appsurface/evidence"
        marked = {name: (starter / name).read_bytes()
                  for name in ("evidence.policy.json", "EvidenceHost.cs", "README.md")}
        if {path.name for path in starter.iterdir()} != set(marked):
            raise ValueError("The installed starter wrote unexpected files.")
        policy = json.loads(marked["evidence.policy.json"])
        profiles = {profile["id"]: profile for profile in policy["profiles"]}
        if (policy["generatedBy"] != "appsurface-evidence-starter-v1"
                or policy["conservativeProfileId"] != "targeted-coverage"
                or profiles["no-evidence"]["producers"] != []
                or [producer["id"] for producer in profiles["targeted-coverage"]["producers"]] != ["coverage"]):
            raise ValueError("The installed starter does not retain its documented policy.")
        policy_arg = ".appsurface/evidence/evidence.policy.json"
        doctor = run("evidence-doctor", [*entry, "evidence", "doctor", "--policy", policy_arg,
                                       "--path", "docs/README.md"], cwd=journey)
        if (b"unverified trusted-envelope:" not in doctor
                or b"do not grant admission" not in doctor
                or b"issue779-consumer-acceptance.md" not in doctor):
            raise ValueError("The installed doctor did not preserve the planning-only boundary.")
        if (journey / "TestResults").exists():
            raise ValueError("Doctor unexpectedly wrote execution artifacts.")
        explain = run("evidence-explain", [*entry, "evidence", "explain", "--policy", policy_arg,
                                         "--path", "src/Orders/SubmitOrder.cs"], cwd=journey)
        for expected in (b"Evidence plan: targeted-coverage (targeted)", b"Required producers: coverage",
                         b"Required resources: none", b"changed-behavior-covered"):
            if expected not in explain:
                raise ValueError("The installed explain output diverged from its documented policy.")
        plan_root = journey / "TestResults/evidence"
        if {path.name for path in plan_root.iterdir()} != {"evidence-plan.json", "evidence-summary.json"}:
            raise ValueError("Planning produced an execution artifact.")
        repeat = run("evidence-starter-repeat", [*entry, "evidence", "init", "--sample"],
                     cwd=journey, expected=1)
        if b"ASEVD202" not in repeat or any((starter / name).read_bytes() != body for name, body in marked.items()):
            raise ValueError("The installed starter overwrote its original marked files.")
        rejected = run("evidence-observation-without-control", [*entry, "evidence", "run",
                       "--mode", "observation"], cwd=journey, expected=1)
        if b"ASEVD402" not in rejected or (plan_root / "evidence-manifest.json").exists():
            raise ValueError("A local Observation request bypassed authenticated execution.")
        legacy = run("evidence-legacy-observation-without-control", [*entry, "evidence", "run",
                     "--observation-only"], cwd=journey, expected=1)
        if legacy != rejected:
            raise ValueError("The legacy Observation alias changed admission behavior.")
        conflict = run("evidence-conflicting-mode", [*entry, "evidence", "run", "--mode", "trusted",
                       "--observation-only"], cwd=journey, expected=1)
        if b"ASEVD401" not in conflict or {path.name for path in plan_root.iterdir()} != {
                "evidence-plan.json", "evidence-summary.json"}:
            raise ValueError("Conflicting mode selection did not reject before execution.")
        summary = json.loads((plan_root / "evidence-summary.json").read_bytes())
        if (summary["kind"] != "plan" or summary["profile"] != "targeted-coverage"
                or summary["obligations"] != ["changed-behavior-covered"]
                or "not use this plan as gate evidence" not in summary["next"]):
            raise ValueError("The installed planning summary overclaimed execution.")
        receipt["planning_journey"] = {
            "starter_sha256": {name: hashlib.sha256(body).hexdigest() for name, body in marked.items()},
            "plan_sha256": hashlib.sha256((plan_root / "evidence-plan.json").read_bytes()).hexdigest(),
            "summary_sha256": hashlib.sha256((plan_root / "evidence-summary.json").read_bytes()).hexdigest(),
            "entry": "installed-nuget-tool", "producer_execution": False,
            "trusted_acceptance": False, "independent_human_usability": False,
        }
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
using ForgeTrust.AppSurface.Evidence.Planner;
using System.Text.Json;
var policy = EvidenceCanonicalJson.Deserialize<EvidencePolicy>(File.ReadAllBytes(args[0]));
var plan = new EvidencePlanner().Resolve(policy, [new NormalizedDiffPath("src/Orders/SubmitOrder.cs")]);
if (plan.Profile.Id != "targeted-coverage") throw new InvalidOperationException("SDK plan mismatch.");
var canonical = EvidenceCanonicalJson.Serialize(plan);
if (!canonical.AsSpan().SequenceEqual(EvidenceCanonicalJson.Serialize(
    EvidenceCanonicalJson.Deserialize<EvidencePlan>(canonical))))
    throw new InvalidOperationException("SDK canonical roundtrip mismatch.");
foreach (var unsupportedSchema in new[] { "0.9", "2.0", "9999" })
{
    try
    {
        _ = EvidenceCanonicalJson.Deserialize<EvidencePlan>(EvidenceCanonicalJson.Serialize(plan with { ContractVersion = unsupportedSchema }));
        throw new InvalidOperationException("Unsupported schema was accepted.");
    }
    catch (JsonException error) when (error.InnerException is null) { }
}
foreach (var observation in new[] { false, true })
{
    try
    {
        _ = EvidenceManifestBuilder.Build(plan, [], observationOnly: observation,
            envelopeStatus: EvidenceEnvelopeStatus.ValidatedNotAttested);
        throw new InvalidOperationException("A Boolean/status-enum builder granted admission.");
    }
    catch (EvidenceAdmissionException error) when (error.Code == "ASEVD400" && error.InnerException is null) { }
}
var configured = false;
await using var host = EvidenceHostBootstrap.Create(plan, _ => configured = true);
try
{
    await host.RunAsync(new EvidenceExecutionRequest(EvidenceExecutionMode.Observation, args[1]));
    throw new InvalidOperationException("Unsupervised SDK execution was accepted.");
}
catch (EvidenceAdmissionException error) when (error.Code == "ASEVD402" && error.InnerException is null) { }
if (configured) throw new InvalidOperationException("Configuration ran before authentication.");
#pragma warning disable CS0618
var omittedConfigured = false;
await using (var omittedHost = EvidenceHostBootstrap.Create(plan, _ => omittedConfigured = true))
{
    var omittedRun = omittedHost.RunAsync();
    try
    {
        await omittedRun;
        throw new InvalidOperationException("The omitted legacy Boolean overload was accepted.");
    }
    catch (EvidenceAdmissionException error) when (error.Code == "ASEVD401" && error.InnerException is null) { }
}
#pragma warning restore CS0618
if (omittedConfigured) throw new InvalidOperationException("Omitted legacy Boolean configured the host.");
Console.WriteLine("PACKAGED_EVIDENCE_MIGRATION legacy-omitted ASEVD401 callbacks=0");
#pragma warning disable CS0618
var falseConfigured = false;
await using (var falseHost = EvidenceHostBootstrap.Create(plan, _ => falseConfigured = true))
{
    var falseRun = falseHost.RunAsync(false);
    try
    {
        await falseRun;
        throw new InvalidOperationException("The false legacy Boolean overload was accepted.");
    }
    catch (EvidenceAdmissionException error) when (error.Code == "ASEVD401" && error.InnerException is null) { }
}
#pragma warning restore CS0618
if (falseConfigured) throw new InvalidOperationException("False legacy Boolean configured the host.");
Console.WriteLine("PACKAGED_EVIDENCE_MIGRATION legacy-false ASEVD401 callbacks=0");
#pragma warning disable CS0618
var trueConfigured = false;
await using (var trueHost = EvidenceHostBootstrap.Create(plan, _ => trueConfigured = true))
{
    var trueRun = trueHost.RunAsync(true);
    try
    {
        await trueRun;
        throw new InvalidOperationException("The true legacy Boolean overload was accepted.");
    }
    catch (EvidenceAdmissionException error) when (error.Code == "ASEVD402" && error.InnerException is null) { }
}
#pragma warning restore CS0618
if (trueConfigured) throw new InvalidOperationException("True legacy Boolean configured the host.");
Console.WriteLine("PACKAGED_EVIDENCE_MIGRATION legacy-true ASEVD402 callbacks=0");
Console.WriteLine("Exact packaged SDK planning, schema and closed-entry controls passed.");
''')
        # A fresh package cache forces actual restore of the local SDK graph and its declared external dependencies.
        run("sdk-restore", ["dotnet", "restore", "Consumer.csproj", "--packages", str(work / "sdk-packages"),
                            "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json", "--use-lock-file", "-warnaserror"], cwd=consumer)
        run("sdk-locked-restore", ["dotnet", "restore", "Consumer.csproj", "--packages", str(work / "sdk-packages"),
                                   "--source", str(feed), "--source", "https://api.nuget.org/v3/index.json", "--locked-mode", "-warnaserror"], cwd=consumer)
        run("sdk-build", ["dotnet", "build", "Consumer.csproj", "--no-restore", "-warnaserror", "-p:UseSharedCompilation=false"], cwd=consumer)
        run("sdk-load", ["dotnet", str(consumer / "bin/Debug/net10.0/Consumer.dll"),
                         str(starter / "evidence.policy.json"), str(journey / "missing-control.sock")], cwd=consumer)
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
