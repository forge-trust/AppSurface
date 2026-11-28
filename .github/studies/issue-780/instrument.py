"""Apply exact timing-only edits to the pinned checkout used by both study arms."""

from pathlib import Path


def insert(path, edits):
    source = path.read_text()
    for old, new in edits:
        if source.count(old) != 1:
            raise ValueError(f"timing boundary is absent or ambiguous in {path}: {old}")
        source = source.replace(old, new)
    path.write_text(source)


def start(name, indentation=8):
    return " " * indentation + f"var {name} = System.Diagnostics.Stopwatch.GetTimestamp();\n"


def stop(name, phase, indentation=8):
    prefix = " " * indentation
    return (
        f'{prefix}Console.WriteLine("ASEVD_STUDY {phase} " + System.Diagnostics.Stopwatch.GetElapsedTime({name})'
        '\n'
        f'{prefix}    .TotalMilliseconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));\n'
    )


workflow = Path("tools/ForgeTrust.AppSurface.Evidence.Validation/RepositoryEvidenceWorkflow.cs")
policy = '        var policy = Read<EvidencePolicy>(Required(options, "--policy"), cancellationToken);'
checks = '        if (operation == "verify")'
output = '        await File.WriteAllBytesAsync(PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.PlanFileName),'
finish = '        if (operation == "plan") return 0;'
manifest = '        await File.WriteAllBytesAsync(PathUtils.PathUnder(output, RepositoryEvidenceOutputLayout.ManifestFileName),'
summary = '        await File.AppendAllTextAsync(summaryPath,'
verify = '        ValidationProof.Require(plan.PolicySnapshot is not null'
verify_manifest = '        ValidationProof.Require(EvidenceManifestBuilder.Verify(plan, manifest),'
insert(workflow, [
    (policy, start("studyPlan") + policy),
    (checks, stop("studyPlan", "planner") + checks),
    (output, start("studyPlanWrite") + output),
    (finish, stop("studyPlanWrite", "planner") + finish),
    (manifest, start("studyManifestWrite") + manifest),
    (summary, stop("studyManifestWrite", "manifest") + summary),
    (verify, start("studyReplan") + verify),
    (verify_manifest, stop("studyReplan", "planner") + verify_manifest),
])
insert(Path("Evidence/ForgeTrust.AppSurface.Evidence.Aspire/EvidenceHostBootstrap.cs"), [
    (
        "        var manifest = EvidenceManifestBuilder.Build(_plan, results, observationOnly, envelopeStatus, resourceResults, metrics);",
        start("studyManifest")
        + "        var manifest = EvidenceManifestBuilder.Build(_plan, results, observationOnly, envelopeStatus, resourceResults, metrics);\n"
        + stop("studyManifest", "manifest").rstrip("\n")
    ),
])
