#!/usr/bin/env python3
"""Pack and consume the coordinated Config public API in an isolated, project-reference-free application."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import tempfile
import time
import xml.etree.ElementTree as ET


def write_nuget_config(path, sources):
    config = ET.Element("configuration")
    package_sources = ET.SubElement(config, "packageSources")
    ET.SubElement(package_sources, "clear")
    for key, value in sources:
        ET.SubElement(package_sources, "add", key=key, value=str(value))
    ET.ElementTree(config).write(path, encoding="unicode")


def package_graph(assets_path):
    assets = json.loads(assets_path.read_text())
    if any(library.get("type") == "project" for library in assets["libraries"].values()):
        raise RuntimeError(f"Restore graph at {assets_path.parent} contains project assets.")
    return {
        package_id: version
        for identity, library in assets["libraries"].items()
        if library.get("type") == "package"
        for package_id, version in [identity.rsplit("/", 1)]
    }


def project_reference_count(project_path):
    project = ET.parse(project_path).getroot()
    return len(project.findall(".//ProjectReference"))


def proof_counters(output):
    pattern = re.compile(
        r"Package proof counters: registration constructors=(\d+) provider-reads=(\d+); "
        r"runtime constructors=(\d+) provider-reads=(\d+); "
        r"audit constructor-delta=(\d+) provider-read-delta=(\d+); "
        r"tripwire constructors=(\d+) provider-reads=(\d+)"
    )
    match = pattern.search(output)
    if not match:
        raise RuntimeError("Consumer output did not include safe phase counter evidence.")
    names = (
        "registrationConstructors", "registrationProviderReads", "runtimeConstructors",
        "runtimeProviderReads", "auditConstructorDelta", "auditProviderReadDelta",
        "tripwireConstructors", "tripwireProviderReads",
    )
    return dict(zip(names, map(int, match.groups())))


SELECTIONS = (
    ("ProductFeatureCatalogConfig", "Skoolit:Features", "packed-domain"),
    ("ProductFeatureWorkerParityReceiptConfig", "Skoolit:FeatureWorkerParity", "packed-domain"),
    ("DurableWorkerPublicOptionsConfig", "Skoolit:DurableWorkers", "worker-root"),
    ("AlphaEvidenceSourceOnlyPublicOptionsConfig", "Skoolit:AlphaEvidence:SourceOnly", "worker-root"),
    ("ForwardingExtractionPublicOptionsConfig", "Skoolit:Forwarding:Extraction", "worker-root"),
    ("SharedDataProtectionPublicOptionsConfig", "Skoolit:DataProtection", "worker-root"),
)


def json_evidence(output, prefix):
    matches = [line[len(prefix):] for line in output.splitlines() if line.startswith(prefix)]
    if len(matches) != 1:
        raise RuntimeError(f"Expected one {prefix.strip()} record.")
    return json.loads(matches[0])


def validate_phase(phase, indices, inspection=False):
    expected_types = {wrapper: int(index in indices) for index, (wrapper, _, _) in enumerate(SELECTIONS)}
    expected_reads = {key: int(index in indices) for index, (_, key, _) in enumerate(SELECTIONS)}
    expected_inits = {key: int(index in indices and not inspection) for index, (_, key, _) in enumerate(SELECTIONS)}
    if phase != {"ConstructorsByType": expected_types, "InitCallsByKey": expected_inits,
                 "ProviderReadsByKey": expected_reads}:
        raise RuntimeError("Worker per-wrapper/per-provider phase counters disagree with the pinned stages.")


def validate_worker_evidence(output):
    proof = json_evidence(output, "Worker stage evidence: ")
    expected_selections = [{"wrapper": wrapper, "key": key, "discovery": membership}
                           for wrapper, key, membership in SELECTIONS]
    if (proof["domainWrappersOutsideDiscovery"], proof["discoveredWorkerWrappers"],
            proof["repeatedWorkerHelperWrappers"]) != (2, 4, 4) or proof["selections"] != expected_selections:
        raise RuntimeError("Worker fixture discovery membership or repeated registration inventory is incorrect.")
    expected_scenarios = {
        "other-lane": ("Other", False, False, False),
        "evidence-disabled": ("EvidenceSupport", True, False, False),
        "admission-disabled": ("EvidenceSupport", True, False, False),
        "evidence-standby": ("EvidenceSupport", True, True, False),
        "evidence-active": ("EvidenceSupport", True, True, True),
    }
    scenarios = proof["scenarios"]
    if len(scenarios) != len(expected_scenarios) or {row["scenario"] for row in scenarios} != set(expected_scenarios):
        raise RuntimeError("Worker fixture did not execute every inactive/active EvidenceSupport branch.")
    for row in scenarios:
        actual = (row["lane"], row["sourceOnlyHelperSelected"], row["evidenceMayRun"], row["secondStageBuilt"])
        if actual != expected_scenarios[row["scenario"]]:
            raise RuntimeError("Worker stage selection conditions disagree with the pinned inventory.")
        if "ConfigPackageConsumer.DomainFixture" in row["discoveryInputs"] or "Consumer" not in row["discoveryInputs"]:
            raise RuntimeError("Packed Domain entered discovery, or worker entry assembly was not discovered.")
        if row["secretProviderReads"] != 0 or row["unselectedSecretWrapperConstructors"] != 0:
            raise RuntimeError("Public worker composition touched the unselected secret tripwire.")
        first = row["firstProvider"]
        if first["provider"] != "first":
            raise RuntimeError("First-stage provider identity missing.")
        validate_phase(first["registration"], [])
        validate_phase(first["startupBeforeResolution"], [])
        validate_phase(first["runtime"], [0, 1, 2, 3] if row["evidenceMayRun"] else [0, 1, 2])
        validate_phase(first["audit"], list(range(6)), inspection=True)
        second = row["secondProvider"]
        if row["secondStageBuilt"]:
            if second is None or second["provider"] != "second":
                raise RuntimeError("Active EvidenceSupport did not build a distinct second provider.")
            validate_phase(second["registration"], [])
            validate_phase(second["startupBeforeResolution"], [])
            validate_phase(second["runtime"], [4, 5])
            validate_phase(second["audit"], list(range(6)), inspection=True)
        elif second is not None:
            raise RuntimeError("Inactive worker branch built a second provider.")
    return proof


def validate_web_evidence(output, candidate):
    proof = json_evidence(output, "Web phase evidence: ")
    wrappers = ["ProductFeatureCatalogConfig", "ProductFeatureWorkerParityReceiptConfig", "PaymentsEndpointConfig"]
    keys = ["Skoolit:Features", "Skoolit:FeatureWorkerParity", "Payments:Endpoint"]
    if proof["mode"] != ("candidate" if candidate else "baseline"):
        raise RuntimeError("Web proof mode disagrees with the requested baseline/candidate.")
    if "ConfigPackageConsumer.DomainFixture" in proof["discoveryInputs"] or "Consumer" in proof["discoveryInputs"]:
        raise RuntimeError("Web proof broadened its discovery boundary.")
    for phase in ["registration", "startupBeforeResolution"]:
        if proof[phase] != {"constructors": 0, "providerReads": 0}:
            raise RuntimeError("Web registration/startup eagerly activated selected Domain wrappers.")
    for phase, count in [("runtime", 1), ("audit", int(candidate))]:
        if proof[phase] != {"constructorsByType": dict.fromkeys(wrappers, count),
                            "providerReadsByKey": dict.fromkeys(keys, count)}:
            raise RuntimeError("Web runtime/audit per-wrapper counters disagree with their contract.")
    if proof["tripwireConstructors"] != 0 or proof["tripwireProviderReads"] != 0:
        raise RuntimeError("Web proof touched the unselected secret-bearing wrapper.")
    return proof


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
    domain_project = work / "domain-project"
    domain_feed = work / "domain-feed"
    owned_names = ["consumer", "domain-project", "domain-feed", "nuget-packages", "http-cache", "dotnet-home"]
    if args.artifacts is None:
        owned_names.append("packages")
    if any((work / name).exists() or (work / name).is_symlink() for name in owned_names):
        raise SystemExit("Verifier-owned fixture or cache path already exists; select a fresh work directory.")
    consumer.mkdir()
    domain_project.mkdir()
    domain_feed.mkdir()
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
        if name.upper().startswith(("PAYMENTS", "PRODUCTION_PAYMENTS", "PRODUCTION__PAYMENTS", "EXTERNAL", "PRODUCTION__EXTERNAL", "SKOOLIT", "PRODUCTION_SKOOLIT", "PRODUCTION__SKOOLIT")) \
                or name == "CONFIG_PACKAGE_CONSUMER_MODE":
            del environment[name]

    def run(stage, command, cwd=repo, env=None):
        stage_state["current"] = stage
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
                "baselineSkoolitRevision": "a589f164ef3346d4ecf8bf39b8a73ec71ea1cb1e",
                "baselineAppSurfaceRevision": "c2f062f2a22afeff972c5c6512f1f34dd182db90",
                "cacheState": "isolated-cold", "stagesSeconds": timings, "result": "incomplete"}
    stage_state = {"current": "setup"}
    try:
        shutil.copyfile(repo / "tests/config-package-consumer/pinned-inventory.md", work / "pinned-inventory.md")
        inventory = (work / "pinned-inventory.md").read_text()
        if any(evidence[key] not in inventory for key in ["baselineSkoolitRevision", "baselineAppSurfaceRevision"]):
            raise RuntimeError("Copied selection inventory disagrees with its baseline revision references.")
        input_paths = [
            repo / "scripts/verify_config_package_consumer.py",
            *sorted((repo / "tests/config-package-consumer").rglob("*.cs")),
            repo / "tests/config-package-consumer/fixtures/domain/DomainFixture.csproj",
            repo / "tests/config-package-consumer/pinned-inventory.md",
            repo / "tests/config-package-consumer/appsettings.json",
        ]
        for package_id in package_ids:
            package_root = repo / ("" if package_id.endswith("Core") else "Config") / package_id
            input_paths.extend(path for path in sorted(package_root.rglob("*"))
                               if path.is_file() and path.suffix in {".cs", ".csproj", ".props", ".targets"}
                               and not {"bin", "obj"}.intersection(path.relative_to(package_root).parts))
        input_paths.extend(repo / name for name in [
            "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json"
        ] if (repo / name).is_file())
        evidence["candidateInputs"] = {
            "checkoutHead": run("checkout-head", ["git", "rev-parse", "HEAD"]).strip(),
            "packageOrigin": "supplied-artifacts" if args.artifacts else "packed-checkout",
            "filesSha256": {str(path.relative_to(repo)): hashlib.sha256(path.read_bytes()).hexdigest()
                            for path in sorted(set(input_paths))},
        }
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

        evidence["candidateInputs"]["packagesSha256"] = {
            package_id: hashlib.sha256((artifacts / f"{package_id}.{args.package_version}.nupkg").read_bytes()).hexdigest()
            for package_id in package_ids
        }
        fixture_source = repo / "tests/config-package-consumer/fixtures/domain"
        for filename in ["DomainFixture.csproj", "DomainConfigs.cs"]:
            shutil.copyfile(fixture_source / filename, domain_project / filename)
        write_nuget_config(domain_project / "NuGet.Config", [
            ("candidate", artifacts),
            ("nuget.org", "https://api.nuget.org/v3/index.json"),
        ])
        if project_reference_count(domain_project / "DomainFixture.csproj") != 0:
            raise RuntimeError("Packed Domain fixture must use package references only.")
        run("domain-restore", [
            "dotnet", "restore", "DomainFixture.csproj", "--configfile", "NuGet.Config",
            "-p:PackageVersion=" + args.package_version, "-p:Version=" + args.package_version,
        ], cwd=domain_project)
        domain_graph = package_graph(domain_project / "obj/project.assets.json")
        for package_id in ["ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Config"]:
            if domain_graph.get(package_id) != args.package_version:
                raise RuntimeError("Domain restore graph did not select the requested candidate package identity.")
        run("pack-domain-fixture", [
            "dotnet", "pack", "DomainFixture.csproj", "--no-restore", "--configuration", args.configuration,
            "--output", str(domain_feed), "-p:PackageVersion=" + args.package_version,
            "-p:Version=" + args.package_version,
        ], cwd=domain_project)
        domain_package_id = "ForgeTrust.AppSurface.Config.PackageConsumer.DomainFixture"
        if not (domain_feed / f"{domain_package_id}.{args.package_version}.nupkg").is_file():
            raise RuntimeError("Packed Domain fixture package was not produced.")

        started = time.monotonic()
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable",
                            "Nullable": "enable", "RestorePackagesWithLockFile": "true"}.items():
            ET.SubElement(properties, name).text = value
        references = ET.SubElement(project, "ItemGroup")
        for package_id in [
            "ForgeTrust.AppSurface.Config",
            "ForgeTrust.AppSurface.Config.Testing",
            domain_package_id,
        ]:
            ET.SubElement(references, "PackageReference", Include=package_id, Version=args.package_version)
        ET.indent(project)
        ET.ElementTree(project).write(consumer / "Consumer.csproj", encoding="unicode")
        write_nuget_config(consumer / "NuGet.Config", [
            ("candidate", artifacts),
            ("domain-fixture", domain_feed),
            ("nuget.org", "https://api.nuget.org/v3/index.json"),
        ])
        for filename in [
            "Program.cs", "ExplicitRegistrationExample.cs", "WorkerRootConfigs.cs", "WorkerStageProof.cs", "appsettings.json",
        ]:
            shutil.copyfile(repo / "tests/config-package-consumer" / filename, consumer / filename)
        timings["scaffold"] = round(time.monotonic() - started, 3)
        if project_reference_count(consumer / "Consumer.csproj") != 0:
            raise RuntimeError("Packed consumer must use package references only.")
        run("restore", ["dotnet", "restore", "--configfile", "NuGet.Config"], cwd=consumer)
        consumer_graph = package_graph(consumer / "obj/project.assets.json")
        for package_id in ["ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Config"]:
            if consumer_graph.get(package_id) != domain_graph.get(package_id):
                raise RuntimeError("Domain and consumer restore graphs disagree on candidate package identity.")
        expected_consumer_packages = [
            "ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Config",
            "ForgeTrust.AppSurface.Config.Testing", domain_package_id,
        ]
        if any(consumer_graph.get(package_id) != args.package_version for package_id in expected_consumer_packages):
            raise RuntimeError("Consumer restore graph did not select the complete candidate package set.")
        evidence["packageGraphs"] = {
            "domain": dict(sorted(domain_graph.items())),
            "consumer": dict(sorted(consumer_graph.items())),
            "sharedCandidateIdentities": {
                package_id: domain_graph[package_id]
                for package_id in ["ForgeTrust.AppSurface.Core", "ForgeTrust.AppSurface.Config"]
            },
            "projectReferenceCount": 0,
        }
        run("build", ["dotnet", "build", "--no-restore", "--configuration", args.configuration], cwd=consumer)
        baseline_environment = environment.copy()
        baseline_environment["CONFIG_PACKAGE_CONSUMER_MODE"] = "baseline"
        baseline = run("baseline-run", [
            "dotnet", "run", "--no-build", "--no-restore", "--configuration", args.configuration,
        ], cwd=consumer, env=baseline_environment)
        if "Baseline manual Domain selection: Missing declaration" not in baseline:
            raise RuntimeError("Baseline did not prove manual Domain initialization lacks its attributed declaration.")
        baseline_counts = proof_counters(baseline)
        baseline_web = validate_web_evidence(baseline, candidate=False)
        if baseline_counts["registrationConstructors"] != 0 or baseline_counts["registrationProviderReads"] != 0:
            raise RuntimeError("Baseline registration phase activated selected wrappers or read the proof provider.")
        if baseline_counts["tripwireConstructors"] != 0 or baseline_counts["tripwireProviderReads"] != 0:
            raise RuntimeError("Baseline activated the unselected wrapper tripwire.")

        candidate_environment = environment.copy()
        candidate_environment["CONFIG_PACKAGE_CONSUMER_MODE"] = "candidate"
        first = run("file-run", [
            "dotnet", "run", "--no-build", "--no-restore", "--configuration", args.configuration,
        ], cwd=consumer, env=candidate_environment)
        if "Payments:ApiKey = file demo (source: FileBasedConfigProvider)" not in first:
            raise RuntimeError("File quickstart output did not match its contract.")
        if "Explicit Domain registration: PASS" not in first:
            raise RuntimeError("Canonical packed Domain registration example did not complete.")
        conformance = "Public conformance: provider counterexample and missing-to-lower fallback PASS 2/2"
        if conformance not in first:
            raise RuntimeError("Public conformance cases did not complete in the file run.")
        candidate_counts = proof_counters(first)
        if candidate_counts["registrationConstructors"] != 0 or candidate_counts["registrationProviderReads"] != 0:
            raise RuntimeError("Candidate registration phase activated selected wrappers or read the proof provider.")
        if candidate_counts["runtimeConstructors"] != 3 or candidate_counts["auditConstructorDelta"] != 3:
            raise RuntimeError("Candidate did not exercise all selected wrapper runtime paths.")
        if candidate_counts["tripwireConstructors"] != 0 or candidate_counts["tripwireProviderReads"] != 0:
            raise RuntimeError("Candidate activated the unselected wrapper tripwire.")
        evidence["proofPhases"] = {"baseline": baseline_counts, "candidate": candidate_counts}
        evidence["webPhases"] = {"baseline": baseline_web, "candidate": validate_web_evidence(first, candidate=True)}
        evidence["workerStages"] = validate_worker_evidence(first)

        override_environment = candidate_environment.copy()
        override_environment["PAYMENTS__APIKEY"] = "environment override"
        second = run("override-run", [
            "dotnet", "run", "--no-build", "--no-restore", "--configuration", args.configuration,
        ],
                     cwd=consumer, env=override_environment)
        expected = "Payments:ApiKey = environment override (source: EnvironmentConfigProvider)"
        if expected not in second or "IConfiguration coexistence: PASS" not in second or conformance not in second:
            raise RuntimeError("Override/public-provider coexistence output did not match its contract.")
        evidence["webPhases"]["environmentOverride"] = validate_web_evidence(second, candidate=True)
        evidence["workerStagesEnvironmentOverride"] = validate_worker_evidence(second)
        evidence["result"] = "passed"
        evidence["consumerSeconds"] = round(sum(value for name, value in timings.items() if not name.startswith("pack-")), 3)
        evidence["selectionProof"] = {
            "baselineSkoolitRevision": evidence["baselineSkoolitRevision"],
            "domainWorkerWrappersOutsideDiscovery": 2,
            "workerRootWrappersDiscoveredAndRepeated": 4,
            "canonicalInjectedDomainExample": "PaymentsEndpointConfig",
            "baselineAudit": "Missing declaration",
            "candidateAudit": "Present, resolved, one attributed entry",
            "unselectedTripwire": "not activated or read",
        }
        print(expected)
        print("Packed public provider and IConfiguration coexistence: PASS")
        print(f"Evidence: {work / 'evidence.json'}")
    except Exception:
        evidence["result"] = "failed"
        evidence["failedStage"] = stage_state["current"]
        raise
    finally:
        (work / "evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")


if __name__ == "__main__":
    main()
